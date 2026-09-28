using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Xunit;
using CombatResolver = Imlight.CoreLib.Game.Combat.CombatResolver;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ClassicDeckRegressionTests : IDisposable {
    private const uint Regular = uint.MaxValue - 401, Reshuffle = uint.MaxValue - 402, Treasure = uint.MaxValue - 403;
    private readonly IDictionary<ulong, CoreTemplate> _templates;

    public ClassicDeckRegressionTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-deck-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally { File.Delete(config); }
        _templates = (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (uint id in new[] { Regular, Reshuffle, Treasure }) {
            _templates.Add(id, new SpellTemplate {
                m_name = "deck-fixture-" + id, m_spellBase = "deck-fixture", m_sMagicSchoolName = "Balance", m_accuracy = 100,
                m_spellRank = new SpellRank { m_spellRank = id == Reshuffle ? (byte) 4 : (byte) 0 }, m_Treasure = id == Treasure,
                m_effects = id == Reshuffle ? [new SpellEffect { m_effectType = kSpellEffects.kReshuffle, m_effectTarget = kEffectTarget.kFriendlySingle }] : [],
            });
        }
    }

    public void Dispose() {
        foreach (uint id in new[] { Regular, Reshuffle, Treasure }) _templates.Remove(id);
        ClassicRuntime.ResetForTests();
    }

    private static CombatDeckSpellData Card(uint id, uint count = 1, bool item = false, bool battle = false)
        => new() { TemplateId = id, Quantity = count, IsItemCard = item, IsBattleCard = battle };
    private static Spell Draw(CombatDeck deck) => Assert.Single(deck.GetHand().m_spellList);

    [Theory]
    [InlineData("late-2009")]
    [InlineData("arc1-2009h1")]
    public void EveryDrawnCopyCanBeDiscardedWithoutLosingAnotherUndrawnCopy(string profile) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(profile));
        var deck = new CombatDeck([Card(Regular, 3)], [], 1);
        for (int left = 2; left >= 0; left--) {
            var card = Draw(deck);
            Assert.Equal(left, deck.RemainingCardCount);
            deck.Discard(card);
            deck.Discard(card); // duplicate callback must neither remove another copy nor add a discard
            Assert.Empty(deck.LastGivenHand);
            Assert.Equal(left, deck.RemainingCardCount);
        }
        Assert.Empty(deck.GetHand().m_spellList);
        deck.Reshuffle();
        Assert.Equal(3, deck.RemainingCardCount);
        deck.Reshuffle();
        Assert.Equal(3, deck.RemainingCardCount);
    }

    [Fact]
    public void ReshufflePreservesHeldCardsAndUndrawnCopiesWithoutDuplicatingThem() {
        var deck = new CombatDeck([Card(Regular, 4)], [], 1);
        deck.Discard(Draw(deck));
        var held = Draw(deck);
        Assert.Equal(2, deck.RemainingCardCount);
        deck.Reshuffle();
        Assert.Same(held, Assert.Single(deck.LastGivenHand));
        Assert.Equal(3, deck.RemainingCardCount);
        deck.Reshuffle();
        Assert.Equal(3, deck.RemainingCardCount);
        deck.Discard(held);
        deck.Reshuffle();
        Assert.Equal(4, deck.RemainingCardCount);
    }

    [Fact]
    public void IdenticalHandCardsAreTrackedByInstanceAndItemBattleFlagsSurviveRestoration() {
        var deck = new CombatDeck([Card(Regular, 2, item: true, battle: true)], [], 2);
        var hand = deck.GetHand().m_spellList;
        var first = hand[0];
        var second = hand[1];
        Assert.NotSame(first, second);
        deck.Discard(second);
        Assert.Same(first, Assert.Single(deck.LastGivenHand));
        deck.Reshuffle();
        Assert.Equal(1, deck.RemainingCardCount);
        var restored = deck.GetHand().m_spellList.Single(card => !ReferenceEquals(card, first));
        Assert.True(restored.m_itemCard);
        Assert.True(restored.m_battleCard);
        Assert.False(restored.m_treasureCard);
        Assert.Equal(0, deck.RemainingCardCount);
    }

    [Fact]
    public void VaultReturnsAndSuccessfulConsumptionAreIdempotentAndReshuffleCannotRestoreTreasureCards() {
        var deck = new CombatDeck([], [Card(Treasure, 3)], 1);
        var card = Assert.IsType<Spell>(deck.DrawFromVault());
        deck.Discard(card);
        deck.Discard(card);
        Assert.Equal(3, deck.VaultRemainingCount);
        Assert.Equal(0, deck.TreasureCardsInHand);
        card = Assert.IsType<Spell>(deck.DrawFromVault());
        Assert.Equal(Treasure, deck.ConsumeFromVault(card));
        Assert.Equal(0u, deck.ConsumeFromVault(card));
        Assert.Equal(2, deck.VaultRemainingCount);
        Assert.Equal(2, deck.VaultTotalCount);
        deck.Reshuffle();
        Assert.Equal(0, deck.RemainingCardCount);
        Assert.Equal(2, deck.VaultRemainingCount);
        card = Assert.IsType<Spell>(deck.DrawFromVault());
        deck.Reshuffle();
        Assert.Same(card, Assert.Single(deck.LastGivenHand));
        Assert.Equal(1, deck.VaultRemainingCount);
        Assert.Equal(1, deck.TreasureCardsInHand);
    }

    [Fact]
    public void StockProfileRetainsItsExistingDeckResetBehavior() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicRules.Stock);
        var deck = new CombatDeck([Card(Regular, 3)], [], 1);
        deck.Discard(Draw(deck));
        Draw(deck);
        Assert.Equal(0, deck.RemainingCardCount); // stock's existing second decrement remains gated outside classic
        deck.Reshuffle();
        Assert.Equal(3, deck.RemainingCardCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulReshuffleRestoresOnlyTheSelectedDeckAndItselfOnlyWhenSelfTargeted(bool allyTarget) {
        var actor = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(actor, "Duel", new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
        var casterDeck = new CombatDeck([Card(Reshuffle)], [], 1);
        var caster = Occupy(actor, 4, casterDeck);
        var reshuffle = Draw(casterDeck);
        var allyDeck = new CombatDeck([Card(Regular, 2)], [], 1);
        allyDeck.Discard(Draw(allyDeck));
        var allyHeld = Draw(allyDeck);
        var ally = Occupy(actor, 5, allyDeck);
        var selected = allyTarget ? ally : caster;
        var action = new QueuedCombatAction { Spell = reshuffle, SpellTemplate = (SpellTemplate) _templates[Reshuffle], SpellCaster = caster, SelectedTarget = selected };
        var result = new CombatAction { m_spell = reshuffle, m_spellCaster = 4, m_targetSubcircleList = [] };
        float time = 0;
        Assert.True(CombatActionResolver.ProcessedQueuedCombatAction(action, ref result, ref time));
        typeof(CombatResolver).GetMethod("DoSpellCastConsequences", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [caster, result]);
        Assert.Equal(allyTarget ? 0 : 1, casterDeck.RemainingCardCount);
        Assert.Equal(allyTarget ? 1 : 0, allyDeck.RemainingCardCount);
        Assert.Same(allyHeld, Assert.Single(allyDeck.LastGivenHand));
        Assert.Empty(casterDeck.LastGivenHand);
        Assert.Equal(new[] { selected.SlotIndex }, result.m_targetSubcircleList);
        Assert.Equal(3, (int) caster.CombatParticipant.m_pipCount.m_genericPips);
        casterDeck.Reshuffle(); // the ally-targeted cast remains recoverable on a later Reshuffle
        Assert.Equal(1, casterDeck.RemainingCardCount);
    }

    [Fact]
    public void FizzledReshuffleKeepsTheCardAndDoesNotRestoreDiscardedCards() {
        var actor = CombatRegressionTests.MakeDuel();
        var deck = new CombatDeck([Card(Reshuffle), Card(Regular)], [], 2);
        var caster = Occupy(actor, 4, deck);
        var hand = deck.GetHand().m_spellList;
        var card = hand.Single(spell => spell.m_templateID == Reshuffle);
        deck.Discard(hand.Single(spell => spell.m_templateID == Regular));
        var resolver = new CombatResolver(new Duel(), actor.SubCircles);
        var actions = new CombatActionListObj { m_actionList = [] };
        var action = new QueuedCombatAction { Spell = card, SpellTemplate = (SpellTemplate) _templates[Reshuffle], SpellCaster = caster, SelectedTarget = caster };
        CombatRegressionTests.Invoke(resolver, "HandleFizzleAction", action, actions);
        Assert.Same(card, Assert.Single(deck.LastGivenHand));
        Assert.Equal(0, deck.RemainingCardCount);
        Assert.Equal(0, (int) Assert.Single(actions.m_actionList).m_spellHits);
        Assert.Equal(7, (int) caster.CombatParticipant.m_pipCount.m_genericPips);
    }

    private static CombatDuelSubCircle Occupy(CombatDuelComponent actor, int slot, CombatDeck deck) {
        var circle = actor.SubCircles[slot];
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_currentHitpoints = stats.m_baseHitpoints = 1000;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant { m_hangingEffects = [], m_pipCount = new PipCount { m_genericPips = 7 } });
        circle._combatDeck = deck;
        return circle;
    }
}
