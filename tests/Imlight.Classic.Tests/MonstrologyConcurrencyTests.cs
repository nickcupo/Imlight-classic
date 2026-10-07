using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents.Session;
using Imlight.Common;
using Imlight.CoreLib.Game.Monstrology;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class MonstrologyConcurrencyTests {
    public MonstrologyConcurrencyTests() {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}monstrology-concurrency-tests.log\n");
            ConfigurationManager.Initialize(path);
        } finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("reward", 910)]
    [InlineData("spend", 890)]
    [InlineData("health", 900)]
    [InlineData("book", 900)]
    [InlineData("add-card", 900)]
    [InlineData("remove-card", 900)]
    public async Task CreationPublishesBeforeCompetingWalletHealthOrBookSave(string competingWrite, int expectedGold) {
        var f = new Fixture();
        if (competingWrite == "remove-card") { f.Persisted.SpellbookBehavior.AddTreasureCard(777); f.Live.SpellbookBehavior.AddTreasureCard(777); }
        var stale = Fixture.Clone(f.Live);
        stale.GameStats.m_currentHitpoints = 500;
        stale.SpellbookBehavior.LearnedSpellTemplateIds = [77];
        var token = TestContext.Current.CancellationToken;
        using var release = new ManualResetEventSlim();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var competingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforePublish = () => {
            Assert.True(WizardCollection.HoldsWriteLane);
            committed.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10), token)) throw new TimeoutException("Test did not release cache publication");
        };
        var creation = Task.Factory.StartNew(f.Create, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal(900, f.Persisted.GameStats.m_currentGold);
        Assert.Equal(1000, f.Live.GameStats.m_currentGold);
        Assert.Contains(321u, f.Persisted.SpellbookBehavior.TreasureCardTemplateIds);
        var competing = Task.Factory.StartNew(() => {
            competingStarted.SetResult();
            return competingWrite switch {
                "reward" => WizardCollection.ChangeGold(f.Live, 10, true, f.Open, f.Load),
                "spend" => WizardCollection.ChangeGold(f.Live, -10, false, f.Open, f.Load),
                "health" => WizardCollection.UpdateCharacterGameStats(stale, f.Open, f.Load),
                "book" => WizardCollection.UpdateCharacterSpellbookBehavior(stale, f.Open, f.Load),
                "add-card" => WizardCollection.ChangeTreasureCard(f.Live, 777, true, f.Open, f.Load),
                "remove-card" => WizardCollection.ChangeTreasureCard(f.Live, 777, false, f.Open, f.Load),
                _ => throw new InvalidOperationException(),
            };
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try {
            await competingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            var first = await Task.WhenAny(competing, Task.Delay(100, token));
            Assert.NotSame(competing, first);
        } finally { release.Set(); }
        Assert.Equal(MonstrologyResult.Applied, await creation.WaitAsync(TimeSpan.FromSeconds(10), token));
        Assert.True(await competing.WaitAsync(TimeSpan.FromSeconds(10), token));
        Assert.Equal(expectedGold, f.Persisted.GameStats.m_currentGold);
        Assert.Equal(expectedGold, f.Live.GameStats.m_currentGold);
        Assert.Equal(1, f.Persisted.SpellbookBehavior.TreasureCardTemplateIds.Count(id => id == 321));
        Assert.Equal(f.Persisted.SpellbookBehavior.TreasureCardTemplateIds, f.Live.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(2, f.Ledger.Animus[123]); Assert.Single(f.Ledger.Creations);
        if (competingWrite == "health") { Assert.Equal(500, f.Persisted.GameStats.m_currentHitpoints); Assert.Equal(900, stale.GameStats.m_currentGold); }
        if (competingWrite == "book") { Assert.Contains(77u, f.Persisted.SpellbookBehavior.LearnedSpellTemplateIds); Assert.Contains(321u, stale.SpellbookBehavior.TreasureCardTemplateIds); }
        if (competingWrite == "add-card") Assert.Equal(1, f.Live.SpellbookBehavior.TreasureCardTemplateIds.Count(id => id == 777));
        if (competingWrite == "remove-card") Assert.DoesNotContain(777u, f.Live.SpellbookBehavior.TreasureCardTemplateIds);
    }

    [Fact]
    public void FailedCreationNeverPublishesLiveWalletOrDeliveredCard() {
        var f = new Fixture { FailSave = true };
        Assert.Throws<InvalidOperationException>(() => f.Create());
        Assert.Equal(1000, f.Persisted.GameStats.m_currentGold); Assert.Equal(1000, f.Live.GameStats.m_currentGold);
        Assert.Empty(f.Persisted.SpellbookBehavior.TreasureCardTemplateIds); Assert.Empty(f.Live.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(5, f.Ledger.Animus[123]); Assert.Empty(f.Ledger.Creations); Assert.Equal(0, f.Publishes);
        Assert.False(WizardCollection.HoldsWriteLane);
        f.FailSave = false; Assert.Equal(MonstrologyResult.Applied, f.Create()); Assert.Equal(900, f.Live.GameStats.m_currentGold);
    }

    [Fact]
    public void RejectedCreationDoesNotSaveOrInvokeHook() {
        var f = new Fixture(); f.Persisted.GameStats.m_currentGold = f.Live.GameStats.m_currentGold = 50;
        Assert.Equal(MonstrologyResult.InsufficientGold, f.Create());
        Assert.Equal(0, f.Saves); Assert.Equal(0, f.Publishes); Assert.Equal(50, f.Live.GameStats.m_currentGold);
        Assert.Empty(f.Live.SpellbookBehavior.TreasureCardTemplateIds); Assert.Equal(5, f.Ledger.Animus[123]);
    }

    [Theory]
    [InlineData("reward")] [InlineData("spend")] [InlineData("book")] [InlineData("add-card")]
    public void FailedWalletOrBookSaveDoesNotPublishStagedState(string write) {
        var f = new Fixture { FailSave = true };
        Assert.Throws<InvalidOperationException>(() => {
            if (write == "reward") WizardCollection.ChangeGold(f.Live, 10, true, f.Open, f.Load);
            else if (write == "spend") WizardCollection.ChangeGold(f.Live, -10, false, f.Open, f.Load);
            else if (write == "book") WizardCollection.UpdateCharacterSpellbookBehavior(f.Live, f.Open, f.Load);
            else WizardCollection.ChangeTreasureCard(f.Live, 321, true, f.Open, f.Load);
        });
        Assert.Equal(1000, f.Live.GameStats.m_currentGold); Assert.Equal(1000, f.Persisted.GameStats.m_currentGold);
        Assert.Empty(f.Live.SpellbookBehavior.TreasureCardTemplateIds); Assert.Empty(f.Persisted.SpellbookBehavior.TreasureCardTemplateIds);
    }

    [Fact]
    public void WalletUsesPersistedValueAndPouchNotStaleCacheAndSnapshotDoesNotAliasGold() {
        var f = new Fixture(); f.Live.GameStats.m_currentGold = 9999; f.Live.GameStats.m_baseGoldPouch = 1;
        Assert.True(WizardCollection.ChangeGold(f.Live, 1500, true, f.Open, f.Load));
        Assert.Equal(2000, f.Persisted.GameStats.m_currentGold); Assert.Equal(2000, f.Live.GameStats.m_currentGold);
        Assert.True(WizardCollection.ChangeGold(f.Live, -100, false, f.Open, f.Load)); Assert.Equal(1900, f.Live.GameStats.m_currentGold);
        var snapshot = f.Live.GameStats.CloneSnapshotWithGold(900);
        Assert.NotSame(f.Live.GameStats, snapshot); f.Live.GameStats.m_currentGold = 111;
        Assert.Equal(900, snapshot.m_currentGold);
        Assert.Equal(f.Live.GameStats.m_currentHitpoints, snapshot.m_currentHitpoints);
    }

    [Fact]
    public async Task ConcurrentTreasurePurchasesCannotSpendTheSameBalanceTwice() {
        var f = new Fixture();
        var stale = Fixture.Clone(f.Live);
        var token = TestContext.Current.CancellationToken;
        using var start = new ManualResetEventSlim();
        Task<bool> Buy(Wizard wizard) => Task.Factory.StartNew(() => {
            start.Wait(token);
            return WizardCollection.TryPurchaseTreasureCards(wizard, 777, 2, 300, f.Open, f.Load);
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var first = Buy(f.Live); var second = Buy(stale); start.Set();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Single(results.Where(x => x));
        Assert.Equal(400, f.Persisted.GameStats.m_currentGold);
        Assert.Equal(2, f.Persisted.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(1, f.Saves);
    }

    [Fact]
    public async Task TreasurePurchaseWaitsForMonstrologyCommitAndRejectsStaleAffordability() {
        var f = new Fixture(); var stale = Fixture.Clone(f.Live);
        var token = TestContext.Current.CancellationToken;
        using var release = new ManualResetEventSlim();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforePublish = () => {
            committed.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10), token)) throw new TimeoutException();
        };
        var creation = Task.Factory.StartNew(f.Create, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var purchase = Task.Factory.StartNew(() => {
            started.SetResult();
            return WizardCollection.TryPurchaseTreasureCards(stale, 777, 1, 950, f.Open, f.Load);
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.NotSame(purchase, await Task.WhenAny(purchase, Task.Delay(100, token)));
        } finally { release.Set(); }
        Assert.Equal(MonstrologyResult.Applied, await creation.WaitAsync(TimeSpan.FromSeconds(10), token));
        Assert.False(await purchase.WaitAsync(TimeSpan.FromSeconds(10), token));
        Assert.Equal(900, f.Persisted.GameStats.m_currentGold);
        Assert.Equal(new uint[] { 321 }, f.Persisted.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(1, f.Saves);
    }

    [Fact]
    public void FailedTreasurePurchasePublishesNeitherDebitNorCopies() {
        var f = new Fixture { FailSave = true };
        Assert.Throws<InvalidOperationException>(() => WizardCollection.TryPurchaseTreasureCards(f.Live, 777, 3, 100, f.Open, f.Load));
        Assert.Equal(1000, f.Live.GameStats.m_currentGold);
        Assert.Equal(1000, f.Persisted.GameStats.m_currentGold);
        Assert.Empty(f.Live.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Empty(f.Persisted.SpellbookBehavior.TreasureCardTemplateIds);
        f.FailSave = false;
        // CLASSIC: a failed acknowledgement refuses the old instance; a fresh authoritative login may buy.
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        Assert.False(WizardCollection.TryPurchaseTreasureCards(f.Live, 777, 3, 100, f.Open, f.Load));
        var reloaded = Fixture.Clone(f.Persisted);
        Assert.True(WizardCollection.TryPurchaseTreasureCards(reloaded, 777, 3, 100, f.Open, f.Load));
        Assert.Equal(700, reloaded.GameStats.m_currentGold);
        Assert.Equal(3, reloaded.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(f.Persisted.SpellbookBehavior.TreasureCardTemplateIds, reloaded.SpellbookBehavior.TreasureCardTemplateIds);
    }

    [Theory]
    [InlineData(int.MaxValue, 2)] [InlineData(2, int.MaxValue)]
    [InlineData(1, -1)] [InlineData(0, 100)]
    public void InvalidTreasurePurchaseCannotCreditGoldOrDeliverCards(int quantity, int price) {
        var f = new Fixture();
        Assert.False(WizardCollection.TryPurchaseTreasureCards(f.Live, 777, quantity, price, f.Open, f.Load));
        Assert.Equal(0, f.Saves); Assert.Equal(1000, f.Persisted.GameStats.m_currentGold);
        Assert.Empty(f.Persisted.SpellbookBehavior.TreasureCardTemplateIds);
    }

    private sealed class Fixture {
        internal Wizard Persisted = Make();
        internal Wizard Live = Make();
        internal MonstrologyLedger Ledger = new() { OwnerId = 42, Animus = new() { [123] = 5 } };
        internal Action? BeforePublish;
        internal bool FailSave;
        internal int Saves, Publishes;
        internal IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, SessionProxy>();
            var proxy = (SessionProxy) (object) session;
            proxy.Working = Clone(Persisted);
            proxy.Ledger = new MonstrologyLedger { OwnerId = Ledger.OwnerId, Animus = new(Ledger.Animus), Creations = new(Ledger.Creations) };
            proxy.Commit = () => {
                if (FailSave) throw new InvalidOperationException("Injected SaveChanges failure");
                Persisted = Clone(proxy.Working); Ledger = proxy.Ledger; Saves++;
            };
            return session;
        }
        internal Wizard Load(IDocumentSession session, ulong id) {
            Assert.Equal(42UL, id); Assert.True(WizardCollection.HoldsWriteLane);
            return ((SessionProxy) (object) session).Working;
        }
        internal MonstrologyResult Create() => MonstrologyRepository.CreateCard(42,
            new AnimusCreation("barrier-create", 42, 123, 321, 3, 100, true), out _, transact: Transact, afterCommit: Publish);
        private bool Transact(ulong owner, Func<IDocumentSession, Wizard, bool> operation, Action<Wizard> afterCommit)
            => WizardCollection.CommitCharacterMutation(owner, operation, afterCommit, Open, Load);
        private void Publish(Wizard committed) {
            Assert.True(WizardCollection.HoldsWriteLane); BeforePublish?.Invoke();
            Live.GameStats.m_currentGold = committed.GameStats.m_currentGold;
            Live.SpellbookBehavior.TreasureCardTemplateIds = committed.SpellbookBehavior.TreasureCardTemplateIds.ToList();
            Publishes++;
        }
        private static Wizard Make() => new() {
            CharId = 42, GameStats = new ServerWizGameStats(default, 1) { m_currentGold = 1000, m_baseGoldPouch = 2000, m_currentHitpoints = 1000 },
            SpellbookBehavior = new ServerWizSpellbookBehavior(),
        };
        internal static Wizard Clone(Wizard wizard) => new() {
            CharId = wizard.CharId, GameStats = wizard.GameStats.CloneSnapshotWithGold(wizard.GameStats.m_currentGold),
            SpellbookBehavior = new ServerWizSpellbookBehavior {
                LearnedSpellTemplateIds = wizard.SpellbookBehavior.LearnedSpellTemplateIds.ToList(),
                TreasureCardTemplateIds = wizard.SpellbookBehavior.TreasureCardTemplateIds.ToList(),
                ExcludedItemSpellIds = wizard.SpellbookBehavior.ExcludedItemSpellIds.ToDictionary(pair => pair.Key, pair => new HashSet<uint>(pair.Value)),
            },
        };
    }

    public class SessionProxy : DispatchProxy {
        internal Wizard Working = null!;
        internal MonstrologyLedger Ledger = null!;
        internal Action Commit = null!;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, AdvancedProxy>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            return method!.Name switch {
                "get_Advanced" => _advanced,
                "Load" => Ledger,
                "Store" => Store(args!),
                "SaveChanges" => Save(),
                "Dispose" => null,
                _ => throw new NotSupportedException(method.Name),
            };
        }
        private object? Store(object?[] args) { Ledger = Assert.IsType<MonstrologyLedger>(args[0]); return null; }
        private object? Save() { Commit(); return null; }
    }
    public class AdvancedProxy : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            if (method!.Name is "set_OptimisticConcurrencyMode" or "IgnoreChangesFor") return null;
            throw new NotSupportedException(method.Name);
        }
    }
}
