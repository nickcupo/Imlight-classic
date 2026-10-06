// CLASSIC: generated native spell structures are interpreted as choices, consecutive hits and exact X tiers.
using System;
using System.Collections.Generic;
using System.Reflection;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pvp;
using Imlight.CoreLib.Classic.Pvp;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ArenaPvpRuntimeTests {
    private static SpellEffect Damage(int amount, int pips = 0, kSpellEffects kind = kSpellEffects.kDamage,
        kEffectTarget target = kEffectTarget.kEnemySingle)
        => new() { m_effectType = kind, m_effectParam = amount, m_sDamageType = "Fire", m_effectTarget = target, m_pipNum = pips };
    private static SpellTemplate Template(params SpellEffect[] effects)
        => new() { m_name = "fixture", m_sMagicSchoolName = "Fire", m_accuracy = 80,
            m_spellRank = new SpellRank { m_spellRank = 2 }, m_effects = [.. effects] };
    private static ArenaPvpCard Card(SpellTemplate template, int pips = 4)
        => ArenaPvpCombat.CardFor(0, SpellFactory.GetSpell(template, 1), template, pips, true);
    private static ArenaPvpCombatant Wizard(int slot, bool ally, params ArenaModifier[] modifiers)
        => new(slot, ally, "Fire", 1000, 1000, 4, modifiers);

    [Fact]
    public void NativeRandomOutcomeIsOneShieldedHitRatherThanThreeConsecutiveHits() {
        var card = Card(Template(new RandomSpellEffect { m_effectList = [Damage(10), Damage(100), Damage(1000)] }));
        var result = ArenaPvpBrain.DamageTo(card, Wizard(0, true), Wizard(4, false,
            new(2, ArenaModifierKind.IncomingDamage, "Fire", -50)));
        Assert.Equal(148, result.Immediate, 6); // mean370 *80%accuracy *50%shield
        Assert.Equal(3, card.DamageBranches!.Count);
        Assert.All(card.DamageBranches, b => Assert.Single(b.Parts));
    }

    [Fact]
    public void NativeEffectListKeepsConsecutiveHitsAndDotTotalsAreNotImmediate() {
        var card = Card(Template(new EffectListSpellEffect { m_effectList = [Damage(100), Damage(100)] },
            Damage(300, kind: kSpellEffects.kDamageOverTime)));
        var hit = ArenaPvpBrain.DamageTo(card, Wizard(0, true), Wizard(4, false,
            new(2, ArenaModifierKind.IncomingDamage, "Fire", -50)));
        Assert.Equal(120, hit.Immediate, 6);
        Assert.Equal(360, hit.Total, 6);
    }

    [Fact]
    public void NativeVariableSpellChoosesTheExactHighestEligiblePipTier() {
        var template = Template(new VariableSpellEffect { m_effectList = [Damage(100, 1), Damage(300, 3), Damage(500, 5)] });
        template.m_spellRank.m_xPipSpell = true;
        Assert.Equal(300, Assert.Single(Card(template, 4).Damage).Amount);
        Assert.Equal(4, Card(template, 4).Pips);
        Assert.Equal(100, Assert.Single(Card(template, 1).Damage).Amount);
        var perPip = Card(Template(Damage(100, kind: kSpellEffects.kDamagePerTotalPipPower)), 4);
        Assert.Equal(400, Assert.Single(perPip.Damage).Amount);
    }

    [Fact]
    public void PositiveDirectHealAndSelfOnlyTargetsAreSeparateFromHotAndSelfDamageCost() {
        var hot = Card(Template(new SpellEffect { m_effectType = kSpellEffects.kHeal, m_effectParam = 0, m_effectTarget = kEffectTarget.kFriendlySingle },
            new SpellEffect { m_effectType = kSpellEffects.kHealOverTime, m_effectParam = 300, m_effectTarget = kEffectTarget.kFriendlySingle }));
        Assert.Equal(0, hot.DirectHeal);
        Assert.True(hot.HealOverTime);
        var sacrifice = Card(Template(Damage(250, target: kEffectTarget.kSelf),
            new SpellEffect { m_effectType = kSpellEffects.kHeal, m_effectParam = 700, m_effectTarget = kEffectTarget.kFriendlySingle }));
        Assert.Equal(ArenaCardRole.Heal, sacrifice.Role);
        Assert.Equal(250, sacrifice.SelfDamage);
        Assert.Equal(700, sacrifice.DirectHeal);
        Assert.False(sacrifice.SelfOnly);
        var self = Card(Template(new SpellEffect { m_effectType = kSpellEffects.kHeal, m_effectParam = 400, m_effectTarget = kEffectTarget.kSelf }));
        Assert.True(self.SelfOnly);
        Assert.Equal(400, self.DirectHeal);
    }

    [Fact]
    public void PowerPipsFollowActualSchoolMasteryForNativeCastEligibility() {
        var circle = (CombatDuelSubCircle) System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CombatDuelSubCircle));
        var stats = (ServerWizGameStats) System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_schoolID = (uint) MagicSchool.Fire;
        var participant = new CombatParticipant { m_pipCount = new PipCount { m_genericPips = 0, m_powerPips = 2 }, m_pGameStats = new WizGameStats() };
        Set("ParticipantGameStats", stats); Set("CombatParticipant", participant);
        var spell = SpellFactory.GetSpell(Template(Damage(100)), 1);
        spell.m_pipCost.m_spellRank = 3;
        Assert.True(circle.HasPipsForSpell(spell));
        spell.m_magicSchoolID = (uint) MagicSchool.Life;
        Assert.False(circle.HasPipsForSpell(spell));
        stats.m_lifeMastery = 1;
        Assert.True(circle.HasPipsForSpell(spell));
        void Set(string name, object value) => typeof(CombatDuelSubCircle).GetProperty(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(circle, value);
    }

    [Fact]
    public void NativeProtectedAndTreasureCardsCannotBeChosenForDiscard() {
        var template = Template(new SpellEffect { m_effectType = kSpellEffects.kReshuffle });
        template.m_noDiscard = true;
        Assert.False(Card(template).Discardable);
        template.m_noDiscard = false; template.m_Treasure = true;
        Assert.False(Card(template).Discardable);
    }
}
