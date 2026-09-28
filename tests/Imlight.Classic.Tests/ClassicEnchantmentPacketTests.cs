using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Xunit;
using Resolver = Imlight.CoreLib.Game.Combat.CombatResolver;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ClassicEnchantmentPacketTests : IDisposable {
    private const uint Source = 4294966700, Target = Source + 1, Mutation = Source + 2;
    private readonly IDictionary<ulong, CoreTemplate> _cache;
    private readonly ActorSystem _system;
    private readonly Channel<object> _packets = Channel.CreateUnbounded<object>();
    private readonly IActorRef _player;
    private readonly CombatDuelComponent _duel;
    private readonly CombatDuelSubCircle _caster;
    private readonly CombatDeck _deck;

    public ClassicEnchantmentPacketTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}imlight-enchant-tests.log\n");
            ConfigurationManager.Initialize(path);
        } finally { File.Delete(path); }
        _cache = (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory).GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        _cache.Add(Source, Template("enchant-fixture", [new SpellEffect { m_effectType = kSpellEffects.kModifyCardDamage, m_effectParam = 100, m_effectTarget = kEffectTarget.kSpell }], 0));
        _cache.Add(Target, Template("target-fixture", [Damage(200)], 2));
        _cache.Add(Mutation, Template("mutation-fixture", [Damage(350)], 3));
        _system = ActorSystem.Create("enchant-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        _player = _system.ActorOf(Props.Create(() => new Recorder(_packets)));
        _duel = CombatRegressionTests.MakeDuel();
        var entity = (ZoneEntity) RuntimeHelpers.GetUninitializedObject(typeof(ZoneEntity));
        CombatRegressionTests.SetProperty(entity, "ActiveGameObject", new CoreObject());
        typeof(ZoneEntityComponent).GetProperty("Entity", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_duel, entity);
        CombatRegressionTests.SetProperty(_duel, "Duel", new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
        CombatRegressionTests.SetProperty(_duel, "CombatResolver", new Resolver(_duel.Duel, _duel.SubCircles));
        _duel.CombatResolver.Reset();
        Field("_tutorialDirector", new TutorialDuelDirector(_duel, ""));
        Field("_awaitingCombatMoves", true);
        _deck = new CombatDeck([], [], 7);
        _caster = Occupy(4, true);
        _caster._combatDeck = _deck;
        CombatRegressionTests.SetProperty(_caster, "ParticipantActor", _player);
        _deck.AddCardToHand(SpellFactory.GetSpell(Source));
        _deck.AddCardToHand(SpellFactory.GetSpell(Target));
    }

    public void Dispose() {
        _system.Terminate().GetAwaiter().GetResult();
        foreach (var id in new[] { Source, Target, Mutation }) _cache.Remove(id);
        ClassicRuntime.ResetForTests();
    }

    private static SpellEffect Damage(int amount) => new() { m_effectType = kSpellEffects.kDamage, m_effectParam = amount, m_effectTarget = kEffectTarget.kEnemySingle, m_sDamageType = "Fire" };
    private static SpellTemplate Template(string name, List<SpellEffect> effects, byte rank) => new() {
        m_name = name, m_spellBase = name, m_sMagicSchoolName = "Fire", m_accuracy = 75,
        m_spellRank = new SpellRank { m_spellRank = rank }, m_effects = effects,
    };
    private SpellTemplate SourceTemplate => (SpellTemplate) _cache[Source];
    private SpellTemplate TargetTemplate => (SpellTemplate) _cache[Target];
    private void Field(string name, object value) => typeof(CombatDuelComponent).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_duel, value);
    private CombatDuelSubCircle Occupy(int slot, bool player) {
        var c = _duel.SubCircles[slot];
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_currentHitpoints = stats.m_baseHitpoints = 1000;
        CombatRegressionTests.SetProperty(c, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(c, "ParticipantObject", new CoreObject { m_templateID = player ? 1u : 2u });
        CombatRegressionTests.SetProperty(c, "ParticipantActor", ActorRefs.Nobody);
        CombatRegressionTests.SetProperty(c, "CombatParticipant", new CombatParticipant { m_hangingEffects = [], m_pipCount = new PipCount { m_genericPips = 7 } });
        c.AddedToDuel = true;
        return c;
    }
    private void Send(byte source = 0, uint target = 1) {
        var wire = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMOVE { MoveType = 5, SpellSelection = source, SpellTarget = target, TimeLeft = 20 };
        var translated = CombatService.TranslateCombatMove(wire, _player);
        Assert.Equal(target, translated.SpellTarget);
        CombatRegressionTests.Invoke(_duel, "ReceiveCombatMove", translated);
    }
    private async Task<Hand> ReadHand() {
        var packet = Assert.IsType<DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND>(await _packets.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        Assert.True(serializer.Deserialize<Hand>(packet.HandData, (PropertyFlags) 5, out var hand));
        return Assert.IsType<Hand>(hand);
    }

    [Fact]
    public async Task DamagePacketConsumesSourceReturnsModifiedHandAndCastsOnlyThisCopyWithBonus() {
        var untouched = SpellFactory.GetSpell(Target);
        _deck.AddCardToHand(untouched);
        Send();
        var hand = await ReadHand();
        Assert.Equal(2, hand.m_spellList.Count);
        Assert.Equal(100, hand.m_spellList[0].m_regularAdjust);
        Assert.Equal(Source, hand.m_spellList[0].m_enchantment);
        Assert.Equal(0u, hand.m_spellList[1].m_enchantment);
        Assert.Null(_duel.CombatResolver.GetQueuedAction(_caster));
        Assert.Equal(7, (int) _caster.CombatParticipant.m_pipCount.m_genericPips);
        var enemy = Occupy(0, false);
        CombatRegressionTests.Invoke(_duel, "HandleAttackMove", _caster, 0, 0u);
        var queued = _duel.CombatResolver.GetQueuedAction(_caster);
        Assert.Equal(300, Assert.Single(queued.SpellTemplate.m_effects).m_effectParam);
        Assert.Equal(200, Assert.Single(TargetTemplate.m_effects).m_effectParam);
        var result = new CombatAction { m_spell = queued.Spell, m_targetSubcircleList = [] };
        float time = 0;
        Assert.True(CombatActionResolver.ProcessedQueuedCombatAction(queued, ref result, ref time));
        Assert.Equal(700, enemy.ParticipantGameStats.m_currentHitpoints);
        _deck.Discard(queued.Spell);
        _deck.Reshuffle();
        Assert.Equal(2, _deck.RemainingCardCount); // base spell and regular enchant return separately
        Assert.Same(untouched, Assert.Single(_deck.LastGivenHand));
        Assert.All(_deck.GetHand().m_spellList, card => Assert.Equal(0u, card.m_enchantment));
    }

    [Theory]
    [InlineData(15, 90)]
    [InlineData(40, 115)]
    public async Task AccuracyBonusIsSerializedOnTheCardUsedByTheHitRoll(int amount, int expected) {
        SourceTemplate.m_effects[0].m_effectType = kSpellEffects.kModifyCardAccuracy;
        SourceTemplate.m_effects[0].m_effectParam = amount;
        Send();
        Assert.Equal(expected, (int) Assert.Single((await ReadHand()).m_spellList).m_accuracy);
        Assert.Equal(75, TargetTemplate.m_accuracy);
    }

    [Fact]
    public async Task MutationUsesRecordedOutputAndRestoresOriginalCardOnReshuffle() {
        SourceTemplate.m_effects[0].m_effectType = kSpellEffects.kModifyCardMutation;
        SourceTemplate.m_effects[0].m_effectTarget = kEffectTarget.kSpecificSpells;
        SourceTemplate.m_effects[0].m_effectParam = unchecked((int) Mutation);
        SourceTemplate.m_validTargetSpells = [Target];
        Send();
        var card = Assert.Single((await ReadHand()).m_spellList);
        Assert.Equal(Mutation, card.m_templateID);
        Assert.Equal(Target, card.m_premutationSpellID);
        Assert.Equal(3, (int) card.m_pipCost.m_spellRank);
        var enemy = Occupy(0, false);
        CombatRegressionTests.Invoke(_duel, "HandleAttackMove", _caster, 0, 0u);
        var action = _duel.CombatResolver.GetQueuedAction(_caster);
        var result = new CombatAction { m_spell = action.Spell, m_targetSubcircleList = [] };
        float time = 0;
        Assert.True(CombatActionResolver.ProcessedQueuedCombatAction(action, ref result, ref time));
        Assert.Equal(650, enemy.ParticipantGameStats.m_currentHitpoints);
        _deck.Discard(action.Spell);
        _deck.Reshuffle();
        var restored = _deck.GetHand().m_spellList;
        Assert.Contains(restored, s => s.m_templateID == Target);
        Assert.DoesNotContain(restored, s => s.m_templateID == Mutation);
    }

    [Fact]
    public async Task TreasureEnchantIsConsumedOnceAndNotRestoredWithTheBaseCard() {
        SourceTemplate.m_Treasure = true;
        var deck = new CombatDeck([], [new CombatDeckSpellData { TemplateId = Source, Quantity = 8 }], 7);
        _caster._combatDeck = deck;
        deck.AddCardToHand(SpellFactory.GetSpell(Target));
        Assert.NotNull(deck.DrawFromVault());
        Send(1, 0);
        Assert.Single((await ReadHand()).m_spellList);
        Assert.Equal(7, deck.VaultTotalCount);
        Assert.Equal(7, deck.VaultRemainingCount);
        Send(1, 0); // stale source slot cannot spend another treasure card
        await ReadHand();
        Assert.Equal(7, deck.VaultTotalCount);
        deck.Discard(Assert.Single(deck.LastGivenHand));
        deck.Reshuffle();
        Assert.Equal(Target, Assert.Single(deck.GetHand().m_spellList).m_templateID);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("range")]
    [InlineData("treasure-target")]
    [InlineData("item-target")]
    [InlineData("already-enchanted")]
    [InlineData("restriction")]
    [InlineData("specific-target")]
    [InlineData("mutation-without-input-list")]
    [InlineData("dot")]
    [InlineData("multi-hit")]
    [InlineData("queued")]
    [InlineData("pvp")]
    public async Task RejectedPacketsLeaveBothCardsAndQueueUnchanged(string kind) {
        var first = _deck.LastGivenHand[0]; var second = _deck.LastGivenHand[1];
        uint target = 1;
        switch (kind) {
            case "self": target = 0; break;
            case "range": target = uint.MaxValue; break;
            case "treasure-target": second.m_treasureCard = true; break;
            case "item-target": second.m_itemCard = true; break;
            case "already-enchanted": second.m_enchantment = Source; break;
            case "restriction": TargetTemplate.m_noPvEEnchant = true; break;
            case "specific-target": SourceTemplate.m_validTargetSpells = [Mutation]; break;
            case "mutation-without-input-list": SourceTemplate.m_effects[0].m_effectType = kSpellEffects.kModifyCardMutation; break;
            case "dot": TargetTemplate.m_effects[0].m_effectType = kSpellEffects.kDamageOverTime; break;
            case "multi-hit": TargetTemplate.m_effects.Add(Damage(50)); break;
            case "pvp": _duel.Duel.m_bPVP = true; break;
            case "queued": _duel.CombatResolver.AddCombatMove(CombatMoveType.Pass, _caster, null, null); break;
        }
        var queue = _duel.CombatResolver.GetQueuedAction(_caster);
        Send(target: target);
        Assert.Equal(2, (await ReadHand()).m_spellList.Count);
        Assert.Same(first, _deck.LastGivenHand[0]); Assert.Same(second, _deck.LastGivenHand[1]);
        Assert.Same(queue, _duel.CombatResolver.GetQueuedAction(_caster));
        _deck.Reshuffle(); Assert.Equal(0, _deck.RemainingCardCount);
    }

    [Fact]
    public async Task RandomDamageAddsBonusToEachAlternativeWithoutChangingCachedTemplate() {
        TargetTemplate.m_effects = [new RandomSpellEffect { m_effectList = [Damage(150), Damage(200), Damage(250)] }];
        Send();
        await ReadHand();
        var card = Assert.Single(_deck.LastGivenHand);
        var template = _deck.CastTemplateFor(card, TargetTemplate);
        Assert.Equal(new[] { 250, 300, 350 }, Assert.IsType<RandomSpellEffect>(template.m_effects[0]).m_effectList.Select(e => e.m_effectParam));
        Assert.Equal(new[] { 150, 200, 250 }, Assert.IsType<RandomSpellEffect>(TargetTemplate.m_effects[0]).m_effectList.Select(e => e.m_effectParam));
    }

    [Fact]
    public async Task ASecondEnchantIsRejectedWithoutSpendingItsSource() {
        Send(); await ReadHand();
        var enchanted = Assert.Single(_deck.LastGivenHand);
        var secondSource = SpellFactory.GetSpell(Source);
        _deck.AddCardToHand(secondSource);
        Send(1, 0); await ReadHand();
        Assert.Same(enchanted, _deck.LastGivenHand[0]);
        Assert.Same(secondSource, _deck.LastGivenHand[1]);
        _deck.Reshuffle(); Assert.Equal(1, _deck.RemainingCardCount);
    }

    [Fact]
    public void StockAndNonPlanningPacketsCannotEnchant() {
        Field("_awaitingCombatMoves", false);
        Send(); Assert.Equal(2, _deck.LastGivenHand.Count);
        Field("_awaitingCombatMoves", true);
        ClassicRuntime.ResetForTests(); ClassicRuntime.Initialize(ClassicRules.Stock);
        Send(); Assert.Equal(2, _deck.LastGivenHand.Count);
        Assert.False(_packets.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(0, 1u, 0u)]
    [InlineData(0, 128u, 7u)]
    [InlineData(2, 0u, 2147483648u)]
    [InlineData(3, 0u, 2147483648u)]
    [InlineData(5, 0u, 0u)]
    [InlineData(5, 6u, 6u)]
    public void TranslationPreservesExistingMovesAndRawEnchantHandIndices(byte move, uint target, uint expected) {
        var result = CombatService.TranslateCombatMove(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMOVE { MoveType = move, SpellSelection = 2, SpellTarget = target }, _player);
        Assert.Equal(move, result.MoveType); Assert.Equal(2, result.SpellSelection); Assert.Equal(expected, result.SpellTarget);
    }

    private sealed class Recorder : ReceiveActor {
        public Recorder(Channel<object> packets) { ReceiveAny(packet => packets.Writer.TryWrite(packet)); }
    }
}
