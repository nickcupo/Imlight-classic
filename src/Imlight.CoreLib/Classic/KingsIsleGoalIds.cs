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
 * CLASSIC QUEST ENGINE
 * ========================================================================
 *
 * PURPOSE:
 * A goal's m_goalNameID is StringHash of KingsIsle's own goal name ("Goal",
 * "Goal 2", ...), the id the client's quest-helper table keys its arrow
 * records by. Our goals keep their own m_goalName, so a client check for
 * "Goal 6" finds the goal whose id is StringHash("Goal 6"), and a finished
 * goal also posts GoalComplete_<quest>_Goal 6.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System.Collections.Frozen;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic;

internal static class KingsIsleGoalIds {

    private const int MaxNumberedGoal = 60;

    // "Goal", "Goal 2" .. "Goal 60" by their StringHash ("Goal 1" is not a KingsIsle name).
    private static readonly FrozenDictionary<uint, string> s_numbered = Enumerable.Range(1, MaxNumberedGoal)
        .Select(n => n == 1 ? "Goal" : $"Goal {n}")
        .ToFrozenDictionary(StringHash.Compute, name => name);

    /// <summary>The KingsIsle numbered goal name whose hash is <paramref name="goalNameId"/>, or null.</summary>
    internal static string NumberedNameOf(uint goalNameId)
        => s_numbered.TryGetValue(goalNameId, out var name) ? name : null;

    /// <summary>The quest's goal whose id is StringHash(<paramref name="goalName"/>), or null.</summary>
    internal static GoalTemplate GoalById(QuestTemplate template, string goalName) {
        if (template?.m_goals is null || string.IsNullOrEmpty(goalName)) {
            return null;
        }

        var id = StringHash.Compute(goalName);

        return template.m_goals.FirstOrDefault(goal => goal is not null && goal.m_goalNameID == id);
    }

}
