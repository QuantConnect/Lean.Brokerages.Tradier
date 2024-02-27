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
 *
*/

using System;
using NodaTime;
using System.Linq;
using QuantConnect.Data;
using QuantConnect.Util;
using QuantConnect.Logging;
using QuantConnect.Data.Market;
using System.Collections.Generic;
using HistoryRequest = QuantConnect.Data.HistoryRequest;

namespace QuantConnect.Brokerages.Tradier
{
    /// <summary>
    /// Tradier Brokerage - IHistoryProvider implementation
    /// </summary>
    public partial class TradierBrokerage
    {
        private bool _loggedTradierSupportsOnlyTradeBars;
        private bool _loggedUnsupportedAssetForHistory;
        private bool _loggedInvalidTimeRangeForHistory;

        /// <summary>
        /// Gets the history for the requested security
        /// </summary>
        /// <param name="request">The historical data request</param>
        /// <returns>An enumerable of bars covering the span specified in the request</returns>
        public override IEnumerable<BaseData> GetHistory(HistoryRequest request)
        {
            if (!CanSubscribe(request.Symbol))
            {
                if (!_loggedUnsupportedAssetForHistory)
                {
                    _loggedUnsupportedAssetForHistory = true;
                    _algorithm?.Debug("Warning: Tradier does not support this asset for history requests.");
                    Log.Error("TradierBrokerage.GetHistory(): Unsupported asset: " + request.Symbol.Value);
                }
                return null;
            }

            if (request.StartTimeUtc >= request.EndTimeUtc)
            {
                if (!_loggedInvalidTimeRangeForHistory)
                {
                    _loggedInvalidTimeRangeForHistory = true;
                    _algorithm?.Debug("Warning: The request start date must precede the end date, no history returned.");
                    Log.Error("TradierBrokerage.GetHistory(): Invalid date range.");
                }
                return null;
            }

            if (request.TickType != TickType.Trade)
            {
                if (!_loggedTradierSupportsOnlyTradeBars)
                {
                    _loggedTradierSupportsOnlyTradeBars = true;
                    _algorithm?.Debug("Warning: Tradier history provider only supports trade information, does not support quotes.");
                    Log.Error("TradierBrokerage.GetHistory(): Tradier only supports TradeBars");
                }
                return null;
            }

            var start = request.StartTimeUtc.ConvertTo(DateTimeZone.Utc, TimeZones.NewYork);
            var end = request.EndTimeUtc.ConvertTo(DateTimeZone.Utc, TimeZones.NewYork);

            IEnumerable<BaseData> history;
            switch (request.Resolution)
            {
                case Resolution.Tick:
                    history = GetHistoryTick(request, start, end);
                    break;

                case Resolution.Second:
                    history = GetHistorySecond(request, start, end);
                    break;

                case Resolution.Minute:
                    history = GetHistoryMinute(request, start, end);
                    break;

                case Resolution.Hour:
                    history = GetHistoryHour(request, start, end);
                    break;

                case Resolution.Daily:
                    history = GetHistoryDaily(request, start, end);
                    break;

                default:
                    throw new ArgumentException("Invalid date range specified");
            }

            return history.Where(bar => bar.Time >= request.StartTimeLocal &&
                bar.EndTime <= request.EndTimeLocal &&
                request.ExchangeHours.IsOpen(bar.Time, bar.EndTime, request.IncludeExtendedMarketHours));
        }

        private IEnumerable<BaseData> GetHistoryTick(HistoryRequest request, DateTime start, DateTime end)
        {
            var symbol = request.Symbol;
            var exchangeTz = request.ExchangeHours.TimeZone;
            var history = GetTimeSeries(symbol, start, end, TradierTimeSeriesIntervals.Tick);

            if (history == null)
            {
                return Enumerable.Empty<BaseData>();
            }

            return history.Select(tick => new Tick
            {
                // ticks are returned in UTC
                Time = tick.Time.ConvertFromUtc(exchangeTz),
                Symbol = symbol,
                Value = tick.Price,
                TickType = TickType.Trade,
                Quantity = Convert.ToInt32(tick.Volume)
            });
        }

        private IEnumerable<BaseData> GetHistorySecond(HistoryRequest request, DateTime start, DateTime end)
        {
            var symbol = request.Symbol;
            var requestedBarSpan = request.Resolution.ToTimeSpan();

            // aggregate ticks into 1 second bars
            var result = GetHistoryTick(request, start, end)
                .OfType<Tick>()
                .GroupBy(x => x.Time.RoundDown(requestedBarSpan))
                .Select(g => new TradeBar(
                    g.Key,
                    symbol,
                    g.First().Price,
                    g.Max(t => t.Price),
                    g.Min(t => t.Price),
                    g.Last().Price,
                    g.Sum(t => t.Quantity),
                    requestedBarSpan));

            return result;
        }

        private IEnumerable<BaseData> GetHistoryMinute(HistoryRequest request, DateTime start, DateTime end)
        {
            var symbol = request.Symbol;
            var exchangeTz = request.ExchangeHours.TimeZone;
            var requestedBarSpan = request.Resolution.ToTimeSpan();
            var history = GetTimeSeries(symbol, start, end, TradierTimeSeriesIntervals.OneMinute);

            if (history == null)
            {
                return Enumerable.Empty<BaseData>();
            }

            return history.Select(bar => new TradeBar(bar.Time, symbol, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, requestedBarSpan));
        }

        private IEnumerable<BaseData> GetHistoryHour(HistoryRequest request, DateTime start, DateTime end)
        {
            var symbol = request.Symbol;
            var exchangeTz = request.ExchangeHours.TimeZone;
            var requestedBarSpan = request.Resolution.ToTimeSpan();
            var history = GetTimeSeries(symbol, start, end, TradierTimeSeriesIntervals.FifteenMinutes);

            if (history == null)
            {
                return Enumerable.Empty<BaseData>();
            }

            var tradierBarSpan = TimeSpan.FromMinutes(15);
            var result = history.Select(bar => new TradeBar(bar.Time, symbol, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, tradierBarSpan));

            // aggregate 15 minute bars into hourly bars
            return LeanData.AggregateTradeBars(result, symbol, requestedBarSpan);
        }

        private IEnumerable<BaseData> GetHistoryDaily(HistoryRequest request, DateTime start, DateTime end)
        {
            var symbol = request.Symbol;
            var exchangeTz = request.ExchangeHours.TimeZone;
            var requestedBarSpan = request.Resolution.ToTimeSpan();
            var history = GetHistoricalData(symbol, start, end);

            if (history == null)
            {
                return Enumerable.Empty<BaseData>();
            }
            return history.Select(bar => new TradeBar(bar.Time, symbol, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, requestedBarSpan));
        }
    }
}
