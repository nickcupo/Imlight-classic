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
 * CREATURE STATS DIRECTORY
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: started creatures (NpcComponent.OnStart) by entity actor, so a
 * duel that seats one builds its MSG_CREATURESTATS from the creature's own
 * components instead of blocking the sigil's actor on an Ask until the
 * creature's actor runs. The answer holds the same references the Ask did.
 * A creature that has not started yet is still asked.
 *
 * USAGE EXAMPLE:
 * var stats = CreatureStatsDirectory.TryGet(creatureActor) ?? Ask(...);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System.Collections.Concurrent;
using Akka.Actor;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Classic;

/// <summary>Started creatures' stats, by entity actor.</summary>
internal static class CreatureStatsDirectory {

    private static readonly ConcurrentDictionary<IActorRef, NpcComponent> s_creatures = new();

    internal static void Set(IActorRef creature, NpcComponent npc) {
        if (creature is not null && npc is not null) {
            s_creatures[creature] = npc;
        }
    }

    internal static void Remove(IActorRef creature) {
        if (creature is not null) {
            s_creatures.TryRemove(creature, out _);
        }
    }

    /// <summary>The creature's stats answer, or null when it is not listed.</summary>
    internal static COMBAT_106_PROTOCOL.MSG_CREATURESTATS TryGet(IActorRef creature)
        => creature is not null && s_creatures.TryGetValue(creature, out var npc) ? npc.BuildCreatureStats() : null;

}
