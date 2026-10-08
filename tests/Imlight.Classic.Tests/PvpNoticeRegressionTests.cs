// CLASSIC: execute the production PvP service handlers against an authored in-memory packet sink.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic.Pvp;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Game.Commands;
using Imlight.CoreLib.Game.Commands.Protocols;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class PvpNoticeRegressionTests : IDisposable {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const ulong Character = 771910;

    public PvpNoticeRegressionTests() {
        EquipmentAttachConcurrencyTests.Configure();
        ClassicRuntime.ResetForTests();
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    [Theory]
    [InlineData(ArenaKind.Practice, true, false, false)]
    [InlineData(ArenaKind.Practice, false, true, false)]
    [InlineData(ArenaKind.Practice, false, false, true)]
    [InlineData(ArenaKind.Ranked, true, false, false)]
    [InlineData(ArenaKind.Ranked, false, true, false)]
    [InlineData(ArenaKind.Ranked, false, false, true)]
    public async Task ProductionOutcomeKeepsNativeResultAndCurrenciesWithoutCreatingAnAlert(
        ArenaKind kind, bool won, bool fled, bool noContest) {
        using var system = ActorSystem.Create("pvp-result-" + Guid.NewGuid().ToString("N"), "akka.actor.provider=local");
        var sink = system.ActorOf(Props.Create(() => new PacketSink()));
        var wizard = Wizard();
        var service = system.ActorOf(Props.Create(() => new ArenaService(Session(sink))));
        var result = new GAME_5_PROTOCOL.MSG_MATCHRESULT();
        var outcome = new ArenaOutcome(1, kind, won, fled, 500, 520, "Private", 0, result,
            "WizardCity/WC_Arena", "ArenaStart", 3600, noContest);
        try {
            await Cache(service, wizard);
            service.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_ARENAOUTCOME { Outcome = outcome });
            await Processed(service);
            var packets = await sink.Ask<object[]>(new Snapshot(), Timeout, TestContext.Current.CancellationToken);
            Assert.Equal(3, packets.Length);
            Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEARENAPOINTS>(packets[0]);
            Assert.IsType<WIZARD3_56_PROTOCOL.MSG_UPDATEPVPCURRENCY>(packets[1]);
            Assert.Same(result, packets[2]);
            Assert.DoesNotContain(packets, packet => packet is EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE);
            Assert.Equal(77, wizard.GameStats.m_currentArenaPoints);
            Assert.Equal(71, wizard.GameStats.m_currentPvPCurrency);
        }
        finally { await system.Terminate(); }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ProductionDuelReleaseKeepsGraceAndCombatStateWithoutCreatingAnAlert(bool won, bool fought) {
        using var system = ActorSystem.Create("pvp-release-" + Guid.NewGuid().ToString("N"), "akka.actor.provider=local");
        var sink = system.ActorOf(Props.Create(() => new PacketSink()));
        var wizard = Wizard();
        wizard.IsInDuel = true;
        var service = system.ActorOf(Props.Create(() => new CombatService(Session(sink))));
        try {
            await Cache(service, wizard);
            service.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE { Won = won, Fought = fought });
            await Processed(service);
            var packets = await sink.Ask<object[]>(new Snapshot(), Timeout, TestContext.Current.CancellationToken);
            var grace = Assert.IsType<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>(Assert.Single(packets));
            Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(grace.Message);
            Assert.False(wizard.IsInDuel);
            Assert.True(wizard.IsInCombatGrace);
            Assert.DoesNotContain(packets, packet => packet is EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task InvalidOpenCircleCommandStillExplainsTheRefusalWithoutCreatingAnAlert() {
        using var system = ActorSystem.Create("pvp-refusal-" + Guid.NewGuid().ToString("N"), "akka.actor.provider=local");
        var sink = system.ActorOf(Props.Create(() => new PacketSink()));
        var service = system.ActorOf(Props.Create(() => new CombatService(Session(sink))));
        try {
            service.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_PVPCOMMAND());
            await Processed(service);
            var packets = await sink.Ask<object[]>(new Snapshot(), Timeout, TestContext.Current.CancellationToken);
            var refusal = Assert.IsType<EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE>(Assert.Single(packets));
            Assert.Equal(1, refusal.Modal);
            Assert.Equal("You are not in an open PvP circle.", refusal.Message);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task ArenaSuccessNoticesStayLocalAndDeliveryFailureStillReachesThePlayerAsAPopup() {
        using var system = ActorSystem.Create("pvp-world-notice-" + Guid.NewGuid().ToString("N"), "akka.actor.provider=local");
        var received = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = system.ActorOf(Props.Create(() => new PacketSink(received)));
        OnlinePlayerCollection.SetVirtualOnlinePlayer(new OnlinePlayer {
            AccountId = Character, CharacterId = Character, ActorPath = sink.Path.ToString(),
        });
        try {
            var world = new ServerArenaWorld(system);
            world.Inform(Character, "Authored successful arena notice.");
            Assert.Empty(await sink.Ask<object[]>(new Snapshot(), Timeout, TestContext.Current.CancellationToken));
            world.InformFailure(Character, "Authored arena delivery failure.");
            await received.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            var packets = await sink.Ask<object[]>(new Snapshot(), Timeout, TestContext.Current.CancellationToken);
            var failure = Assert.IsType<EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE>(Assert.Single(packets));
            Assert.Equal(1, failure.Modal);
            Assert.Equal("Authored arena delivery failure.", failure.Message);
        }
        finally { OnlinePlayerCollection.RemoveVirtualOnlinePlayer(Character); await system.Terminate(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitPvpStatusStillReturnsItsDiagnosticWithoutCreatingAnAlert(bool enabled) {
        using var system = ActorSystem.Create("pvp-status-" + Guid.NewGuid().ToString("N"), "akka.actor.provider=local");
        var sink = system.ActorOf(Props.Create(() => new PacketSink()));
        var config = typeof(ClassicPvp).GetField("s_config", BindingFlags.Static | BindingFlags.NonPublic)!;
        var prior = config.GetValue(null);
        try {
            ClassicRuntime.Initialize(ClassicDataFixture.RealRules("dev-unrestricted"));
            config.SetValue(null, enabled
                ? OpenPvpLoader.Load(Path.Combine(ClassicDataFixture.Root, "pvp", "open-pvp-2009.yaml")) : null);
            Assert.Equal(enabled, ClassicPvp.Enabled);
            var wizard = Wizard();
            wizard.Zone = "Authored/NoCircle";
            Assert.True(new CommandPvpProtocol().Execute("status", new CommandContext { Character = wizard, SessionActor = sink }));
            var packets = await sink.Ask<object[]>(new Snapshot(), Timeout, TestContext.Current.CancellationToken);
            var diagnostic = Assert.IsType<EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE>(Assert.Single(packets));
            Assert.Equal(1, diagnostic.Modal);
            if (enabled) Assert.StartsWith("No duel circle is open here.", diagnostic.Message);
            else Assert.Equal("Open PvP is off on this server.", diagnostic.Message);
        }
        finally { config.SetValue(null, prior); await system.Terminate(); }
    }

    private static Wizard Wizard() => new() {
        CharId = Character,
        GameStats = new ServerWizGameStats(default, 1) {
            m_currentHitpoints = 30, m_baseHitpoints = 100, m_currentArenaPoints = 77, m_currentPvPCurrency = 71,
        },
    };

    private static SessionActor Session(IActorRef sink) {
        var session = (SessionActor) RuntimeHelpers.GetUninitializedObject(typeof(SessionActor));
        typeof(SessionActor).GetField("<ActorRef>k__BackingField", Private)!.SetValue(session, sink);
        return session;
    }

    private static async Task Cache(IActorRef service, Wizard wizard) {
        var identity = await Processed(service);
        typeof(MessageService).GetField("_cachedWizard", Private)!.SetValue(identity.Service, wizard);
        typeof(MessageService).GetField("_cachedWizardGameObject", Private)!.SetValue(identity.Service, wizard.GameObject);
    }

    private static Task<SERVICE_101_PROTOCOL.MSG_MESSAGESERVICEIDENTITY> Processed(IActorRef service)
        => service.Ask<SERVICE_101_PROTOCOL.MSG_MESSAGESERVICEIDENTITY>(
            new SERVICE_101_PROTOCOL.MSG_QUERYMESSAGESERVICEIDENTITY(), Timeout, TestContext.Current.CancellationToken);

    private sealed record Snapshot;
    private sealed class PacketSink : ReceiveActor {
        public PacketSink() : this(null) { }
        public PacketSink(TaskCompletionSource<object>? received) {
            var packets = new List<object>();
            Receive<Snapshot>(_ => Sender.Tell(packets.ToArray()));
            ReceiveAny(packet => { packets.Add(packet); received?.TrySetResult(packet); });
        }
    }
}
