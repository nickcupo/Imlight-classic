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
 * QUEST SERVICE SCAVENGE GOALS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: credits "Defeat and Collect" (GOAL_TYPE_SCAVENGE) goals on a
 * combat win.
 *
 * USAGE EXAMPLE:
 * Called from QuestService.ReceiveCombatVictory with the defeated mobs'
 * template IDs.
 *
 * NOTE:
 * The quest items have no client item templates, so, like BOUNTYCOLLECT,
 * the item is only the goal tally; ScavengeGoalIndex says which mobs drop
 * it. Nothing enters the backpack, so nothing is removed on completion.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using System.Linq;

namespace Imlight.CoreLib.Game.Services;

internal partial class QuestService {

    private void ProcessScavengeGoals(Wizard wizard, ulong[] defeatedMobTemplateIds) {
        if (defeatedMobTemplateIds is not { Length: > 0 }) {
            return;
        }

        // Iterate a copy: completing a goal can complete or grant quests.
        foreach (var qInstance in wizard.QuestBehavior.CurrentQuestInstances.ToArray()) {
            var qTemplate = _cachedQuestTemplates.FirstOrDefault(q => q.m_questName == qInstance.QuestName);
            if (qTemplate == null) {
                continue;
            }

            foreach (var goal in qTemplate.m_goals.OfType<ScavengeGoalTemplate>()) {
                if (qInstance.IsGoalActive(goal.m_goalName)) {
                    ProcessScavengeGoal(wizard, qInstance, goal, defeatedMobTemplateIds);
                }
            }
        }
    }

    private void ProcessScavengeGoal(Wizard wizard,
                                     QuestInstance qInstance,
                                     ScavengeGoalTemplate goalTemplate,
                                     ulong[] defeatedMobTemplateIds) {
        // Each player in the duel gets their own MSG_COMBATWIN, so each rolls for themselves.
        var gInstance = qInstance.GoalProgress.FirstOrDefault(g => g.GoalName == goalTemplate.m_goalName);
        if (gInstance == null) {
            return;
        }

        var goalMax = goalTemplate.m_tallyCounter?.m_count ?? 1;
        var chance = goalTemplate.m_tallyCounter?.m_percentChance ?? DEFAULT_KILL_COLLECT_CHANCE;

        var collected = ScavengeGoalIndex.RollDrops(qInstance.QuestName, goalTemplate.m_goalName,
            defeatedMobTemplateIds, chance, goalMax - gInstance.CurrentProgress);

        // A saved tally already at the total (the index's count was lowered) completes here too.
        if (collected == 0 && gInstance.CurrentProgress < goalMax) {
            return;
        }

        for (var i = 0; i < collected; i++) {
            wizard.IncrementQuestGoal(qInstance.QuestName, goalTemplate.m_goalName);
        }

        if (gInstance.CurrentProgress >= goalMax) {
            CompleteGoal(qInstance, goalTemplate);

            return;
        }

        SendGoalMessage(goalTemplate, qInstance, 2);
    }

}
