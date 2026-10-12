// CLASSIC: existing quest mutations publish prepared native progress only in their acknowledged character lane.
using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class QuestService {
    private bool StopUncertainQuestSession(Wizard wizard) {
        if (!WizardCollection.IsInventorySnapshotUncertain(wizard)) return false;
        CloseSession();
        return true;
    }

    private bool RunQuestMutation(Wizard wizard, Func<QuestMutationStatus> mutation) {
        if (StopUncertainQuestSession(wizard)) return false;
        try {
            var status = mutation();
            if (status == QuestMutationStatus.Committed) return !StopUncertainQuestSession(wizard);
            StopUncertainQuestSession(wizard);
            return false; // A repeated start/completion never replays native messages or activation results.
        }
        catch {
            if (StopUncertainQuestSession(wizard)) return false;
            throw;
        }
    }

    private bool GoalRequirementsMet(Wizard wizard, QuestInstance quest, GoalTemplate goal)
        => goal.m_goalRequirements?.m_requirements?.Count is not > 0
            || RequirementDispatcher.EvaluateRequirements(goal.m_goalRequirements,
                new QuestRequirementContext(goal.m_goalRequirements, SessionActor.ActorRef,
                    GetActiveGameObject(), wizard, quest.QuestName, goal.m_goalName));

    private bool PrepareQuestMessages(IReadOnlyList<IMessage> messages)
        => messages is not null && messages.All(message => message is not null
            && WizardProgressionTransactions.Prepare(message));

    private void PublishQuestMessages(IReadOnlyList<IMessage> messages) {
        foreach (var message in messages) {
            if (message is QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL
                or QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEGOAL or QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEQUEST)
                RefreshDoorLights();
            SendToSocket(message);
        }
    }

    private bool PrepareProgressionDialog(ActorDialogListBase list, string tag, string completion,
        ulong quest, ulong goal, out IMessage message) {
        message = null;
        var dialog = (list as ActorDialogList)?.m_dialogs?.FirstOrDefault(entry => entry.m_dialogTag == tag);
        if (dialog is null) return true;
        if (!new ObjectSerializer(Versionable: false).Serialize(ClassicDialogCamera.ForClient(dialog), 16, out var data)
            || data.Length == 0) return false;
        message = new WIZARD_12_PROTOCOL.MSG_ACTORDIALOG { MobileID = 0, QuestID = quest, GoalID = goal,
            CompletionType = completion, ActorDialog = data, Persona = "", PersonaName = "", PersonaIcon = "" };
        return true;
    }

    // Async result handlers retain their existing separate saves. A known quarantined snapshot cannot dispatch
    // more results; this boundary does not claim their later delivery or compound activation/tutorial rewards.
    private bool ExecuteAcknowledgedQuestResults(Wizard wizard, ResultList results, string quest, string goal = null) {
        if (StopUncertainQuestSession(wizard)) return false;
        try {
            ResultDispatcher.ExecuteResults(actorContext: Context, results: results, playerRef: SessionActor.ActorRef,
                playerObj: GetActiveGameObject(), zoneActor: ResultZoneActor(), questName: quest, goalName: goal);
            return !StopUncertainQuestSession(wizard);
        }
        catch {
            if (StopUncertainQuestSession(wizard)) return false;
            throw;
        }
    }

    private void AcceptCommittedQuest(Wizard wizard, QuestTemplate template, QuestInstance candidate) {
        IReadOnlyList<IMessage> messages = null;
        if (!RunQuestMutation(wizard, () => WizardQuestTransactions.TryAdd(wizard, candidate, out _,
            preparePublication: receipt => {
                messages = PrepareDungeonQuestMessages(receipt.Saved, template, receipt.Quest,
                    goal => GoalRequirementsMet(receipt.Saved, receipt.Quest, goal));
                return PrepareQuestMessages(messages);
            }, afterCommit: _ => {
                if (!_cachedQuestTemplates.Contains(template)) _cachedQuestTemplates.Add(template);
                // Keep the native start order: quest, quest dialogue, each goal, activation, goal dialogue.
                foreach (var message in messages) {
                    PublishQuestMessages([message]);
                    if (message is QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL sent) {
                        var goal = template.m_goals.Single(entry => entry.m_goalNameID == sent.GoalNameID);
                        if (!ExecuteAcknowledgedQuestResults(wizard, goal.m_activateResults,
                            candidate.QuestName, goal.m_goalName)) return;
                    }
                }
                foreach (var goal in template.m_goals.Where(goal => template.m_startGoals.Contains(goal.m_goalName))) {
                    if (GoalRequirementsMet(wizard, candidate, goal)) QueueZoneEntryCheck(goal);
                }
                if (!ExecuteAcknowledgedQuestResults(wizard, template.m_startResults, candidate.QuestName)) return;
                _cachedQuestOffers.Remove(template.m_questName);
                RefreshTowerGuide();
            }))) return;
        // The constructor already began initial goals. No nested StartGoal save or replay is required.
    }

    private bool IncrementCommittedGoal(Wizard wizard, QuestInstance quest, GoalTemplate template, bool sendProgress) {
        if (!ClassicQuestEngine.IsActive) return wizard.IncrementQuestGoal(quest.QuestName, template.m_goalName);
        var goal = quest.GoalProgress?.FirstOrDefault(entry => entry?.GoalName == template.m_goalName);
        IMessage message = null;
        if (RunQuestMutation(wizard, () => WizardQuestTransactions.TryIncrementGoal(wizard, quest, goal, out _,
            preparePublication: receipt => {
                if (!sendProgress || receipt.Goal.CurrentProgress >= (template.m_tallyCounter?.m_count
                    ?? (template.m_goalType == GOAL_TYPE.GOAL_TYPE_BOUNTY ? 0 : 1))) return true;
                message = PrepareGoalMessage(template, receipt.Quest, 2, strict: true);
                return PrepareQuestMessages([message]);
            }, afterCommit: _ => {
                if (message is not null) PublishQuestMessages([message]);
            }), out var status)) return true;
        // CLASSIC: nothing saved (the saved goal is already done or further on): bring the client up to date.
        ResyncQuestJournalAfterNoWrite(wizard, status, $"a credit for {quest.QuestName}/{template.m_goalName}");
        return false;
    }

    private void StartCommittedGoal(Wizard wizard, QuestInstance quest, GoalTemplate template) {
        var goal = quest.GoalProgress?.FirstOrDefault(entry => entry?.GoalName == template.m_goalName);
        var messages = new List<IMessage>();
        if (!RunQuestMutation(wizard, () => WizardQuestTransactions.TryStartGoal(wizard, quest, goal, out _,
            preparePublication: receipt => {
                messages.Add(PrepareGoalMessage(template, receipt.Quest, 1, strict: true));
                if (!PrepareProgressionDialog(template.m_dialogList, "Prep", "QuestStart", receipt.Quest.ID,
                    receipt.Goal.ID, out var dialog)) return false;
                if (dialog is not null) messages.Add(dialog);
                return PrepareQuestMessages(messages);
            }, afterCommit: _ => {
                PublishQuestMessages([messages[0]]);
                if (!ExecuteAcknowledgedQuestResults(wizard, template.m_activateResults, quest.QuestName, template.m_goalName)) return;
                PublishQuestMessages(messages.Skip(1).ToArray());
                QueueZoneEntryCheck(template);
            }, validateFresh: saved => GoalRequirementsMet(saved,
                WizardQuestTransactions.Held(saved, quest.QuestName), template)), out var status)) {
            // CLASSIC: the saved goal had begun already (a write this session missed). A refusal is usually the goal's
            // own gate (another school's goal), which needs no check.
            if (status == QuestMutationStatus.Unchanged) {
                ResyncQuestJournalAfterNoWrite(wizard, status, $"starting {quest.QuestName}/{template.m_goalName}");
            }
            return;
        }
    }

    private void CompleteCommittedGoal(Wizard wizard, QuestInstance quest, GoalTemplate template) {
        var goal = quest.GoalProgress?.FirstOrDefault(entry => entry?.GoalName == template.m_goalName);
        var messages = new List<IMessage>();
        if (!RunQuestMutation(wizard, () => WizardQuestTransactions.TryCompleteGoal(wizard, quest, goal, out _,
            preparePublication: receipt => {
                messages.Add(new QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEGOAL { QuestID = receipt.Quest.ID, GoalID = receipt.Goal.ID });
                if (!PrepareProgressionDialog(template.m_dialogList, "Completion", "Completion", receipt.Quest.ID,
                    receipt.Goal.ID, out var dialog)) return false;
                if (dialog is not null) messages.Add(dialog);
                return PrepareQuestMessages(messages);
            }, afterCommit: _ => PublishQuestMessages(messages)), out var status)) {
            // CLASSIC: the saved goal was already done (its notice may never have reached the client): re-send.
            ResyncQuestJournalAfterNoWrite(wizard, status, $"completing {quest.QuestName}/{template.m_goalName}");
            return;
        }
        if (!ExecuteAcknowledgedQuestResults(wizard, template.m_completeResults, quest.QuestName, template.m_goalName)) return;
        PostGoalCompleteEvents(quest, template);
        var authored = _cachedQuestTemplates.FirstOrDefault(entry => entry.m_questName == quest.QuestName);
        if (authored is null) return;
        if (!DetermineNextGoals(authored, quest, out var next)) { CompleteQuest(quest); return; }
        foreach (var nextGoal in next) {
            if (StopUncertainQuestSession(wizard)) return;
            StartGoal(quest, nextGoal);
        }
        RefreshTowerGuide();
    }

    private void CompleteCommittedQuest(Wizard wizard, QuestInstance quest) {
        var template = _cachedQuestTemplates.FirstOrDefault(entry => entry.m_questName == quest.QuestName);
        if (template is null) return;
        var messages = new List<IMessage> { new QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEQUEST { QuestID = quest.ID },
            new QUEST_MESSAGES_52_PROTOCOL.MSG_REMOVEQUEST { QuestID = quest.ID } };
        if (!RunQuestMutation(wizard, () => WizardQuestTransactions.TryComplete(wizard, quest, out _,
            preparePublication: _ => {
                if (!PrepareProgressionDialog(template.m_dialogList, "Complete", "QuestComplete", 0, 0, out var dialog)) return false;
                if (dialog is not null) messages.Add(dialog);
                return PrepareQuestMessages(messages);
            }, afterCommit: _ => PublishQuestMessages(messages)))) return;
        PublishLegacyQuestResults(wizard, quest, template);
    }
}
