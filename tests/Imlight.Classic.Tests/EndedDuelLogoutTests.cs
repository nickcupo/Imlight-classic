// CLASSIC (crash review 2026-10-28): a logout, dropped connection or phase timer that reaches a duel which already ended
// does nothing.
// A wizard who walked into a fight in its last round was never told it ended (no win, defeat or release: not yet added
// to the duel) and kept it as its duel. Its later logout ran the flee on the ended duel: a defeat home with no mana,
// and a second EndDuel that reset the old creatures (by then perhaps in another fight) and reported the party lost again.
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
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class EndedDuelLogoutTests : IDisposable {
    private const ulong Sigil = 996762;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ActorSystem _system;

    public EndedDuelLogoutTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-ended-duel-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally { File.Delete(config); }
        _system = ActorSystem.Create("ended-duel-" + Guid.NewGuid().ToString("N"), "akka.actor.provider=local");
    }

    public void Dispose() {
        _system.Terminate().GetAwaiter().GetResult();
        ClassicRuntime.ResetForTests();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALogoutOrDropThatReachesAnEndedDuelDoesNothing(bool drop) {
        var ct = TestContext.Current.CancellationToken;
        var zone = _system.ActorOf(Props.Create(() => new Recorder()));
        var player = _system.ActorOf(Props.Create(() => new Recorder()));
        var host = _system.ActorOf(Props.Create(() => new EndedDuelHost(zone, player)));
        var duel = await host.Ask<CombatDuelComponent>(new GetDuel(), Timeout, ct);

        // The session forwards it to the duel it last joined, as the wizard's own actor.
        object message = drop ? new GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT() : new GAME_5_PROTOCOL.MSG_QUERY_LOGOUT();
        host.Tell(new ComponentMessage(duel, message), player);
        await host.Ask<CombatDuelComponent>(new GetDuel(), Timeout, ct); // the handler has run

        Assert.Empty(await player.Ask<object[]>(new GetMessages(), Timeout, ct));
        Assert.Empty(await zone.Ask<object[]>(new GetMessages(), Timeout, ct));
        Assert.True(duel.SubCircles[4].Occupied);
    }

    /// <summary>
    /// The pre-planning timer of a duel that ended in its 2.5 s window (a logout, a rejoin expiry) set the ended duel
    /// back to Planning, sent hands to the seats left over and started a planning timer.
    /// </summary>
    [Fact]
    public void APrePlanningTimerThatFiresAfterTheDuelEndedDoesNothing() {
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelID = Sigil, m_duelPhase = kDuelPhase.kPhase_Ended });

        CombatRegressionTests.Invoke(duel, "ReceivePlanningPhaseBegin");

        Assert.Equal(kDuelPhase.kPhase_Ended, duel.Duel.m_duelPhase);
    }

    private sealed record GetDuel;
    private sealed record GetMessages;

    private sealed class Recorder : ReceiveActor {
        private readonly List<object> _messages = [];
        public Recorder() {
            Receive<GetMessages>(_ => Sender.Tell(_messages.ToArray()));
            ReceiveAny(message => _messages.Add(message));
        }
    }

    /// <summary>A PvE sigil whose duel ended, with a wizard still seated who walked in and was never added.</summary>
    private sealed class EndedDuelHost : ZoneEntity {
        private readonly CombatDuelComponent _duel;

        public EndedDuelHost(IActorRef zone, IActorRef player)
            : base(new CoreObject { m_globalID = Sigil }, new GameObjectTemplate { m_behaviors = [] },
                new CoreObjectInfo(), zone, null!) {
            _duel = new CombatDuelComponent(this);
            _duel.AttachTo(Self);
            CombatRegressionTests.SetProperty(_duel, "Duel", new Duel {
                m_duelID = Sigil, m_duelPhase = kDuelPhase.kPhase_Ended,
                m_firstTeamToAct = (int) CombatTeam.Player, m_flatParticipantList = [],
                m_duelModifier = new DuelModifier { m_battlefieldEffects = [] },
            });
            CombatRegressionTests.SetProperty(_duel, "SubCircles", Enumerable.Range(0, 8).Select(slot =>
                new CombatDuelSubCircle(_duel, 0, 0, default, slot) {
                    SlotType = slot < 4 ? CombatSlotType.Creature : CombatSlotType.Player,
                }).ToArray());
            SetField(_duel, "_tutorialDirector", new TutorialDuelDirector(_duel, ""));
            SetField(_duel, "_combatSigilObjectInfo", new CombatSigilObjectInfo { m_zoneTag = "ended duel fixture" });

            var circle = _duel.SubCircles[4];
            CombatRegressionTests.SetProperty(circle, "ParticipantActor", player);
            CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject { m_templateID = 1, m_globalID = 204UL });
            var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
            stats.m_currentHitpoints = stats.m_baseHitpoints = 1000;
            CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
            CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant {
                m_hangingEffects = [], m_pipCount = new PipCount(), m_subcircle = 4,
            });
        }

        protected override void ConfigureReceivers() {
            Receive<GetDuel>(_ => Sender.Tell(_duel));
            base.ConfigureReceivers();
        }

        protected override void PostStop() {
            ActiveDuels.Remove(Sigil);
            base.PostStop();
        }
    }

    private static void SetField(CombatDuelComponent duel, string name, object value)
        => typeof(CombatDuelComponent).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(duel, value);
}
