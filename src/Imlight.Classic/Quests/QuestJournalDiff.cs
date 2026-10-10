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
 * QUEST JOURNAL DIFF
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: what the client must be told when a session's quest journal turns
 * out to be behind the saved one (a quest save found the goal already done,
 * or a final-goal claim found the quest already claimed). The session
 * reloads the saved journal; this compares the journal the client was shown
 * with the reloaded one and lists the native updates, in the order the normal
 * progression sends them: a finished goal (MSG_COMPLETEGOAL), a finished or
 * dropped quest (MSG_COMPLETEQUEST / MSG_REMOVEQUEST), a new quest
 * (MSG_SENDQUEST with its goals), a goal that began (MSG_SENDGOAL start) and
 * a tally that moved (MSG_SENDGOAL progress).
 *
 * NOTE:
 * Progress -1 is "not begun", int.MaxValue is "done" (GoalInstance). A goal
 * the client never saw begin and that is already done is not reported: the
 * client has nothing to tick off.
 *
 * USAGE EXAMPLE:
 * var updates = QuestJournalDiff.Compare(shown, saved, questCompleted);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Quests;

/// <summary>One goal of a journal snapshot: its id, name and progress (-1 not begun, int.MaxValue done).</summary>
public sealed record JournalGoal(ulong Id, string Name, int Progress) {
    public bool Begun => Progress >= 0;
    public bool Done => Progress == int.MaxValue;
    public bool Active => Begun && !Done;
}

/// <summary>One held quest of a journal snapshot.</summary>
public sealed record JournalQuest(ulong Id, string Name, IReadOnlyList<JournalGoal> Goals);

public enum JournalUpdateKind {
    /// <summary>The goal the client shows as active is done: MSG_COMPLETEGOAL.</summary>
    GoalCompleted,
    /// <summary>The quest the client shows is finished: MSG_COMPLETEQUEST then MSG_REMOVEQUEST.</summary>
    QuestCompleted,
    /// <summary>The quest the client shows is gone without being finished: MSG_REMOVEQUEST.</summary>
    QuestRemoved,
    /// <summary>A quest the client does not show is held: MSG_SENDQUEST and its active goals.</summary>
    QuestAdded,
    /// <summary>A goal the client does not show began: MSG_SENDGOAL (start).</summary>
    GoalStarted,
    /// <summary>A goal both show as active has another tally: MSG_SENDGOAL (progress).</summary>
    GoalProgressed,
}

public sealed record JournalUpdate(JournalUpdateKind Kind, ulong QuestId, string QuestName, ulong GoalId = 0,
    string? GoalName = null);

public static class QuestJournalDiff {

    /// <summary>
    /// The native updates that take a client showing <paramref name="shown"/> to <paramref name="saved"/>.
    /// <paramref name="questCompleted"/> says whether a quest name is in the saved completed list.
    /// </summary>
    public static IReadOnlyList<JournalUpdate> Compare(IReadOnlyList<JournalQuest>? shown, IReadOnlyList<JournalQuest>? saved,
        Func<string, bool>? questCompleted) {
        shown ??= [];
        saved ??= [];
        var updates = new List<JournalUpdate>();
        var savedById = saved.Where(quest => quest is not null).GroupBy(quest => quest.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var shownById = shown.Where(quest => quest is not null).GroupBy(quest => quest.Id)
            .ToDictionary(group => group.Key, group => group.First());

        // Finished goals first, as the native completion does (MSG_COMPLETEGOAL before the next MSG_SENDGOAL).
        foreach (var before in shownById.Values) {
            if (!savedById.TryGetValue(before.Id, out var after)) {
                // The quest is gone. Its goals finished with it; one notice for the quest is enough.
                var completed = questCompleted?.Invoke(before.Name) == true;
                updates.Add(new JournalUpdate(completed ? JournalUpdateKind.QuestCompleted : JournalUpdateKind.QuestRemoved,
                    before.Id, before.Name));
                continue;
            }

            foreach (var goal in after.Goals ?? []) {
                var old = Find(before, goal.Id);
                if (goal.Done && old is { Active: true }) {
                    updates.Add(new JournalUpdate(JournalUpdateKind.GoalCompleted, after.Id, after.Name, goal.Id, goal.Name));
                }
            }
        }

        foreach (var after in savedById.Values) {
            if (!shownById.TryGetValue(after.Id, out var before)) {
                updates.Add(new JournalUpdate(JournalUpdateKind.QuestAdded, after.Id, after.Name));
                continue;
            }

            foreach (var goal in after.Goals ?? []) {
                if (!goal.Active) {
                    continue;
                }

                var old = Find(before, goal.Id);
                if (old is null || !old.Begun) {
                    updates.Add(new JournalUpdate(JournalUpdateKind.GoalStarted, after.Id, after.Name, goal.Id, goal.Name));
                }
                else if (old.Active && old.Progress != goal.Progress) {
                    updates.Add(new JournalUpdate(JournalUpdateKind.GoalProgressed, after.Id, after.Name, goal.Id, goal.Name));
                }
            }
        }

        return updates;
    }

    private static JournalGoal? Find(JournalQuest quest, ulong goalId)
        => quest.Goals?.FirstOrDefault(goal => goal is not null && goal.Id == goalId);

}
