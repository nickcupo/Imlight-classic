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
 */

using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Results.Handlers;

/// <summary>
/// CLASSIC: ResReInteract. The objects the wizard stands at send their options again, so the dialog of the NPC the
/// wizard was just talking to opens again with what it offers now (a quest step that adds the next option), and a
/// discovered teleport stone lists its destination. Its fields (actor type, delay, persona name) are not in the
/// generated type, so every object in range of the wizard answers. It had no handler: "No handler registered for result
/// type ResReInteract", and the player had to click the NPC again.
/// </summary>
internal sealed class ResReInteractHandler : BaseResultHandler<ResReInteract> {

    public override bool Execute(IResultContext context) {
        var zoneActor = context.GetZoneActor();
        var player = context.GetPlayerRef();
        if (zoneActor is null || player is null) {
            return false;
        }

        zoneActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Messages = [new InteractServiceMementoComponent.ReInteractPlayer(player)],
            Targets = ZoneBroadcastTarget.Objects,
        });

        return true;
    }

}

/// <summary>CLASSIC: ResDownloadPackage asks a streaming client to fetch a package; the classic client has them all.</summary>
internal sealed class ResDownloadPackageHandler : BaseResultHandler<ResDownloadPackage> {

    public override bool Execute(IResultContext context) => true;

}

/// <summary>CLASSIC: ResMarkZoneNoWarn turns off a later client's zone-entry warning; nothing to do for this client.</summary>
internal sealed class ResMarkZoneNoWarnHandler : BaseResultHandler<ResMarkZoneNoWarn> {

    public override bool Execute(IResultContext context) => true;

}
