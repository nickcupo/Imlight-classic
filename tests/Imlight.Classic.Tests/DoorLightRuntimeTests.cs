using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Game.Zone.Supervisors;
using Imlight.CoreLib.Shared.Packets;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class DoorLightRuntimeTests {
    private sealed class LightEntity : ZoneEntity {
        public LightEntity(IActorRef zone, bool light)
            : base(new WizClientObject { m_globalID = 900UL },
                new GameObjectTemplate { m_templateID = light ? 741306u : 1u, m_objectName = light ? "WC-QuestLight" : "Other", m_behaviors = [] },
                new CoreObjectInfo { m_zoneTag = "TestLight" }, zone, null) {
            AddComponent(typeof(RenderComponent));
        }
    }
    private sealed class Recorder : ReceiveActor {
        public Recorder(Channel<object> packets) { Receive<object>(p => packets.Writer.TryWrite(p)); }
    }
    private sealed record GetProducer;
    private sealed record JoinLight(IActorRef Player);
    private sealed record LeaveLight(IActorRef Player);
    private sealed record ReplayLight(IActorRef Player);
    private sealed class ProducerEntity : ZoneEntity {
        public ProducerEntity(IActorRef zone) : base(new WizClientObject { m_globalID = 901UL },
            new GameObjectTemplate { m_templateID = 741306u, m_objectName = "WC-QuestLight", m_behaviors = [] },
            new CoreObjectInfo { m_zoneTag = "TestLight" }, zone, null) { AddComponent(typeof(QuestDoorLightComponent)); }
        protected override void ConfigureReceivers() {
            Receive<GetProducer>(_ => Sender.Tell(GetComponentOfType<QuestDoorLightComponent>().ActorRef));
            Receive<JoinLight>(m => { GetComponentOfType<QuestDoorLightComponent>().OnPlayerJoin(new CoreObject(), m.Player, null); Sender.Tell(true); });
            Receive<LeaveLight>(m => { GetComponentOfType<QuestDoorLightComponent>().OnPlayerLeave(m.Player, 1); Sender.Tell(true); });
            Receive<ReplayLight>(m => { GetComponentOfType<QuestDoorLightComponent>().ReplayFor(m.Player); Sender.Tell(true); });
            base.ConfigureReceivers();
        }
    }
    [Fact] public async Task ProducerScopesCachesAndReplaysPerPlayerAndIgnoresDepartedReplies() {
        using var system = ActorSystem.Create("door-light-producer", "akka.actor.provider = local");
        try {
            var aPackets=Channel.CreateUnbounded<object>(); var bPackets=Channel.CreateUnbounded<object>();
            var a=system.ActorOf(Props.Create(() => new Recorder(aPackets))); var b=system.ActorOf(Props.Create(() => new Recorder(bPackets)));
            var entity=system.ActorOf(Props.Create(() => new ProducerEntity(a)));
            var timeout=TimeSpan.FromSeconds(5); var cancel=TestContext.Current.CancellationToken;
            var producer = await entity.Ask<IActorRef>(new GetProducer(), timeout, cancel);
            await entity.Ask<bool>(new JoinLight(a), timeout, cancel); await entity.Ask<bool>(new JoinLight(b), timeout, cancel);
            producer.Tell(new DoorLightDecision(a, "Off")); producer.Tell(new DoorLightDecision(b, "Quest"));
            Assert.Equal(StringHash.Compute("Off"), Assert.IsType<GAME_5_PROTOCOL.MSG_ENTERSTATE>(await aPackets.Reader.ReadAsync(cancel).AsTask().WaitAsync(timeout,cancel)).State);
            Assert.Equal(StringHash.Compute("Quest"), Assert.IsType<GAME_5_PROTOCOL.MSG_ENTERSTATE>(await bPackets.Reader.ReadAsync(cancel).AsTask().WaitAsync(timeout,cancel)).State);
            producer.Tell(new DoorLightDecision(a,"Off"));
            await entity.Ask<bool>(new ReplayLight(a),timeout,cancel);
            Assert.Equal(StringHash.Compute("Off"), Assert.IsType<GAME_5_PROTOCOL.MSG_ENTERSTATE>(await aPackets.Reader.ReadAsync(cancel).AsTask().WaitAsync(timeout,cancel)).State);
            await entity.Ask<bool>(new LeaveLight(a),timeout,cancel);
            producer.Tell(new DoorLightDecision(a,"On"));
            await entity.Ask<bool>(new ReplayLight(a),timeout,cancel);
            Assert.False(aPackets.Reader.TryRead(out _)); Assert.False(bPackets.Reader.TryRead(out _));
        } finally { await system.Terminate(); }
    }
    [Fact] public void BindingRequiresExactLightTemplateAndPlacementTagAndOneEvent() {
        Trigger Link(string tag, uint tid, string eventName) => new() {
            m_triggerObjInfo = new TriggerObjectInfo { m_templateID = tid, m_zoneTag = tag }, m_fireEvents = [eventName],
        };
        Assert.Null(ZoneTriggerSupervisor.BindingEvent([], "Lamp"));
        Assert.Null(ZoneTriggerSupervisor.BindingEvent([Link("Lamp-other", 741306, "Enter")], "Lamp"));
        Assert.Null(ZoneTriggerSupervisor.BindingEvent([Link("Lamp", 1, "Enter")], "Lamp"));
        Assert.Null(ZoneTriggerSupervisor.BindingEvent([Link("Lamp", 741306, "EnterA"), Link("Lamp", 741306, "EnterB")], "Lamp"));
        Assert.Equal("Enter", ZoneTriggerSupervisor.BindingEvent([Link("Lamp", 741306, "Enter")], "Lamp"));
    }
    [Theory]
    [InlineData("Off")]
    [InlineData("On")]
    [InlineData("Quest")]
    public async Task ExactLightStatesAreVisualPacketsRatherThanDespawn(string state) {
        using var system = ActorSystem.Create("door-light-visual", "akka.actor.provider = local");
        try {
            var packets = Channel.CreateUnbounded<object>();
            var player = system.ActorOf(Props.Create(() => new Recorder(packets)));
            var light = system.ActorOf(Props.Create(() => new LightEntity(player, true)));
            light.Tell(new ZONE_102_PROTOCOL.MSG_ENTERSTATE { ObjectName = "TestLight", StateName = state, Sender = player });
            var packet = await packets.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var visual = Assert.IsType<GAME_5_PROTOCOL.MSG_ENTERSTATE>(packet);
            Assert.Equal(900UL, visual.GameObjectID); Assert.Equal(StringHash.Compute(state), visual.State);
        } finally { await system.Terminate(); }
    }
    [Fact] public async Task OrdinaryObjectsRetainOffDespawnBehavior() {
        using var system = ActorSystem.Create("door-light-ordinary", "akka.actor.provider = local");
        try {
            var packets = Channel.CreateUnbounded<object>();
            var player = system.ActorOf(Props.Create(() => new Recorder(packets)));
            var ordinary = system.ActorOf(Props.Create(() => new LightEntity(player, false)));
            ordinary.Tell(new ZONE_102_PROTOCOL.MSG_ENTERSTATE { ObjectName = "TestLight", StateName = "Off", Sender = player });
            Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEOBJECT>(await packets.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        } finally { await system.Terminate(); }
    }
}
