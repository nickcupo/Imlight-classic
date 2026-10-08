// CLASSIC: native PvP ending phase must reach each actual released actor before its own seat removal.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaPvpReleaseTransportTests : IDisposable {
    private const ulong Sigil = 996761;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ActorSystem _system;
    private readonly IActorRef _host, _zone;
    private readonly IActorRef[] _players;

    public ArenaPvpReleaseTransportTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        var fixture = Path.Combine(Path.GetTempPath(), "imlight-pvp-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var config = Path.Combine(fixture, "settings.ini");
        File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(fixture, "server.log")}\n");
        ConfigurationManager.Initialize(config);
        _system = ActorSystem.Create("pvp-release-transport-" + Guid.NewGuid().ToString("N"), "akka.actor.provider=local");
        _players = Enumerable.Range(0, 5).Select(_ => _system.ActorOf(Props.Create(() => new Recorder()))).ToArray();
        _zone = _system.ActorOf(Props.Create(() => new ZoneRecorder(_players)));
        _host = _system.ActorOf(Props.Create(() => new DuelHost(_zone)));
    }

    public void Dispose() {
        _system.Terminate().GetAwaiter().GetResult();
        ClassicRuntime.ResetForTests();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task LastWizardConcedeDeliversNativeEndingBeforeOwnRemovalAndOneSharedResult(int losingSlot) {
        await Run(duel => {
            Occupy(duel, 0, _players[0]);
            Occupy(duel, 4, _players[2]);
            CombatRegressionTests.Invoke(duel, "HandleFleeAction", duel.SubCircles[losingSlot]);
            Assert.Equal(kDuelPhase.kPhase_Ended, duel.Duel.m_duelPhase);
            Assert.DoesNotContain(duel.SubCircles, circle => circle.Occupied);
            return true;
        });
        await FlushZone();
        AssertEndingBeforeOwnRemoval(await Messages(_players[0]), ObjectId(0), won: losingSlot != 0);
        AssertEndingBeforeOwnRemoval(await Messages(_players[2]), ObjectId(4), won: losingSlot != 4);
        var native = (await Messages(_zone)).OfType<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>().Select(m => m.Message).ToArray();
        Assert.Single(native.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT>());
        Assert.Single(native.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_ENDDUEL>());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 2)]
    public async Task TeamConcedeClosesOnlyTheLeaversHandWithoutEndingSharedPhaseOrChangingTimers(int slot, int player) {
        await Run(duel => {
            OccupyTeams(duel);
            var before = DuelHost.TimerCalls(duel);
            CombatRegressionTests.Invoke(duel, "HandleFleeAction", duel.SubCircles[slot]);
            Assert.Equal(kDuelPhase.kPhase_Planning, duel.Duel.m_duelPhase);
            Assert.Equal(3, duel.SubCircles.Count(circle => circle.Occupied));
            Assert.Equal(before, DuelHost.TimerCalls(duel));
            return true;
        });
        await FlushZone();
        AssertEndingBeforeOwnRemoval(await Messages(_players[player]), ObjectId(slot), won: false);
        for (var index = 0; index < _players.Length; index++) {
            if (index == player) continue;
            Assert.DoesNotContain(await Messages(_players[index]), message => message is DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPHASE);
        }
        var native = (await Messages(_zone)).OfType<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>().Select(m => m.Message).ToArray();
        Assert.DoesNotContain(native, message => message is DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT or DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_ENDDUEL);
    }

    [Fact]
    public async Task OrdinaryCompletedMatchSendsOnePrivateEndingToEveryJoinedWizard() {
        await Run(duel => {
            OccupyTeams(duel);
            CombatRegressionTests.Invoke(duel, "PvpEndDuel");
            Assert.Equal(kDuelPhase.kPhase_Ended, duel.Duel.m_duelPhase);
            return true;
        });
        await FlushZone();
        foreach (var (slot, index) in new[] { (0, 0), (1, 1), (4, 2), (5, 3) })
            AssertEndingBeforeOwnRemoval(await Messages(_players[index]), ObjectId(slot), won: slot < 4);
        Assert.DoesNotContain(await Messages(_players[4]), message => message is DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPHASE);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LobbyReleaseEndsOnlyAnActuallyAddedLocalCombat(bool added) {
        await Run(duel => {
            var seat = Occupy(duel, 0, _players[0]);
            seat.AddedToDuel = added;
            SetField(duel, "_pvpLobby", true);
            CombatRegressionTests.Invoke(duel, "PvpReleaseSeat", seat, false, false);
            Assert.Equal(kDuelPhase.kPhase_Planning, duel.Duel.m_duelPhase);
            return true;
        });
        await FlushZone();
        var packets = await Messages(_players[0]);
        var release = Assert.Single(packets.OfType<CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE>());
        Assert.False(release.Fought);
        Assert.Equal(added ? 1 : 0, packets.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPHASE>().Count());
        if (added) AssertEndingBeforeOwnRemoval(packets, ObjectId(0), won: false, fought: false);
    }

    [Fact]
    public async Task RepeatedReleaseAndLateFleeDoNotCloseAnotherHandOrCreateAnotherOutcome() {
        await Run(duel => {
            OccupyTeams(duel);
            var seat = duel.SubCircles[0];
            CombatRegressionTests.Invoke(duel, "PvpReleaseSeat", seat, false, true);
            CombatRegressionTests.Invoke(duel, "PvpReleaseSeat", seat, false, true);
            CombatRegressionTests.Invoke(duel, "ReceiveCombatMove", new COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE {
                Actor = _players[0], MoveType = (byte) CombatMoveType.Flee,
            });
            Assert.Equal(3, duel.SubCircles.Count(circle => circle.Occupied));
            Assert.Equal(kDuelPhase.kPhase_Planning, duel.Duel.m_duelPhase);
            return true;
        });
        await FlushZone();
        AssertEndingBeforeOwnRemoval(await Messages(_players[0]), ObjectId(0), won: false);
        foreach (var actor in _players.Skip(1)) {
            Assert.DoesNotContain(await Messages(actor), message => message is DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPHASE
                or CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE);
        }
    }

    [Fact]
    public async Task HeldDisconnectedSeatCannotSendNativeEndingToFormerSessionOrTeammates() {
        await Run(duel => {
            OccupyTeams(duel);
            var seat = duel.SubCircles[0];
            seat.HoldSeat(DateTime.UtcNow);
            CombatRegressionTests.Invoke(duel, "PvpReleaseSeat", seat, false, true);
            Assert.False(seat.Occupied);
            return true;
        });
        await FlushZone();
        foreach (var actor in _players)
            Assert.DoesNotContain(await Messages(actor), message => message is DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPHASE);
    }

    [Fact]
    public async Task UnrelatedActorFleeCannotCloseAnyOccupiedHand() {
        await Run(duel => {
            OccupyTeams(duel);
            CombatRegressionTests.Invoke(duel, "ReceiveCombatMove", new COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE {
                Actor = _players[4], MoveType = (byte) CombatMoveType.Flee,
            });
            Assert.Equal(4, duel.SubCircles.Count(circle => circle.Occupied));
            return true;
        });
        await FlushZone();
        foreach (var actor in _players) Assert.Empty(await Messages(actor));
    }

    private static void AssertEndingBeforeOwnRemoval(object[] packets, ulong ownId, bool won, bool fought = true) {
        var phase = Assert.Single(packets.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPHASE>());
        Assert.Equal(Sigil, phase.DuelID);
        Assert.Equal((byte) kDuelPhase.kPhase_Ended, phase.NewPhase);
        Assert.Equal(0UL, phase.PlayerID);
        Assert.Equal("", phase.Data.ToString());
        var release = Assert.Single(packets.OfType<CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE>());
        Assert.Equal(won, release.Won);
        Assert.Equal(fought, release.Fought);
        var remove = Assert.Single(packets.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATREMOVE>(),
            message => message.ParticipantID == ownId);
        Assert.True(Array.IndexOf(packets, phase) < Array.IndexOf(packets, release));
        Assert.True(Array.IndexOf(packets, phase) < Array.IndexOf(packets, remove));
    }

    private void OccupyTeams(CombatDuelComponent duel) {
        Occupy(duel, 0, _players[0]); Occupy(duel, 1, _players[1]);
        Occupy(duel, 4, _players[2]); Occupy(duel, 5, _players[3]);
    }

    private static ulong ObjectId(int slot) => (ulong) (slot + 200);
    private static CombatDuelSubCircle Occupy(CombatDuelComponent duel, int slot, IActorRef actor) {
        var circle = duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", actor);
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject { m_templateID = 1, m_globalID = ObjectId(slot) });
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_currentHitpoints = stats.m_baseHitpoints = 1000;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant {
            m_hangingEffects = [], m_pipCount = new PipCount(), m_subcircle = slot,
        });
        circle.AddedToDuel = true;
        return circle;
    }

    private Task<object> Run(Func<CombatDuelComponent, object> action)
        => _host.Ask<object>(new RunDuel(action), Timeout, TestContext.Current.CancellationToken);
    private Task<object[]> FlushZone() => Messages(_zone);
    private static Task<object[]> Messages(IActorRef actor)
        => actor.Ask<object[]>(new GetMessages(), Timeout, TestContext.Current.CancellationToken);
    private static void SetField(CombatDuelComponent duel, string name, object value)
        => typeof(CombatDuelComponent).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(duel, value);
    private sealed record RunDuel(Func<CombatDuelComponent, object> Action);
    private sealed record GetMessages;

    private sealed class Recorder : ReceiveActor {
        private readonly List<object> _messages = [];
        public Recorder() {
            Receive<GetMessages>(_ => Sender.Tell(_messages.ToArray()));
            ReceiveAny(message => _messages.Add(message));
        }
    }

    private sealed class ZoneRecorder : ReceiveActor {
        private readonly List<object> _messages = [];
        public ZoneRecorder(IActorRef[] recipients) {
            Receive<GetMessages>(_ => Sender.Tell(_messages.ToArray()));
            ReceiveAny(message => {
                _messages.Add(message);
                if (message is ZONE_102_PROTOCOL.MSG_ZONEBROADCAST { Message: { } packet })
                    foreach (var recipient in recipients) recipient.Tell(packet);
            });
        }
    }

    private sealed class DuelHost : ZoneEntity {
        private readonly CombatDuelComponent _duel;
        public static string[] TimerCalls(CombatDuelComponent duel) => ((PvpReleaseTimers) (object) duel.Timers).Calls.ToArray();
        public DuelHost(IActorRef zone)
            : base(new CoreObject { m_globalID = Sigil }, new GameObjectTemplate { m_behaviors = [] },
                new CoreObjectInfo(), zone, null!) {
            _duel = new CombatDuelComponent(this);
            _duel.AttachTo(Self);
            _duel.Timers = DispatchProxy.Create<ITimerScheduler, PvpReleaseTimers>();
            CombatRegressionTests.SetProperty(_duel, "Duel", new Duel {
                m_duelID = Sigil, m_bPVP = true, m_duelPhase = kDuelPhase.kPhase_Planning,
                m_firstTeamToAct = (int) CombatTeam.Player, m_flatParticipantList = [],
                m_duelModifier = new DuelModifier { m_battlefieldEffects = [] },
            });
            CombatRegressionTests.SetProperty(_duel, "SubCircles", Enumerable.Range(0, 8).Select(slot =>
                new CombatDuelSubCircle(_duel, 0, 0, default, slot) {
                    PvpTeam = slot < 4 ? CombatTeam.Monster : CombatTeam.Player,
                    SlotType = slot < 4 ? CombatSlotType.Creature : CombatSlotType.Player,
                }).ToArray());
            SetField(_duel, "_pvp", true);
            SetField(_duel, "_isActive", true);
            SetField(_duel, "_tutorialDirector", new TutorialDuelDirector(_duel, ""));
            SetField(_duel, "_combatSigilObjectInfo", new CombatSigilObjectInfo { m_zoneTag = "PvP release fixture" });
        }
        protected override void ConfigureReceivers() {
            Receive<RunDuel>(request => {
                try { Sender.Tell(request.Action(_duel)); }
                catch (Exception error) { Sender.Tell(new Status.Failure(error)); }
            });
            base.ConfigureReceivers();
        }
        protected override void PostStop() {
            ActiveDuels.Remove(Sigil);
            ClassicPvp.Forget("", "PvP release fixture");
            base.PostStop();
        }
    }
}

public class PvpReleaseTimers : DispatchProxy {
    public List<string> Calls { get; } = [];
    protected override object? Invoke(MethodInfo? method, object?[]? arguments) {
        Calls.Add(method!.Name);
        return method.ReturnType == typeof(void) ? null
            : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
