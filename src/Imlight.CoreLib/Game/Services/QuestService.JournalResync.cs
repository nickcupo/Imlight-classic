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
 * QUEST SERVICE: RE-SEND WHEN THE SAVED JOURNAL IS AHEAD
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: a goal save that finds nothing to do (the saved goal is already
 * done or already further along) or a final-goal claim that finds the quest
 * already claimed used to stop silently. If the client had never been told
 * (a notice that went out while the client was changing zones, or any write
 * this session's copy missed), the client kept showing the goal as open and
 * a group hunt kept fighting for a credit that was already saved
 * (Marleybone playthrough, 2026-10-08/09). Now the session reloads its
 * journal from the saved one and sends the client exactly what changed, with
 * the same native messages the normal progression uses
 * (Imlight.Classic.Quests.QuestJournalDiff). Nothing is saved, rolled or
 * rewarded again: the write that got there first already did that.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class QuestService {

    // CLASSIC: RunQuestMutation with its outcome, so a "nothing to save" can be checked against the saved journal.
    private bool RunQuestMutation(Wizard wizard, Func<QuestMutationStatus> mutation, out QuestMutationStatus status) {
        var outcome = QuestMutationStatus.Refused;
        var committed = RunQuestMutation(wizard, () => outcome = mutation());
        status = outcome;
        return committed;
    }

    /// <summary>
    /// CLASSIC: a quest write this session asked for was not made (status <paramref name="status"/>). When the saved
    /// journal is ahead of what this session showed the client, the session takes the saved journal and tells the
    /// client what changed. Returns the number of native updates sent (0 when the session was already current).
    /// </summary>
    private int ResyncQuestJournalAfterNoWrite(Wizard wizard, QuestMutationStatus status, string reason) {
        if (status == QuestMutationStatus.Committed || !ClassicQuestEngine.IsActive || wizard?.QuestBehavior is null
            || StopUncertainQuestSession(wizard)) {
            return 0;
        }

        var shown = Snapshot(wizard.QuestBehavior.CurrentQuestInstances);
        QuestMutationStatus reloaded;
        try {
            reloaded = WizardQuestTransactions.ReconcileLoadedJournal(wizard, out _);
        }
        catch (Exception error) {
            Logger.Warning("Quest journal of {0} could not be checked after {1}: {2}",
                Logger.Args(wizard.CharId, reason, error.Message));
            StopUncertainQuestSession(wizard);
            return 0;
        }

        if (reloaded == QuestMutationStatus.Refused || StopUncertainQuestSession(wizard)) {
            return 0;
        }

        var current = wizard.QuestBehavior.CurrentQuestInstances ?? [];
        var updates = QuestJournalDiff.Compare(shown, Snapshot(current),
            name => wizard.QuestBehavior.HasCompletedQuest(name));
        if (updates.Count == 0) {
            return 0;
        }

        Logger.Warning("Quest journal of {0} was behind its saved state after {1} ({2}); re-sending {3}.",
            Logger.Args(wizard.CharId, reason, status,
                string.Join(", ", updates.Select(update => $"{update.Kind} {update.QuestName}/{update.GoalName}"))));

        var sent = 0;
        foreach (var update in updates) {
            var quest = current.FirstOrDefault(entry => entry?.ID == update.QuestId);
            var template = TemplateFor(update.QuestName);
            switch (update.Kind) {
                case JournalUpdateKind.GoalCompleted:
                    PublishQuestMessages([new QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEGOAL {
                        QuestID = update.QuestId, GoalID = update.GoalId }]);
                    sent++;
                    break;
                case JournalUpdateKind.QuestCompleted:
                    PublishQuestMessages([new QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEQUEST { QuestID = update.QuestId },
                        new QUEST_MESSAGES_52_PROTOCOL.MSG_REMOVEQUEST { QuestID = update.QuestId }]);
                    sent++;
                    break;
                case JournalUpdateKind.QuestRemoved:
                    PublishQuestMessages([new QUEST_MESSAGES_52_PROTOCOL.MSG_REMOVEQUEST { QuestID = update.QuestId }]);
                    sent++;
                    break;
                case JournalUpdateKind.QuestAdded:
                    if (quest is not null && template is not null) {
                        SendQuestResumeMessage(template, quest);
                        sent++;
                    }
                    break;
                case JournalUpdateKind.GoalStarted:
                case JournalUpdateKind.GoalProgressed:
                    var goal = template?.m_goals?.FirstOrDefault(entry => entry?.m_goalName == update.GoalName);
                    if (quest is not null && goal is not null
                        && PrepareGoalMessage(goal, quest, update.Kind == JournalUpdateKind.GoalStarted ? (byte) 1 : (byte) 2)
                            is { } message) {
                        PublishQuestMessages([message]);
                        sent++;
                    }
                    break;
            }
        }

        RefreshTowerGuide();
        return sent;
    }

    private QuestTemplate TemplateFor(string questName) {
        if (string.IsNullOrEmpty(questName)) {
            return null;
        }

        var template = _cachedQuestTemplates.FirstOrDefault(entry => entry?.m_questName == questName)
            ?? QuestTemplateCollection.GetQuestByName(questName);
        if (template is not null && !_cachedQuestTemplates.Contains(template)) {
            _cachedQuestTemplates.Add(template);
        }

        return template;
    }

    private static List<JournalQuest> Snapshot(IEnumerable<QuestInstance> quests)
        => (quests ?? []).Where(quest => quest is not null)
            .Select(quest => new JournalQuest(quest.ID, quest.QuestName,
                (quest.GoalProgress ?? []).Where(goal => goal is not null)
                    .Select(goal => new JournalGoal(goal.ID, goal.GoalName, goal.CurrentProgress)).ToList()))
            .ToList();

}
