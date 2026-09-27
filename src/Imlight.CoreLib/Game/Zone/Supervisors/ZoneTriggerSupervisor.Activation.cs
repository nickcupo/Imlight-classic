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
 * ZONE TRIGGERS
 * ========================================================================
 * 
 * PURPOSE:
 * CLASSIC: honours a trigger's m_deactivateEvents and m_activateEvents per
 * player, so a trigger that disables itself stops firing for that player.
 * 
 * USAGE EXAMPLE:
 * Called from ZoneTriggerSupervisor.ReceivePostEvent before any trigger fires.
 * 
 * NOTE:
 * Only triggers with a deactivate event are tracked; the rest are always
 * armed, as in stock Imlight. Every posted event carries its player (volumes,
 * trigger results, the tutorial, zone entry), so state is kept per player
 * actor. See Imlight.Classic.Quests.TriggerActivation for the rules.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

internal sealed partial class ZoneTriggerSupervisor {

    private readonly Dictionary<IActorRef, (string Name, TriggerActivation<IActorRef> State)> _activation = [];

    private void TrackActivation(Trigger trigger, IActorRef triggerActor) {
        if (trigger is null || !ClassicQuestEngine.IsActive) {
            return;
        }

        var state = new TriggerActivation<IActorRef>(
            trigger.m_activateEvents?.Select(name => (string) name),
            trigger.m_deactivateEvents?.Select(name => (string) name));
        if (state.CanDisarm) {
            _activation[triggerActor] = ((string) trigger.m_triggerName, state);
        }
    }

    private void ObserveActivationEvent(ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        foreach (var (name, state) in _activation.Values) {
            if (state.Observe(message.EventName, message.PlayerActor)) {
                Logger.Debug("Zone {Zone} trigger {Trigger} is {State} for {Player} by {Event}.",
                    Logger.Args(Zone.ZonePath, name, state.IsArmed(message.PlayerActor) ? "armed" : "disarmed",
                        message.PlayerActor?.Path.Name, message.EventName));
            }
        }
    }

    private bool IsArmed(IActorRef triggerActor, IActorRef player)
        => !_activation.TryGetValue(triggerActor, out var entry) || entry.State.IsArmed(player);

}
