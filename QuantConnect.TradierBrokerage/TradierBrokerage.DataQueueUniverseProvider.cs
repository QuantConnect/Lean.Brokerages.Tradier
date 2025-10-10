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
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Tradier
{
    /// <summary>
    /// Tradier Class: IDataQueueUniverseProvider implementation
    /// </summary>
    public partial class TradierBrokerage
    {
        /// <summary>
        /// Method returns a collection of Symbols that are available at the data source.
        /// </summary>
        /// <param name="symbol">Symbol to lookup</param>
        /// <param name="includeExpired">Include expired contracts</param>
        /// <param name="securityCurrency">Expected security currency(if any)</param>
        /// <returns>Enumerable of Symbols, that are associated with the provided Symbol</returns>
        public IEnumerable<Symbol> LookupSymbols(Symbol symbol, bool includeExpired, string securityCurrency = null)
        {
            //Removed Conditional check for supported asset types, because we can pass the 

            var lookupName = symbol.Underlying?.Value ?? symbol.Value;
            var underlyingSecurityType = symbol.Underlying?.SecurityType ?? symbol.SecurityType;

            Log.Trace($"TradierBrokerage.LookupSymbols(): Requesting symbol list for {lookupName} ...");

            var symbols = new List<Symbol>();
            var today = DateTime.UtcNow.ConvertFromUtc(TimeZones.NewYork).Date;
            
            // Convert Lean symbol to brokerage format for API call (e.g., BRK.B -> BRK/B)
            // For canonical option symbols, we need to use the underlying symbol's security type
            var brokerageLookupName = _symbolMapper.GetBrokerageSymbol(Symbol.Create(lookupName, (SecurityType)underlyingSecurityType, symbol.ID.Market));
            
            // Use Tradier's options lookup API endpoint to get options symbols
            var optionsLookupResult = GetOptionsLookup(brokerageLookupName);
            
            // Convert Tradier options symbols to Lean symbols
            if (optionsLookupResult?.Options != null)
            {
                foreach (var optionSymbol in optionsLookupResult.Options)
                {
                    try
                    {
                        var leanSymbol = _symbolMapper.GetLeanSymbol(optionSymbol);
                        if (leanSymbol != null)
                        {
                            symbols.Add(leanSymbol);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Trace($"TradierBrokerage.LookupSymbols(): Failed to convert symbol {optionSymbol}: {ex.Message}");
                    }
                }
            }

            // Try to remove options contracts that have expired
            if (!includeExpired)
            {
                var removedSymbols = symbols.Where(x => x.ID.Date < today).ToHashSet();

                if (symbols.RemoveAll(x => removedSymbols.Contains(x)) > 0)
                {
                    Log.Trace(
                        $"TradierBrokerage.LookupSymbols(): Removed contract(s) for having expiry in the past: {string.Join(",", removedSymbols.Select(x => x.Value))}");
                }
            }

            Log.Trace($"TradierBrokerage.LookupSymbols(): Returning {symbols.Count} contract(s) for {lookupName}");

            return symbols;
        }

        /// <summary>
        /// Returns whether selection can take place or not.
        /// </summary>
        /// <remarks>This is useful to avoid a selection taking place during invalid times, for example IB reset times or when not connected,
        /// because if allowed selection would fail since IB isn't running and would kill the algorithm</remarks>
        /// <returns>True if selection can take place</returns>
        public bool CanPerformSelection()
        {
            return true;
        }
    }
}
