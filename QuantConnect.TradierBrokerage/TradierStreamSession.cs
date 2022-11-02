﻿/*
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

namespace QuantConnect.Brokerages.Tradier
{
    /// <summary>
    /// Create a new stream session
    /// </summary>
    public class TradierStreamSession
    {
        private readonly static TimeSpan LifeSpan = TimeSpan.FromMinutes(4.9);
        private readonly DateTime _createdTime = DateTime.UtcNow;

        /// <summary>
        /// Trading Stream: Session Id
        /// </summary>
        public string SessionId;

        /// <summary>
        /// Trading Stream: Stream URL
        /// </summary>
        public string Url;

        /// <summary>
        /// Determines if this session Id is valid
        /// </summary>
        public bool IsValid => DateTime.UtcNow - _createdTime < LifeSpan;
    }
}
