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
 * CLASSIC QUEST ENGINE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: KingsIsle's ReqEncounterComplete zone-trigger requirement ("the
 * named encounter is complete"). Its encounter names are quest names, and the
 * check reads as "the wizard has completed that quest".
 *
 * USAGE EXAMPLE:
 * EncounterComplete.Met(name => wizard.HasCompletedQuest(name), "MS-DTH3-C03-001")
 *
 * NOTE:
 * The class (hash 1516738250, "class ReqEncounterComplete")
 * and its one std::string property m_encounterName (3079383855) are not in the
 * r806919 or r756936 type dumps; the names were recovered by hashing
 * dictionary words from the dumps against the raw triggers.xml. Before, it
 * loaded as null and passed. Its nine r806919 uses all read as "the previous
 * quest is done":
 * - the Tree of Life spirit-world portal (Question of Faith; then Tree of Life
 *   uses the tree);
 * - the Tree of Life healing (Tree of Life);
 * - the Temple of Storms exit (Get Smart);
 * - the Crimson Fields back gate (Battle of Evermore, before Warlord Katsumori);
 * - the Retreat keymaster door (KT-SPH5-C02-003);
 * - the Ironworks gate 2 (MB-AIR3-C01-002);
 * - the Katz Lab puzzles 1 and 2 (The Right Combination, then The Second Door,
 *   whose waypoint the puzzle opens).
 *
 * TODO:
 * - KingsIsle's own meaning of an "encounter" (zone m_encounterNames) is not documented; a zone's encounter is its
 *   dungeon's first quest (MS_Death_Zone3_AncientTree: MS-DTH3-C02-001).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System;

namespace Imlight.Classic.Quests;

/// <summary>ReqEncounterComplete's ids and check.</summary>
public static class EncounterComplete {

    /// <summary>The hash of "class ReqEncounterComplete".</summary>
    public const uint ClassHash = 1516738250;

    /// <summary>m_encounterName (std::string).</summary>
    public const uint EncounterNameHash = 3079383855;

    /// <summary>True when the wizard has completed the quest the encounter names; an empty name is never complete.</summary>
    public static bool Met(Func<string, bool> hasCompletedQuest, string? encounterName)
        => !string.IsNullOrWhiteSpace(encounterName) && hasCompletedQuest(encounterName);

}
