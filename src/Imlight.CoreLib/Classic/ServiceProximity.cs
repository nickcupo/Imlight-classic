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
 * SERVICE PROXIMITY
 * ========================================================================
 *
 * PURPOSE:
 * Server-side check that a wizard stands by the service NPC or object (trainer, shop, bank chest, Bazaar, potion
 * vendor) a request names, instead of trusting the client to have walked there. It also remembers the last service
 * each wizard used, for requests whose message carries no NPC id (or 0).
 *
 * USAGE EXAMPLE:
 * var vendor = ServiceProximity.FindNear<InteractVendorComponent>(wizard, message.npcGlobalID, GetZoneObject);
 * if (vendor is null) { refuse; }
 *
 * NOTE:
 * The wizard's position is the last one its client reported (MSG_CLIENTMOVE); the zone lookup only finds objects
 * in the wizard's own zone. The radius is Imlight.Classic.Rules.ServiceRange.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

#nullable enable

using System;
using System.Collections.Concurrent;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

internal static class ServiceProximity {

    // The service object each wizard last used (MSG_INTERACTNPC / MSG_INTERACTOPTION), by character id.
    private static readonly ConcurrentDictionary<ulong, ulong> s_lastService = new();

    /// <summary>Remembers the service object a wizard just used.</summary>
    public static void Record(Wizard? wizard, ulong objectId) {
        if (wizard is not null && objectId != 0) {
            s_lastService[wizard.CharId] = objectId;
        }
    }

    /// <summary>The service object the wizard last used, or 0.</summary>
    public static ulong LastService(Wizard? wizard)
        => wizard is not null && s_lastService.TryGetValue(wizard.CharId, out var id) ? id : 0;

    /// <summary>True when the wizard stands within <see cref="ServiceRange.Radius"/> of the entity.</summary>
    public static bool IsNear(Wizard? wizard, ZoneEntity? entity) {
        if (wizard is null || entity?.ActiveGameObject is not { } obj) {
            return false;
        }

        var here = wizard.Location;
        var there = obj.m_location;

        return ServiceRange.IsWithin(here.X, here.Y, here.Z, there.X, there.Y, there.Z);
    }

    /// <summary>
    /// The entity with component <typeparamref name="T"/> the request names, or else the one the wizard last used,
    /// when it is in the wizard's zone and the wizard stands by it; null otherwise.
    /// </summary>
    /// <param name="wizard">The requesting wizard.</param>
    /// <param name="objectId">The NPC or object id in the request (0 when it has none).</param>
    /// <param name="lookup">The session's zone lookup (MessageService.GetZoneObject).</param>
    public static ZoneEntity? FindNear<T>(Wizard? wizard, ulong objectId, Func<ulong, ZoneEntity?> lookup) where T : class {
        if (wizard is null) {
            return null;
        }

        foreach (var id in new[] { objectId, LastService(wizard) }) {
            if (id == 0) {
                continue;
            }

            var entity = lookup(id);
            if (entity?.GetComponentOfType<T>() is not null && IsNear(wizard, entity)) {
                return entity;
            }
        }

        return null;
    }

}
