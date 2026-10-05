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
 * CLASSIC INSTANCE RESETS (RUNTIME)
 * ========================================================================
 *
 * PURPOSE:
 * Applies the 2009 dungeon reset rule (Imlight.Classic.Travel.InstanceGroups,
 * InstanceResetPolicy) to live wizards: remembers how each wizard last left a
 * dungeon, tells a transfer or a login whether it starts a fresh copy, finds
 * the sigil run a wizard may go back to, and answers whether anyone is still
 * inside a copy (online players, wizards on their way in, held fight seats).
 *
 * USAGE EXAMPLE:
 * if (InstanceResets.ResetOnEntry(charId, wizard.Zone, dest, owner, now)) message.ResetInstance = true;   // ZoneService
 * InstanceResets.NoteTransfer(charId, fromZone, fromOwner, dest, destOwner, keepInstance, now);          // after it
 * if (InstanceResets.ResetOnLogin(charId, zone, owner, now)) zoneMsg.ResetInstance = true;               // AttachService
 *
 * NOTE:
 * A reset never drops a copy anyone is in: the occupancy check counts every
 * online wizard (ambient wizards too) in a zone of the copy, every wizard whose
 * transfer into it is under way, and every held seat of a fight there.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Rules;
using Imlight.Classic.Travel;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// CLASSIC: the live side of the 2009 dungeon reset rule.
/// </summary>
internal static class InstanceResets {

    /// <summary>How long a transfer is remembered for the attach that follows it.</summary>
    internal static readonly TimeSpan TransferLifetime = TimeSpan.FromMinutes(2);

    private sealed record Transfer(string Zone, ulong Owner, DateTime ExpiresUtc);

    private static readonly ConcurrentDictionary<ulong, InstanceDeparture> s_departures = new();
    private static readonly ConcurrentDictionary<ulong, Transfer> s_transfers = new();

    /// <summary>True when the rule applies (a classic profile is active).</summary>
    internal static bool IsActive => Imlight.Classic.ClassicRuntime.IsActive;

    /// <summary>
    /// True when a transfer of <paramref name="charId"/> from <paramref name="fromZone"/> into <paramref name="toZone"/>
    /// (copy <paramref name="owner"/>) should start the dungeon fresh.
    /// </summary>
    internal static bool ResetOnEntry(ulong charId, string? fromZone, string? toZone, ulong owner, DateTime nowUtc) {
        var group = InstanceGroups.GroupOf(toZone);
        if (group is null || group.Contains(fromZone)) {
            return false;
        }

        s_departures.TryGetValue(charId, out var departure);

        return InstanceResetPolicy.ResetsOnEntry(group, fromZone, owner == charId, IsOccupied(owner, group, charId, nowUtc),
            departure, owner, nowUtc, InstanceGroups.Rules.ReturnWindow);
    }

    /// <summary>
    /// Records a transfer the server accepted: leaving a dungeon notes how (a door or trigger inside it is its exit;
    /// anything else is "another way"), entering another dungeon forgets the old one, and the transfer is kept for the
    /// attach that follows it.
    /// </summary>
    internal static void NoteTransfer(ulong charId, string? fromZone, ulong fromOwner, string toZone, ulong toOwner,
            bool keepInstance, DateTime nowUtc) {
        s_transfers[charId] = new Transfer(toZone, toOwner, nowUtc + TransferLifetime);

        var from = InstanceGroups.GroupOf(fromZone);
        var to = InstanceGroups.GroupOf(toZone);
        if (from is not null && fromOwner != 0 && InstanceResetPolicy.Leaves(from, toZone)) {
            s_departures[charId] = InstanceResetPolicy.Departure(from, fromOwner,
                keepInstance ? DepartureWay.Exit : DepartureWay.Elsewhere, nowUtc);
        }
        else if (to is not null && s_departures.TryGetValue(charId, out var old)
                 && !string.Equals(old.GroupId, to.Id, StringComparison.Ordinal)) {
            // "Going to another Dungeon will reset the Dungeon you started."
            s_departures.TryRemove(charId, out _);
        }
    }

    /// <summary>
    /// The attach of <paramref name="charId"/> into <paramref name="zone"/> (copy <paramref name="owner"/>): true when
    /// it is a login (not the end of a transfer) inside a dungeon that logging out has reset. A login also forgets the
    /// wizard's last departure ("logging out ... will reset the Dungeon you started").
    /// </summary>
    internal static bool ResetOnLogin(ulong charId, string? zone, ulong owner, DateTime nowUtc) {
        if (s_transfers.TryRemove(charId, out var transfer) && transfer.ExpiresUtc > nowUtc
                && string.Equals(transfer.Zone, zone, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        s_departures.TryRemove(charId, out _);
        var group = InstanceGroups.GroupOf(zone);

        return group is not null
            && InstanceResetPolicy.ResetsOnLogin(group, owner == charId, IsOccupied(owner, group, charId, nowUtc));
    }

    /// <summary>
    /// The sigil run <paramref name="charId"/> may go back to when starting a sigil into <paramref name="toZone"/>: the
    /// run of that dungeon the wizard left "another way" within the return window, while it still exists; else 0.
    /// </summary>
    internal static ulong ResumableRun(ulong charId, string? toZone, DateTime nowUtc) {
        if (InstanceGroups.GroupOf(toZone) is not { } group || !s_departures.TryGetValue(charId, out var departure)
                || !GroupInstances.IsRun(departure.Owner)) {
            return 0;
        }

        return InstanceResetPolicy.CanResume(departure, group, departure.Owner, nowUtc, InstanceGroups.Rules.ReturnWindow)
            ? departure.Owner
            : 0;
    }

    /// <summary>The last departure of <paramref name="charId"/>, if any (tests and logs).</summary>
    internal static InstanceDeparture? DepartureOf(ulong charId) => s_departures.GetValueOrDefault(charId);

    /// <summary>
    /// True when anyone but <paramref name="exceptCharId"/> is inside copy <paramref name="owner"/> of
    /// <paramref name="group"/>: online in one of its zones, on the way into one, or holding a seat in a fight there.
    /// </summary>
    internal static bool IsOccupied(ulong owner, InstanceGroup group, ulong exceptCharId, DateTime nowUtc) {
        if (owner == 0) {
            return false;
        }

        foreach (var player in OnlinePlayerCollection.GetOnlinePlayers()) {
            if (player.CharacterId != exceptCharId && player.InstanceOwnerId == owner && group.Contains(player.CurrentZone)) {
                return true;
            }
        }

        foreach (var (charId, transfer) in s_transfers) {
            if (charId != exceptCharId && transfer.Owner == owner && transfer.ExpiresUtc > nowUtc && group.Contains(transfer.Zone)) {
                return true;
            }
        }

        return ActiveDuels.AnyHeldSeat(owner, group.Contains, nowUtc);
    }

    /// <summary>
    /// True when an empty copy of <paramref name="zone"/> in instance <paramref name="owner"/> is reset after the empty
    /// lifetime: every zone of a sigil run, and the dungeon zones of a wizard's own copy (other own copies, such as the
    /// dorm or a shop, are kept).
    /// </summary>
    internal static bool TracksEmptyCopy(ulong owner, string zone)
        => IsActive && owner != 0 && (GroupInstances.IsRun(owner) || InstanceGroups.GroupOf(zone) is not null);

    /// <summary>The zones a reset of the copy holding <paramref name="zone"/> drops: its dungeon's, or the zone alone.</summary>
    internal static IReadOnlyList<string> ZonesToDrop(string zone)
        => IsActive && InstanceGroups.GroupOf(zone) is { } group ? group.Zones : [zone];

    internal static void ClearForTests() {
        s_departures.Clear();
        s_transfers.Clear();
    }

}
