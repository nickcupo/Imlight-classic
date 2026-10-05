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
 * ZONE TRIGGERS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: each zone instance's trigger state as the rest of the zone reads
 * it: which triggers are armed (ReqTriggerState) and which trigger-managed
 * objects are there (RenderComponent, for a wizard who joins mid-way).
 *
 * USAGE EXAMPLE:
 * ZoneTriggerTables.Find(zoneRef)?.IsPresent("Gate of Paulson", playerActor);
 *
 * NOTE:
 * Only ZoneTriggerSupervisor writes a table; zone objects and requirement
 * handlers read it from their own actors, so every access takes the lock.
 * The state scope is the whole zone in an instanced zone (a dungeon: what
 * one wizard opens is open for the group, and a wizard who rejoins finds
 * it as it was) and each player in a public zone.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imlight.Classic.Quests;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// One zone instance's trigger arming and trigger-object presence.
/// </summary>
internal sealed class ZoneTriggerTable {

    private readonly Lock _lock = new();
    private readonly ZoneTriggerPlan _plan;
    private readonly IActorRef _sharedScope;
    private readonly bool _shared;
    private readonly TriggerObjectPresence<IActorRef> _presence;
    private readonly Dictionary<(IActorRef Scope, int Index), bool> _armed = [];
    private readonly Dictionary<string, int[]> _byName;
    private readonly Dictionary<IActorRef, HashSet<string>> _seenEvents = [];

    /// <summary>
    /// Creates the table of a zone instance.
    /// </summary>
    /// <param name="plan">The zone's trigger plan.</param>
    /// <param name="zone">The zone actor: the scope of an instanced zone.</param>
    /// <param name="shared">True for an instanced zone, whose state is shared by everyone in it.</param>
    internal ZoneTriggerTable(ZoneTriggerPlan plan, IActorRef zone, bool shared) {
        _plan = plan;
        _sharedScope = zone;
        _shared = shared;
        _presence = new TriggerObjectPresence<IActorRef>(plan.Liveness.Objects);
        _byName = Enumerable.Range(0, plan.Triggers.Count)
            .GroupBy(i => (string) plan.Triggers[i].m_triggerName ?? "", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>The zone's trigger plan.</summary>
    internal ZoneTriggerPlan Plan => _plan;

    /// <summary>True when the zone's trigger state is shared by everyone in it (an instanced zone).</summary>
    internal bool Shared => _shared;

    /// <summary>The state scope of a player's event: the zone instance, or the player in a public zone.</summary>
    internal IActorRef ScopeOf(IActorRef player) => _shared || player is null ? _sharedScope : player;

    /// <summary>True when the zone's triggers manage an object with this tag.</summary>
    internal bool Manages(string tag) => _presence.Manages(tag);

    /// <summary>True when the trigger (plan index) is armed for the player's scope.</summary>
    internal bool IsArmed(int index, IActorRef player) {
        lock (_lock) {
            return ArmedLocked(index, ScopeOf(player));
        }
    }

    /// <summary>
    /// ReqTriggerState: true when a trigger of that name is armed for the player's scope; null for a name the zone
    /// does not have.
    /// </summary>
    internal bool? IsArmedNamed(string name, IActorRef player) {
        if (string.IsNullOrEmpty(name) || !_byName.TryGetValue(name, out var indices)) {
            return null;
        }

        lock (_lock) {
            var scope = ScopeOf(player);

            return indices.Any(i => ArmedLocked(i, scope));
        }
    }

    /// <summary>True when the object is there for the player (its scope); true for a tag the zone does not manage.</summary>
    internal bool IsPresent(string tag, IActorRef player) {
        if (!_presence.Manages(tag)) {
            return true;
        }

        lock (_lock) {
            var scope = ScopeOf(player);

            return _presence.IsPresent(tag, scope, i => ArmedLocked(i, scope));
        }
    }

    /// <summary>
    /// Records a trigger's new arming in the player's scope; returns the objects whose presence changed.
    /// </summary>
    internal IReadOnlyList<(string Tag, bool Present)> SetArmed(int index, IActorRef player, bool armed) {
        if (index < 0 || index >= _plan.Triggers.Count) {
            return [];
        }

        lock (_lock) {
            var scope = ScopeOf(player);
            var tags = _presence.TagsOwnedBy(index);
            var before = Snapshot(tags, scope);
            if (armed == _plan.Liveness.StartsArmed[index]) {
                _armed.Remove((scope, index));
            } else {
                _armed[(scope, index)] = armed;
            }

            _presence.OwnerChanged(index, scope);

            return Diff(tags, scope, before);
        }
    }

    /// <summary>
    /// ResRemoveTriggerObject / ResAddTriggerObject in the player's scope; returns the objects whose presence changed.
    /// </summary>
    internal IReadOnlyList<(string Tag, bool Present)> SetPresent(string tag, IActorRef player, bool present) {
        if (!_presence.Manages(tag)) {
            return [];
        }

        lock (_lock) {
            var scope = ScopeOf(player);
            string[] tags = [tag];
            var before = Snapshot(tags, scope);
            if (present) {
                _presence.Add(tag, scope);
            } else {
                _presence.Remove(tag, scope);
            }

            return Diff(tags, scope, before);
        }
    }

    /// <summary>
    /// Records an event posted in the player's scope; false when the scope had seen it already.
    /// </summary>
    internal bool MarkSeen(string eventName, IActorRef player) {
        lock (_lock) {
            var scope = ScopeOf(player);
            if (!_seenEvents.TryGetValue(scope, out var seen)) {
                _seenEvents[scope] = seen = new HashSet<string>(StringComparer.Ordinal);
            }

            return seen.Add(eventName);
        }
    }

    /// <summary>True when the event was posted in the player's scope.</summary>
    internal bool HasSeen(string eventName, IActorRef player) {
        lock (_lock) {
            return _seenEvents.TryGetValue(ScopeOf(player), out var seen) && seen.Contains(eventName);
        }
    }

    private bool ArmedLocked(int index, IActorRef scope)
        => index >= 0 && index < _plan.Triggers.Count
            && (_armed.TryGetValue((scope, index), out var armed) ? armed : _plan.Liveness.StartsArmed[index]);

    private Dictionary<string, bool> Snapshot(IEnumerable<string> tags, IActorRef scope)
        => tags.Distinct(StringComparer.Ordinal)
            .ToDictionary(t => t, t => _presence.IsPresent(t, scope, i => ArmedLocked(i, scope)), StringComparer.Ordinal);

    private List<(string, bool)> Diff(IEnumerable<string> tags, IActorRef scope, Dictionary<string, bool> before)
        => [.. Snapshot(tags, scope).Where(after => before[after.Key] != after.Value).Select(after => (after.Key, after.Value))];

}

/// <summary>
/// The trigger tables of the running zone instances, by zone actor.
/// </summary>
internal static class ZoneTriggerTables {

    private static readonly ConcurrentDictionary<IActorRef, ZoneTriggerTable> s_tables = new();

    /// <summary>The zone's table, or null.</summary>
    internal static ZoneTriggerTable Find(IActorRef zone)
        => zone is not null && s_tables.TryGetValue(zone, out var table) ? table : null;

    /// <summary>Creates (or replaces, on a reload) the zone's table.</summary>
    internal static ZoneTriggerTable Create(IActorRef zone, ZoneTriggerPlan plan, bool shared) {
        var table = new ZoneTriggerTable(plan, zone, shared);
        s_tables[zone] = table;

        return table;
    }

    internal static void Remove(IActorRef zone) {
        if (zone is not null) {
            s_tables.TryRemove(zone, out _);
        }
    }

}
