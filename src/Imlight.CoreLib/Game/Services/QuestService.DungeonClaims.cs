// CLASSIC: publish prepared dungeon quest starts without re-entering saving AddQuest/StartGoal handlers.
using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class QuestService {
    private bool TryGrantCommittedDungeonQuests(Wizard wizard) {
        if (!ClassicQuestEngine.IsActive) return false;
        if (wizard is null) return true;
        if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return true; }
        try {
            ClassicQuestClaims.TryReconcileDungeonQuests(wizard, DungeonQuestIndex.GetQuestsForZone(wizard.Zone),
                SessionActor.ActorRef, GetActiveGameObject(), ResultZoneActor(), out _, PrepareDungeonQuestMessages,
                grants => PublishDungeonStarts(wizard, grants));
        }
        catch {
            // No repeated starts or result dispatch after an unknown save or runtime publication outcome.
            WizardCollection.WithCharacterLock(wizard.CharId, () => {
                WizardCollection.MarkInventorySnapshotUncertain(wizard);
                return true;
            });
            CloseSession();
            throw;
        }
        return true;
    }

    private void PublishDungeonStarts(Wizard wizard, IReadOnlyList<DungeonQuestGrant> grants) {
        foreach (var grant in grants) {
            if (!_cachedQuestTemplates.Contains(grant.Template)) _cachedQuestTemplates.Add(grant.Template);
            foreach (var message in grant.Messages) {
                if (message is QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL) RefreshDoorLights();
                SendToSocket(message);
            }
            foreach (var goal in grant.Template.m_goals.Where(goal => grant.Template.m_startGoals.Contains(goal.m_goalName))) {
                QueueZoneEntryCheck(goal);
            }
            ResultDispatcher.ExecuteFilteredResults(Context, grant.TransientStartResults, SessionActor.ActorRef,
                GetActiveGameObject(), ResultZoneActor(), grant.Quest.QuestName);
            RefreshTowerGuide();
            Logger.Information("Granted dungeon quest '{0}' to {1} in '{2}'.",
                Logger.Args(grant.Template.m_questName, wizard.CharId, wizard.Zone));
        }
    }

    private IReadOnlyList<IMessage> PrepareDungeonQuestMessages(Wizard saved, QuestTemplate template, QuestInstance quest) {
        var madlibs = QuestMadlibs.GetMadLibForQuest(template);
        if (!_goalSerializer.Serialize(madlibs, 1, out var madlibData)
            || !_goalSerializer.Serialize(GetQuestRewardsFromTemplate(template, null, SessionActor.ActorRef, saved), 1, out var rewards)
            || !_goalSerializer.Serialize(GetAssociatedWorlds(template), 1, out var worlds)) return null;
        var messages = new List<IMessage> { new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDQUEST {
            QuestID = quest.ID, QuestNameID = StringHash.Compute(template.m_questName), QuestType = 0,
            QuestLevel = template.m_questLevel, QuestTitle = template.m_questTitle, QuestInfo = "", New = 1,
            QuestMadlibs = madlibData, GoalData = "", Rewards = rewards, ClientTags = "", AssociatedWorlds = worlds,
            NoQuestHelper = template.m_noQuestHelper ? (byte)1 : (byte)0,
            Mainline = template.m_mainline ? (byte)1 : (byte)0, ReadyToTurnIn = 0, SkipQHAutoSelect = 0,
            PetOnlyQuest = template.m_playAsYourPetNPC ? (byte)1 : (byte)0, ActivityType = 0,
        } };
        if (!ClassicQuestClaims.TryPrepareDungeonDialog(template.m_dialogList, "Start", 0, 0, out var questDialog)) return null;
        if (questDialog is not null) messages.Add(questDialog);
        // The template enters the live cache only after ACK. Read its existing Prep portrait directly.
        var patronIcon = (template.m_dialogList as ActorDialogList)?.m_dialogs
            .FirstOrDefault(dialog => dialog.m_dialogTag == "Prep")?.m_dialogEntries.FirstOrDefault()?.m_picture ?? "";
        foreach (var goal in template.m_goals.Where(goal => template.m_startGoals.Contains(goal.m_goalName))) {
            var instance = quest.GoalProgress.Single(entry => entry.GoalName == goal.m_goalName);
            if (!_goalSerializer.Serialize(QuestMadlibs.GetAppropriateMadlibBlockForGoal(goal, instance), 1, out var goalMadlibs)) return null;
            var tags = GetClientTagList(goal.m_clientTags?.ToArray() ?? []);
            if (!new ObjectSerializer(false).Serialize(tags, 1, out var clientTags)) return null;
            if (tags.m_clientTags.Count == 0) clientTags = string.Empty;
            messages.Add(new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL {
                QuestID = quest.ID, GoalID = instance.ID, GoalNameID = goal.m_goalNameID,
                GoalTitle = goal.m_goalTitle, GoalLocation = goal.m_locationName,
                GoalDestinationZone = goal.m_destinationZone ?? "", GoalImage1 = goal.m_displayImage1,
                GoalImage2 = goal.m_displayImage2, PersonaName = "", PatronIcon = patronIcon,
                GoalType = (byte)goal.m_goalType, GoalStatus = 0, GoalCount = instance.CurrentProgress,
                UseTally = goal.m_tallyCounter is not null ? (byte)1 : (byte)0,
                GoalTotal = goal.m_tallyCounter?.m_count ?? 0, TallyText = goal.m_tallyCounter?.m_descriptor ?? "",
                SubscriberGoalTotal = goal.m_tallyCounter?.m_count ?? 0, SendType = 1,
                GoalMadlibs = goalMadlibs, ClientTags = clientTags, NoQuestHelper = goal.m_noQuestHelper ? (byte)1 : (byte)0,
            });
            if (!ClassicQuestClaims.TryPrepareDungeonDialog(goal.m_dialogList, "Prep", quest.ID, instance.ID, out var goalDialog)) return null;
            if (goalDialog is not null) messages.Add(goalDialog);
        }
        return messages;
    }
}
