// CLASSIC: an NPC that starts after a wizard is already in the zone (a dormant spawner started for the wizard's open goal,
// an informant a zone trigger brings) never saw the wizard's MSG_ADDPLAYER, and an NPC is offered only on a join or a move:
// qa-s246 stood 14 units from the Factory Baxter (MB-AIR3-NPC02, Ironworks) and got no options. The zone now introduces
// the wizards already there (ZONE_102_PROTOCOL.MSG_INTRODUCEPLAYERS, MSG_LATEPLAYER) once the creature has loaded.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class LateSpawnedNpcOptionsTests : IDisposable {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly object? _originalPriority;
    private readonly object? _originalOrder;

    public LateSpawnedNpcOptionsTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        _originalPriority = PriorityField("s_wizBangPriority").GetValue(null);
        _originalOrder = PriorityField("s_wizBangList").GetValue(null);
        PriorityField("s_wizBangPriority").SetValue(null, new WizBangPriorityTemplate());
        PriorityField("s_wizBangList").SetValue(null, new List<WizBangs> { WizBangs.StartQuest });
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-latenpc-tests.log")}\n");
            ConfigurationManager.Initialize(path);
        } finally {
            File.Delete(path);
        }
    }

    public void Dispose() {
        PriorityField("s_wizBangPriority").SetValue(null, _originalPriority);
        PriorityField("s_wizBangList").SetValue(null, _originalOrder);
        ClassicRuntime.ResetForTests();
    }

    private static FieldInfo PriorityField(string name)
        => typeof(WizBangPriority).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    [Fact]
    public async Task AWizardAlreadyBesideAnNpcThatSpawnsLaterIsOfferedItWithoutMoving() {
        using var system = ActorSystem.Create("npc-late-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        try {
            var npc = system.ActorOf(Props.Create(() => new Npc()));
            var near = system.ActorOf(Props.Create(() => new Recorder()));
            var far = system.ActorOf(Props.Create(() => new Recorder()));
            await npc.Ask<Done>(new Prepare(), Timeout, TestContext.Current.CancellationToken);

            npc.Tell(Late(near, 14));
            npc.Tell(Late(far, 5000, id: 78));
            Assert.Equal(["OPTIONS 0"], await Drain(npc, near));
            Assert.Empty(await Drain(npc, far));

            // A second introduction (the wizard's own join raced the creature's load) sends nothing new.
            npc.Tell(Late(near, 14));
            Assert.Empty(await Drain(npc, near));

            // The wizard the NPC was introduced to is tracked as any other: walking away leaves its range.
            npc.Tell(Move(near, 5000));
            Assert.Equal(["LEAVE"], await Drain(npc, near));
        } finally {
            await system.Terminate();
        }
    }

    private static ZONE_102_PROTOCOL.MSG_LATEPLAYER Late(IActorRef player, float x, ulong id = 77)
        => new() { PlayerActor = player, PlayerObject = Body(x, id), Wizard = new Wizard() };

    private static ZONE_102_PROTOCOL.MSG_PLAYERMOVE Move(IActorRef player, float x)
        => new() { PlayerActor = player, PlayerObject = Body(x) };

    private static CoreObject Body(float x, ulong id = 77) => new() { m_globalID = id, m_location = new Vector3(x, 0, 0) };

    // What the player got since the last drain, once the NPC has handled everything sent before.
    private static async Task<string[]> Drain(IActorRef npc, IActorRef player) {
        await npc.Ask<Done>(new Prepare(), Timeout, TestContext.Current.CancellationToken);
        return await player.Ask<string[]>(new Take(), Timeout, TestContext.Current.CancellationToken);
    }

    private sealed record Prepare;
    private sealed record SetOptions(int Count);
    private sealed record Done;
    private sealed record Take;

    private sealed class Npc : ZoneEntity {
        public Npc() : base(new CoreObject { m_globalID = 900UL }, new GameObjectTemplate { m_displayName = "Baxter" },
            null!, ActorRefs.Nobody, null!) {
            AddComponent(typeof(Service));
            AddComponent(typeof(InteractServiceMementoComponent));
            GetComponentOfType<InteractServiceMementoComponent>().OnStart();
        }

        [MessageHandler(typeof(Prepare))]
        private void OnPrepare(Prepare _) {
            Timers.CancelAll(); // no automatic ticks or join-range timer: the test sends them
            Sender.Tell(new Done());
        }

        [MessageHandler(typeof(SetOptions))]
        private void OnSetOptions(SetOptions message) {
            GetComponentOfType<Service>().Count = message.Count;
            Sender.Tell(new Done());
        }
    }

    private sealed class Recorder : ReceiveActor {
        public Recorder() {
            var got = new List<string>();
            Receive<CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD>(_ => Sender.Tell(new CHARACTER_103_PROTOCOL.MSG_CHARACTER { Wizard = new Wizard() }));
            Receive<GAME_5_PROTOCOL.MSG_LEAVESERVICERANGE>(_ => got.Add("LEAVE"));
            Receive<QUEST_MESSAGES_52_PROTOCOL.MSG_SENDNPCOPTIONS>(m => got.Add($"OPTIONS {m.Reinteract}"));
            Receive<Take>(_ => {
                Sender.Tell(got.ToArray());
                got.Clear();
            });
            ReceiveAny(_ => { });
        }
    }

    private sealed class Service(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent {
        public int Count { get; set; } = 1;
        public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter)
            => Enumerable.Range(0, Count).Select(_ => new InteractableOption { m_serviceName = "Test" });
        public void OnServiceInteraction(IActorRef actor, Wizard wizard, CoreObject obj, uint index) { }
        public string ServiceName => "Test";
        public string NpcIcon => "";
        public string NpcNameKey => "";
        public string NpcTextKey => "";
        public WizBangs WizBang => WizBangs.StartQuest;
        public string StateName => "";
        public string InteractWizBang => "";
        public string DisplayKey => "";
    }
}
