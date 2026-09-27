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
 * CLASSIC: posts "GoalComplete_<quest>_<goal>" into the wizard's zone when
 * a goal completes, under the goal's own name and under the KingsIsle
 * name the client's triggers listen for.
 *
 * USAGE EXAMPLE:
 * PostGoalCompleteEvents(questInstance, goalTemplate); // CompleteGoal
 *
 * NOTE:
 * r806919 has 25 Arc 1 triggers on such events: the Wizard City hub gate to
 * Unicorn Way (GoalComplete_WC-MAIN-C01-003_WC-MAIN-C01-003_Goal0), the
 * Ravenwood teleporter (GoalComplete_WC-MAIN-C01-010_Goal 3), Hyde Park's
 * monsters (GoalComplete_MB-AIR1-C01-001_Goal), the Burial Ground mantra
 * dialog (GoalComplete_MS-DTH1-C01-001_Goal) and more. A captured goal also
 * posts its KingsIsle names (Imlight.Classic.Quests.KingsIsleGoalNames);
 * a trigger's own requirements still decide whether it fires.
 *
 * TODO:
 * - Two triggers listen for "GoalComplete__" (a Treasure Tower and the
 *   Drains); what KingsIsle posted under that name is unknown.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class QuestService {

    private const string GOAL_COMPLETE_EVENT_PREFIX = "GoalComplete_";

    private void PostGoalCompleteEvents(QuestInstance questInstance, GoalTemplate goalTemplate) {
        if (!ClassicQuestEngine.IsActive) {
            return;
        }

        var zoneActor = SessionActor.GetZoneActor();
        if (zoneActor is null) {
            return;
        }

        var questName = questInstance.QuestName;
        var goalNames = QuestTemplateCollection.GetQuestByName(questName)?.m_goals?
            .Select(goal => goal?.m_goalName)
            .ToList() ?? [];
        var names = new[] { goalTemplate.m_goalName }
            .Concat(KingsIsleGoalNames.AliasesOf(questName, goalNames, goalTemplate.m_goalName))
            .Distinct();

        foreach (var name in names) {
            zoneActor.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                EventName = $"{GOAL_COMPLETE_EVENT_PREFIX}{questName}_{name}",
                PlayerActor = SessionActor.ActorRef,
                PlayerGameObject = GetActiveGameObject(),
            });
        }
    }

}
