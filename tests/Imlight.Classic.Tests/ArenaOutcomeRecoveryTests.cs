// CLASSIC: real Akka termination + production wallet writes; no retry of a save that already started.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic.Pvp;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Game;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaOutcomeRecoveryTests {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    public ArenaOutcomeRecoveryTests() => EquipmentAttachConcurrencyTests.Configure();
    private static ArenaOutcome Outcome() => new(1, ArenaKind.Ranked, true, false, 500, 520,
        "Private", 4, new GAME_5_PROTOCOL.MSG_MATCHRESULT(), "WizardCity/WC_Arena", "ArenaStart", 0);

    [Fact]
    public async Task DroppedAwardWaitsForEveryChildThenRecoversOnceAndSurvivesAPrefetchedHealthSnapshot() {
        using var system = ActorSystem.Create("ticket-dropped-award", "akka.actor.provider=local");
        var store = new TicketWalletFixture();
        using var scope = store.Scope();
        var prefetched = store.Live();
        prefetched.GameStats.m_currentHitpoints = 80;
        using var releaseChild = new ManualResetEventSlim();
        var childStopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = system.ActorOf(Props.Create(() => new DroppingParent(childStopping, parentStopped, releaseChild)));
        var receipts = new ArenaOutcomeReceipts();
        var receipt = receipts.Begin(TicketWalletFixture.Char);
        var recovered = 0;
        bool Recover() {
            Assert.True(parentStopped.Task.IsCompletedSuccessfully);
            Interlocked.Increment(ref recovered);
            return ServerArenaWorld.SaveOfflineOutcome(TicketWalletFixture.Char, 4);
        }
        var delivery = ArenaOutcomeDelivery.DeliverAsync(target, receipt, Outcome(), Recover);
        Task? duplicate = null;
        try {
            await childStopping.Task.WaitAsync(Timeout);
            Assert.False(parentStopped.Task.IsCompleted);
            Assert.False(delivery.IsCompleted);
            Assert.False(receipt.Completion.IsCompleted);
            Assert.Equal(0, store.Saves);
            duplicate = ArenaOutcomeDelivery.DeliverAsync(target, receipt, Outcome(), Recover);
            releaseChild.Set();
            await Task.WhenAll(delivery, duplicate).WaitAsync(Timeout);
            Assert.Equal(1, recovered);
            Assert.Equal(1, store.Saves);
            Assert.Equal(104, store.Live().GameStats.m_currentArenaPoints);
            Assert.False(receipt.ApplyAward(() => throw new InvalidOperationException("late handler must not save")));
            Assert.True(WizardCollection.UpdateCharacterGameStats(prefetched, store.Open, store.Load));
            var saved = store.Live();
            Assert.Equal(104, saved.GameStats.m_currentArenaPoints);
            Assert.Equal(104, saved.GameStats.m_currentPvPCurrency);
            Assert.Equal(80, saved.GameStats.m_currentHitpoints);
            Assert.Equal(104, prefetched.GameStats.m_currentArenaPoints);
            await receipts.DrainAsync().WaitAsync(Timeout);
        }
        finally {
            releaseChild.Set();
            await delivery.WaitAsync(Timeout);
            if (duplicate is not null) await duplicate.WaitAsync(Timeout);
            await system.Terminate();
        }
    }

    [Fact]
    public async Task StopDuringStartedSaveWaitsForThatSaveWithoutRecoveryOrDoubleAward() {
        using var system = ActorSystem.Create("ticket-started-award", "akka.actor.provider=local");
        var store = new TicketWalletFixture();
        using var scope = store.Scope();
        var live = store.Live();
        using var releaseSave = new ManualResetEventSlim();
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeSave = () => {
            saveEntered.TrySetResult();
            if (!releaseSave.Wait(Timeout)) throw new TimeoutException("test did not release original save");
        };
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<bool> saveOriginal = () => { using var actorScope = store.Scope(); return ArenaService.SaveOutcomeTickets(live, 4); };
        var target = system.ActorOf(Props.Create(() => new AwardingTarget(saveOriginal, handled)));
        var receipt = new ArenaOutcomeReceipts().Begin(TicketWalletFixture.Char);
        var recovered = 0;
        var delivery = ArenaOutcomeDelivery.DeliverAsync(target, receipt, Outcome(), () => {
            Interlocked.Increment(ref recovered);
            return ServerArenaWorld.SaveOfflineOutcome(TicketWalletFixture.Char, 4);
        });
        try {
            await saveEntered.Task.WaitAsync(Timeout);
            system.Stop(target);
            Assert.False(receipt.Completion.IsCompleted);
            Assert.False(delivery.IsCompleted);
            Assert.Equal(0, recovered);
            releaseSave.Set();
            await delivery.WaitAsync(Timeout);
            await handled.Task.WaitAsync(Timeout);
            Assert.True(await target.WatchAsync().WaitAsync(Timeout));
            Assert.Equal(0, recovered);
            Assert.Equal(1, store.Saves);
            Assert.Equal(104, store.Live().GameStats.m_currentArenaPoints);
            Assert.Equal(104, live.GameStats.m_currentArenaPoints);
        }
        finally { releaseSave.Set(); await system.Terminate(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartedFailedSaveIsNeverRecoveredEvenAfterActorTerminates(bool uncertainCommitted) {
        using var system = ActorSystem.Create("ticket-failed-original", "akka.actor.provider=local");
        var store = new TicketWalletFixture { FailSave = true, CommitBeforeFailure = uncertainCommitted };
        using var scope = store.Scope();
        var live = store.Live();
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<bool> saveOriginal = () => { using var actorScope = store.Scope(); return ArenaService.SaveOutcomeTickets(live, 4); };
        var target = system.ActorOf(Props.Create(() => new AwardingTarget(saveOriginal, handled)));
        var receipt = new ArenaOutcomeReceipts().Begin(TicketWalletFixture.Char);
        var recovered = 0;
        bool Recover() { Interlocked.Increment(ref recovered); return ServerArenaWorld.SaveOfflineOutcome(TicketWalletFixture.Char, 4); }
        try {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ArenaOutcomeDelivery.DeliverAsync(target, receipt, Outcome(), Recover));
            await handled.Task.WaitAsync(Timeout);
            Assert.True(await target.WatchAsync().WaitAsync(Timeout));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ArenaOutcomeDelivery.DeliverAsync(target, receipt, Outcome(), Recover));
            Assert.Equal(0, recovered);
            Assert.Equal(uncertainCommitted ? 1 : 0, store.Saves);
            Assert.Equal(uncertainCommitted ? 104 : 100, store.Live().GameStats.m_currentArenaPoints);
            Assert.Equal(100, live.GameStats.m_currentArenaPoints);
        }
        finally { await system.Terminate(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedOrUncertainRecoveryFaultsReceiptAndNeverAttemptsAgain(bool uncertainCommitted) {
        using var system = ActorSystem.Create("ticket-failed-recovery", "akka.actor.provider=local");
        var store = new TicketWalletFixture { RefuseLoad = !uncertainCommitted, FailSave = uncertainCommitted,
            CommitBeforeFailure = uncertainCommitted };
        using var scope = store.Scope();
        var target = system.ActorOf(Props.Create(() => new DroppingTarget()));
        var receipts = new ArenaOutcomeReceipts();
        var receipt = receipts.Begin(TicketWalletFixture.Char);
        var recovered = 0;
        bool Recover() { Interlocked.Increment(ref recovered); return ServerArenaWorld.SaveOfflineOutcome(TicketWalletFixture.Char, 4); }
        try {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ArenaOutcomeDelivery.DeliverAsync(target, receipt, Outcome(), Recover));
            Assert.True(await target.WatchAsync().WaitAsync(Timeout));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ArenaOutcomeDelivery.DeliverAsync(target, receipt, Outcome(), Recover));
            await Assert.ThrowsAsync<InvalidOperationException>(() => receipts.DrainAsync());
            Assert.Equal(1, recovered);
            Assert.Equal(uncertainCommitted ? 104 : 100, store.Live().GameStats.m_currentArenaPoints);
            Assert.Equal(uncertainCommitted ? 1 : 0, store.Saves);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task OutcomeTargetRequiresMatchingHolderPathCharacterAndAccountAndRejectsADeadIncarnation() {
        using var system = ActorSystem.Create("ticket-target-identity", "akka.actor.provider=local");
        var target = system.ActorOf(Props.Empty);
        var session = FakeSession(target);
        var account = new Account();
        const ulong accountId = 771900;
        typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(account, accountId);
        var wizard = new TicketWalletFixture().Live();
        wizard.Account = account;
        var online = new OnlinePlayer { AccountId = accountId, CharacterId = wizard.CharId, ActorPath = target.Path.ToString() };
        try {
            Assert.NotEqual(AccountClaim.Refused, AccountSessions.Claim(accountId, session, DateTime.UtcNow));
            ActiveWizardDirectory.SetWizard(target, wizard);
            Assert.Equal(target, ServerArenaWorld.OutcomeTarget(online, wizard.CharId));
            Assert.Null(ServerArenaWorld.OutcomeTarget(online, wizard.CharId + 1));
            online.ActorPath += "-stale";
            Assert.Null(ServerArenaWorld.OutcomeTarget(online, wizard.CharId));
            online.ActorPath = target.Path.ToString();
            online.AccountId++;
            Assert.Null(ServerArenaWorld.OutcomeTarget(online, wizard.CharId));
            online.AccountId = accountId;
            wizard.Account = new Account();
            Assert.Null(ServerArenaWorld.OutcomeTarget(online, wizard.CharId));
            wizard.Account = account;
            Assert.True(await target.GracefulStop(Timeout));
            Assert.Null(ServerArenaWorld.OutcomeTarget(online, wizard.CharId));
        }
        finally { ActiveWizardDirectory.Remove(target); AccountSessions.Release(session); await system.Terminate(); }
    }

    [Fact]
    public async Task StaleOnlinePathUsesSavedOfflineAwardWithoutSendingToTheReplacementHolder() {
        using var system = ActorSystem.Create("ticket-stale-delivery", "akka.actor.provider=local");
        var store = new TicketWalletFixture();
        using var scope = store.Scope();
        var prefetched = store.Live();
        var target = system.ActorOf(Props.Create(() => new OutcomeCounter()));
        var session = FakeSession(target);
        const ulong accountId = 771902;
        var account = new Account();
        typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(account, accountId);
        prefetched.Account = account;
        try {
            Assert.NotEqual(AccountClaim.Refused, AccountSessions.Claim(accountId, session, DateTime.UtcNow));
            ActiveWizardDirectory.SetWizard(target, prefetched);
            OnlinePlayerCollection.SetVirtualOnlinePlayer(new OnlinePlayer { AccountId = accountId,
                CharacterId = TicketWalletFixture.Char, ActorPath = target.Path + "-old" });
            new ServerArenaWorld(system).Deliver(TicketWalletFixture.Char, Outcome());
            Assert.Equal(0, await target.Ask<int>("Count", Timeout));
            Assert.Equal(104, store.Live().GameStats.m_currentArenaPoints);
            prefetched.GameStats.m_currentHitpoints = 80;
            Assert.True(WizardCollection.UpdateCharacterGameStats(prefetched, store.Open, store.Load));
            Assert.Equal(104, prefetched.GameStats.m_currentArenaPoints);
            Assert.Equal(104, store.Live().GameStats.m_currentArenaPoints);
        }
        finally {
            OnlinePlayerCollection.RemoveVirtualOnlinePlayer(accountId);
            ActiveWizardDirectory.Remove(target); AccountSessions.Release(session); await system.Terminate();
        }
    }

    private static SessionActor FakeSession(IActorRef target) {
        var session = (SessionActor) RuntimeHelpers.GetUninitializedObject(typeof(SessionActor));
        typeof(SessionActor).GetField("<ActorRef>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, target);
        return session;
    }
    private sealed class DroppingParent : ReceiveActor {
        private readonly TaskCompletionSource _stopped;
        public DroppingParent(TaskCompletionSource childStopping, TaskCompletionSource stopped, ManualResetEventSlim release) {
            _stopped = stopped;
            Context.ActorOf(Props.Create(() => new BlockingStopChild(childStopping, release)));
            Receive<CLASSIC_FEATURES_PROTOCOL.MSG_ARENAOUTCOME>(_ => Context.Stop(Self));
        }
        protected override void PostStop() { _stopped.TrySetResult(); base.PostStop(); }
    }
    private sealed class BlockingStopChild : ReceiveActor {
        private readonly TaskCompletionSource _stopping;
        private readonly ManualResetEventSlim _release;
        public BlockingStopChild(TaskCompletionSource stopping, ManualResetEventSlim release) {
            _stopping = stopping; _release = release;
        }
        protected override void PostStop() {
            _stopping.TrySetResult();
            if (!_release.Wait(Timeout)) throw new TimeoutException("test did not release child stop");
            base.PostStop();
        }
    }
    private sealed class DroppingTarget : ReceiveActor {
        public DroppingTarget() => Receive<CLASSIC_FEATURES_PROTOCOL.MSG_ARENAOUTCOME>(_ => Context.Stop(Self));
    }
    private sealed class AwardingTarget : ReceiveActor {
        public AwardingTarget(Func<bool> saveOriginal, TaskCompletionSource handled) {
            Receive<CLASSIC_FEATURES_PROTOCOL.MSG_ARENAOUTCOME>(message => {
                try { message.Receipt.ApplyAward(saveOriginal); }
                catch (InvalidOperationException) { } // injected refusal/uncertain save is asserted through the real receipt
                finally { handled.TrySetResult(); Context.Stop(Self); }
            });
        }
    }
    private sealed class OutcomeCounter : ReceiveActor {
        public OutcomeCounter() {
            var count = 0;
            Receive<CLASSIC_FEATURES_PROTOCOL.MSG_ARENAOUTCOME>(_ => count++);
            Receive<string>(message => message == "Count", _ => Sender.Tell(count));
        }
    }
}
