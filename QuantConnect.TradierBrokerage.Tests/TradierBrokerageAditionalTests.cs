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
using System.Collections.Generic;
using System.Net;
using System.Reflection;

namespace QuantConnect.Tests.Brokerages.Tradier
{
    [TestFixture]
    public class TradierBrokerageAditionalTests
    {
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