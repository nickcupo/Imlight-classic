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
 * INTERACTABLE QUEST EVENTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: names the quest events that using a world object fires, so a
 * captured "Use" waypoint goal tagged with one of them can complete.
 *
 * USAGE EXAMPLE:
 * InteractableQuestEvents.AddGoalsCompletedBy(objectTemplate, goalsByQuest); // InteractQuestSelectComponent
 * InteractableQuestEvents.IsCompletedByEvent(goal); // ParallelStartGoals
 *
 * NOTE:
 * Some captured "Use" goals are GOAL_TYPE_WAYPOINT with no zone or
 * proximity tag; only a client tag names what completes them. The object's
 * InteractableBehaviorTemplate (a server type Imcodec does not know, so it
 * deserializes to null) fires a quest event with that name when the object
 * is used. The events below were read from those behaviors in Root.wad,
 * and each object spawns in its goal's zone. An event the behavior allows
 * in one zone only keeps that zone (the Amphitheatre's crystal stands share
 * templates between towers). Goals with a zone or proximity trigger of their
 * own keep it, even when a client tag names an event (DS-LIB1-C01-008's
 * explore goal names the blue crystal's).
 *
 * TODO:
 * - DS-ACAD1-C01-002's goals 1 to 5 complete on entering the Crystal Grove;
 *   should the other five samples' events complete them instead?
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.WizardData.Collections;

/// <summary>
/// CLASSIC: the quest events world objects fire when used, which the server cannot read from
/// their InteractableBehaviorTemplate.
/// </summary>
internal static class InteractableQuestEvents {

    private const string AmphitheatreTowers = "DragonSpire/DS_A3_Kings/Interiors/DS_Amphitheatre_T";

    private static readonly Dictionary<string, QuestEvent[]> s_questEventsByObject = new() {
        ["MS_SoulChainForge"] = [new("forgeSoulChain")], // MS-DTH2-C02-001 goal 3, in the Village of Sorrow
        ["MS_AirShrine"] = [new("AirShrineUsed", "MooShu/MS_Plague/Interiors/MS_Plague2_T5")], // MS-PLAG2-C04-005 goal 2
        ["KT_MapRoomStaff"] = [new("UseStaff")], // KT-PYMHub-C01-005 goal 3, in the Throne Room of Fire
        ["DS_Crystal_SampleGrove2_006"] = [new("CollectCrystal_Grove2_6")], // DS-ACAD1-C01-002 goal 6, in the Crystal Grove
        ["DS_Desk"] = [new("UseTable")], // DS-ACAD1-C04-003 goal 2, in the Crystal Grove's first tower
        ["DS_CrystalStand_ACAD2-C01-001"] = [new("CrystalStandOne", AmphitheatreTowers + "6")], // DS-ACAD2-C01-001 goal 2
        ["DS_KnowledgeCrystal"] = [new("CrystalStandTwo", AmphitheatreTowers + "7")], // DS-ACAD2-C01-002 goal 2
        ["DS_CrystalStandGreenBlue"] = [new("CrystalStandThree", AmphitheatreTowers + "8")], // DS-ACAD2-C01-005 goal 2
        ["DS_ActivationCrystal_Yellow"] = [new("activateCrystalYellow")], // DS-LIB1-C01-002 goal 2, in the Tower Archives
        ["DS_ActivationCrystal_Orange"] = [new("ActivateCrystalOrange")], // DS-LIB1-C01-004 goal 2
        ["DS_ActivationCrystal_Green"] = [new("ActivateCrystalGreen")], // DS-LIB1-C01-006 goal 2
        ["DS_ActivationCrystal_Blue"] = [new("ActivateCrystalBlue")], // DS-LIB1-C01-008 goal 2
        ["DS_CrystalStandDSNECHatch"] = [new("UseCrystalStand3")], // DS-NEC1-C01-004 goal 2, in Windhammer's tower
        ["DS_DragonEgg"] = [new("EggHatch")], // DS-NEC1-C05-004 goal 2, in the Drake Hatchery
    };

    /// <summary>
    /// Whether the object fires any quest event a goal can name.
    /// </summary>
    /// <param name="objectTemplate">The object's template.</param>
    internal static bool FiresQuestEvents(GameObjectTemplate objectTemplate)
        => objectTemplate?.m_objectName is not null && s_questEventsByObject.ContainsKey(objectTemplate.m_objectName);

    /// <summary>
    /// Whether using the object in the zone completes the goal: a waypoint goal without a zone or
    /// proximity trigger whose client tags name one of the quest events the object fires there.
    /// </summary>
    /// <param name="objectTemplate">The used object's template.</param>
    /// <param name="zonePath">The zone the object is in.</param>
    /// <param name="goal">The goal template.</param>
    internal static bool CompletesGoal(GameObjectTemplate objectTemplate, string zonePath, GoalTemplate goal)
        => goal is WaypointGoalTemplate waypoint
            && !HasOwnTrigger(waypoint)
            && objectTemplate?.m_objectName is not null
            && s_questEventsByObject.TryGetValue(objectTemplate.m_objectName, out var questEvents)
            && questEvents.Any(questEvent => questEvent.FiresIn(zonePath) && goal.m_clientTags?.Contains(questEvent.Name) == true);

    /// <summary>
    /// Whether some object's quest event completes the goal, a waypoint goal without a zone or
    /// proximity trigger.
    /// </summary>
    /// <param name="goal">The goal template.</param>
    internal static bool IsCompletedByEvent(GoalTemplate goal)
        => goal is WaypointGoalTemplate waypoint
            && !HasOwnTrigger(waypoint)
            && goal.m_clientTags?.Any(tag => s_questEventsByObject.Values.Any(questEvents => questEvents.Any(questEvent => questEvent.Name == tag))) == true;

    /// <summary>
    /// Adds every quest goal that using the object in the zone completes to the goals it is
    /// interactable for.
    /// </summary>
    /// <param name="objectTemplate">The object's template.</param>
    /// <param name="zonePath">The zone the object is in.</param>
    /// <param name="goalsByQuest">The object's goals, by quest name.</param>
    internal static void AddGoalsCompletedBy(GameObjectTemplate objectTemplate, string zonePath,
                                             Dictionary<string, List<GoalTemplate>> goalsByQuest) {
        if (!FiresQuestEvents(objectTemplate)) {
            return;
        }

        foreach (var quest in QuestTemplateCollection.GetAllQuests()) {
            if (quest?.m_goals is null) {
                continue;
            }

            foreach (var goal in quest.m_goals.Where(goal => CompletesGoal(objectTemplate, zonePath, goal))) {
                if (!goalsByQuest.TryGetValue(quest.m_questName, out var goals)) {
                    goals = [];
                    goalsByQuest[quest.m_questName] = goals;
                }
                goals.Add(goal);
            }
        }
    }

    private static bool HasOwnTrigger(WaypointGoalTemplate waypoint)
        => !string.IsNullOrEmpty(waypoint.m_proximityTag)
            || (!string.IsNullOrEmpty(waypoint.m_zoneTag) && (waypoint.m_zoneEntry || waypoint.m_zoneExit));

    private sealed record QuestEvent(string Name, string OnlyInZone = null) {

        public bool FiresIn(string zonePath)
            => OnlyInZone is null || string.Equals(OnlyInZone, zonePath, StringComparison.OrdinalIgnoreCase);

    }

}
