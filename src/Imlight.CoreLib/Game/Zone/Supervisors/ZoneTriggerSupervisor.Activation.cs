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
 * CLASSIC: which triggers fire on a posted event. Honours a trigger's
 * m_deactivateEvents and m_activateEvents per player, and lets only a
 * teleport with a destination take the event's one teleport.
 * 
 * USAGE EXAMPLE:
 * ReceivePostEvent hands every event here when ClassicQuestEngine.IsActive.
 * 
 * NOTE:
 * The decision itself is Imlight.Classic.Quests.TriggerEventDispatch, where
 * it is tested. Only triggers with a deactivate event are tracked; the rest
 * are always armed, as in stock Imlight. Every posted event carries its
 * player (volumes, trigger results, the tutorial, zone entry), so state is
 * kept per player actor. A trigger whose ZoneTransfer entry carries
 * requirements (a classic travel hand decision) fires only for a wizard who
 * meets them, so the event's teleport passes to the next trigger.
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
using Imlight.Classic;
using Imlight.Classic.Quests;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

internal sealed partial class ZoneTriggerSupervisor {

    private readonly TriggerEventDispatch<IActorRef, IActorRef> _activation = new();

    private void TrackActivation(Trigger trigger, IActorRef triggerActor) {
        if (trigger is null || !ClassicQuestEngine.IsActive) {
            return;
        }

        _activation.Track(triggerActor, (string) trigger.m_triggerName,
            trigger.m_activateEvents?.Select(name => (string) name),
            trigger.m_deactivateEvents?.Select(name => (string) name));
    }

    private void ReceiveClassicPostEvent(ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        // A waypoint goal may name the event itself (KT-CRY5-C01-002's "PuzzleComplete").
        message.PlayerActor?.Tell(new ZONE_102_PROTOCOL.MSG_CLASSICZONEEVENT { EventName = message.EventName });

        // Monster_Killed triggers check the defeated monster (ClassicReqMonsterKilled); one whose check did not
        // decode would read as met for every kill, so it stays quiet.
        var isKill = message.EventName == KilledMonster.EventName;
        using var killed = KilledMonsterScope.Enter(message.KilledTemplateIds);
        var fires = _activation.Dispatch(_orderedTriggers, entry => entry.Actor, message.EventName, message.PlayerActor,
            listens: entry => entry.Trigger?.m_fireEvents?.Any(x => x == message.EventName) == true
                && !(isKill && HasUndecodedRequirement(entry.Trigger.m_requirements)),
            meetsRequirements: entry => EvaluateRequirements(entry.Trigger, message)
                && EvaluateTeleportRequirements(entry.Trigger, message),
            teleportsSomewhere: entry => HasTeleportDestination(entry.Trigger),
            stateChanged: (name, armed) => Logger.Debug("Zone {Zone} trigger {Trigger} is {State} for {Player} by {Event}.",
                Logger.Args(Zone.ZonePath, name, armed ? "armed" : "disarmed", message.PlayerActor?.Path.Name, message.EventName)));

        QueueLegacyDoors(message.PlayerActor);
        ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Sender = message.PlayerActor, Targets = ZoneBroadcastTarget.Objects,
            Messages = [new Imlight.CoreLib.Game.Zone.Components.DoorLightRefresh { Player = message.PlayerActor }],
        });

        foreach (var fire in fires) {
            fire.Trigger.Actor.Forward(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                EventName = message.EventName,
                PlayerActor = message.PlayerActor,
                PlayerGameObject = message.PlayerGameObject,
                SuppressTeleportResults = fire.SuppressTeleport,
                KilledTemplateIds = message.KilledTemplateIds,
            });
        }
    }

    // A trigger whose results the ZoneTransfer overlay replaced may carry requirements on that teleport (the classic
    // travel overlay's hand decisions: MS_Plague_Zone2_RiverVillage's 'Teleport MS_Plague2_T2' closes to holders of
    // MS-PLAG2-C01-003 so the part-2 door on the same volume takes the event). Its only result is that teleport, so a
    // wizard who fails them does not fire it, and the event's one teleport passes to the next trigger. No SpiralDB
    // trigger entry carries requirements, so this changes nothing else.
    private readonly Dictionary<Trigger, RequirementList> _teleportRequirements = new(ReferenceEqualityComparer.Instance);

    private void RememberTeleportRequirements(Trigger trigger, ResTeleport teleport) {
        if (!ClassicQuestEngine.IsActive || trigger is null || teleport?.m_requirements?.m_requirements is not { Count: > 0 }) {
            return;
        }

        _teleportRequirements[trigger] = teleport.m_requirements;
    }

    private bool EvaluateTeleportRequirements(Trigger trigger, ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        if (trigger is null || !_teleportRequirements.TryGetValue(trigger, out var requirements)) {
            return true;
        }

        var wizard = PlayerQuery.ActiveWizard(message.PlayerActor, $"Zone {Zone.ZonePath} trigger {trigger.m_triggerName}");
        if (wizard is null) {
            return false;
        }

        return RequirementDispatcher.EvaluateRequirements(
            requirements: requirements,
            context: new ZoneRequirementContext(
                requirements,
                message.PlayerActor,
                message.PlayerGameObject,
                wizard,
                ZoneRef,
                trigger.m_triggerName));
    }

    private static bool HasUndecodedRequirement(RequirementList requirements)
        => requirements?.m_requirements?.Any(requirement => requirement is null
            || (requirement is RequirementList nested && HasUndecodedRequirement(nested))) == true;

    // The r806919 client fires every teleport stone's discovery trigger on EnterZone, so a wizard finds every stone of
    // a zone on arrival. Under rules.teleport_stones: discover the trigger fires on its volume beside the far stone
    // instead (the classic travel overlay names the trigger and the volume's enter event), as in 2009.
    private void ApplyStoneDiscovery(List<Trigger> triggers) {
        if (!ClassicQuestEngine.IsActive || !ClassicRuntime.Rules.TeleportStonesNeedDiscovery) {
            return;
        }

        var discovery = ZoneDataCollection.GetZoneData(Zone.ZonePath)?.Classic?.DiscoveryTriggers;
        if (discovery is not { Count: > 0 }) {
            return;
        }

        foreach (var entry in discovery) {
            if (string.IsNullOrEmpty(entry?.Trigger) || string.IsNullOrEmpty(entry.Event)) {
                continue;
            }

            foreach (var trigger in triggers) {
                if (trigger is null || (string) trigger.m_triggerName != entry.Trigger) {
                    continue;
                }

                var events = (trigger.m_fireEvents ?? []).Where(name => (string) name != ENTER_ZONE_EVENT).ToList();
                if (!events.Any(name => (string) name == entry.Event)) {
                    events.Add(entry.Event);
                }

                trigger.m_fireEvents = events;
            }
        }
    }

    private const string ENTER_ZONE_EVENT = "EnterZone";

    private static bool HasTeleportDestination(Trigger trigger)
        => trigger.m_results?.m_results?.Any(result => result is ResTeleport teleport
            && !string.IsNullOrEmpty(teleport.m_destinationZone)) == true;

}
