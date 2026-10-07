// CLASSIC: quest wrappers stage fresh originals and selected journal changes before one acknowledged write.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(BadgeRulesTests))]
public sealed class QuestProgressionAcknowledgementTests {
    [Theory]
    [InlineData("add")] [InlineData("remove")] [InlineData("complete")]
    [InlineData("start")] [InlineData("increment")] [InlineData("goal-complete")]
    public void PublicWrappersCommitFreshAuthorityOnceAndPreserveRuntimeAliases(string operation) {
        using var scope = new Scope(); var f = scope.F; Arrange(f, operation);
        var behavior = f.Live.QuestBehavior; var registry = behavior.Registry;
        var quest = f.Expected; var goal = quest.GoalProgress[0]; var before = Snapshot(f.Live);
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(before, Snapshot(f.Live));
            Assert.Equal(100, f.Working!.Wizard.GameStats.m_currentGold);
            if (IsGoal(operation)) Assert.Contains(f.Working.Wizard, f.Working.Ignored);
            else Assert.DoesNotContain(f.Working.Wizard, f.Working.Ignored);
        };
        Assert.True(RunPublic(f, operation)); Assert.Equal(1, f.Saves); Assert.Equal(1, f.Opened);
        Assert.Same(behavior, f.Live.QuestBehavior); Assert.Same(registry, f.Live.QuestBehavior.Registry);
        Assert.Equal(901, f.Live.GameStats.m_currentGold); Assert.Equal(45UL, registry["LiveUnrelated"]);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        if (operation is "remove" or "complete") {
            Assert.Empty(f.Live.QuestBehavior.CurrentQuestIDs); Assert.Empty(f.Quests);
            Assert.Equal(operation == "complete", f.Saved.QuestBehavior.HasCompletedQuest(TerminalClaimFixture.QuestName));
        }
        else {
            Assert.Same(quest, Assert.Single(f.Live.QuestBehavior.CurrentQuestInstances));
            Assert.Same(goal, quest.GoalProgress[0]);
            Assert.Equal(Assert.Single(f.Quests).GoalProgress[0].CurrentProgress, goal.CurrentProgress);
            Assert.Equal(operation switch { "increment" => 1, "goal-complete" => int.MaxValue, _ => 0 }, goal.CurrentProgress);
        }
        Assert.Empty(f.Receipts); Assert.Equal(0, f.Rolls);
    }

    [Theory]
    [InlineData("set", 0UL)] [InlineData("set", 9UL)] [InlineData("quest-set", 7UL)]
    public void RegistryWrappersPersistOnlySelectedFreshEntriesAndKeepIndependentLiveValues(string operation, ulong value) {
        using var scope = new Scope(); var f = scope.F;
        f.Saved.QuestBehavior.Registry["Selected"] = 3; f.Live.QuestBehavior.Registry["Selected"] = 99;
        var behavior = f.Live.QuestBehavior; var registry = behavior.Registry; var goal = f.Expected.GoalProgress[0];
        f.OnSave = () => {
            Assert.Equal(99UL, registry["Selected"]); Assert.True(WizardCollection.HoldsWriteLane);
            Assert.All(f.Working!.Quests, row => Assert.Contains(row, f.Working.Ignored));
        };
        Assert.True(operation == "set" ? f.Live.SetRegistryValue("Selected", value)
            : f.Live.SetQuestRegistryValue(TerminalClaimFixture.QuestName, "Selected", value));
        var key = operation == "set" ? "Selected" : TerminalClaimFixture.QuestName + "_Selected";
        Assert.Equal(value, f.Saved.QuestBehavior.Registry[key]); Assert.Equal(value, registry[key]);
        Assert.Equal(45UL, registry["LiveUnrelated"]); Assert.Equal(21UL, f.Saved.QuestBehavior.Registry["SavedUnrelated"]);
        Assert.False(f.Saved.QuestBehavior.Registry.ContainsKey("LiveUnrelated")); Assert.Same(behavior, f.Live.QuestBehavior);
        Assert.Same(registry, f.Live.QuestBehavior.Registry); Assert.Same(goal, f.Expected.GoalProgress[0]); Assert.Equal(1, f.Saves);
    }

    [Fact]
    public void TrustedRegistryStageUsesSavedCountersAndCannotMutateQuestOriginals() {
        using var scope = new Scope(); var f = scope.F;
        f.Saved.QuestBehavior.Registry["Counter"] = 7; f.Live.QuestBehavior.Registry["Counter"] = 100;
        Assert.Equal(QuestMutationStatus.Committed, WizardQuestTransactions.TryChangeRegistry(f.Live, journal => {
            Assert.Equal(7UL, journal.Registry["Counter"]); journal.Registry["Counter"] += 2; journal.Registry["Award"] = 1; return true;
        }, out var receipt));
        Assert.Equal(9UL, f.Saved.QuestBehavior.Registry["Counter"]); Assert.Equal(9UL, f.Live.QuestBehavior.Registry["Counter"]);
        Assert.Equal(1UL, receipt.Journal.Registry["Award"]); Assert.Equal(45UL, f.Live.QuestBehavior.Registry["LiveUnrelated"]);
        Assert.Equal(QuestMutationStatus.Refused, WizardQuestTransactions.TryChangeRegistry(f.Live, journal => {
            journal.CurrentQuestInstances[0].GoalProgress[0].CompleteGoal(); journal.Registry["Counter"] = 88; return true;
        }, out var refused));
        Assert.Null(refused); Assert.Equal(1, f.Saves); Assert.Equal(0, f.Quests[0].GoalProgress[0].CurrentProgress);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("missing-reference")] [InlineData("duplicate-reference")]
    [InlineData("foreign-original")] [InlineData("goal-collision")]
    public void CorruptFreshJournalRefusesBeforeSaveWithoutPublishingTheHeldSnapshot(string defect) {
        using var scope = new Scope(); var f = scope.F; var before = Snapshot(f.Live);
        switch (defect) {
            case "missing-reference": f.Quests.Clear(); break;
            case "duplicate-reference": f.Saved.QuestBehavior.CurrentQuestIDs.Add(f.Expected.ID); break;
            case "foreign-original": f.Quests[0].OwnerCharId++; break;
            case "goal-collision": var foreign = TerminalClaimFixture.CloneQuest(f.Quests[0]);
                foreign.ID += 100; foreign.OwnerCharId++; foreign.QuestName = "QA-Foreign"; f.Quests.Add(foreign); break;
        }
        Assert.False(f.Live.IncrementQuestGoal(TerminalClaimFixture.QuestName, TerminalClaimFixture.GoalName));
        Assert.Equal(0, f.Saves); Assert.Equal(before, Snapshot(f.Live)); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("foreign-quest-id")] [InlineData("foreign-goal-id")]
    [InlineData("owned-orphan-name")] [InlineData("completed-marker")]
    public void AddChecksGlobalIdentitiesAndUnreferencedOwnedNamesBeforeStoringAnything(string defect) {
        using var scope = new Scope(); var f = scope.F; Arrange(f, "add"); var before = Snapshot(f.Live);
        if (defect == "completed-marker") f.Saved.QuestBehavior.Registry[TerminalClaimFixture.QuestName + "_Complete"] = 1;
        else {
            var conflict = TerminalClaimFixture.CloneQuest(f.Expected);
            if (defect != "owned-orphan-name") { conflict.OwnerCharId++; conflict.QuestName = "QA-Foreign"; }
            if (defect != "foreign-quest-id") conflict.ID += 100;
            if (defect != "foreign-goal-id") conflict.GoalProgress[0].ID += 100;
            f.Quests.Add(conflict);
        }
        Assert.False(f.Live.AddQuest(f.Expected)); Assert.Equal(0, f.Saves); Assert.Equal(before, Snapshot(f.Live));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void KnownNativePreparationRefusalNeverStoresSavesOrPublishes(bool throws) {
        using var scope = new Scope(); var f = scope.F; Arrange(f, "add"); var before = Snapshot(f.Live); var callbacks = 0;
        Assert.Equal(QuestMutationStatus.Refused, RunTyped(f, "add", out var result, receipt => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(0, f.Saves); Assert.Empty(f.Working!.Quests);
            Assert.Empty(f.Live.QuestBehavior.CurrentQuestIDs); Assert.Equal(100, receipt.Saved.GameStats.m_currentGold);
            Assert.NotSame(f.Expected, receipt.Quest); Assert.NotSame(f.Expected.GoalProgress[0], receipt.Quest.GoalProgress[0]);
            if (throws) throw new InvalidOperationException("authored serializer refusal"); return false;
        }, _ => callbacks++));
        Assert.Null(result); Assert.Empty(f.Quests); Assert.Equal(0, f.Saves); Assert.Equal(0, callbacks);
        Assert.Equal(before, Snapshot(f.Live)); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("add", "before")] [InlineData("add", "lost")] [InlineData("add", "publication")]
    [InlineData("remove", "before")] [InlineData("remove", "lost")] [InlineData("remove", "publication")]
    [InlineData("complete", "before")] [InlineData("complete", "lost")] [InlineData("complete", "publication")]
    [InlineData("start", "before")] [InlineData("start", "lost")] [InlineData("start", "publication")]
    [InlineData("increment", "before")] [InlineData("increment", "lost")] [InlineData("increment", "publication")]
    [InlineData("goal-complete", "before")] [InlineData("goal-complete", "lost")] [InlineData("goal-complete", "publication")]
    public void EveryWrapperQuarantinesUnknownOutcomesInsideItsAckLaneAndRequiresFreshReload(string operation, string failure) {
        using var scope = new Scope(); var f = scope.F; Arrange(f, operation); var live = f.Live; var before = Snapshot(live);
        f.FailSave = failure != "publication"; f.Durable = failure == "lost";
        scope.Dependencies.BeforePublish = _ => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, f.Saves); Assert.Equal(before, Snapshot(live));
            throw new InvalidOperationException("authored publication failure");
        };
        var disposed = false;
        f.OnSave = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live)); Assert.Equal(before, Snapshot(live)); };
        f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); disposed = true; };
        Assert.False(RunPublic(f, operation)); Assert.True(disposed); Assert.Equal(1, f.Saves); Assert.Equal(before, Snapshot(live));
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); Assert.False(RunPublic(f, operation));
        WizardCollection.UpdateCharacterItems(live); Assert.False(live.SetRegistryValue("Retry", 1)); Assert.Equal(1, f.Opened);
        var durable = failure != "before";
        if (operation == "add") Assert.Equal(durable ? 1 : 0, f.Quests.Count);
        else if (operation is "remove" or "complete") Assert.Equal(durable ? 0 : 1, f.Quests.Count);
        else Assert.Equal(durable ? operation switch { "start" => 0, "increment" => 1, _ => int.MaxValue }
            : operation == "start" ? -1 : 0, Assert.Single(f.Quests).GoalProgress[0].CurrentProgress);
        scope.Dependencies.BeforePublish = null; f.FailSave = false; f.OnSave = null; f.OnDispose = null; f.Live = f.Reload();
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live)); Assert.True(f.Live.SetRegistryValue("FreshContinuation", 1));
        Assert.Equal(2, f.Saves); Assert.Equal(1UL, f.Saved.QuestBehavior.Registry["FreshContinuation"]);
    }

    [Fact]
    public async Task PostAckCallbackFailureQuarantinesBeforeQueuedStaleInventorySaveCanEnter() {
        using var scope = new Scope(); var f = scope.F; Arrange(f, "add");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var attempted = new ManualResetEventSlim();
        var prepared = 0; var disposed = false;
        f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); disposed = true; };
        var transaction = Task.Run(() => RunTyped(f, "add", out _, receipt => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(0, f.Saves); Assert.Empty(f.Live.QuestBehavior.CurrentQuestIDs);
            Assert.NotSame(f.Working!.Quests.FirstOrDefault(), receipt.Quest); prepared++; return true;
        }, receipt => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, f.Saves); Assert.Same(f.Expected, Assert.Single(f.Live.QuestBehavior.CurrentQuestInstances));
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); throw new InvalidOperationException("authored packet callback failure");
        }));
        Task? stale = null;
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            stale = Task.Run(() => { attempted.Set(); WizardCollection.UpdateCharacterItems(f.Live); });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5))); Assert.False(stale.IsCompleted); Assert.Equal(1, f.Opened);
        }
        finally { release.Set(); }
        Assert.Equal(QuestMutationStatus.Refused, await transaction); await stale!;
        Assert.True(disposed); Assert.Equal(1, prepared); Assert.Equal(1, f.Saves); Assert.Equal(1, f.Opened);
        Assert.Single(f.Quests); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public void LoadCleanupUsesDurableOrderDeletesExactRowsAndNeverAdoptsOrphans() {
        using var scope = new Scope(); var f = scope.F; var kept = f.Quests[0];
        var duplicate = Extra(kept, 100, kept.QuestName); var completed = Extra(kept, 200, "QA-Completed");
        var orphan = Extra(kept, 300, "QA-Orphan"); var foreign = Extra(kept, 400, "QA-Foreign"); foreign.OwnerCharId++;
        f.Quests.AddRange([duplicate, completed, orphan, foreign]);
        f.Saved.QuestBehavior.CurrentQuestIDs = [duplicate.ID, kept.ID, completed.ID, 999999];
        f.Saved.QuestBehavior.Registry[completed.QuestName + "_Complete"] = 1;
        f.Live = f.Reload(); var held = f.Live.QuestBehavior.CurrentQuestInstances.Single(q => q.ID == duplicate.ID);
        var before = Snapshot(f.Live);
        f.OnSave = () => {
            Assert.Equal(before, Snapshot(f.Live)); Assert.Equal(new[] { kept.ID, completed.ID }, f.Working!.Deleted.Cast<QuestInstance>().Select(q => q.ID).OrderBy(id => id));
            Assert.All(f.Working.Quests.Where(q => q.ID != TerminalClaimFixture.QuestId && q.ID != completed.ID), q => Assert.Contains(q, f.Working.Ignored));
        };
        Assert.Equal(QuestMutationStatus.Committed, WizardQuestTransactions.ReconcileLoadedJournal(f.Live, out var receipt));
        Assert.Same(held, Assert.Single(f.Live.QuestBehavior.CurrentQuestInstances)); Assert.Equal(new[] { duplicate.ID }, f.Saved.QuestBehavior.CurrentQuestIDs);
        Assert.Equal(new[] { duplicate.ID }, receipt.Journal.CurrentQuestIDs); Assert.Equal(1, f.Saves);
        Assert.Equal(new[] { duplicate.ID, orphan.ID, foreign.ID }, f.Quests.Select(q => q.ID).OrderBy(id => id));
        f.OnSave = null; f.Live = f.Reload();
        Assert.Equal(QuestMutationStatus.Unchanged, WizardQuestTransactions.ReconcileLoadedJournal(f.Live, out _));
        Assert.Equal(1, f.Saves); Assert.Equal(duplicate.ID, Assert.Single(f.Live.QuestBehavior.CurrentQuestInstances).ID);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReconcileUnknownWriteRequiresReloadAndDurableCleanupDoesNotSaveAgain(bool durable) {
        using var scope = new Scope(); var f = scope.F; f.Saved.QuestBehavior.CurrentQuestIDs.Add(999999);
        f.FailSave = true; f.Durable = durable; var before = Snapshot(f.Live);
        f.OnDispose = () => Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        Assert.Equal(QuestMutationStatus.Refused, WizardQuestTransactions.ReconcileLoadedJournal(f.Live, out var result));
        Assert.Null(result); Assert.Equal(before, Snapshot(f.Live)); Assert.Equal(1, f.Saves);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        f.FailSave = false; f.OnDispose = null; f.Live = f.Reload();
        Assert.Equal(durable ? QuestMutationStatus.Unchanged : QuestMutationStatus.Committed,
            WizardQuestTransactions.ReconcileLoadedJournal(f.Live, out _)); Assert.Equal(durable ? 1 : 2, f.Saves);
        Assert.Equal(TerminalClaimFixture.QuestId, Assert.Single(f.Live.QuestBehavior.CurrentQuestIDs));
    }

    [Theory]
    [InlineData("start")] [InlineData("increment")] [InlineData("goal-complete")]
    public void HarmlessGoalReplayReturnsUnchangedWithoutNativePreparationOrSave(string operation) {
        using var scope = new Scope(); var f = scope.F;
        if (operation != "start") { f.Quests[0].GoalProgress[0].CompleteGoal(); f.Expected.GoalProgress[0].CompleteGoal(); }
        var callbacks = 0;
        Assert.Equal(QuestMutationStatus.Unchanged, RunTyped(f, operation, out var result, _ => { callbacks++; return true; }, _ => callbacks++));
        Assert.NotNull(result); Assert.Equal(0, callbacks); Assert.Equal(0, f.Saves); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public void RegistryTryAddKeepsExistingZeroAndActiveRawSnapshotAndOwnerlessWritesRefuse() {
        using var scope = new Scope(); var f = scope.F; f.Saved.QuestBehavior.Registry["Existing"] = 0;
        Assert.Equal(QuestMutationStatus.Unchanged, WizardQuestTransactions.TryAddRegistry(f.Live, "Existing", 1, out _));
        Assert.Equal(0UL, f.Saved.QuestBehavior.Registry["Existing"]); Assert.Equal(0, f.Saves);
        Assert.False(WizardCollection.UpdateCharacterQuestBehavior(f.Live)); Assert.False(QuestInstanceCollection.AddQuestInstance(f.Expected));
        Assert.False(QuestInstanceCollection.RemoveQuestInstance(f.Live.CharId, TerminalClaimFixture.QuestName));
        Assert.False(QuestInstanceCollection.RemoveQuestInstance(f.Expected.ID)); Assert.False(QuestInstanceCollection.StartQuestGoal(f.Expected.ID, TerminalClaimFixture.GoalName));
        Assert.False(QuestInstanceCollection.IncrementQuestGoal(f.Expected.ID, TerminalClaimFixture.GoalName)); Assert.False(QuestInstanceCollection.CompleteQuestGoal(f.Expected.ID, TerminalClaimFixture.GoalName));
        Assert.Equal(1, f.Opened); Assert.Equal(0, f.Saves);
    }

    private static QuestInstance Extra(QuestInstance source, ulong offset, string name) {
        var copy = TerminalClaimFixture.CloneQuest(source); copy.ID += offset; copy.QuestName = name;
        foreach (var goal in copy.GoalProgress) goal.ID += offset; return copy;
    }
    private static bool IsGoal(string operation) => operation is "start" or "increment" or "goal-complete";
    private static void Arrange(TerminalClaimFixture f, string operation) {
        if (operation == "add") { f.Quests.Clear(); f.Saved.QuestBehavior.CurrentQuestIDs = []; f.Live.QuestBehavior.CurrentQuestIDs = []; f.Live.QuestBehavior.CurrentQuestInstances = []; }
        if (operation == "start") {
            typeof(GoalInstance).GetProperty(nameof(GoalInstance.CurrentProgress))!.SetValue(f.Quests[0].GoalProgress[0], -1);
            typeof(GoalInstance).GetProperty(nameof(GoalInstance.CurrentProgress))!.SetValue(f.Expected.GoalProgress[0], -1);
        }
        if (operation == "increment") typeof(GoalInstance).GetProperty(nameof(GoalInstance.CurrentProgress))!.SetValue(f.Expected.GoalProgress[0], 4);
    }
    private static string Snapshot(Wizard live) => string.Join("|", live.QuestBehavior.CurrentQuestIDs) + "/"
        + string.Join(";", live.QuestBehavior.CurrentQuestInstances.Select(q => q.ID + ":" + q.QuestName + ":" + string.Join(",", q.GoalProgress.Select(g => g.ID + ":" + g.CurrentProgress))))
        + "/" + string.Join(";", live.QuestBehavior.Registry.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value));
    private static bool RunPublic(TerminalClaimFixture f, string operation) => operation switch {
        "add" => f.Live.AddQuest(f.Expected), "remove" => f.Live.RemoveQuest(TerminalClaimFixture.QuestName),
        "complete" => f.Live.CompleteQuest(TerminalClaimFixture.QuestName), "start" => f.Live.StartQuestGoal(TerminalClaimFixture.QuestName, TerminalClaimFixture.GoalName),
        "increment" => f.Live.IncrementQuestGoal(TerminalClaimFixture.QuestName, TerminalClaimFixture.GoalName),
        "goal-complete" => f.Live.CompleteQuestGoal(TerminalClaimFixture.QuestName, TerminalClaimFixture.GoalName), _ => throw new ArgumentException(operation),
    };
    private static QuestMutationStatus RunTyped(TerminalClaimFixture f, string operation, out QuestMutationReceipt receipt,
        Func<QuestMutationReceipt, bool>? prepare = null, Action<QuestMutationReceipt>? publish = null) => operation switch {
        "add" => WizardQuestTransactions.TryAdd(f.Live, f.Expected, out receipt, prepare!, publish!),
        "remove" => WizardQuestTransactions.TryRemove(f.Live, f.Expected, out receipt, prepare!, publish!),
        "complete" => WizardQuestTransactions.TryComplete(f.Live, f.Expected, out receipt, prepare!, publish!),
        "start" => WizardQuestTransactions.TryStartGoal(f.Live, f.Expected, f.Expected.GoalProgress[0], out receipt, prepare!, publish!),
        "increment" => WizardQuestTransactions.TryIncrementGoal(f.Live, f.Expected, f.Expected.GoalProgress[0], out receipt, prepare!, publish!),
        "goal-complete" => WizardQuestTransactions.TryCompleteGoal(f.Live, f.Expected, f.Expected.GoalProgress[0], out receipt, prepare!, publish!),
        _ => throw new ArgumentException(operation),
    };
    private sealed class Scope : IDisposable {
        internal readonly TerminalClaimFixture F = new();
        internal readonly QuestMutationDependencies Dependencies = new();
        private readonly QuestMutationDependencies? _previous;
        internal Scope() { _previous = WizardQuestTransactions.TestScope.Value; WizardQuestTransactions.TestScope.Value = Dependencies; }
        public void Dispose() { WizardQuestTransactions.TestScope.Value = _previous; F.Dispose(); }
    }
}
