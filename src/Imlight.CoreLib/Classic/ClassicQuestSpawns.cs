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
 * CLASSIC QUEST SPAWNS
 * ========================================================================
 *
 * PURPOSE:
 * The CoreLib side of Imlight.Classic.Quests.DormantSpawners: which
 * templates a wizard's active goals need, and the dormant spawners of a
 * zone's spawn data.
 *
 * USAGE EXAMPLE:
 * var spawns = ClassicQuestSpawns.Find(zoneData, spawnData, triggerData);   // ZonePathSupervisor load
 * var start = DormantSpawners.Plan(spawns.Dormant, id => ClassicQuestSpawns.Needs(wizard, id), spawns.Placed);
 *
 * NOTE:
 * A template is needed when an active goal of the wizard talks to it
 * (PERSONA: m_personaName is its m_objectName), fights it (BOUNTY and
 * BOUNTYCOLLECT: one of its adjectives is one of the goal's
 * m_npcAdjectives) or is one of the targets CombatGoalTargetIndex fills in
 * for a captured combat goal. Started spawners are those a trigger's
 * ResSpawn activates (m_activate) and those InteractableQuestEvents starts
 * on an object use; a death trigger's deactivating ResSpawn starts nothing,
 * so KT_ThroneRoom's Krokenkahmen and KT_Retreat's Overseer stay dormant.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// A zone's dormant spawners and the templates it places anyway.
/// </summary>
internal sealed record ZoneQuestSpawns(IReadOnlyList<SpawnerInfo> Dormant, IReadOnlySet<uint> Placed);

internal static class ClassicQuestSpawns {

    /// <summary>
    /// The zone's inactive spawners that no trigger result and no known object use starts, in wad
    /// order, and the templates its static objects and active spawners place.
    /// </summary>
    /// <param name="zoneData">The zone's game data (static objects).</param>
    /// <param name="spawnData">The zone's spawn data.</param>
    /// <param name="triggerData">The zone's triggers.</param>
    internal static ZoneQuestSpawns Find(WizZoneData zoneData, SpawnManager spawnData, WizZoneTriggers triggerData) {
        var spawners = (spawnData?.m_spawners ?? [])
            .Where(spawner => spawner is not null)
            .Select(spawner => new SpawnerInfo(
                (uint) spawner.m_id.Full,
                spawner.m_active,
                (spawner.m_spawnList ?? [])
                    .Where(item => item?.m_objectInfo is not null)
                    .Select(item => (uint) item.m_objectInfo.m_templateID.Full)
                    .ToList()))
            .ToList();
        var started = (triggerData?.m_triggers ?? [])
            .Where(trigger => trigger?.m_results?.m_results is not null)
            .SelectMany(trigger => trigger.m_results.m_results)
            .OfType<ResSpawn>()
            .Where(spawn => spawn.m_activate) // m_activate false stops a spawner (a boss's death trigger); it starts nothing.
            .Select(spawn => (uint) spawn.m_spawnID)
            .Concat(InteractableQuestEvents.SpawnersStartedByObjects());

        var statics = (zoneData?.m_objectList ?? [])
            .Where(info => info is not null)
            .Select(info => (uint) info.m_templateID.Full);

        return new ZoneQuestSpawns(DormantSpawners.Find(spawners, started), DormantSpawners.Placed(spawners, statics));
    }

    /// <summary>
    /// Whether one of the wizard's active goals talks to or fights the template.
    /// </summary>
    /// <param name="wizard">The wizard.</param>
    /// <param name="templateId">An NPC or mob template.</param>
    internal static bool Needs(Wizard wizard, uint templateId) {
        if (wizard?.QuestBehavior?.CurrentQuestInstances is not { } quests
            || CoreObjectFactory.GetCoreTemplate(templateId) is not GameObjectTemplate template) {
            return false;
        }

        foreach (var quest in quests.ToArray()) {
            var questTemplate = QuestTemplateCollection.GetQuestByName(quest.QuestName);
            if (questTemplate?.m_goals is null) {
                continue;
            }

            foreach (var goal in questTemplate.m_goals) {
                if (goal is not null && quest.IsGoalActive(goal.m_goalName) && Targets(quest.QuestName, goal, template, templateId)) {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Targets(string questName, GoalTemplate goal, GameObjectTemplate template, uint templateId)
        => goal switch {
            PersonaGoalTemplate persona => !string.IsNullOrEmpty(persona.m_personaName)
                && persona.m_personaName == template.m_objectName,
            BountyGoalTemplate bounty when bounty.m_npcAdjectives is { Count: > 0 } adjectives
                => template.m_adjectiveList?.Any(adjectives.Contains) == true,
            _ => CombatGoalTargetIndex.IsTargetMob(questName, goal.m_goalName, templateId),
        };

}
