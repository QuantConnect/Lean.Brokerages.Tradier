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
using NUnit.Framework;
using QuantConnect.Brokerages.Tradier;

namespace QuantConnect.Tests.Brokerages.Tradier
{
    [TestFixture]
    public class TradierSymbolMapperTests
    {
        private TradierSymbolMapper _symbolMapper;
        private static TestCaseData[] TestParameters
        {
            get
            {
                return new[]
                {
                    // Basic equity and index symbols
                    new TestCaseData(Symbols.AAPL, "AAPL"),
                    new TestCaseData(Symbols.SPX, "SPX"),
                    new TestCaseData(Symbol.Create("VIX", SecurityType.Index, Market.USA), "VIX"),
                    
                    // Equity options
                    new TestCaseData(Symbol.CreateOption("QQQ", Market.USA, SecurityType.Option.DefaultOptionStyle(), OptionRight.Put, 350m, new DateTime(2025, 7, 25)), "QQQ250725P00350000"),
                    new TestCaseData(Symbol.CreateOption("SPY", Market.USA, SecurityType.Option.DefaultOptionStyle(), OptionRight.Call, 410m, new DateTime(2021, 3, 19)), "SPY210319C00410000"),
                    new TestCaseData(Symbol.CreateOption("AAPL", Market.USA, SecurityType.Option.DefaultOptionStyle(), OptionRight.Call, 150m, new DateTime(2025, 1, 17)), "AAPL250117C00150000"),
                    
                    // Index options - including SPX and SPXW weeklies
                    new TestCaseData(Symbol.CreateOption(Symbols.SPX, "SPX", Market.USA, SecurityType.IndexOption.DefaultOptionStyle(), OptionRight.Call, 5900m, new DateTime(2025, 7, 25)), "SPX250725C05900000"),
                    new TestCaseData(Symbol.CreateOption(Symbols.SPX, "SPXW", Market.USA, SecurityType.IndexOption.DefaultOptionStyle(), OptionRight.Call, 5900m, new DateTime(2025, 7, 25)), "SPXW250725C05900000"),
                    new TestCaseData(Symbol.CreateOption(Symbol.Create("VIX", SecurityType.Index, Market.USA), "VIX", Market.USA, SecurityType.IndexOption.DefaultOptionStyle(), OptionRight.Put, 25m, new DateTime(2025, 8, 20)), "VIX250820P00025000"),
                    
                    // Dot ticker symbols and their options
                    new TestCaseData(Symbol.Create("BRK.B", SecurityType.Equity, Market.USA), "BRK/B"),
                    new TestCaseData(Symbol.CreateOption(Symbol.Create("BRK.B", SecurityType.Equity, Market.USA), Market.USA, SecurityType.Option.DefaultOptionStyle(), OptionRight.Call, 455.0m , new DateTime(2025, 9, 12)), "BRKB250912C00455000"),                
                };
            }
        }

        [OneTimeSetUp]
        public void Setup()
        {
            // Mock quote function for testing - returns a quote with AAPL as underlying asset
            _symbolMapper = new TradierSymbolMapper(symbol => new TradierQuote 
            { 
                Options_UnderlyingAsset = "AAPL" 
            });
        }

        [Test, TestCaseSource(nameof(TestParameters))]
        public void ReturnsCorrectLeanSymbol(Symbol expectedLeanSymbol, string brokerSymbol)
        {
            Assert.AreEqual(expectedLeanSymbol, _symbolMapper.GetLeanSymbol(brokerSymbol));
        }

        [Test, TestCaseSource(nameof(TestParameters))]
        public void ReturnsCorrectBrokerageSymbol(Symbol leanSymbol, string expectedBrokerSymbol)
        {
            Assert.AreEqual(expectedBrokerSymbol, _symbolMapper.GetBrokerageSymbol(leanSymbol));
        }
    }
}