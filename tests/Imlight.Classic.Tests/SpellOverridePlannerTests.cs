/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * CLASSIC SPELL VALUES TESTS
 * ========================================================================
 *
 * PURPOSE:
 * SpellOverridePlanner pairs classic effects with template effects the way
 * the real templates need (ranges, X cards, multi-school spells, drains)
 * and refuses the pairings that would change what an effect means.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 * The shapes are hand-made; the cases mirror real cards named in each test.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Linq;
using Imlight.Classic.Spells;
using Xunit;
using static Imlight.Classic.Tests.SpellFixture;

namespace Imlight.Classic.Tests;

public sealed class SpellOverridePlannerTests {

    [Fact]
    public void UnchangedTemplateNeedsNoChanges() {
        var shape = Shape(1, 75, Random(
            Plain(TemplateEffectKind.Damage, 80), Plain(TemplateEffectKind.Damage, 90), Plain(TemplateEffectKind.Damage, 100),
            Plain(TemplateEffectKind.Damage, 110), Plain(TemplateEffectKind.Damage, 120)));

        var plan = Plan(shape, SpellPips.Of(1), 0.75, Effect(SpellEffectKind.Damage, min: 80, max: 120));

        Assert.False(plan.ChangesTemplate);
        Assert.Equal(new[] { SpellEffectKind.Damage }, plan.AppliedEffects.ToArray());
        Assert.Empty(plan.SkippedEffects);
        Assert.Equal(0, plan.UnmatchedTemplateEffects);
    }

    [Fact]
    public void PipsAndAccuracyAreReplaced() {
        var shape = Shape(0, 100, Plain(TemplateEffectKind.Other, 1, damageType: "All")) with { SchoolPips = 1 };

        var plan = Plan(shape, SpellPips.Of(1), 0.75);

        Assert.Equal(1, plan.Rank);
        Assert.Equal(75, plan.Accuracy);
        Assert.True(plan.ClearSchoolPips);
        Assert.True(plan.ChangesTemplate);
        Assert.Equal(1, plan.UnmatchedTemplateEffects);
    }

    [Fact]
    public void XCostsOnlyMeetXTemplates() {
        var fixedTemplate = Shape(3, 100, Plain(TemplateEffectKind.Other, 1));
        var xTemplate = Shape(0, 100, PerPip(Plain(TemplateEffectKind.Damage, 100, pipNumber: 1))) with { IsXPip = true };

        Assert.Equal(PipsSkip.XOnFixedCostTemplate, Plan(fixedTemplate, SpellPips.X, 1.0).PipsSkip);
        Assert.Null(Plan(fixedTemplate, SpellPips.X, 1.0).Rank);
        Assert.Equal(PipsSkip.FixedOnXTemplate, Plan(xTemplate, SpellPips.Of(2), 1.0).PipsSkip);
        Assert.Equal(PipsSkip.None, Plan(xTemplate, SpellPips.X, 1.0).PipsSkip);
    }

    [Fact]
    public void RangeIsSpreadOverTheRolledChildrenLowestFirst() {
        // Colossus: 515..595 in the client, 460..540 in 2009; the children are listed out of order here.
        var shape = Shape(6, 80, Random(
            Plain(TemplateEffectKind.Damage, 555, damageType: "Ice"), Plain(TemplateEffectKind.Damage, 515, damageType: "Ice"),
            Plain(TemplateEffectKind.Damage, 595, damageType: "Ice"), Plain(TemplateEffectKind.Damage, 535, damageType: "Ice"),
            Plain(TemplateEffectKind.Damage, 575, damageType: "Ice")));

        var plan = Plan(shape, SpellPips.Of(6), 0.8, Effect(SpellEffectKind.Damage, "ice", 460, 540));

        Assert.Equal(new int?[] { 500, 460, 540, 480, 520 }, plan.EffectChanges.OrderBy(c => c.Address.Child).Select(c => c.Param).ToArray());
        Assert.All(plan.EffectChanges, change => Assert.Equal(0, change.Address.Index));
    }

    [Fact]
    public void AFixedAmountFillsEveryRolledChild() {
        // Wild Bolt: 10, 100 or 1000 in the client; always 1000 (at 10% accuracy) in 2009.
        var shape = Shape(2, 70, Random(
            Plain(TemplateEffectKind.Damage, 10, damageType: "Storm"), Plain(TemplateEffectKind.Damage, 100, damageType: "Storm"),
            Plain(TemplateEffectKind.Damage, 1000, damageType: "Storm")));

        var plan = Plan(shape, SpellPips.Of(2), 0.1, Effect(SpellEffectKind.Damage, "storm", 1000));

        Assert.Equal(10, plan.Accuracy);
        Assert.Equal(new[] { 0, 1 }, plan.EffectChanges.Select(c => c.Address.Child).ToArray());
        Assert.All(plan.EffectChanges, change => Assert.Equal(1000, change.Param));
    }

    [Fact]
    public void ARangeOnASingleValueUsesTheMean() {
        var plain = Shape(1, 80, Plain(TemplateEffectKind.Damage, 100, damageType: "Ice"));
        var oneChild = Shape(1, 80, Random(Plain(TemplateEffectKind.Damage, 100, damageType: "Ice")));

        Assert.Equal(85, Assert.Single(Plan(plain, SpellPips.Of(1), 0.8, Effect(SpellEffectKind.Damage, "ice", 65, 105)).EffectChanges).Param);
        Assert.Equal(85, Assert.Single(Plan(oneChild, SpellPips.Of(1), 0.8, Effect(SpellEffectKind.Damage, "ice", 65, 105)).EffectChanges).Param);
    }

    [Fact]
    public void XCardsScaleThePerPipAmountByTier() {
        // Heck Hound: 130 per pip over three rounds in the client, 120 in 2009.
        var shape = Shape(0, 75, PerPip(
            Plain(TemplateEffectKind.DamageOverTime, 130, rounds: 3, pipNumber: 1),
            Plain(TemplateEffectKind.DamageOverTime, 260, rounds: 3, pipNumber: 2),
            Plain(TemplateEffectKind.DamageOverTime, 390, rounds: 3, pipNumber: 3))) with { IsXPip = true };

        var plan = Plan(shape, SpellPips.X, 0.75, Effect(SpellEffectKind.Dot, min: 120, rounds: 3));

        Assert.Equal(new int?[] { 120, 240, 360 }, plan.EffectChanges.Select(c => c.Param).ToArray());
        Assert.All(plan.EffectChanges, change => Assert.Null(change.Rounds));
    }

    [Fact]
    public void OverTimeEffectsTakeTheirTotalAndRounds() {
        // Sprite: a 30 heal, then 280 over four rounds in the client; 270 over three in 2009.
        var shape = Shape(1, 90,
            Plain(TemplateEffectKind.Heal, 30, TemplateTarget.FriendlySingle, "Life"),
            Plain(TemplateEffectKind.HealOverTime, 280, TemplateTarget.FriendlySingle, "Life", rounds: 4));

        var plan = Plan(shape, SpellPips.Of(1), 0.9,
            Effect(SpellEffectKind.Heal, "life", 30),
            Effect(SpellEffectKind.Hot, "life", 270, rounds: 3));

        var change = Assert.Single(plan.EffectChanges);
        Assert.Equal(new EffectAddress(1), change.Address);
        Assert.Equal(270, change.Param);
        Assert.Equal(3, change.Rounds);
    }

    [Fact]
    public void DrainsTakeTheirHealShare() {
        var shape = Shape(4, 85, Plain(TemplateEffectKind.StealHealth, 335, damageType: "Death", healModifier: 0.5f));

        var plan = Plan(shape, SpellPips.Of(4), 0.85, Effect(SpellEffectKind.Steal, "death", 350, percent: 100));

        var change = Assert.Single(plan.EffectChanges);
        Assert.Equal(350, change.Param);
        Assert.Equal(1f, change.HealModifier);
    }

    [Fact]
    public void EachSchoolFindsItsOwnEffect() {
        // Hydra lists Fire, Ice and Storm hits; the record may list them in another order.
        var shape = Shape(6, 85,
            Plain(TemplateEffectKind.Damage, 230, damageType: "Fire"),
            Plain(TemplateEffectKind.Damage, 230, damageType: "Ice"),
            Plain(TemplateEffectKind.Damage, 230, damageType: "Storm"));

        var plan = Plan(shape, SpellPips.Of(6), 0.85,
            Effect(SpellEffectKind.Damage, "storm", 250),
            Effect(SpellEffectKind.Damage, "fire", 190),
            Effect(SpellEffectKind.Damage, "ice", 210));

        // The record lists the hits in card order, so the template's hits are put in that order.
        var after = SpellPlanSimulator.Apply(shape, plan);
        Assert.Equal(new[] { ("Storm", 250), ("Fire", 190), ("Ice", 210) }, after.Effects.Select(e => (e.DamageType, e.Param)).ToArray());
        Assert.Equal(new[] { new EffectAddress(2), new EffectAddress(0), new EffectAddress(1) }, plan.Structure!.Value.ToArray());
        Assert.Empty(plan.RemainingIssues);
    }

    [Fact]
    public void OneOfSeveralOutcomesIsMatchedChildByChild() {
        // Spectral Blast rolls one of three schools; each outcome is its own effect.
        var shape = Shape(5, 85, Random(
            Plain(TemplateEffectKind.Damage, 500, damageType: "Fire"),
            Plain(TemplateEffectKind.Damage, 400, damageType: "Ice"),
            Plain(TemplateEffectKind.Damage, 600, damageType: "Storm")));

        var plan = Plan(shape, SpellPips.Of(5), 0.85,
            Effect(SpellEffectKind.Damage, "fire", 440),
            Effect(SpellEffectKind.Damage, "ice", 365),
            Effect(SpellEffectKind.Damage, "storm", 550));

        Assert.Equal(new[] { (0, 0, 440), (0, 1, 365), (0, 2, 550) },
            plan.EffectChanges.Select(c => (c.Address.Index, c.Address.Child, c.Param!.Value)).ToArray());
    }

    [Fact]
    public void PercentsNeverFlipAnEffectsSign() {
        // A trap (+) must not land on a shield (-) of the same card, and the reverse.
        var shape = Shape(0, 100,
            Plain(TemplateEffectKind.ModifyIncomingDamage, -50, TemplateTarget.FriendlySingle, "All"),
            Plain(TemplateEffectKind.ModifyIncomingDamage, 30, TemplateTarget.EnemySingle, "All"));

        var plan = Plan(shape, SpellPips.Of(0), 1.0,
            Effect(SpellEffectKind.Trap, "all", percent: 25),
            Effect(SpellEffectKind.Shield, "all", percent: -40));

        Assert.Equal(new[] { (0, -40), (1, 25) },
            plan.EffectChanges.OrderBy(c => c.Address.Index).Select(c => (c.Address.Index, c.Param!.Value)).ToArray());
    }

    [Fact]
    public void AnAllSchoolTemplateEffectTakesASchoolCharm() {
        // Guidance: the record gives the card's school, the template's accuracy charm is school-agnostic.
        var shape = Shape(1, 100, Plain(TemplateEffectKind.ModifyAccuracy, 25, TemplateTarget.FriendlyTeam, "All"));

        var plan = Plan(shape, SpellPips.Of(1), 1.0, Effect(SpellEffectKind.Charm, "life", percent: 10, targets: SpellTargets.AllAllies));

        Assert.Equal(10, Assert.Single(plan.EffectChanges).Param);
    }

    [Fact]
    public void AGlobalOfAnotherMeaningIsRetyped() {
        // Power Play: a 2009 power pip bubble for everyone; the client's is a Balance damage bubble. The bubble
        // becomes the power pip chance bubble of the record.
        var shape = Shape(2, 100, Plain(TemplateEffectKind.ModifyOutgoingDamage, 25, TemplateTarget.Global, "Balance"));

        var plan = Plan(shape, SpellPips.Of(4), 1.0,
            Effect(SpellEffectKind.Global, "all", percent: 35, targets: null, notes: "bubble: +35% power pip chance for every combatant"));

        Assert.Equal(new EffectChange(new EffectAddress(0), Param: 35, EffectType: "kModifyPowerPipChance"), Assert.Single(plan.EffectChanges));
        Assert.Null(plan.Structure);
        Assert.Equal(4, plan.Rank);
        Assert.Empty(plan.RemainingIssues);
    }

    [Fact]
    public void AHealingBubbleMatchesWhateverItsSchool() {
        // Doom and Gloom: -50% to every heal; the client files the bubble under Life, the record under all.
        var shape = Shape(2, 100, Plain(TemplateEffectKind.ModifyOutgoingHeal, -35, TemplateTarget.Global, "Life"));

        var plan = Plan(shape, SpellPips.Of(3), 1.0,
            Effect(SpellEffectKind.Global, "all", percent: -50, targets: null, notes: "bubble: every healing spell heals 50% less"));

        Assert.Equal(new EffectChange(new EffectAddress(0), Param: -50), Assert.Single(plan.EffectChanges));
        Assert.Equal(0, plan.ZeroedTemplateEffects);
    }

    [Fact]
    public void AnUpFrontHitAndHealTheCardLackedAreRemoved() {
        // Link: 180 over 3 rounds and 120 back over 3 rounds, with no up-front hit or heal in 2009.
        var shape = Shape(2, 75,
            Plain(TemplateEffectKind.Damage, 30),
            Plain(TemplateEffectKind.DamageOverTime, 150, rounds: 3, pipNumber: 1),
            Plain(TemplateEffectKind.Heal, 15, TemplateTarget.Self, "Life"),
            Plain(TemplateEffectKind.HealOverTime, 105, TemplateTarget.Self, "Life", rounds: 3, pipNumber: 1));

        var plan = Plan(shape, SpellPips.Of(2), 0.75,
            Effect(SpellEffectKind.Dot, min: 180, rounds: 3),
            Effect(SpellEffectKind.Hot, min: 120, rounds: 3, targets: SpellTargets.Self));

        Assert.Equal(new[] { new EffectAddress(1), new EffectAddress(3) }, plan.Structure!.Value.ToArray());
        Assert.Equal(new int?[] { 180, 120 }, plan.EffectChanges.OrderBy(c => c.Address.Index).Select(c => c.Param).ToArray());
        Assert.Empty(plan.RemainingIssues);
    }

    [Fact]
    public void AnUpFrontHealBesideAHealOverTimeIsRemoved() {
        // Helping Hands: 540 over 3 rounds on an ally; the client adds a 120 heal up front.
        var shape = Shape(3, 85,
            Plain(TemplateEffectKind.Heal, 120, TemplateTarget.FriendlySingle, "Life"),
            Plain(TemplateEffectKind.HealOverTime, 720, TemplateTarget.FriendlySingle, "Life", rounds: 4));

        var plan = Plan(shape, SpellPips.Of(3), 1.0, Effect(SpellEffectKind.Hot, "balance", 540, rounds: 3));

        Assert.Equal(new[] { new EffectAddress(1) }, plan.Structure!.Value.ToArray());
        Assert.Equal(new EffectChange(new EffectAddress(0), Param: 540, Rounds: 3), Assert.Single(plan.EffectChanges));
    }

    [Fact]
    public void ALeftoverEffectIsKeptUnlessTheRecordClearlyLacksIt() {
        // A heal the record says nothing about is not zeroed beside the record's damage, and nothing is zeroed
        // while one of the record's own amounts found no template effect.
        var otherFamily = Shape(2, 75, Plain(TemplateEffectKind.Damage, 100), Plain(TemplateEffectKind.Heal, 50, TemplateTarget.Self, "Life"));
        var otherTargets = Shape(2, 75, Plain(TemplateEffectKind.Damage, 100), Plain(TemplateEffectKind.Damage, 50, TemplateTarget.Self));
        var unplaced = Shape(2, 75, Plain(TemplateEffectKind.Damage, 100), Plain(TemplateEffectKind.Damage, 50));

        Assert.Equal(0, Plan(otherFamily, SpellPips.Of(2), 0.75, Effect(SpellEffectKind.Damage, min: 120)).ZeroedTemplateEffects);
        Assert.Equal(0, Plan(otherTargets, SpellPips.Of(2), 0.75, Effect(SpellEffectKind.Damage, min: 120)).ZeroedTemplateEffects);
        Assert.Equal(0, Plan(unplaced, SpellPips.Of(2), 0.75,
            Effect(SpellEffectKind.Damage, min: 120), Effect(SpellEffectKind.Heal, min: 60, targets: SpellTargets.Self)).ZeroedTemplateEffects);
    }

    [Fact]
    public void ARolledOutcomeIsNeverZeroed() {
        // Spectral Blast rolls one of three schools; a record that places one outcome leaves the others alone.
        var shape = Shape(3, 80, Random(
            Plain(TemplateEffectKind.Damage, 300, damageType: "Fire"),
            Plain(TemplateEffectKind.Damage, 300, damageType: "Ice"),
            Plain(TemplateEffectKind.Damage, 300, damageType: "Storm")));

        var plan = Plan(shape, SpellPips.Of(3), 0.8, Effect(SpellEffectKind.Damage, "fire", 290, 330));

        Assert.Equal(0, plan.ZeroedTemplateEffects);
        Assert.Equal(2, plan.UnmatchedTemplateEffects);
    }

    [Fact]
    public void AShareOfMaxHealthBecomesTheRecordsFlatHit() {
        // Empower: the caster paid 500 health in 2009; the client charges 5% of max health.
        var shape = Shape(0, 100,
            Plain(TemplateEffectKind.MaxHealthDamage, 5, TemplateTarget.Self, "Death"),
            Plain(TemplateEffectKind.ModifyPips, 3, TemplateTarget.Self, "All"));

        var plan = Plan(shape, SpellPips.Of(1), 1.0,
            Effect(SpellEffectKind.Damage, "death", 500, targets: SpellTargets.Self),
            Effect(SpellEffectKind.Pip, "death", 3, targets: SpellTargets.Self));

        Assert.Equal(new EffectChange(new EffectAddress(0), Param: 500, Kind: TemplateEffectKind.Damage), Assert.Single(plan.EffectChanges));
        Assert.Empty(plan.SkippedEffects);
    }

    [Fact]
    public void AShareOfMaxHealthOnOtherTargetsBecomesTheRecordsHit() {
        // The values alone never turn an enemy's share of max health into the caster's own hit; the rebuilt card does.
        var shape = Shape(2, 100, Plain(TemplateEffectKind.MaxHealthDamage, 10, TemplateTarget.EnemySingle, "Death"));

        var plan = Plan(shape, SpellPips.Of(2), 1.0, Effect(SpellEffectKind.Damage, "death", 300, targets: SpellTargets.Self));

        Assert.Equal(new EffectChange(new EffectAddress(0), Param: 300, Target: TemplateTarget.Self, EffectType: "kDamage"),
            Assert.Single(plan.EffectChanges));
        Assert.Empty(plan.RemainingIssues);
    }

    [Fact]
    public void AmountsWaitForAPipCostThatFits() {
        // An X card's amount is per pip and a fixed card's is the whole effect; neither is written across.
        var fixedTemplate = Shape(3, 100, Plain(TemplateEffectKind.Damage, 300));
        var xTemplate = Shape(0, 100, PerPip(Plain(TemplateEffectKind.Damage, 100, pipNumber: 1), Plain(TemplateEffectKind.Damage, 200, pipNumber: 2)))
            with { IsXPip = true };

        var xOnFixed = Plan(fixedTemplate, SpellPips.X, 1.0, Effect(SpellEffectKind.Damage, min: 90));
        var fixedOnX = Plan(xTemplate, SpellPips.Of(2), 1.0, Effect(SpellEffectKind.Damage, min: 90));

        Assert.Empty(xOnFixed.EffectChanges);
        Assert.Equal(new SkippedEffect(SpellEffectKind.Damage, EffectSkipReason.PipsNotApplied), Assert.Single(xOnFixed.SkippedEffects));
        Assert.Empty(fixedOnX.EffectChanges);
        Assert.Equal(new SkippedEffect(SpellEffectKind.Damage, EffectSkipReason.PipsNotApplied), Assert.Single(fixedOnX.SkippedEffects));
    }

    [Fact]
    public void AnXCostOnAFixedTemplateChargesTheInheritedFixedCost() {
        // Taunt in arc1-2009h1: X, 2 in late-2009, 3 in the client.
        var shape = Shape(3, 100, Plain(TemplateEffectKind.Other, 1, TemplateTarget.Self, "Ice"));
        var record = Record(SpellPips.X, 1.0);

        var inherited = SpellOverridePlanner.Plan(record, SpellMatch.ClientTemplate, record.Values, shape, inheritedFixedPips: 2);
        var none = SpellOverridePlanner.Plan(record, SpellMatch.ClientTemplate, record.Values, shape);

        Assert.Equal(2, inherited.Rank);
        Assert.Equal(PipsSkip.XOnFixedCostTemplate, inherited.PipsSkip);
        Assert.True(inherited.PipsInherited);
        Assert.Null(none.Rank);
        Assert.False(none.PipsInherited);
    }

    [Fact]
    public void AmountsMatchAcrossTheTemplatesDamageType() {
        // Sap Health: a Balance card whose heal the client files under Life.
        var shape = Shape(1, 100,
            Plain(TemplateEffectKind.Other, 10000, TemplateTarget.MinionSingle, "Balance"),
            Plain(TemplateEffectKind.Heal, 450, TemplateTarget.Self, "Life"));

        var plan = Plan(shape, SpellPips.Of(1), 1.0, Effect(SpellEffectKind.Heal, "balance", 500, targets: SpellTargets.Self));

        var change = Assert.Single(plan.EffectChanges);
        Assert.Equal(new EffectAddress(1), change.Address);
        Assert.Equal(500, change.Param);
        Assert.Equal(1, plan.UnmatchedTemplateEffects);
    }

    [Fact]
    public void OrthrusBecomesTwoHitsOnOneEnemy() {
        // Orthrus: 50 then 650 Myth damage on one enemy in 2009, 700 to every enemy in the client. The values alone
        // cannot pair two hits with one; the rebuilt list copies the area hit twice and narrows both copies.
        var shape = Shape(7, 80, Plain(TemplateEffectKind.Damage, 700, TemplateTarget.EnemyTeam, "Myth"));

        var plan = Plan(shape, SpellPips.Of(7), 0.8,
            Effect(SpellEffectKind.Damage, "myth", 50, notes: "first hit"),
            Effect(SpellEffectKind.Damage, "myth", 650, notes: "second hit, same target"));

        Assert.Equal(new[] { new EffectAddress(0), new EffectAddress(0) }, plan.Structure!.Value.ToArray());
        Assert.Equal(new[] {
            new EffectChange(new EffectAddress(0), Param: 50, Target: TemplateTarget.EnemySingle),
            new EffectChange(new EffectAddress(1), Param: 650, Target: TemplateTarget.EnemySingle),
        }, plan.EffectChanges.ToArray());
        var after = SpellPlanSimulator.Apply(shape, plan);
        Assert.Equal(new[] { (50, TemplateTarget.EnemySingle), (650, TemplateTarget.EnemySingle) },
            after.Effects.Select(e => (e.Param, e.Target)).ToArray());
        Assert.Empty(plan.RemainingIssues);
    }

    [Fact]
    public void TargetsWidenAndNarrow() {
        // The overlay makes the client ask for the target a narrowed card needs, and the combat server picks one for a
        // client without it (CastTargeting), so a single target is written like any other.
        var single = Shape(2, 100, Plain(TemplateEffectKind.ModifyOutgoingDamage, -25, TemplateTarget.EnemySingle, "All"));
        var area = Shape(2, 100, Plain(TemplateEffectKind.ModifyOutgoingDamage, -25, TemplateTarget.EnemyTeam, "All"));

        var widened = Plan(single, SpellPips.Of(2), 1.0, Effect(SpellEffectKind.Charm, "all", percent: -25, targets: SpellTargets.AllEnemies));
        var narrowed = Plan(area, SpellPips.Of(2), 1.0, Effect(SpellEffectKind.Charm, "all", percent: -25, targets: SpellTargets.Single));

        Assert.Equal(TemplateTarget.EnemyTeam, Assert.Single(widened.EffectChanges).Target);
        Assert.Null(Assert.Single(widened.EffectChanges).Param);
        Assert.Equal(TemplateTarget.EnemySingle, Assert.Single(narrowed.EffectChanges).Target);
        Assert.Empty(narrowed.RemainingIssues);
    }

    [Fact]
    public void KindsTheServerCannotUseAreReported() {
        var shape = Shape(2, 80, Plain(TemplateEffectKind.Other, 1, damageType: "Fire"));

        var plan = Plan(shape, SpellPips.Of(2), 0.8,
            Effect(SpellEffectKind.Stun, rounds: 1, targets: SpellTargets.AllEnemies),
            Effect(SpellEffectKind.Minion, targets: SpellTargets.Self),
            Effect(SpellEffectKind.Steal, targets: SpellTargets.Single));

        Assert.Equal(new[] {
            new SkippedEffect(SpellEffectKind.Stun, EffectSkipReason.KindNotApplied),
            new SkippedEffect(SpellEffectKind.Minion, EffectSkipReason.KindNotApplied),
            new SkippedEffect(SpellEffectKind.Steal, EffectSkipReason.NoValues),
        }, plan.SkippedEffects.ToArray());
        Assert.Empty(plan.AppliedEffects);
    }

    [Fact]
    public void PipDrainsKeepTheirSign() {
        var shape = Shape(1, 100, Plain(TemplateEffectKind.ModifyPips, -1, TemplateTarget.EnemySingle, "All"));

        var plan = Plan(shape, SpellPips.Of(1), 1.0, Effect(SpellEffectKind.Pip, "fire", 2));

        Assert.Equal(-2, Assert.Single(plan.EffectChanges).Param);
    }

    [Fact]
    public void AppliedKindsAreTheOverriddenSet() {
        Assert.Equal(new[] {
            SpellEffectKind.Damage, SpellEffectKind.Dot, SpellEffectKind.Heal, SpellEffectKind.Hot, SpellEffectKind.Steal,
            SpellEffectKind.Pip, SpellEffectKind.Blade, SpellEffectKind.Charm, SpellEffectKind.Trap, SpellEffectKind.Shield,
            SpellEffectKind.Ward, SpellEffectKind.Global, SpellEffectKind.StunResist, SpellEffectKind.CriticalBlock,
        }, SpellOverridePlanner.AppliedKinds.ToArray());
    }

}
