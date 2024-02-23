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
using QuantConnect.Data;
using QuantConnect.Logging;
using QuantConnect.Securities;
using QuantConnect.Data.Market;
using QuantConnect.Configuration;
using QuantConnect.Brokerages.Tradier;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Securities.Option;
using QuantConnect.Interfaces;
using QuantConnect.Util;

namespace QuantConnect.Tests.Brokerages.Tradier
{
    [TestFixture]
    public class TradierBrokerageHistoryProviderTests
    {
        private TradierBrokerage _brokerage;
        private IOptionChainProvider _chainProvider;
        private bool _useSandbox = Config.GetBool("tradier-use-sandbox");
        private readonly string _environment = Config.Get("tradier-environment");
        private readonly string _accountId = Config.Get("tradier-account-id");
        private readonly string _accessToken = Config.Get("tradier-access-token");

        private static TestCaseData[] TestParameters
        {
            get
            {
                return new[]
                {
                    // valid parameters
                    new TestCaseData(Symbols.AAPL, Resolution.Tick, false, -1, TickType.Trade),
                    new TestCaseData(Symbols.AAPL, Resolution.Second, false, -1, TickType.Trade),
                    new TestCaseData(Symbols.AAPL, Resolution.Minute, false, 60 + 1, TickType.Trade),
                    new TestCaseData(Symbols.AAPL, Resolution.Hour, false, 7 * 2, TickType.Trade),
                    new TestCaseData(Symbols.AAPL, Resolution.Daily, false, 6, TickType.Trade),

                    // invalid tick type, null result
                    new TestCaseData(Symbols.AAPL, Resolution.Minute, true, 0, TickType.Quote),
                    new TestCaseData(Symbols.AAPL, Resolution.Minute, true, 0, TickType.OpenInterest),

                    // canonical symbol, null result
                    new TestCaseData(Symbols.SPY_Option_Chain, Resolution.Daily, true, 0, TickType.Trade),

                    // invalid security type, null result
                    new TestCaseData(Symbols.EURUSD, Resolution.Daily, true, 0, TickType.Trade)
                };
            }
        }

        [OneTimeSetUp]
        public void Setup()
        {
            if (!string.IsNullOrEmpty(_environment))
            {
                _useSandbox = _environment.ToLowerInvariant() == "paper";
            }

            _brokerage = new TradierBrokerage(null, null, null, null, _useSandbox, _accountId, _accessToken);
            _chainProvider = new CachingOptionChainProvider(new LiveOptionChainProvider(TestGlobals.DataCacheProvider, TestGlobals.MapFileProvider));
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            _brokerage.Disconnect();
            _brokerage.Dispose();
        }

        [Test, TestCaseSource(nameof(TestParameters))]
        public void GetsHistory(Symbol symbol, Resolution resolution, bool unsupported, int expectedCount, TickType tickType)
        {
            if (_useSandbox && (resolution == Resolution.Tick || resolution == Resolution.Second))
            {
                // sandbox doesn't allow tick data, we generate second resolution from tick
                return;
            }
            var mhdb = MarketHoursDatabase.FromDataFolder().GetEntry(symbol.ID.Market, symbol, symbol.SecurityType);

            GetStartEndTime(mhdb, resolution, expectedCount, out var startUtc, out var endUtc);

            var request = new HistoryRequest(startUtc, endUtc, LeanData.GetDataType(resolution, tickType), symbol, resolution, mhdb.ExchangeHours,
                mhdb.DataTimeZone, null, false, false, DataNormalizationMode.Adjusted, tickType);

            if (unsupported)
            {
                Assert.IsNull(_brokerage.GetHistory(request));
            }
            else
            {
                var count = GetHistoryHelper(request);

                if (request.Resolution == Resolution.Tick || request.Resolution == Resolution.Second)
                {
                    // more than 60 points
                    Assert.Greater(count, 60, $"Symbol: {request.Symbol.Value}. Resolution {request.Resolution}");
                }
                else
                {
                    Assert.AreEqual(expectedCount, count, $"Symbol: {request.Symbol.Value}. Resolution {request.Resolution}");
                }
            }
        }

        [TestCase(Resolution.Daily, 20)]
        [TestCase(Resolution.Hour, 30)]
        [TestCase(Resolution.Minute, 60 * 10)]
        [TestCase(Resolution.Second, 60 * 10 * 5)]
        [TestCase(Resolution.Tick, -1)]
        public void GetsOptionHistory(Resolution resolution, int expectedCount)
        {
            if (_useSandbox && (resolution == Resolution.Tick || resolution == Resolution.Second))
            {
                // sandbox doesn't allow tick data, we generate second resolution from tick
                return;
            }
            var spy = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
            var mhdb = MarketHoursDatabase.FromDataFolder().GetEntry(spy.ID.Market, spy, spy.SecurityType);

            GetStartEndTime(mhdb, resolution, expectedCount, out var startUtc, out var endUtc);

            var chain = _chainProvider.GetOptionContractList(spy, startUtc.ConvertFromUtc(mhdb.ExchangeHours.TimeZone)).ToList();

            var quote = _brokerage.GetQuotes(new() { "SPY" }).First().Last;
            var option = chain.Where(x => x.ID.OptionRight == OptionRight.Call)
                // drop weeklies
                .Where(x => OptionSymbol.IsStandard(x))
                // not expired
                .Where(x => x.ID.Date >= endUtc.ConvertFromUtc(mhdb.ExchangeHours.TimeZone))
                // closest to expire first
                .OrderBy(x => x.ID.Date)
                // most in the money
                .ThenBy(x => x.ID.StrikePrice)
                // but not too far in the money
                .First(x => (x.ID.StrikePrice + quote * 0.01m) > quote);

            var request = new HistoryRequest(startUtc,
                endUtc,
                typeof(TradeBar),
                option,
                resolution,
                mhdb.ExchangeHours,
                mhdb.DataTimeZone,
                null,
                false,
                false,
                DataNormalizationMode.Adjusted,
                TickType.Trade);

            var count = GetHistoryHelper(request);

            // more than X points
            Assert.Greater(count, 15, $"Symbol: {request.Symbol.Value}. Resolution {request.Resolution}");
        }

        private void GetStartEndTime(MarketHoursDatabase.Entry entry, Resolution resolution, int expectedCount, out DateTime startTimeUtc, out DateTime endTimeUtc)
        {
            if (resolution == Resolution.Tick || resolution == Resolution.Second)
            {
                // for tick ask for X minutes worth of data
                expectedCount = 30;
                resolution = Resolution.Minute;
            }

            // tradier returns data for the last few days, so we need to adjust start and end time here
            endTimeUtc = DateTime.UtcNow.AddHours(-1);
            var endLocalTime = endTimeUtc.ConvertFromUtc(entry.ExchangeHours.TimeZone);
            var resolutionSpan = resolution.ToTimeSpan();

            var localStartTime = Time.GetStartTimeForTradeBars(entry.ExchangeHours, endLocalTime, resolutionSpan, expectedCount, false, entry.DataTimeZone);
            startTimeUtc = localStartTime.ConvertToUtc(entry.ExchangeHours.TimeZone);
        }

        private int GetHistoryHelper(HistoryRequest request)
        {
            var count = 0;
            BaseData previous = null;
            var history = _brokerage.GetHistory(request);

            Assert.IsNotNull(history);

            foreach (var data in history)
            {
                Assert.AreEqual(request.Resolution.ToTimeSpan(), data.EndTime - data.Time);

                if (previous != null)
                {
                    if(request.Resolution == Resolution.Tick)
                    {
                        Assert.IsTrue(previous.EndTime <= data.EndTime);
                    }
                    else
                    {
                        Assert.IsTrue(previous.EndTime < data.EndTime);
                    }
                }
                count++;
                previous = data;

                Log.Debug($"{data.EndTime}-{data.Time} {data}");
            }

            return count;
        }
    }
}
