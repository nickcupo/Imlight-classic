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
 * ZONE OBJECT STATES
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: each zone instance's state objects (the Temple of Storms
 * obelisks) and their current state, shared by the objects that change
 * them, the trigger results that set them and the ReqState checks of the
 * zone's triggers. Also the tags the zone's triggers listen to by state
 * event, so an object only offers a click when a trigger cares.
 *
 * USAGE EXAMPLE:
 * ZoneObjectStates.For(zoneRef).Set("Bug1", "Idle_On");
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System.Collections.Concurrent;
using System.Collections.Generic;
using Akka.Actor;
using Imlight.Classic.Quests;

namespace Imlight.CoreLib.Classic;

/// <summary>State objects by zone actor.</summary>
internal static class ZoneObjectStates {

    private static readonly ConcurrentDictionary<IActorRef, ObjectStateTable> s_tables = new();
    private static readonly ConcurrentDictionary<IActorRef, HashSet<string>> s_listened = new();

    /// <summary>The zone's table, created on first use; null for no zone.</summary>
    internal static ObjectStateTable For(IActorRef zone)
        => zone is null || zone.IsNobody() ? null : s_tables.GetOrAdd(zone, _ => new ObjectStateTable());

    /// <summary>The zone's table when it has one.</summary>
    internal static ObjectStateTable Find(IActorRef zone)
        => zone is not null && s_tables.TryGetValue(zone, out var table) ? table : null;

    /// <summary>Records the tags the zone's triggers listen to by "&lt;tag&gt;.&lt;state&gt;.EnterState".</summary>
    internal static void SetListenedTags(IActorRef zone, HashSet<string> tags) {
        if (zone is not null) {
            s_listened[zone] = tags ?? [];
        }
    }

    /// <summary>True when one of the zone's triggers listens to a state event of <paramref name="tag"/>.</summary>
    internal static bool IsListened(IActorRef zone, string tag)
        => zone is not null && !string.IsNullOrEmpty(tag) && s_listened.TryGetValue(zone, out var tags) && tags.Contains(tag);

    internal static void Remove(IActorRef zone) {
        if (zone is not null) {
            s_tables.TryRemove(zone, out _);
            s_listened.TryRemove(zone, out _);
        }
    }

}
