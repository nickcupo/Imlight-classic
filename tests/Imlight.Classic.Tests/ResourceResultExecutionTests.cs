// CLASSIC: actual initialized resource result handlers retain authenticated session binding and ACK ordering.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Game.Results.Handlers;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Services;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;
using Type = System.Type;

namespace Imlight.Classic.Tests;

[Collection(nameof(BadgeRulesTests))]
public sealed class ResourceResultExecutionTests {
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const ulong AccountId = (1UL << 44) + 782090, CharacterId = (1UL << 40) + 782000;

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InitializedResultSavesFreshSelectedResourceBeforeItsNativePacket(bool mana) {
        using var scope = new Scope(); var f = scope.F; var old = Current(f.Live, mana);
        using var actor = await Fixture.Create(f, scope.Dependencies, mana);
        var native = f.Live.GameObject; var stats = f.Live.GameStats;
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(old, Current(f.Live, mana));
            Assert.Empty(actor.Packets); Assert.Equal(old, Current(f.Saved, mana));
        };
        scope.Dependencies.BeforePublish = _ => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, f.Saves);
            Assert.Equal(Maximum(f.Live, mana), Current(f.Saved, mana)); Assert.Empty(actor.Packets);
        };
        Assert.True(await actor.Execute(mana));
        AssertPacket(Assert.Single(await actor.Drain()), f.Live, mana, expectedSaves: 1);
        Assert.Equal(1, f.Opened); Assert.Equal(1, f.Saves); Assert.Same(native, f.Live.GameObject); Assert.Same(stats, f.Live.GameStats);
        Assert.Equal(100, f.Saved.GameStats.m_currentGold); Assert.Equal(901, f.Live.GameStats.m_currentGold);
        Assert.Equal(mana ? 23 : 17, Current(f.Saved, !mana)); Assert.False(actor.Session.IsDisposed);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InitializedResultRehydratesHealthyFreshNoOpWithoutSavingOrClosing(bool mana) {
        using var scope = new Scope(); var f = scope.F;
        SetCurrent(f.Saved, mana, Maximum(f.Live, mana));
        using var actor = await Fixture.Create(f, scope.Dependencies, mana);
        f.OnSave = () => Assert.Fail("a fresh resource no-op must not save");
        scope.Dependencies.BeforePublish = _ => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(0, f.Saves); Assert.Empty(actor.Packets);
        };
        Assert.True(await actor.Execute(mana));
        AssertPacket(Assert.Single(await actor.Drain()), f.Live, mana, expectedSaves: 0);
        Assert.Equal(1, f.Opened); Assert.Equal(0, f.Saves); Assert.False(actor.Session.IsDisposed);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task InitializedResultClosesUnknownSaveOutcomeWithoutAResourcePacket(bool mana, bool lostAck) {
        using var scope = new Scope(); var f = scope.F; var old = Current(f.Live, mana);
        f.FailSave = true; f.Durable = lostAck;
        using var actor = await Fixture.Create(f, scope.Dependencies, mana);
        f.OnSave = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(old, Current(f.Live, mana)); Assert.Empty(actor.Packets); };
        f.OnDispose = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        };
        Assert.False(await actor.Execute(mana));
        await actor.Closed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Empty(await actor.Drain()); Assert.True(actor.Session.IsDisposed);
        Assert.Equal(1, f.Opened); Assert.Equal(1, f.Saves); Assert.Equal(old, Current(f.Live, mana));
        Assert.Equal(lostAck ? Maximum(f.Live, mana) : old, Current(f.Saved, mana));
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData(false, "session")] [InlineData(true, "session")]
    [InlineData(false, "membership")] [InlineData(true, "membership")]
    [InlineData(false, "account")] [InlineData(true, "account")]
    [InlineData(false, "zero-account")] [InlineData(true, "zero-account")]
    [InlineData(false, "native-character")] [InlineData(true, "native-character")]
    [InlineData(false, "native-global")] [InlineData(true, "native-global")]
    [InlineData(false, "native-permanent")] [InlineData(true, "native-permanent")]
    public async Task InitializedResultRejectsUnboundSessionMembershipOrFullNativeIdentityBeforeOpeningAStore(bool mana, string defect) {
        using var scope = new Scope(); var f = scope.F;
        using var actor = await Fixture.Create(f, scope.Dependencies, mana);
        var player = actor.Endpoint; CoreObject supplied = f.Live.GameObject;
        switch (defect) {
            case "session": player = actor.OtherEndpoint; break;
            case "membership": f.Live.Account.CharacterIds.Clear(); break;
            case "account": f.Live.AccountId ^= 1UL << 40; break; // Same low bits as the authenticated account.
            case "zero-account":
                typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(f.Live.Account, 0UL);
                f.Live.AccountId = f.Saved.AccountId = 0; break;
            case "native-character": supplied = f.Live.GameObject with { m_characterId = f.Live.CharId ^ (1UL << 44) }; break;
            case "native-global": supplied = f.Live.GameObject with { m_globalID = f.Live.GameObjectID ^ (1UL << 44) }; break;
            case "native-permanent": supplied = f.Live.GameObject with { m_permID = f.Live.GameObjectID ^ (1UL << 44) }; break;
        }
        var old = Current(f.Live, mana);
        Assert.False(await actor.Execute(mana, player, supplied));
        Assert.Empty(await actor.Drain()); Assert.Equal(0, f.Opened); Assert.Equal(0, f.Saves);
        Assert.Equal(old, Current(f.Live, mana)); Assert.False(actor.Session.IsDisposed);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    private static void AssertPacket(PacketObservation observation, Wizard wizard, bool mana, int expectedSaves) {
        var maximum = Maximum(wizard, mana);
        Assert.Equal(expectedSaves, observation.Saves); Assert.Equal(maximum, observation.SavedValue); Assert.Equal(maximum, observation.LiveValue);
        var decoded = Assert.Single(MessageEncoder.Decode(MessageEncoder.Encode(observation.Message))!);
        if (mana) {
            var message = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEMANA>(decoded);
            Assert.Equal(maximum, message.Mana); Assert.Equal(50, message.MaxMana);
        } else {
            var message = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH>(decoded);
            Assert.Equal(wizard.GameObjectID, message.CharacterID); Assert.Equal(maximum, message.NewHealth); Assert.Equal(140, message.NewHealthMax);
        }
    }
    private static int Current(Wizard wizard, bool mana) => mana ? wizard.GameStats.m_currentMana : wizard.GameStats.m_currentHitpoints;
    private static int Maximum(Wizard wizard, bool mana) => mana ? wizard.GameStats.m_baseMana : wizard.GameStats.m_baseHitpoints;
    private static void SetCurrent(Wizard wizard, bool mana, int value) { if (mana) wizard.GameStats.m_currentMana = value; else wizard.GameStats.m_currentHitpoints = value; }
    private sealed class Scope : IDisposable {
        internal readonly TerminalClaimFixture F = new();
        internal readonly ResourceMutationDependencies Dependencies = new();
        private readonly ResourceMutationDependencies? _old = WizardResourceTransactions.TestScope.Value;
        internal Scope() {
            F.Saved.AccountId = F.Live.AccountId = AccountId; F.Saved.CharId = F.Live.CharId = CharacterId;
            F.Live.GameStats.m_baseHitpoints += 35; F.Live.GameStats.m_baseMana += 17;
            WizardResourceTransactions.TestScope.Value = Dependencies;
        }
        public void Dispose() { WizardResourceTransactions.TestScope.Value = _old; F.Dispose(); }
    }

    private sealed record Ready;
    private sealed record ExecuteStep(IResultContext Context, Result Result);
    private sealed record PacketObservation(IMessage Message, int Saves, int SavedValue, int LiveValue);
    private sealed class ResultContext(IActorRef player, CoreObject supplied, Result result) : IResultContext {
        public IEnumerable<Result> GetResults() => [result];
        public IActorRef GetZoneActor() => ActorRefs.Nobody;
        public IActorRef GetPlayerRef() => player;
        public CoreObject GetPlayerObj() => supplied;
        public IActorRef GetReplyTo() => ActorRefs.Nobody;
    }
    private sealed class Fixture : IDisposable {
        private readonly ActorSystem _system = ActorSystem.Create("resource-result-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        private readonly TerminalClaimFixture _store;
        private IActorRef _socket = null!, _handler = null!;
        internal IActorRef Endpoint = null!, OtherEndpoint = null!;
        internal SessionActor Session = null!;
        internal readonly ConcurrentQueue<PacketObservation> Packets = new();
        internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Fixture(TerminalClaimFixture store) => _store = store;
        internal static async Task<Fixture> Create(TerminalClaimFixture store, ResourceMutationDependencies dependencies, bool mana) {
            var f = new Fixture(store);
            try {
                f._socket = f._system.ActorOf(Props.Create(() => new SocketProbe(f, mana)), "socket");
                f.Endpoint = f._system.ActorOf(Props.CreateBy(new SessionProducer(f._socket)), "session");
                f.OtherEndpoint = f._system.ActorOf(Props.CreateBy(new SessionProducer(f._socket)), "other-session");
                f.Session = await f.Endpoint.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
                await f.OtherEndpoint.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
                var account = new Account();
                typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(account, store.Live.AccountId);
                account.CharacterIds.Add(store.Live.CharId);
                var accountRef = f._system.ActorOf(Props.Create(() => new AccountProbe(f.Session, account)), "account-service");
                var actualAccount = await accountRef.Ask<AccountService>(new Ready(), Timeout, TestContext.Current.CancellationToken);
                f.Session.RegisterService(accountRef, actualAccount); store.Live.Account = account;
                ActiveWizardDirectory.SetWizard(f.Endpoint, store.Live); ActiveWizardDirectory.SetGameObject(f.Endpoint, store.Live.GameObject);
                ActiveWizardDirectory.SetWizard(f.OtherEndpoint, store.Live); ActiveWizardDirectory.SetGameObject(f.OtherEndpoint, store.Live.GameObject);
                f._handler = f._system.ActorOf(Props.CreateBy(new HandlerProducer(store, dependencies, mana)), "result");
                Assert.True(await f._handler.Ask<bool>(new Ready(), Timeout, TestContext.Current.CancellationToken));
                var watcher = f._system.ActorOf(Props.Create(() => new CloseWatcher(f.Endpoint, f.Closed)), "watcher");
                Assert.True(await watcher.Ask<bool>(new Ready(), Timeout, TestContext.Current.CancellationToken));
                return f;
            } catch { f.Dispose(); throw; }
        }
        internal Task<bool> Execute(bool mana, IActorRef? player = null, CoreObject? supplied = null) {
            Result result = mana ? new ResAddMana() : new ResAddHealth();
            return _handler.Ask<bool>(new ExecuteStep(new ResultContext(player ?? Endpoint, supplied ?? _store.Live.GameObject, result), result),
                Timeout, TestContext.Current.CancellationToken);
        }
        internal async Task<PacketObservation[]> Drain() {
            if (!Session.IsDisposed) await Endpoint.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
            await _socket.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
            var result = new List<PacketObservation>(); while (Packets.TryDequeue(out var packet)) result.Add(packet); return result.ToArray();
        }
        public void Dispose() {
            if (Endpoint is not null) ActiveWizardDirectory.Remove(Endpoint);
            if (OtherEndpoint is not null) ActiveWizardDirectory.Remove(OtherEndpoint);
            _system.Terminate().GetAwaiter().GetResult(); _system.Dispose();
        }
        private sealed class SocketProbe : ReceiveActor {
            public SocketProbe(Fixture f, bool mana) => Receive<IMessage>(message => f.Packets.Enqueue(new(message,
                f._store.Saves, Current(f._store.Saved, mana), Current(f._store.Live, mana))));
        }
    }
    private sealed class SessionProducer(IActorRef socket) : IIndirectActorProducer {
        public Type ActorType => typeof(SessionActor);
        public ActorBase Produce() => new SessionActor(socket);
        public void Release(ActorBase actor) { }
    }
    private sealed class AccountProbe : AccountService {
        public AccountProbe(SessionActor session, Account account) : base(session)
            => typeof(AccountService).GetMethod("InternalReceiveSetAccount", Private)!.Invoke(this,
                [new ACCOUNT_104_PROTOCOL.MSG_ACCOUNT { Account = account }]);
        protected override void ConfigureReceivers() { Receive<Ready>(_ => Sender.Tell(this)); base.ConfigureReceivers(); }
    }
    private sealed class CloseWatcher : ReceiveActor {
        public CloseWatcher(IActorRef session, TaskCompletionSource closed) {
            Context.Watch(session); Receive<Ready>(_ => Sender.Tell(true)); Receive<Terminated>(_ => closed.TrySetResult());
        }
    }
    private sealed class HandlerProducer(TerminalClaimFixture store, ResourceMutationDependencies dependencies, bool mana) : IIndirectActorProducer {
        public Type ActorType => mana ? typeof(ResAddManaHandler) : typeof(ResAddHealthHandler);
        public ActorBase Produce() {
            ReceiveProtocolDispatcher actual = mana ? new ResAddManaHandler() : new ResAddHealthHandler();
            // Only the mailbox adapter installs this test's database hooks. Initialize and Execute are the actual
            // sealed production handler methods, including PlayerQuery and ResourceRewardBinding.
            var driver = new HandlerDriver(actual, store, dependencies);
            typeof(ActorBase).GetMethod("Become", Private, null, [typeof(Receive)], null)!.Invoke(actual, [new Receive(driver.Dispatch)]);
            return actual;
        }
        public void Release(ActorBase actor) { }
    }
    private sealed class HandlerDriver(ReceiveProtocolDispatcher actor, TerminalClaimFixture store, ResourceMutationDependencies dependencies) {
        internal bool Dispatch(object message) {
            var sender = (IActorRef)typeof(ActorBase).GetProperty("Sender", Private | BindingFlags.Public)!.GetValue(actor)!;
            if (message is Ready) { sender.Tell(true); return true; }
            if (message is not ExecuteStep call) return true;
            try {
                using var hooks = EnterScopes();
                var handler = (IResultHandler)actor; handler.Initialize(call.Context, call.Result);
                sender.Tell(handler.Execute(call.Context));
            } catch (Exception error) { sender.Tell(new Status.Failure(error)); }
            return true;
        }
        private IDisposable EnterScopes() {
            var oldStore = WizardCollection.TestStoreScope.Value; var oldClaim = ClassicQuestClaims.TestScope.Value;
            var oldStack = ClassicStackRewards.TestScope.Value; var oldProgression = WizardProgressionTransactions.TestScope.Value;
            var oldItems = WizardInventoryTransactions.TestRowsScope.Value; var oldReagents = WizardReagentCollection.TestRowsScope.Value;
            var oldResource = WizardResourceTransactions.TestScope.Value;
            store.Install(); WizardResourceTransactions.TestScope.Value = dependencies;
            return new Restore(() => {
                WizardCollection.TestStoreScope.Value = oldStore; ClassicQuestClaims.TestScope.Value = oldClaim;
                ClassicStackRewards.TestScope.Value = oldStack; WizardProgressionTransactions.TestScope.Value = oldProgression;
                WizardInventoryTransactions.TestRowsScope.Value = oldItems; WizardReagentCollection.TestRowsScope.Value = oldReagents;
                WizardResourceTransactions.TestScope.Value = oldResource;
            });
        }
    }
    private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
}
