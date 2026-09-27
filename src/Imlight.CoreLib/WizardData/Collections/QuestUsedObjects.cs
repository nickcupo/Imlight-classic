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
 * QUEST USED OBJECTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: names the world objects the loaded quests use, so an object the
 * client makes usable through its InteractableBehavior alone still takes
 * quest interactions.
 *
 * USAGE EXAMPLE:
 * QuestUsedObjects.IsUsedByQuests(template); // InteractQuestSelectComponent.ShouldAttachToEntity
 *
 * NOTE:
 * Imcodec deserializes InteractableBehaviorTemplate to null, so the server
 * only sees WizardSelectBehavior. Grizzleheim's berries, peat and fires,
 * DS_Desk and KT_MapRoomStaff have no WizardSelectBehavior. An object counts
 * when a usage goal names it (client tag or scavenge item adjective, as
 * InteractQuestSelectComponent matches it) or it fires a quest event;
 * every other object keeps upstream's check.
 *
 * TODO:
 * - Does the client show the use prompt for an object without
 *   WizardSelectBehavior?
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.WizardData.Collections;

/// <summary>
/// CLASSIC: the world objects that loaded quests use, whose templates may not show the server that
/// they are usable.
/// </summary>
internal static class QuestUsedObjects {

    private static UsedObjects s_usedObjects = new(null, [], []);

    /// <summary>
    /// Whether a loaded quest's usage goal names the object, or the object fires a quest event.
    /// </summary>
    /// <param name="template">The object's template.</param>
    internal static bool IsUsedByQuests(CoreTemplate template) {
        if (template is not GameObjectTemplate objectTemplate) {
            return false;
        }

        if (InteractableQuestEvents.FiresQuestEvents(objectTemplate)) {
            return true;
        }

        var usedObjects = CurrentUsedObjects();

        return (objectTemplate.m_objectName is not null && usedObjects.Names.Contains(objectTemplate.m_objectName))
            || objectTemplate.m_adjectiveList?.Any(usedObjects.ItemAdjectives.Contains) == true;
    }

    private static UsedObjects CurrentUsedObjects() {
        // Rebuilt whenever SpiralDB swaps in a new quest list.
        var quests = SpiralDB.QuestTemplates;
        var usedObjects = s_usedObjects;
        if (ReferenceEquals(usedObjects.Quests, quests)) {
            return usedObjects;
        }

        var usageGoals = quests
            .Where(quest => quest?.m_goals is not null)
            .SelectMany(quest => quest.m_goals)
            .Where(goal => goal?.m_goalType == GOAL_TYPE.GOAL_TYPE_USAGE)
            .ToList();
        var names = usageGoals
            .SelectMany(goal => goal.m_clientTags ?? [])
            .Where(tag => tag is not null)
            .ToHashSet();
        var itemAdjectives = usageGoals
            .OfType<ScavengeGoalTemplate>()
            .SelectMany(goal => goal.m_itemAdjectives ?? [])
            .Where(adjective => adjective is not null)
            .ToHashSet();

        usedObjects = new UsedObjects(quests, names, itemAdjectives);
        s_usedObjects = usedObjects;

        return usedObjects;
    }

    private sealed record UsedObjects(IReadOnlyList<QuestTemplate> Quests, HashSet<string> Names,
                                      HashSet<string> ItemAdjectives);

}
