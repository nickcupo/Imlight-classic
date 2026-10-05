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
 * GAME CLOCK
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the calendar day the game follows ([Classic] GameTimeZone): when
 * holiday events start and end, and when the daily Second Chance uses come
 * back. The machine and the server keep UTC for everything else (logs,
 * timers, throttles), so a daylight-saving change never moves them.
 *
 * NOTE:
 * Empty GameTimeZone keeps the machine's own zone (the old behaviour: UTC
 * on the live container, local time on a Mac rig). An unknown zone id logs
 * a warning and falls back the same way.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;

namespace Imlight.Classic.Admin;

/// <summary>CLASSIC: the game's calendar day in a configured time zone.</summary>
public static class GameClock {

    /// <summary>
    /// The zone for <paramref name="id"/> (an IANA id such as America/New_York), or <paramref name="fallback"/>
    /// (default: the machine's zone) when it is empty or unknown; <paramref name="error"/> says why it fell back.
    /// </summary>
    public static TimeZoneInfo Resolve(string? id, out string? error, TimeZoneInfo? fallback = null) {
        error = null;
        fallback ??= TimeZoneInfo.Local;
        if (string.IsNullOrWhiteSpace(id)) {
            return fallback;
        }

        try {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) {
            error = $"unknown time zone '{id}' ({ex.Message}); using {fallback.Id}";

            return fallback;
        }
    }

    /// <summary>The calendar day in <paramref name="zone"/> at the instant <paramref name="utc"/>.</summary>
    public static DateOnly DayOf(DateTime utc, TimeZoneInfo zone) {
        var instant = utc.Kind switch {
            DateTimeKind.Utc => utc,
            DateTimeKind.Local => utc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(utc, DateTimeKind.Utc), // unspecified: treated as UTC
        };

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(instant, zone));
    }

}
