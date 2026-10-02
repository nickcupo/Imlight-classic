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
 * A wizard who stays inside the radius is tried again every few seconds
 * (Retry), as a try can come to nothing.
 *
 * TODO:
 * - KingsIsle's retry interval is not known; 5 s is ours.
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

    /// <summary>How long a creature waits before it tries again to fight a wizard still inside its radius.</summary>
    public static readonly System.TimeSpan RetryAfter = System.TimeSpan.FromSeconds(5);

    /// <summary>
    /// True when a creature should try again to fight a wizard who is still inside its radius: the wizard is free (not in
    /// a duel or its grace) and the last try is at least <see cref="RetryAfter"/> old. A try can come to nothing (the
    /// wizard was pulled into another creature's duel first), and stock Imlight never tried again while the wizard stayed
    /// near: a soldier on the Crimson Fields beside the last fight never engaged (Battle of Evermore).
    /// </summary>
    public static bool Retry(bool isMonster, bool inGrace, bool inDuel, System.DateTime lastTryUtc, System.DateTime nowUtc)
        => isMonster && !inGrace && !inDuel && nowUtc - lastTryUtc >= RetryAfter;

}
