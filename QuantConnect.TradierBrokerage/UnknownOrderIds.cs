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

using System.Collections.Generic;

namespace QuantConnect.Brokerages.Tradier
{
    /// <summary>
    /// The Tradier order ids the fill polling found and a verification task has to check, at most one task runs at a time
    /// </summary>
    public class UnknownOrderIds
    {
        private readonly HashSet<long> _ids = [];

        /// <summary>
        /// Adds the ids. Returns true when no task is in flight and there is something to verify, so the caller starts one
        /// </summary>
        public bool TryBeginVerification(IEnumerable<long> ids)
        {
            lock (_ids)
            {
                var idle = _ids.Count == 0;
                _ids.UnionWith(ids);
                return idle && _ids.Count != 0;
            }
        }

        /// <summary>
        /// The ids the running task has to verify
        /// </summary>
        public HashSet<long> ToHashSet()
        {
            lock (_ids)
            {
                return [.. _ids];
            }
        }

        /// <summary>
        /// Releases all the ids once the task is done, so the next poll can start a new one
        /// </summary>
        public void EndVerification()
        {
            lock (_ids)
            {
                _ids.Clear();
            }
        }
    }
}
