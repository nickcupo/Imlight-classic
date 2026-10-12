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
 * ZONE ENTITY QUERY
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: whether a MSG_QUERYZONEENTITY (find an object by global id or mobile id) names a given zone entity.
 *
 * NOTE:
 * MessageService.GetZoneObject asks by global id only, so the query's mobile id is 0. The entity check used to be
 * "global id equal OR mobile id equal": an entity with mobile id 0 (one that never got a mobile id, such as a
 * moving path creature) answered every query, and the first answer wins. A click on an NPC or a sigil was then
 * delivered to that entity ("No service components found for NPC 70A66"), so the interaction (a turn-in, an
 * offer, a zone sigil) was silently dropped (rig-final 2026-10-12; likely the cause of the earlier "timed out
 * waiting for quest offers" failures too). A mobile id of 0 never names an entity.
 *
 * Created by: Nick with Claude Code (claude-sonnet-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/12/2026
 */

namespace Imlight.Classic.Zones;

public static class EntityQuery {

    /// <summary>True when the query names the entity by its global id, or by a non-zero mobile id.</summary>
    public static bool Matches(ulong entityGlobalId, ushort entityMobileId, ulong queryGlobalId, ushort queryMobileId)
        => (queryGlobalId != 0 && entityGlobalId == queryGlobalId)
        || (queryMobileId != 0 && entityMobileId == queryMobileId);

}
