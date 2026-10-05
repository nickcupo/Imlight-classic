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
 * QUEST DIALOGUE REVIEW
 * ========================================================================
 *
 * PURPOSE:
 * The answer to the ? ("More Info") button on a quest card in the spellbook's
 * Quests tab (MSG_REQUESTQUESTDIALOG -> MSG_QUESTDIALOG): the conversations
 * the wizard has had for a quest they hold, in the order they had them. The
 * wiki's Quests page (oldid 4887, 2009-01-23; unchanged in oldid 62609,
 * 2010-02-16) says the window shows "all the conversations you had that
 * have lead you through the steps of that quest", for the quests in the
 * journal. The live server's answers, cached by a real client in its
 * DialogCache (QuestListPage::DialogDataCacheEntry), are: the quest's offer
 * (Prep), its Start, then each finished goal's Completion, one dialog with
 * an empty tag, every source dialog's NPC madlib block kept, no events, no
 * Underway chatter and no lines of goals not yet reached.
 *
 * USAGE EXAMPLE:
 * var message = QuestDialogReview.Message(template, instance, entry => true);
 *
 * NOTE:
 * Read only: nothing about the wizard or the quest changes. Lines whose own
 * requirements fail are left out (the r806919 client drops entries whose
 * m_meetsRequirements is false; WizardGUIManager::HandleQuestDialog). Camera
 * directions are cleared and m_bypassCameraOnReview set: the review is a
 * page in the spellbook, not a scene. A goal's Prep (said when the goal
 * starts; no classic quest has one) is included before its Completion.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

internal static class QuestDialogReview {

    /// <summary>
    /// At most this many lines are sent (the longest classic quest has far fewer).
    /// </summary>
    internal const int MaxEntries = 128;

    /// <summary>
    /// Requests a session may make at once before the ? button is rate limited (QuestService.DialogReview.cs).
    /// </summary>
    internal const int RequestBurst = 6;

    /// <summary>
    /// Requests a session regains per second.
    /// </summary>
    internal const double RequestsPerSecond = 1;

    /// <summary>
    /// A session's request limiter.
    /// </summary>
    internal static Imlight.Classic.Security.TokenBucket NewRequestLimiter() => new(RequestBurst, RequestsPerSecond);

    /// <summary>
    /// The quest the client means by <paramref name="questNameId"/> among the ones <paramref name="wizard"/> holds; null when they hold none by that id.
    /// </summary>
    internal static QuestInstance? HeldQuest(Wizard? wizard, uint questNameId)
        => wizard?.QuestBehavior?.CurrentQuestInstances?
            .FirstOrDefault(q => q is not null && !string.IsNullOrEmpty(q.QuestName) && StringHash.Compute(q.QuestName) == questNameId);

    /// <summary>
    /// The quest's goals in the order a wizard reaches them: the start goals, then each wave its goal logic adds;
    /// goals no logic reaches last; template order within a wave.
    /// </summary>
    internal static List<GoalTemplate> GoalsInOrder(QuestTemplate quest) {
        var goals = (quest.m_goals ?? []).Where(g => g is not null).ToList();
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in quest.m_startGoals ?? []) {
            if (name is not null) {
                rank.TryAdd(name, 0);
            }
        }

        for (var wave = 0; wave < goals.Count; wave++) {
            var added = false;
            foreach (var logic in quest.m_goalLogic ?? []) {
                if (logic?.m_goalsToAdd is not { Count: > 0 } toAdd) {
                    continue;
                }

                var before = (logic.m_goalsAND ?? []).Concat(logic.m_goalsOR ?? []).Where(n => n is not null).ToList();
                if (before.Count == 0 || !before.Any(rank.ContainsKey)) {
                    continue;
                }

                var at = before.Where(rank.ContainsKey).Max(n => rank[n]) + 1;
                foreach (var name in toAdd.Where(n => n is not null)) {
                    added |= rank.TryAdd(name, at);
                }
            }

            if (!added) {
                break;
            }
        }

        return [.. goals.Select((g, i) => (Goal: g, Index: i))
            .OrderBy(x => rank.TryGetValue(x.Goal.m_goalName ?? "", out var r) ? r : int.MaxValue)
            .ThenBy(x => x.Index)
            .Select(x => x.Goal)];
    }

    /// <summary>
    /// The dialogs <paramref name="instance"/>'s wizard has seen, in order (offer, start, then per goal its start and completion).
    /// </summary>
    internal static List<ActorDialog> SeenDialogs(QuestTemplate quest, QuestInstance instance) {
        var seen = new List<ActorDialog>();
        void Add(ActorDialogListBase? list, string tag) {
            if (list is ActorDialogList { m_dialogs: { } dialogs }
                && dialogs.FirstOrDefault(d => d is not null && d.m_dialogTag == tag) is { } dialog) {
                seen.Add(dialog);
            }
        }

        Add(quest.m_dialogList, "Prep");
        Add(quest.m_dialogList, "Start");
        foreach (var goal in GoalsInOrder(quest)) {
            var progress = instance.GoalProgress?.FirstOrDefault(g => g is not null && g.GoalName == goal.m_goalName);
            if (progress is null || !progress.DoesPlayerHaveGoal()) {
                continue;
            }

            Add(goal.m_dialogList, "Prep");
            if (progress.IsGoalCompleted()) {
                Add(goal.m_dialogList, "Completion");
            }
        }

        return seen;
    }

    /// <summary>
    /// The review dialog: every seen line whose requirements <paramref name="meets"/> passes, as one dialog for the spellbook.
    /// </summary>
    internal static ActorDialog Dialog(QuestTemplate quest, QuestInstance instance, Func<RequirementList, bool> meets) {
        var entries = new List<ActorDialogEntry>();
        var madlibs = new List<ActorMadlib>();
        foreach (var dialog in SeenDialogs(quest, instance)) {
            var kept = false;
            foreach (var entry in dialog.m_dialogEntries ?? []) {
                if (entry is null || entries.Count >= MaxEntries) {
                    continue;
                }

                if (entry.m_requirements is { m_requirements.Count: > 0 } requirements && !Meets(meets, requirements)) {
                    continue;
                }

                entries.Add(ForReview(entry));
                kept = true;
            }

            if (kept) {
                madlibs.AddRange((dialog.m_madlibs ?? []).Where(m => m is not null));
            }
        }

        return new ActorDialog {
            m_dialogTag = "",
            m_dialogEntries = entries,
            m_madlibs = madlibs,
            m_dialogEvents = [],
        };
    }

    /// <summary>
    /// The answer to the ? button for <paramref name="instance"/>; null when it does not serialize.
    /// </summary>
    internal static WIZARD_12_PROTOCOL.MSG_QUESTDIALOG? Message(QuestTemplate quest, QuestInstance instance, Func<RequirementList, bool> meets) {
        if (!new ObjectSerializer(Versionable: false).Serialize(Dialog(quest, instance, meets), 16, out var data)) {
            return null;
        }

        return new WIZARD_12_PROTOCOL.MSG_QUESTDIALOG {
            QuestNameID = StringHash.Compute(instance.QuestName),
            ActorDialog = data,
        };
    }

    // A line as the review shows it: a copy (the shared template is never changed), no camera, no event.
    private static ActorDialogEntry ForReview(ActorDialogEntry entry) => ClassicDialogCamera.WithoutCamera(entry) with {
        m_dialogEvent = "",
        m_bypassCameraOnReview = true,
        m_meetsRequirements = true,
    };

    private static bool Meets(Func<RequirementList, bool> meets, RequirementList requirements) {
        try {
            return meets(requirements);
        } catch (Exception) {
            return false; // a line that cannot be judged is not shown
        }
    }

}
