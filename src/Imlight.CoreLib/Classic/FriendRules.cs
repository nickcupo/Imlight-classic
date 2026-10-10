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
 * FRIEND RULES
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: what the friends list lets a client do (security audit 2026-10-04).
 *   Teleport to a player (MSG_GOTOPLAYER): only to a friend (2009: "Teleport to Friend" on the friends list), never
 *     to oneself, never out of a duel, never into a minigame and never into a gauntlet (Quests oldid 21718 and
 *     113449). A friend's dungeon run or dorm is still reached
 *     through the existing instance rules (Classic.GroupInstances).
 *   Stats (MSG_BUDDYSTATS): oneself, a friend, or a wizard in the same zone (the client asks when a wizard is
 *     selected); anyone else is refused, and the lookups are rate-limited per session.
 *   Friend requests: not to or from a wizard who has ignored the other. The ignore list is capped at MaxIgnored.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

#nullable enable

using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

internal enum GoToPlayerRefusal {
    None,
    Self,
    NotFriend,
    InDuel,
    Minigame,
}

internal static class FriendRules {

    internal const int MaxIgnored = 100;

    /// <summary>A live friendship: a relationship neither ended nor blocked.</summary>
    internal static bool IsFriend(Relationship? relationship) => relationship is { Blocked: false, IsBrokenUp: false };

    internal static GoToPlayerRefusal CheckGoTo(ulong self, ulong target, Relationship? relationship, bool inDuel,
                                                bool targetInMinigame) {
        if (target == self) return GoToPlayerRefusal.Self;
        if (!IsFriend(relationship)) return GoToPlayerRefusal.NotFriend;
        if (inDuel) return GoToPlayerRefusal.InDuel;
        if (targetInMinigame) return GoToPlayerRefusal.Minigame;

        return GoToPlayerRefusal.None;
    }

    /// <summary>The client's own words when the friend is in a gauntlet (Teleportation_Gauntlet).</summary>
    internal const string GauntletBusyMessage = "Your friend is busy right now.";

    /// <summary>
    /// CLASSIC: true when a friend in <paramref name="zone"/> is in a gauntlet of the active profile (Golem Tower,
    /// Briskbreeze Tower, the gauntlet rooms of classic-data/rules/instance-resets-2009.yaml) and cannot be teleported to.
    /// </summary>
    internal static bool TargetInGauntlet(bool classic, string? zone)
        => classic && Imlight.Classic.Travel.InstanceGroups.GroupOf(zone) is { Kind: Imlight.Classic.Rules.InstanceKind.Gauntlet };

    internal static bool MayViewStats(ulong self, ulong target, Relationship? relationship, bool targetInSameZone)
        => target == self || IsFriend(relationship) || targetInSameZone;

    internal static bool MayIgnoreAnother(int ignoredCount) => ignoredCount < MaxIgnored;

}
