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
 * CLASSIC TIME
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the game's calendar day ([Classic] GameTimeZone, see GameClock):
 * holiday events and the daily Second Chance uses follow it. The live
 * container stays on UTC; deploy-from-mac.sh sets America/New_York.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

#nullable enable

using System;
using Imlight.Classic.Admin;
using Imlight.Common;

namespace Imlight.CoreLib.Classic;

/// <summary>CLASSIC: the game's time zone and calendar day.</summary>
public static class ClassicTime {

    private static readonly Lazy<TimeZoneInfo> s_zone = new(() => {
        string? id = null;
        try {
            id = ConfigurationManager.GetSetting("Classic.GameTimeZone");
        }
        catch (Exception) {
            // no configuration (tests): the machine's zone
        }

        var zone = GameClock.Resolve(id, out var error);
        if (error is not null) {
            Logger.Warning("[Classic] GameTimeZone: {Error}", Logger.Args(error));
        }
        else {
            Logger.Information("[Classic] Game days (holidays, Second Chance uses) follow {Zone}.", Logger.Args(zone.Id));
        }

        return zone;
    });

    /// <summary>The zone the game's days follow.</summary>
    public static TimeZoneInfo Zone => s_zone.Value;

    /// <summary>The game's calendar day at the instant <paramref name="utc"/>.</summary>
    public static DateOnly DayOf(DateTime utc) => GameClock.DayOf(utc, Zone);

    /// <summary>The game's calendar day now.</summary>
    public static DateOnly Today => DayOf(DateTime.UtcNow);

}
