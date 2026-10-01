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
 * OWNED MINION CONTROL
 * ========================================================================
 * 
 * PURPOSE:
 * Validates optional owner orders independently of creature AI and combat resolution.
 *
 * USAGE EXAMPLE:
 * Used by CombatDuelComponent during planning.
 *
 * NOTE:
 * Owner and minion keys use object identity, not reusable sigil slots.
 *
 * TODO:
 *
 * Created by: Nick with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System.Collections.Generic;
using System.Linq;
using static Imlight.CoreLib.Shared.Packets.COMBAT_106_PROTOCOL;

namespace Imlight.CoreLib.Game.Combat;

internal readonly record struct OwnedMinionOrder(byte MoveType, byte SpellSelection, uint SpellTarget, object TargetIdentity = null);

internal readonly record struct OwnedMinionAccess(
    bool SameDuel, bool SameRound, bool Planning, bool OwnerPresent, bool OwnerAlive,
    bool OwnerIsMyth, bool MinionPresent, bool MinionAlive, bool Owned, bool SupportedSummon);

internal enum OwnedMinionTarget {
    None, Self, Enemy, Friend, FriendNotSelf, OwnMinion, FriendMinion, EnemyMinion, AnyMinion,
}

internal sealed class OwnedMinionControl {
    private readonly HashSet<object> _owners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, uint> _requests = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, OwnedMinionOrder> _orders = new(ReferenceEqualityComparer.Instance);

    internal static OwnedMinionStatus ValidateAccess(OwnedMinionAccess access, bool allMinionsQuery) {
        if (!access.SameDuel) return OwnedMinionStatus.InvalidDuel;
        if (!access.SameRound) return OwnedMinionStatus.InvalidRound;
        if (!access.Planning) return OwnedMinionStatus.NotPlanning;
        if (!access.OwnerPresent || !access.OwnerAlive) return OwnedMinionStatus.InvalidOwner;
        if (!access.OwnerIsMyth) return OwnedMinionStatus.NotMyth;
        if (allMinionsQuery) return OwnedMinionStatus.Accepted;
        if (!access.MinionPresent || !access.MinionAlive || !access.Owned) return OwnedMinionStatus.NotOwnedMinion;
        return access.SupportedSummon ? OwnedMinionStatus.Accepted : OwnedMinionStatus.UnsupportedSummon;
    }

    internal static bool ValidTarget(OwnedMinionTarget requirement, bool provided, bool live,
                                    bool sameTeam, bool self, bool minion, bool owned) {
        if (!provided) return requirement is OwnedMinionTarget.None or OwnedMinionTarget.Self;
        if (!live) return false;
        return requirement switch {
            OwnedMinionTarget.None => true,
            OwnedMinionTarget.Self => self,
            OwnedMinionTarget.Enemy => !sameTeam,
            OwnedMinionTarget.Friend => sameTeam,
            OwnedMinionTarget.FriendNotSelf => sameTeam && !self,
            OwnedMinionTarget.OwnMinion => minion && owned,
            OwnedMinionTarget.FriendMinion => minion && sameTeam,
            OwnedMinionTarget.EnemyMinion => minion && !sameTeam,
            OwnedMinionTarget.AnyMinion => minion,
            _ => false,
        };
    }

    internal bool IsOptedIn(object owner) => _owners.Contains(owner);
    internal void OptIn(object owner) => _owners.Add(owner);
    internal void Disable(object owner) {
        _owners.Remove(owner);
    }
    internal bool IsNewRequest(object owner, uint request) => !_requests.TryGetValue(owner, out var last) || request > last;
    internal void RecordRequest(object owner, uint request) => _requests[owner] = request;
    internal bool HasOrder(object minion) => _orders.ContainsKey(minion);
    internal bool TryGetOrder(object minion, out OwnedMinionOrder order) => _orders.TryGetValue(minion, out order);
    internal void SetOrder(object minion, OwnedMinionOrder order) => _orders[minion] = order;
    internal void Withdraw(object minion) => _orders.Remove(minion);
    internal bool AllOrdered(IEnumerable<(object Owner, object Minion)> liveOwnedMinions)
        => liveOwnedMinions.All(pair => !IsOptedIn(pair.Owner) || HasOrder(pair.Minion));

    internal void FinishRound() => _orders.Clear();

    internal void NewRound() {
        FinishRound();
        _requests.Clear();
    }

    internal void Clear() {
        NewRound();
        _owners.Clear();
    }
}
