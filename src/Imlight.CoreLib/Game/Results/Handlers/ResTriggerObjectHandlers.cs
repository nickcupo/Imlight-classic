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
 * CLASSIC TRIGGER OBJECT RESULTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: ResRemoveTriggerObject and ResAddTriggerObject take a zone object
 * away and bring it back by its tag (Sunken City's gates, their collision
 * walls and the Marla stand-ins); ResStateChange puts a trigger object into
 * a state, as ResModifyTriggerObject does.
 *
 * USAGE EXAMPLE:
 * Run by ResultDispatcher for zone-trigger results (classic zone data).
 *
 * NOTE:
 * The zone's trigger supervisor owns the presence (ZoneTriggerTables): it
 * applies the change in the event's state scope and tells the zone's
 * objects which players now see the object.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Zone.Supervisors;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Results.Handlers;

internal static class TriggerObjectPresenceResult {

    internal static bool Execute(IResultContext context, ClassicResTriggerObjectPresence result) {
        var zone = context.GetZoneActor();
        if (!ClassicQuestEngine.IsActive || zone is null || zone.Equals(ActorRefs.Nobody) || string.IsNullOrEmpty(result?.ObjectName)) {
            return false;
        }

        zone.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Targets = ZoneBroadcastTarget.Triggers,
            Messages = [new TriggerObjectPresenceChange {
                Tag = result.ObjectName,
                Present = result.Adds,
                Player = context.GetPlayerRef(),
            }],
        });

        return true;
    }

}

internal sealed class ResRemoveTriggerObjectHandler : BaseResultHandler<ClassicResRemoveTriggerObject> {

    public override bool Execute(IResultContext context) => TriggerObjectPresenceResult.Execute(context, Result);

}

internal sealed class ResAddTriggerObjectHandler : BaseResultHandler<ClassicResAddTriggerObject> {

    public override bool Execute(IResultContext context) => TriggerObjectPresenceResult.Execute(context, Result);

}

internal sealed class ResStateChangeHandler : BaseResultHandler<ClassicResStateChange> {

    public override bool Execute(IResultContext context) {
        var zone = context.GetZoneActor();
        if (!ClassicQuestEngine.IsActive || zone is null || zone.Equals(ActorRefs.Nobody) || Result?.CanExecute != true) {
            Logger.Warning("Cannot execute classic trigger object state change for {ObjectName}: missing context or unsupported fields.",
                Logger.Args(Result?.ObjectName));

            return false;
        }

        ZoneObjectStates.For(zone)?.Set(Result.ObjectName, Result.State);
        zone.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Targets = ZoneBroadcastTarget.Objects,
            Messages = [new ZONE_102_PROTOCOL.MSG_MODIFYTRIGGEROBJECT {
                ObjectName = Result.ObjectName,
                StateName = Result.State,
            }],
        });

        return true;
    }

}
