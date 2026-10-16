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
 * CELESTIA RULES AND THE MOON REWORK SWITCH
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: "is this profile Celestia (celestia-2010 or one extending it)?"
 * for the Astral mechanics (Star auras, Moon polymorph, the Sun enchant
 * rules), and the [Classic] MoonRework switch for the owner's Moon rework.
 *
 * USAGE EXAMPLE:
 * if (ClassicCelestiaRules.Active) { ... }
 * if (MoonReworkSettings.Current is { } rework) { ... }
 *
 * NOTE:
 * The Moon rework is an owner extra outside the period rules, like the Myth
 * minion control (EnhancedGameplaySettings). [Classic] MoonRework is on by
 * default for the Celestia family (blank or true), false turns it off; it
 * never applies to october-2010-arc1, late-2009 or arc1-2009h1, which have
 * no Astral schools at all.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/16/2026
 */

#nullable enable

using System;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.Common;

namespace Imlight.CoreLib.Classic;

/// <summary>CLASSIC: the Celestia (October 2010, level 60) mechanics: Astral schools.</summary>
internal static class ClassicCelestiaRules {

    /// <summary>True for celestia-2010 and every profile that extends it.</summary>
    internal static bool Active => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive
        && ClassicRuntime.Rules.Profile.IsOrExtends(ClassicProfiles.Celestia2010);

    /// <summary>The active profile's Astral schools file, or <see cref="AstralRules.Empty"/> outside Celestia.</summary>
    internal static AstralRules Astral
        => Active && ClassicProgression.Astral.AppliesTo(ClassicRuntime.Rules.Profile.Id) ? ClassicProgression.Astral : AstralRules.Empty;
}

/// <summary>
/// CLASSIC OWNER EXTRA: [Classic] MoonRework (the owner's Moon rework, not 2010). Blank or true: on wherever the active
/// Celestia profile's Astral file carries a moon_rework; false: off, and the dated 2010 polymorph runs alone.
/// </summary>
internal static class MoonReworkSettings {

    /// <summary>Tests set this to force the switch (null: read [Classic] MoonRework).</summary>
    internal static bool? OverrideForTests { get; set; }

    /// <summary>True when [Classic] MoonRework is blank or true.</summary>
    internal static bool Switch {
        get {
            if (OverrideForTests is { } forced) return forced;
            try {
                var value = ConfigurationManager.Settings["Classic.MoonRework"].AsString();
                return string.IsNullOrWhiteSpace(value) || (bool.TryParse(value.Trim(), out var enabled) && enabled);
            }
            catch (Exception) {
                return true; // no configuration loaded (tests, tools): the default
            }
        }
    }

    /// <summary>The rework in force, or null (switch off, not Celestia, or no moon_rework in the Astral file).</summary>
    internal static MoonRework? Current => Switch && ClassicCelestiaRules.Astral.MoonRework is { } rework ? rework : null;
}
