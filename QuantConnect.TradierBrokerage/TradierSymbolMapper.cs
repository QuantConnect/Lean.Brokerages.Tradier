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

using QuantConnect.Configuration;
using QuantConnect.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Brokerages.Tradier
{
    /// <summary>
    /// Provides the mapping between Lean symbols and Tradier symbols.
    /// </summary>
    public class TradierSymbolMapper : ISymbolMapper
    {
        private readonly Func<List<string>, List<TradierQuote>> _getQuotesDelegate;

        public static readonly FrozenSet<SecurityType> SupportedOptionTypes =
        [
            SecurityType.Option,
            SecurityType.IndexOption
        ];

        public static readonly FrozenSet<SecurityType> SupportedSecurityTypes =
        [
            SecurityType.Equity,
            SecurityType.Option,
            SecurityType.Index,
            SecurityType.IndexOption
        ];

        private static readonly ConcurrentDictionary<string, string> _leanUnderlyingSymbol = [];

        /// <summary>
        /// Initializes a new instance of the TradierSymbolMapper class.
        /// </summary>
        /// <param name="getQuotesDelegate">Delegate to get quotes for symbols.</param>
        public TradierSymbolMapper(Func<List<string>, List<TradierQuote>> getQuotesDelegate = null)
        {
            _getQuotesDelegate = getQuotesDelegate;
        }

        /// <summary>
        /// Converts a Lean symbol instance to a Tradier symbol
        /// </summary>
        /// <param name="symbol">A Lean symbol instance</param>
        /// <returns>The Tradier symbol</returns>
        public string GetBrokerageSymbol(Symbol symbol)
        {
            switch (symbol.SecurityType)
            {
                case SecurityType.Option:
                case SecurityType.IndexOption:
                    return GenerateOptionBrokerageSymbols(symbol);
                
                case SecurityType.Equity:
                case SecurityType.Index:
                    return ToBrokerageTickerFormat(symbol.Value);
                
                default:
                    throw new NotSupportedException($"{nameof(TradierSymbolMapper)}.{nameof(GetBrokerageSymbol)}: Unsupported security type: {symbol.SecurityType}");
            }
        }

        /// <summary>
        /// Converts a Tradier symbol to a Lean symbol instance
        /// </summary>
        /// <param name="brokerageSymbol">The Tradier symbol</param>
        /// <param name="securityType">The security type</param>
        /// <param name="market">The market</param>
        /// <param name="expirationDate">Expiration date of the security(if applicable)</param>
        /// <param name="strike">The strike of the security (if applicable)</param>
        /// <param name="optionRight">The option right of the security (if applicable)</param>
        /// <returns>A new Lean Symbol instance</returns>
        public Symbol GetLeanSymbol(
           string brokerageSymbol,
           SecurityType securityType,
           string market,
           DateTime expirationDate = default(DateTime),
           decimal strike = 0,
           OptionRight optionRight = OptionRight.Call
           )
        {
            // unused
            throw new NotImplementedException();
        }

        /// <summary>
        /// Converts a Tradier symbol to a Lean symbol instance
        /// </summary>
        /// <param name="brokerageSymbol">The Tradier symbol</param>
        /// <returns>A new Lean Symbol instance</returns>
        public Symbol GetLeanSymbol(string brokerageSymbol)
        {
            if (brokerageSymbol.Length > 15)
            {
                // Determine security type first
                var underlying = brokerageSymbol.Substring(0, brokerageSymbol.Length - 15);
                var securityType = Securities.IndexOption.IndexOptionSymbol.IsIndexOption(underlying) ? SecurityType.IndexOption : SecurityType.Option;

                switch (securityType)
                {
                    case SecurityType.IndexOption:
                        // Use OSI parsing for IndexOptions (works perfectly)
                        var ticker = underlying.PadRight(6, ' ') + brokerageSymbol.Substring(underlying.Length);
                        return SymbolRepresentation.ParseOptionTickerOSI(ticker, securityType, securityType.DefaultOptionStyle(), Market.USA);

                    case SecurityType.Option:
                        // Manual creation for Options (like BRK.B options)
                        if (!SymbolRepresentation.TryDecomposeOptionTickerOSI(brokerageSymbol, out var optionTicker, out var expiration, out var right, out var strike))
                        {
                            throw new NotSupportedException($"{nameof(TradierSymbolMapper)}.{nameof(GetLeanSymbol)}: Unsupported option symbol '{brokerageSymbol}': Could not parse as OSI format");
                        }
                        var underlyingBrokerageSymbol = GetUnderlyingBrokerageSymbol(brokerageSymbol,optionTicker);
                        var originalUnderlying = !string.IsNullOrEmpty(underlyingBrokerageSymbol) 
                            ? ToLeanTickerFormat(underlyingBrokerageSymbol) 
                            : optionTicker;
                        
                        var underlyingSymbol = Symbol.Create(originalUnderlying, SecurityType.Equity, Market.USA);

                        return Symbol.CreateOption(underlyingSymbol, Market.USA, 
                            securityType.DefaultOptionStyle(), right, strike, expiration);

                    default:
                        throw new NotSupportedException($"{nameof(TradierSymbolMapper)}.{nameof(GetLeanSymbol)}: Unsupported security type for option symbol '{brokerageSymbol}'");
                }
            }

            if (Securities.IndexOption.IndexOptionSymbol.IsIndexOption(brokerageSymbol))
            {
                return Symbol.Create(brokerageSymbol, SecurityType.Index, Market.USA);
            }

            return Symbol.Create(ToLeanTickerFormat(brokerageSymbol), SecurityType.Equity, Market.USA);
        }

        /// <summary>
        /// Generates the brokerage symbol for an option.
        /// </summary>
        /// <param name="symbol">The option symbol to generate the brokerage symbol for.</param>
        /// <returns>The brokerage symbol for the option.</returns>
        private static string GenerateOptionBrokerageSymbols(Symbol symbol)
        {
            return SymbolRepresentation.GenerateOptionTickerOSICompact(
                symbol.ID.Symbol,
                symbol.ID.OptionRight,
                symbol.ID.StrikePrice,
                symbol.ID.Date).Replace(" ", "").Replace(".", "");
        }

        /// <summary>
        /// Get the underlying brokerage symbol for an option.
        /// </summary>
        /// <param name="brokerageSymbol"></param>
        /// <param name="optionTicker"></param>
        /// <returns></returns>
        private string GetUnderlyingBrokerageSymbol(string brokerageSymbol, string optionTicker)
        {
            if (_leanUnderlyingSymbol.TryGetValue(optionTicker, out var cached))
            {
                return cached;
            }

            // If no delegate provided, return the option ticker as fallback
            if (_getQuotesDelegate == null)
            {
                Log.Trace($"{nameof(TradierSymbolMapper)}.{nameof(GetUnderlyingBrokerageSymbol)}: No quotes delegate provided, using option ticker as underlying: {optionTicker}");
                _leanUnderlyingSymbol.TryAdd(optionTicker, optionTicker);
                return optionTicker;
            }

            try
            {
                var quotes = _getQuotesDelegate(new List<string> { brokerageSymbol });
                var underlying = quotes?.FirstOrDefault()?.Options_UnderlyingAsset;
                
                if (string.IsNullOrEmpty(underlying))
                {
                    Log.Trace($"{nameof(TradierSymbolMapper)}.{nameof(GetUnderlyingBrokerageSymbol)}: No underlying asset found for expired or invalid option '{brokerageSymbol}', using option ticker: {optionTicker}");
                    underlying = optionTicker;
                }
                
                _leanUnderlyingSymbol.TryAdd(optionTicker, underlying);
                return underlying;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"{nameof(TradierSymbolMapper)}.{nameof(GetUnderlyingBrokerageSymbol)}: Error getting quotes for '{brokerageSymbol}', using option ticker as fallback: {optionTicker}");
                _leanUnderlyingSymbol.TryAdd(optionTicker, optionTicker);
                return optionTicker;
            }
        }
        /// <summary>
        /// Normalizes a brokerage-formatted equity/index ticker to Lean format by replacing slashes ('/') with periods ('.').
        /// Example: "BRK/B" -> "BRK.B". Use when converting symbols received from Tradier back into Lean.
        /// </summary>
        /// <param name="brokerageTicker">Ticker received from Tradier (e.g., "BRK/B").</param>
        /// <returns>Lean-compatible ticker (e.g., "BRK.B").</returns>
        private static string ToLeanTickerFormat(string brokerageTicker) => brokerageTicker.Replace('/', '.');


        /// <summary>
        /// Converts a Lean equity/index ticker to Tradier brokerage format by replacing periods ('.') with slashes ('/').
        /// Example: "BRK.B" -> "BRK/B". Use when preparing symbols for Tradier REST requests.
        /// </summary>
        /// <param name="leanTicker">Lean ticker (e.g., "BRK.B").</param>
        /// <returns>Brokerage-compatible ticker (e.g., "BRK/B").</returns>
        private static string ToBrokerageTickerFormat(string leanTicker) => leanTicker.Replace('.', '/');
    }
}
