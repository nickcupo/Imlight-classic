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
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Packets;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Results.Handlers;

internal sealed class ResSpawnHandler : BaseResultHandler<ResSpawn> {
    
    public override bool Execute(IResultContext context) {
        var zoneActor = context.GetZoneActor();
        if (zoneActor == null) {
            return false;
        }

        if (Result is null) {
            return false;
        }

        // CLASSIC: m_activate false deactivates the spawner (a boss's death trigger, after its ResDespawn); it must not
        // spawn the boss again. The spawner keeps its own respawn timer, as before Monster_Killed was posted.
        if (ClassicQuestEngine.IsActive && !Result.m_activate) {
            // CLASSIC: a zone trigger's stop ends the spawner (its respawn timer brought the boss back 10 s after its death
            // trigger). A quest's ResSpawn without m_activate (overlay data) still does nothing, as before.
            if (context is not Contexts.GenericResultContext { TriggerName.Length: > 0 }) {
                return true;
            }

            zoneActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Messages = [new ZONE_102_PROTOCOL.MSG_ZONEPATHDEACTIVATE { SpawnObjectID = (uint) Result.m_spawnID }],
                Targets = ZoneBroadcastTarget.Paths,
            });

            return true;
        }

        var broadcastMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Messages = [new ZONE_102_PROTOCOL.MSG_ZONEPATHSPAWN {
                SpawnObjectID = (uint) Result.m_spawnID
            }],
            Targets = ZoneBroadcastTarget.Paths,
        };

        zoneActor.Tell(broadcastMsg);
        
        return true;
    }

}

internal sealed class ResDespawnHandler : BaseResultHandler<ResDespawn> {
    
    public override bool Execute(IResultContext context) {
        var zoneActor = context.GetZoneActor();
        if (zoneActor == null) {
            return false;
        }

        if (Result is null) {
            return false;
        }

        // Path-spawned creatures live under the path supervisor; include Paths so they see the removal.
        var broadcastMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Messages = [new ZONE_102_PROTOCOL.MSG_REMOVEOBJECT {
                TemplateID = Result.m_templateID,
            }],
            Targets = ZoneBroadcastTarget.Objects | ZoneBroadcastTarget.Paths,
        };

        zoneActor.Tell(broadcastMsg);
        
        return true;
    }

}