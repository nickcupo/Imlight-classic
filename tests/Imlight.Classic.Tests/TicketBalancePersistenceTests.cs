// CLASSIC: saved ticket wallets must survive stale health/transfer snapshots and uncertain persistence.
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class TicketBalancePersistenceTests {
    public TicketBalancePersistenceTests() => EquipmentAttachConcurrencyTests.Configure();

    [Fact]
    public void StaleHealthSavePreservesIndependentSavedCurrencyFieldsAndPublishesTheirBalances() {
        var store = new TicketWalletFixture(100, 71);
        var stale = store.Live();
        stale.GameStats.m_currentArenaPoints = 1;
        stale.GameStats.m_currentPvPCurrency = 2;
        stale.GameStats.m_currentGold = 3;
        stale.GameStats.m_currentHitpoints = 80;
        stale.GameStats.m_currentMana = 12;
        Assert.True(WizardCollection.UpdateCharacterGameStats(stale, store.Open, store.Load));
        var saved = store.Live();
        Assert.Equal(100, saved.GameStats.m_currentArenaPoints);
        Assert.Equal(71, saved.GameStats.m_currentPvPCurrency);
        Assert.Equal(900, saved.GameStats.m_currentGold);
        Assert.Equal(80, saved.GameStats.m_currentHitpoints);
        Assert.Equal(12, saved.GameStats.m_currentMana);
        Assert.Equal(100, stale.GameStats.m_currentArenaPoints);
        Assert.Equal(71, stale.GameStats.m_currentPvPCurrency);
        Assert.Equal(900, stale.GameStats.m_currentGold);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AwardAndStaleHealthSaveSerializeWithoutOverwritingTheAward(bool awardFirst) {
        var store = new TicketWalletFixture();
        using var scope = store.Scope();
        var live = store.Live();
        var stale = store.Live();
        stale.GameStats.m_currentHitpoints = 80;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var commits = 0;
        store.BeforeSave = () => {
            if (Interlocked.Increment(ref commits) != 1) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test did not release wallet save");
        };
        bool Award() => ArenaService.SaveOutcomeTickets(live, 4);
        bool Health() => WizardCollection.UpdateCharacterGameStats(stale, store.Open, store.Load);
        var first = Task.Factory.StartNew(() => awardFirst ? Award() : Health(), TaskCreationOptions.LongRunning);
        Task<bool>? second = null;
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            second = Task.Factory.StartNew(() => {
                secondStarted.TrySetResult();
                return awardFirst ? Health() : Award();
            }, TaskCreationOptions.LongRunning);
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            release.Set();
            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
            var saved = store.Live();
            Assert.Equal(104, saved.GameStats.m_currentArenaPoints);
            Assert.Equal(104, saved.GameStats.m_currentPvPCurrency);
            Assert.Equal(80, saved.GameStats.m_currentHitpoints);
            Assert.Equal(104, live.GameStats.m_currentArenaPoints);
        }
        finally {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AwardFailureNeverPublishesAnUnsavedOrUncertainLiveBalance(bool uncertainCommitted) {
        var store = new TicketWalletFixture { FailSave = true, CommitBeforeFailure = uncertainCommitted };
        using var scope = store.Scope();
        var live = store.Live();
        Assert.Throws<InvalidOperationException>(() => ArenaService.SaveOutcomeTickets(live, 4));
        Assert.Equal(100, live.GameStats.m_currentArenaPoints);
        Assert.Equal(100, live.GameStats.m_currentPvPCurrency);
        Assert.Equal(uncertainCommitted ? 104 : 100, store.Live().GameStats.m_currentArenaPoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedHealthSaveCannotPublishSavedBalancesBeforePersistenceIsAcknowledged(bool uncertainCommitted) {
        var store = new TicketWalletFixture(100, 71) { FailSave = true, CommitBeforeFailure = uncertainCommitted };
        var stale = store.Live();
        stale.GameStats.m_currentArenaPoints = 1;
        stale.GameStats.m_currentPvPCurrency = 2;
        stale.GameStats.m_currentGold = 3;
        stale.GameStats.m_currentHitpoints = 80;
        Assert.Throws<InvalidOperationException>(() => WizardCollection.UpdateCharacterGameStats(stale, store.Open, store.Load));
        Assert.Equal(1, stale.GameStats.m_currentArenaPoints);
        Assert.Equal(2, stale.GameStats.m_currentPvPCurrency);
        Assert.Equal(3, stale.GameStats.m_currentGold);
        var saved = store.Live();
        Assert.Equal(100, saved.GameStats.m_currentArenaPoints);
        Assert.Equal(71, saved.GameStats.m_currentPvPCurrency);
        Assert.Equal(uncertainCommitted ? 80 : 30, saved.GameStats.m_currentHitpoints);
    }

    [Fact]
    public void MissingWizardRefusesAwardAndGenericSaveWithoutPublishingLiveBalances() {
        var store = new TicketWalletFixture { RefuseLoad = true };
        using var scope = store.Scope();
        var live = store.Live();
        Assert.False(ArenaService.SaveOutcomeTickets(live, 4));
        live.GameStats.m_currentArenaPoints = 1;
        live.GameStats.m_currentPvPCurrency = 2;
        Assert.False(WizardCollection.UpdateCharacterGameStats(live, store.Open, store.Load));
        Assert.Equal(1, live.GameStats.m_currentArenaPoints);
        Assert.Equal(2, live.GameStats.m_currentPvPCurrency);
        Assert.Equal(100, store.Live().GameStats.m_currentArenaPoints);
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(2147483647L)]
    [InlineData(-101L)]
    public void InvalidTicketArithmeticChangesNeitherSavedNorLiveWallet(long delta) {
        var store = new TicketWalletFixture();
        var live = store.Live();
        Assert.False(WizardCollection.ChangeArenaTickets(live, delta, false, store.Open, store.Load));
        Assert.Equal(100, live.GameStats.m_currentArenaPoints);
        Assert.Equal(100, store.Live().GameStats.m_currentArenaPoints);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void WrongLiveCharacterCannotPublishAnotherCharactersTicketWallet() {
        var store = new TicketWalletFixture();
        var other = store.Live();
        other.CharId++;
        Assert.False(WizardCollection.ChangeArenaTickets(TicketWalletFixture.Char, 4, other, false, store.Open, store.Load));
        Assert.Equal(100, other.GameStats.m_currentArenaPoints);
        Assert.Equal(0, store.Saves);
    }
}

// CLASSIC: copies a document on load/save as Raven does, while using the real shared character write lane.
internal sealed class TicketWalletFixture {
    internal const ulong Char = 771901;
    private readonly object _gate = new();
    private Wizard _saved;
    internal bool FailSave;
    internal bool CommitBeforeFailure;
    internal bool RefuseLoad;
    internal Action? BeforeSave;
    internal int Saves;

    internal TicketWalletFixture(int points = 100, int pvpCurrency = 100) {
        _saved = new Wizard { CharId = Char, GameStats = new ServerWizGameStats(default, 1) {
            m_currentArenaPoints = points, m_currentPvPCurrency = pvpCurrency, m_currentGold = 900,
            m_currentHitpoints = 30, m_currentMana = 10,
        } };
    }

    internal Wizard Live() { lock (_gate) return Clone(_saved); }
    internal IDisposable Scope() {
        var previous = WizardCollection.TestStoreScope.Value;
        WizardCollection.TestStoreScope.Value = new WizardCollection.TestStore(Open, Load);
        return new Restore(() => WizardCollection.TestStoreScope.Value = previous);
    }
    internal IDocumentSession Open() {
        var session = DispatchProxy.Create<IDocumentSession, TreasureTradeTests.MultiSessionProxy>();
        var proxy = (TreasureTradeTests.MultiSessionProxy) (object) session;
        proxy.Commit = working => {
            Assert.True(WizardCollection.HoldsWriteLane);
            BeforeSave?.Invoke();
            if (FailSave && !CommitBeforeFailure) throw new InvalidOperationException("injected refused wallet save");
            lock (_gate) _saved = Clone(working.Values.Single());
            Interlocked.Increment(ref Saves);
            if (FailSave) throw new InvalidOperationException("injected uncertain committed wallet save");
        };
        return session;
    }
    internal Wizard Load(IDocumentSession session, ulong id) {
        Assert.Equal(Char, id);
        Assert.True(WizardCollection.HoldsWriteLane);
        if (RefuseLoad) return null!;
        var working = Live();
        ((TreasureTradeTests.MultiSessionProxy) (object) session).Working[id] = working;
        return working;
    }
    private static Wizard Clone(Wizard wizard) => new() {
        CharId = wizard.CharId, GameStats = wizard.GameStats.CloneSnapshotWithGold(wizard.GameStats.m_currentGold),
    };
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
}
