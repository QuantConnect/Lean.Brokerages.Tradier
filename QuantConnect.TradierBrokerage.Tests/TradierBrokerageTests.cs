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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.Tradier;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Brokerages.Tradier
{
    public partial class TradierBrokerageTests : BrokerageTests
    {
        /// <summary>
        /// Provides the data required to test each order type in various cases
        /// </summary>
        private static TestCaseData[] OrderParameters()
        {
            return new[]
            {
                new TestCaseData(new MarketOrderTestParameters(Symbols.AAPL)).SetName("MarketOrder"),
                new TestCaseData(new LimitOrderTestParameters(Symbols.AAPL, 1000m, 0.01m)).SetName("LimitOrder"),
                new TestCaseData(new StopMarketOrderTestParameters(Symbols.AAPL, 1000m, 0.01m)).SetName("StopMarketOrder"),
                new TestCaseData(new StopLimitOrderTestParameters(Symbols.AAPL, 1000m, 0.01m)).SetName("StopLimitOrder")
            };
        }

        /// <summary>
        /// Provides the data required to test each order type in various cases
        /// Good Till Orders are not allowed when shorting
        /// </summary>
        private static TestCaseData[] ShortOrderParameters()
        {
            var orderProperties = new OrderProperties();
            orderProperties.TimeInForce = TimeInForce.Day;
            return new[]
            {
                new TestCaseData(new MarketOrderTestParameters(Symbols.AAPL, properties: orderProperties)).SetName("MarketOrder"),
                new TestCaseData(new LimitOrderTestParameters(Symbols.AAPL, 1000m, 0.01m, properties: orderProperties)).SetName("LimitOrder"),
                new TestCaseData(new StopMarketOrderTestParameters(Symbols.AAPL, 1000m, 0.01m, properties: orderProperties)).SetName("StopMarketOrder"),
                new TestCaseData(new StopLimitOrderTestParameters(Symbols.AAPL, 1000m, 0.01m, properties: orderProperties)).SetName("StopLimitOrder")
            };
        }

        /// <summary>
        /// Creates the brokerage under test
        /// </summary>
        /// <returns>A connected brokerage instance</returns>
        protected override IBrokerage CreateBrokerage(IOrderProvider orderProvider, ISecurityProvider securityProvider)
        {
            var useSandbox = TradierBrokerageFactory.Configuration.UseSandbox;
            var environment = TradierBrokerageFactory.Configuration.Environment;
            if (!string.IsNullOrEmpty(environment))
            {
                useSandbox = environment.ToLowerInvariant() == "paper";
            }
            var accountId = TradierBrokerageFactory.Configuration.AccountId;
            var accessToken = TradierBrokerageFactory.Configuration.AccessToken;

            return new TradierBrokerage(new AlgorithmStub(), orderProvider, securityProvider, new AggregationManager(), useSandbox, accountId, accessToken);
        }

        /// <summary>
        /// Gets the symbol to be traded, must be shortable
        /// </summary>
        protected override Symbol Symbol => Symbols.AAPL;

        /// <summary>
        /// Gets the security type associated with the <see cref="BrokerageTests.Symbol"/>
        /// </summary>
        protected override SecurityType SecurityType => SecurityType.Equity;

        /// <summary>
        /// Returns wether or not the brokers order methods implementation are async
        /// </summary>
        protected override bool IsAsync()
        {
            return false;
        }

        [TestCase("VXX190517P00016000", true)]
        [TestCase("AAPL231222C00200000", false)]
        public void GetQuotesDoesNotReturnNull(string contract, bool isEmpty)
        {
            var tradier = (TradierBrokerage) Brokerage;
            var quotes = tradier.GetQuotes(new List<string> { contract });

            Assert.IsNotNull(quotes);
            if(isEmpty)
            {
                Assert.IsEmpty(quotes);
            }
            else
            {
                Assert.IsNotEmpty(quotes);
            }
        }

        /// <summary>
        /// Gets the current market price of the specified security
        /// </summary>
        protected override decimal GetAskPrice(Symbol symbol)
        {
            var tradier = (TradierBrokerage) Brokerage;
            var quotes = tradier.GetQuotes(new List<string> {symbol.Value});
            return quotes.Single().Ask ?? 0;
        }

        [Test, TestCaseSource(nameof(OrderParameters))]
        public void AllowsOneActiveOrderPerSymbol(OrderTestParameters parameters)
        {
            // tradier's api gets special with zero holdings crossing in that they need to fill the order
            // before the next can be submitted, so we just limit this impl to only having on active order
            // by symbol at a time, new orders will issue cancel commands for the existing order

            bool orderFilledOrCanceled = false;
            var order = parameters.CreateLongOrder(1);
            EventHandler<List<OrderEvent>> brokerageOnOrderStatusChanged = (sender, args) =>
            {
                var orderEvent = args.Single();
                // we expect all orders to be cancelled except for market orders, they may fill before the next order is submitted
                if (orderEvent.OrderId == order.Id && orderEvent.Status == OrderStatus.Canceled || (order is MarketOrder && orderEvent.Status == OrderStatus.Filled))
                {
                    orderFilledOrCanceled = true;
                }
            };

            Brokerage.OrdersStatusChanged += brokerageOnOrderStatusChanged;

            // starting from zero initiate two long orders and see that the first is canceled
            PlaceOrderWaitForStatus(order, OrderStatus.Submitted);
            PlaceOrderWaitForStatus(parameters.CreateLongMarketOrder(1));

            Brokerage.OrdersStatusChanged -= brokerageOnOrderStatusChanged;

            Assert.IsTrue(orderFilledOrCanceled);
        }

        [Test]
        public void RejectedOrderForInsufficientBuyingPower()
        {
            var message = string.Empty;
            EventHandler<BrokerageMessageEvent> messageHandler = (s, e) => { message = e.Message; };

            Brokerage.Message += messageHandler;

            var symbol = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
            PlaceOrderWaitForStatus(new MarketOrder(symbol, 1000000, DateTime.Now), OrderStatus.Invalid, allowFailedSubmission: true);

            Brokerage.Message -= messageHandler;

            // Raw response: {"errors":{"error":["Backoffice rejected override of the order.","DayTradingBuyingPowerExceeded"]}}

            Assert.That(message.Contains("Backoffice rejected override of the order", StringComparison.InvariantCulture));
        }

        [Test]
        public void RejectedOrderForInvalidSymbol()
        {
            // This test exists to verify how rejected orders are handled when we don't receive an order ID back from Tradier
            var message = string.Empty;
            EventHandler<BrokerageMessageEvent> messageHandler = (s, e) => { message = e.Message; };

            Brokerage.Message += messageHandler;

            var symbol = Symbol.Create("XYZ", SecurityType.Equity, Market.USA);
            var orderProperties = new OrderProperties();
            orderProperties.TimeInForce = TimeInForce.Day;
            PlaceOrderWaitForStatus(new MarketOrder(symbol, -1, DateTime.Now, properties: orderProperties), OrderStatus.Invalid, allowFailedSubmission: true);

            Brokerage.Message -= messageHandler;

            // Raw response: "Order 1: Undefined symbol: XYZ."

            Assert.AreEqual("Order 1: Undefined symbol: XYZ", message);
        }

        [Test]
        public void RejectedCancelOrderIfNotOurs()
        {
            var message = string.Empty;
            EventHandler<BrokerageMessageEvent> messageHandler = (s, e) => { message = e.Message; };

            Brokerage.Message += messageHandler;

            var symbol = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
            var order = new MarketOrder(symbol, 1, DateTime.Now);
            order.BrokerId.Add("9999999999999999");

            Brokerage.CancelOrder(order);

            Brokerage.Message -= messageHandler;

            // Raw response: "Unauthorized Account: xxx"

            Assert.That(message.Contains($"Unauthorized Account: {TradierBrokerageFactory.Configuration.AccountId}", StringComparison.InvariantCulture));
        }

        [Test]
        public void RejectedCancelOrderIfAlreadyFilled()
        {
            var message = string.Empty;
            EventHandler<BrokerageMessageEvent> messageHandler = (s, e) => { message = e.Message; };

            Brokerage.Message += messageHandler;

            var symbol = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
            var order = new MarketOrder(symbol, 1, DateTime.Now);

            PlaceOrderWaitForStatus(order, OrderStatus.Filled);

            Brokerage.CancelOrder(order);

            Brokerage.Message -= messageHandler;

            Assert.That(message.Contains("Unable to cancel the order because it has already been filled or cancelled", StringComparison.InvariantCulture));
        }

        [Test]
        public void RejectedCancelOrderIfAlreadyCancelled()
        {
            var symbol = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
            var order = new LimitOrder(symbol, 1, 100, DateTime.Now);

            var canceledEvent = new ManualResetEvent(false);

            var message = string.Empty;
            EventHandler<BrokerageMessageEvent> messageHandler = (s, e) => { message = e.Message; };
            EventHandler<List<OrderEvent>> orderStatusHandler = (s, e) =>
            {
                order.Status = e.Single().Status;

                if (order.Status == OrderStatus.Canceled)
                {
                    canceledEvent.Set();
                }
            };

            Brokerage.Message += messageHandler;
            Brokerage.OrdersStatusChanged += orderStatusHandler;

            PlaceOrderWaitForStatus(order, OrderStatus.Submitted);

            Brokerage.CancelOrder(order);

            if (!canceledEvent.WaitOne(TimeSpan.FromSeconds(5)))
            {
                Log.Error("Timeout waiting for Canceled event");
            }

            Brokerage.CancelOrder(order);

            Brokerage.Message -= messageHandler;
            Brokerage.OrdersStatusChanged -= orderStatusHandler;

            Assert.That(message.Contains("Unable to cancel the order because it has already been filled or cancelled", StringComparison.InvariantCulture));
        }

        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void CancelOrders(OrderTestParameters parameters)
        {
            base.CancelOrders(parameters);
        }

        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void LongFromZero(OrderTestParameters parameters)
        {
            base.LongFromZero(parameters);
        }

        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void CloseFromLong(OrderTestParameters parameters)
        {
            base.CloseFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(ShortOrderParameters))]
        public override void ShortFromZero(OrderTestParameters parameters)
        {
            base.ShortFromZero(parameters);
        }

        [Test, TestCaseSource(nameof(ShortOrderParameters))]
        public override void CloseFromShort(OrderTestParameters parameters)
        {
            base.CloseFromShort(parameters);
        }

        [Test, TestCaseSource(nameof(ShortOrderParameters))]
        public override void ShortFromLong(OrderTestParameters parameters)
        {
            base.ShortFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(ShortOrderParameters))]
        public override void LongFromShort(OrderTestParameters parameters)
        {
            base.LongFromShort(parameters);
        }

        /// <summary>
        /// Tests the scenario where a market order transitions from a short position to a long position,
        /// crossing zero in the process. This test ensures the order status change events occur in the expected
        /// sequence: Submitted, PartiallyFilled, and Filled.
        /// 
        /// The method performs the following steps:
        /// <list type="number">
        /// <item>Creates a market order for the AAPL symbol with a TimeInForce property set to Day.</item>
        /// <item>Places a short market order to establish a short position of at least -1 quantity.</item>
        /// <item>Subscribes to the order status change events and records the status changes.</item>
        /// <item>Places a long market order that crosses zero, effectively transitioning from short to long.</item>
        /// <item>Asserts that the order is not null, has a BrokerId, and the status change events match the expected sequence.</item>
        /// </list>
        /// </summary>
        /// <param name="longQuantityMultiplayer">The multiplier for the long order quantity, relative to the default quantity.</param>
        [TestCase(4)]
        public void MarketCrossZeroLongFromShort(decimal longQuantityMultiplayer)
        {
            var expectedOrderStatusChangedOrdering = new[] { OrderStatus.Submitted, OrderStatus.PartiallyFilled, OrderStatus.Filled };
            var actualCrossZeroOrderStatusOrdering = new Queue<OrderStatus>();

            // create market order to holding something
            var marketOrder = new MarketOrderTestParameters(Symbols.AAPL, properties: new OrderProperties() { TimeInForce = TimeInForce.Day });

            // place short position to holding at least -1 quantity to run of cross zero order
            PlaceOrderWaitForStatus(marketOrder.CreateShortMarketOrder(-GetDefaultQuantity()), OrderStatus.Filled);

            // validate ordering of order status change events
            Brokerage.OrdersStatusChanged += (_, orderEvents) => actualCrossZeroOrderStatusOrdering.Enqueue(orderEvents[0].Status);

            // Place Order with crossZero processing
            var order = PlaceOrderWaitForStatus(marketOrder.CreateLongOrder(longQuantityMultiplayer * GetDefaultQuantity()), OrderStatus.Filled);

            Assert.IsNotNull(order);
            Assert.Greater(order.BrokerId.Count, 0);
            CollectionAssert.AreEquivalent(expectedOrderStatusChangedOrdering, actualCrossZeroOrderStatusOrdering);
        }
    }
}
