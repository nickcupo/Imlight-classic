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
 * CLASSIC: when a dueling creature's proximity check counts a wizard as
 * inside its radius. A wizard in a duel or in the after-duel grace is not
 * attacked, and is not counted either, so the first move after the grace
 * aggroes a creature the wizard is standing beside.
 *
 * USAGE EXAMPLE:
 * if (!ProximityAggro.Deferred(isMonster, inGrace, inDuel)) inRange.Add(player);
 *
 * NOTE:
 * The case: Ideyoshi's death trigger in MS_Plague2_PalaceInterior spawns the
 * Plague Oni beside the wizard as the duel ends (proximity 350). Stock
 * Imlight counted the wizard inside the radius during the grace, so the Oni
 * never fought until the wizard left the room and came back (ms.md, Release).
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

namespace Imlight.Classic.Quests;

/// <summary>The proximity-aggro bookkeeping rule.</summary>
public static class ProximityAggro {

    /// <summary>
    /// True when a dueling creature should not yet count the wizard as inside its radius: the wizard is in a duel or
    /// its after-duel grace, so the creature did not attack and must look again on the next move.
    /// </summary>
    public static bool Deferred(bool isMonster, bool inGrace, bool inDuel)
        => isMonster && (inGrace || inDuel);

}
