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
 * PARALLEL START GOALS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: makes the goal logic of a captured quest wait for every goal
 * the quest starts together before it moves on.
 *
 * USAGE EXAMPLE:
 * ParallelStartGoals.Join(questTemplate); // SpiralDB load
 *
 * NOTE:
 * The quest builder chained goals in the order one capture completed them,
 * even goals that all start on accept (KT-PYM2-C01-002's two defeat goals,
 * DS-ACAD2-C01-006's two collect goals, KT-PYM2-C01-001's four talks).
 * Finishing the last of them first then started what follows, and the quest
 * could complete with the others undone. A quest has that shape when a
 * logic entry adds one of its requirement-free start goals once another is
 * complete. Those links are dropped, and the entries that followed them
 * wait for all of the start goals. Start goals with requirements (per-school
 * goals) are left out, since not every player gets them.
 *
 * TODO:
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
/// CLASSIC: joins the goals a captured quest starts together, whose goal logic the quest builder
/// chained one after another.
/// </summary>
internal static class ParallelStartGoals {

    /// <summary>
    /// Rewrites a quest's goal logic so the goals it starts together must all be complete before
    /// the goals that follow them start. Quests without chained start goals are left alone.
    /// </summary>
    /// <param name="quest">The quest template, just deserialized.</param>
    /// <returns>Whether the goal logic changed.</returns>
    internal static bool Join(QuestTemplate quest) {
        if (quest?.m_goalLogic is null || quest.m_startGoals is null || quest.m_goals is null) {
            return false;
        }

        var startGoals = quest.m_startGoals
            .Where(name => quest.m_goals.Any(goal => goal.m_goalName == name && !HasRequirements(goal)))
            .Distinct()
            .ToList();
        if (startGoals.Count < 2) {
            return false;
        }

        if (quest.m_goalLogic.RemoveAll(logic => IsLinkBetween(logic, startGoals)) == 0) {
            return false;
        }

        foreach (var logic in quest.m_goalLogic.Where(logic => logic?.m_goalsAND?.Any(startGoals.Contains) == true)) {
            logic.m_goalsAND = [.. startGoals.Union(logic.m_goalsAND)];
        }

        return true;
    }

    private static bool IsLinkBetween(GoalCompleteLogic logic, List<string> startGoals)
        => logic is not null
            && logic.m_goalsToAdd is { Count: > 0 }
            && logic.m_goalsToAdd.All(startGoals.Contains)
            && logic.m_goalsAND is { Count: > 0 }
            && logic.m_goalsAND.All(startGoals.Contains)
            && logic.m_goalsOR is not { Count: > 0 };

    private static bool HasRequirements(GoalTemplate goal)
        => goal.m_goalRequirements?.m_requirements is { Count: > 0 };

}
