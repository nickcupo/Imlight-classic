// CLASSIC: the terminal transaction replaces only its supported final-goal path; legacy/tutorial paths stay explicit.
using System;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class QuestService {
    private bool TryCompleteTerminalClaim(Wizard wizard, QuestInstance quest, GoalTemplate goal) {
        if (!ClassicQuestEngine.IsActive) return false;
        var template = _cachedQuestTemplates.Find(candidate => candidate?.m_questName == quest?.QuestName);
        TerminalQuestClaim claim;
        QuestClaimStatus status;
        try {
            status = ClassicQuestClaims.TryClaim(wizard, quest, goal, template, SessionActor.ActorRef,
                GetActiveGameObject(), ResultZoneActor(), out claim, committed => PublishTerminalClaim(wizard, committed));
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return true; }
            throw;
        }
        if (status is QuestClaimStatus.Legacy or QuestClaimStatus.NonTerminal) return false;
        if (status != QuestClaimStatus.Committed) {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return true; }
            // CLASSIC: already claimed (or the saved goal is no longer open): no rewards again, but the client is told
            // what the saved journal says if this session never showed it.
            ResyncQuestJournalAfterNoWrite(wizard, QuestMutationStatus.Unchanged,
                $"the final goal of {quest?.QuestName} ({status})");
            return true; // Receipt replay/refusal must never reach old saves, rolls or result handlers.
        }
        // A separate idempotent ACK-only reconciliation; never a nested claim reward/start save.
        TryGrantDungeonQuests(wizard);
        RefreshTowerGuide();
        return true;
    }

    // Called only inside the claim's acknowledged write lane; a packet/publication failure is quarantined there.
    private void PublishTerminalClaim(Wizard wizard, TerminalQuestClaim claim) {
        RefreshDoorLights();
        foreach (var message in claim.GoalCompletionMessages) SendToSocket(message);
        PublishClaimActions(wizard, claim.GoalActions, claim.Quest.QuestName, claim.Goal.m_goalName);
        PostGoalCompleteEvents(claim.Quest, claim.Goal);
        RefreshDoorLights();
        foreach (var message in claim.QuestCompletionMessages) SendToSocket(message);
        PublishClaimActions(wizard, claim.EndActions, claim.Quest.QuestName);
        foreach (var message in claim.CinematicMessages) SendToSocket(message);
        ClassicBadges.PublishQuestCompleted(wizard, claim.Badges, SendToSocket);
    }

    private void PublishClaimActions(Wizard wizard, IReadOnlyList<QuestClaimAction> actions, string questName, string goalName = null) {
        foreach (var action in actions) {
            if (action.Message is WIZARD_12_PROTOCOL.MSG_LEVELUP) ZoneBroadcast(action.Message, false);
            else if (action.Message is not null) SendToSocket(action.Message);
            if (action.LevelCapAudit is { } level) ClassicGate.LevelCapReached(wizard.CharId, level);
            if (action.StateChange is not null) ZoneBroadcastNoPlayers(action.StateChange);
            if (action.PostEvent is not null) ResultZoneActor()?.Tell(action.PostEvent);
            if (action.Transient is not null) ResultDispatcher.ExecuteFilteredResults(Context,
                new ResultList { m_results = [action.Transient] }, SessionActor.ActorRef, GetActiveGameObject(),
                ResultZoneActor(), questName, goalName);
        }
    }
}
