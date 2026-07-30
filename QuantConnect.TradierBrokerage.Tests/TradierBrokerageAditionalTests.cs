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
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.Tradier;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Util;
using RestSharp;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;

namespace QuantConnect.Tests.Brokerages.Tradier
{
    [TestFixture]
    public class TradierBrokerageAditionalTests
    {
        private const long BrokerageSideOrderId = 1234;
        private static readonly string[] BrokerageSideOrderBrokerIds = [BrokerageSideOrderId.ToStringInvariant()];

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

        // Orders placed directly in the Tradier account, outside of the algorithm, are offered to the algorithm through
        // the NewBrokerageOrderNotification event, so a brokerage message handler can take ownership of them instead of
        // the algorithm being terminated for interference it doesn't control
        [Test]
        public void NotifiesOrdersPlacedOutsideOfTheAlgorithm()
        {
            var orderProvider = new OrderProvider();
            var brokerage = CreateBrokerageWithOrderTracking(orderProvider);

            var orderEvents = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, events) => orderEvents.AddRange(events);

            Order notifiedOrder = null;
            brokerage.NewBrokerageOrderNotification += (_, e) =>
            {
                notifiedOrder = e.Order;
                // this is what the transaction handler does when the algorithm accepts the order: it assigns the Lean id
                orderProvider.Add(e.Order);
            };

            Assert.IsTrue(InvokeTryHandleBrokerageSideOrder(brokerage, CreateBrokerageSideOrder(TradierOrderStatus.Filled, quantityExecuted: 10m)));

            Assert.IsNotNull(notifiedOrder);
            Assert.AreEqual(OrderType.Market, notifiedOrder.Type);
            Assert.AreEqual("SPY", notifiedOrder.Symbol.Value);
            Assert.AreEqual(10m, notifiedOrder.Quantity);
            CollectionAssert.AreEqual(BrokerageSideOrderBrokerIds, notifiedOrder.BrokerId);

            // the order is first reported as submitted and then the fill it already had when we found it is emitted
            Assert.AreEqual(2, orderEvents.Count);
            Assert.AreEqual(OrderStatus.Submitted, orderEvents[0].Status);
            Assert.AreEqual(OrderStatus.Filled, orderEvents[1].Status);
            Assert.AreEqual(10m, orderEvents[1].FillQuantity);
            Assert.AreEqual(123.45m, orderEvents[1].FillPrice);
        }

        [Test]
        public void TracksOpenOrdersPlacedOutsideOfTheAlgorithmForFutureFills()
        {
            var orderProvider = new OrderProvider();
            var brokerage = CreateBrokerageWithOrderTracking(orderProvider);

            var orderEvents = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, events) => orderEvents.AddRange(events);
            brokerage.NewBrokerageOrderNotification += (_, e) => orderProvider.Add(e.Order);

            Assert.IsTrue(InvokeTryHandleBrokerageSideOrder(brokerage, CreateBrokerageSideOrder(TradierOrderStatus.Open)));

            // the order is still open, so it's only reported as submitted, no fill event yet
            Assert.AreEqual(1, orderEvents.Count);
            Assert.AreEqual(OrderStatus.Submitted, orderEvents[0].Status);

            // and it's cached so the regular fill detection picks up its fills from now on
            Assert.IsTrue(GetCachedOpenOrders(brokerage).Contains(BrokerageSideOrderId));
        }

        // When the order is not accepted, which is what the default brokerage message handler does, the order is left
        // untracked and reported back as unhandled so the caller can fail the algorithm like it did before
        [Test]
        public void DoesNotTrackOrdersPlacedOutsideOfTheAlgorithmWhenTheyAreNotAccepted()
        {
            var brokerage = CreateBrokerageWithOrderTracking(new OrderProvider());

            var orderEvents = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, events) => orderEvents.AddRange(events);

            var notified = false;
            // the default brokerage message handler ignores these orders, leaving the Lean order id unset
            brokerage.NewBrokerageOrderNotification += (_, e) => notified = true;

            Assert.IsFalse(InvokeTryHandleBrokerageSideOrder(brokerage, CreateBrokerageSideOrder(TradierOrderStatus.Filled, quantityExecuted: 10m)));

            Assert.IsTrue(notified);
            Assert.IsEmpty(orderEvents);
            Assert.IsFalse(GetCachedOpenOrders(brokerage).Contains(BrokerageSideOrderId));
        }

        private static TradierOrder CreateBrokerageSideOrder(TradierOrderStatus status, decimal quantityExecuted = 0m)
        {
            return new TradierOrder
            {
                Id = BrokerageSideOrderId,
                Type = TradierOrderType.Market,
                Symbol = "SPY",
                Direction = TradierOrderDirection.Buy,
                Quantity = 10m,
                Status = status,
                Duration = TradierOrderDuration.Day,
                QuantityExecuted = quantityExecuted,
                RemainingQuantity = 10m - quantityExecuted,
                LastFillPrice = quantityExecuted > 0 ? 123.45m : 0m,
                AverageFillPrice = quantityExecuted > 0 ? 123.45m : 0m,
                Class = TradierOrderClass.Equity,
                TransactionDate = DateTime.UtcNow,
                CreatedDate = DateTime.UtcNow
            };
        }

        // Builds a brokerage with just the state the brokerage side order handling needs, skipping the heavy Initialize
        // (license validation, timers, streaming threads) that a full construction would trigger.
        private static TradierBrokerage CreateBrokerageWithOrderTracking(OrderProvider orderProvider)
        {
            var brokerage = new TradierBrokerage();

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
            return (IDictionary)typeof(TradierBrokerage)
                .GetField("_cachedOpenOrdersByTradierOrderID", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(brokerage);
        }

        private static bool InvokeTryHandleBrokerageSideOrder(TradierBrokerage brokerage, TradierOrder brokerageSideOrder)
        {
            var method = typeof(TradierBrokerage).GetMethod("TryHandleBrokerageSideOrder", BindingFlags.NonPublic | BindingFlags.Instance);
            try
            {
                return (bool)method.Invoke(brokerage, [brokerageSideOrder]);
            }
            catch (TargetInvocationException e)
            {
                throw e.InnerException;
            }
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
                    { TradierApiRequestType.Standard, new RateGate(1, TimeSpan.FromMilliseconds(1)) }
                });

            return brokerage;
        }

        private static T InvokeExecute<T>(TradierBrokerage brokerage, TradierApiRequestType type, int max) where T : new()
        {
            var method = typeof(TradierBrokerage)
                .GetMethod("Execute", BindingFlags.NonPublic | BindingFlags.Instance)
                .MakeGenericMethod(typeof(T));
            try
            {
                return (T)method.Invoke(brokerage, new object[] { new RestRequest("user/profile", Method.GET), type, "", 0, max });
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

        private class TestableTradierBrokerage : TradierBrokerage
        {
            public static TradierOrderDirection ConvertDirectionPublic(OrderDirection direction, SecurityType securityType, decimal holdingQuantity)
            {
                return ConvertDirection(direction, securityType, holdingQuantity);
            }
        }
    }
}