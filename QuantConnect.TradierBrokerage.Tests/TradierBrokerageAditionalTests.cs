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

using NUnit.Framework;
using QuantConnect.Brokerages.Tradier;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Util;
using System;

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

        private class TestableTradierBrokerage : TradierBrokerage
        {
            public static TradierOrderDirection ConvertDirectionPublic(OrderDirection direction, SecurityType securityType, decimal holdingQuantity)
            {
                return ConvertDirection(direction, securityType, holdingQuantity);
            }
        }
    }
}