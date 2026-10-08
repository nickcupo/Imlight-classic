// CLASSIC: reagent acquisition/removal must save the tracked count and bag identity once before publication.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ReagentPersistenceRegressionTests {
    public ReagentPersistenceRegressionTests()
        => EquipmentAttachConcurrencyTests.Configure("[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");

    [Fact]
    public void FiveCopyAliasedStackBecomesSixBothSavedAndLiveRatherThanEleven() {
        var store = new ReagentStore(5);
        using var scope = store.Scope();
        var live = store.Live();
        var row = Assert.Single(live.AlchemyBehavior.Reagents);
        var unrelatedStats = live.GameStats;
        Assert.True(live.AddReagent(row));
        Assert.Same(row, Assert.Single(live.AlchemyBehavior.Reagents));
        Assert.Equal(6, row.m_quantity);
        Assert.Equal(6, Assert.Single(store.SavedRows).m_quantity);
        Assert.Equal(ReagentStore.ItemId, row.m_globalID.Full);
        Assert.Equal(ReagentStore.ItemId, Assert.Single(store.Saved.AlchemyBehavior.ReagentItemIds));
        Assert.Equal(1, store.SaveAttempts);
        Assert.Equal(0, store.Stored);
        Assert.Same(unrelatedStats, live.GameStats);
        Assert.Equal(900, live.GameStats.m_currentGold);
        Assert.Equal(100, live.GameStats.m_currentArenaPoints);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Fact]
    public void TemplateAcquisitionReturnsTheActualExistingNativeIdentityAndAcknowledgedCount() {
        var store = new ReagentStore(5);
        using var scope = store.Scope();
        var live = store.Live();
        var row = Assert.Single(live.AlchemyBehavior.Reagents);
        Assert.True(live.AddReagent(ReagentStore.TemplateId, out var updated));
        Assert.Same(row, updated);
        Assert.Equal(6, updated.m_quantity);
        Assert.Equal(ReagentStore.ItemId, updated.m_globalID.Full);
        Assert.Equal(6, Assert.Single(store.SavedRows).m_quantity);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Fact]
    public void DetachedPickupQuantityIsNotTheAcquisitionDeltaAndReportsTheOwnedStack() {
        var store = new ReagentStore(5);
        using var scope = store.Scope();
        var live = store.Live();
        var candidate = ReagentStore.Row(ReagentStore.ItemId + 20, ReagentStore.TemplateId, 99);
        Assert.True(live.AddReagent(candidate));
        Assert.Equal(6, Assert.Single(store.SavedRows).m_quantity);
        Assert.Equal(ReagentStore.ItemId, candidate.m_globalID.Full);
        Assert.Equal(6, candidate.m_quantity);
        Assert.Equal(0, store.Stored);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Fact]
    public void FreshSavedQuantityWinsWithoutSpeculativelyRepairingAnOlderInflatedCount() {
        var store = new ReagentStore(11);
        using var scope = store.Scope();
        var live = store.Live();
        Assert.Single(live.AlchemyBehavior.Reagents).m_quantity = 5;
        Assert.True(live.AddReagent(Assert.Single(live.AlchemyBehavior.Reagents)));
        Assert.Equal(12, Assert.Single(store.SavedRows).m_quantity);
        Assert.Equal(12, Assert.Single(live.AlchemyBehavior.Reagents).m_quantity);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1000)]
    public void FullOrPreviouslyOverfullSavedStackRefusesWithoutMutatingAnything(int count) {
        var store = new ReagentStore(count);
        using var scope = store.Scope();
        var live = store.Live();
        var row = Assert.Single(live.AlchemyBehavior.Reagents);
        Assert.False(live.AddReagent(row));
        Assert.Equal(count, row.m_quantity);
        Assert.Equal(count, Assert.Single(store.SavedRows).m_quantity);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Equal(0, store.Stored);
    }

    [Fact]
    public void LastCopyRemovalDeletesItsSavedRowAndBagReferenceRatherThanSubtractingZero() {
        var store = new ReagentStore(1);
        using var scope = store.Scope();
        var live = store.Live();
        var row = Assert.Single(live.AlchemyBehavior.Reagents);
        Assert.True(live.RemoveReagent(ReagentStore.ItemId, out var removed));
        Assert.Same(row, removed);
        Assert.Equal(0, removed.m_quantity);
        Assert.Empty(store.SavedRows);
        Assert.Empty(store.Saved.AlchemyBehavior.ReagentItemIds);
        Assert.Empty(live.AlchemyBehavior.Reagents);
        Assert.Empty(live.AlchemyBehavior.ReagentItemIds);
        Assert.Equal(1, store.Deleted);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Fact]
    public void PartialRemovalPublishesTheFreshSavedDecrementAndRetainsIdentity() {
        var store = new ReagentStore(5);
        using var scope = store.Scope();
        var live = store.Live();
        Assert.Single(live.AlchemyBehavior.Reagents).m_quantity = 2;
        Assert.True(live.RemoveReagent(ReagentStore.ItemId, out var updated));
        Assert.Equal(4, updated.m_quantity);
        Assert.Equal(4, Assert.Single(store.SavedRows).m_quantity);
        Assert.Equal(ReagentStore.ItemId, Assert.Single(store.Saved.AlchemyBehavior.ReagentItemIds));
        Assert.Equal(0, store.Deleted);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedOrUnacknowledgedSavedAddOrRemovalNeverPublishesOrRetries(bool remove, bool committed) {
        var store = new ReagentStore(remove ? 1 : 5) { FailSave = true, CommitBeforeFailure = committed };
        using var scope = store.Scope();
        var live = store.Live();
        var rows = live.AlchemyBehavior.Reagents;
        var ids = live.AlchemyBehavior.ReagentItemIds;
        var original = Assert.Single(rows);
        Assert.Throws<InvalidOperationException>(() => {
            if (remove) live.RemoveReagent(ReagentStore.ItemId, out _);
            else live.AddReagent(original);
        });
        Assert.Same(rows, live.AlchemyBehavior.Reagents);
        Assert.Same(ids, live.AlchemyBehavior.ReagentItemIds);
        Assert.Equal(remove ? 1 : 5, original.m_quantity);
        Assert.Equal(1, store.SaveAttempts);
        Assert.Equal(committed ? (remove ? 0 : 6) : (remove ? 1 : 5), store.SavedRows.Sum(row => row.m_quantity));
        Assert.Equal(committed && remove ? 0 : 1, store.Saved.AlchemyBehavior.ReagentItemIds.Count);
        Assert.Equal(900, live.GameStats.m_currentGold);
        Assert.Equal(100, live.GameStats.m_currentArenaPoints);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.False(live.AddReagent(original));
        Assert.False(live.RemoveReagent(ReagentStore.ItemId, out _));
        Assert.Equal(1, store.SaveAttempts); // no later generic delta may retry through this uncertain live snapshot
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingSavedWizardRefusesWithoutChangingLiveBag(bool remove) {
        var store = new ReagentStore(5) { RefuseLoad = true };
        using var scope = store.Scope();
        var live = store.Live();
        Assert.False(remove ? live.RemoveReagent(ReagentStore.ItemId, out _) : live.AddReagent(Assert.Single(live.AlchemyBehavior.Reagents)));
        Assert.Equal(5, Assert.Single(live.AlchemyBehavior.Reagents).m_quantity);
        Assert.Equal(5, Assert.Single(store.SavedRows).m_quantity);
        Assert.Equal(0, store.SaveAttempts);
    }

    [Theory]
    [InlineData("duplicate-reference")]
    [InlineData("missing-row")]
    [InlineData("foreign-row")]
    [InlineData("duplicate-identity")]
    [InlineData("duplicate-template")]
    [InlineData("orphan-template")]
    [InlineData("zero-count")]
    public void AmbiguousMissingOrUnownedSavedReferencesRefuseUnchanged(string invalid) {
        var store = new ReagentStore(5);
        var live = store.Live();
        switch (invalid) {
            case "duplicate-reference": store.Saved.AlchemyBehavior.ReagentItemIds.Add(ReagentStore.ItemId); break;
            case "missing-row": store.SavedRows.Clear(); break;
            case "foreign-row": store.SavedRows[0].m_characterId = ReagentStore.Char + 1; break;
            case "duplicate-identity": store.SavedRows.Add(store.SavedRows[0] with { }); break;
            case "duplicate-template": store.SavedRows.Add(ReagentStore.Row(ReagentStore.ItemId + 1, ReagentStore.TemplateId, 7)); break;
            case "orphan-template": store.Saved.AlchemyBehavior.ReagentItemIds.Clear(); break;
            case "zero-count": store.SavedRows[0].m_quantity = 0; break;
        }
        var before = store.SavedRows.Select(row => (row.m_globalID.Full, row.m_characterId.Full, row.m_quantity)).ToArray();
        var ids = store.Saved.AlchemyBehavior.ReagentItemIds.ToArray();
        using var scope = store.Scope();
        Assert.False(live.AddReagent(Assert.Single(live.AlchemyBehavior.Reagents)));
        Assert.False(live.RemoveReagent(ReagentStore.ItemId, out _));
        Assert.Equal(before, store.SavedRows.Select(row => (row.m_globalID.Full, row.m_characterId.Full, row.m_quantity)).ToArray());
        Assert.Equal(ids, store.Saved.AlchemyBehavior.ReagentItemIds);
        Assert.Equal(5, Assert.Single(live.AlchemyBehavior.Reagents).m_quantity);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Equal(0, store.Stored);
        Assert.Equal(0, store.Deleted);
    }

    [Fact]
    public void ForeignCandidateCannotChangeOwnershipOrClaimAnExistingIdentity() {
        var store = new ReagentStore(5);
        using var scope = store.Scope();
        var live = store.Live();
        var candidate = ReagentStore.Row(ReagentStore.ItemId, ReagentStore.TemplateId, 5);
        candidate.m_characterId = ReagentStore.Char + 1;
        Assert.False(live.AddReagent(candidate));
        Assert.Equal(ReagentStore.Char + 1, candidate.m_characterId.Full);
        Assert.Equal(5, candidate.m_quantity);
        Assert.Equal(5, Assert.Single(store.SavedRows).m_quantity);
        Assert.Equal(0, store.SaveAttempts);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(int.MaxValue)]
    public void StagingInvalidBulkQuantitiesChangesNoTrackedRowOrReference(int quantity) {
        var store = new ReagentStore(5);
        using var scope = store.Scope();
        using var session = store.Open();
        var saved = store.ForStage(session);
        var beforeIds = saved.AlchemyBehavior.ReagentItemIds;
        Assert.False(WizardReagentCollection.TryStageAdd(session, saved, ReagentStore.Row(), quantity, out var added));
        Assert.False(WizardReagentCollection.TryStageRemove(session, saved, ReagentStore.ItemId, quantity, out var removed));
        Assert.Null(added);
        Assert.Null(removed);
        Assert.Equal(5, Assert.Single(ReagentStore.Session(session).Rows).m_quantity);
        Assert.Same(beforeIds, saved.AlchemyBehavior.ReagentItemIds);
        Assert.Equal(0, store.SaveAttempts);
    }

    [Fact]
    public void SessionOnlyBulkStagingDoesNotSaveOrPublishBeforeItsEnclosingCommit() {
        var store = new ReagentStore(5);
        using var scope = store.Scope();
        var live = store.Live();
        ClientReagentItem staged = null!;
        Assert.True(WizardCollection.CommitCharacterMutation(ReagentStore.Char, (session, saved) => {
            Assert.True(WizardReagentCollection.TryStageAdd(session, saved, ReagentStore.Row(), 3, out staged));
            Assert.Equal(8, staged.m_quantity);
            Assert.Equal(5, Assert.Single(live.AlchemyBehavior.Reagents).m_quantity);
            Assert.Equal(0, store.SaveAttempts);
            return true;
        }, saved => WizardReagentCollection.PublishCommittedBag(live, saved, staged)));
        Assert.Equal(8, Assert.Single(store.SavedRows).m_quantity);
        Assert.Equal(8, Assert.Single(live.AlchemyBehavior.Reagents).m_quantity);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Fact]
    public void NewStackAndSavedReferenceAreOneAcknowledgedCommitWithTheRequestedBulkCount() {
        var store = new ReagentStore(0);
        using var scope = store.Scope();
        var live = store.Live();
        var candidate = ReagentStore.Row(quantity: 70);
        ClientReagentItem staged = null!;
        Assert.True(WizardCollection.CommitCharacterMutation(ReagentStore.Char,
            (session, saved) => WizardReagentCollection.TryStageAdd(session, saved, candidate, 3, out staged),
            saved => WizardReagentCollection.PublishCommittedBag(live, saved, staged)));
        Assert.Equal(3, Assert.Single(store.SavedRows).m_quantity);
        Assert.Equal(3, Assert.Single(live.AlchemyBehavior.Reagents).m_quantity);
        Assert.Equal(70, candidate.m_quantity); // caller snapshot never becomes the delta or tracked persisted row
        Assert.NotSame(candidate, Assert.Single(store.SavedRows));
        Assert.Equal(ReagentStore.ItemId, Assert.Single(store.Saved.AlchemyBehavior.ReagentItemIds));
        Assert.Equal(1, store.Stored);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedNewStackSaveNeverPublishesEitherItsRowReferenceOrCandidateFields(bool committed) {
        var store = new ReagentStore(0) { FailSave = true, CommitBeforeFailure = committed };
        using var scope = store.Scope();
        var live = store.Live();
        var candidate = ReagentStore.Row(quantity: 70);
        candidate.m_characterId = 0;
        Assert.Throws<InvalidOperationException>(() => live.AddReagent(candidate));
        Assert.Empty(live.AlchemyBehavior.ReagentItemIds);
        Assert.Empty(live.AlchemyBehavior.Reagents);
        Assert.Equal(0ul, candidate.m_characterId.Full);
        Assert.Equal(70, candidate.m_quantity);
        Assert.Equal(committed ? 1 : 0, store.SavedRows.Count);
        Assert.Equal(committed ? 1 : 0, store.Saved.AlchemyBehavior.ReagentItemIds.Count);
        Assert.Equal(1, store.SaveAttempts);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Fact]
    public void BulkOverCapacityAndOverRemovalRefuseWithoutPartialMutation() {
        var store = new ReagentStore(998);
        using var scope = store.Scope();
        using var session = store.Open();
        var saved = store.ForStage(session);
        Assert.False(WizardReagentCollection.TryStageAdd(session, saved, ReagentStore.Row(), 2, out _));
        Assert.False(WizardReagentCollection.TryStageRemove(session, saved, ReagentStore.ItemId, 999, out _));
        Assert.Equal(998, Assert.Single(ReagentStore.Session(session).Rows).m_quantity);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Equal(0, store.Deleted);
    }

    [Fact]
    public void OtherReagentAndNonReagentLiveStateRemainUntouchedByAcknowledgedPublication() {
        var store = new ReagentStore(5);
        store.SavedRows.Add(ReagentStore.Row(ReagentStore.ItemId + 1, ReagentStore.TemplateId + 1, 9));
        store.Saved.AlchemyBehavior.ReagentItemIds.Add(ReagentStore.ItemId + 1);
        using var scope = store.Scope();
        var live = store.Live();
        var other = live.AlchemyBehavior.Reagents[1];
        var originalAlchemy = live.AlchemyBehavior;
        Assert.True(live.AddReagent(live.AlchemyBehavior.Reagents[0]));
        Assert.Same(other, live.AlchemyBehavior.Reagents[1]);
        Assert.Same(originalAlchemy, live.AlchemyBehavior);
        Assert.Equal(9, other.m_quantity);
        Assert.Equal(100, live.GameStats.m_currentArenaPoints);
        Assert.Equal(900, live.GameStats.m_currentGold);
    }

    [Fact]
    public void OwnedBagHydrationMaterializesExactSavedReferencesWithoutSavingOrRepairingCounts() {
        var store = new ReagentStore(11);
        store.SavedRows.Add(ReagentStore.Row(ReagentStore.ItemId + 1, ReagentStore.TemplateId + 1, 3)); // orphan retained
        using var scope = store.Scope();
        using var session = store.Open();
        var saved = store.ForStage(session);
        Assert.True(WizardReagentCollection.TryReadOwnedBag(session, saved, out var owned));
        Assert.Equal(ReagentStore.ItemId, Assert.Single(owned).m_globalID.Full);
        Assert.Equal(11, Assert.Single(owned).m_quantity);
        Assert.Equal(2, ReagentStore.Session(session).Rows.Count);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Equal(0, store.Deleted);
    }

    [Fact]
    public void LegacyDanglingReferencesAreDroppedOnLoadAndTheBagThenValidates() {
        var store = new ReagentStore(4);
        store.Saved.AlchemyBehavior.ReagentItemIds.Add(ReagentStore.ItemId + 99); // row deleted before the atomic bag
        using var scope = store.Scope();
        var live = store.Live();
        live.AlchemyBehavior.ReagentItemIds = [.. store.Saved.AlchemyBehavior.ReagentItemIds];
        using var session = store.Open();
        Assert.False(WizardReagentCollection.TryReadOwnedBag(session, store.ForStage(session), out _));
        WizardReagentCollection.RepairDanglingReferences(live, session);
        Assert.Equal(ReagentStore.ItemId, Assert.Single(live.AlchemyBehavior.ReagentItemIds));
        Assert.Equal(ReagentStore.ItemId, Assert.Single(store.Saved.AlchemyBehavior.ReagentItemIds));
        Assert.True(WizardReagentCollection.TryReadOwnedBag(session, store.ForStage(session), out var owned));
        Assert.Equal(4, Assert.Single(owned).m_quantity);
        Assert.Equal(1, Assert.Single(store.SavedRows) is { } ? 1 : 0);
    }

    [Fact]
    public void OwnedBagHydrationRefusesDuplicateOrForeignReferencesWithoutDeletingAnything() {
        var store = new ReagentStore(5);
        store.SavedRows[0].m_characterId = ReagentStore.Char + 1;
        using var scope = store.Scope();
        using var session = store.Open();
        var saved = store.ForStage(session);
        Assert.False(WizardReagentCollection.TryReadOwnedBag(session, saved, out var owned));
        Assert.Empty(owned);
        Assert.Equal(5, Assert.Single(ReagentStore.Session(session).Rows).m_quantity);
        Assert.Equal(ReagentStore.ItemId, Assert.Single(saved.AlchemyBehavior.ReagentItemIds));
        Assert.Equal(0, store.SaveAttempts);
        Assert.Equal(0, store.Deleted);
    }

    [Fact]
    public async Task TwoConcurrentAliasedAcquisitionsUseTheSameCharacterLaneAndSaveSevenOnceEach() {
        var store = new ReagentStore(5);
        using var scope = store.Scope();
        var first = store.Live();
        var second = store.Live();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var saves = 0;
        store.BeforeSave = () => {
            if (Interlocked.Increment(ref saves) != 1) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test did not release reagent save");
        };
        var firstTask = Task.Factory.StartNew(() => first.AddReagent(Assert.Single(first.AlchemyBehavior.Reagents)),
            TaskCreationOptions.LongRunning);
        Task<bool>? secondTask = null;
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            secondTask = Task.Factory.StartNew(() => {
                secondStarted.TrySetResult();
                return second.AddReagent(Assert.Single(second.AlchemyBehavior.Reagents));
            }, TaskCreationOptions.LongRunning);
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(firstTask.IsCompleted);
            Assert.False(secondTask.IsCompleted);
            Assert.Equal(5, Assert.Single(first.AlchemyBehavior.Reagents).m_quantity);
            Assert.Equal(5, Assert.Single(second.AlchemyBehavior.Reagents).m_quantity);
            release.Set();
            Assert.True(await firstTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(await secondTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(7, Assert.Single(store.SavedRows).m_quantity);
            Assert.Equal(6, Assert.Single(first.AlchemyBehavior.Reagents).m_quantity);
            Assert.Equal(7, Assert.Single(second.AlchemyBehavior.Reagents).m_quantity);
            Assert.Equal(2, store.SaveAttempts);
        }
        finally {
            release.Set();
            await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
            if (secondTask is not null) await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task AQueuedAcquisitionRechecksUncertainStateInsideTheLaneBeforeApplyingAnotherDelta() {
        var store = new ReagentStore(5) { FailSave = true, CommitBeforeFailure = true };
        using var scope = store.Scope();
        var live = store.Live();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        store.BeforeSave = () => {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test did not release uncertain reagent save");
        };
        var first = Task.Factory.StartNew(() => Assert.Throws<InvalidOperationException>(
            () => live.AddReagent(Assert.Single(live.AlchemyBehavior.Reagents))), TaskCreationOptions.LongRunning);
        Task<bool>? second = null;
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = Task.Factory.StartNew(() => {
                secondStarted.TrySetResult();
                return live.AddReagent(Assert.Single(live.AlchemyBehavior.Reagents));
            }, TaskCreationOptions.LongRunning);
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(second.IsCompleted);
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
            Assert.Equal(5, Assert.Single(live.AlchemyBehavior.Reagents).m_quantity);
            Assert.Equal(6, Assert.Single(store.SavedRows).m_quantity);
            Assert.Equal(1, store.SaveAttempts);
        }
        finally {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void AReadFailureBeforeStagingDoesNotMarkTheSnapshotOrAttemptASave() {
        var store = new ReagentStore(5);
        using var scope = store.Scope();
        var live = store.Live();
        WizardReagentCollection.TestRowsScope.Value = _ => throw new InvalidOperationException("injected failed fresh read");
        Assert.Throws<InvalidOperationException>(() => live.AddReagent(Assert.Single(live.AlchemyBehavior.Reagents)));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.Equal(5, Assert.Single(live.AlchemyBehavior.Reagents).m_quantity);
        Assert.Equal(5, Assert.Single(store.SavedRows).m_quantity);
        Assert.Equal(0, store.SaveAttempts);
    }

    [Fact]
    public void NormalAndRareBatchUsesOneFreshReadAndOneAcknowledgedSave() {
        var store = new ReagentStore(5);
        var rare = ReagentStore.Row(ReagentStore.ItemId + 1, ReagentStore.TemplateId + 1, 2);
        store.SavedRows.Add(rare);
        store.Saved.AlchemyBehavior.ReagentItemIds.Add(rare.m_globalID.Full);
        using var scope = store.Scope();
        var live = store.Live();
        var normalLive = live.AlchemyBehavior.Reagents[0];
        var rareLive = live.AlchemyBehavior.Reagents[1];
        Assert.True(WizardReagentCollection.AddReagents(live, [new(normalLive, 3), new(rareLive, 1)], out var receipts));
        Assert.Equal(2, receipts.Count);
        Assert.Same(normalLive, receipts[0].Reagent);
        Assert.Equal((3, 8), (receipts[0].Acquired, receipts[0].Reagent.m_quantity));
        Assert.Same(rareLive, receipts[1].Reagent);
        Assert.Equal((1, 3), (receipts[1].Acquired, receipts[1].Reagent.m_quantity));
        Assert.Equal(new[] { 8, 3 }, store.SavedRows.Select(row => row.m_quantity));
        Assert.Equal(1, store.Reads);
        Assert.Equal(1, store.SaveAttempts);
        Assert.Equal(0, store.Stored);
    }

    [Fact]
    public void BatchAcquiresOnlyFittingNormalCopiesAndSavesNewRareIdentityTogether() {
        var store = new ReagentStore(998);
        using var scope = store.Scope();
        var live = store.Live();
        var rare = ReagentStore.Row(ReagentStore.ItemId + 1, ReagentStore.TemplateId + 1, 81);
        Assert.True(WizardReagentCollection.AddReagents(live,
            [new(Assert.Single(live.AlchemyBehavior.Reagents), 3), new(rare, 1)], out var receipts));
        Assert.Equal(new[] { 1, 1 }, receipts.Select(receipt => receipt.Acquired));
        Assert.Equal(new[] { 999, 1 }, receipts.Select(receipt => receipt.Reagent.m_quantity));
        Assert.Equal(new[] { ReagentStore.ItemId, rare.m_globalID.Full }, store.Saved.AlchemyBehavior.ReagentItemIds);
        Assert.Equal(new[] { 999, 1 }, store.SavedRows.Select(row => row.m_quantity));
        Assert.Equal(81, rare.m_quantity); // explicit rare request1, not its caller snapshot81
        Assert.Equal(1, store.Stored);
        Assert.Equal(1, store.Reads);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1000)]
    public void FullOrInflatedNormalStackIsSkippedWhileRareAcquisitionCommits(int count) {
        var store = new ReagentStore(count);
        using var scope = store.Scope();
        var live = store.Live();
        var normal = Assert.Single(live.AlchemyBehavior.Reagents);
        var rare = ReagentStore.Row(ReagentStore.ItemId + 1, ReagentStore.TemplateId + 1, 7);
        Assert.True(WizardReagentCollection.AddReagents(live, [new(normal, 3), new(rare, 1)], out var receipts));
        var receipt = Assert.Single(receipts);
        Assert.Equal(rare.m_templateID.Full, receipt.Reagent.m_templateID.Full);
        Assert.Equal(1, receipt.Acquired);
        Assert.Equal(count, store.SavedRows[0].m_quantity);
        Assert.Equal(count, normal.m_quantity);
        Assert.Equal(1, store.SavedRows[1].m_quantity);
        Assert.Equal(1, store.Reads);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Fact]
    public void AllFullBatchRefusesWithoutAnySaveOrReceipt() {
        var store = new ReagentStore(999);
        store.SavedRows.Add(ReagentStore.Row(ReagentStore.ItemId + 1, ReagentStore.TemplateId + 1, 999));
        store.Saved.AlchemyBehavior.ReagentItemIds.Add(ReagentStore.ItemId + 1);
        using var scope = store.Scope();
        var live = store.Live();
        Assert.False(WizardReagentCollection.AddReagents(live,
            [new(live.AlchemyBehavior.Reagents[0], 3), new(live.AlchemyBehavior.Reagents[1], 1)], out var receipts));
        Assert.Empty(receipts);
        Assert.Equal(new[] { 999, 999 }, store.SavedRows.Select(row => row.m_quantity));
        Assert.Equal(1, store.Reads);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Equal(0, store.Stored);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Fact]
    public void SameTemplateBatchSumsExplicitRequestsAndPublishesOneCanonicalIdentity() {
        var store = new ReagentStore(5);
        using var scope = store.Scope();
        var live = store.Live();
        var first = Assert.Single(live.AlchemyBehavior.Reagents);
        var second = ReagentStore.Row(ReagentStore.ItemId + 10, ReagentStore.TemplateId, 83);
        Assert.True(WizardReagentCollection.AddReagents(live,
            [new(first, 2), new(second, 3), new(first, 1)], out var receipts));
        var receipt = Assert.Single(receipts);
        Assert.Same(first, receipt.Reagent);
        Assert.Equal(6, receipt.Acquired);
        Assert.Equal(11, receipt.Reagent.m_quantity);
        Assert.Equal(11, Assert.Single(store.SavedRows).m_quantity);
        Assert.Equal(83, second.m_quantity);
        Assert.Equal(1, store.Reads);
        Assert.Equal(1, store.SaveAttempts);
        Assert.Equal(0, store.Stored);
    }

    [Fact]
    public void SameTemplateNewCandidatesUseTheFirstCanonicalIdentityAndOnlyOneNewRow() {
        var store = new ReagentStore(0);
        using var scope = store.Scope();
        var live = store.Live();
        var first = ReagentStore.Row(quantity: 50);
        var second = ReagentStore.Row(ReagentStore.ItemId + 10, ReagentStore.TemplateId, 83);
        Assert.True(WizardReagentCollection.AddReagents(live, [new(first, 2), new(second, 3)], out var receipts));
        Assert.Equal(5, Assert.Single(receipts).Acquired);
        Assert.Equal(ReagentStore.ItemId, Assert.Single(receipts).Reagent.m_globalID.Full);
        Assert.Equal(5, Assert.Single(store.SavedRows).m_quantity);
        Assert.Equal(ReagentStore.ItemId, Assert.Single(store.Saved.AlchemyBehavior.ReagentItemIds));
        Assert.Equal(1, store.Stored);
        Assert.Equal(1, store.Reads);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Fact]
    public void LargeGroupedRequestsUseLongArithmeticAndAcquireOnlyTheExistingStackCapacity() {
        var store = new ReagentStore(0);
        using var scope = store.Scope();
        var live = store.Live();
        var candidate = ReagentStore.Row(quantity: 70);
        Assert.True(WizardReagentCollection.AddReagents(live,
            [new(candidate, int.MaxValue), new(candidate, int.MaxValue)], out var receipts));
        Assert.Equal(999, Assert.Single(receipts).Acquired);
        Assert.Equal(999, Assert.Single(receipts).Reagent.m_quantity);
        Assert.Equal(999, Assert.Single(store.SavedRows).m_quantity);
        Assert.Equal(70, candidate.m_quantity);
        Assert.Equal(1, store.Reads);
        Assert.Equal(1, store.SaveAttempts);
        Assert.Equal(1, store.Stored);
    }

    [Fact]
    public void StaleBatchSnapshotsUseFreshSavedCountsAndRetainUnrelatedWalletFields() {
        var store = new ReagentStore(11);
        store.SavedRows.Add(ReagentStore.Row(ReagentStore.ItemId + 1, ReagentStore.TemplateId + 1, 997));
        store.Saved.AlchemyBehavior.ReagentItemIds.Add(ReagentStore.ItemId + 1);
        using var scope = store.Scope();
        var live = store.Live();
        live.AlchemyBehavior.Reagents[0].m_quantity = 5;
        live.AlchemyBehavior.Reagents[1].m_quantity = 20;
        Assert.True(WizardReagentCollection.AddReagents(live,
            [new(live.AlchemyBehavior.Reagents[0], 2), new(live.AlchemyBehavior.Reagents[1], 3)], out var receipts));
        Assert.Equal(new[] { 2, 2 }, receipts.Select(receipt => receipt.Acquired));
        Assert.Equal(new[] { 13, 999 }, receipts.Select(receipt => receipt.Reagent.m_quantity));
        Assert.Equal(new[] { 13, 999 }, store.SavedRows.Select(row => row.m_quantity));
        Assert.Equal(900, store.Saved.GameStats.m_currentGold);
        Assert.Equal(100, store.Saved.GameStats.m_currentArenaPoints);
        Assert.Equal(900, live.GameStats.m_currentGold);
        Assert.Equal(100, live.GameStats.m_currentArenaPoints);
        Assert.Equal(1, store.Reads);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedNormalRareBatchNeverSavesAPartialGroupOrPublishesAReceipt(bool newNormal, bool committed) {
        var store = new ReagentStore(newNormal ? 0 : 5) { FailSave = true, CommitBeforeFailure = committed };
        using var scope = store.Scope();
        var live = store.Live();
        var normal = newNormal ? ReagentStore.Row(quantity: 53) : Assert.Single(live.AlchemyBehavior.Reagents);
        var rare = ReagentStore.Row(ReagentStore.ItemId + 1, ReagentStore.TemplateId + 1, 81);
        IReadOnlyList<ReagentAcquisitionReceipt> receipts = [];
        Assert.Throws<InvalidOperationException>(() => WizardReagentCollection.AddReagents(live,
            [new(normal, 3), new(rare, 1)], out receipts));
        Assert.Empty(receipts);
        Assert.Equal(newNormal ? 0 : 1, live.AlchemyBehavior.ReagentItemIds.Count);
        Assert.Equal(newNormal ? 0 : 1, live.AlchemyBehavior.Reagents.Count);
        Assert.Equal(newNormal ? 53 : 5, normal.m_quantity);
        Assert.Equal(81, rare.m_quantity);
        if (committed) {
            Assert.Equal(new[] { newNormal ? 3 : 8, 1 }, store.SavedRows.Select(row => row.m_quantity));
            Assert.Equal(2, store.Saved.AlchemyBehavior.ReagentItemIds.Count);
        }
        else {
            Assert.Equal(newNormal ? 0 : 1, store.SavedRows.Count);
            Assert.Equal(newNormal ? 0 : 1, store.Saved.AlchemyBehavior.ReagentItemIds.Count);
        }
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.False(WizardReagentCollection.AddReagents(live, [new(normal, 3), new(rare, 1)], out _));
        Assert.Equal(1, store.Reads);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Theory]
    [InlineData("foreign", false)]
    [InlineData("foreign", true)]
    [InlineData("collision", false)]
    [InlineData("collision", true)]
    [InlineData("duplicate-template-row", false)]
    [InlineData("duplicate-template-row", true)]
    public void InvalidRareIdentityRefusesWholeBatchBeforeNormalTrackedCountChanges(string invalid, bool normalFull) {
        var store = new ReagentStore(normalFull ? 999 : 5);
        using var scope = store.Scope();
        var live = store.Live();
        var rare = ReagentStore.Row(ReagentStore.ItemId + 1, ReagentStore.TemplateId + 1, 81);
        switch (invalid) {
            case "foreign": rare.m_characterId = ReagentStore.Char + 1; break;
            case "collision": rare.m_globalID = ReagentStore.ItemId; break;
            case "duplicate-template-row":
                store.SavedRows.Add(rare with { });
                store.SavedRows.Add(rare with { m_globalID = ReagentStore.ItemId + 2 });
                break;
        }
        var original = store.SavedRows.Select(row => (row.m_globalID.Full, row.m_templateID.Full, row.m_quantity)).ToArray();
        Assert.False(WizardReagentCollection.AddReagents(live,
            [new(Assert.Single(live.AlchemyBehavior.Reagents), 3), new(rare, 1)], out var receipts));
        Assert.Empty(receipts);
        Assert.Equal(original, store.SavedRows.Select(row => (row.m_globalID.Full, row.m_templateID.Full, row.m_quantity)).ToArray());
        Assert.Equal(normalFull ? 999 : 5, Assert.Single(live.AlchemyBehavior.Reagents).m_quantity);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Equal(0, store.Stored);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
        if (store.LastSession is not null)
            Assert.Equal(normalFull ? 999 : 5, store.LastSession.Rows[0].m_quantity);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("collision")]
    public void AFullStackStillValidatesItsCandidateBeforeAllowingAnotherTemplateAcquisition(string invalid) {
        var store = new ReagentStore(999);
        using var scope = store.Scope();
        var live = store.Live();
        var fullCandidate = Assert.Single(live.AlchemyBehavior.Reagents) with { };
        var rare = ReagentStore.Row(ReagentStore.ItemId + 1, ReagentStore.TemplateId + 1, 77);
        if (invalid == "foreign") fullCandidate.m_characterId = ReagentStore.Char + 1;
        else {
            var foreign = ReagentStore.Row(ReagentStore.ItemId + 20, ReagentStore.TemplateId + 20, 9);
            foreign.m_characterId = ReagentStore.Char + 1;
            store.SavedRows.Add(foreign);
            fullCandidate.m_globalID = foreign.m_globalID;
        }
        Assert.False(WizardReagentCollection.AddReagents(live, [new(fullCandidate, 3), new(rare, 1)], out var receipts));
        Assert.Empty(receipts);
        Assert.Equal(999, store.SavedRows[0].m_quantity);
        Assert.DoesNotContain(store.SavedRows, row => row.m_templateID.Full == rare.m_templateID.Full);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Equal(0, store.Stored);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Fact]
    public void RealQueryRequestsEveryRowAndAnExplicitBoundedFreshIndexResult() {
        using var store = new DocumentStore { Urls = ["http://127.0.0.1:1"], Database = "reagent-query" }.Initialize();
        using var session = store.OpenSession();
        var query = (IRavenQueryInspector) WizardReagentCollection.ReagentQuery(session);
        var request = query.GetIndexQuery(false);
        Assert.True(request.WaitForNonStaleResults);
        Assert.Equal(TimeSpan.FromSeconds(5), request.WaitForNonStaleResultsTimeout);
        Assert.Contains("limit", request.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(request.QueryParameters.Values,
            value => (value is int || value is long) && Convert.ToInt64(value) == int.MaxValue);
    }

    // CLASSIC: detached documents on load/save model acknowledged, refused and uncertain Raven writes without
    // starting a database. The real shared lane, session staging, native identities and publication remain in use.
    internal sealed class ReagentStore {
        internal const ulong Char = 772961, ItemId = 772962, TemplateId = 772963;
        internal Wizard Saved;
        internal List<ClientReagentItem> SavedRows;
        internal bool FailSave, CommitBeforeFailure, RefuseLoad;
        internal int SaveAttempts, Stored, Deleted, Reads;
        internal System.Action? BeforeSave;
        internal ReagentSession? LastSession;

        internal ReagentStore(int count) {
            SavedRows = count == 0 ? [] : [Row(quantity: count)];
            Saved = new Wizard { CharId = Char, PlayerNameBehavior = new() { NameOverride = "Reagent fixture" },
                AlchemyBehavior = new() { ReagentItemIds = count == 0 ? [] : [ItemId], Reagents = [] },
                GameStats = new ServerWizGameStats(default, 1) { m_currentGold = 900, m_currentArenaPoints = 100 },
            };
        }

        internal static ClientReagentItem Row(ulong id = ItemId, ulong template = TemplateId, int quantity = 5)
            => new() { m_globalID = id, m_permID = id, m_characterId = Char, m_templateID = template, m_quantity = quantity };

        internal Wizard Live() {
            var live = Clone(Saved);
            live.AlchemyBehavior.Reagents = [.. SavedRows.Where(row => row.m_characterId.Full == Char
                && live.AlchemyBehavior.ReagentItemIds.Contains(row.m_globalID.Full)).Select(row => row with { })];
            return live;
        }
        internal IDisposable Scope() {
            var previousStore = WizardCollection.TestStoreScope.Value;
            var previousRows = WizardReagentCollection.TestRowsScope.Value;
            WizardCollection.TestStoreScope.Value = new WizardCollection.TestStore(Open, Load);
            WizardReagentCollection.TestRowsScope.Value = session => { Reads++; return Session(session).Rows; };
            return new Restore(() => {
                WizardCollection.TestStoreScope.Value = previousStore;
                WizardReagentCollection.TestRowsScope.Value = previousRows;
            });
        }
        internal static ReagentSession Session(IDocumentSession session) => (ReagentSession) (object) session;
        internal IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, ReagentSession>();
            var proxy = Session(session);
            LastSession = proxy;
            proxy.Rows = [.. SavedRows.Select(row => row with { })];
            proxy.Store = row => { Stored++; proxy.Rows.Add(row); };
            proxy.Delete = row => { Deleted++; Assert.True(proxy.Rows.Remove(row)); };
            proxy.Save = () => {
                Assert.True(WizardCollection.HoldsWriteLane);
                SaveAttempts++;
                BeforeSave?.Invoke();
                if (FailSave && !CommitBeforeFailure) throw new InvalidOperationException("injected refused reagent save");
                Saved = Clone(proxy.Wizard!);
                SavedRows = [.. proxy.Rows.Select(row => row with { })];
                if (FailSave) throw new InvalidOperationException("injected uncertain committed reagent save");
            };
            return session;
        }
        internal Wizard ForStage(IDocumentSession session) => Session(session).Wizard = Clone(Saved);
        private Wizard Load(IDocumentSession session, ulong id) {
            Assert.True(WizardCollection.HoldsWriteLane);
            Assert.Equal(Char, id);
            return RefuseLoad ? null! : ForStage(session);
        }
        private static Wizard Clone(Wizard wizard) => new() { CharId = wizard.CharId,
            PlayerNameBehavior = new() { NameOverride = "Reagent fixture" },
            AlchemyBehavior = new() { ReagentItemIds = [.. wizard.AlchemyBehavior.ReagentItemIds], Reagents = [] },
            GameStats = wizard.GameStats.CloneSnapshotWithGold(wizard.GameStats.m_currentGold),
        };
        private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
    }

    public class ReagentSession : DispatchProxy {
        internal Wizard? Wizard;
        internal List<ClientReagentItem> Rows = [];
        internal Action<ClientReagentItem> Store = null!, Delete = null!;
        internal System.Action Save = null!;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, ReagentAdvanced>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            switch (method!.Name) {
                case "get_Advanced": return _advanced;
                case "Store": Store(Assert.IsType<ClientReagentItem>(args![0])); return null;
                case "Delete": Delete(Assert.IsType<ClientReagentItem>(args![0])); return null;
                case "SaveChanges": Save(); return null;
                case "Dispose": return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
    public class ReagentAdvanced : DispatchProxy {
        private readonly IMetadataDictionary _metadata = DispatchProxy.Create<IMetadataDictionary, ReagentMetadata>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "set_OptimisticConcurrencyMode" => null,
            "GetMetadataFor" => _metadata,
            _ => throw new NotSupportedException(method.Name),
        };
    }
    public class ReagentMetadata : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            if (method!.Name != "set_Item") throw new NotSupportedException(method.Name);
            Assert.Equal(Raven.Client.Constants.Documents.Metadata.Collection, args![0]);
            Assert.Equal(WizardReagentCollection.CollectionName, args[1]);
            return null;
        }
    }
}
