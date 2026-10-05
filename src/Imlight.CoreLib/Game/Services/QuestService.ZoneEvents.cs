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
 * QUEST SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the quest service's side of zone events. Quest results run with
 * the wizard's zone (ResSpawn, ResPostEvent), a combat win posts
 * Monster_Killed, a zone event completes a waypoint goal that names it,
 * and a zone-entry goal that starts in its own zone completes.
 *
 * USAGE EXAMPLE:
 * ResultDispatcher.ExecuteResults(..., zoneActor: ResultZoneActor(), ...);
 * PostMonsterKilled(message.MobTemplateIds); // ReceiveCombatVictory
 *
 * NOTE:
 * Stock Imlight runs quest results without a zone, so a quest's ResSpawn
 * and ResPostEvent did nothing: Grizzleheim's Red Claw prisoners, the
 * Dean's boss form in Dragonspyre and the Conquest Plaza summons never
 * appeared. Monster_Killed feeds 974 Arc 1 zone triggers (boss-death
 * spawns, the Marleybone counterweight waves, Hrafn in the Raven fortress).
 * A zone-entry goal is checked once the change that started it is done
 * (a message to this service), so the quest's other goals start first.
 *
 * TODO:
 * - Each wizard's win posts its own Monster_Killed; KingsIsle may have posted once per monster. Spawner limits keep a
 *   doubled ResSpawn from spawning twice.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Services;

internal partial class QuestService {

    private const string MONSTER_KILLED_EVENT = "Monster_Killed"; // KilledMonster.EventName; the zone triggers' event name.

    private bool _zoneEntryCheckQueued;

    private IActorRef ResultZoneActor()
        => ClassicQuestEngine.IsActive ? SessionActor.GetZoneActor() : null;

    // CLASSIC: on entering a zone, post again the zone events of active goals there (GoalZoneEvents): a new instance
    // has not seen them (Stealthy Stuff's MovePawman in Big Ben).
    private void ReplayGoalZoneEvents(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard) {
        if (!ClassicQuestEngine.IsActive || wizard?.QuestBehavior is null) {
            return;
        }

        var held = wizard.QuestBehavior.CurrentQuestInstances
            .Where(q => q is not null)
            .ToDictionary(q => q.QuestName, q => q, StringComparer.Ordinal);
        var templates = held.Keys.Select(QuestTemplateCollection.GetQuestByName).Where(t => t is not null).ToList();
        foreach (var (quest, goal) in GoalZoneEvents.ToReplay(templates,
                     (q, goalName) => held.TryGetValue(q.m_questName, out var instance) && instance.IsGoalActive(goalName),
                     wizard.Zone)) {
            ResultDispatcher.ExecuteResults(
                actorContext: Context,
                results: goal.m_activateResults,
                playerRef: SessionActor.ActorRef,
                playerObj: GetActiveGameObject(),
                zoneActor: ResultZoneActor(),
                questName: quest.m_questName,
                goalName: goal.m_goalName
            );
        }
    }

    private void PostMonsterKilled(ulong[] defeatedTemplateIds) {
        if (!ClassicQuestEngine.IsActive || defeatedTemplateIds is not { Length: > 0 }) {
            return;
        }

        SessionActor.GetZoneActor()?.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
            EventName = MONSTER_KILLED_EVENT,
            PlayerActor = SessionActor.ActorRef,
            PlayerGameObject = GetActiveGameObject(),
            KilledTemplateIds = defeatedTemplateIds,
        });
    }

    private void QueueZoneEntryCheck(GoalTemplate goalTemplate) {
        if (!ClassicQuestEngine.IsActive || _zoneEntryCheckQueued
            || goalTemplate is not WaypointGoalTemplate { m_zoneEntry: true } waypointGoal
            || string.IsNullOrEmpty(waypointGoal.m_zoneTag)
            || waypointGoal.m_zoneTag != GetActiveWizard()?.Zone) {
            return;
        }

        _zoneEntryCheckQueued = true;
        Self.Tell(new ZONE_102_PROTOCOL.MSG_CLASSICZONEENTRYCHECK());
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_CLASSICZONEENTRYCHECK))]
    private void ReceiveZoneEntryCheck(ZONE_102_PROTOCOL.MSG_CLASSICZONEENTRYCHECK message) {
        _zoneEntryCheckQueued = false;

        var wizard = GetActiveWizard();
        if (wizard is not null) {
            CheckForWaypointGoalZoneEntry(wizard);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_CLASSICZONEEVENT))]
    private void ReceiveClassicZoneEvent(ZONE_102_PROTOCOL.MSG_CLASSICZONEEVENT message) {
        var wizard = GetActiveWizard();
        if (!ClassicQuestEngine.IsActive || string.IsNullOrEmpty(message.EventName) || wizard?.QuestBehavior is null) {
            return;
        }

        foreach (var qInstance in wizard.QuestBehavior.CurrentQuestInstances.ToArray()) {
            var qTemplate = _cachedQuestTemplates.FirstOrDefault(q => q.m_questName == qInstance.QuestName);
            if (qTemplate is null) {
                continue;
            }

            var named = qTemplate.m_goals
                .OfType<WaypointGoalTemplate>()
                .Where(goal => goal.m_clientTags?.Contains(message.EventName) == true
                    && qInstance.IsGoalActive(goal.m_goalName)
                    && (string.IsNullOrEmpty(goal.m_zoneTag) || goal.m_zoneTag == wizard.Zone))
                .ToArray();
            foreach (var goal in named) {
                if (!wizard.QuestBehavior.CurrentQuestInstances.Contains(qInstance)) {
                    break;
                }

                if (qInstance.IsGoalActive(goal.m_goalName)) {
                    CompleteGoal(qInstance, goal);
                }
            }
        }
    }

}
