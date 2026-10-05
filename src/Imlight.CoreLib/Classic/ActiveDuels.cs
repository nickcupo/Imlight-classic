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
 * ACTIVE DUELS AND HELD SEATS
 * ========================================================================
 *
 * PURPOSE:
 * A server-wide view of the duels in progress (for safe restarts and the
 * admin dashboard) and of the seats held for wizards who dropped mid-fight
 * (so their next login goes back into the same dungeon instance).
 *
 * NOTE:
 * Duel actors write these; readers take snapshots. Entries hold no actor
 * state beyond identifiers.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Classic;

/// <summary>A duel in progress, as the dashboard shows it.</summary>
public sealed record ActiveDuelInfo(ulong DuelId, string Zone, bool Pvp, int Wizards, int Creatures, int HeldSeats,
    DateTime StartedUtc, IReadOnlyList<ulong> CharacterIds);

/// <summary>A seat held for a wizard who dropped mid-fight.</summary>
public sealed record HeldSeat(ulong CharacterId, string Zone, ulong InstanceOwnerId, DateTime ExpiresUtc);

/// <summary>
/// Duels in progress and seats held for dropped wizards.
/// </summary>
public static class ActiveDuels {

    private static readonly ConcurrentDictionary<ulong, ActiveDuelInfo> s_duels = new();
    private static readonly ConcurrentDictionary<ulong, HeldSeat> s_held = new();

    public static void Update(ActiveDuelInfo info) => s_duels[info.DuelId] = info;

    public static void Remove(ulong duelId) => s_duels.TryRemove(duelId, out _);

    /// <summary>The duels in progress now.</summary>
    public static IReadOnlyList<ActiveDuelInfo> Snapshot() => [.. s_duels.Values.OrderBy(duel => duel.StartedUtc)];

    /// <summary>The number of wizards in a duel now (held seats count: their fight is not over).</summary>
    public static int WizardsInDuels => s_duels.Values.Sum(duel => duel.Wizards);

    public static void Hold(HeldSeat seat) => s_held[seat.CharacterId] = seat;

    public static void Release(ulong characterId) => s_held.TryRemove(characterId, out _);

    /// <summary>The held seat of <paramref name="characterId"/>, if it has not expired.</summary>
    public static HeldSeat? HeldFor(ulong characterId, DateTime nowUtc)
        => s_held.TryGetValue(characterId, out var seat) && seat.ExpiresUtc > nowUtc ? seat : null;

    /// <summary>
    /// The instance owner a login should use to reach <paramref name="zone"/>: the owner of the instance that holds
    /// the wizard's seat, or the wizard.
    /// </summary>
    public static ulong InstanceOwnerForLogin(ulong characterId, string zone, DateTime nowUtc)
        => HeldFor(characterId, nowUtc) is { InstanceOwnerId: not 0 } seat
           && string.Equals(seat.Zone, zone, StringComparison.OrdinalIgnoreCase)
            ? seat.InstanceOwnerId
            : characterId;

    /// <summary>
    /// CLASSIC: true when a seat is held (not expired) in instance <paramref name="instanceOwnerId"/> in a zone
    /// <paramref name="zoneMatches"/> accepts: a fight there is not over, so its dungeon must not be reset.
    /// </summary>
    public static bool AnyHeldSeat(ulong instanceOwnerId, Func<string?, bool> zoneMatches, DateTime nowUtc, ulong exceptCharacterId = 0)
        => s_held.Values.Any(seat => seat.InstanceOwnerId == instanceOwnerId && seat.ExpiresUtc > nowUtc && zoneMatches(seat.Zone)
                                     && (exceptCharacterId == 0 || seat.CharacterId != exceptCharacterId));

    internal static void ClearForTests() {
        s_duels.Clear();
        s_held.Clear();
    }

}
