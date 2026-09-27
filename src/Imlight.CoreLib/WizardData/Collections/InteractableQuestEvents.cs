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
 * InteractableQuestEvents.CompletesGoal(objectTemplate, goal); // InteractQuestSelectComponent
 *
 * NOTE:
 * Some captured "Use" goals are GOAL_TYPE_WAYPOINT with no zone or
 * proximity tag; only a client tag names what completes them. The object's
 * InteractableBehaviorTemplate (a server type Imcodec does not know, so it
 * deserializes to null) fires a quest event with that name when the object
 * is used. The events below were read from those behaviors in Root.wad,
 * and each object spawns in its goal's zone.
 *
 * TODO:
 * - Should the 13 other captured "Use" waypoint goals whose objects fire
 *   a matching event (DS crystal stands, KT_MapRoomStaff, ...) join it?
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.WizardData.Collections;

/// <summary>
/// CLASSIC: the quest events world objects fire when used, which the server cannot read from
/// their InteractableBehaviorTemplate.
/// </summary>
internal static class InteractableQuestEvents {

    private static readonly Dictionary<string, string[]> s_questEventsByObject = new() {
        ["MS_SoulChainForge"] = ["forgeSoulChain"], // MS-DTH2-C02-001 goal 3, in the Village of Sorrow
    };

    /// <summary>
    /// Whether using the object completes the goal: a waypoint goal whose client tags name one of
    /// the quest events the object fires.
    /// </summary>
    /// <param name="objectTemplate">The used object's template.</param>
    /// <param name="goal">The goal template.</param>
    internal static bool CompletesGoal(GameObjectTemplate objectTemplate, GoalTemplate goal)
        => goal is WaypointGoalTemplate
            && objectTemplate?.m_objectName is not null
            && s_questEventsByObject.TryGetValue(objectTemplate.m_objectName, out var questEvents)
            && goal.m_clientTags?.Any(questEvents.Contains) == true;

}
