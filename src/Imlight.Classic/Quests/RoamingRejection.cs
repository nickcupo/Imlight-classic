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
 * ROAMING REJECTION
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: whether a creature a full duel circle turned away despawns. One
 * that walked in leaves (the stock lifecycle); one that has only just
 * spawned inside the circle stays out of the fight where it stands, and
 * fights the wizard once the duel is over.
 *
 * USAGE EXAMPLE:
 * if (RoamingRejection.Despawns(DateTime.UtcNow - bornUtc)) Entity.DeleteObject();
 *
 * NOTE:
 * The case: MS_Plague3_T1 (Summoning the Spirit). Its Diseased Water Spirit
 * respawns every 5 s at a node 100 units from the room's duel sigil. While
 * the wizard fought the room's two guards (the circle's cap for one wizard),
 * every respawn was turned away and deleted at once, the spawner made
 * another 5 s later, and the deletes, sent in the same instant as the new
 * objects, did not reach the client: 28 idle copies stood in the room and
 * none could be fought (playbot ms, finbot02, run p02, 2026-10-03).
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/03/2026
 */

using System;

namespace Imlight.Classic.Quests;

/// <summary>What a creature turned away by a full duel circle does.</summary>
public static class RoamingRejection {

    /// <summary>How old a creature must be to count as having walked into the circle rather than spawned in it.</summary>
    public static readonly TimeSpan FreshSpawn = TimeSpan.FromSeconds(3);

    /// <summary>
    /// True when the turned-away creature despawns: it is older than <see cref="FreshSpawn"/>, so it walked in.
    /// A creature that spawned inside the circle stays, out of the duel.
    /// </summary>
    /// <param name="age">The time since the creature spawned.</param>
    /// <returns>Whether to delete the creature.</returns>
    public static bool Despawns(TimeSpan age) => age >= FreshSpawn;

}
