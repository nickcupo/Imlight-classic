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
 * CLASSIC RULES AUDIT
 * ========================================================================
 * 
 * PURPOSE:
 * Collapses repeated refusals of the same (character, subject) inside a
 * short window, so the player sees one dialog and the log gets one line.
 * 
 * USAGE EXAMPLE:
 * if (throttle.ShouldReport(charId, zone)) { audit(...); inform(...); }
 * 
 * NOTE:
 * Imlight delivers some zone transfer requests to ZoneService twice
 * (SessionActor forwards them and CantripService tells them again), so one
 * refused teleport would otherwise show two dialogs. Subjects compare
 * without case.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Generic;
using System.Threading;

namespace Imlight.Classic;

/// <summary>
/// Suppresses duplicate refusal reports inside a time window.
/// </summary>
public sealed class DenialThrottle {

    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly Lock _lock = new();
    private readonly Dictionary<(ulong CharId, string Subject), DateTimeOffset> _lastReported = new(KeyComparer.Instance);

    /// <summary>
    /// Creates a throttle.
    /// </summary>
    /// <param name="time">The clock; the system clock when null.</param>
    /// <param name="window">How long a report suppresses repeats; <see cref="DefaultWindow"/> when null.</param>
    public DenialThrottle(TimeProvider? time = null, TimeSpan? window = null) {
        _time = time ?? TimeProvider.System;
        _window = window ?? DefaultWindow;
    }

    /// <summary>
    /// Two seconds.
    /// </summary>
    public static TimeSpan DefaultWindow { get; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True when this (character, subject) was not reported inside the window; the call then counts as a report.
    /// </summary>
    /// <param name="charId">The character; 0 when unknown.</param>
    /// <param name="subject">The zone or feature refused.</param>
    /// <returns>True if the caller should report it.</returns>
    public bool ShouldReport(ulong charId, string subject) {
        var now = _time.GetUtcNow();
        var key = (charId, subject);
        lock (_lock) {
            if (_lastReported.TryGetValue(key, out var last) && now - last < _window) {
                return false;
            }

            foreach (var (staleKey, reportedAt) in _lastReported) {
                if (now - reportedAt >= _window) {
                    _lastReported.Remove(staleKey);
                }
            }

            _lastReported[key] = now;

            return true;
        }
    }

    private sealed class KeyComparer : IEqualityComparer<(ulong CharId, string Subject)> {

        public static KeyComparer Instance { get; } = new();

        public bool Equals((ulong CharId, string Subject) x, (ulong CharId, string Subject) y)
            => x.CharId == y.CharId && StringComparer.OrdinalIgnoreCase.Equals(x.Subject, y.Subject);

        public int GetHashCode((ulong CharId, string Subject) key)
            => HashCode.Combine(key.CharId, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Subject));

    }

}
