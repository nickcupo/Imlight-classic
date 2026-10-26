// CLASSIC: refreshing an NPC's options for a wizard who is still in range sends MSG_SENDNPCOPTIONS only. r806919's
// MSG_LEAVESERVICERANGE (0x14172b6a0) runs RemoveInteraction and CancelInteraction, which closes whatever the wizard has
// open at that NPC; NewNPCInteraction (0x14172ad00) replaces a known NPC's options in place. qa-s164: finbot04 opened
// Sergeant Muldoon's services menu with X, and 2 s later the join-range resend (LEAVE, then the options) closed it.
// Only a real exit from range sends LEAVE.

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
public sealed class NpcOptionsNoLeaveTests : IDisposable {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly object? _originalPriority;
    private readonly object? _originalOrder;

    public NpcOptionsNoLeaveTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        _originalPriority = PriorityField("s_wizBangPriority").GetValue(null);
        _originalOrder = PriorityField("s_wizBangList").GetValue(null);
        PriorityField("s_wizBangPriority").SetValue(null, new WizBangPriorityTemplate());
        PriorityField("s_wizBangList").SetValue(null, new List<WizBangs> { WizBangs.StartQuest });
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-noleave-tests.log")}\n");
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
    public void OnlyTheStockEngineLeavesRangeToRefresh() {
        Assert.False(InteractServiceMementoComponent.RefreshLeavesRange(classicEngine: true));
        Assert.True(InteractServiceMementoComponent.RefreshLeavesRange(classicEngine: false));
    }

    [Fact]
    public async Task EveryRefreshOfAWizardInRangeSendsTheOptionsWithoutLeavingRange() {
        using var system = ActorSystem.Create("npc-noleave-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        try {
            var npc = system.ActorOf(Props.Create(() => new Npc()));
            var player = system.ActorOf(Props.Create(() => new Recorder()));
            await npc.Ask<Done>(new Prepare(), Timeout, TestContext.Current.CancellationToken);

            // Coming into range: the options.
            npc.Tell(Move(player, 10));
            Assert.Equal(["OPTIONS 0"], await Drain(npc, player));

            // The join-range resend after zone entry (qa-s164's closing menu).
            npc.Tell(new InteractServiceMementoComponent.ResendJoinRange(player));
            Assert.Equal(["OPTIONS 0"], await Drain(npc, player));

            // ResReInteract.
            npc.Tell(new InteractServiceMementoComponent.ReInteractPlayer(player));
            Assert.Equal(["OPTIONS 0"], await Drain(npc, player));

            // A click on an option of a list the wizard no longer has (a service the NPC does not offer now).
            npc.Tell(new ZONE_102_PROTOCOL.MSG_ZONEINTERACTION {
                GlobalID = 900, ServiceName = "QuestOfferService", ServiceIndex = 7, PlayerActor = player,
                PlayerCharacter = new Wizard(), PlayerObject = Body(10),
            });
            Assert.Equal(["OPTIONS 2"], await Drain(npc, player));

            // An object whose list was empty when the wizard came into range has options now (a quest step done).
            await npc.Ask<Done>(new SetOptions(0), Timeout, TestContext.Current.CancellationToken);
            npc.Tell(new InteractServiceMementoComponent.ResendJoinRange(player)); // records the empty list
            Assert.Empty(await Drain(npc, player));
            await npc.Ask<Done>(new SetOptions(1), Timeout, TestContext.Current.CancellationToken);
            npc.Tell(new ZONE_102_PROTOCOL.MSG_WIZBANG_UPDATE_RESULT { PlayerActors = [player], Wizards = [new Wizard()] });
            Assert.Equal(["OPTIONS 0"], await Drain(npc, player));

            // Walking out of range is a real exit: LEAVE.
            npc.Tell(Move(player, 5000));
            Assert.Equal(["LEAVE"], await Drain(npc, player));
        } finally {
            await system.Terminate();
        }
    }

    private static ZONE_102_PROTOCOL.MSG_PLAYERMOVE Move(IActorRef player, float x)
        => new() { PlayerActor = player, PlayerObject = Body(x) };

    private static CoreObject Body(float x) => new() { m_globalID = 77UL, m_location = new Vector3(x, 0, 0) };

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
        public Npc() : base(new CoreObject { m_globalID = 900UL }, new GameObjectTemplate { m_displayName = "Sergeant Muldoon" },
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
