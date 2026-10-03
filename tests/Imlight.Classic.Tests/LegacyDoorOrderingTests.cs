using System;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Game.Zone.Supervisors;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class LegacyDoorOrderingTests {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private sealed record Sent(IMessage Packet, IActorRef From);
    private sealed record SetWizard(Wizard Wizard) : IServerMessage {
        public byte MessageOrder => 0;
        public byte ServiceID => 102;
    }
    private sealed record SetDoor(LegacyDoorBindings.Binding Binding, bool Eligible, bool ActiveGoal) : IServerMessage {
        public byte MessageOrder => 0;
        public byte ServiceID => 102;
    }
    private sealed record Release;

    private sealed class FailureProbe : ReceiveActor {
        public FailureProbe(TaskCompletionSource<Exception> failure) {
            Receive<Akka.Event.Error>(error => failure.TrySetResult(new Exception(error.ToString(), error.Cause)));
        }
    }
    private sealed class SocketProbe : ReceiveActor {
        public SocketProbe(Channel<Sent> packets) {
            Receive<IMessage>(p => packets.Writer.TryWrite(new(p, Sender)));
        }
    }
    private sealed class HeldOwnerObject : ReceiveActor {
        public HeldOwnerObject(IActorRef session, LegacyDoorOwnerObject message) {
            Receive<Release>(_ => { session.Tell(message, Self); Sender.Tell(true); });
        }
    }
    private sealed class WizardProbeService : MessageService {
        public WizardProbeService(SessionActor session) : base(session) { }
        private Wizard _wizard = null!;
        [MessageHandler(typeof(SetWizard))]
        public void Configure(SetWizard message) { _wizard = message.Wizard; Sender.Tell(true); }
        [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD))]
        public void Query(CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD message)
            => Sender.Tell(new CHARACTER_103_PROTOCOL.MSG_CHARACTER { Wizard = _wizard, WizardGameObject = _wizard.GameObject });
    }
    private sealed class DoorZoneProbeService : MessageService {
        public DoorZoneProbeService(SessionActor session) : base(session) { }
        private LegacyDoorBindings.Binding _binding = null!;
        private string _state = null!;
        [MessageHandler(typeof(SetDoor))]
        public void Configure(SetDoor message) {
            _binding = message.Binding;
            GoalTemplate[] goals = message.ActiveGoal
                ? [new WaypointGoalTemplate { m_zoneEntry = true, m_zoneTag = _binding.Destination, m_destinationZone = "" }]
                : [];
            _state = DoorLightRules.State([new(_binding.Destination, true, message.Eligible)],
                ZoneTriggerSupervisor.RelevantDoorDestinations(goals, [_binding]))!;
            Sender.Tell(true);
        }
        [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST))]
        public void Refresh(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST message) {
            var attach = SessionActor.DoorAttach!;
            SessionActor.ActorRef.Tell(new LegacyDoorSnapshot {
                Zone = attach.Zone, ZoneActor = attach.Actor, AttachGeneration = attach.Generation,
                States = [new(_binding, _state)],
            }, Self);
        }
    }

    private sealed class SessionProducer(IActorRef socket) : IIndirectActorProducer {
        public System.Type ActorType => typeof(SessionActor);
        public ActorBase Produce() => new SessionActor(socket, typeof(WizardProbeService), typeof(DoorZoneProbeService), typeof(QuestService));
        public void Release(ActorBase actor) { }
    }

    private sealed class Fixture : IAsyncDisposable {
        public ActorSystem System { get; }
        public Channel<Sent> Packets { get; } = Channel.CreateUnbounded<Sent>();
        public IActorRef Session { get; private set; } = null!;
        public SessionActor Instance { get; private set; } = null!;
        public IActorRef Quest { get; private set; } = null!;
        public IActorRef Socket { get; private set; } = null!;
        public ZoneAttachContext Attach { get; private set; } = null!;
        public LegacyDoorBindings.Binding Binding { get; }
        public ulong Owner => 12345;
        public string State { get; }
        private Fixture(string tag, string state) {
            EquipmentAttachConcurrencyTests.Configure();
            System = ActorSystem.Create("door-order-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
            Binding = Assert.Single(LegacyDoorBindings.All, b => b.Tag == tag);
            State = state;
        }
        public static async Task<Fixture> Create(string tag = "WC_Unicorn_H02", string state = "Quest", bool? eligible = null, bool? activeGoal = null) {
            var fixture = new Fixture(tag, state);
            try {
                var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
                var errors = fixture.System.ActorOf(Props.Create(() => new FailureProbe(failure)));
                fixture.System.EventStream.Subscribe(errors, typeof(Akka.Event.Error));
                fixture.Socket = fixture.System.ActorOf(Props.Create(() => new SocketProbe(fixture.Packets)), "socket");
                fixture.Session = fixture.System.ActorOf(Props.CreateBy(new SessionProducer(fixture.Socket)), "session");
                var identity = fixture.Session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
                if (await Task.WhenAny(identity, failure.Task) == failure.Task) throw await failure.Task;
                fixture.Instance = await identity;
                fixture.Quest = await fixture.System.ActorSelection(fixture.Session.Path + "/" + typeof(QuestService))
                    .ResolveOne(Timeout, TestContext.Current.CancellationToken);
                var wizard = new Wizard { CharId = fixture.Owner - 2 };
                wizard.Zone = fixture.Binding.Zone;
                wizard.GameObject = new WizClientObject { m_globalID = fixture.Owner, m_inactiveBehaviors = [new ClientDynaModBehavior()] };
                Assert.True(await fixture.Session.Ask<bool>(new SetWizard(wizard), Timeout, TestContext.Current.CancellationToken));
                Assert.True(await fixture.Session.Ask<bool>(new SetDoor(fixture.Binding, eligible ?? state != "Off", activeGoal ?? state == "Quest"), Timeout, TestContext.Current.CancellationToken));
                var zone = await fixture.System.ActorSelection(fixture.Session.Path + "/" + typeof(DoorZoneProbeService))
                    .ResolveOne(Timeout, TestContext.Current.CancellationToken);
                fixture.Attach = new(fixture.Binding.Zone, zone, 10, fixture.Owner);
                fixture.Instance.PublishDoorAttach(fixture.Attach);
                return fixture;
            } catch { await fixture.DisposeAsync(); throw; }
        }
        public LegacyDoorOwnerObject Object(ZoneAttachContext? attach = null, ulong? owner = null) {
            var id = owner ?? Owner;
            var serializer = new CoreObjectSerializer(false, SerializerFlags.None);
            Assert.True(serializer.Serialize(new WizClientObject { m_globalID = id, m_inactiveBehaviors = [new ClientDynaModBehavior()] }, 28, out var data));
            return new(attach ?? Attach, id, new GAME_5_PROTOCOL.MSG_NEWOBJECT { Data = data });
        }
        public LegacyDoorSnapshot Snapshot(ZoneAttachContext? attach = null) {
            var context = attach ?? Attach;
            return new() { Zone = context.Zone, ZoneActor = context.Actor, AttachGeneration = context.Generation, States = [new(Binding, State)] };
        }
        public async Task Drain() {
            await Session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
            await Quest.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
            await Session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
            await Socket.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
        }
        public async Task Baseline() {
            var first = await Next();
            var created = Assert.IsType<GAME_5_PROTOCOL.MSG_NEWOBJECT>(first.Packet);
            Assert.Equal(Session, first.From);
            Assert.True(new CoreObjectSerializer(false, SerializerFlags.None).Deserialize<WizClientObject>((byte[])created.Data, 28, out var player));
            Assert.NotNull(player);
            Assert.Equal(Owner, player.m_globalID.Full);
            for (int i = 0; i < 7; i++) {
                var sent = await Next();
                Assert.Equal(Session, sent.From);
                var packet = Assert.IsType<GAME_5_PROTOCOL.MSG_DYNAMODBEHAVIOR_UPDATEMODS>(sent.Packet);
                Assert.Equal(Owner, packet.GlobalID);
                Assert.Equal(0, packet.UpdateAll);
                var state = i < 6 ? new[] { "Off", "Quest", "On" }[i / 2] : State;
                Assert.Equal(QuestService.LegacyDoorIndex(Binding, state), packet.Index);
                Assert.Equal(i < 6 && i % 2 == 1 ? 1 : 0, packet.Remove);
                Assert.Equal(i < 6 && i % 2 == 1 ? 0 : 1, packet.Add);
                if (packet.Add == 1) {
                    Assert.True(new ObjectSerializer(false, SerializerFlags.None).Deserialize<DynaMod>((byte[])packet.NewMod, 24, out var mod));
                    Assert.NotNull(mod);
                    Assert.Equal(QuestService.LegacyDoorAlias(Binding, state), mod.m_clientTag);
                }
            }
            await Drain();
            Assert.False(Packets.Reader.TryRead(out _));
        }
        private async Task<Sent> Next() => await Packets.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(Timeout, TestContext.Current.CancellationToken);
        public void Reconnect() {
            Attach = Attach with { Generation = Attach.Generation + 1 };
            Instance.PublishDoorAttach(Attach);
        }
        public async ValueTask DisposeAsync() => await System.Terminate();
    }

    [Theory]
    [InlineData("WC_Unicorn_H02", "Quest")]
    [InlineData("WC_Hub_H17", "On")]
    public async Task HeldOwnerObjectOrdersSevenPacketsAndRecreationAndReconnectReplayIdenticalState(string tag, string state) {
        await using var fixture = await Fixture.Create(tag, state);
        var held = fixture.System.ActorOf(Props.Create(() => new HeldOwnerObject(fixture.Session, fixture.Object())));
        fixture.Session.Tell(fixture.Snapshot());
        await fixture.Drain();
        Assert.False(fixture.Packets.Reader.TryRead(out _));
        Assert.True(await held.Ask<bool>(new Release(), Timeout, TestContext.Current.CancellationToken));
        await fixture.Baseline();
        fixture.Session.Tell(fixture.Snapshot());
        await fixture.Drain();
        Assert.False(fixture.Packets.Reader.TryRead(out _));
        fixture.Session.Tell(fixture.Object());
        await fixture.Baseline();
        fixture.Reconnect();
        fixture.Session.Tell(fixture.Snapshot());
        await fixture.Drain();
        Assert.False(fixture.Packets.Reader.TryRead(out _));
        fixture.Session.Tell(fixture.Object());
        await fixture.Baseline();
    }

    [Fact]
    public async Task StaleObjectsReadyAndSocketBatchesCannotEmitOrInvalidateCurrentState() {
        await using var fixture = await Fixture.Create();
        fixture.Session.Tell(fixture.Object());
        await fixture.Baseline();
        var peer = fixture.System.ActorOf(Props.Create(() => new SocketProbe(Channel.CreateUnbounded<Sent>())));
        foreach (var stale in new[] {
            fixture.Attach with { Generation = 9 }, fixture.Attach with { Actor = peer },
            fixture.Attach with { Zone = "WizardCity/WC_Hub" }, fixture.Attach with { Owner = 54321 },
        }) {
            fixture.Session.Tell(fixture.Object(stale));
            fixture.Session.Tell(new LegacyDoorPlayerReady { Zone = stale.Zone, ZoneActor = stale.Actor, AttachGeneration = stale.Generation, Owner = stale.Owner, Replay = true });
            fixture.Session.Tell(new LegacyDoorSocketBatch(stale, fixture.Owner, [QuestService.LegacyDoorPacket(fixture.Owner, fixture.Binding)]));
        }
        fixture.Session.Tell(fixture.Object(owner: 54321));
        fixture.Session.Tell(new LegacyDoorSocketBatch(fixture.Attach, fixture.Owner,
            [QuestService.LegacyDoorPacket(fixture.Owner, fixture.Binding), QuestService.LegacyDoorPacket(54321, fixture.Binding)]));
        fixture.Session.Tell(fixture.Snapshot());
        await fixture.Drain();
        Assert.False(fixture.Packets.Reader.TryRead(out _));
        // A context change between QuestService producing a batch and session
        // delivery must reject it even if the old feed already recorded it.
        var old = fixture.Attach;
        fixture.Reconnect();
        fixture.Session.Tell(new LegacyDoorSocketBatch(old, fixture.Owner, [QuestService.LegacyDoorPacket(fixture.Owner, fixture.Binding)]));
        await fixture.Drain();
        Assert.False(fixture.Packets.Reader.TryRead(out _));
        fixture.Session.Tell(fixture.Object());
        await fixture.Baseline();
    }

    [Theory]
    [InlineData(true, true, "Quest")]
    [InlineData(true, false, "On")]
    [InlineData(false, true, "Off")]
    [InlineData(false, false, "Off")]
    public async Task OryanRouteEligibilityAndActiveInteriorGoalSelectBlueYellowOrOff(bool eligible, bool activeGoal, string expected) {
        await using var fixture = await Fixture.Create("WC_Unicorn_H02", expected, eligible, activeGoal);
        fixture.Session.Tell(fixture.Object());
        await fixture.Baseline();
    }

    private sealed class PeerBroadcastHarness : ZoneEntitySupervisor {
        public PeerBroadcastHarness(IActorRef owner, IActorRef peer) : base(null!) { EntityActors.Add(owner); EntityActors.Add(peer); }
        public override void ReceiveZoneLoadResults(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) { }
    }

    [Fact]
    public async Task BoundPeerBroadcastUsesProductionFilterToExcludeOnlyOwner() {
        EquipmentAttachConcurrencyTests.Configure();
        using var system = ActorSystem.Create("door-peer-broadcast", "akka.actor.provider = local");
        try {
            var ownPackets = Channel.CreateUnbounded<Sent>();
            var peerPackets = Channel.CreateUnbounded<Sent>();
            var owner = system.ActorOf(Props.Create(() => new SocketProbe(ownPackets)));
            var peer = system.ActorOf(Props.Create(() => new SocketProbe(peerPackets)));
            var broadcast = system.ActorOf(Props.Create(() => new PeerBroadcastHarness(owner, peer)));
            var packet = new GAME_5_PROTOCOL.MSG_NEWOBJECT();
            broadcast.Tell(ZoneService.PlayerSpawnBroadcast(packet, true, owner));
            await broadcast.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
            await owner.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
            await peer.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
            Assert.False(ownPackets.Reader.TryRead(out _));
            Assert.True(peerPackets.Reader.TryRead(out var sent));
            Assert.Same(packet, sent.Packet);
            broadcast.Tell(ZoneService.PlayerSpawnBroadcast(packet, false, owner));
            await broadcast.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
            await owner.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
            Assert.True(ownPackets.Reader.TryRead(out sent));
            Assert.Same(packet, sent.Packet);
        } finally { await system.Terminate(); }
    }

    [Fact]
    public void OwnerOrderingIsLimitedToBoundClassicZones() {
        ClassicRuntime.ResetForTests();
        try {
            ClassicRuntime.Initialize(new ClassicRules(ZoneFixture.Profile(levelCap: 50), ZoneFixture.MinimalMap()));
            Assert.True(ZoneService.UsesLegacyDoorOrdering("WizardCity/WC_Streets/WC_Unicorn"));
            Assert.True(ZoneService.UsesLegacyDoorOrdering("WizardCity/WC_Hub"));
            Assert.False(ZoneService.UsesLegacyDoorOrdering("Unbound/Zone"));
            ClassicRuntime.ResetForTests();
            ClassicRuntime.Initialize(ClassicRules.Stock);
            Assert.False(ZoneService.UsesLegacyDoorOrdering("WizardCity/WC_Streets/WC_Unicorn"));
        } finally { ClassicRuntime.ResetForTests(); }
    }

    [Fact]
    public async Task OrdinaryOutgoingPacketsKeepTheirOriginalSender() {
        await using var fixture = await Fixture.Create();
        var origin = fixture.System.ActorOf(Props.Create(() => new SocketProbe(Channel.CreateUnbounded<Sent>())));
        var packet = new GAME_5_PROTOCOL.MSG_NEWOBJECT();
        fixture.Session.Tell(packet, origin);
        await fixture.Drain();
        Assert.True(fixture.Packets.Reader.TryRead(out var sent));
        Assert.Same(packet, sent.Packet);
        Assert.Equal(origin, sent.From);
    }
}
