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
 * TALK GOALS SHARED INSIDE AN INSTANCE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: inside a shared instance (a dungeon or tower copy that more than
 * one wizard is in), a talk goal (or a one-step "use" goal) one wizard
 * completes counts for every other wizard in that same copy who holds the
 * same quest and has that goal active. Out in the open world every goal stays
 * each player's own.
 *
 * EVIDENCE:
 *   - Owner ruling 2026-10-09 (relayed by the coordinator): "talking to an npc
 *     counting for everyone was only true in instances, not out in the
 *     world." Treated as 2010 behaviour (the owner's own memory of the game);
 *     no dated wiki text was found either way (Quests oldid 113449,
 *     2010-10-12, says nothing about goal credit in groups).
 *   - A "use" goal with a tally (collect 5 of something) stays per player:
 *     the ruling names talking only, and each player has their own objects
 *     to click.
 *
 * USAGE EXAMPLE:
 * if (InstanceGoalSharing.SharesGoal(isTalk, isUse, tally)
 *     && InstanceGoalSharing.SamePlace(zoneA, ownerA, deedA, zoneB, ownerB, deedB)) { ... }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

#nullable enable

using System;

namespace Imlight.Classic.Quests;

/// <summary>CLASSIC: which goals one wizard's progress completes for the others in a shared instance.</summary>
public static class InstanceGoalSharing {

    /// <summary>
    /// True for a place several wizards can share as one copy: an instance (a sigil run, a dungeon or tower copy, a
    /// friend's copy), not a public zone (instance owner 0) and not a house.
    /// </summary>
    public static bool IsInstance(ulong instanceOwnerId, ulong housingDeedId) => instanceOwnerId != 0 && housingDeedId == 0;

    /// <summary>True when both wizards stand in the same zone of the same instance copy.</summary>
    public static bool SamePlace(string? zoneA, ulong ownerA, ulong deedA, string? zoneB, ulong ownerB, ulong deedB)
        => IsInstance(ownerA, deedA) && IsInstance(ownerB, deedB) && ownerA == ownerB
           && !string.IsNullOrEmpty(zoneA) && string.Equals(zoneA, zoneB, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a goal one wizard completes for the others there: a talk (persona) goal, or a use goal that needs a
    /// single use.
    /// </summary>
    public static bool SharesGoal(bool isTalk, bool isUse, int tally) => isTalk || (isUse && tally <= 1);

}
