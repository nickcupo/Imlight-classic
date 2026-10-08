// CLASSIC: terminal goal retirement and its admitted persistent rewards share one acknowledged Raven write.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.Classic;

internal enum QuestClaimStatus { Legacy, NonTerminal, Refused, AlreadyClaimed, Committed }

internal sealed record QuestClaimAction(IMessage Message = null, Result Transient = null,
    ResAddDynaMod Dynamod = null, ZONE_102_PROTOCOL.MSG_ENTERSTATE StateChange = null,
    ZONE_102_PROTOCOL.MSG_POSTEVENT PostEvent = null, int? LevelCapAudit = null);

internal sealed record TerminalQuestClaim(QuestInstance Quest, GoalTemplate Goal, QuestClaimReceipt Receipt,
    StackRewardReceipt Stack, IReadOnlyList<QuestClaimAction> GoalActions, IReadOnlyList<QuestClaimAction> EndActions,
    IReadOnlyList<IMessage> CinematicMessages, IReadOnlyList<ClassicBadges.PreparedQuestBadgeAward> Badges,
    IReadOnlyList<IMessage> GoalCompletionMessages, IReadOnlyList<IMessage> QuestCompletionMessages);

// CLASSIC: fixtures author assets/native preparation, never ownership queries or the transaction/save authority.
internal sealed class QuestClaimDependencies {
    internal Func<string, Wizard, DropTableResult> RollQuestReward;
    internal Func<uint, Spell> Spell;
    internal Func<LootInfoList, uint, ByteString> SerializeLoot;
    internal Func<ActorDialog, uint, ByteString> SerializeDialog;
    internal Func<IMessage, bool> Prepare;
    internal Action<Wizard> BeforePublish;
}

internal static partial class ClassicQuestClaims {
    internal const string ReceiptCollection = "ClassicQuestClaims";
    internal static readonly AsyncLocal<QuestClaimDependencies> TestScope = new();
    internal static string ReceiptId(ulong owner, ulong quest) => $"ClassicQuestClaims/{owner}/{quest}";

    // Dedicated tutorials/no-logic templates retain their existing, explicitly unclaimed path.
    internal static bool UsesTerminalClaims(QuestTemplate template)
        => ClassicQuestEngine.IsActive && template is not null && template.m_goalLogic?.Count > 0
            && template.m_questName is not ("Tutorial_Intro" or "WC-TUT-C03-001" or "WC-TUT-C05-001");

    internal static QuestClaimStatus TryClaim(Wizard live, QuestInstance expectedQuest, GoalTemplate expectedGoal,
        QuestTemplate template, IActorRef playerRef, CoreObject playerObj, IActorRef zoneRef,
        out TerminalQuestClaim result, Action<TerminalQuestClaim> afterCommit = null) {
        result = null;
        if (!ClassicQuestEngine.IsActive) return QuestClaimStatus.Legacy;
        if (template is not null && !UsesTerminalClaims(template)) return QuestClaimStatus.Legacy;
        if (live?.CharId is not > 0 || live.QuestBehavior is null || WizardCollection.IsInventorySnapshotUncertain(live)
            || template is null || expectedQuest is null || expectedGoal is null
            || expectedQuest.ID == 0 || expectedQuest.OwnerCharId != live.CharId
            || !string.Equals(Canonical(expectedQuest.QuestName), Canonical(template.m_questName), StringComparison.Ordinal))
            return QuestClaimStatus.Refused;
        var identities = expectedQuest.GoalProgress?.Where(goal => goal?.GoalName == expectedGoal.m_goalName).ToArray() ?? [];
        var authoredGoals = template.m_goals?.Where(goal => goal?.m_goalName == expectedGoal.m_goalName).ToArray() ?? [];
        if (identities.Length != 1 || identities[0].ID == 0 || identities[0].OwnerCharId != live.CharId
            || identities[0].GoalType != expectedGoal.m_goalType || authoredGoals.Length != 1
            || authoredGoals[0].m_goalNameID != expectedGoal.m_goalNameID
            || authoredGoals[0].m_goalType != expectedGoal.m_goalType) return QuestClaimStatus.Refused;
        var goalId = identities[0].ID;
        expectedGoal = authoredGoals[0]; // the authoritative template supplies results, not a lookalike caller object.
        var status = QuestClaimStatus.Refused;
        TerminalQuestClaim stagedResult = null;
        StagedStackRewards stagedStack = null;
        ProgressionReceipt progression = null;
        List<PotionReceipt> potions = [];
        DynamodSet dynamods = null;
        var goldChanged = false;
        var trainingChanged = false;
        var learnedChanged = false;
        Dictionary<string, ulong> registryBefore = null;
        var dependencies = TestScope.Value ?? new();

        var committed = WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
            if (WizardCollection.IsInventorySnapshotUncertain(live) || saved.CharId != live.CharId
                || saved.QuestBehavior is null) return false;
            var existing = session.Load<QuestClaimReceipt>(ReceiptId(live.CharId, expectedQuest.ID));
            if (existing is not null) {
                status = Matches(existing, live.CharId, expectedQuest.ID, goalId, template.m_questName, expectedGoal.m_goalName)
                    ? QuestClaimStatus.AlreadyClaimed : QuestClaimStatus.Refused;
                return false; // No queries/rolls/publication repair of a previously acknowledged claim.
            }
            if (saved.QuestBehavior.HasCompletedQuest(template.m_questName)) return false; // No retroactive legacy award.
            if (!TryReadJournal(session, saved, out var owned, out var ownedOriginals)) return false;
            var targets = owned.Where(quest => quest.ID == expectedQuest.ID
                && Canonical(quest.QuestName) == Canonical(template.m_questName)).ToArray();
            var global = session.Query<QuestInstance>(collectionName: QuestInstanceCollection.CollectionName)
                .Customize(query => query.WaitForNonStaleResults()).Where(quest => quest.ID == expectedQuest.ID).Take(2).ToList();
            if (targets.Length != 1 || global.Count != 1 || !ReferenceEquals(targets[0], global[0])) return false;
            var original = targets[0];
            var freshGoals = original.GoalProgress?.Where(goal => goal?.ID == goalId
                && goal.GoalName == expectedGoal.m_goalName).ToArray() ?? [];
            if (freshGoals.Length != 1 || freshGoals[0].OwnerCharId != live.CharId
                || freshGoals[0].GoalType != expectedGoal.m_goalType || !original.IsGoalActive(expectedGoal.m_goalName)
                || original.GoalProgress.Any(goal => goal is null || goal.ID == 0 || goal.OwnerCharId != live.CharId)
                || original.GoalProgress.Select(goal => goal.ID).Distinct().Count() != original.GoalProgress.Length
                || original.GoalProgress.Select(goal => goal.GoalName).Distinct(StringComparer.Ordinal).Count() != original.GoalProgress.Length)
                return false;

            // Detached proposed state: no live goal or tracked quest row changes before the terminal decision.
            var proposed = ProposeCompletion(original, freshGoals[0]);
            if (QuestService.DetermineNextGoals(template, proposed, out _)) {
                status = QuestClaimStatus.NonTerminal;
                return false;
            }
            saved.QuestBehavior.CurrentQuestInstances = owned.Select(quest => ReferenceEquals(quest, original) ? proposed : quest).ToList();
            registryBefore = saved.QuestBehavior.Registry.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            var goalResults = ResultDispatcher.FilterResultsForWizard(expectedGoal.m_completeResults, saved,
                playerRef, playerObj, zoneRef, proposed.QuestName, expectedGoal.m_goalName);
            var goalState = CopyJournal(saved.QuestBehavior);
            if (!saved.QuestBehavior.CompleteQuest(proposed)) return false;
            var endResults = ResultDispatcher.FilterResultsForWizard(template.m_endResults, saved,
                playerRef, playerObj, zoneRef, proposed.QuestName);
            // Both phases are filtered before any XP, registry, inventory or other reward mutation.
            if (!Supported(goalResults) || !Supported(endResults)) return false;
            try {
                if (!TryStageRewards(session, live, saved, proposed, expectedGoal, template, goalState, goalResults, endResults,
                    playerRef, playerObj, dependencies, out stagedStack, out progression, out potions, out dynamods,
                    out goldChanged, out trainingChanged, out learnedChanged, out stagedResult)) return false;
            }
            catch { return false; } // asset/requirements/native-preparation refusal: no save or live mutation.
            session.Delete(original); // Match existing quest retirement, in the same write as its completed journal.
            session.Store(stagedResult.Receipt, ReceiptId(live.CharId, expectedQuest.ID));
            session.Advanced.GetMetadataFor(stagedResult.Receipt)[Raven.Client.Constants.Documents.Metadata.Collection] = ReceiptCollection;
            ProtectReadOnlyQuestRows(session, ownedOriginals, original);
            WizardInventoryTransactions.ProtectUnmodifiedRows(session); // after the complete outer write/delete set.
            return true;
        }, saved => {
            dependencies.BeforePublish?.Invoke(live);
            // CLASSIC: validate this nonzero staged mana transition before publishing any live claim fields.
            if (progression?.ManaTransition is not null) WizardProgressionTransactions.ValidatePublication(live, progression);
            // Only this claim's committed fields; runtime deck/temporary spell/equipment state is retained.
            live.QuestBehavior.CurrentQuestIDs = [.. saved.QuestBehavior.CurrentQuestIDs];
            live.QuestBehavior.CurrentQuestInstances = [.. saved.QuestBehavior.CurrentQuestInstances];
            foreach (var key in registryBefore.Keys.Concat(saved.QuestBehavior.Registry.Keys).Distinct(StringComparer.Ordinal)) {
                var had = registryBefore.TryGetValue(key, out var before);
                var has = saved.QuestBehavior.Registry.TryGetValue(key, out var after);
                if (had == has && before == after) continue;
                if (has) live.QuestBehavior.Registry[key] = after;
                else live.QuestBehavior.Registry.TryRemove(key, out _);
            }
            if (goldChanged) live.GameStats.m_currentGold = saved.GameStats.m_currentGold;
            if (trainingChanged) live.MagicSchoolBehavior.TrainingPoints = saved.MagicSchoolBehavior.TrainingPoints;
            if (progression is not null) WizardProgressionTransactions.Publish(live, saved, progression);
            foreach (var potion in potions) WizardPotionTransactions.Publish(live, saved, potion);
            if (learnedChanged) live.SpellbookBehavior.LearnedSpellTemplateIds = [.. saved.SpellbookBehavior.LearnedSpellTemplateIds];
            if (dynamods is not null) live.DynamodSet = dynamods;
            stagedResult = stagedResult with { Stack = ClassicStackRewards.Publish(live, saved, stagedStack) };
            afterCommit?.Invoke(stagedResult); // native receipts share the original ACK lane, before another XP writer enters.
        }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        if (!committed) return status;
        result = stagedResult;
        return QuestClaimStatus.Committed;
    }

    internal static List<QuestInstance> ReadOwnedQuestRows(IDocumentSession session, ulong owner)
        => session.Query<QuestInstance>(collectionName: QuestInstanceCollection.CollectionName)
            .Customize(query => query.WaitForNonStaleResults()).Where(quest => quest.OwnerCharId == owner)
            .Take(int.MaxValue).ToList();

    private static bool TryReadJournal(IDocumentSession session, Wizard saved, out List<QuestInstance> current) {
        return TryReadJournal(session, saved, out current, out _);
    }

    private static bool TryReadJournal(IDocumentSession session, Wizard saved, out List<QuestInstance> current,
        out List<QuestInstance> ownedOriginals) {
        current = [];
        ownedOriginals = [];
        var ids = saved.QuestBehavior.CurrentQuestIDs;
        if (ids is null || ids.Any(id => id == 0) || ids.Distinct().Count() != ids.Count) return false;
        ownedOriginals = ReadOwnedQuestRows(session, saved.CharId);
        foreach (var id in ids) {
            var matches = ownedOriginals.Where(quest => quest.ID == id && quest.OwnerCharId == saved.CharId).ToArray();
            if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].QuestName)) return false;
            current.Add(matches[0]);
        }
        return current.Select(quest => Canonical(quest.QuestName)).Distinct(StringComparer.Ordinal).Count() == current.Count;
    }

    // CLASSIC: Raven may normalize untouched loaded quest JSON. Finalize only the captured originals,
    // after the complete write/delete set, retaining explicitly intended mutations and deletions.
    internal static void ProtectReadOnlyQuestRows(IDocumentSession session, IEnumerable<QuestInstance> ownedOriginals,
        params QuestInstance[] intendedOriginals) {
        if (session is null || ownedOriginals is null) return;
        var intended = new HashSet<QuestInstance>(intendedOriginals ?? [], ReferenceEqualityComparer.Instance);
        foreach (var original in ownedOriginals.Where(row => row is not null).Distinct<QuestInstance>(ReferenceEqualityComparer.Instance)) {
            if (!intended.Contains(original)) session.Advanced.IgnoreChangesFor(original);
        }
    }

    private static string Canonical(string name) => QuestNameAliases.Current.Canonical(name ?? string.Empty);
    private static ServerQuestBehavior CopyJournal(ServerQuestBehavior source) {
        var copy = new ServerQuestBehavior { CurrentQuestIDs = [.. source.CurrentQuestIDs],
            CurrentQuestInstances = [.. source.CurrentQuestInstances] };
        foreach (var entry in source.Registry) copy.Registry[entry.Key] = entry.Value;
        return copy;
    }
    private static bool Matches(QuestClaimReceipt receipt, ulong owner, ulong quest, ulong goal, string name, string goalName)
        => receipt.CharId == owner && receipt.QuestId == quest && receipt.GoalId == goal
            && receipt.QuestName == Canonical(name) && receipt.GoalName == goalName;

    private static QuestInstance ProposeCompletion(QuestInstance original, GoalInstance finalGoal) {
        var completed = new GoalInstance { ID = finalGoal.ID, OwnerCharId = finalGoal.OwnerCharId,
            GoalName = finalGoal.GoalName, GoalType = finalGoal.GoalType };
        completed.CompleteGoal();
        return new QuestInstance { ID = original.ID, OwnerCharId = original.OwnerCharId, QuestName = original.QuestName,
            GoalProgress = original.GoalProgress.Select(goal => ReferenceEquals(goal, finalGoal) ? completed : goal).ToArray() };
    }

    private static bool Supported(ResultList results)
        => results.m_results.All(result => result is ResDropTable or ResLearnSpell or ResModifyEntry or ResAddDynaMod
            or ResPostEvent or ResTeleport);
}
