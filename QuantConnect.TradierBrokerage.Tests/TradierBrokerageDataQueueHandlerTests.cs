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
using System.Linq;
using NUnit.Framework;
using System.Threading;
using QuantConnect.Data;
using QuantConnect.Logging;
using QuantConnect.Data.Market;
using System.Collections.Generic;
using System.Collections.Concurrent;
using QuantConnect.Brokerages.Tradier;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators;

namespace QuantConnect.Tests.Brokerages.Tradier
{
    [TestFixture]
    public partial class TradierBrokerageTests : BrokerageTests
    {
        private static IEnumerable<TestCaseData> TestParameters
        {
            get
            {
                // valid parameters, for example
                yield return new TestCaseData(Symbols.AAPL, Resolution.Tick, false);
                yield return new TestCaseData(Symbols.SPX, Resolution.Tick, false);

                // Option: AAPL
                var aaplOption = Symbol.CreateOption(Symbols.AAPL, Market.USA, Symbols.AAPL.SecurityType.DefaultOptionStyle(), OptionRight.Call, 227.5m, new DateTime(2025, 09, 12));
                yield return new TestCaseData(aaplOption, Resolution.Tick, false);
                yield return new TestCaseData(aaplOption, Resolution.Second, false);

                // IndexOption: SPX
                var spxOption = Symbol.CreateOption(Symbols.SPX, Symbols.SPX.ID.Market, SecurityType.IndexOption.DefaultOptionStyle(), OptionRight.Call, 6550m, new DateTime(2025, 09, 19));
                yield return new TestCaseData(spxOption, Resolution.Tick, false);

                // IndexOption: SPXW
                var spxwOption = Symbol.CreateOption(Symbols.SPX, "SPXW", Symbols.SPX.ID.Market, SecurityType.IndexOption.DefaultOptionStyle(), OptionRight.Call, 6580m, new DateTime(2025, 09, 12));
                yield return new TestCaseData(spxwOption, Resolution.Tick, false);
                yield return new TestCaseData(spxwOption, Resolution.Second, false);
            }
        }

        [Test, TestCaseSource(nameof(TestParameters)), Explicit("Long execution time")]
        public void StreamsData(Symbol symbol, Resolution resolution, bool throwsException)
        {
            var obj = new object();
            var cancelationTokenSource = new CancellationTokenSource();
            var resetEvent = new AutoResetEvent(false);

            var brokerage = (TradierBrokerage)Brokerage;
            var configs = new List<SubscriptionDataConfig>();
            if (resolution == Resolution.Tick)
            {
                var tradeConfig = new SubscriptionDataConfig(GetSubscriptionDataConfig<Tick>(symbol, resolution), tickType: TickType.Trade);
                var quoteConfig = new SubscriptionDataConfig(GetSubscriptionDataConfig<Tick>(symbol, resolution), tickType: TickType.Quote);
                configs.AddRange(tradeConfig, quoteConfig);
            }
            else
            {
                configs.Add(GetSubscriptionDataConfig<QuoteBar>(symbol, resolution));
                configs.Add(GetSubscriptionDataConfig<TradeBar>(symbol, resolution));
            }

            var incomingSymbolDataByTickType = new ConcurrentDictionary<(Symbol, TickType), List<BaseData>>();

            Action<BaseData> callback = (dataPoint) =>
            {
                if (dataPoint == null)
                {
                    return;
                }

                switch (dataPoint)
                {
                    case Tick tick:
                        AddOrUpdateDataPoint(incomingSymbolDataByTickType, tick.Symbol, tick.TickType, tick);
                        break;
                    case TradeBar tradeBar:
                        AddOrUpdateDataPoint(incomingSymbolDataByTickType, tradeBar.Symbol, TickType.Trade, tradeBar);
                        break;
                    case QuoteBar quoteBar:
                        AddOrUpdateDataPoint(incomingSymbolDataByTickType, quoteBar.Symbol, TickType.Quote, quoteBar);
                        break;
                }

                lock (obj)
                {
                    if (incomingSymbolDataByTickType.Count == configs.Count && incomingSymbolDataByTickType.Any(d => d.Value.Count > 2))
                    {
                        resetEvent.Set();
                    }
                }
            };

            foreach (var config in configs)
            {
                ProcessFeed(brokerage.Subscribe(config, (s, e) =>
                {
                    var dataPoint = ((NewDataAvailableEventArgs)e).DataPoint;
                    Log.Trace($"{dataPoint}. Time span: {dataPoint.Time} - {dataPoint.EndTime}");
                }),
                cancelationTokenSource,
                callback: callback);
            }

            resetEvent.WaitOne(TimeSpan.FromMinutes(2), cancelationTokenSource.Token);

            foreach (var config in configs)
            {
                brokerage.Unsubscribe(config);
            }

            resetEvent.WaitOne(TimeSpan.FromSeconds(5), cancelationTokenSource.Token);

            var symbolVolatilities = incomingSymbolDataByTickType.Where(kv => kv.Value.Count > 0).ToList();

            Assert.IsNotEmpty(symbolVolatilities);
            Assert.That(symbolVolatilities.Count, Is.GreaterThan(1));

            cancelationTokenSource.Cancel();
        }

        private void AddOrUpdateDataPoint(
            ConcurrentDictionary<(Symbol, TickType), List<BaseData>> dictionary,
            Symbol symbol,
            TickType tickType,
            BaseData dataPoint)
        {
            dictionary.AddOrUpdate(
                (symbol, tickType),
                [dataPoint], // Add scenario: create a new list with the dataPoint
                (key, existingList) =>
                {
                    existingList.Add(dataPoint); // Add dataPoint to the existing list
                    return existingList; // Return the updated list
                }
            );
        }
    }
}