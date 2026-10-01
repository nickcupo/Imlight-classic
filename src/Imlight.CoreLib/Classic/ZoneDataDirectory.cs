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
 * ZONE DATA DIRECTORY
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: each loaded zone's WizZoneData, published by the zone actor when
 * its load finishes, so a session reads it (for instance m_noMounts on
 * every zone entry) without a blocking MSG_QUERYZONEDATA Ask. The data
 * does not change after the load; a zone not listed is still asked.
 *
 * USAGE EXAMPLE:
 * if (ZoneDataDirectory.TryGet(zoneActor, out var data)) { ... }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System.Collections.Concurrent;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic;

/// <summary>Loaded zones' data by zone actor.</summary>
internal static class ZoneDataDirectory {

    private static readonly ConcurrentDictionary<IActorRef, WizZoneData> s_zones = new();

    internal static void Set(IActorRef zone, WizZoneData data) {
        if (zone is not null && data is not null) {
            s_zones[zone] = data;
        }
    }

    internal static void Remove(IActorRef zone) {
        if (zone is not null) {
            s_zones.TryRemove(zone, out _);
        }
    }

    internal static bool TryGet(IActorRef zone, out WizZoneData data) {
        data = null;
        return zone is not null && s_zones.TryGetValue(zone, out data);
    }

}
