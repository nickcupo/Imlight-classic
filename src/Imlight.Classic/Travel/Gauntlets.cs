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
 * CLASSIC GAUNTLETS
 * ========================================================================
 *
 * PURPOSE:
 * A 2009 gauntlet resets whenever its wizard leaves it. The Golem Tower is
 * the one this server knows: five solo floors (WC_Golem_Tower_1..5) that a
 * wizard climbs once per trip (Regina Flametalon's three quests, the level-12
 * school quests that send the wizard back for the Iron Golem). Coming back
 * in from outside the tower starts a fresh tower, every floor repopulated.
 *
 * EVIDENCE:
 * - client r806919 help text Help_Instances01: gauntlets reset if you leave
 *   them "for any reason", and the Golem Tower is the example it names;
 * - Fandom Quests oldid 21718 (2009-06-20), "Gauntlets" (see GroupInstances);
 * - players 2008-2010 (Wizard101 forum threads 11024, 20787, 22191; the
 *   Silver Shade of Winter blog, 2010-02-28): solo, one run per quest, the
 *   floors full again on each run.
 * Without the reset the wizard's own instance kept a dead Iron Golem until a
 * restart, so a later quest for it (I've Got the Power, Animation) found no
 * golem (playbot-reports/golem-tower.md, run r6).
 *
 * USAGE EXAMPLE:
 * if (Gauntlets.EntersFromOutside(fromZone, toZone)) { ... drop Gauntlets.ZonesOf(toZone) ... }
 *
 * NOTE:
 * Moving from floor to floor is not leaving: the floors behind stay as the
 * wizard left them. Only an entry from a zone outside the gauntlet resets it.
 *
 * TODO:
 * Other gauntlets (Briskbreeze Tower and the later ones) when their reset is
 * checked against dated sources.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Immutable;
using System.Linq;

namespace Imlight.Classic.Travel;

/// <summary>
/// CLASSIC: the 2009 gauntlets, which reset whenever their wizard leaves them.
/// </summary>
public static class Gauntlets {

    /// <summary>The Golem Tower's five floors, bottom to top.</summary>
    public static readonly ImmutableArray<string> GolemTower = [
        "WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_1",
        "WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_2",
        "WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_3",
        "WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_4",
        "WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_5",
    ];

    private static readonly ImmutableArray<ImmutableArray<string>> s_all = [GolemTower];

    /// <summary>Every zone of the gauntlet <paramref name="zone"/> belongs to; empty when it is not a gauntlet zone.</summary>
    public static ImmutableArray<string> ZonesOf(string? zone) {
        if (string.IsNullOrEmpty(zone)) {
            return [];
        }

        foreach (var gauntlet in s_all) {
            if (gauntlet.Contains(zone, StringComparer.OrdinalIgnoreCase)) {
                return gauntlet;
            }
        }

        return [];
    }

    /// <summary>True when a transfer from <paramref name="fromZone"/> into <paramref name="toZone"/> enters a gauntlet
    /// from outside it (a new trip: the gauntlet starts fresh).</summary>
    public static bool EntersFromOutside(string? fromZone, string? toZone) {
        var gauntlet = ZonesOf(toZone);

        return !gauntlet.IsEmpty && !gauntlet.Contains(fromZone ?? "", StringComparer.OrdinalIgnoreCase);
    }

}
