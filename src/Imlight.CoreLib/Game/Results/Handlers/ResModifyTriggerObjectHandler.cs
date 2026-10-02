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
 * Decodes classic object state changes and their fractional-second waits.
 *
 * USAGE EXAMPLE:
 * Registered by ClassicZoneTypeRegistry for classic zone data.
 *
 * NOTE:
 * Unknown nonempty state-change fields are not executable.
 *
 * TODO:
 *
 * Created by: Nick with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 09/28/2026
 */

using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Results.Handlers;

internal sealed class ResModifyTriggerObjectHandler : BaseResultHandler<ClassicResModifyTriggerObject> {
    public override bool Execute(IResultContext context) {
        var zone = context.GetZoneActor();
        if (!ClassicQuestEngine.IsActive || zone is null || zone.Equals(ActorRefs.Nobody) || Result?.CanExecute != true) {
            Logger.Warning("Cannot execute classic trigger object state change for {ObjectName}: missing context or unsupported fields.",
                Logger.Args(Result?.ObjectName));
            return false;
        }
        // CLASSIC: the zone's state objects keep the new state for ReqState, and a state object that changed posts
        // "<tag>.<state>.EnterState" (the Temple of Storms moons turning the suns off).
        if (ZoneObjectStates.For(zone)?.Set(Result.ObjectName, Result.State) == true
                && ZoneObjectStates.IsListened(zone, Result.ObjectName)) {
            zone.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                EventName = Imlight.Classic.Quests.ObjectStateRules.EnterStateEvent(Result.ObjectName, Result.State),
                PlayerActor = context.GetPlayerRef(),
                PlayerGameObject = context.GetPlayerObj(),
            });
        }
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
