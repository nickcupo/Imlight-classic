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

using System;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

/// <summary>
/// Exists as a child actor of a <see cref="Zone"/> and is the supervisor 
/// for any objects that are created within the zone.
/// <remarks>Initializes any <see cref="CoreObjectInfo"/> found within <see cref="ZoneData.m_objectList"/> field of the
/// given zone data.</remarks>
/// </summary>
/// <param name="zone">The zone that this supervisor is responsible for.</param>
internal sealed class ZoneObjectSupervisor(Core.Zone zone) : ZoneEntitySupervisor(zone) {

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS))]
    public override void ReceiveZoneLoadResults(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) {
        // We only care about the ZoneData section of the message.
        var zoneData = message.ZoneData;
        var walkerPaths = new PlacedWalkerPaths(message.PathData, message.NodeData); // CLASSIC

        // Initialize any objects found within the zone data.
        // CLASSIC: plus the NPCs that stood in this zone in 2009 but that the later data places elsewhere.
        // CLASSIC: the classic object lists name zones by path (WizardCity/WC_Golem_Tower); zone.ZoneName can be the
        // display key (WizardZone_GolemCourt), so moved NPCs never matched their zone.
        var zonePath = string.IsNullOrEmpty(zoneData.m_zoneName) ? zone.ZoneName : zoneData.m_zoneName;
        foreach (var objectInfo in zoneData.m_objectList.Concat(ClassicMovedObjects.MovedInto(zonePath))) {
            if (!IsObjectEligibleForSpawn(objectInfo, zonePath)) {
                continue;
            }

            var template = (GameObjectTemplate) CoreObjectFactory.GetCoreTemplate(objectInfo.m_templateID);
            if (template is null) {
                Logger.Warning("Could not create {0} because template ID {1} was not found.",
                    Logger.Args(objectInfo.m_zoneTag, objectInfo.m_templateID));

                continue;
            }

            var coreObject = CoreObjectFactory.FinalizeCoreObject(objectInfo, template);
            if (coreObject is null) {
                Logger.Warning("Could not finalize CoreObject {0} with template ID {1}.",
                    Logger.Args(objectInfo.m_zoneTag, objectInfo.m_templateID));

                continue;
            }

            // Determine if this is a critical object.
            // If so, register it with the Zone.
            if (IsCriticalObject(template)) {
                RegisterCriticalObject(coreObject.m_globalID);
            }

            var entityActor = CreateEntityActor(coreObject, template, objectInfo);
            TellPlacedWalkerItsPath(entityActor, template, objectInfo, walkerPaths);
        }

        ReportLoadedWhenEntitiesLoad();
    }

    // CLASSIC: a walker placed in the zone (the Marleybone cops) names its path in its template's PathBehavior; only
    // ZonePath told spawned walkers their nodes, so placed ones timed out and were deleted. A walker with no usable
    // path (no id, or an id the zone's path data lacks) is told so and stands still.
    private void TellPlacedWalkerItsPath(IActorRef entityActor, CoreTemplate template, CoreObjectInfo objectInfo,
                                         PlacedWalkerPaths walkerPaths) {
        if (!Components.PathMovementComponent.ShouldAttachToEntity(template)) {
            return;
        }

        var pathId = ((GameObjectTemplate) template).m_behaviors.OfType<PathBehaviorTemplate>().First().m_pathID.Full;
        var nodes = walkerPaths.NodesOf(pathId);
        if (nodes.Count == 0) {
            Logger.Debug("Zone {Zone} walker {Name} has no usable path data (path id {PathId}); it stands still.",
                Logger.Args(zone.ZoneName, objectInfo.m_zoneTag, pathId));
        }

        entityActor.Tell(new ZONE_102_PROTOCOL.MSG_PATHDETAILS { NodeObjects = nodes });
    }

    private static bool IsObjectEligibleForSpawn(CoreObjectInfo objectInfo, string? zoneName) {
        if (objectInfo is null) {
            return false;
        }

        // Do not spawn combat sigils within this supervisor.
        if (objectInfo is CombatSigilObjectInfo) {
            return false;
        }

        // Dungeon-entry sigil pads spawn within ZoneSigilSupervisor.
        if (objectInfo is MinigameSigilInfo) {
            return false;
        }

        // CLASSIC: objects from later versions of classic quests are not spawned.
        if (ClassicLaterObjects.Skips(objectInfo, zoneName)) {
            return false;
        }

        // CLASSIC: NPCs that stood elsewhere in 2009 are placed in their 2009 zone instead (ClassicMovedObjects).
        if (ClassicMovedObjects.MovedAway(objectInfo, zoneName)) {
            return false;
        }

        return true;
    }

    private static bool IsCriticalObject(CoreTemplate template) {
        if (template is not GameObjectTemplate goTemplate) {
            return false;
        }

        // Check if the adjectives contain "Critical."
        var adjectives = goTemplate.m_adjectiveList;
        return adjectives is not null
            && adjectives.Any(adj => adj.Equals("Critical", StringComparison.OrdinalIgnoreCase));
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_SPAWNENTITY))]
    private void ReceiveSpawnEntity(ZONE_102_PROTOCOL.MSG_SPAWNENTITY message) {
        var coreObject = message.CoreObject;
        var template = message.Template;

        var objectActor = CreateEntityActor(coreObject, template, null);
        if (objectActor is null) {
            Logger.Error("Failed to spawn entity from MSG_SPAWNENTITY.");
            return;
        }

        // Spawned entities (summoned pets) can be stopped while the zone lives on.
        Context.Watch(objectActor);

        // Reply with the created entity.
        var rsp = new ZONE_102_PROTOCOL.MSG_SPAWNENTITYRSP {
            EntityActor = objectActor,
            SpawnedObject = coreObject
        };
        Sender.Tell(rsp);
    }

    [MessageHandler(typeof(Terminated))]
    private void ReceiveEntityTerminated(Terminated message)
        => EntityActors.Remove(message.ActorRef);

    private void RegisterCriticalObject(GID id) {
        var msg = new ZONE_102_PROTOCOL.MSG_REGISTERCRITICALOBJECT {
            ObjectID = id
        };

        base.ZoneRef.Tell(msg);
    }

}