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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Api;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.TimeInForces;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;
using QuantConnect.Util;
using RestSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using QuantConnect.Brokerages.CrossZero;
using QuantConnect.Brokerages.Services;
using System.Net.NetworkInformation;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuantConnect.Brokerages.Tradier
{
    /// <summary>
    /// Tradier Class:
    ///  - Handle authentication.
    ///  - Data requests.
    ///  - Rate limiting.
    ///  - Placing orders.
    ///  - Getting user data.
    /// </summary>
    [BrokerageFactory(typeof(TradierBrokerageFactory))]
    public partial class TradierBrokerage : BaseWebsocketsBrokerage, IDataQueueHandler, IDataQueueUniverseProvider
    {
        // Pre/Post market sessions: https://documentation.tradier.com/brokerage-api/trading/getting-started
        // See section "Pre/Post Market Sessions"
        private static readonly MarketHoursSegment PreMarketSession = new MarketHoursSegment(
            MarketHoursState.PreMarket,
            new TimeSpan(4, 0, 0),
            new TimeSpan(9, 24, 0));
        private static readonly MarketHoursSegment PostMarketSession = new MarketHoursSegment(
            MarketHoursState.PostMarket,
            new TimeSpan(16, 0, 0),
            new TimeSpan(19, 55, 0));

        private bool _useSandbox;
        private string _accountId;

        // we're reusing the equity exchange here to grab typical exchange hours
        private static readonly EquityExchange Exchange =
            new EquityExchange(MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, null, SecurityType.Equity));

        private readonly SymbolPropertiesDatabase _symbolPropertiesDatabase = SymbolPropertiesDatabase.FromDataFolder();
        private TradierSymbolMapper _symbolMapper;

        private string _previousResponseRaw = "";
        private readonly object _lockAccessCredentials = new object();

        //Tradier Spec:
        private Dictionary<TradierApiRequestType, RateGate> _rateLimitNextRequest;

        private IAlgorithm _algorithm;
        private IOrderProvider _orderProvider;
        private ISecurityProvider _securityProvider;
        private IDataAggregator _aggregator;

        // we will send subscription requests in batches
        private Thread _subscribeThead;
        private readonly ManualResetEvent _subscribeProcedure = new(false);
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        // what the first leg of an in-flight cross-zero order filled: keyed by the Lean order id between
        // the second leg's request and its watch, then by the second leg's brokerage id for the reads
        private readonly ConcurrentDictionary<int, decimal> _crossZeroFirstLegQuantityByLeanOrderId = new();
        private readonly ConcurrentDictionary<string, decimal> _crossZeroFirstLegQuantityByBrokerageId = new();

        // the polling service reports fills with a zero fee, so the plugin adds the fee to the first fill of each order
        private readonly FixedSizeHashQueue<int> _feeEmittedLeanOrderIds = new FixedSizeHashQueue<int>(10000);

        private readonly FixedSizeHashQueue<int> _cancelledQcOrderIDs = new FixedSizeHashQueue<int>(10000);
        private string _restApiUrl = "https://api.tradier.com/v1/";
        private string _restApiSandboxUrl = "https://sandbox.tradier.com/v1/";

        /// <summary>
        /// Returns the brokerage account's base currency
        /// </summary>
        public override string AccountBaseCurrency => Currencies.USD;

        /// <summary>
        /// Enables or disables concurrent processing of messages to and from the brokerage.
        /// </summary>
        public override bool ConcurrencyEnabled => true;

        /// <summary>
        /// Create a new Tradier Object:
        /// </summary>
        public TradierBrokerage() : base("Tradier Brokerage")
        {
        }

        /// <summary>
        /// Create a new Tradier Object:
        /// </summary>
        public TradierBrokerage(
            IAlgorithm algorithm,
            IOrderProvider orderProvider,
            ISecurityProvider securityProvider,
            IDataAggregator aggregator,
            bool useSandbox,
            string accountId,
            string accessToken)
            : base("Tradier Brokerage")
        {
            Initialize(
                wssUrl: WebSocketUrl,
                accountId: accountId,
                accessToken: accessToken,
                useSandbox: useSandbox,
                algorithm: algorithm,
                orderProvider: orderProvider,
                securityProvider: securityProvider,
                aggregator: aggregator
            );
        }

        #region Tradier client implementation

        /// <summary>
        /// Execute a authenticated call:
        /// </summary>
        private T Execute<T>(RestRequest request, TradierApiRequestType type, string rootName = "", int attempts = 0, int max = 10) where T : new()
        {
            var response = default(T);

            var method = "TradierBrokerage.Execute." + request.Resource;
            var parameters = request.Parameters.Select(x => x.Name + ": " + x.Value);

            if (attempts != 0)
            {
                Log.Trace(method + "(): Begin attempt " + attempts);
            }

            lock (_lockAccessCredentials)
            {
                //Wait for the API rate limiting
                _rateLimitNextRequest[type].WaitToProceed();

                //Send the request:
                var raw = RestClient.Execute(request);
                _previousResponseRaw = raw.Content;

                if (!raw.IsSuccessful)
                {
                    Log.Error($"TradierBrokerage.Execute(1): {request.Method} {RestClient.BuildUri(request)} failed. " +
                        $"Status: {raw.StatusCode} ({raw.ResponseStatus}). " +
                        $"Parameters: {string.Join(", ", parameters)}. " +
                        $"Error: {raw.ErrorMessage}. " +
                        $"Response: {raw.Content}");

                    // fault errors, e.g. {"fault":{"faultstring":"Datastore Error","detail":{...}}}
                    if (raw.Content.Contains("\"fault\""))
                    {
                        var fault = JsonConvert.DeserializeObject<TradierFaultContainer>(raw.Content);
                        var description = fault?.Fault?.Description ?? raw.Content;

                        // fail fast only on non-retryable authentication faults (e.g. "Invalid Access Token");
                        // transient backend faults (e.g. "Datastore Error") fall through to the retry logic below
                        if (raw.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                            || description.Contains("Access Token", StringComparison.OrdinalIgnoreCase))
                        {
                            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "TradierFault", description));

                            return default(T);
                        }
                    }

                    // this happens when we try to cancel a filled or cancelled order
                    if (raw.Content.Contains("order already in finalized state:"))
                    {
                        if (request.Method == Method.DELETE)
                        {
                            var orderId = raw.ResponseUri.Segments.LastOrDefault() ?? "[unknown]";

                            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "OrderAlreadyFilledOrCancelled",
                                "Unable to cancel the order because it has already been filled or cancelled. TradierOrderId: " + orderId
                            ));
                        }
                        return default(T);
                    }

                    // this happens when we placing a pre/post market limit order outsite the actual pre/post market segments.
                    // e.g.: Invalid parameter, duration: pre market no longer available
                    if (raw.Content.Contains("Invalid parameter, duration:"))
                    {
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotExtendedMarketSegment",
                            $"Unable to place Pre/Post market hours order outside a Pre/Post market segment: {raw.Content}"
                        ));
                        return default(T);
                    }

                    // this happens when a request for historical data should return an empty response
                    if (type == TradierApiRequestType.Data && rootName == "series")
                    {
                        return new T();
                    }

                    if (attempts++ < max)
                    {
                        Log.Trace(method + "(2): Attempting again...");
                        // this will retry on time outs and other transport exception
                        Thread.Sleep(3000);
                        return Execute<T>(request, type, rootName, attempts, max);
                    }
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, raw.StatusCode.ToStringInvariant(), raw.Content));

                    return default(T);
                }

                try
                {
                    if (!string.IsNullOrEmpty(rootName))
                    {
                        if (TryDeserializeRemoveRoot(raw.Content, rootName, out response))
                        {
                            // if we are able to successfully deserialize the rootName, even if null, return it. For example if there is no historical data
                            // tradier will just return success response with null value in 'rootName' and we don't want to retry & sleep because of it
                            return response;
                        }
                    }
                    else
                    {
                        response = JsonConvert.DeserializeObject<T>(raw.Content);
                    }
                }
                catch (Exception e)
                {
                    // a transiently malformed body (e.g. an HTML maintenance/error page served instead of JSON) can fail
                    // deserialization; treat it like the other transient failures and retry before raising a fatal error
                    Log.Error($"{method}(JsonError): Parameters: {string.Join(",", parameters)} Response: {raw.Content} Error: {e.Message}");
                    if (attempts++ < max)
                    {
                        Log.Trace(method + "(JsonError): Attempting again...");
                        Thread.Sleep(3000);
                        return Execute<T>(request, type, rootName, attempts, max);
                    }
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "JsonError", $"Error deserializing message: {raw.Content} Error: {e.Message}"));
                }

                if (raw.ErrorException != null)
                {
                    if (attempts++ < max)
                    {
                        Log.Trace(method + "(3): Attempting again...");
                        // this will retry on time outs and other transport exception
                        Thread.Sleep(3000);
                        return Execute<T>(request, type, rootName, attempts, max);
                    }

                    Log.Trace(method + "(3): Parameters: " + string.Join(",", parameters));
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, raw.ErrorException.GetType().Name, raw.ErrorException.ToString()));

                    const string message = "Error retrieving response.  Check inner details for more info.";
                    throw new ApplicationException(message, raw.ErrorException);
                }
            }

            if (response == null)
            {
                if (attempts++ < max)
                {
                    Log.Trace(method + "(4): Attempting again...");
                    // this will retry on time outs and other transport exception
                    Thread.Sleep(3000);
                    return Execute<T>(request, type, rootName, attempts, max);
                }

                Log.Trace(method + "(4): Parameters: " + string.Join(",", parameters));
                Log.Error(method + "(4): NULL Response: Raw Response: " + _previousResponseRaw);
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "NullResponse", _previousResponseRaw));
            }

            return response;
        }

        /// <summary>
        /// Using this auth token get the tradier user:
        /// </summary>
        /// <remarks>
        /// Returns null if the request was unsucessful
        /// </remarks>
        /// <returns>Tradier user model:</returns>
        public TradierUser GetUserProfile()
        {
            var request = new RestRequest("user/profile", Method.GET);
            var userContainer = Execute<TradierUserContainer>(request, TradierApiRequestType.Standard);
            return userContainer.Profile;
        }

        /// <summary>
        /// Get all the users balance information:
        /// </summary>
        /// <remarks>
        /// Returns null if the request was unsucessful
        /// </remarks>
        /// <returns>Balance</returns>
        public TradierBalanceDetails GetBalanceDetails()
        {
            var request = new RestRequest($"accounts/{_accountId}/balances", Method.GET);
            var balContainer = Execute<TradierBalance>(request, TradierApiRequestType.Standard);

            return balContainer?.Balances;
        }

        /// <summary>
        /// Get a list of the tradier positions for this account:
        /// </summary>
        /// <remarks>
        /// Returns null if the request was unsucessful
        /// </remarks>
        /// <returns>Array of the symbols we hold.</returns>
        public List<TradierPosition> GetPositions()
        {
            var request = new RestRequest($"accounts/{_accountId}/positions", Method.GET);
            var positionContainer = Execute<TradierPositionsContainer>(request, TradierApiRequestType.Standard);

            if (positionContainer?.TradierPositions?.Positions == null)
            {
                // we had a successful call but there weren't any positions
                Log.Trace("Tradier.Positions(): No positions found");
                return new List<TradierPosition>();
            }

            return positionContainer.TradierPositions.Positions;
        }

        /// <summary>
        /// Get a list of historical events for this account:
        /// </summary>
        /// <remarks>
        /// Returns null if the request was unsucessful
        /// </remarks>
        public List<TradierEvent> GetAccountEvents()
        {
            var request = new RestRequest($"accounts/{_accountId}/history", Method.GET);

            var eventContainer = Execute<TradierEventContainer>(request, TradierApiRequestType.Standard);

            if (eventContainer.TradierEvents?.Events == null)
            {
                // we had a successful call but there weren't any events
                Log.Trace("Tradier.GetAccountEvents(): No events found");
                return new List<TradierEvent>();
            }

            return eventContainer.TradierEvents.Events;
        }

        /// <summary>
        /// GainLoss of recent trades for this account:
        /// </summary>
        public List<TradierGainLoss> GetGainLoss()
        {
            var request = new RestRequest($"accounts/{_accountId}/gainloss");

            var gainLossContainer = Execute<TradierGainLossContainer>(request, TradierApiRequestType.Standard);

            if (gainLossContainer.GainLossClosed?.ClosedPositions == null)
            {
                // we had a successful call but there weren't any records returned
                Log.Trace("Tradier.GetGainLoss(): No gain loss found");
                return new List<TradierGainLoss>();
            }

            return gainLossContainer.GainLossClosed.ClosedPositions;
        }

        /// <summary>
        /// Get Intraday and pending orders for users account: accounts/{account_id}/orders
        /// </summary>
        private List<TradierOrder> GetIntradayAndPendingOrders()
        {
            var request = new RestRequest($"accounts/{_accountId}/orders");
            var ordersContainer = Execute<TradierOrdersContainer>(request, TradierApiRequestType.Standard);

            if (ordersContainer?.Orders == null)
            {
                // we had a successful call but there weren't any orders returned
                Log.Trace("Tradier.FetchOrders(): No orders found");
                return new List<TradierOrder>();
            }

            return ordersContainer.Orders.Orders;
        }

        /// <summary>
        /// Get information about a specific order: accounts/{account_id}/orders/{id}
        /// </summary>
        public TradierOrderDetailed GetOrder(long orderId)
        {
            var request = new RestRequest($"accounts/{_accountId}/orders/" + orderId);
            var detailsParent = Execute<TradierOrderDetailedContainer>(request, TradierApiRequestType.Standard);
            if (detailsParent?.DetailedOrder == null)
            {
                Log.Error("Tradier.GetOrder(): Null response.");
                return new TradierOrderDetailed();
            }

            return detailsParent.DetailedOrder;
        }

        /// <summary>
        /// Place Order through API.
        /// accounts/{account-id}/orders
        /// </summary>
        private TradierOrderResponse PlaceOrder(
            TradierOrderClass classification,
            TradierOrderDirection direction,
            string symbol,
            decimal quantity,
            decimal price = 0,
            decimal stop = 0,
            string optionSymbol = "",
            TradierOrderType type = TradierOrderType.Market,
            TradierOrderDuration duration = TradierOrderDuration.GTC)
        {
            //Compose the request:
            var request = new RestRequest($"accounts/{_accountId}/orders");

            //Add data:
            request.AddParameter("class", GetEnumDescription(classification));
            request.AddParameter("symbol", symbol);
            request.AddParameter("duration", GetEnumDescription(duration));
            request.AddParameter("type", GetEnumDescription(type));
            request.AddParameter("quantity", quantity);
            request.AddParameter("side", GetEnumDescription(direction));

            //Add optionals:
            if (price > 0) request.AddParameter("price", Math.Round(price, 2));
            if (stop > 0) request.AddParameter("stop", Math.Round(stop, 2));
            if (!string.IsNullOrWhiteSpace(optionSymbol)) request.AddParameter("option_symbol", optionSymbol);

            //Set Method:
            request.Method = Method.POST;

            return Execute<TradierOrderResponse>(request, TradierApiRequestType.Orders);
        }

        /// <summary>
        /// Update an exiting Tradier Order:
        /// </summary>
        public TradierOrderResponse ChangeOrder(
            long orderId,
            TradierOrderType type = TradierOrderType.Market,
            TradierOrderDuration duration = TradierOrderDuration.GTC,
            decimal price = 0,
            decimal stop = 0)
        {
            //Create Request:
            var request = new RestRequest($"accounts/{_accountId}/orders/{orderId}")
            {
                Method = Method.PUT
            };

            //Add Data:
            request.AddParameter("type", GetEnumDescription(type));
            request.AddParameter("duration", GetEnumDescription(duration));
            if (price != 0) request.AddParameter("price", Math.Round(price, 2).ToString(CultureInfo.InvariantCulture));
            if (stop != 0) request.AddParameter("stop", Math.Round(stop, 2).ToString(CultureInfo.InvariantCulture));

            //Send:
            return Execute<TradierOrderResponse>(request, TradierApiRequestType.Orders);
        }

        /// <summary>
        /// Cancel the order with this account and id number
        /// </summary>
        public TradierOrderResponse CancelOrder(long orderId)
        {
            //Compose Request:
            var request = new RestRequest($"accounts/{_accountId}/orders/{orderId}")
            {
                Method = Method.DELETE
            };

            //Transmit Request:
            return Execute<TradierOrderResponse>(request, TradierApiRequestType.Orders);
        }

        /// <summary>
        /// List of quotes for symbols
        /// </summary>
        public List<TradierQuote> GetQuotes(List<string> symbols)
        {
            if (symbols.Count == 0)
            {
                return new List<TradierQuote>();
            }

            //Send Request:
            var request = new RestRequest("markets/quotes", Method.GET);
            var csvSymbols = string.Join(",", symbols);
            request.AddParameter("symbols", csvSymbols, ParameterType.QueryString);

            var dataContainer = Execute<TradierQuoteContainer>(request, TradierApiRequestType.Data, "quotes");
            // can return null quotes and not really be failing for cases where the provided symbols do not match
            return dataContainer?.Quotes ?? new List<TradierQuote>();
        }

        /// <summary>
        /// Gets the underlying asset for the specified brokerage option symbol.
        /// </summary>
        /// <param name="brokerageSymbol">The brokerage option symbol</param>
        /// <returns>The underlying asset symbol, or null if not found</returns>
        private string GetUnderlyingAssetByBrokerageSymbol(string brokerageSymbol)
        {
            var quotes = GetQuotes(new List<string> { brokerageSymbol });
            var quote = quotes?.FirstOrDefault();
            return quote?.Options_UnderlyingAsset;
        }

        /// <summary>
        /// Get the historical bars for this period
        /// </summary>
        private IEnumerable<TradierTimeSeries> GetTimeSeries(HistoryRequest historyRequest, DateTime start, DateTime end, TradierTimeSeriesIntervals interval)
        {
            // Create and send request, take into account tradier limitations, else we get an error like:
            // 'Invalid parameter, start: must be on or after 2024-01-15 00:00:00.'
            // ref https://documentation.tradier.com/brokerage-api/markets/get-timesales
            /*
Interval	Data Available (Open)	Data Available (All)
    tick	5 days					N/A
    1min	20 days					10 days
    5min	40 days					18 days
    15min	40 days					18 days
             */
            TimeSpan maximumTimeAgo;
            if (interval == TradierTimeSeriesIntervals.FifteenMinutes || interval == TradierTimeSeriesIntervals.FiveMinutes)
            {
                maximumTimeAgo = TimeSpan.FromDays(40);
            }
            else if (interval == TradierTimeSeriesIntervals.OneMinute)
            {
                maximumTimeAgo = TimeSpan.FromDays(20);
            }
            else if (interval == TradierTimeSeriesIntervals.Tick)
            {
                maximumTimeAgo = TimeSpan.FromDays(5);
            }
            else
            {
                throw new ArgumentException($"Invalid TradierTimeSeriesIntervals value: {interval}");
            }

            var nyCurrentTime = DateTime.UtcNow.ConvertFromUtc(TimeZones.NewYork);
            if (nyCurrentTime - start > maximumTimeAgo)
            {
                if (!_loggedInvalidStartTimeForHistory)
                {
                    _loggedInvalidStartTimeForHistory = true;
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidStartTime", "Warning: Adjusting history request start time to fit Tradier limitations"));
                }

                start = nyCurrentTime.Add(-maximumTimeAgo);
                if (start > end)
                {
                    yield break;
                }
            }

            var requestEnd = end;
            var requestStart = start;

            // we ask tick data in chunks, if not tradier API blows up
            if (interval == TradierTimeSeriesIntervals.Tick && requestEnd > (requestStart + Time.OneHour))
            {
                requestEnd = requestStart + Time.OneHour;
            }

            var ticker = _symbolMapper.GetBrokerageSymbol(historyRequest.Symbol);
            do
            {
                if (historyRequest.ExchangeHours.IsOpen(requestStart, requestEnd, historyRequest.IncludeExtendedMarketHours))
                {
                    var request = new RestRequest("markets/timesales", Method.GET);
                    request.AddParameter("symbol", ticker, ParameterType.QueryString);
                    request.AddParameter("interval", GetEnumDescription(interval), ParameterType.QueryString);
                    request.AddParameter("start", requestStart.ToStringInvariant("yyyy-MM-dd HH:mm"), ParameterType.QueryString);
                    request.AddParameter("end", requestEnd.ToStringInvariant("yyyy-MM-dd HH:mm"), ParameterType.QueryString);
                    request.AddParameter("session_filter", historyRequest.IncludeExtendedMarketHours ? "all" : "open", ParameterType.QueryString);
                    var dataContainer = Execute<TradierTimeSeriesContainer>(request, TradierApiRequestType.Data, "series");

                    // there could be no data the requested symbol and time, tradier will return null
                    foreach (var point in dataContainer?.TimeSeries ?? Enumerable.Empty<TradierTimeSeries>())
                    {
                        yield return point;
                    }
                }

                requestStart += Time.OneHour;
                requestEnd += Time.OneHour;
            }
            while (requestEnd < end);
        }

        /// <summary>
        /// Get full daily, weekly or monthly bars of historical periods:
        /// </summary>
        private List<TradierHistoryBar> GetHistoricalData(Symbol symbol,
            DateTime start,
            DateTime end,
            TradierHistoricalDataIntervals interval = TradierHistoricalDataIntervals.Daily)
        {
            // Create and send request
            var ticker = _symbolMapper.GetBrokerageSymbol(symbol);
            var request = new RestRequest("markets/history", Method.GET);
            request.AddParameter("symbol", ticker, ParameterType.QueryString);
            request.AddParameter("start", start.ToStringInvariant("yyyy-MM-dd"), ParameterType.QueryString);
            request.AddParameter("end", end.ToStringInvariant("yyyy-MM-dd"), ParameterType.QueryString);
            request.AddParameter("interval", GetEnumDescription(interval));
            var dataContainer = Execute<TradierHistoryDataContainer>(request, TradierApiRequestType.Data, "history");

            // there could be no data the requested symbol and time, tradier will return null
            return dataContainer?.Data ?? new List<TradierHistoryBar>();
        }

        /// <summary>
        /// Get the current market status
        /// </summary>
        public TradierMarketStatus GetMarketStatus()
        {
            var request = new RestRequest("markets/clock", Method.GET);
            return Execute<TradierMarketStatus>(request, TradierApiRequestType.Data, "clock");
        }

        /// <summary>
        /// Get the list of days status for this calendar month, year:
        /// </summary>
        public List<TradierCalendarDay> GetMarketCalendar(int month, int year)
        {
            var request = new RestRequest("markets/calendar", Method.GET);
            request.AddParameter("month", month.ToStringInvariant());
            request.AddParameter("year", year.ToStringInvariant());
            var calendarContainer = Execute<TradierCalendarStatus>(request, TradierApiRequestType.Data, "calendar");
            return calendarContainer.Days.Days;
        }

        /// <summary>
        /// Get the list of days status for this calendar month, year:
        /// </summary>
        public List<TradierSearchResult> Search(string query, bool includeIndexes = true)
        {
            var request = new RestRequest("markets/search", Method.GET);
            request.AddParameter("q", query);
            request.AddParameter("indexes", includeIndexes.ToStringInvariant());
            var searchContainer = Execute<TradierSearchContainer>(request, TradierApiRequestType.Data, "securities");
            return searchContainer.Results;
        }

        /// <summary>
        /// Get the list of days status for this calendar month, year:
        /// </summary>
        public List<TradierSearchResult> LookUpSymbol(string query, bool includeIndexes = true)
        {
            var request = new RestRequest("markets/lookup", Method.GET);
            request.AddParameter("q", query);
            request.AddParameter("indexes", includeIndexes.ToStringInvariant());
            var searchContainer = Execute<TradierSearchContainer>(request, TradierApiRequestType.Data, "securities");
            return searchContainer.Results;
        }

        /// <summary>
        /// Get all options symbols for the given underlying.
        /// </summary>
        /// <param name="underlying">Underlying symbol of the chain</param>
        /// <returns>Options lookup results</returns>
        private TradierOptionsLookupResult GetOptionsLookup(string underlying)
        {
            var request = new RestRequest("markets/options/lookup", Method.GET);
            request.AddParameter("underlying", underlying, ParameterType.QueryString);
            var optionsContainer = Execute<List<TradierOptionsLookupResult>>(request, TradierApiRequestType.Data, "symbols");
            return optionsContainer != null ? optionsContainer.FirstOrDefault() : new TradierOptionsLookupResult();
        }

        /// <summary>
        /// Convert the C# Enums back to the Tradier API Equivalent:
        /// </summary>
        private string GetEnumDescription(Enum value)
        {
            // Get the Description attribute value for the enum value
            var fi = value.GetType().GetField(value.ToString());
            var attributes = (EnumMemberAttribute[])fi.GetCustomAttributes(typeof(EnumMemberAttribute), false);

            if (attributes.Length > 0)
            {
                return attributes[0].Value;
            }
            else
            {
                return value.ToString();
            }
        }

        /// <summary>
        /// Get the rype inside the nested root:
        /// </summary>
        private bool TryDeserializeRemoveRoot<T>(string json, string rootName, out T obj)
        {
            obj = default;
            var success = false;

            try
            {
                //Dynamic deserialization:
                dynamic dynDeserialized = JsonConvert.DeserializeObject(json);
                obj = JsonConvert.DeserializeObject<T>(dynDeserialized[rootName].ToString());

                // if we arrieved here without exploding it's a success even if obj is null, because that's what we got back
                success = true;
            }
            catch (Exception err)
            {
                Log.Error(err, "RootName: " + rootName);
            }

            return success;
        }

        #endregion Tradier client implementation

        #region IBrokerage implementation

        /// <summary>
        /// Returns true if we're currently connected to the broker
        /// </summary>
        public override bool IsConnected => WebSocket.IsOpen;

        /// <summary>
        /// Gets all open orders on the account.
        /// NOTE: The order objects returned do not have QC order IDs.
        /// </summary>
        /// <returns>The open orders returned from IB</returns>
        public override List<Order> GetOpenOrders()
        {
            var orders = new List<Order>();
            var openOrders = GetIntradayAndPendingOrders().Where(OrderIsOpen);

            foreach (var openOrder in openOrders)
            {
                // watch the adopted order seeded with its current state, so the poll reports only what changes from here on
                OrderPollingService.Watch(openOrder.Id.ToStringInvariant(), ToOrderState(openOrder));
                orders.Add(ConvertOrder(openOrder));
            }

            return orders;
        }

        /// <summary>
        /// Gets all holdings for the account
        /// </summary>
        /// <returns>The current holdings from the account</returns>
        public override List<Holding> GetAccountHoldings()
        {
            var holdings = GetPositions().Select(ConvertHolding).Where(x => x.Quantity != 0).ToList();
            var tickers = holdings.Select(x => _symbolMapper.GetBrokerageSymbol(x.Symbol)).ToList();

            var quotes = GetQuotes(tickers).ToDictionary(x => x.Symbol);
            foreach (var holding in holdings)
            {
                var ticker = _symbolMapper.GetBrokerageSymbol(holding.Symbol);

                TradierQuote quote;
                if (quotes.TryGetValue(ticker, out quote))
                {
                    holding.MarketPrice = quote.Last;
                }
            }
            return holdings;
        }

        /// <summary>
        /// Gets the current cash balance for each currency held in the brokerage account
        /// </summary>
        /// <returns>The current cash balance for each currency available for trading</returns>
        public override List<CashAmount> GetCashBalance()
        {
            var balanceDetails = GetBalanceDetails();
            if (balanceDetails == null)
            {
                return new List<CashAmount>();
            }

            return new List<CashAmount>
            {
                new CashAmount(balanceDetails.TotalCash, Currencies.USD)
            };
        }

        /// <summary>
        /// Places a new order and assigns a new broker ID to the order
        /// </summary>
        /// <param name="order">The order to be placed</param>
        /// <returns>True if the request for a new order has been placed, false otherwise</returns>
        public override bool PlaceOrder(Order order)
        {
            Log.Trace("TradierBrokerage.PlaceOrder(): " + order);

            if (_cancelledQcOrderIDs.Contains(order.Id))
            {
                Log.Trace("TradierBrokerage.PlaceOrder(): Cancelled Order: " + order.Id + " - " + order);
                return false;
            }

            // before doing anything, verify only one outstanding order per symbol
            var openOrder = _orderProvider?.GetOpenOrders(open => open.Id != order.Id && open.Symbol == order.Symbol).FirstOrDefault();
            if (openOrder != null)
            {
                // let the world know what we're doing
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "OneOrderPerSymbol",
                    "Tradier Brokerage currently only supports one outstanding order per symbol. Canceled old order: " + openOrder.Id)
                    );

                // don't worry about the response here, if it couldn't be canceled it was
                // more than likely already filled, either way we'll trust we're clean to proceed
                // with this new order
                CancelOrder(openOrder);
            }

            var holdingQuantity = _securityProvider.GetHoldingsQuantity(order.Symbol);

            var isPlaceCrossOrder = TryCrossZeroPositionOrder(order, holdingQuantity);

            if (isPlaceCrossOrder == null)
            {
                var orderRequest = new TradierPlaceOrderRequest(order, order.Quantity, ConvertSecurityType(order.SecurityType), holdingQuantity, order.Type, _symbolMapper, _securityProvider);
                var response = TradierPlaceOrder(orderRequest);
                if (response == null || !response.Errors.Errors.IsNullOrEmpty())
                {
                    return false;
                }
                return true;
            }
            return isPlaceCrossOrder.Value;
        }

        /// <summary>
        /// Places an order that crosses zero (transitions from a short position to a long position or vice versa) and returns the response.
        /// This method implements brokerage-specific logic for placing such orders using Tradier brokerage.
        /// </summary>
        /// <param name="crossZeroOrderRequest">The request object containing details of the cross zero order to be placed.</param>
        /// <param name="isPlaceOrderWithLeanEvent">
        /// A boolean indicating whether the order should be placed with triggering a Lean event.
        /// Default is <c>true</c>, meaning Lean events will be triggered.
        /// </param>
        /// <returns>
        /// A <see cref="CrossZeroOrderResponse"/> object indicating the result of the order placement.
        /// </returns>
        protected override CrossZeroOrderResponse PlaceCrossZeroOrder(CrossZeroFirstOrderRequest crossZeroOrderRequest, bool isPlaceOrderWithLeanEvent)
        {
            var orderRequest = new TradierPlaceOrderRequest(crossZeroOrderRequest.LeanOrder, crossZeroOrderRequest.OrderQuantity, ConvertSecurityType(crossZeroOrderRequest.LeanOrder.SecurityType), crossZeroOrderRequest.OrderQuantityHolding, crossZeroOrderRequest.OrderType, _symbolMapper, _securityProvider);

            if (crossZeroOrderRequest is CrossZeroSecondOrderRequest)
            {
                // the second leg continues the Lean order, so its watch seed must carry what the first
                // leg already filled - the poll then counts the leg's fills on top of it and the last
                // fill closes the whole order
                _crossZeroFirstLegQuantityByLeanOrderId[crossZeroOrderRequest.LeanOrder.Id] =
                    Math.Abs(crossZeroOrderRequest.LeanOrder.Quantity) - Math.Abs(crossZeroOrderRequest.OrderQuantity);
            }

            var response = TradierPlaceOrder(orderRequest, isPlaceOrderWithLeanEvent);
            if (response == null || !response.Errors.Errors.IsNullOrEmpty())
            {
                _crossZeroFirstLegQuantityByLeanOrderId.TryRemove(crossZeroOrderRequest.LeanOrder.Id, out _);
                return new CrossZeroOrderResponse(string.Empty, false);
            }
            return new CrossZeroOrderResponse(response.Order.Id.ToStringInvariant(), true);
        }

        /// <summary>
        /// Updates the order with the same id
        /// </summary>
        /// <param name="order">The new order information</param>
        /// <returns>True if the request was made for the order to be updated, false otherwise</returns>
        public override bool UpdateOrder(Order order)
        {
            Log.Trace("TradierBrokerage.UpdateOrder(): " + order);

            if (!order.BrokerId.Any())
            {
                // we need the brokerage order id in order to perform an update
                Log.Trace("TradierBrokerage.UpdateOrder(): Unable to update order without BrokerId.");
                return false;
            }

            if (!TryGetUpdateCrossZeroOrderQuantity(order, out _))
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, "TradierBrokerage.UpdateOrder(): Unable to modify order quantities."));
                return false;
            }

            // there's only one active tradier order per qc order, and the last brokerage id is the one in flight
            var activeOrderId = Parse.Long(order.BrokerId.Last());

            var orderType = ConvertOrderType(order.Type);
            var orderDuration = GetOrderDuration(order, _securityProvider);
            var limitPrice = GetLimitPrice(order);
            var stopPrice = GetStopPrice(order);
            var response = ChangeOrder(activeOrderId,
                orderType,
                orderDuration,
                limitPrice,
                stopPrice
                );

            if (!response.Errors.Errors.IsNullOrEmpty())
            {
                string errors = string.Join(", ", response.Errors.Errors);
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UpdateFailed", "Failed to update Tradier order id: " + activeOrderId + ". " + errors));
                return false;
            }

            // success
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
            { Status = OrderStatus.UpdateSubmitted });

            return true;
        }

        /// <summary>
        /// Cancels the order with the specified ID
        /// </summary>
        /// <param name="order">The order to cancel</param>
        /// <returns>True if the request was made for the order to be canceled, false otherwise</returns>
        public override bool CancelOrder(Order order)
        {
            Log.Trace("TradierBrokerage.CancelOrder(): " + order);

            if (!order.BrokerId.Any())
            {
                Log.Trace("TradierBrokerage.CancelOrder(): Unable to cancel order without BrokerId.");
                return false;
            }

            // add this id to the cancelled list, this is to prevent resubmits of certain simulated order
            // types, such as market on close
            _cancelledQcOrderIDs.Add(order.Id);

            foreach (var orderID in order.BrokerId)
            {
                var id = Parse.Long(orderID);
                var response = CancelOrder(id);
                if (response == null)
                {
                    // this can happen if the order has already been filled
                    return false;
                }
                if (response.Errors.Errors.IsNullOrEmpty() && response.Order.Status == "ok")
                {
                    // record the cancel as already reported: the id leaves the read list, and a sweep that
                    // read the order just before this cannot report the cancel or its fills a second time
                    OrderPollingService.UpdateOrderState(orderID, new BrokerOrderState
                    {
                        BrokerageOrderId = orderID,
                        Status = OrderStatus.Canceled,
                        TimeUtc = DateTime.UtcNow
                    });
                    _crossZeroFirstLegQuantityByBrokerageId.TryRemove(orderID, out _);
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "Tradier Order Event")
                    { Status = OrderStatus.Canceled });
                }
            }

            return true;
        }

        /// <summary>
        /// Disconnects the client from the broker's remote servers
        /// </summary>
        public override void Disconnect()
        {
            if (WebSocket != null && WebSocket.IsOpen)
            {
                WebSocket.Close();
            }

            OrderPollingService?.Stop();
        }

        /// <summary>
        /// Dispose of the brokerage instance. The base class disposes the order polling service.
        /// </summary>
        public override void Dispose()
        {
            _subscribeThead.StopSafely(TimeSpan.FromSeconds(5), _cancellationTokenSource);
            base.Dispose();
        }

        /// <summary>
        /// Event invocator for the Message event
        /// </summary>
        /// <param name="e">The error</param>
        protected override void OnMessage(BrokerageMessageEvent e)
        {
            var message = e;
            if (Exchange.DateTimeIsOpen(DateTime.Now) && ErrorsDuringMarketHours.Contains(e.Code))
            {
                // elevate this to an error
                message = new BrokerageMessageEvent(BrokerageMessageType.Error, e.Code, e.Message);
            }
            base.OnMessage(message);
        }

        /// <summary>
        /// Places an order using the Tradier brokerage and returns the response.
        /// </summary>
        /// <param name="order">The request object containing details of the order to be placed.</param>
        /// <param name="isSubmittedEvent">
        /// A boolean indicating whether a submitted event should be triggered.
        /// Default is <c>true</c>, meaning the submitted event will be triggered.
        /// </param>
        /// <returns>
        /// A <see cref="TradierOrderResponse"/> object indicating the result of the order placement.
        /// </returns>
        private TradierOrderResponse TradierPlaceOrder(TradierPlaceOrderRequest order, bool isSubmittedEvent = true)
        {
            string stopLimit = string.Empty;
            if (order.Price != 0 || order.Stop != 0)
            {
                var stopStr = order.Stop == 0 ? "" : $" stop {order.Stop.ToStringInvariant()}";
                var limitStr = order.Price == 0 ? "" : $" limit {order.Price.ToStringInvariant()}";
                stopLimit = $" at{stopStr}{limitStr}";
            }

            Log.Trace($"TradierBrokerage.TradierPlaceOrder(): {order.Type} to {order.Direction} " +
                $"{order.Quantity.ToStringInvariant()} units of {order.Symbol}{stopLimit}"
            );

            var response = PlaceOrder(
                order.Classification,
                order.Direction,
                order.Symbol,
                order.Quantity,
                order.Price,
                order.Stop,
                order.OptionSymbol,
                order.Type,
                order.Duration
                );

            // if no errors, add to our open orders collection
            if (response != null && response.Errors.Errors.IsNullOrEmpty())
            {
                Log.Trace($"TradierBrokerage.TradierPlaceOrder(): order submitted successfully: {response.Order.Id}");

                order.QCOrder.BrokerId.Add(response.Order.Id.ToStringInvariant());

                if (isSubmittedEvent)
                {
                    // If this is not a cross order, send the submitted event to Lean.
                    // For cross orders, we should not send the submitted event to Lean as they are handled differently.
                    OnOrderEvent(new OrderEvent(order.QCOrder, DateTime.UtcNow, OrderFee.Zero) { Status = OrderStatus.Submitted });
                }

                // watch the order before returning, so it's guaranteed to be in the poll registry when we poll for
                // updates; the seed says the submit was already sent to Lean, so the poll never repeats it
                var seedState = new BrokerOrderState
                {
                    BrokerageOrderId = response.Order.Id.ToStringInvariant(),
                    Status = OrderStatus.Submitted,
                    TimeUtc = DateTime.UtcNow
                };
                if (_crossZeroFirstLegQuantityByLeanOrderId.TryRemove(order.QCOrder.Id, out var firstLegQuantity))
                {
                    // the second leg of a cross-zero order starts from what the first leg already filled
                    seedState.FilledQuantity = firstLegQuantity;
                    _crossZeroFirstLegQuantityByBrokerageId[seedState.BrokerageOrderId] = firstLegQuantity;
                }
                OrderPollingService.Watch(seedState.BrokerageOrderId, seedState);
            }
            else
            {
                // invalidate the order, bad request
                OnOrderEvent(new OrderEvent(order.QCOrder, DateTime.UtcNow, OrderFee.Zero)
                { Status = OrderStatus.Invalid });

                string message = _previousResponseRaw;
                if (response != null && response.Errors != null && !response.Errors.Errors.IsNullOrEmpty())
                {
                    message = "Order " + order.QCOrder.Id + ": " + string.Join(Environment.NewLine, response.Errors.Errors);
                }

                // send this error through to the console
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "OrderError", message));

                // if we weren't given a broker ID, make an async request to fetch it and set the broker ID property on the qc order
                if (response == null || response.Order == null || response.Order.Id == 0)
                {
                    Task.Run(() =>
                    {
                        var orders = GetIntradayAndPendingOrders()
                            .Where(x => x.Status == TradierOrderStatus.Rejected)
                            .Where(x => DateTime.UtcNow - x.TransactionDate < TimeSpan.FromSeconds(2));

                        var recentOrder = orders.OrderByDescending(x => x.TransactionDate).FirstOrDefault(x => x.Symbol == order.Symbol && x.Quantity == order.Quantity && x.Direction == order.Direction && x.Type == order.Type);
                        if (recentOrder == null)
                        {
                            // some orders (e.g. invalid symbol) can be rejected with an error message only, for these we cannot obtain a Tradier order id
                            Log.Error("TradierBrokerage.TradierPlaceOrder(): Unable to resolve rejected Tradier order id for QC order: " + order.QCOrder.Id);
                            return;
                        }

                        order.QCOrder.BrokerId.Add(recentOrder.Id.ToStringInvariant());
                        Log.Trace("TradierBrokerage.TradierPlaceOrder(): Successfully resolved missing order ID: " + recentOrder.Id);
                    });
                }
            }

            return response;
        }

        /// <summary>
        /// Reads the current state of one watched order for the polling service.
        /// </summary>
        /// <param name="brokerageId">The Tradier order id to read.</param>
        /// <returns>The state of the order, or null when Tradier does not know the id - the read is
        /// simply retried on the next sweep.</returns>
        private BrokerOrderState ReadOrderState(string brokerageId)
        {
            var brokerageOrder = GetOrder(Parse.Long(brokerageId));
            if (brokerageOrder == null || brokerageOrder.Id == 0)
            {
                return null;
            }

            var orderState = ToOrderState(brokerageOrder);
            HandleClosedOrderState(brokerageOrder, orderState);
            return orderState;
        }

        /// <summary>
        /// Converts one Tradier order into the state the shared diff understands.
        /// </summary>
        /// <param name="brokerageOrder">The order read from the Tradier orders endpoint.</param>
        private BrokerOrderState ToOrderState(TradierOrder brokerageOrder)
        {
            var orderState = new BrokerOrderState
            {
                BrokerageOrderId = brokerageOrder.Id.ToStringInvariant(),
                Status = ConvertStatus(brokerageOrder.Status),
                TimeUtc = brokerageOrder.TransactionDate.ToUniversalTime(),
                Message = brokerageOrder.ReasonDescription
            };

            if (brokerageOrder.QuantityExecuted > 0)
            {
                // the second leg of a cross-zero order counts its fills on top of what the first leg
                // filled, so the leg's last fill closes the whole Lean order
                _crossZeroFirstLegQuantityByBrokerageId.TryGetValue(orderState.BrokerageOrderId, out var firstLegQuantity);
                orderState.FilledQuantity = firstLegQuantity + brokerageOrder.QuantityExecuted;
                // the price of the most recent fill: several fills within one sweep collapse to the last
                // price, the same limitation the replaced polling had
                orderState.FillPrice = brokerageOrder.LastFillPrice;
            }

            return orderState;
        }

        /// <summary>
        /// Keeps the cross-zero machinery running from the poll: Tradier reporting the closing leg filled
        /// is the signal to submit the remaining leg. The base helper owns the pending leg and ignores
        /// every other order, so this can run for every closed order a read returns.
        /// </summary>
        /// <param name="brokerageOrder">The order read from the Tradier orders endpoint.</param>
        /// <param name="orderState">The state <see cref="ToOrderState"/> built from it.</param>
        private void HandleClosedOrderState(TradierOrder brokerageOrder, BrokerOrderState orderState)
        {
            if (OrderIsOpen(brokerageOrder))
            {
                return;
            }

            var brokerageId = orderState.BrokerageOrderId;

            // the cross-zero map resolves the leg ids the order provider may not have indexed yet, and
            // forgets them once the leg closed
            var resolvedFromCrossZeroMap = TryGetOrRemoveCrossZeroOrder(brokerageId, orderState.Status, out var leanOrder);
            if (!resolvedFromCrossZeroMap)
            {
                leanOrder = _orderProvider?.GetOrdersByBrokerageId(brokerageId)?.SingleOrDefault();
            }
            if (leanOrder == null)
            {
                return;
            }

            if (orderState.Status != OrderStatus.Filled)
            {
                // a canceled or rejected closing leg: the base helper drops its pending remaining leg,
                // and the service reports the close itself
                TryHandleRemainingCrossZeroOrder(leanOrder, new OrderEvent(leanOrder, orderState.TimeUtc, OrderFee.Zero)
                {
                    Status = orderState.Status
                });
                return;
            }

            var reportedQuantity = OrderPollingService.TryGetLastOrderState(brokerageId, out var lastSeen)
                ? lastSeen.FilledQuantity ?? 0m
                : 0m;
            var newQuantity = (orderState.FilledQuantity ?? 0m) - reportedQuantity;
            if (newQuantity <= 0m)
            {
                // everything is reported; the second leg's fill offset has done its job
                _crossZeroFirstLegQuantityByBrokerageId.TryRemove(brokerageId, out _);
                return;
            }

            var fillEvent = new OrderEvent(leanOrder, orderState.TimeUtc, OrderFee.Zero, "Tradier Fill Event")
            {
                // the base helper reads Filled as "closing leg done": it rewrites the event to
                // PartiallyFilled, reports it, and submits the remaining leg
                Status = OrderStatus.Filled,
                FillPrice = brokerageOrder.LastFillPrice,
                FillQuantity = IsShort(brokerageOrder.Direction) ? -newQuantity : newQuantity
            };
            if (TryHandleRemainingCrossZeroOrder(leanOrder, fillEvent))
            {
                // the helper reported the fill, so tell the service before the sweep's own diff runs -
                // otherwise it would report the same fill again
                OrderPollingService.UpdateOrderState(brokerageId, orderState);
            }
            else if (resolvedFromCrossZeroMap)
            {
                // the order only resolved through the cross-zero map, so the service's own diff may not
                // find it either; report the closing fill here and mark it reported
                OnOrderEvent(fillEvent);
                OrderPollingService.UpdateOrderState(brokerageId, orderState);
            }
        }

        /// <summary>
        /// Attaches the order fee to the first fill of each Lean order. The polling service reports fills
        /// with a zero fee, so the plugin adds it once per order, the way the replaced polling did.
        /// </summary>
        /// <param name="orderEvents">The order events to deliver.</param>
        protected override void OnOrderEvents(List<OrderEvent> orderEvents)
        {
            foreach (var orderEvent in orderEvents)
            {
                if ((orderEvent.Status == OrderStatus.PartiallyFilled || orderEvent.Status == OrderStatus.Filled)
                    && _securityProvider != null)
                {
                    var leanOrder = _orderProvider?.GetOrderById(orderEvent.OrderId);
                    if (leanOrder != null && _feeEmittedLeanOrderIds.Add(orderEvent.OrderId))
                    {
                        var security = _securityProvider.GetSecurity(orderEvent.Symbol);
                        orderEvent.OrderFee = security.FeeModel.GetOrderFee(new OrderFeeParameters(security, leanOrder));
                    }
                }
            }
            base.OnOrderEvents(orderEvents);
        }

        #endregion IBrokerage implementation

        #region Conversion routines

        /// <summary>
        /// Returns true if the specified order is considered open, otherwise false
        /// </summary>
        protected static bool OrderIsOpen(TradierOrder order)
        {
            return order.Status != TradierOrderStatus.Filled
                && order.Status != TradierOrderStatus.Canceled
                && order.Status != TradierOrderStatus.Expired
                && order.Status != TradierOrderStatus.Rejected;
        }

        /// <summary>
        /// Returns true if the specified tradier order direction represents a short position
        /// </summary>
        protected static bool IsShort(TradierOrderDirection direction)
        {
            switch (direction)
            {
                case TradierOrderDirection.Sell:
                case TradierOrderDirection.SellShort:
                case TradierOrderDirection.SellToOpen:
                case TradierOrderDirection.SellToClose:
                    return true;

                case TradierOrderDirection.Buy:
                case TradierOrderDirection.BuyToCover:
                case TradierOrderDirection.BuyToClose:
                case TradierOrderDirection.BuyToOpen:
                case TradierOrderDirection.None:
                    return false;

                default:
                    throw new ArgumentOutOfRangeException("direction", direction, null);
            }
        }

        /// <summary>
        /// Converts the specified tradier order into a qc order.
        /// The 'task' will have a value if we needed to issue a rest call for the stop price, otherwise it will be null
        /// </summary>
        protected Order ConvertOrder(TradierOrder order)
        {
            Order qcOrder;

            var symbol = _symbolMapper.GetLeanSymbol(order.Class == TradierOrderClass.Option ? order.OptionSymbol : order.Symbol);
            var quantity = ConvertQuantity(order);
            var time = order.TransactionDate;
            var properties = new TradierOrderProperties();

            switch (order.Type)
            {
                case TradierOrderType.Limit:
                    qcOrder = new LimitOrder(symbol, quantity, order.Price, time, properties: properties);
                    if (order.Duration == TradierOrderDuration.Pre || order.Duration == TradierOrderDuration.Post)
                    {
                        properties.OutsideRegularTradingHours = true;
                    }
                    break;

                case TradierOrderType.Market:
                    qcOrder = new MarketOrder(symbol, quantity, time, properties: properties);
                    break;

                case TradierOrderType.StopMarket:
                    qcOrder = new StopMarketOrder(symbol, quantity, GetOrder(order.Id).StopPrice, time, properties: properties);
                    break;

                case TradierOrderType.StopLimit:
                    qcOrder = new StopLimitOrder(symbol, quantity, GetOrder(order.Id).StopPrice, order.Price, time, properties: properties);
                    break;

                //case TradierOrderType.Credit:
                //case TradierOrderType.Debit:
                //case TradierOrderType.Even:
                default:
                    throw new NotImplementedException("The Tradier order type " + order.Type + " is not implemented.");
            }

            qcOrder.Status = ConvertStatus(order.Status);
            qcOrder.BrokerId.Add(order.Id.ToStringInvariant());
            //qcOrder.ContingentId =
            qcOrder.Properties.TimeInForce = ConvertTimeInForce(order.Duration);
            return qcOrder;
        }

        /// <summary>
        /// Converts the qc order type into a tradier order type
        /// </summary>
        protected static TradierOrderType ConvertOrderType(OrderType type)
        {
            switch (type)
            {
                case OrderType.Market:
                    return TradierOrderType.Market;

                case OrderType.Limit:
                    return TradierOrderType.Limit;

                case OrderType.StopMarket:
                    return TradierOrderType.StopMarket;

                case OrderType.StopLimit:
                    return TradierOrderType.StopLimit;

                default:
                    throw new ArgumentOutOfRangeException("type", type, null);
            }
        }

        /// <summary>
        /// Converts the tradier order duration into a qc order time in force
        /// </summary>
        private static TimeInForce ConvertTimeInForce(TradierOrderDuration duration)
        {
            switch (duration)
            {
                case TradierOrderDuration.GTC:
                    return TimeInForce.GoodTilCanceled;

                case TradierOrderDuration.Day:
                case TradierOrderDuration.Pre:
                case TradierOrderDuration.Post:
                    return TimeInForce.Day;

                default:
                    throw new ArgumentOutOfRangeException(nameof(duration), $"Unsupported order duration: {duration}");
            }
        }

        /// <summary>
        /// Converts the tradier order status into a qc order status
        /// </summary>
        protected OrderStatus ConvertStatus(TradierOrderStatus status)
        {
            switch (status)
            {
                case TradierOrderStatus.Filled:
                    return OrderStatus.Filled;

                case TradierOrderStatus.Canceled:
                    return OrderStatus.Canceled;

                case TradierOrderStatus.Open:
                case TradierOrderStatus.Submitted:
                    return OrderStatus.Submitted;

                case TradierOrderStatus.Expired:
                case TradierOrderStatus.Rejected:
                    return OrderStatus.Invalid;

                case TradierOrderStatus.Pending:
                    return OrderStatus.New;

                case TradierOrderStatus.PartiallyFilled:
                    return OrderStatus.PartiallyFilled;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Converts the qc order status into a tradier order status
        /// </summary>
        protected TradierOrderStatus ConvertStatus(OrderStatus status)
        {
            switch (status)
            {
                case OrderStatus.New:
                    return TradierOrderStatus.Pending;

                case OrderStatus.Submitted:
                    return TradierOrderStatus.Submitted;

                case OrderStatus.PartiallyFilled:
                    return TradierOrderStatus.PartiallyFilled;

                case OrderStatus.Filled:
                    return TradierOrderStatus.Filled;

                case OrderStatus.Canceled:
                    return TradierOrderStatus.Canceled;

                case OrderStatus.None:
                    return TradierOrderStatus.Pending;

                case OrderStatus.Invalid:
                    return TradierOrderStatus.Rejected;

                default:
                    throw new ArgumentOutOfRangeException("status", status, null);
            }
        }

        /// <summary>
        /// Converts the tradier order quantity into a qc quantity
        /// </summary>
        /// <remarks>
        /// Tradier quantities are always positive and use the direction to denote +/-, where as qc
        /// order quantities determine the direction
        /// </remarks>
        protected int ConvertQuantity(TradierOrder order)
        {
            switch (order.Direction)
            {
                case TradierOrderDirection.Buy:
                case TradierOrderDirection.BuyToCover:
                case TradierOrderDirection.BuyToClose:
                case TradierOrderDirection.BuyToOpen:
                    return (int)order.Quantity;

                case TradierOrderDirection.SellShort:
                case TradierOrderDirection.Sell:
                case TradierOrderDirection.SellToOpen:
                case TradierOrderDirection.SellToClose:
                    return -(int)order.Quantity;

                case TradierOrderDirection.None:
                    return 0;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Converts the tradier position into a qc holding
        /// </summary>
        protected Holding ConvertHolding(TradierPosition position)
        {
            var symbol = _symbolMapper.GetLeanSymbol(position.Symbol);

            var averagePrice = position.CostBasis / position.Quantity;
            if (TradierSymbolMapper.SupportedOptionTypes.Contains(symbol.SecurityType))
            {
                var multiplier = _symbolPropertiesDatabase.GetSymbolProperties(
                        symbol.ID.Market,
                        symbol,
                        symbol.SecurityType,
                        _algorithm.Portfolio.CashBook.AccountCurrency)
                    .ContractMultiplier;

                averagePrice /= multiplier;
            }

            return new Holding
            {
                Symbol = symbol,
                AveragePrice = averagePrice,
                CurrencySymbol = "$",
                MarketPrice = 0m, //--> GetAccountHoldings does a call to GetQuotes to fill this data in
                Quantity = position.Quantity
            };
        }

        /// <summary>
        /// Converts the QC order direction to a tradier order direction
        /// </summary>
        protected static TradierOrderDirection ConvertDirection(OrderDirection direction, SecurityType securityType, decimal holdingQuantity)
        {
            // Equity codes: buy, buy_to_cover, sell, sell_short
            // Option codes: buy_to_open, buy_to_close, sell_to_open, sell_to_close
            // Tradier has 4 types of orders for this: buy/sell/buy to cover and sell short.

            var position = GetOrderPosition(direction, holdingQuantity);
            return position switch
            {
                // Increasing existing long position or opening new long position from zero
                OrderPosition.BuyToOpen => TradierSymbolMapper.SupportedOptionTypes.Contains(securityType) ? TradierOrderDirection.BuyToOpen : TradierOrderDirection.Buy,

                // Decreasing existing short position or opening new short position from zero
                OrderPosition.SellToOpen => TradierSymbolMapper.SupportedOptionTypes.Contains(securityType) ? TradierOrderDirection.SellToOpen : TradierOrderDirection.SellShort,

                // Buying from an existing short position (reducing, closing or flipping)
                OrderPosition.BuyToClose => TradierSymbolMapper.SupportedOptionTypes.Contains(securityType) ? TradierOrderDirection.BuyToClose : TradierOrderDirection.BuyToCover,

                // Selling from an existing long position (reducing, closing or flipping)
                OrderPosition.SellToClose => TradierSymbolMapper.SupportedOptionTypes.Contains(securityType) ? TradierOrderDirection.SellToClose : TradierOrderDirection.Sell,

                // This should never happen
                _ => TradierOrderDirection.None
            };
        }

        /// <summary>
        /// Converts the qc order duration into a tradier order duration
        /// </summary>
        protected static TradierOrderDuration GetOrderDuration(Order order, ISecurityProvider securityProvider)
        {
            if ((order.Properties as TradierOrderProperties)?.OutsideRegularTradingHours ?? false)
            {
                var exchangeTimeZone = securityProvider.GetSecurity(order.Symbol).Exchange.TimeZone;
                var now = DateTime.UtcNow.ConvertFromUtc(exchangeTimeZone);

                if (PreMarketSession.Contains(now.TimeOfDay))
                {
                    return TradierOrderDuration.Pre;
                }

                if (PostMarketSession.Contains(now.TimeOfDay))
                {
                    return TradierOrderDuration.Post;
                }
            }

            if (order.TimeInForce is GoodTilCanceledTimeInForce)
            {
                return TradierOrderDuration.GTC;
            }

            if (order.TimeInForce is DayTimeInForce)
            {
                return TradierOrderDuration.Day;
            }

            throw new ArgumentOutOfRangeException();
        }

        /// <summary>
        /// Converts the qc order type into a tradier order type
        /// </summary>
        protected static TradierOrderType ConvertOrderType(Order order)
        {
            switch (order.Type)
            {
                case OrderType.Market:
                    return TradierOrderType.Market;

                case OrderType.Limit:
                    return TradierOrderType.Limit;

                case OrderType.StopMarket:
                    return TradierOrderType.StopMarket;

                case OrderType.StopLimit:
                    return TradierOrderType.StopLimit;

                default:
                    throw new ArgumentOutOfRangeException("type", order.Type, order.Type + " not supported");
            }
        }

        /// <summary>
        /// Converts a LEAN security type to a Tradier order class
        /// </summary>
        private static TradierOrderClass ConvertSecurityType(SecurityType securityType)
        {
            switch (securityType)
            {
                case SecurityType.Equity:
                    return TradierOrderClass.Equity;

                case SecurityType.Option:
                case SecurityType.IndexOption:
                    return TradierOrderClass.Option;

                default:
                    throw new NotSupportedException($"Unsupported security type: {securityType}");
            }
        }

        /// <summary>
        /// Gets the stop price used in API calls with tradier from the specified qc order instance
        /// </summary>
        protected static decimal GetStopPrice(Order order)
        {
            var stopm = order as StopMarketOrder;
            if (stopm != null)
            {
                return stopm.StopPrice;
            }
            var stopl = order as StopLimitOrder;
            if (stopl != null)
            {
                return stopl.StopPrice;
            }
            return 0;
        }

        /// <summary>
        /// Gets the limit price used in API calls with tradier from the specified qc order instance
        /// </summary>
        /// <param name="order"></param>
        /// <returns></returns>
        protected static decimal GetLimitPrice(Order order)
        {
            var limit = order as LimitOrder;
            if (limit != null)
            {
                return limit.LimitPrice;
            }
            var stopl = order as StopLimitOrder;
            if (stopl != null)
            {
                return stopl.LimitPrice;
            }
            return 0;
        }

        #endregion Conversion routines

        /// <summary>
        /// Initailze the instance of this class
        /// </summary>
        private void Initialize(string wssUrl,
            string accountId, string accessToken, bool useSandbox, IAlgorithm algorithm,
            IOrderProvider orderProvider, ISecurityProvider securityProvider, IDataAggregator aggregator)
        {
            if (IsInitialized)
            {
                return;
            }
            var restClient = new RestClient(useSandbox ? _restApiSandboxUrl : _restApiUrl);
            base.Initialize(wssUrl, new WebSocketClientWrapper(), restClient, null, null);
            _algorithm = algorithm;
            _orderProvider = orderProvider;
            _securityProvider = securityProvider;
            _aggregator = aggregator;
            _useSandbox = useSandbox;
            _accountId = accountId;

            RestClient.AddDefaultHeader("Accept", "application/json");
            RestClient.AddDefaultHeader("Authorization", $"Bearer {accessToken}");

            var subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager();
            subscriptionManager.SubscribeImpl += (symbols, _) => Subscribe(symbols);
            subscriptionManager.UnsubscribeImpl += (symbols, _) => Unsubscribe(symbols);
            SubscriptionManager = subscriptionManager;

            // we can poll orders once a second in sandbox and twice a second in production
            var interval = _useSandbox ? 1000 : 500;
            _rateLimitNextRequest = new Dictionary<TradierApiRequestType, RateGate>
            {
                { TradierApiRequestType.Data, new RateGate(1, TimeSpan.FromMilliseconds(interval))},
                { TradierApiRequestType.Standard, new RateGate(1, TimeSpan.FromMilliseconds(interval))},
                { TradierApiRequestType.Orders, new RateGate(1, TimeSpan.FromMilliseconds(1000))},
            };

            // one read per watched order per sweep, each passing the standard rate gate above; nothing
            // is requested while no order is watched
            CreateOrderPollingService(ReadOrderState, messageHandler: null, _orderProvider, pollInterval: TimeSpan.FromMilliseconds(interval));
            OrderPollingService.Start();
            WebSocket.Error += (sender, error) =>
            {
                if (!WebSocket.IsOpen)
                {
                    // on error we clear our state, on Open we will re susbscribe
                    _subscribedTickers.Clear();
                    _streamSession = null;
                }
            };
            ValidateSubscription();
            
            // Initialize the symbol mapper with the GetUnderlyingAssetByBrokerageSymbol function
            _symbolMapper = new TradierSymbolMapper(GetUnderlyingAssetByBrokerageSymbol);

            _subscribeThead = new Thread(() =>
            {
                Log.Trace("TradierBrokerage(): Starting subscription thread");
                while (true)
                {
                    // let's wait for any subscription update request
                    var handles = new WaitHandle[] { _subscribeProcedure, _cancellationTokenSource.Token.WaitHandle };
                    WaitHandle.WaitAny(handles, GetSubscriptionRefreshTimeout(DateTime.UtcNow));
                    if (_cancellationTokenSource.IsCancellationRequested)
                    {
                        Log.Trace("TradierBrokerage(): Subscription thread ended");
                        return;
                    }

                    // send subscriptions every X seconds, we will aggregate requests during this time
                    // this is useful for options where we add/remove multiple symbols in the chain sequencially
                    var subscribeCountDown = 10;
                    while (subscribeCountDown-- > 0)
                    {
                        if (_cancellationTokenSource.Token.WaitHandle.WaitOne(Time.GetSecondUnevenWait(1000)))
                        {
                            Log.Trace("TradierBrokerage(): Subscription thread ended");
                            return;
                        }
                    }
                    // clear flag, any new requesst after this will wait X seconds
                    // This allows us to avoid race conditions, the API session we will use bellow will not be refreshed until X seconds past as minimum
                    _subscribeProcedure.Reset();

                    try
                    {
                        SendSubscribeMessage();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex);
                    }
                }
            })
            { IsBackground = true };
            _subscribeThead.Start();
        }

        private readonly HashSet<string> ErrorsDuringMarketHours = new HashSet<string>
        {
            "OrderPollingFailed", "NullResponse"
        };

        private class TradierPlaceOrderRequest
        {
            public readonly Order QCOrder;
            public readonly TradierOrderClass Classification;
            public readonly TradierOrderDirection Direction;
            public readonly string Symbol;
            public decimal Quantity;
            public decimal Price;
            public decimal Stop;
            public readonly string OptionSymbol;
            public TradierOrderType Type;
            public TradierOrderDuration Duration;

            public TradierPlaceOrderRequest(Order order, decimal orderQuantity, TradierOrderClass classification, decimal holdingQuantity, OrderType orderType, ISymbolMapper symbolMapper, ISecurityProvider securityProvider)
            {
                QCOrder = order;
                Classification = classification;

                if (TradierSymbolMapper.SupportedOptionTypes.Contains(order.SecurityType))
                {
                    OptionSymbol = symbolMapper.GetBrokerageSymbol(order.Symbol);
                    Symbol = order.Symbol.Underlying.Value;
                }
                else
                {
                    Symbol = order.Symbol.Value;
                }

                Direction = ConvertDirection(order.Direction, order.SecurityType, holdingQuantity);
                Quantity = Math.Abs(orderQuantity);
                Price = GetLimitPrice(order);
                Stop = GetStopPrice(order);
                Type = ConvertOrderType(orderType);
                Duration = GetOrderDuration(order, securityProvider);
            }

            public void ConvertStopOrderTypes()
            {
                // when this is a contingent order we'll want to convert stop types into their base order type
                if (Type == TradierOrderType.StopMarket)
                {
                    Type = TradierOrderType.Market;
                    Stop = 0m;
                }
                else if (Type == TradierOrderType.StopLimit)
                {
                    Type = TradierOrderType.Limit;
                    Stop = 0m;
                }
            }
        }

        private class ModulesReadLicenseRead : Api.RestResponse
        {
            [JsonProperty(PropertyName = "license")]
            public string License;
            [JsonProperty(PropertyName = "organizationId")]
            public string OrganizationId;
        }

        /// <summary>
        /// Validate the user of this project has permission to be using it via our web API.
        /// </summary>
        private static void ValidateSubscription()
        {
            try
            {
                const int productId = 185;
                var userId = Globals.UserId;
                var token = Globals.UserToken;
                var organizationId = Globals.OrganizationID;
                // Verify we can authenticate with this user and token
                var api = new ApiConnection(userId, token);
                if (!api.Connected)
                {
                    throw new ArgumentException("Invalid api user id or token, cannot authenticate subscription.");
                }
                // Compile the information we want to send when validating
                var information = new Dictionary<string, object>()
                {
                    {"productId", productId},
                    {"machineName", System.Environment.MachineName},
                    {"userName", System.Environment.UserName},
                    {"domainName", System.Environment.UserDomainName},
                    {"os", System.Environment.OSVersion}
                };
                // IP and Mac Address Information
                try
                {
                    var interfaceDictionary = new List<Dictionary<string, object>>();
                    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.OperationalStatus == OperationalStatus.Up))
                    {
                        var interfaceInformation = new Dictionary<string, object>();
                        // Get UnicastAddresses
                        var addresses = nic.GetIPProperties().UnicastAddresses
                            .Select(uniAddress => uniAddress.Address)
                            .Where(address => !IPAddress.IsLoopback(address)).Select(x => x.ToString());
                        // If this interface has non-loopback addresses, we will include it
                        if (!addresses.IsNullOrEmpty())
                        {
                            interfaceInformation.Add("unicastAddresses", addresses);
                            // Get MAC address
                            interfaceInformation.Add("MAC", nic.GetPhysicalAddress().ToString());
                            // Add Interface name
                            interfaceInformation.Add("name", nic.Name);
                            // Add these to our dictionary
                            interfaceDictionary.Add(interfaceInformation);
                        }
                    }
                    information.Add("networkInterfaces", interfaceDictionary);
                }
                catch (Exception)
                {
                    // NOP, not necessary to crash if fails to extract and add this information
                }
                // Include our OrganizationId is specified
                if (!string.IsNullOrEmpty(organizationId))
                {
                    information.Add("organizationId", organizationId);
                }
                var request = new RestRequest("modules/license/read", Method.POST) { RequestFormat = DataFormat.Json };
                request.AddParameter("application/json", JsonConvert.SerializeObject(information), ParameterType.RequestBody);
                api.TryRequest(request, out ModulesReadLicenseRead result);
                if (!result.Success)
                {
                    throw new InvalidOperationException($"Request for subscriptions from web failed, Response Errors : {string.Join(',', result.Errors)}");
                }

                var encryptedData = result.License;
                // Decrypt the data we received
                DateTime? expirationDate = null;
                long? stamp = null;
                bool? isValid = null;
                if (encryptedData != null)
                {
                    // Fetch the org id from the response if we are null, we need it to generate our validation key
                    if (string.IsNullOrEmpty(organizationId))
                    {
                        organizationId = result.OrganizationId;
                    }
                    // Create our combination key
                    var password = $"{token}-{organizationId}";
                    var key = SHA256.HashData(Encoding.UTF8.GetBytes(password));
                    // Split the data
                    var info = encryptedData.Split("::");
                    var buffer = Convert.FromBase64String(info[0]);
                    var iv = Convert.FromBase64String(info[1]);
                    // Decrypt our information
                    using var aes = new AesManaged();
                    var decryptor = aes.CreateDecryptor(key, iv);
                    using var memoryStream = new MemoryStream(buffer);
                    using var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read);
                    using var streamReader = new StreamReader(cryptoStream);
                    var decryptedData = streamReader.ReadToEnd();
                    if (!decryptedData.IsNullOrEmpty())
                    {
                        var jsonInfo = JsonConvert.DeserializeObject<JObject>(decryptedData);
                        expirationDate = jsonInfo["expiration"]?.Value<DateTime>();
                        isValid = jsonInfo["isValid"]?.Value<bool>();
                        stamp = jsonInfo["stamped"]?.Value<int>();
                    }
                }
                // Validate our conditions
                if (!expirationDate.HasValue || !isValid.HasValue || !stamp.HasValue)
                {
                    throw new InvalidOperationException("Failed to validate subscription.");
                }

                var nowUtc = DateTime.UtcNow;
                var timeSpan = nowUtc - Time.UnixTimeStampToDateTime(stamp.Value);
                if (timeSpan > TimeSpan.FromHours(12))
                {
                    throw new InvalidOperationException("Invalid API response.");
                }
                if (!isValid.Value)
                {
                    throw new ArgumentException($"Your subscription is not valid, please check your product subscriptions on our website.");
                }
                if (expirationDate < nowUtc)
                {
                    throw new ArgumentException($"Your subscription expired {expirationDate}, please renew in order to use this product.");
                }
            }
            catch (Exception e)
            {
                Log.Error($"ValidateSubscription(): Failed during validation, shutting down. Error : {e.Message}");
                System.Environment.Exit(1);
            }
        }

        /// <summary>
        /// We refresh 4 am new york, pre market open
        /// </summary>
        public static TimeSpan GetSubscriptionRefreshTimeout(DateTime utcTime)
        {
            var nyTime = utcTime.ConvertFromUtc(TimeZones.NewYork);
            if (nyTime.TimeOfDay < TimeSpan.FromHours(4))
            {
                return TimeSpan.FromHours(4) - nyTime.TimeOfDay;
            }
            return (nyTime.AddDays(1).Date - nyTime) + TimeSpan.FromHours(4);
        }
    }
}
