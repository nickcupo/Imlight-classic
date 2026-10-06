// CLASSIC: exercise real summons, both PvP halves, and duel endings without world data or a database.
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
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaPvpMinionTests : IDisposable {
    private const uint MinionTid = uint.MaxValue - 2299;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly ActorSystem _system;
    private readonly IActorRef _host, _zone, _side0, _side1;
    private readonly IDictionary<ulong, CoreTemplate> _cache;
    private readonly CoreTemplate? _previous;

    public ArenaPvpMinionTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        var fixture = Path.Combine(Path.GetTempPath(), "imlight-pvp-minion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var config = Path.Combine(fixture, "settings.ini");
        File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(fixture, "server.log")}\n");
        ConfigurationManager.Initialize(config); // Retained with the other recoverable fixtures.
        _cache = (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        _cache.TryGetValue(MinionTid, out _previous);
        _cache[MinionTid] = new GameObjectTemplate {
            m_templateID = MinionTid,
            m_objectName = "PvP minion fixture",
            m_behaviors = [new NPCBehaviorTemplate {
                m_behaviorName = "NPCBehavior", m_schoolOfFocus = "Myth", m_nLevel = 1,
                m_nStartingHealth = 1000, m_baseEffects = [],
            }],
        };
        _system = ActorSystem.Create("pvp-minion-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        _zone = _system.ActorOf(Props.Create(() => new Recorder()));
        _side0 = _system.ActorOf(Props.Create(() => new Recorder()));
        _side1 = _system.ActorOf(Props.Create(() => new Recorder()));
        _host = _system.ActorOf(Props.Create(() => new DuelHost(_zone)));
    }

    public void Dispose() {
        _system.Terminate().GetAwaiter().GetResult();
        if (_previous is not null) _cache[MinionTid] = _previous;
        else _cache.Remove(MinionTid);
        ClassicRuntime.ResetForTests();
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(4, 5, true)]
    [InlineData(3, 0, false)]
    [InlineData(7, 4, false)]
    public async Task ActualSummonUsesCastersPhysicalHalfAndKeepsOwnerTeamEvenWhenEnemyHalfIsFull(
        int casterSlot, int expectedSlot, bool fillEnemyHalf) {
        var result = await Run(duel => {
            var caster = Occupy(duel, casterSlot, wizard: true, _side0);
            var enemyStart = casterSlot < 4 ? 4 : 0;
            foreach (var slot in Enumerable.Range(enemyStart, fillEnemyHalf ? 4 : 1))
                Occupy(duel, slot, wizard: true, _side1);
            CombatRegressionTests.Invoke(duel, "SpawnAndAssignMinion", MinionTid, caster, false);
            var minion = Assert.Single(duel.SubCircles.Where(circle => circle.IsSummonedMinion));
            var ai = (CombatCreatureAIComponent) RuntimeHelpers.GetUninitializedObject(typeof(CombatCreatureAIComponent));
            var hate = new Dictionary<int, int>();
            typeof(CombatCreatureAIComponent).GetField("_hateTable", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(ai, hate);
            CombatRegressionTests.Invoke(ai, "ReceiveCombatAdded", new COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL {
                Duel = duel, SubCircle = minion,
            });
            return new SummonState(minion.SlotIndex, minion.OccupiedTeam, minion.IsOwnedMinionOf(caster),
                minion.CombatParticipant.m_minionSubCircle, minion.CombatParticipant.m_teamID,
                hate.Keys.OrderBy(slot => slot).ToArray());
        });
        var summon = Assert.IsType<SummonState>(result);
        var team = casterSlot < 4 ? CombatTeam.Monster : CombatTeam.Player;
        Assert.Equal(expectedSlot, summon.Slot);
        Assert.Equal(team, summon.Team);
        Assert.True(summon.Owned);
        Assert.Equal(casterSlot, summon.OwnerSlot);
        Assert.Equal((int) team, summon.SerializedTeam);
        Assert.Equal(Enumerable.Range(casterSlot < 4 ? 4 : 0, 4), summon.EnemySlots);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task ActualSummonRefusesFullOwnHalfWithoutTakingAnEmptyEnemySeat(int casterSlot) {
        await Run(duel => {
            var caster = Occupy(duel, casterSlot, wizard: true, _side0);
            var ownStart = casterSlot < 4 ? 0 : 4;
            foreach (var slot in Enumerable.Range(ownStart, 4).Where(slot => slot != casterSlot))
                Occupy(duel, slot, wizard: true, _side0);
            var enemyStart = casterSlot < 4 ? 4 : 0;
            Occupy(duel, enemyStart, wizard: true, _side1);
            var before = duel.SubCircles.Select(circle => circle.ParticipantObject).ToArray();
            CombatRegressionTests.Invoke(duel, "SpawnAndAssignMinion", MinionTid, caster, false);
            Assert.Equal(before, duel.SubCircles.Select(circle => circle.ParticipantObject).ToArray());
            Assert.DoesNotContain(duel.SubCircles, circle => circle.IsSummonedMinion);
            return true;
        });
    }

    [Theory]
    [InlineData(0, "fled")]
    [InlineData(4, "fled")]
    [InlineData(0, "vacated")]
    [InlineData(4, "vacated")]
    [InlineData(0, "replaced")]
    [InlineData(4, "replaced")]
    [InlineData(0, "actor changed")]
    [InlineData(4, "actor changed")]
    [InlineData(0, "identity changed")]
    [InlineData(4, "identity changed")]
    public async Task DeferredSummonCannotOutliveItsCasterOccupantWhileTeammateKeepsDuelActive(int casterSlot, string change) {
        await Run(duel => {
            var caster = Occupy(duel, casterSlot, wizard: true, _side0);
            Occupy(duel, casterSlot + 2, wizard: true, _side0);
            Occupy(duel, casterSlot < 4 ? 4 : 0, wizard: true, _side1);
            duel.SummonMinion(MinionTid, caster);
            var deferred = Assert.Single(((ArenaMinionTimers) duel.Timers).Messages
                .OfType<ZONE_102_PROTOCOL.MSG_DEFERREDMINIONSUMMON>());
            switch (change) {
                case "fled": CombatRegressionTests.Invoke(duel, "HandleFleeAction", caster); break;
                case "vacated": caster.RemoveParticipant(); break;
                // Same actor and ID, but a new occupant object: no transfer of the previous occupant's summon.
                case "replaced": caster.RemoveParticipant(); Occupy(duel, casterSlot, wizard: true, _side0); break;
                case "actor changed": CombatRegressionTests.SetProperty(caster, "ParticipantActor", _side1); break;
                case "identity changed": caster.ParticipantObject.m_globalID = 123456; break;
                default: throw new InvalidOperationException("unknown fixture change");
            }
            Assert.True((bool) typeof(CombatDuelComponent).GetField("_isActive", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(duel)!);
            var before = duel.SubCircles.Select(circle => circle.ParticipantObject).ToArray();
            CombatRegressionTests.Invoke(duel, "ReceiveDeferredMinionSummon", deferred);
            Assert.Equal(before, duel.SubCircles.Select(circle => circle.ParticipantObject).ToArray());
            Assert.DoesNotContain(duel.SubCircles, circle => circle.IsSummonedMinion);
            return true;
        });
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    [InlineData(4, true)]
    public async Task OccupiedOriginalCasterResolvesQueuedSummonOnceIncludingAfterItsHealthFalls(int casterSlot, bool defeated) {
        await Run(duel => {
            var caster = Occupy(duel, casterSlot, wizard: true, _side0);
            Occupy(duel, casterSlot + 2, wizard: true, _side0);
            Occupy(duel, casterSlot < 4 ? 4 : 0, wizard: true, _side1);
            duel.SummonMinion(MinionTid, caster);
            var deferred = Assert.Single(((ArenaMinionTimers) duel.Timers).Messages
                .OfType<ZONE_102_PROTOCOL.MSG_DEFERREDMINIONSUMMON>());
            if (defeated) caster.ParticipantGameStats.m_currentHitpoints = 0;
            CombatRegressionTests.Invoke(duel, "ReceiveDeferredMinionSummon", deferred);
            CombatRegressionTests.Invoke(duel, "ReceiveDeferredMinionSummon", deferred);
            var minion = Assert.Single(duel.SubCircles.Where(circle => circle.IsSummonedMinion));
            Assert.Equal(casterSlot + 1, minion.SlotIndex);
            Assert.Equal(caster.OccupiedTeam, minion.OccupiedTeam);
            Assert.Equal(casterSlot, minion.CombatParticipant.m_minionSubCircle);
            return true;
        });
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    [InlineData(4, true)]
    public async Task AlreadyCastSummonSurvivesRealHeldSeatAndAuthenticatedSameCharacterRejoin(int casterSlot, bool rejoin) {
        await Run(duel => {
            var caster = Occupy(duel, casterSlot, wizard: true, _side0);
            caster._wizard = WizardForCharacter(42, caster.ParticipantGameStats);
            duel.SummonMinion(MinionTid, caster);
            var deferred = Assert.Single(((ArenaMinionTimers) duel.Timers).Messages
                .OfType<ZONE_102_PROTOCOL.MSG_DEFERREDMINIONSUMMON>());
            caster.HoldSeat(DateTime.UtcNow);
            Assert.True(caster.Disconnected);
            Assert.Equal(ActorRefs.Nobody, caster.ParticipantActor);
            if (rejoin) caster.RejoinSeat(_side1, new CoreObject { m_templateID = 1, m_globalID = 123456 },
                WizardForCharacter(42, caster.ParticipantGameStats));
            CombatRegressionTests.Invoke(duel, "ReceiveDeferredMinionSummon", deferred);
            var minion = Assert.Single(duel.SubCircles.Where(circle => circle.IsSummonedMinion));
            Assert.Equal(casterSlot + 1, minion.SlotIndex);
            Assert.True(minion.IsOwnedMinionOf(caster));
            Assert.Equal(caster.OccupiedTeam, minion.OccupiedTeam);
            return true;
        });
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("global ID")]
    [InlineData("object")]
    [InlineData("character")]
    public async Task AuthenticatedRejoinDoesNotAuthorizeLaterArbitraryIdentityChanges(string change) {
        await Run(duel => {
            var caster = Occupy(duel, 0, wizard: true, _side0);
            caster._wizard = WizardForCharacter(42, caster.ParticipantGameStats);
            duel.SummonMinion(MinionTid, caster);
            var deferred = Assert.Single(((ArenaMinionTimers) duel.Timers).Messages
                .OfType<ZONE_102_PROTOCOL.MSG_DEFERREDMINIONSUMMON>());
            caster.HoldSeat(DateTime.UtcNow);
            caster.RejoinSeat(_side1, new CoreObject { m_templateID = 1, m_globalID = 123456 },
                WizardForCharacter(42, caster.ParticipantGameStats));
            switch (change) {
                case "actor": CombatRegressionTests.SetProperty(caster, "ParticipantActor", _zone); break;
                case "global ID": caster.ParticipantObject.m_globalID = 654321; break;
                case "object": CombatRegressionTests.SetProperty(caster, "ParticipantObject",
                    new CoreObject { m_templateID = 1, m_globalID = 123456 }); break;
                case "character": caster._wizard.CharId = 43; break;
                default: throw new InvalidOperationException("unknown fixture change");
            }
            CombatRegressionTests.Invoke(duel, "ReceiveDeferredMinionSummon", deferred);
            Assert.DoesNotContain(duel.SubCircles, circle => circle.IsSummonedMinion);
            return true;
        });
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    [InlineData(4, true)]
    public async Task LastWizardDefeatOrFleeEndsEitherSideDespiteLivingMinionAndDeliversOneResult(
        int losingSlot, bool flee) {
        await Run(duel => {
            var side0 = Occupy(duel, 0, wizard: true, _side0);
            var side1 = Occupy(duel, 4, wizard: true, _side1);
            var loser = duel.SubCircles[losingSlot];
            var minion = Occupy(duel, losingSlot + 1, wizard: false, _zone, loser);
            if (flee) CombatRegressionTests.Invoke(duel, "HandleFleeAction", loser);
            else {
                loser.ParticipantGameStats.m_currentHitpoints = 0;
                Assert.Equal(losingSlot == 0 ? 0 : 1, duel.AliveCreatureCount);
                Assert.Equal(losingSlot == 0 ? 0 : 1, duel.AliveAndInDuelCreatureCount);
                Assert.Equal(losingSlot == 4 ? 0 : 1, duel.AlivePlayerCount);
                Assert.Equal(losingSlot == 4 ? 0 : 1, duel.AliveAndInDuelPlayerCount);
                CombatRegressionTests.Invoke(duel, "ReceiveRoundResolution", new COMBAT_106_PROTOCOL.MSG_ROUNDRESOLUTION());
            }
            Assert.True(minion.IsAlive); // A living summon must not keep its defeated owner's duel open.
            Assert.Equal(kDuelPhase.kPhase_Ended, duel.Duel.m_duelPhase);
            Assert.False(side0.Occupied);
            Assert.False(side1.Occupied);
            // A delayed resolution after the ending must not deliver a second match result.
            CombatRegressionTests.Invoke(duel, "ReceiveRoundResolution", new COMBAT_106_PROTOCOL.MSG_ROUNDRESOLUTION());
            CombatRegressionTests.Invoke(duel, "ReceiveRoundResolution", new COMBAT_106_PROTOCOL.MSG_ROUNDRESOLUTION());
            return true;
        });
        var broadcasts = (await Messages(_zone)).OfType<ZONE_102_PROTOCOL.MSG_ZONEBROADCAST>().Select(m => m.Message).ToArray();
        var result = Assert.Single(broadcasts.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT>());
        Assert.Equal((byte) (losingSlot == 0 ? CombatTeam.Player : CombatTeam.Monster), result.WinningTeam);
        Assert.Single(broadcasts.OfType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_ENDDUEL>());
        var release0 = Assert.Single((await Messages(_side0)).OfType<CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE>());
        var release1 = Assert.Single((await Messages(_side1)).OfType<CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE>());
        Assert.Equal(losingSlot != 0, release0.Won);
        Assert.Equal(losingSlot != 4, release1.Won);
        Assert.True(release0.Fought);
        Assert.True(release1.Fought);
        Assert.Single((await Messages(_zone)).OfType<COMBAT_106_PROTOCOL.MSG_COMBATDEATH>());
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 5)]
    public async Task PveSummonRetainsExistingPlayerHalfSelection(int casterSlot, int expectedSlot) {
        await Run(duel => {
            SetField(duel, "_pvp", false);
            foreach (var circle in duel.SubCircles) circle.PvpTeam = null;
            var caster = Occupy(duel, casterSlot, wizard: casterSlot >= 4, _side0);
            CombatRegressionTests.Invoke(duel, "SpawnAndAssignMinion", MinionTid, caster, false);
            var minion = Assert.Single(duel.SubCircles.Where(circle => circle.IsSummonedMinion));
            Assert.Equal(expectedSlot, minion.SlotIndex);
            Assert.Equal(caster.OccupiedTeam, minion.OccupiedTeam);
            Assert.Equal(casterSlot, minion.CombatParticipant.m_minionSubCircle);
            return true;
        });
    }

    [Fact]
    public async Task PveLivingMonsterSummonStillCountsWhenItsBossFallsAndPlayerSummonDoesNot() {
        await Run(duel => {
            SetField(duel, "_pvp", false);
            foreach (var circle in duel.SubCircles) circle.PvpTeam = null;
            var boss = Occupy(duel, 0, wizard: false, _side0);
            var player = Occupy(duel, 4, wizard: true, _side1);
            Occupy(duel, 1, wizard: false, _zone, boss);
            Occupy(duel, 5, wizard: false, _zone, player);
            boss.ParticipantGameStats.m_currentHitpoints = 0;
            player.ParticipantGameStats.m_currentHitpoints = 0;
            Assert.Equal(1, duel.AliveCreatureCount);
            Assert.Equal(1, duel.AliveAndInDuelCreatureCount);
            Assert.Equal(0, duel.AlivePlayerCount);
            Assert.Equal(0, duel.AliveAndInDuelPlayerCount);
            return true;
        });
    }

    private Task<object> Run(Func<CombatDuelComponent, object> action)
        => _host.Ask<object>(new RunDuel(action), Timeout, TestContext.Current.CancellationToken);
    private static Task<object[]> Messages(IActorRef actor)
        => actor.Ask<object[]>(new GetMessages(), Timeout, TestContext.Current.CancellationToken);
    private static void SetField(CombatDuelComponent duel, string name, object value)
        => typeof(CombatDuelComponent).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(duel, value);

    private static Wizard WizardForCharacter(ulong characterId, ServerWizGameStats stats) {
        var wizard = (Wizard) RuntimeHelpers.GetUninitializedObject(typeof(Wizard));
        wizard.GameObject = new WizClientObject();
        wizard.CharId = characterId;
        wizard.GameStats = stats;
        return wizard;
    }

    private static CombatDuelSubCircle Occupy(CombatDuelComponent duel, int slot, bool wizard, IActorRef actor,
        CombatDuelSubCircle? owner = null) {
        var circle = duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject {
            m_templateID = wizard ? 1UL : MinionTid, m_globalID = (ulong) (slot + 100),
        });
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", actor);
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = stats.m_currentHitpoints = 1000;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant {
            m_hangingEffects = [], m_pipCount = new PipCount(), m_subcircle = slot,
            m_isMinion = owner is not null, m_minionSubCircle = owner?.SlotIndex ?? 0,
        });
        CombatRegressionTests.SetProperty(circle, "IsSummonedMinion", owner is not null);
        if (owner is not null) circle.CaptureMinionOwner(owner.SlotIndex);
        circle.AddedToDuel = true;
        return circle;
    }

    private sealed record SummonState(int Slot, CombatTeam Team, bool Owned, int OwnerSlot, int SerializedTeam, int[] EnemySlots);
    private sealed record RunDuel(Func<CombatDuelComponent, object> Action);
    private sealed record GetMessages;
    private sealed class Recorder : ReceiveActor {
        private readonly List<object> _messages = [];
        public Recorder() {
            Receive<GetMessages>(_ => Sender.Tell(_messages.ToArray()));
            ReceiveAny(message => _messages.Add(message));
        }
    }
    private sealed class DuelHost : ZoneEntity {
        private readonly CombatDuelComponent _duel;
        public DuelHost(IActorRef zone)
            : base(new CoreObject { m_globalID = 999 }, new GameObjectTemplate { m_behaviors = [] },
                new CoreObjectInfo(), zone, null!) {
            _duel = new CombatDuelComponent(this);
            _duel.AttachTo(Self);
            _duel.Timers = DispatchProxy.Create<ITimerScheduler, ArenaMinionTimers>();
            CombatRegressionTests.SetProperty(_duel, "Duel", new Duel {
                m_duelID = 999, m_bPVP = true, m_firstTeamToAct = (int) CombatTeam.Player,
                m_duelPhase = kDuelPhase.kPhase_Execution, m_flatParticipantList = [],
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
            SetField(_duel, "_combatSigilObjectInfo", new CombatSigilObjectInfo { m_zoneTag = "PvP minion fixture" });
        }
        protected override void ConfigureReceivers() {
            Receive<RunDuel>(request => {
                try { Sender.Tell(request.Action(_duel)); }
                catch (Exception error) { Sender.Tell(new Status.Failure(error)); }
            });
            base.ConfigureReceivers();
        }
        protected override void PostStop() {
            ActiveDuels.Remove(999);
            ClassicPvp.Forget("", "PvP minion fixture");
            base.PostStop();
        }
    }
}

public class ArenaMinionTimers : DispatchProxy {
    public List<object> Messages { get; } = [];
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
        if (targetMethod!.Name == "StartSingleTimer")
            Messages.AddRange(args!.OfType<ZONE_102_PROTOCOL.MSG_DEFERREDMINIONSUMMON>());
        return targetMethod.ReturnType == typeof(void) ? null
            : targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }
}
