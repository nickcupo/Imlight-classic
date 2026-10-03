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
    public void OversizedHandStillOptsInWithoutTheOpaqueBlobs() {
        // CLASSIC: an oversized participant used to switch control off for the whole duel; now only the service-90
        // blobs are dropped and the Minion Helper keeps working from HelperView.
        var template = (SpellTemplate) _cache[Tid]; template.m_name = new string('x', 30000);
        for (var i = 0; i < 300; i++) _minion._combatDeck.LastGivenHand.Add(_spell);
        var result = Request(1, query: true); Assert.Equal(OwnedMinionStatus.Accepted, result.Status);
        Assert.True(result.Accepted);
        var snapshot = Assert.Single(result.Snapshots);
        Assert.Empty(snapshot.HandData); Assert.Empty(snapshot.ParticipantData);
        Assert.False(_duel.HaveAllOwnedMinionOrders());
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

    // CLASSIC: Minion Helper (Classic/MinionHelper) behaviour.

    [Fact]
    public void ChangingAnOrderDoesNotRestartThePlanningCountdown() {
        Field("_ownedMinionPlanningDeadline", DateTime.UtcNow.AddSeconds(5));
        QueueAllPasses(); Assert.True(Request(1, query: true).Accepted); Assert.True(Request(2).Accepted);
        var before = _timers.Delays.Count;
        Assert.True(Request(3, move: CombatMoveType.ChangeMind).Accepted);
        Assert.All(_timers.Delays.Skip(before), delay => Assert.True(delay <= TimeSpan.FromSeconds(6), $"timer restarted at {delay}"));
    }

    [Fact]
    public void LettingTheAiChooseCountsAsTheOrderAndKeepsTheAiMove() {
        QueueAllPasses(); var fallback = _duel.CombatResolver.GetQueuedAction(_minion);
        Assert.True(Request(1, query: true).Accepted); Assert.False(_duel.HaveAllOwnedMinionOrders());
        var ai = _duel.ProcessOwnedMinionRequest(new MSG_OWNEDMINIONREQUEST {
            OwnerActor = _ownerActor, DuelID = 123, Round = 2, RequestID = 2, MinionID = 50, MoveType = OwnedMinionAiMove,
            SpellTarget = uint.MaxValue });
        Assert.True(ai.Accepted);
        Assert.True(_duel.HaveAllOwnedMinionOrders());
        Assert.Equal(fallback.Spell, _duel.CombatResolver.GetQueuedAction(_minion).Spell);
        CombatRegressionTests.Invoke(_duel, "PrepareOwnedMinionExecution");
        Assert.True(_duel.HaveAllOwnedMinionOrders());
    }

    [Fact]
    public void AnAiMoveHeldBehindTheOwnersOrderComesBackWhenTheOwnerHandsTheRoundBack() {
        foreach (var circle in new[] { _owner, _enemy }) _duel.CombatResolver.AddCombatMove(CombatMoveType.Pass, circle, null!, null!);
        Assert.True(Request(1, query: true).Accepted); Assert.True(Request(2).Accepted);
        // The minion's AI moves after the owner's order: it is held, not queued.
        CombatRegressionTests.Invoke(_duel, "ReceiveCombatMove", new MSG_ACTORCOMBATMOVE {
            Actor = _minion.ParticipantActor, MoveType = (byte) CombatMoveType.Attack, SpellSelection = 0, SpellTarget = 0 });
        Assert.Same(_spell, _duel.CombatResolver.GetQueuedAction(_minion).Spell);
        Assert.Same(_enemy, _duel.CombatResolver.GetQueuedAction(_minion).SelectedTarget);
        var ai = _duel.ProcessOwnedMinionRequest(new MSG_OWNEDMINIONREQUEST {
            OwnerActor = _ownerActor, DuelID = 123, Round = 2, RequestID = 3, MinionID = 50, MoveType = OwnedMinionAiMove,
            SpellTarget = uint.MaxValue });
        Assert.True(ai.Accepted);
        Assert.NotNull(_duel.CombatResolver.GetQueuedAction(_minion));
        Assert.True(_duel.HaveAllOwnedMinionOrders());
    }

    [Fact]
    public void HelperViewListsEachCardsLegalTargetsAndWhyACardCannotBeCast() {
        QueueAllPasses();
        var response = Request(1, query: true); Assert.True(response.Accepted);
        using (var view = JsonDocument.Parse(_duel.BuildMinionHelperView(_ownerActor, response)!)) {
            var root = view.RootElement;
            Assert.Equal("planning", root.GetProperty("phase").GetString());
            Assert.True(root.GetProperty("myth").GetBoolean());
            Assert.True(root.GetProperty("controlling").GetBoolean());
            var minion = Assert.Single(root.GetProperty("minions").EnumerateArray());
            Assert.Equal("50", minion.GetProperty("id").GetString());
            var card = Assert.Single(minion.GetProperty("hand").EnumerateArray());
            Assert.True(card.GetProperty("castable").GetBoolean());
            Assert.Equal([0], card.GetProperty("targets").EnumerateArray().Select(t => t.GetInt32()));
            Assert.False(card.GetProperty("untargeted").GetBoolean());
            Assert.Equal(3, root.GetProperty("combatants").GetArrayLength());
            Assert.Contains(root.GetProperty("combatants").EnumerateArray(), c => c.GetProperty("yours").GetBoolean());
        }

        _minion.CombatParticipant.m_pipCount.m_genericPips = 0;
        using (var view = JsonDocument.Parse(_duel.BuildMinionHelperView(_ownerActor, response)!)) {
            var card = view.RootElement.GetProperty("minions")[0].GetProperty("hand")[0];
            Assert.False(card.GetProperty("castable").GetBoolean());
            Assert.Equal("not enough pips", card.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public void HelperOptInWorksOutsidePlanningButNeverForNonMyth() {
        _duel.Duel.m_duelPhase = kDuelPhase.kPhase_Execution;
        CombatRegressionTests.Invoke(_duel, "ReceiveOwnedMinionOptIn", new MSG_OWNEDMINIONOPTIN { OwnerActor = _ownerActor, Enable = true });
        Assert.True(OptedIn());
        CombatRegressionTests.Invoke(_duel, "ReceiveOwnedMinionDisable", new MSG_OWNEDMINIONDISABLE { OwnerActor = _ownerActor });
        Assert.False(OptedIn());
        _owner._wizard.MagicSchoolBehavior.MagicSchool = MagicSchool.Fire;
        CombatRegressionTests.Invoke(_duel, "ReceiveOwnedMinionOptIn", new MSG_OWNEDMINIONOPTIN { OwnerActor = _ownerActor, Enable = true });
        Assert.False(OptedIn());
    }

    // CLASSIC: the Myth minion hand (Game/Zone/Components/CombatDuelComponent.MinionHand.cs).

    private System.Collections.Concurrent.ConcurrentQueue<object> HandOn(MagicSchool school = MagicSchool.Myth, int dealDelayMs = 0) {
        Settings(true, minionHand: true, dealDelayMs: dealDelayMs);
        _owner._wizard.MagicSchoolBehavior.MagicSchool = school;
        var inbox = new System.Collections.Concurrent.ConcurrentQueue<object>();
        CombatRegressionTests.SetProperty(_owner, "ParticipantActor", _system.ActorOf(Props.Create(() => new Recorder(inbox))));
        _duel.RegisterOwnedMinionForControl(_minion, _owner);
        foreach (var circle in new[] { _minion, _enemy })
            _duel.CombatResolver.AddCombatMove(CombatMoveType.Pass, circle, null!, null!);
        return inbox;
    }

    private void OwnerMove(CombatMoveType move, byte card = 0, uint target = 0)
        => CombatRegressionTests.Invoke(_duel, "ReceiveCombatMove", new MSG_ACTORCOMBATMOVE {
            Actor = _owner.ParticipantActor, MoveType = (byte) move, SpellSelection = card, SpellTarget = target });

    private static List<object> Drain(System.Collections.Concurrent.ConcurrentQueue<object> inbox, int atLeast = 1) {
        var got = new List<object>();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline) {
            while (inbox.TryDequeue(out var item)) got.Add(item);
            if (got.Count >= atLeast) {
                System.Threading.Thread.Sleep(50);
                while (inbox.TryDequeue(out var more)) got.Add(more);
                return got;
            }
            System.Threading.Thread.Sleep(10);
        }
        return got;
    }

    private bool StageActive() => (bool) CombatRegressionTests.Invoke(_duel, "MinionHandStageActive", _owner)!;

    [Fact]
    public void AfterTheMythWizardsOwnMoveTheCardWindowShowsTheMinionsHand() {
        var inbox = HandOn();
        OwnerMove(CombatMoveType.Pass);
        var sent = Drain(inbox, 4);
        var hand = Assert.Single(sent.OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>());
        Assert.Equal(_owner.ParticipantObject.m_globalID.Full, (ulong) hand.ParticipantID);
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        Assert.True(serializer.Deserialize<Hand>(hand.HandData, (PropertyFlags) 5, out var decoded));
        Assert.Equal(Tid, Assert.IsType<Hand>(decoded).m_spellList.Single().m_templateID);
        // The wizard's own pips stay: the stock client announces any pip increase as a gained pip.
        Assert.Empty(sent.OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPIPS>());
        Assert.Single(sent.OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SHOWCOMBATUI>());
        Assert.Single(sent.OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SETPLANNINGPHASETIMER>());
        // No chat cue: a chat line under the creature's plain-text name froze the client.
        Assert.Empty(sent.OfType<Imcodec.MessageLayer.Generated.GAME_5_PROTOCOL.MSG_RADIALCHAT>());
        Assert.Empty(sent.OfType<Imcodec.MessageLayer.Generated.EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE>());
        Assert.True(StageActive());
        Assert.False(_duel.HaveAllOwnedMinionOrders()); // the round waits for the minion's pick (or the timer)
    }

    [Fact]
    public void TheWizardsNextPickIsTheMinionsAndTheWizardsOwnMoveIsKept() {
        var inbox = HandOn();
        OwnerMove(CombatMoveType.Pass);
        Drain(inbox, 4);
        var own = _duel.CombatResolver.GetQueuedAction(_owner);
        OwnerMove(CombatMoveType.Attack, card: 0, target: 0);
        Assert.Same(own, _duel.CombatResolver.GetQueuedAction(_owner));
        Assert.Null(_duel.CombatResolver.GetQueuedAction(_owner).Spell);
        Assert.Same(_spell, _duel.CombatResolver.GetQueuedAction(_minion).Spell);
        Assert.Same(_enemy, _duel.CombatResolver.GetQueuedAction(_minion).SelectedTarget);
        Assert.False(StageActive());
        Assert.True(_duel.HaveAllOwnedMinionOrders());
        Assert.Equal(TimeSpan.FromSeconds(1), _timers.Delays.Last()); // everyone has moved: the round ends early
    }

    [Fact]
    public void AMinionPickTheMinionCannotMakeIsRefusedAndItsHandShownAgain() {
        var inbox = HandOn();
        OwnerMove(CombatMoveType.Pass);
        Drain(inbox, 4);
        _minion.CombatParticipant.m_pipCount.m_genericPips = 0;
        OwnerMove(CombatMoveType.Attack, card: 0, target: 0);
        var again = Drain(inbox, 4);
        Assert.Single(again.OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>());
        Assert.True(StageActive());
        Assert.Null(_duel.CombatResolver.GetQueuedAction(_minion).Spell);
        Assert.False(_duel.HaveAllOwnedMinionOrders());
        OwnerMove(CombatMoveType.Pass); // the wizard passes for the minion
        Assert.False(StageActive());
        Assert.True(_duel.HaveAllOwnedMinionOrders());
    }

    [Fact]
    public void ChangeAfterTheMinionsPickReopensIt() {
        var inbox = HandOn();
        OwnerMove(CombatMoveType.Pass);
        OwnerMove(CombatMoveType.Attack, card: 0, target: 0);
        Drain(inbox, 4);
        OwnerMove(CombatMoveType.ChangeMind);
        Assert.True(StageActive());
        Assert.False(_duel.HaveAllOwnedMinionOrders());
        Assert.Null(_duel.CombatResolver.GetQueuedAction(_minion).Spell); // back to its AI pass
        Assert.Single(Drain(inbox, 4).OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>());
    }

    [Theory]
    [InlineData(MagicSchool.Fire)] [InlineData(MagicSchool.Life)] [InlineData(MagicSchool.Balance)]
    public void OtherSchoolsKeepTheirMinionsAi(MagicSchool school) {
        var inbox = HandOn(school);
        OwnerMove(CombatMoveType.Pass);
        Assert.Empty(Drain(inbox, 1).OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>());
        Assert.False(StageActive());
        Assert.True(_duel.HaveAllOwnedMinionOrders());
    }

    [Fact]
    public void AStunnedMinionIsSkippedAndTheSettingTurnsTheHandOff() {
        var inbox = HandOn();
        _minion.CombatParticipant.m_stunned = 1;
        OwnerMove(CombatMoveType.Pass);
        Assert.Empty(Drain(inbox, 1).OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>());
        Assert.True(_duel.HaveAllOwnedMinionOrders());

        Settings(true, minionHand: false);
        Assert.False(MythMinionHandSettings.Enabled);
    }

    [Fact]
    public void TheMinionsHandIsDealtAfterABeatAndChangeInTheBeatIsTheWizardsOwn() {
        var inbox = HandOn(dealDelayMs: 750);
        OwnerMove(CombatMoveType.Pass);
        Assert.Empty(Drain(inbox, 1).OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>());
        Assert.Equal(TimeSpan.FromMilliseconds(750), _timers.Delays.Last());
        Assert.True(StageActive());                 // the round still waits for the minion
        Assert.False(_duel.HaveAllOwnedMinionOrders());

        // A stale ticket does nothing; the current one deals the hand and the minion's cue.
        CombatRegressionTests.Invoke(_duel, "ReceiveMinionHandDeal", new MSG_MINIONHANDDEAL { Owner = _owner.ParticipantObject, Ticket = -1 });
        Assert.Empty(Drain(inbox, 1).OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>());
        CombatRegressionTests.Invoke(_duel, "ReceiveMinionHandDeal", new MSG_MINIONHANDDEAL { Owner = _owner.ParticipantObject, Ticket = Ticket() });
        var dealt = Drain(inbox, 5);
        Assert.Single(dealt.OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>());
        Assert.Empty(dealt.OfType<Imcodec.MessageLayer.Generated.GAME_5_PROTOCOL.MSG_RADIALCHAT>());

        // A second round: "Change" during the beat re-picks the wizard's own card, and no hand is dealt.
        var ticketBefore = Ticket();
        OwnerMove(CombatMoveType.Attack, card: 0, target: 0); // the minion's pick
        Assert.False(StageActive());
        OwnerMove(CombatMoveType.ChangeMind);              // reopens the minion's pick after a beat
        Assert.True(StageActive());
        Assert.NotEqual(ticketBefore, Ticket());
        OwnerMove(CombatMoveType.ChangeMind);              // in the beat: the wizard's own change
        Assert.False(StageActive());
        CombatRegressionTests.Invoke(_duel, "ReceiveMinionHandDeal", new MSG_MINIONHANDDEAL { Owner = _owner.ParticipantObject, Ticket = Ticket() });
        Assert.Empty(Drain(inbox, 1).OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>());
    }

    private int Ticket() {
        var stages = (System.Collections.IDictionary) typeof(CombatDuelComponent).GetField("_minionHandStages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_duel)!;
        var stage = stages[_owner.ParticipantObject];
        return stage is null ? 0 : (int) stage.GetType().GetField("PendingTicket", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stage)!;
    }

    [Fact]
    public void TheMinionsHandShowsTheMinionsDeckCountsAndItsDiscardWorks() {
        _minion._combatDeck = new CombatDeck([new CombatDeckSpellData { TemplateId = Tid, Quantity = 5 }], [], 7);
        _minion._combatDeck.AddCardToHand(_spell);
        var second = new Spell { m_templateID = Tid, m_magicSchoolID = (uint) MagicSchool.Myth, m_pipCost = new SpellRank { m_spellRank = 2 } };
        _minion._combatDeck.AddCardToHand(second);
        var inbox = HandOn();
        OwnerMove(CombatMoveType.Pass);
        var hand = Assert.Single(Drain(inbox, 4).OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>());
        Assert.Equal(5, hand.DeckCount);
        Assert.Equal(5, hand.TotalDeckCount);
        Assert.Equal(0, hand.TreasureCardCount);

        OwnerMove(CombatMoveType.Discard, card: 0);
        var after = Assert.Single(Drain(inbox, 1).OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>());
        Assert.Equal(_owner.ParticipantObject.m_globalID.Full, (ulong) after.ParticipantID);
        Assert.Equal(new[] { second }, _minion._combatDeck.LastGivenHand);
        Assert.True(StageActive()); // still the minion's pick
    }

    [Fact]
    public void AnEndlessCreatureDeckShowsItsDifferentCardsOnTheCounter() {
        _minion._combatDeck = new CombatDeck([new CombatDeckSpellData { TemplateId = Tid, Quantity = 9999 },
            new CombatDeckSpellData { TemplateId = Tid - 1, Quantity = 9999 }], [], 7);
        _minion._combatDeck.AddCardToHand(_spell);
        var inbox = HandOn();
        OwnerMove(CombatMoveType.Pass);
        var hand = Assert.Single(Drain(inbox, 4).OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>());
        Assert.Equal(2, hand.DeckCount);
        Assert.Equal(2, hand.TotalDeckCount);
    }

    [Fact]
    public void AWizardsTreasureCardDiscardIsRefusedWithTheHandAsItStands() {
        var inbox = HandOn();
        _owner._combatDeck = new CombatDeck([], [new CombatDeckSpellData { TemplateId = Tid, Quantity = 1, IsTreasureCard = true }], 7);
        var tc = Assert.IsType<Spell>(_owner._combatDeck.DrawFromVault());
        OwnerMove(CombatMoveType.Discard, card: 0);
        var hand = Assert.Single(Drain(inbox, 1).OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>());
        Assert.Equal(_owner.ParticipantObject.m_globalID.Full, (ulong) hand.ParticipantID);
        Assert.Same(tc, Assert.Single(_owner._combatDeck.LastGivenHand));
        Assert.Equal(1, _owner._combatDeck.VaultTotalCount);
        Assert.Equal(0, _owner._combatDeck.VaultRemainingCount);
    }

    [Fact]
    public void ATreasureCardInTheMinionsHandCannotBeDiscarded() {
        _minion._combatDeck = new CombatDeck([], [], 7);
        var tc = new Spell { m_templateID = Tid, m_magicSchoolID = (uint) MagicSchool.Myth, m_pipCost = new SpellRank { m_spellRank = 2 }, m_treasureCard = true };
        _minion._combatDeck.AddCardToHand(tc);
        var inbox = HandOn();
        OwnerMove(CombatMoveType.Pass);
        Drain(inbox, 4);
        OwnerMove(CombatMoveType.Discard, card: 0);
        Assert.Single(Drain(inbox, 1).OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>());
        Assert.Same(tc, Assert.Single(_minion._combatDeck.LastGivenHand));
        Assert.True(StageActive());
    }

    /// <summary>
    /// The r806919 client's pip-gain flags (ClientDuelManager::MSG_CombatPips, 0x1420d6130) and when the combat-message
    /// code (0x1402d3ad0) would announce them: generic when the generic gain is above one, power when the power flag is set.
    /// </summary>
    private sealed class ClientPipFlags {
        public (int G, int P) Pips; public int GenericGain; public bool PowerFlag;
        public ClientPipFlags((int, int) start) => Pips = start;
        public void Apply((int G, int P) next) {
            if (next.G > Pips.G) { GenericGain = next.G - Pips.G; PowerFlag = false; }
            if (next.P > Pips.P) { PowerFlag = true; GenericGain = 0; }
            Pips = next;
        }
        public bool WouldAnnounce => GenericGain > 1 || PowerFlag;
    }

    [Fact]
    public void FlagNeutralPipStepsReachEveryTargetWithoutAnAnnouncement() {
        for (var g0 = 0; g0 <= 7; g0++) for (var p0 = 0; p0 + g0 <= 7; p0++)
        for (var g1 = 0; g1 <= 7; g1++) for (var p1 = 0; p1 + g1 <= 7; p1++) {
            var client = new ClientPipFlags((g0, p0)) { GenericGain = 3, PowerFlag = true }; // worst stale state
            var steps = CombatDuelComponent.FlagNeutralPipSteps(((byte) g0, (byte) p0), ((byte) g1, (byte) p1));
            foreach (var s in steps) client.Apply((s.Generic, s.Power));
            Assert.Equal((g1, p1), client.Pips);
            if (steps.Count > 0) Assert.False(client.WouldAnnounce, $"{g0}+{p0}P -> {g1}+{p1}P");
            else Assert.Equal((g0, p0), (g1, p1));
            Assert.True(steps.All(s => s.Generic <= 7));
        }
    }

    private static (int G, int P)? OwnerEntry(Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPIPS m, ulong owner) {
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        Assert.True(serializer.Deserialize<CombatPipListObj>(m.PipData, (PropertyFlags) 5, out var list));
        var e = list!.m_pipList.FirstOrDefault(x => (ulong) x.m_partID == owner);
        return e is null ? null : (e.m_pips.m_genericPips, e.m_pips.m_powerPips);
    }

    [Fact]
    public void TheMinionsHandGreysByTheMinionsPipsAndTheWizardsComeBackWithoutAnAnnouncement() {
        _owner.CombatParticipant.m_pipCount = new PipCount { m_genericPips = 1, m_powerPips = 0 };
        _minion.CombatParticipant.m_pipCount = new PipCount { m_genericPips = 1, m_powerPips = 2 };
        var inbox = HandOn();
        OwnerMove(CombatMoveType.Pass);
        var sent = Drain(inbox, 5);
        var pipMsgs = sent.OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPIPS>().ToList();
        Assert.NotEmpty(pipMsgs);
        Assert.True(sent.IndexOf(pipMsgs.Last()) < sent.IndexOf(sent.OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>().Single()));
        var owner = _owner.ParticipantObject.m_globalID.Full;
        var client = new ClientPipFlags((1, 0));
        foreach (var m in pipMsgs) client.Apply(OwnerEntry(m, owner)!.Value);
        Assert.Equal((1, 2), client.Pips);   // the minion's pips: a 3-pip card (2 power pips count double) is castable
        Assert.False(client.WouldAnnounce);
        Assert.Equal(1, _owner.CombatParticipant.m_pipCount.m_genericPips); // the wizard's real pips never change
        Assert.Equal(0, _owner.CombatParticipant.m_pipCount.m_powerPips);

        OwnerMove(CombatMoveType.Attack, card: 0, target: 0); // the minion's pick: the stage ends
        foreach (var m in Drain(inbox, 1).OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPIPS>()) client.Apply(OwnerEntry(m, owner)!.Value);
        Assert.Equal((1, 0), client.Pips);
        Assert.False(client.WouldAnnounce);
        Assert.False(StageActive());
    }

    [Fact]
    public void ANewRoundWithEmptyCirclesResetsTheMinionHandWithoutThrowing() {
        // Rig mp1: an empty circle's null participant object as a dictionary key stopped every duel's MSG_NEWROUND.
        Assert.Contains(_duel.SubCircles, circle => circle is not null && circle.ParticipantObject is null);
        _owner.CombatParticipant.m_pipCount = new PipCount { m_genericPips = 1, m_powerPips = 0 };
        _minion.CombatParticipant.m_pipCount = new PipCount { m_genericPips = 3, m_powerPips = 0 };
        var inbox = HandOn();
        OwnerMove(CombatMoveType.Pass); // the minion's pips are now shown
        Drain(inbox, 5);
        typeof(CombatDuelComponent).GetMethod("ResetMinionHand", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_duel, null);
        Assert.False(StageActive());
    }

    [Fact]
    public void AMinionOfAnotherSchoolShowsItsSchoolValueAsPlainPips() {
        _minion.CombatParticipant.m_primaryMagicSchoolID = (int) MagicSchool.Death;
        _minion.CombatParticipant.m_pipCount = new PipCount { m_genericPips = 1, m_powerPips = 2 };
        Assert.Equal(((byte) 5, (byte) 0), CombatDuelComponent.MinionPipsForOwnerWindow(_owner, _minion));
        _minion.CombatParticipant.m_pipCount = new PipCount { m_genericPips = 3, m_powerPips = 4 };
        Assert.Equal(((byte) 7, (byte) 0), CombatDuelComponent.MinionPipsForOwnerWindow(_owner, _minion));
    }

    [Fact]
    public void ATreasureCardDrawWhileTheMinionsHandShowsIsRefused() {
        var inbox = HandOn();
        OwnerMove(CombatMoveType.Pass);
        Drain(inbox, 4);
        CombatRegressionTests.Invoke(_duel, "ReceiveCombatDraw", new MSG_ACTORCOMBATDRAW { Actor = _owner.ParticipantActor });
        Assert.Single(Drain(inbox, 4).OfType<Imcodec.MessageLayer.Generated.DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>());
        Assert.True(StageActive());
    }

    private sealed class Recorder : ReceiveActor {
        public Recorder(System.Collections.Concurrent.ConcurrentQueue<object> inbox) => ReceiveAny(inbox.Enqueue);
    }

    private bool OptedIn() {
        var control = (OwnedMinionControl) typeof(CombatDuelComponent).GetField("_ownedMinionControl", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_duel)!;
        return control.IsOptedIn(_owner.ParticipantObject);
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
    // CLASSIC: these tests cover owner orders sent from outside the card window (service 90, the Minion Helper),
    // so the in-client minion hand (on by default for Myth wizards) is off unless a test turns it on.
    private static void Settings(bool enabled, bool minionHand = false, int dealDelayMs = 0) {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}imlight-owned-minion-tests.log\n[Classic]\nOwnedMinionControl={enabled}\nMythMinionHand={minionHand}\nMythMinionHandDelayMs={dealDelayMs}\n");
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
