/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * LOG ONCE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: "first time this process sees this key" for log lines that matter
 * once and are noise after (a missing handler, a data typo, an unknown
 * requirement type). The first is logged at its real level, the rest lower.
 *
 * USAGE EXAMPLE:
 * if (LogOnce.FirstTime("spell-name", name)) Logger.Warning(...); else Logger.Debug(...);
 * if (UnhandledMessageLog.FirstTime(message.GetType())) Logger.Warning(...);
 *
 * NOTE:
 * Bounded: past 10 000 keys the set starts over (a key may then log again).
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Concurrent;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// Remembers which keys were already logged in this process.
/// </summary>
internal static class LogOnce {

    private const int MaxKeys = 10_000;
    private static readonly ConcurrentDictionary<(string Category, string Key), byte> s_seen = new();

    /// <summary>True the first time <paramref name="category"/>/<paramref name="key"/> is seen by this process.</summary>
    internal static bool FirstTime(string category, string key) {
        if (s_seen.Count > MaxKeys) {
            s_seen.Clear();
        }

        return s_seen.TryAdd((category ?? "", key ?? ""), 0);
    }

    /// <summary>Forgets everything (tests).</summary>
    internal static void ResetForTests() => s_seen.Clear();

}

/// <summary>
/// CLASSIC: client messages the server has no handler for, warned about once per message type per process.
/// </summary>
internal static class UnhandledMessageLog {

    /// <summary>True the first time a message of <paramref name="type"/> goes unhandled in this process.</summary>
    internal static bool FirstTime(Type type) => type is not null && LogOnce.FirstTime("unhandled-message", type.FullName);

}
