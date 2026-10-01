using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Akka.Actor;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;
using static Imlight.CoreLib.Shared.Packets.COMBAT_106_PROTOCOL;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class OwnedMinionDuelTests : IDisposable {
    private const uint Tid = uint.MaxValue - 999;
    private readonly IDictionary<ulong, CoreTemplate> _cache;
    private readonly ActorSystem _system;
    private readonly CombatDuelComponent _duel;
    private readonly CombatDuelSubCircle _owner, _minion, _enemy;
    private readonly IActorRef _ownerActor;
    private readonly Spell _spell;
    private readonly RecordingMinionTimers _timers;

    public OwnedMinionDuelTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        Settings(true);
        _cache = (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        _cache.Add(Tid, new SpellTemplate {
            m_name = "owned-minion-fixture", m_sMagicSchoolName = "Myth", m_spellRank = new SpellRank { m_spellRank = 2 },
            m_effects = [new SpellEffect { m_effectType = kSpellEffects.kDamage, m_effectTarget = kEffectTarget.kEnemySingle,
                m_sDamageType = "Myth", m_effectParam = 100 }],
        });
        _system = ActorSystem.Create("owned-minion-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        _ownerActor = _system.ActorOf(Props.Create(() => new Sink()));
        _duel = CombatRegressionTests.MakeDuel();
        var entity = (ZoneEntity) RuntimeHelpers.GetUninitializedObject(typeof(ZoneEntity));
        CombatRegressionTests.SetProperty(entity, "ActiveGameObject", new CoreObject { m_globalID = 123 });
        CombatRegressionTests.SetProperty(entity, "ZoneRef", ActorRefs.Nobody);
        typeof(ZoneEntityComponent).GetProperty("Entity", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_duel, entity);
        CombatRegressionTests.SetProperty(_duel, "Duel", new Duel {
            m_duelID = 123, m_roundNum = 2, m_duelPhase = kDuelPhase.kPhase_Planning,
            m_duelModifier = new DuelModifier { m_battlefieldEffects = [] },
        });
        CombatRegressionTests.SetProperty(_duel, "CombatResolver", new Imlight.CoreLib.Game.Combat.CombatResolver(_duel.Duel, _duel.SubCircles));
        _duel.CombatResolver.Reset();
        Field("_isActive", true); Field("_awaitingCombatMoves", true);
        Field("_tutorialDirector", new TutorialDuelDirector(_duel, ""));
        Field("_ownedMinionPlanningDeadline", DateTime.UtcNow.AddSeconds(30));
        var proxy = DispatchProxy.Create<ITimerScheduler, RecordingMinionTimers>();
        _timers = (RecordingMinionTimers) proxy;
        _duel.Timers = proxy;
        _owner = Occupy(4, true, 40); CombatRegressionTests.SetProperty(_owner, "ParticipantActor", _ownerActor);
        _owner._wizard = (Wizard) RuntimeHelpers.GetUninitializedObject(typeof(Wizard));
        _owner._wizard.MagicSchoolBehavior = new ServerMagicSchoolBehavior { MagicSchool = MagicSchool.Myth };
        _minion = Occupy(5, false, 50, _owner); _enemy = Occupy(0, false, 10);
        _minion._combatDeck = new CombatDeck([], [], 7);
        _spell = new Spell { m_templateID = Tid, m_magicSchoolID = (uint) MagicSchool.Myth, m_pipCost = new SpellRank { m_spellRank = 2 } };
        _minion._combatDeck.AddCardToHand(_spell);
        _duel.RegisterOwnedMinionForControl(_minion, _owner);
    }

    public void Dispose() {
        _cache.Remove(Tid); _system.Terminate().GetAwaiter().GetResult();
        ClassicRuntime.ResetForTests(); Settings(true);
    }

    [Theory]
    [InlineData(MagicSchool.Fire)] [InlineData(MagicSchool.Ice)] [InlineData(MagicSchool.Life)]
    [InlineData(MagicSchool.Death)] [InlineData(MagicSchool.Storm)] [InlineData(MagicSchool.Balance)]
    public void NonMythOwnersCannotOptInEvenForOwnedMonstrologyStyleSummon(MagicSchool school) {
        _owner._wizard.MagicSchoolBehavior.MagicSchool = school;
        Assert.Equal(OwnedMinionStatus.NotMyth, Request(1, query: true).Status);
        Assert.Equal(OwnedMinionStatus.NotMyth, Request(2).Status);
        Assert.True(_duel.HaveAllOwnedMinionOrders());
    }

    [Fact]
    public void MythControlsOwnedOtherSchoolSummonButNotHiredOrAlliedSummon() {
        _minion.CombatParticipant.m_primaryMagicSchoolID = (int) MagicSchool.Fire;
        var query = Request(1, query: true); Assert.True(query.Accepted);
        Assert.Equal(50UL, Assert.Single(query.Snapshots).MinionID);
        var ally = Occupy(6, true, 60); var otherMinion = Occupy(7, false, 70, ally);
        otherMinion._combatDeck = new CombatDeck([], [], 7); _duel.RegisterOwnedMinionForControl(otherMinion, ally);
        Assert.Equal(OwnedMinionStatus.NotOwnedMinion, Request(2, minionId: 70).Status);
        var unregistered = Occupy(6, false, 60, _owner); unregistered._combatDeck = new CombatDeck([], [], 7);
        Assert.Equal(OwnedMinionStatus.UnsupportedSummon, Request(3, minionId: 60).Status);
    }

    [Fact]
    public void CurrentHandSnapshotDoesNotDrawRefillSpendOrChangePips() {
        var count = _minion.TotalSpells; var hand = _minion._combatDeck.LastGivenHand.ToArray();
        var result = Request(1, query: true); Assert.True(result.Accepted);
        var snapshot = Assert.Single(result.Snapshots);
        Assert.NotEmpty(snapshot.HandData); Assert.NotEmpty(snapshot.ParticipantData);
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        Assert.True(serializer.Deserialize<Hand>(snapshot.HandData, (PropertyFlags) 5, out var decoded));
        Assert.Equal(Tid, Assert.IsType<Hand>(decoded).m_spellList.Single().m_templateID);
        Assert.Equal(count, _minion.TotalSpells); Assert.Equal(hand, _minion._combatDeck.LastGivenHand);
        Assert.Equal(7, snapshot.GenericPips);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(new { result.DuelID, result.Round, result.MinionID, result.RequestID, result.Accepted, Status = result.Status.ToString(),
            Snapshots = result.Snapshots.Select(snapshot => new { snapshot.OwnerID, snapshot.MinionID, snapshot.Slot, snapshot.Team, snapshot.Health,
                snapshot.GenericPips, snapshot.PowerPips, snapshot.HandData, snapshot.ParticipantData, snapshot.HasOrder, snapshot.MoveType, snapshot.SpellSelection, snapshot.SpellTarget }) }).Length < 24000);
    }

    [Fact]
    public void AcceptedOrderOverridesAiAndChangeMindRestoresOriginalFallbackAndDeadline() {
        QueueAllPasses(); var originalFallback = _duel.CombatResolver.GetQueuedAction(_minion);
        Assert.True(Request(1, query: true).Accepted); Assert.False(_duel.HaveAllOwnedMinionOrders());
        var accepted = Request(2); Assert.True(accepted.Accepted); Assert.True(_duel.HaveAllOwnedMinionOrders());
        Assert.Same(_spell, _duel.CombatResolver.GetQueuedAction(_minion).Spell);
        CombatRegressionTests.Invoke(_duel, "ReceiveCombatMove", new MSG_ACTORCOMBATMOVE {
            Actor = _minion.ParticipantActor, MoveType = (byte) CombatMoveType.Pass,
        });
        Assert.Same(_spell, _duel.CombatResolver.GetQueuedAction(_minion).Spell);
        Assert.Equal(OwnedMinionStatus.RequestReplay, Request(2, move: CombatMoveType.Pass).Status);
        var changed = Request(3, move: CombatMoveType.ChangeMind); Assert.True(changed.Accepted);
        Assert.False(Assert.Single(changed.Snapshots).HasOrder); Assert.False(_duel.HaveAllOwnedMinionOrders());
        Assert.Equal(originalFallback.Spell, _duel.CombatResolver.GetQueuedAction(_minion).Spell);
        Assert.True(_timers.Delays.Last() > TimeSpan.FromSeconds(20));
        Assert.Equal(7, _minion.CombatParticipant.m_pipCount.m_genericPips);
        Assert.Single(_minion._combatDeck.LastGivenHand);
    }

    [Fact]
    public void AcceptedManualAttackUsesExistingStunOrderingAndDoesNotSpendAnUncastCard() {
        QueueAllPasses(); Assert.True(Request(1, query: true).Accepted); Assert.True(Request(2).Accepted);
        _minion.CombatParticipant.m_stunned = 1;
        var before = _minion._combatDeck.LastGivenHand.ToArray();
        _duel.CombatResolver.ApplyQueuedCombatActions(out var actions);
        Assert.Equal(new[] { 4, 5, 0 }, actions.m_actionList.Select(action => action.m_spellCaster));
        Assert.Equal(0, _minion.CombatParticipant.m_stunned);
        Assert.Null(actions.m_actionList.Single(action => action.m_spellCaster == 5).m_spell);
        Assert.Equal(before, _minion._combatDeck.LastGivenHand);
        Assert.Equal(7, _minion.CombatParticipant.m_pipCount.m_genericPips);
        Assert.Equal(1000, _enemy.ParticipantGameStats.m_currentHitpoints);
    }

    [Fact]
    public void InvalidRequestsNeverReplaceFallbackOrAcceptedOrder() {
        QueueAllPasses(); Assert.True(Request(1, query: true).Accepted);
        Assert.Equal(OwnedMinionStatus.InvalidTarget, Request(2, target: 4).Status);
        Assert.Equal(OwnedMinionStatus.InvalidTarget, Request(2, target: uint.MaxValue).Status);
        Assert.Equal(OwnedMinionStatus.InvalidTarget, Request(2, target: 2).Status);
        Assert.Equal(OwnedMinionStatus.InvalidTarget, Request(2, target: 999).Status);
        Assert.Null(_duel.CombatResolver.GetQueuedAction(_minion).Spell);
        Assert.Equal(OwnedMinionStatus.InvalidCard, Request(2, selection: 7).Status);
        _minion.CombatParticipant.m_pipCount.m_genericPips = 0;
        Assert.Equal(OwnedMinionStatus.InsufficientPips, Request(2).Status);
        _minion.CombatParticipant.m_pipCount.m_genericPips = 7;
        Assert.True(Request(2).Accepted);
        Assert.Equal(OwnedMinionStatus.InvalidRound, Request(3, round: 1).Status);
        Assert.Equal(OwnedMinionStatus.InvalidDuel, Request(3, duelId: 124).Status);
        Assert.Equal(OwnedMinionStatus.InvalidMove, Request(3, move: CombatMoveType.Discard).Status);
        Assert.Same(_spell, _duel.CombatResolver.GetQueuedAction(_minion).Spell);
    }

    [Fact]
    public void DisableRestoresAiUnblocksPlanningAndStrictSettingCannotBeBypassed() {
        QueueAllPasses(); Assert.True(Request(1, query: true).Accepted); Assert.True(Request(2).Accepted);
        CombatRegressionTests.Invoke(_duel, "ReceiveOwnedMinionDisable", new MSG_OWNEDMINIONDISABLE { OwnerActor = _ownerActor });
        Assert.Null(_duel.CombatResolver.GetQueuedAction(_minion).Spell); Assert.True(_duel.HaveAllOwnedMinionOrders());
        Assert.Equal(OwnedMinionStatus.NotOptedIn, Request(3).Status);
        Settings(false); Assert.Equal(OwnedMinionStatus.Disabled, Request(4, query: true).Status);
        Assert.False(EnhancedGameplaySettings.Enabled);
    }

    [Fact]
    public void DeathAndReplacementInvalidatePendingManualState() {
        QueueAllPasses(); Assert.True(Request(1, query: true).Accepted); Assert.True(Request(2).Accepted);
        _owner.ParticipantGameStats.m_currentHitpoints = 0;
        CombatRegressionTests.Invoke(_duel, "PrepareOwnedMinionExecution");
        Assert.Null(_duel.CombatResolver.GetQueuedAction(_minion).Spell);
        Assert.Equal(OwnedMinionStatus.InvalidOwner, Request(3).Status);
        _owner.ParticipantGameStats.m_currentHitpoints = 1000;
        _minion.ParticipantGameStats.m_currentHitpoints = 0;
        Assert.Equal(OwnedMinionStatus.NotOwnedMinion, Request(3).Status);
        Assert.Empty(Request(3, query: true).Snapshots);
        _minion.ParticipantGameStats.m_currentHitpoints = 1000;
        CombatDuelComponent.OnMinionRemoved(_minion);
        var replacement = Occupy(5, false, 51, _owner); replacement._combatDeck = new CombatDeck([], [], 7);
        _duel.RegisterOwnedMinionForControl(replacement, _owner);
        var snapshot = Assert.Single(Request(4, query: true).Snapshots); Assert.False(snapshot.HasOrder);
        Assert.Equal(OwnedMinionStatus.NotOwnedMinion, Request(5, minionId: 50).Status);
        Assert.False(_duel.HaveAllOwnedMinionOrders());
    }

    [Fact]
    public void OptInDuringCompletionGraceReopensRemainingPlanningAndWaitsForAllMinions() {
        QueueAllPasses();
        CombatRegressionTests.Invoke(_duel, "ReevaluateOwnedMinionPlanning");
        Assert.Equal(TimeSpan.FromSeconds(1), _timers.Delays.Last());
        Assert.True(Request(1, query: true).Accepted);
        Assert.True(_timers.Delays.Last() > TimeSpan.FromSeconds(20));
        var second = Occupy(6, false, 60, _owner); second._combatDeck = new CombatDeck([], [], 7);
        _duel.RegisterOwnedMinionForControl(second, _owner);
        _duel.CombatResolver.AddCombatMove(CombatMoveType.Pass, second, null!, null!);
        Assert.True(Request(2, move: CombatMoveType.Pass).Accepted);
        Assert.False(_duel.HaveAllOwnedMinionOrders());
        Assert.True(Request(3, move: CombatMoveType.Pass, minionId: 60).Accepted);
        Assert.True(_duel.HaveAllOwnedMinionOrders());
        Assert.Equal(TimeSpan.FromSeconds(1), _timers.Delays.Last());
    }

    [Fact]
    public void UnorderedMinionKeepsAiFallbackAtTimeoutAndDeadEnemyDoesNotGetRetargeted() {
        QueueAllPasses(); var fallback = _duel.CombatResolver.GetQueuedAction(_minion);
        Assert.True(Request(1, query: true).Accepted);
        CombatRegressionTests.Invoke(_duel, "PrepareOwnedMinionExecution");
        Assert.Same(fallback, _duel.CombatResolver.GetQueuedAction(_minion));
        Assert.True(Request(2).Accepted);
        _enemy.ParticipantGameStats.m_currentHitpoints = 0;
        Assert.Equal(OwnedMinionStatus.InvalidTarget, Request(3).Status);
        CombatRegressionTests.Invoke(_duel, "PrepareOwnedMinionExecution");
        Assert.Null(_duel.CombatResolver.GetQueuedAction(_minion).Spell);
        Assert.Null(_duel.CombatResolver.GetQueuedAction(_minion).SelectedTarget);
    }

    [Fact]
    public void OversizedHandCannotOptInAndTimerRetainsAiWhenNoManualOrder() {
        var template = (SpellTemplate) _cache[Tid]; template.m_name = new string('x', 30000);
        for (var i = 0; i < 300; i++) _minion._combatDeck.LastGivenHand.Add(_spell);
        var result = Request(1, query: true); Assert.Equal(OwnedMinionStatus.SnapshotUnavailable, result.Status);
        Assert.False(result.Accepted); Assert.Empty(result.Snapshots); Assert.True(_duel.HaveAllOwnedMinionOrders());
    }

    [Fact]
    public void AcceptedAttackCannotFollowReplacementIntoTheSameEnemySlot() {
        QueueAllPasses();
        Assert.True(Request(1, query: true).Accepted);
        Assert.True(Request(2).Accepted);
        var identity = _enemy.ParticipantObject;
        _enemy.RemoveParticipant();
        var replacement = Occupy(0, false, 11);
        Assert.NotSame(identity, replacement.ParticipantObject);
        CombatRegressionTests.Invoke(_duel, "PrepareOwnedMinionExecution");
        Assert.Null(_duel.CombatResolver.GetQueuedAction(_minion).Spell);
        Assert.False(_duel.HaveAllOwnedMinionOrders());
        // A fresh explicit selection of the new occupant is still valid.
        Assert.True(Request(3).Accepted);
        Assert.Same(replacement, _duel.CombatResolver.GetQueuedAction(_minion).SelectedTarget);
    }

    [Fact]
    public void AcceptedAttackKeepsItsOriginalLiveTargetIdentity() {
        QueueAllPasses();
        Assert.True(Request(1, query: true).Accepted);
        Assert.True(Request(2).Accepted);
        CombatRegressionTests.Invoke(_duel, "PrepareOwnedMinionExecution");
        Assert.Same(_spell, _duel.CombatResolver.GetQueuedAction(_minion).Spell);
        Assert.Same(_enemy, _duel.CombatResolver.GetQueuedAction(_minion).SelectedTarget);
        Assert.True(_duel.HaveAllOwnedMinionOrders());
    }

    private void QueueAllPasses() {
        foreach (var circle in new[] { _owner, _minion, _enemy })
            _duel.CombatResolver.AddCombatMove(CombatMoveType.Pass, circle, null!, null!);
    }

    private MSG_OWNEDMINIONRESPONSE Request(uint id, bool query = false, CombatMoveType move = CombatMoveType.Attack,
        uint target = 0, byte selection = 0, int round = 2, ulong duelId = 123, ulong? minionId = null)
        => _duel.ProcessOwnedMinionRequest(new MSG_OWNEDMINIONREQUEST {
            OwnerActor = _ownerActor, DuelID = duelId, Round = round, RequestID = id,
            MinionID = minionId ?? (query ? 0UL : 50UL), Query = query,
            MoveType = (byte) move, SpellTarget = target, SpellSelection = selection,
        });

    private CombatDuelSubCircle Occupy(int slot, bool player, ulong id, CombatDuelSubCircle? owner = null) {
        var circle = _duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject { m_globalID = id, m_templateID = player ? 1UL : 2UL });
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", _system.ActorOf(Props.Create(() => new Sink())));
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_currentHitpoints = stats.m_baseHitpoints = 1000; stats.m_schoolID = (uint) MagicSchool.Myth;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant {
            m_isPlayer = player, m_isMinion = owner is not null, m_hangingEffects = [], m_pipCount = new PipCount { m_genericPips = 7 },
            m_subcircle = slot, m_playerHealth = 1000, m_maxPlayerHealth = 1000,
        });
        CombatRegressionTests.SetProperty(circle, "IsSummonedMinion", owner is not null);
        if (owner is not null) circle.CaptureMinionOwner(owner.SlotIndex);
        circle.AddedToDuel = true; return circle;
    }

    private void Field(string name, object value)
        => typeof(CombatDuelComponent).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_duel, value);
    private static void Settings(bool enabled) {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}imlight-owned-minion-tests.log\n[Classic]\nOwnedMinionControl={enabled}\n");
            ConfigurationManager.Initialize(path);
        } finally { File.Delete(path); }
    }
    private sealed class Sink : ReceiveActor { public Sink() { ReceiveAny(_ => { }); } }
}

public class RecordingMinionTimers : DispatchProxy {
    public List<TimeSpan> Delays { get; } = [];
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
        if (targetMethod!.Name == "StartSingleTimer") Delays.Add(args!.OfType<TimeSpan>().Single());
        return targetMethod.ReturnType == typeof(void) ? null
            : targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }
}
