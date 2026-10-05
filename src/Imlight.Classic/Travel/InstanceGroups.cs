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
 * CLASSIC INSTANCE GROUPS AND THE 2009 RESET RULE
 * ========================================================================
 *
 * PURPOSE:
 * The dungeons of the active profile (Rules.InstanceResetRules) and the
 * decisions of the 2009 reset rule, as pure functions:
 *   - a dungeon resets when its wizard logs out, leaves through its entrance
 *     or goes into another dungeon; leaving any other way keeps it for the
 *     return window (client Help_Instances00);
 *   - a gauntlet resets whenever its wizard leaves it (Help_Instances01);
 *   - an empty dungeon resets after the empty lifetime; nothing resets while
 *     anyone is inside any of its zones (Fandom The Vault of Ice rev 55817,
 *     2010-01-03: the level resets once everyone has left);
 *   - every zone of a dungeon resets together, and moving between its zones is
 *     not leaving it (Sunken City rev 67077, 2010-04-19).
 *
 * USAGE EXAMPLE:
 * var group = InstanceGroups.GroupOf(zone);
 * if (InstanceResetPolicy.ResetsOnEntry(group, fromZone, ownCopy, occupied, departure, now, window)) { ... }
 *
 * NOTE:
 * The runtime state (who left which dungeon when, who is inside) lives in
 * Imlight.CoreLib.Classic.InstanceResets; this file holds no state but the
 * active table.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Immutable;
using Imlight.Classic.Rules;

namespace Imlight.Classic.Travel;

/// <summary>How a wizard left a dungeon.</summary>
public enum DepartureWay {

    /// <summary>Through the dungeon's own exit (a door or trigger inside it): "leaving through the Dungeon entrance".</summary>
    Exit,

    /// <summary>Any other way: Go Home, the dorm, a world door, a teleport to a friend.</summary>
    Elsewhere,

}

/// <summary>A wizard's last departure from a dungeon copy (<paramref name="Owner"/>: the copy's instance owner or run).</summary>
public sealed record InstanceDeparture(ulong Owner, string GroupId, DateTime LeftUtc, bool Resumable);

/// <summary>
/// CLASSIC: the active profile's dungeons.
/// </summary>
public static class InstanceGroups {

    private static volatile InstanceResetRules s_rules = InstanceResetRules.BuiltIn;

    /// <summary>The active rules (the built-in Golem Tower table until a profile names a file).</summary>
    public static InstanceResetRules Rules => s_rules;

    /// <summary>Uses <paramref name="rules"/>, or the built-in table for null.</summary>
    public static void Use(InstanceResetRules? rules) => s_rules = rules ?? InstanceResetRules.BuiltIn;

    /// <summary>The dungeon <paramref name="zone"/> belongs to, or null.</summary>
    public static InstanceGroup? GroupOf(string? zone) => s_rules.GroupOf(zone);

    /// <summary>Every zone of the dungeon <paramref name="zone"/> belongs to; empty when it is not a dungeon zone.</summary>
    public static ImmutableArray<string> ZonesOf(string? zone) => GroupOf(zone)?.Zones ?? [];

    /// <summary>True when both zones are zones of the same dungeon.</summary>
    public static bool SameGroup(string? a, string? b) => GroupOf(a) is { } group && group.Contains(b);

    /// <summary>True when a transfer from <paramref name="fromZone"/> into <paramref name="toZone"/> enters a dungeon from
    /// outside it.</summary>
    public static bool EntersFromOutside(string? fromZone, string? toZone)
        => GroupOf(toZone) is { } group && !group.Contains(fromZone);

    /// <summary>The key a copy's zones are kept and reset under: the dungeon's id, or the zone itself outside any.</summary>
    public static string GroupKey(string zone) => GroupOf(zone)?.Id ?? zone;

}

/// <summary>
/// CLASSIC: the decisions of the 2009 reset rule (see the file header).
/// </summary>
public static class InstanceResetPolicy {

    /// <summary>
    /// The record of a wizard leaving <paramref name="group"/> (copy <paramref name="owner"/>) by <paramref name="way"/>:
    /// a dungeon left any other way than its exit may be resumed; a gauntlet never.
    /// </summary>
    public static InstanceDeparture Departure(InstanceGroup group, ulong owner, DepartureWay way, DateTime nowUtc)
        => new(owner, group.Id, nowUtc, group.Kind == InstanceKind.Dungeon && way == DepartureWay.Elsewhere);

    /// <summary>True when <paramref name="departure"/> lets its wizard return to copy <paramref name="owner"/> of
    /// <paramref name="group"/> as it was.</summary>
    public static bool CanResume(InstanceDeparture? departure, InstanceGroup group, ulong owner, DateTime nowUtc, TimeSpan window)
        => departure is { Resumable: true } d && group.Kind == InstanceKind.Dungeon
           && string.Equals(d.GroupId, group.Id, StringComparison.Ordinal) && d.Owner == owner
           && nowUtc - d.LeftUtc <= window;

    /// <summary>
    /// True when a wizard entering <paramref name="group"/> from <paramref name="fromZone"/> should find a fresh copy:
    /// the entry is from outside the dungeon, into the wizard's own copy (<paramref name="ownCopy"/>; a friend's copy or
    /// a sigil run is joined as it is), nobody is inside it (<paramref name="occupied"/>), and the wizard did not leave
    /// it "any other way" within the return window.
    /// </summary>
    public static bool ResetsOnEntry(InstanceGroup? group, string? fromZone, bool ownCopy, bool occupied,
        InstanceDeparture? departure, ulong owner, DateTime nowUtc, TimeSpan window)
        => group is not null && !group.Contains(fromZone) && ownCopy && !occupied
           && !CanResume(departure, group, owner, nowUtc, window);

    /// <summary>
    /// True when a wizard logging in inside <paramref name="group"/> should find a fresh copy: logging out reset it
    /// (Help_Instances00), unless it is someone else's copy, someone is inside, or a fight there holds a seat.
    /// </summary>
    public static bool ResetsOnLogin(InstanceGroup? group, bool ownCopy, bool occupied)
        => group is not null && ownCopy && !occupied;

    /// <summary>
    /// True when a transfer from <paramref name="fromZone"/> to <paramref name="toZone"/> leaves the dungeon of
    /// <paramref name="fromZone"/> (a move between its zones does not).
    /// </summary>
    public static bool Leaves(InstanceGroup? fromGroup, string? toZone) => fromGroup is not null && !fromGroup.Contains(toZone);

}
