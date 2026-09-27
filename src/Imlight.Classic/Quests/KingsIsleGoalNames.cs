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
 * Maps the goal names the client's zone data checks (KingsIsle's "Goal",
 * "Goal 2", "<quest>_Goal0") onto the goals of a quest SpiralDB captured
 * under other names ("2_WizardQuestGoals_TalkNPC"), by goal position.
 *
 * USAGE EXAMPLE:
 * var goal = KingsIsleGoalNames.Resolve(questName, goalNames, "Goal 2"); // ReqHasGoal
 * var names = KingsIsleGoalNames.NamesFor(questName, 1);                  // GoalComplete_ events
 *
 * NOTE:
 * A requested name that is one of the quest's goals always wins, and a
 * quest that already names any goal the KingsIsle way (an overlay quest a
 * story builder renamed, or chapter 1's "<quest>_Goal0") gets no alias, so
 * the alias only ever fills in for SpiralDB's captured names.
 *
 * KingsIsle's editor numbered goals two ways (r806919 zone data, checked
 * against the goals of the loaded quests):
 * - legacy 2008-2010 quests (names like MB-AIR2-C02-003): "Goal" is the
 *   first goal and "Goal N" the Nth (DS-ACAD-C01-005's "Goal 4" is its
 *   fourth and last goal, MB-AIR2-C02-003's "Goal 3" spawns the informant
 *   its third goal talks to, WC-MAIN-C01-012's "Goal 2"/"Goal 5" are its
 *   two Myth tower goals);
 * - the 2012+ rewrites (names like WC-TRITON-MAIN-005): "Goal" is the
 *   first and "Goal N" the (N+1)th (WC-TRITON-MAIN-005 checks both "Goal"
 *   and "Goal 1": its cog and Scarlet Screamer goals).
 * "<quest>_Goal<N>" counts from 0 in both (WC-MAIN-C01-003_Goal0).
 * A goal's position is its SpiralDB number prefix ("3_..." is the third),
 * or its place in m_goals when it has none.
 *
 * TODO:
 * - A legacy quest whose triggers check both "Goal" and "Goal 1"
 *   (WC-LIFE-C07-002, the Grizzleheim C09 school quests) maps both to the
 *   first goal; those quests are not in SpiralDB and get authored with
 *   KingsIsle's names, which turns the alias off for them.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Imlight.Classic.Quests;

/// <summary>
/// KingsIsle's goal names as the client's zone data checks them, mapped onto a quest's goals by position.
/// </summary>
public static partial class KingsIsleGoalNames {

    [GeneratedRegex(@"-C\d{2}-\d{3}[A-Za-z]?$")]
    private static partial Regex LegacyQuestName();

    [GeneratedRegex(@"^Goal(?:\s*(?<n>\d+)(?:\D.*)?)?$")]
    private static partial Regex NumberedGoal();

    [GeneratedRegex(@"_Goal(?<n>\d+)$")]
    private static partial Regex QuestPrefixedGoal();

    [GeneratedRegex(@"^(?<n>\d+)_")]
    private static partial Regex CapturedNumber();

    /// <summary>
    /// Whether the quest carries a 2008-2010 KingsIsle name (world, chain, "-Cnn-nnn"), whose goals
    /// the editor numbered "Goal", "Goal 2", "Goal 3".
    /// </summary>
    /// <param name="questName">The quest's m_questName.</param>
    public static bool UsesLegacyNumbering(string? questName)
        => questName is not null && LegacyQuestName().IsMatch(questName);

    /// <summary>
    /// Whether the name is one of KingsIsle's numbered goal names ("Goal", "Goal 3", "Goal 6 - talked
    /// to ...", "WC-MAIN-C01-003_Goal0").
    /// </summary>
    /// <param name="goalName">A goal name.</param>
    public static bool IsKingsIsleName(string? goalName)
        => goalName is not null && (NumberedGoal().IsMatch(goalName) || QuestPrefixedGoal().IsMatch(goalName));

    /// <summary>
    /// The 0-based goal position a KingsIsle goal name stands for in the quest, or null when the
    /// name is not a numbered KingsIsle name (a designer's own name such as "KillColossus").
    /// </summary>
    /// <param name="questName">The quest's m_questName (it decides the numbering).</param>
    /// <param name="goalName">The goal name the client data checks.</param>
    public static int? IndexOf(string? questName, string? goalName) {
        if (goalName is null) {
            return null;
        }

        var prefixed = QuestPrefixedGoal().Match(goalName);
        if (prefixed.Success) {
            return ParseNumber(prefixed.Groups["n"].Value);
        }

        var numbered = NumberedGoal().Match(goalName);
        if (!numbered.Success) {
            return null;
        }

        if (!numbered.Groups["n"].Success) {
            return 0;
        }

        var n = ParseNumber(numbered.Groups["n"].Value);
        if (n is null) {
            return null;
        }

        return UsesLegacyNumbering(questName) ? Math.Max(n.Value - 1, 0) : n.Value;
    }

    /// <summary>
    /// The quest goal a client check means: the goal of that name, else, for a quest none of whose
    /// goals has a KingsIsle numbered name, the goal at the position the KingsIsle name stands for.
    /// Null when neither exists.
    /// </summary>
    /// <param name="questName">The quest's m_questName.</param>
    /// <param name="goalNames">The quest's goal names in m_goals order.</param>
    /// <param name="requested">The goal name the client data checks.</param>
    public static string? Resolve(string? questName, IReadOnlyList<string?> goalNames, string? requested) {
        if (requested is null || goalNames is null) {
            return null;
        }

        foreach (var name in goalNames) {
            if (string.Equals(name, requested, StringComparison.Ordinal)) {
                return name;
            }
        }

        if (UsesKingsIsleNames(goalNames)) {
            return null;
        }

        var index = IndexOf(questName, requested);
        if (index is null) {
            return null;
        }

        for (var i = 0; i < goalNames.Count; i++) {
            if (goalNames[i] is { } name && PositionOf(name, i) == index.Value) {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// The KingsIsle names the goal at a position had, for a quest none of whose goals has a KingsIsle
    /// numbered name (empty otherwise): the names client triggers use in GoalComplete_ events.
    /// </summary>
    /// <param name="questName">The quest's m_questName.</param>
    /// <param name="goalNames">The quest's goal names in m_goals order.</param>
    /// <param name="goalName">The goal (one of <paramref name="goalNames"/>).</param>
    public static IReadOnlyList<string> AliasesOf(string? questName, IReadOnlyList<string?> goalNames, string? goalName) {
        if (goalName is null || goalNames is null || UsesKingsIsleNames(goalNames)) {
            return [];
        }

        for (var i = 0; i < goalNames.Count; i++) {
            if (string.Equals(goalNames[i], goalName, StringComparison.Ordinal)) {
                return NamesFor(questName, PositionOf(goalName, i));
            }
        }

        return [];
    }

    /// <summary>
    /// KingsIsle's names for the goal at a 0-based position of the quest.
    /// </summary>
    /// <param name="questName">The quest's m_questName.</param>
    /// <param name="index">The goal's 0-based position.</param>
    public static IReadOnlyList<string> NamesFor(string? questName, int index) {
        if (index < 0) {
            return [];
        }

        var names = new List<string>();
        if (UsesLegacyNumbering(questName)) {
            names.Add(index == 0 ? "Goal" : $"Goal {index + 1}");
            if (index == 0) {
                names.Add("Goal 1");
            }
        }
        else {
            names.Add(index == 0 ? "Goal" : $"Goal {index}");
            if (index == 0) {
                names.Add("Goal 0");
            }
        }

        if (!string.IsNullOrEmpty(questName)) {
            names.Add($"{questName}_Goal{index}");
        }

        return names;
    }

    private static bool UsesKingsIsleNames(IReadOnlyList<string?> goalNames) {
        foreach (var name in goalNames) {
            if (IsKingsIsleName(name)) {
                return true;
            }
        }

        return false;
    }

    private static int PositionOf(string goalName, int listIndex) {
        var captured = CapturedNumber().Match(goalName);
        if (captured.Success && ParseNumber(captured.Groups["n"].Value) is { } n && n > 0) {
            return n - 1;
        }

        return listIndex;
    }

    private static int? ParseNumber(string digits)
        => int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

}
