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
 * QUEST SERVICE INDEXED COMBAT GOALS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: credits the captured combat goals that CombatGoalTargetIndex
 * describes on a combat win: "Defeat and Collect" (GOAL_TYPE_SCAVENGE)
 * goals, and "Defeat" bounty goals captured without adjectives.
 *
 * USAGE EXAMPLE:
 * Called from QuestService.ReceiveCombatVictory with the defeated mobs'
 * template IDs.
 *
 * NOTE:
 * The adjective match in ProcessCombatGoal never fires for these goals, so
 * each defeated target mob counts once here instead. The scavenge quest
 * items have no client item templates, so, like BOUNTYCOLLECT, the item is
 * only the goal tally; nothing enters the backpack or is removed later.
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

    private void ProcessIndexedCombatGoals(Wizard wizard, ulong[] defeatedMobTemplateIds) {
        if (defeatedMobTemplateIds is not { Length: > 0 }) {
            return;
        }

        // Iterate a copy: completing a goal can complete or grant quests.
        foreach (var qInstance in wizard.QuestBehavior.CurrentQuestInstances.ToArray()) {
            var qTemplate = _cachedQuestTemplates.FirstOrDefault(q => q.m_questName == qInstance.QuestName);
            if (qTemplate == null) {
                continue;
            }

            // Only goals active before this win count it; one it completes may start the next.
            var activeGoals = qTemplate.m_goals
                .Where(goal => CombatGoalTargetIndex.IsIndexedGoal(qInstance.QuestName, goal)
                    && qInstance.IsGoalActive(goal.m_goalName))
                .ToArray();

            foreach (var goal in activeGoals) {
                if (!wizard.QuestBehavior.CurrentQuestInstances.Contains(qInstance)) {
                    break;
                }

                ProcessIndexedCombatGoal(wizard, qInstance, goal, defeatedMobTemplateIds);
            }
        }
    }

    private void ProcessIndexedCombatGoal(Wizard wizard,
                                          QuestInstance qInstance,
                                          GoalTemplate goalTemplate,
                                          ulong[] defeatedMobTemplateIds) {
        // Each player in the duel gets their own MSG_COMBATWIN, so each is credited on their own.
        var gInstance = qInstance.GoalProgress.FirstOrDefault(g => g.GoalName == goalTemplate.m_goalName);
        if (gInstance == null || !qInstance.IsGoalActive(goalTemplate.m_goalName)) {
            return;
        }

        var goalMax = goalTemplate.m_tallyCounter?.m_count ?? 1;
        var chance = goalTemplate.m_tallyCounter?.m_percentChance ?? DEFAULT_KILL_COLLECT_CHANCE;

        var credited = CombatGoalTargetIndex.RollCredits(qInstance.QuestName, goalTemplate.m_goalName,
            defeatedMobTemplateIds, chance, goalMax - gInstance.CurrentProgress);

        // A saved tally already at the total (the index's count was lowered) completes here too.
        if (credited == 0 && gInstance.CurrentProgress < goalMax) {
            return;
        }

        for (var i = 0; i < credited; i++) {
            wizard.IncrementQuestGoal(qInstance.QuestName, goalTemplate.m_goalName);
        }

        if (gInstance.CurrentProgress >= goalMax) {
            CompleteGoal(qInstance, goalTemplate);

            return;
        }

        SendGoalMessage(goalTemplate, qInstance, 2);
    }

}
