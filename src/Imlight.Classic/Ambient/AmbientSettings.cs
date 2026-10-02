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
 * AMBIENT WIZARD SETTINGS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the [Classic] switches for ambient wizards, parsed without the
 * server so they can be tested:
 *   AmbientWizards     = off | 0 | N   (N per zone; off or 0 turns them off)
 *   AmbientWizardZones = zone[:N], ...  (empty: the Commons, Unicorn Way, the
 *                                        Shopping District and every open
 *                                        world hub)
 *   AmbientWizardChat     = true|false (canned and reply chat)
 *   AmbientWizardBattles  = true|false (ask to help in real players' fights)
 *   AmbientWizardStreetFights = true|false (fight street mobs on their own)
 *
 * USAGE EXAMPLE:
 * var settings = AmbientSettings.Parse(count: "4", zones: "", chat: "", battles: "", fights: "");
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace Imlight.Classic.Ambient;

/// <summary>The ambient wizard switches (see the file header).</summary>
public sealed record AmbientSettings(int PerZone, ImmutableArray<(string Zone, int Count)> Zones, bool UsesDefaultZones,
                                     bool Chat, bool Battles, bool StreetFights) {

    /// <summary>The per-zone count when the setting is missing.</summary>
    public const int DefaultPerZone = 4;

    /// <summary>The zones used when AmbientWizardZones is empty; world hubs are added by the server.</summary>
    public static readonly ImmutableArray<string> DefaultStreetZones =
        ["WizardCity/WC_Hub", "WizardCity/WC_Streets/WC_Unicorn", "WizardCity/WC_Shop_Area"];

    /// <summary>Hard cap on wizards per zone, whatever the setting says.</summary>
    public const int MaxPerZone = 12;

    /// <summary>Off: no ambient wizard anywhere.</summary>
    public static AmbientSettings Off { get; } = new(0, [], false, false, false, false);

    public bool Enabled => PerZone > 0 || Zones.Any(z => z.Count > 0);

    /// <summary>Parses the five settings; blank values take their defaults.</summary>
    public static AmbientSettings Parse(string? count, string? zones, string? chat, string? battles, string? fights) {
        var text = (count ?? "").Trim();
        int perZone;
        if (text.Length == 0) {
            perZone = DefaultPerZone;
        }
        else if (text.Equals("off", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase)
                 || text.Equals("no", StringComparison.OrdinalIgnoreCase)) {
            return Off;
        }
        else if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out perZone) || perZone <= 0) {
            return Off;
        }

        perZone = Math.Min(perZone, MaxPerZone);
        var list = new List<(string, int)>();
        foreach (var entry in (zones ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var colon = entry.LastIndexOf(':');
            if (colon > 0 && int.TryParse(entry[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) {
                list.Add((entry[..colon].Trim(), Math.Clamp(n, 0, MaxPerZone)));
            }
            else {
                list.Add((entry, perZone));
            }
        }

        var usesDefault = list.Count == 0;
        if (usesDefault) {
            list.AddRange(DefaultStreetZones.Select(zone => (zone, perZone)));
        }

        return new AmbientSettings(perZone, [.. list.DistinctBy(z => z.Item1, StringComparer.OrdinalIgnoreCase)], usesDefault,
            Flag(chat), Flag(battles), Flag(fights));
    }

    private static bool Flag(string? value)
        => string.IsNullOrWhiteSpace(value) || !bool.TryParse(value.Trim(), out var on) || on;

}
