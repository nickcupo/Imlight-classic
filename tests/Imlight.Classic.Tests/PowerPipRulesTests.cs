using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Classic.Spells;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class PowerPipRulesTests {
    [Theory]
    [InlineData(0f, 0f, 0, 0)]
    [InlineData(.2f, .1f, 0, 30)]
    [InlineData(.2f, .1f, 35, 65)]
    [InlineData(0f, 0f, 35, 35)]
    [InlineData(.8f, .1f, 35, 100)]
    [InlineData(.1f, 0f, -35, 0)]
    public void ProductionDecisionUsesAdditivePercentagePointsAndClamps(float baseChance, float gear, int global, int wins) {
        var participant = Participant(baseChance, gear);
        var duel = Battlefield();
        if (global != 0) ReplaceGlobal(duel, PowerPlay(global));
        // Exhaust every percentile midpoint: deterministic probability coverage, not a statistical/flaky test.
        Assert.Equal(wins, Enumerable.Range(0, 100).Count(i =>
            CombatDuelSubCircle.DeterminePowerPipGain(participant, duel, (i + .5) / 100.0)));
        Assert.Equal(baseChance, participant.m_pGameStats.m_powerPipBase);
        Assert.Equal(gear, participant.m_pGameStats.m_powerPipBonusPercentAll);
    }

    [Fact]
    public void ExactRollBoundariesHaveNoFreeSuccessAtZeroOrAtTheThreshold() {
        var participant = Participant(0, 0);
        var duel = Battlefield();
        Assert.False(CombatDuelSubCircle.DeterminePowerPipGain(participant, duel, 0));
        ReplaceGlobal(duel, PowerPlay());
        Assert.True(CombatDuelSubCircle.DeterminePowerPipGain(participant, duel, Math.BitDecrement(.35)));
        Assert.False(CombatDuelSubCircle.DeterminePowerPipGain(participant, duel, .35));
        Assert.False(CombatDuelSubCircle.DeterminePowerPipGain(participant, duel, .99));
        participant.m_pGameStats.m_powerPipBase = 1;
        Assert.True(CombatDuelSubCircle.DeterminePowerPipGain(participant, duel, Math.BitDecrement(1.0)));
    }

    [Fact]
    public void ActualGlobalReplacementRemovesTheBonusAndRecastsDoNotStack() {
        var participant = Participant(.25f, 0);
        var duel = Battlefield();
        Assert.False(Decide(.5));
        ReplaceGlobal(duel, PowerPlay());
        Assert.True(Decide(.5));
        ReplaceGlobal(duel, PowerPlay());
        Assert.Single(duel.m_duelModifier.m_battlefieldEffects);
        Assert.False(Decide(.7)); // 60%, not 95%
        ReplaceGlobal(duel, new SpellEffect {
            m_effectType = kSpellEffects.kModifyOutgoingDamage, m_effectParam = 35,
            m_effectTarget = kEffectTarget.kGlobal, m_sDamageType = "Fire",
        });
        Assert.False(Decide(.5));
        Assert.True(Decide(.2));
        ReplaceGlobal(duel, new SpellEffect {
            m_effectType = kSpellEffects.kModifyOutgoingHeal, m_effectParam = 50, m_effectTarget = kEffectTarget.kGlobal,
        });
        Assert.False(Decide(.5));
        ReplaceGlobal(duel, PowerPlay());
        Assert.True(Decide(.5));
        Assert.Equal(.25f, participant.m_pGameStats.m_powerPipBase);
        bool Decide(double roll) => CombatDuelSubCircle.DeterminePowerPipGain(participant, duel, roll);
    }

    [Fact]
    public void GlobalAffectsEveryParticipantWithoutDependingOnBubbleSchool() {
        var duel = Battlefield();
        var global = PowerPlay();
        global.m_sDamageType = "Balance";
        ReplaceGlobal(duel, global);
        foreach (float baseChance in new[] { 0f, .1f, .25f, .5f }) {
            var participant = Participant(baseChance, 0);
            Assert.True(CombatDuelSubCircle.DeterminePowerPipGain(participant, duel, baseChance + .3));
            Assert.False(CombatDuelSubCircle.DeterminePowerPipGain(participant, duel, baseChance + .4));
        }
    }

    [Fact]
    public void MissingBattlefieldModifierMeansNoGlobal() {
        var participant = Participant(.25f, .125f);
        var duel = new Duel();
        Assert.True(CombatDuelSubCircle.DeterminePowerPipGain(participant, duel, .374));
        Assert.False(CombatDuelSubCircle.DeterminePowerPipGain(participant, duel, .375));
    }

    [Theory]
    [InlineData(-.001)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    public void InvalidInjectedRollsAreRejected(double roll)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PowerPipRules.GainsPowerPip(0, 0, 35, roll));

    [Theory]
    [InlineData(0, 0, false, 1, 0)]
    [InlineData(0, 0, true, 0, 1)]
    [InlineData(6, 0, true, 6, 1)]
    [InlineData(3, 4, true, 3, 4)]
    [InlineData(7, 0, true, 7, 0)]
    [InlineData(0, 7, false, 0, 7)]
    public void ActualPipGainPreservesOnePipPerRoundAndTheSevenPipCap(int generic, int power, bool guaranteedPower, int expectedGeneric, int expectedPower) {
        var actor = new CombatDuelComponent(null!);
        var duel = Battlefield();
        Set(actor, "Duel", duel);
        var participant = Participant(0, 0);
        // Endpoint cases deterministically exercise the real RNG path in DoPipGain without faking random results.
        if (guaranteedPower) ReplaceGlobal(duel, PowerPlay(100));
        participant.m_pipCount = new PipCount { m_genericPips = (byte) generic, m_powerPips = (byte) power };
        var circle = new CombatDuelSubCircle(actor, 0, 0, default, 4);
        Set(circle, "CombatParticipant", participant);
        circle.DoPipGain();
        Assert.Equal(expectedGeneric, (int) participant.m_pipCount.m_genericPips);
        Assert.Equal(expectedPower, (int) participant.m_pipCount.m_powerPips);
    }

    [Fact]
    public void RealClassicPowerPlayTemplateFlowsThroughGlobalReplacementIntoProductionPipDecision() {
        var record = ClassicSpellLoader.Load(System.IO.Path.Combine(ClassicDataFixture.Root, "spells")).FindByName("Power Play")!;
        var overrides = new ClassicSpellOverrides(new ClassicSpellBook("spells", [record]), ClassicDataFixture.LoadProfile("late-2009"));
        var template = new SpellTemplate {
            m_name = "Power Play", m_accuracy = 100, m_spellRank = new SpellRank { m_spellRank = 2 },
            m_effects = [new SpellEffect {
                m_effectType = kSpellEffects.kModifyOutgoingDamage, m_effectParam = 25,
                m_effectTarget = kEffectTarget.kGlobal, m_sDamageType = "Balance",
            }],
        };
        var plan = overrides.PlanFor(SpellTemplateEditor.ShapeOf(template, "Spells/Power Play.xml"))!;
        SpellTemplateEditor.ApplyPlan(template, plan);
        var effect = Assert.Single(template.m_effects);
        Assert.Equal(kSpellEffects.kModifyPowerPipChance, effect.m_effectType);
        Assert.Equal(35, effect.m_effectParam);
        var duel = Battlefield();
        ReplaceGlobal(duel, effect);
        Assert.True(CombatDuelSubCircle.DeterminePowerPipGain(Participant(.25f, 0), duel, .59));
        Assert.False(CombatDuelSubCircle.DeterminePowerPipGain(Participant(.25f, 0), duel, .60));
    }

    [Theory]
    [InlineData(MagicSchool.Fire, false, 1)]
    [InlineData(MagicSchool.Ice, false, 0)]
    [InlineData(MagicSchool.Ice, true, 1)]
    public void GlobalDoesNotChangeSchoolMasteryOrPowerPipSpending(MagicSchool spellSchool, bool iceMastery, int genericLeft) {
        var actor = new CombatDuelComponent(null!);
        var duel = Battlefield();
        ReplaceGlobal(duel, PowerPlay());
        Set(actor, "Duel", duel);
        var circle = new CombatDuelSubCircle(actor, 0, 0, default, 4);
        var participant = Participant(.25f, 0);
        participant.m_pipCount = new PipCount { m_genericPips = 1, m_powerPips = 1 };
        Set(circle, "CombatParticipant", participant);
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_schoolID = (uint) MagicSchool.Fire;
        stats.m_iceMastery = iceMastery ? 1 : 0;
        Set(circle, "ParticipantGameStats", stats);
        circle.DeductPips(spellSchool, 2);
        Assert.Equal(genericLeft, (int) participant.m_pipCount.m_genericPips);
        Assert.Equal(0, (int) participant.m_pipCount.m_powerPips);
    }

    private static CombatParticipant Participant(float baseChance, float gear) {
        return new CombatParticipant { m_pGameStats = new WizGameStats {
            m_powerPipBase = baseChance, m_powerPipBonusPercentAll = gear,
        } };
    }

    private static Duel Battlefield() => new() { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } };
    private static SpellEffect PowerPlay(int percent = 35) => new() {
        m_effectType = kSpellEffects.kModifyPowerPipChance, m_effectTarget = kEffectTarget.kGlobal,
        m_effectParam = percent, m_sDamageType = "All",
    };

    private static void ReplaceGlobal(Duel duel, SpellEffect effect) {
        // Invoke the real replacement implementation, rather than simulate list clearing in the test.
        typeof(CombatEffectApplicator).GetMethod("ApplyGlobalEffect", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [effect, duel]);
    }

    private static void Set(object instance, string property, object value)
        => instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(instance, value);
}
