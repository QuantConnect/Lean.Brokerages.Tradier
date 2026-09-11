/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.Tradier;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Util;
using RestSharp;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace QuantConnect.Tests.Brokerages.Tradier
{
    [TestFixture]
    public class TradierBrokerageAditionalTests
    {
        private const long BrokerageSideOrderId = 1234;

        [Test]
        public void InitializesFactoryFromComposer()
        {
            using var factory = Composer.Instance.Single<IBrokerageFactory>(instance => instance.BrokerageType == typeof(TradierBrokerage));
            Assert.IsNotNull(factory);
        }

        [TestCase("2022-04-01T15:00:00", "17:00:00")]
        [TestCase("2022-04-01T20:00:00", "12:00:00")]
        [TestCase("2022-04-01T02:00:00", "6:00:00")]
        [TestCase("2022-04-01T05:00:00", "3:00:00")]
        [TestCase("2022-04-01T08:00:00", "1.00:00:00")]
        public void SubscriptionRefreshTimeout(DateTime utctime, TimeSpan expected)
        {
            var result = TradierBrokerage.GetSubscriptionRefreshTimeout(utctime);

            Assert.AreEqual(expected, result);
        }

        // Options
        [TestCase(OrderDirection.Buy, 0, SecurityType.Option, ExpectedResult = TradierOrderDirection.BuyToOpen)]
        [TestCase(OrderDirection.Buy, 100, SecurityType.Option, ExpectedResult = TradierOrderDirection.BuyToOpen)]
        [TestCase(OrderDirection.Buy, -100, SecurityType.Option, ExpectedResult = TradierOrderDirection.BuyToClose)]
        [TestCase(OrderDirection.Sell, 0, SecurityType.Option, ExpectedResult = TradierOrderDirection.SellToOpen)]
        [TestCase(OrderDirection.Sell, 100, SecurityType.Option, ExpectedResult = TradierOrderDirection.SellToClose)]
        [TestCase(OrderDirection.Sell, -100, SecurityType.Option, ExpectedResult = TradierOrderDirection.SellToOpen)]
        // IndexOptions
        [TestCase(OrderDirection.Buy, 0, SecurityType.IndexOption, ExpectedResult = TradierOrderDirection.BuyToOpen)]
        [TestCase(OrderDirection.Buy, 100, SecurityType.IndexOption, ExpectedResult = TradierOrderDirection.BuyToOpen)]
        [TestCase(OrderDirection.Buy, -100, SecurityType.IndexOption, ExpectedResult = TradierOrderDirection.BuyToClose)]
        [TestCase(OrderDirection.Sell, 0, SecurityType.IndexOption, ExpectedResult = TradierOrderDirection.SellToOpen)]
        [TestCase(OrderDirection.Sell, 100, SecurityType.IndexOption, ExpectedResult = TradierOrderDirection.SellToClose)]
        [TestCase(OrderDirection.Sell, -100, SecurityType.IndexOption, ExpectedResult = TradierOrderDirection.SellToOpen)]
        // Equities
        [TestCase(OrderDirection.Buy, 0, SecurityType.Equity, ExpectedResult = TradierOrderDirection.Buy)]
        [TestCase(OrderDirection.Buy, 100, SecurityType.Equity, ExpectedResult = TradierOrderDirection.Buy)]
        [TestCase(OrderDirection.Buy, -100, SecurityType.Equity, ExpectedResult = TradierOrderDirection.BuyToCover)]
        [TestCase(OrderDirection.Sell, 0, SecurityType.Equity, ExpectedResult = TradierOrderDirection.SellShort)]
        [TestCase(OrderDirection.Sell, 100, SecurityType.Equity, ExpectedResult = TradierOrderDirection.Sell)]
        [TestCase(OrderDirection.Sell, -100, SecurityType.Equity, ExpectedResult = TradierOrderDirection.SellShort)]
        public TradierOrderDirection ConvertsOrderDirection(OrderDirection direction, decimal holdingsQuantity, SecurityType securityType)
        {
            return TestableTradierBrokerage.ConvertDirectionPublic(direction, securityType, holdingsQuantity);
        }

        // Tradier's API can transiently serve a non-JSON body (e.g. its docs/maintenance HTML page) with a 200 status.
        // Deserialization then throws; this must be treated as a transient failure and retried, not raised as a fatal
        // error before the retry runs (see https://github.com/QuantConnect/Lean.Brokerages.Tradier/issues/45).
        [Test]
        public void RetriesTransientlyMalformedResponseInsteadOfFailing()
        {
            var htmlPage = "<!DOCTYPE html><html lang=\"en\"><head><title>Tradier API</title></head><body></body></html>";
            var errors = new List<BrokerageMessageEvent>();
            var restClient = new Mock<IRestClient>();
            restClient.SetupSequence(x => x.Execute(It.IsAny<IRestRequest>()))
                .Returns(CreateResponse(htmlPage))
                .Returns(CreateResponse("{\"ok\":true}"));

            var brokerage = CreateBrokerageWithRestClient(restClient.Object, errors);
            var result = InvokeExecute<JObject>(brokerage, TradierApiRequestType.Standard, max: 3);

            // the malformed body must not raise a fatal error before the retry, which succeeds and returns the payload
            Assert.IsEmpty(errors);
            Assert.IsNotNull(result);
            Assert.AreEqual(true, result["ok"].Value<bool>());
            restClient.Verify(x => x.Execute(It.IsAny<IRestRequest>()), Times.Exactly(2));
        }

        [Test]
        public void RaisesErrorOnlyAfterRetriesAreExhaustedOnMalformedResponse()
        {
            var htmlPage = "<!DOCTYPE html><html><head><title>Tradier API</title></head></html>";
            var errors = new List<BrokerageMessageEvent>();
            var restClient = new Mock<IRestClient>();
            restClient.Setup(x => x.Execute(It.IsAny<IRestRequest>()))
                .Returns(() => CreateResponse(htmlPage));

            var brokerage = CreateBrokerageWithRestClient(restClient.Object, errors);
            var result = InvokeExecute<JObject>(brokerage, TradierApiRequestType.Standard, max: 1);

            // with max == 1: attempt 0 retries, attempt 1 exhausts retries and raises the fatal JsonError
            Assert.IsNull(result);
            Assert.AreEqual(1, errors.Count);
            Assert.AreEqual("JsonError", errors[0].Code);
            restClient.Verify(x => x.Execute(It.IsAny<IRestRequest>()), Times.Exactly(2));
        }

        // Tradier's gateway can transiently serve a JSON fault body (e.g. {"fault":{"faultstring":"Datastore Error"}})
        // for backend problems. Only non-retryable authentication faults may fail fast; everything else must take the
        // retry path (see https://github.com/QuantConnect/Lean.Brokerages.Tradier/issues/51).
        [Test]
        public void RetriesTransientFaultResponseInsteadOfFailing()
        {
            var faultBody = "{\"fault\":{\"faultstring\":\"Datastore Error\",\"detail\":{\"errorcode\":\"steps.servicecallout.ExecutionFailed\"}}}";
            var errors = new List<BrokerageMessageEvent>();
            var restClient = new Mock<IRestClient>();
            restClient.SetupSequence(x => x.Execute(It.IsAny<IRestRequest>()))
                .Returns(CreateResponse(faultBody, HttpStatusCode.InternalServerError))
                .Returns(CreateResponse("{\"ok\":true}"));

            var brokerage = CreateBrokerageWithRestClient(restClient.Object, errors);
            var result = InvokeExecute<JObject>(brokerage, TradierApiRequestType.Standard, max: 3);

            // the transient fault must not raise a fatal error before the retry, which succeeds and returns the payload
            Assert.IsEmpty(errors);
            Assert.IsNotNull(result);
            Assert.AreEqual(true, result["ok"].Value<bool>());
            restClient.Verify(x => x.Execute(It.IsAny<IRestRequest>()), Times.Exactly(2));
        }

        [Test]
        public void RaisesErrorOnlyAfterRetriesAreExhaustedOnTransientFault()
        {
            var faultBody = "{\"fault\":{\"faultstring\":\"Datastore Error\",\"detail\":{\"errorcode\":\"steps.servicecallout.ExecutionFailed\"}}}";
            var errors = new List<BrokerageMessageEvent>();
            var restClient = new Mock<IRestClient>();
            restClient.Setup(x => x.Execute(It.IsAny<IRestRequest>()))
                .Returns(() => CreateResponse(faultBody, HttpStatusCode.InternalServerError));

            var brokerage = CreateBrokerageWithRestClient(restClient.Object, errors);
            var result = InvokeExecute<JObject>(brokerage, TradierApiRequestType.Standard, max: 1);

            // with max == 1: attempt 0 retries, attempt 1 exhausts retries and raises the fatal error
            Assert.IsNull(result);
            Assert.AreEqual(1, errors.Count);
            Assert.IsTrue(errors[0].Message.Contains("Datastore Error"));
            restClient.Verify(x => x.Execute(It.IsAny<IRestRequest>()), Times.Exactly(2));
        }

        [TestCase("{\"fault\":{\"faultstring\":\"Invalid Access Token\",\"detail\":{\"errorcode\":\"keymanagement.service.invalid_access_token\"}}}", HttpStatusCode.Unauthorized)]
        [TestCase("{\"fault\":{\"faultstring\":\"Access Token expired\",\"detail\":{\"errorcode\":\"keymanagement.service.access_token_expired\"}}}", HttpStatusCode.InternalServerError)]
        public void FailsFastOnAuthenticationFault(string faultBody, HttpStatusCode statusCode)
        {
            var errors = new List<BrokerageMessageEvent>();
            var restClient = new Mock<IRestClient>();
            restClient.Setup(x => x.Execute(It.IsAny<IRestRequest>()))
                .Returns(() => CreateResponse(faultBody, statusCode));

            var brokerage = CreateBrokerageWithRestClient(restClient.Object, errors);
            var result = InvokeExecute<JObject>(brokerage, TradierApiRequestType.Standard, max: 3);

            // authentication faults are not retryable: fail fast with a single fatal error and no retry
            Assert.IsNull(result);
            Assert.AreEqual(1, errors.Count);
            Assert.AreEqual("TradierFault", errors[0].Code);
            restClient.Verify(x => x.Execute(It.IsAny<IRestRequest>()), Times.Once);
        }

        // Orders placed in the Tradier account outside of the algorithm are offered to it through the
        // NewBrokerageOrderNotification event, so a brokerage message handler can take ownership of them
        [Test]
        public void NotifiesOrdersPlacedOutsideOfTheAlgorithm()
        {
            var orderProvider = new OrderProvider();
            var brokerage = CreateBrokerageWithOrderTracking(orderProvider);

            List<OrderEvent> orderEvents = [];
            brokerage.OrdersStatusChanged += (_, events) => orderEvents.AddRange(events);

            Order notifiedOrder = null;
            brokerage.NewBrokerageOrderNotification += (_, e) =>
            {
                notifiedOrder = e.Order;
                // this is what the transaction handler does when the algorithm accepts the order: it assigns the Lean id
                orderProvider.Add(e.Order);
            };

            Assert.AreEqual("Tracked",
                InvokeHandleBrokerageSideOrder(brokerage, CreateBrokerageSideOrder(TradierOrderStatus.Filled, quantityExecuted: 10m)));

            Assert.IsNotNull(notifiedOrder);
            Assert.AreEqual(OrderType.Market, notifiedOrder.Type);
            Assert.AreEqual("SPY", notifiedOrder.Symbol.Value);
            Assert.AreEqual(10m, notifiedOrder.Quantity);
            Assert.AreEqual(BrokerageSideOrderId.ToStringInvariant(), notifiedOrder.BrokerId.Single());

            // the order is first reported as submitted and then the fill it already had when we found it is emitted
            Assert.AreEqual(2, orderEvents.Count);
            Assert.AreEqual(OrderStatus.Submitted, orderEvents[0].Status);
            Assert.AreEqual(OrderStatus.Filled, orderEvents[1].Status);
            Assert.AreEqual(10m, orderEvents[1].FillQuantity);
            Assert.AreEqual(123.45m, orderEvents[1].FillPrice);
            Assert.IsFalse(GetCachedOpenOrders(brokerage).Contains(BrokerageSideOrderId));
        }

        [Test]
        public void TracksOpenOrdersPlacedOutsideOfTheAlgorithmForFutureFills()
        {
            var orderProvider = new OrderProvider();
            var brokerage = CreateBrokerageWithOrderTracking(orderProvider);

            List<OrderEvent> orderEvents = [];
            brokerage.OrdersStatusChanged += (_, events) => orderEvents.AddRange(events);
            brokerage.NewBrokerageOrderNotification += (_, e) => orderProvider.Add(e.Order);

            Assert.AreEqual("Tracked", InvokeHandleBrokerageSideOrder(brokerage, CreateBrokerageSideOrder(TradierOrderStatus.Open)));

            // the order is still open, so it's only reported as submitted, no fill event yet
            Assert.AreEqual(1, orderEvents.Count);
            Assert.AreEqual(OrderStatus.Submitted, orderEvents[0].Status);

            // and it's cached so the regular fill detection picks up its fills from now on
            Assert.IsTrue(GetCachedOpenOrders(brokerage).Contains(BrokerageSideOrderId));
        }

        // Lean only applies the fills of fill events, so the executed part of an order that closed without
        // filling has to be reported on its own before the final status
        [TestCase(TradierOrderStatus.Canceled, OrderStatus.Canceled)]
        [TestCase(TradierOrderStatus.Expired, OrderStatus.Invalid)]
        public void ReportsThePartialFillOfClosedOrdersPlacedOutsideOfTheAlgorithm(TradierOrderStatus status, OrderStatus expectedFinalStatus)
        {
            var orderProvider = new OrderProvider();
            var brokerage = CreateBrokerageWithOrderTracking(orderProvider);

            List<OrderEvent> orderEvents = [];
            brokerage.OrdersStatusChanged += (_, events) => orderEvents.AddRange(events);
            brokerage.NewBrokerageOrderNotification += (_, e) => orderProvider.Add(e.Order);

            Assert.AreEqual("Tracked",
                InvokeHandleBrokerageSideOrder(brokerage, CreateBrokerageSideOrder(status, quantityExecuted: 4m)));

            Assert.AreEqual(3, orderEvents.Count);
            Assert.AreEqual(OrderStatus.Submitted, orderEvents[0].Status);
            Assert.AreEqual(OrderStatus.PartiallyFilled, orderEvents[1].Status);
            Assert.AreEqual(4m, orderEvents[1].FillQuantity);
            Assert.AreEqual(123.45m, orderEvents[1].FillPrice);
            Assert.AreEqual(expectedFinalStatus, orderEvents[2].Status);
            Assert.AreEqual(0m, orderEvents[2].FillQuantity);
            Assert.IsFalse(GetCachedOpenOrders(brokerage).Contains(BrokerageSideOrderId));
        }

        // Not accepting the order is what the default brokerage message handler does, it's left untracked
        [Test]
        public void DoesNotTrackOrdersPlacedOutsideOfTheAlgorithmWhenTheyAreNotAccepted()
        {
            var brokerage = CreateBrokerageWithOrderTracking(new OrderProvider());

            List<OrderEvent> orderEvents = [];
            brokerage.OrdersStatusChanged += (_, events) => orderEvents.AddRange(events);

            var notified = false;
            // the default brokerage message handler ignores these orders, leaving the Lean order id unset
            brokerage.NewBrokerageOrderNotification += (_, e) => notified = true;

            Assert.AreEqual("NotAccepted",
                InvokeHandleBrokerageSideOrder(brokerage, CreateBrokerageSideOrder(TradierOrderStatus.Filled, quantityExecuted: 10m)));

            Assert.IsTrue(notified);
            Assert.IsEmpty(orderEvents);
            Assert.IsFalse(GetCachedOpenOrders(brokerage).Contains(BrokerageSideOrderId));
        }

        // Orders LEAN cannot represent are never offered to the algorithm: the multileg only order types, and the
        // multileg classes, which Tradier reports on the underlying and would otherwise convert to a single order on it
        [TestCase(TradierOrderType.Credit, TradierOrderClass.Multileg)]
        [TestCase(TradierOrderType.Debit, TradierOrderClass.Multileg)]
        [TestCase(TradierOrderType.Even, TradierOrderClass.Multileg)]
        [TestCase(TradierOrderType.Market, TradierOrderClass.Multileg)]
        [TestCase(TradierOrderType.Limit, TradierOrderClass.Combo)]
        public void ReportsUnsupportedOrdersAsUnprocessable(TradierOrderType type, TradierOrderClass orderClass)
        {
            var brokerage = CreateBrokerageWithOrderTracking(new OrderProvider());

            List<OrderEvent> orderEvents = [];
            brokerage.OrdersStatusChanged += (_, events) => orderEvents.AddRange(events);

            var notified = false;
            brokerage.NewBrokerageOrderNotification += (_, e) => notified = true;

            var brokerageSideOrder = CreateBrokerageSideOrder(TradierOrderStatus.Open, type: type, orderClass: orderClass);
            Assert.AreEqual("Unprocessable", InvokeHandleBrokerageSideOrder(brokerage, brokerageSideOrder));

            Assert.IsFalse(notified);
            Assert.IsEmpty(orderEvents);
            Assert.IsFalse(GetCachedOpenOrders(brokerage).Contains(BrokerageSideOrderId));
        }

        // Declining the order is a valid outcome, so the algorithm keeps running: it's warned once and the id is
        // marked as verified, else every poll would offer the order to the algorithm again
        [Test]
        public void WarnsOnceAboutOrdersPlacedOutsideOfTheAlgorithmThatItDoesNotAccept()
        {
            var brokerageSideOrder = CreateBrokerageSideOrder(TradierOrderStatus.Filled, quantityExecuted: 10m);
            // the order has to look newer than the brokerage instance for the fill polling to flag it
            brokerageSideOrder.TransactionDate = DateTime.UtcNow.AddHours(1);

            var restClient = new Mock<IRestClient>();
            restClient.Setup(x => x.Execute(It.IsAny<IRestRequest>())).Returns(() => CreateResponse(SerializeOrders(brokerageSideOrder)));

            var brokerage = CreateBrokerageWithOrderTracking(new OrderProvider(), restClient.Object);

            var notifications = 0;
            brokerage.NewBrokerageOrderNotification += (_, e) => Interlocked.Increment(ref notifications);

            List<BrokerageMessageEvent> messages = [];
            var warned = new ManualResetEvent(false);
            brokerage.Message += (_, e) =>
            {
                lock (messages) { messages.Add(e); }
                if (e.Code == "UnknownOrderId")
                {
                    warned.Set();
                }
            };

            InvokeCheckForFills(brokerage);

            Assert.IsTrue(warned.WaitOne(TimeSpan.FromSeconds(30)),
                "The order was never reported. Messages: " + string.Join(" | ", messages.Select(x => $"{x.Type}:{x.Code}:{x.Message}")));
            var warning = messages.Single(x => x.Code == "UnknownOrderId");
            Assert.AreEqual(BrokerageMessageType.Warning, warning.Type);
            Assert.IsTrue(warning.Message.Contains(BrokerageSideOrderId.ToStringInvariant()), warning.Message);
            Assert.AreEqual(1, notifications);

            // the id is verified right after the warning
            var verifiedOrderIDs = GetPrivateField<FixedSizeHashQueue<long>>(typeof(TradierBrokerage), brokerage, "_verifiedUnknownTradierOrderIDs");
            Assert.IsTrue(SpinWait.SpinUntil(() => verifiedOrderIDs.Contains(BrokerageSideOrderId), TimeSpan.FromSeconds(5)));

            // so the next poll neither flags it nor offers it again
            InvokeCheckForFills(brokerage);
            Assert.IsEmpty(GetPrivateField<HashSet<long>>(typeof(TradierBrokerage), brokerage, "_unknownTradierOrderIDs"));
            Assert.AreEqual(1, notifications);
        }

        // A failed verification used to add the unknown ids back to the pending set, but a new verification task only
        // fires when that set is empty, so a single failure disabled the unknown order detection for the rest of the run
        [Test]
        public void RechecksUnknownOrderIdsAfterAFailedVerification()
        {
            var brokerageSideOrder = CreateBrokerageSideOrder(TradierOrderStatus.Open);
            // the order has to look newer than the brokerage instance for the fill polling to flag it
            brokerageSideOrder.TransactionDate = DateTime.UtcNow.AddHours(1);

            var restClient = new Mock<IRestClient>();
            restClient.Setup(x => x.Execute(It.IsAny<IRestRequest>())).Returns(() => CreateResponse(SerializeOrders(brokerageSideOrder)));

            // fail the verification: the order provider lookup is the first thing the task does
            var orderProvider = new Mock<IOrderProvider>();
            orderProvider.Setup(x => x.GetOrdersByBrokerageId(It.IsAny<string>())).Throws(new Exception("Verification failed"));
            var brokerage = CreateBrokerageWithOrderTracking(orderProvider.Object, restClient.Object);

            List<BrokerageMessageEvent> messages = [];
            var verificationFailures = 0;
            var verificationFailed = new AutoResetEvent(false);
            brokerage.Message += (_, e) =>
            {
                lock (messages) { messages.Add(e); }
                if (e.Code == "UnknownIdResolution")
                {
                    Interlocked.Increment(ref verificationFailures);
                    verificationFailed.Set();
                }
            };

            InvokeCheckForFills(brokerage);

            // the verification runs two seconds after the order is flagged
            Assert.IsTrue(verificationFailed.WaitOne(TimeSpan.FromSeconds(30)),
                "The verification was never attempted. Messages: " + string.Join(" | ", messages.Select(x => $"{x.Type}:{x.Code}:{x.Message}")));

            // the failed task releases the ids once it's done, keep polling like the fill timer does until a new one fires
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!verificationFailed.WaitOne(TimeSpan.FromMilliseconds(100)))
            {
                Assert.Less(DateTime.UtcNow, deadline, "The verification was never retried");
                InvokeCheckForFills(brokerage);
            }
            Assert.AreEqual(2, verificationFailures);
        }

        // the orders container is only meant to be deserialized, its converter doesn't write, so wrap the orders by hand
        private static string SerializeOrders(params TradierOrder[] orders)
        {
            return $"{{\"orders\":{{\"order\":{JsonConvert.SerializeObject(orders)}}}}}";
        }

        private static TradierOrder CreateBrokerageSideOrder(TradierOrderStatus status, decimal quantityExecuted = 0m,
            TradierOrderType type = TradierOrderType.Market, TradierOrderClass orderClass = TradierOrderClass.Equity)
        {
            return new TradierOrder
            {
                Id = BrokerageSideOrderId,
                Type = type,
                Symbol = "SPY",
                Direction = TradierOrderDirection.Buy,
                Quantity = 10m,
                Status = status,
                Duration = TradierOrderDuration.Day,
                QuantityExecuted = quantityExecuted,
                RemainingQuantity = 10m - quantityExecuted,
                LastFillPrice = quantityExecuted > 0 ? 123.45m : 0m,
                AverageFillPrice = quantityExecuted > 0 ? 123.45m : 0m,
                Class = orderClass,
                TransactionDate = DateTime.UtcNow,
                CreatedDate = DateTime.UtcNow
            };
        }

        // Builds a brokerage with just the state the brokerage side order handling needs, skipping the heavy Initialize
        // (license validation, timers, streaming threads) that a full construction would trigger.
        private static TradierBrokerage CreateBrokerageWithOrderTracking(IOrderProvider orderProvider, IRestClient restClient = null)
        {
            var brokerage = restClient == null ? new TradierBrokerage() : CreateBrokerageWithRestClient(restClient, []);

            SetPrivateField(typeof(TradierBrokerage), brokerage, "_orderProvider", orderProvider);
            SetPrivateField(typeof(TradierBrokerage), brokerage, "_securityProvider", new SecurityProvider());
            SetPrivateField(typeof(TradierBrokerage), brokerage, "_symbolMapper", new TradierSymbolMapper(_ => null));

            // the cached open orders are keyed by a private type, so let reflection create the dictionary for us
            var cachedOpenOrders = typeof(TradierBrokerage).GetField("_cachedOpenOrdersByTradierOrderID",
                BindingFlags.NonPublic | BindingFlags.Instance);
            cachedOpenOrders.SetValue(brokerage, Activator.CreateInstance(cachedOpenOrders.FieldType));

            return brokerage;
        }

        private static IDictionary GetCachedOpenOrders(TradierBrokerage brokerage)
        {
            return GetPrivateField<IDictionary>(typeof(TradierBrokerage), brokerage, "_cachedOpenOrdersByTradierOrderID");
        }

        // the result type is private to the brokerage, so compare its name
        private static string InvokeHandleBrokerageSideOrder(TradierBrokerage brokerage, TradierOrder brokerageSideOrder)
        {
            return InvokePrivate(brokerage, "HandleBrokerageSideOrder", brokerageSideOrder).ToString();
        }

        [Test]
        public void DoesNotBlockOtherRequestsWhileRetrying()
        {
            var errors = new List<BrokerageMessageEvent>();
            var firstAttemptSent = new ManualResetEventSlim(false);
            var restClient = new Mock<IRestClient>();
            // the order request fails with a 500 on its first attempt and succeeds on the retry
            restClient.Setup(x => x.Execute(It.Is<IRestRequest>(r => r.Resource == "accounts/orders")))
                .Returns(() =>
                {
                    if (firstAttemptSent.IsSet)
                    {
                        return CreateResponse("{\"ok\":true}");
                    }
                    firstAttemptSent.Set();
                    return CreateResponse("Internal Server Error", HttpStatusCode.InternalServerError);
                });
            // the account request succeeds right away
            restClient.Setup(x => x.Execute(It.Is<IRestRequest>(r => r.Resource == "user/profile")))
                .Returns(CreateResponse("{\"ok\":true}"));

            var brokerage = CreateBrokerageWithRestClient(restClient.Object, errors);
            var orderRequest = Task.Run(() => InvokeExecute<JObject>(brokerage, TradierApiRequestType.Orders, max: 1, resource: "accounts/orders"));
            Assert.IsTrue(firstAttemptSent.Wait(TimeSpan.FromSeconds(5)), "the order request never sent its first attempt");

            // the order request is now sleeping before its retry; the account request must not wait for it
            var accountRequest = Task.Run(() => InvokeExecute<JObject>(brokerage, TradierApiRequestType.Standard, max: 1));
            Assert.IsTrue(accountRequest.Wait(TimeSpan.FromSeconds(1)), "the account request waited on the retrying order request");
            Assert.AreEqual(true, accountRequest.Result["ok"].Value<bool>());

            Assert.IsTrue(orderRequest.Wait(TimeSpan.FromSeconds(10)), "the order request never completed its retry");
            Assert.AreEqual(true, orderRequest.Result["ok"].Value<bool>());
            Assert.IsEmpty(errors);
        }

        // Live version of the test above, against the Tradier sandbox. Costs three sandbox requests and about four seconds.
        [Test, Explicit("Requires Tradier sandbox credentials")]
        public void DoesNotBlockOtherRequestsWhileRetryingLive()
        {
            using var brokerage = CreateLiveBrokerage();
            var rejectedResource = $"accounts/{TradierBrokerageFactory.Configuration.AccountId}/does-not-exist";
            var firstAttemptSent = SignalFirstResponse(brokerage, rejectedResource);

            // Tradier rejects the path, so Execute sleeps 3 s before its single retry
            var stopwatch = Stopwatch.StartNew();
            var rejectedRequest = Task.Run(() => InvokeExecute<JObject>(brokerage, TradierApiRequestType.Orders, max: 1, resource: rejectedResource));
            Assert.IsTrue(firstAttemptSent.Wait(TimeSpan.FromSeconds(5)), "the rejected request never sent its first attempt");

            // the account request must not wait for that sleep
            var accountRequest = Task.Run(brokerage.GetCashBalance);
            Assert.IsTrue(accountRequest.Wait(TimeSpan.FromSeconds(1.5)), "the account request waited on the retrying rejected request");
            Assert.IsNotNull(accountRequest.Result);

            Assert.IsTrue(rejectedRequest.Wait(TimeSpan.FromSeconds(15)), "the rejected request never completed its retry");
            Assert.IsTrue(stopwatch.Elapsed >= TimeSpan.FromSeconds(3), "the rejected request did not go through the retry sleep");
        }

        // one order per symbol, because the plugin allows only one open order per symbol
        private static readonly string[] BurstTickers =
        {
            "AAPL", "MSFT", "AMZN", "GOOGL", "GOOG", "META", "NVDA", "TSLA", "JPM", "JNJ", "V", "PG", "UNH", "HD", "MA", "XOM",
            "BAC", "PFE", "ABBV", "KO", "PEP", "CSCO", "CVX", "TMO", "AVGO", "COST", "MRK", "WMT", "DIS", "ABT", "ACN", "ADBE",
            "CRM", "DHR", "MCD", "NKE", "NFLX", "LLY", "VZ", "T", "INTC", "CMCSA", "WFC", "ORCL", "QCOM", "TXN", "AMD", "HON",
            "UNP", "PM", "LOW", "IBM", "AMGN", "CAT", "GS", "MS", "BLK", "SBUX", "INTU", "GE", "RTX", "BA", "DE", "LMT", "SPGI",
            "AXP", "BKNG", "GILD", "MDT", "ADP", "MDLZ", "TJX", "C", "CVS", "SCHW", "MMM", "USB", "CB", "ELV", "BMY", "PLD",
            "SO", "DUK", "NEE", "MO", "TGT", "CL", "PYPL", "AMAT", "ISRG", "ADI", "REGN", "VRTX", "ZTS", "PGR", "BDX", "CI",
            "MU", "LRCX", "F", "GM", "UBER", "ABNB", "PLTR", "KHC", "GIS", "EBAY", "MAR", "HLT"
        };

        // Sends 100 limit orders to the real API at the same time, far below the market so nothing fills, then cancels them.
        // Tradier allows 60 trading requests per minute and the plugin stays a bit below that: the first batch goes out at once and the rest wait for free slots,
        // with no 429 and no duplicate orders. Costs about 200 trading requests and 2 to 3 minutes.
        [Test, Explicit("Places and cancels 100 orders in the Tradier sandbox")]
        public void PlacesOrderBurstWithinTradingRateLimit()
        {
            var orderProvider = new OrderProvider();
            var securityProvider = new SecurityProvider();
            using var brokerage = CreateLiveBrokerage(orderProvider, securityProvider);
            var messages = new ConcurrentBag<BrokerageMessageEvent>();
            brokerage.Message += (_, e) => messages.Add(e);

            var quotes = brokerage.GetQuotes(BurstTickers.ToList()).Where(x => x.Last > 0).Take(100).ToList();
            Assert.AreEqual(100, quotes.Count, "not enough quoted symbols for the burst");
            var orders = quotes.Select(quote => new LimitOrder(Symbol.Create(quote.Symbol, SecurityType.Equity, Market.USA), 1,
                Math.Max(0.01m, Math.Round(quote.Last / 2, 2)), DateTime.UtcNow, properties: new OrderProperties { TimeInForce = TimeInForce.Day })).ToList();
            orders.ForEach(orderProvider.Add);
            // create the securities up front, the test security provider is not thread safe
            orders.ForEach(order => securityProvider.GetSecurity(order.Symbol));

            // orders left open on these symbols by earlier runs do not count
            var openBefore = brokerage.GetOpenOrders().Count(x => orders.Any(o => o.Symbol == x.Symbol));

            var stopwatch = Stopwatch.StartNew();
            var placements = orders.Select(order => Task.Run(() => (Order: order, Placed: brokerage.PlaceOrder(order), ReturnedAt: stopwatch.Elapsed))).ToArray();
            Assert.IsTrue(Task.WaitAll(placements, TimeSpan.FromMinutes(3)), "placing 100 orders did not finish in 3 minutes");
            var results = placements.Select(x => x.Result).OrderBy(x => x.ReturnedAt).ToList();
            var placed = results.Where(x => x.Placed).Select(x => x.Order).ToList();
            var longestWait = Enumerable.Range(1, results.Count - 1).Select(i => (Request: i + 1, Wait: results[i].ReturnedAt - results[i - 1].ReturnedAt)).OrderByDescending(x => x.Wait).First();
            Log.Trace($"PlacesOrderBurstWithinTradingRateLimit(): placed {placed.Count}/100 in {stopwatch.Elapsed}; " +
                $"request 1 returned at {results[0].ReturnedAt}, 100 at {results[^1].ReturnedAt}; longest wait {longestWait.Wait} before request {longestWait.Request}; " +
                $"message codes: {string.Join(", ", messages.Select(x => x.Code).Distinct())}");

            // what Tradier holds for these orders, before the clean up
            var brokerIds = placed.SelectMany(x => x.BrokerId).ToList();
            var openOrders = brokerage.GetOpenOrders();
            var openById = openOrders.Count(x => x.BrokerId.Any(brokerIds.Contains));
            var openBySymbol = openOrders.Count(x => orders.Any(o => o.Symbol == x.Symbol));

            var cancels = placed.Select(order => Task.Run(() => brokerage.CancelOrder(order))).ToArray();
            Assert.IsTrue(Task.WaitAll(cancels, TimeSpan.FromMinutes(3)), "cancelling the orders did not finish in 3 minutes");
            Log.Trace($"PlacesOrderBurstWithinTradingRateLimit(): cancelled {cancels.Count(x => x.Result)}/{placed.Count} in {stopwatch.Elapsed}");

            Assert.IsEmpty(messages.Where(x => x.Code == "TooManyRequests"), "Tradier answered 429 during the burst");
            Assert.AreEqual(100, placed.Count, "not every order was placed: " + string.Join(" | ", messages.Where(x => x.Type != BrokerageMessageType.Information).Select(x => x.Message).Take(5)));
            Assert.AreEqual(100, brokerIds.Distinct().Count(), "duplicate broker ids");
            Assert.AreEqual(100, openById, "Tradier did not show every placed order as open");
            Assert.AreEqual(openBefore + 100, openBySymbol, "Tradier shows extra orders on the burst symbols, possible duplicates");
            Assert.AreEqual(100, cancels.Count(x => x.Result), "not every order was cancelled");
        }

        [TestCase("<html>\r\n<head><title>502 Bad Gateway</title></head>\r\n<body>\r\n<center><h1>502 Bad Gateway</h1></center>\r\n<hr><center>nginx</center>\r\n</body>\r\n</html>", true)]
        [TestCase("<!DOCTYPE html><html><head><title>502 Bad Gateway</title></head><body></body></html>", true)]
        [TestCase("An error occurred while communicating with the backend.", false)]
        [TestCase("{\"errors\":{\"error\":\"Something bad happened\"}}", false)]
        public void ReplacesProxyHtmlPageWithActionableMessageAfterRetriesAreExhausted(string body, bool isProxyHtml)
        {
            var restClient = new Mock<IRestClient>();
            restClient.Setup(x => x.Execute(It.IsAny<IRestRequest>())).Returns(() => CreateResponse(body, HttpStatusCode.BadGateway));

            var brokerage = CreateBrokerageWithRestClient(restClient.Object, []);
            var messages = new List<BrokerageMessageEvent>();
            brokerage.Message += (_, e) => messages.Add(e);
            var result = InvokeExecute<JObject>(brokerage, TradierApiRequestType.Standard, max: 1);

            Assert.IsNull(result);
            Assert.AreEqual(1, messages.Count);
            Assert.AreEqual(BrokerageMessageType.Error, messages[0].Type);

            var message = messages[0].Message;
            Assert.AreEqual("BadGateway", messages[0].Code);
            Assert.IsTrue(message.StartsWith("Tradier returned BadGateway for GET user/profile and the request still failed after 1 retries. "), message);
            if (isProxyHtml)
            {
                Assert.IsTrue(message.Contains("Tradier's API is likely temporarily unavailable"), message);
                Assert.IsFalse(message.Contains("<html"), message);
            }
            else
            {
                Assert.IsTrue(message.Contains($"Response: {body}"), message);
            }
            restClient.Verify(x => x.Execute(It.IsAny<IRestRequest>()), Times.Exactly(2));
        }

        private static IRestResponse CreateResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            return new RestResponse
            {
                Content = content,
                StatusCode = statusCode,
                ResponseStatus = ResponseStatus.Completed
            };
        }

        // Builds a brokerage with just the state Execute needs (rest client + rate gate), skipping the heavy Initialize
        // (license validation, timers, streaming threads) that a full construction would trigger.
        private static TradierBrokerage CreateBrokerageWithRestClient(IRestClient restClient, List<BrokerageMessageEvent> errors)
        {
            var brokerage = new TradierBrokerage();
            brokerage.Message += (_, e) =>
            {
                // OnMessage elevates NullResponse warnings to Error when the machine-local clock falls within
                // US equity market hours; ignore them so these tests are deterministic regardless of run time
                if (e.Type == BrokerageMessageType.Error && e.Code != "NullResponse")
                {
                    errors.Add(e);
                }
            };

            SetPrivateField(typeof(BaseWebsocketsBrokerage), brokerage, "_restClient", restClient);
            SetPrivateField(typeof(TradierBrokerage), brokerage, "_rateLimitNextRequest",
                new Dictionary<TradierApiRequestType, RateGate>
                {
                    { TradierApiRequestType.Standard, new RateGate(1, TimeSpan.FromMilliseconds(1)) },
                    { TradierApiRequestType.Orders, new RateGate(1, TimeSpan.FromMilliseconds(1)) }
                });

            return brokerage;
        }

        // Builds a brokerage for the account in config.json, reading the sandbox flag the same way the factory does
        private static TradierBrokerage CreateLiveBrokerage(IOrderProvider orderProvider = null, ISecurityProvider securityProvider = null)
        {
            var environment = TradierBrokerageFactory.Configuration.Environment;
            var useSandbox = string.IsNullOrEmpty(environment) ? TradierBrokerageFactory.Configuration.UseSandbox : environment.ToLowerInvariant() == "paper";
            return new TradierBrokerage(null, orderProvider, securityProvider, null, useSandbox, TradierBrokerageFactory.Configuration.AccountId, TradierBrokerageFactory.Configuration.AccessToken);
        }

        // Swaps in a rest client that still calls Tradier and signals once a request for the resource has its response
        private static ManualResetEventSlim SignalFirstResponse(TradierBrokerage brokerage, string resource)
        {
            var signal = new ManualResetEventSlim(false);
            var realRestClient = GetPrivateField<IRestClient>(typeof(BaseWebsocketsBrokerage), brokerage, "_restClient");
            var restClient = new Mock<IRestClient>();
            restClient.Setup(x => x.Execute(It.IsAny<IRestRequest>()))
                .Returns((IRestRequest request) =>
                {
                    var response = realRestClient.Execute(request);
                    if (request.Resource == resource)
                    {
                        signal.Set();
                    }
                    return response;
                });
            SetPrivateField(typeof(BaseWebsocketsBrokerage), brokerage, "_restClient", restClient.Object);
            return signal;
        }

        private static T InvokeExecute<T>(TradierBrokerage brokerage, TradierApiRequestType type, int max, string resource = "user/profile") where T : new()
        {
            var method = typeof(TradierBrokerage)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(x => x.Name == "Execute" && !x.GetParameters().Any(p => p.IsOut))
                .MakeGenericMethod(typeof(T));
            try
            {
                return (T)method.Invoke(brokerage, new object[] { new RestRequest(resource, Method.GET), type, "", max });
            }
            catch (TargetInvocationException e)
            {
                throw e.InnerException;
            }
        }

        private static void InvokeCheckForFills(TradierBrokerage brokerage)
        {
            InvokePrivate(brokerage, "CheckForFills");
        }

        private static object InvokePrivate(TradierBrokerage brokerage, string name, params object[] args)
        {
            var method = typeof(TradierBrokerage).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            try
            {
                return method.Invoke(brokerage, args);
            }
            catch (TargetInvocationException e)
            {
                throw e.InnerException;
            }
        }

        private static void SetPrivateField(Type type, object instance, string name, object value)
        {
            type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value);
        }

        private static T GetPrivateField<T>(Type type, object instance, string name)
        {
            return (T)type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
        }

        private class TestableTradierBrokerage : TradierBrokerage
        {
            public static TradierOrderDirection ConvertDirectionPublic(OrderDirection direction, SecurityType securityType, decimal holdingQuantity)
            {
                return ConvertDirection(direction, securityType, holdingQuantity);
            }
        }
    }
}