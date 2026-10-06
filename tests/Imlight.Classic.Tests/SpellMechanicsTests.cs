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
 * CLASSIC SPELL MECHANICS TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Every class of 2009 mechanic the classic pass writes beyond plain numbers:
 * a changed target (area to one enemy, self to an ally), a rebuilt effect
 * list (two hits for one, an added weakness, a dropped up-front heal or
 * duplicate stun block), a retyped effect, a changed school; the audit that
 * checks them; the server's copy of a rebuilt list; the Treasure Card match;
 * and the target combat picks when the client sends none.
 *
 * USAGE EXAMPLE:
 * W101C_REQUIRE_CLASSIC_DATA=1 dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 * The r806919 shapes here are copied from the client templates the audit
 * reads (tools/overlay-wad dump), numbers only.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Spells;
using Imlight.CoreLib.Classic;
using Xunit;
using static Imlight.Classic.Tests.SpellFixture;

namespace Imlight.Classic.Tests;

public sealed class SpellMechanicsTests {

    private static TemplateEffectNode Typed(string effectType, int param, TemplateTarget target, string damageType = "Fire", int rounds = 0)
        => Plain(SpellTemplateMapping.KindOf(effectType), param, target, damageType, rounds) with {
            EffectType = effectType,
            TargetName = SpellTemplateMapping.EffectTargetName(target) ?? "",
        };

    private static SpellOverridePlan PlanFor(ClassicSpellRecord record, SpellTemplateShape shape)
        => SpellOverridePlanner.Plan(record, SpellMatch.ClientTemplate, record.Values, shape);

    private static ClassicSpellRecord RecordOf(string school, SpellPips pips, double accuracy, params SpellEffectValues[] effects)
        => Record(pips, accuracy, effects) is var record ? new ClassicSpellRecord {
            Id = record.Id, Name = record.Name, School = school, Kind = record.Kind, ClientTemplate = record.ClientTemplate,
            Profiles = record.Profiles, Values = record.Values, SourceFile = record.SourceFile,
        } : null!;

    // ---- the audit ----

    [Fact]
    public void TheAuditFlagsOrthrusAsTheClientShipsIt() {
        var shape = Shape(7, 80, Typed("kDamage", 700, TemplateTarget.EnemyTeam, "Myth"));
        var values = new SpellValues(SpellPips.Of(7), 0.8, 48, null, null, [
            Effect(SpellEffectKind.Damage, "myth", 50), Effect(SpellEffectKind.Damage, "myth", 650),
        ]);

        var issues = SpellMechanicsAudit.Compare(values, shape, "myth");

        Assert.Contains(issues, issue => issue.Kind == MechanicsIssueKind.Target);
        Assert.Contains(issues, issue => issue.Kind == MechanicsIssueKind.Amount);
        Assert.Contains(issues, issue => issue.Kind == MechanicsIssueKind.MissingEffect);
    }

    [Fact]
    public void TheAuditCountsAZeroedEffectAsStillCast() {
        var shape = Shape(3, 100, Typed("kHeal", 0, TemplateTarget.FriendlySingle, "Life"),
            Typed("kHealOverTime", 540, TemplateTarget.FriendlySingle, "Life", rounds: 3));
        var values = new SpellValues(SpellPips.Of(3), 1.0, null, null, null, [Effect(SpellEffectKind.Hot, "balance", 540, rounds: 3)]);

        var issue = Assert.Single(SpellMechanicsAudit.Compare(values, shape, "balance"));

        Assert.Equal(MechanicsIssueKind.ExtraEffect, issue.Kind);
        Assert.Contains("zeroed", issue.Detail);
    }

    [Fact]
    public void TheAuditChecksTheOrderOfHits() {
        var shape = Shape(6, 85, Typed("kDamage", 190, TemplateTarget.EnemySingle, "Ice"), Typed("kDamage", 190, TemplateTarget.EnemySingle, "Fire"));
        var values = new SpellValues(SpellPips.Of(6), 0.85, null, null, null, [
            Effect(SpellEffectKind.Damage, "fire", 190), Effect(SpellEffectKind.Damage, "ice", 190),
        ]);

        Assert.Equal(MechanicsIssueKind.HitOrder, Assert.Single(SpellMechanicsAudit.Compare(values, shape, "balance")).Kind);
    }

    [Fact]
    public void TheAuditReadsTheRecordsWayOfSayingThings() {
        // An enchantment is cast on a card; an any-school charm names the card's school; a sacrifice kills the minion;
        // a roll between schools is one outcome per record effect.
        var enchant = Shape(0, 100, Typed("kModifyCardAccuracy", 15, TemplateTarget.Other, "Fire") with { TargetName = "kSpell" });
        var charm = Shape(1, 100, Typed("kModifyAccuracy", -40, TemplateTarget.EnemyTeam, "All"));
        var sacrifice = Shape(1, 100, Typed("kInstantKill", 10000, TemplateTarget.MinionSingle, "Ice") with { TargetName = "kMinion" },
            Typed("kModifyPips", 3, TemplateTarget.Self, "Myth"));
        var blast = Shape(4, 85, Random(Typed("kDamage", 440, TemplateTarget.EnemySingle, "Fire"),
            Typed("kDamage", 365, TemplateTarget.EnemySingle, "Ice"), Typed("kDamage", 550, TemplateTarget.EnemySingle, "Storm")));

        Assert.Empty(SpellMechanicsAudit.Compare(new SpellValues(SpellPips.Of(0), 1.0, null, null, null,
            [Effect(SpellEffectKind.Enchant, "balance", percent: 15)]), enchant, "balance"));
        Assert.Empty(SpellMechanicsAudit.Compare(new SpellValues(SpellPips.Of(1), 1.0, null, null, null,
            [Effect(SpellEffectKind.Charm, "fire", percent: -40, targets: SpellTargets.AllEnemies, notes: "accuracy: each enemy's next spell")]), charm, "fire"));
        Assert.Empty(SpellMechanicsAudit.Compare(new SpellValues(SpellPips.Of(1), 1.0, null, null, null,
            [Effect(SpellEffectKind.Pip, "fire", 3, targets: SpellTargets.Self, notes: "sacrifices the caster's own minion to gain 3 pips")]), sacrifice, "fire"));
        Assert.Empty(SpellMechanicsAudit.Compare(new SpellValues(SpellPips.Of(4), 0.85, null, null, null, [
            Effect(SpellEffectKind.Damage, "fire", 440), Effect(SpellEffectKind.Damage, "ice", 365), Effect(SpellEffectKind.Damage, "storm", 550),
        ]), blast, "balance"));
    }

    // ---- the rebuilt effect list ----

    [Fact]
    public void AMissingEffectIsACopyOfAPlainOneRetyped() {
        // Power Nova: 470 to every enemy and a -25% weakness on each in 2009; the client has the hit only.
        var shape = Shape(7, 85, Typed("kDamage", 665, TemplateTarget.EnemyTeam, "Balance"));
        var record = RecordOf("balance", SpellPips.Of(7), 0.85,
            Effect(SpellEffectKind.Damage, "balance", 470, targets: SpellTargets.AllEnemies),
            Effect(SpellEffectKind.Charm, "all", percent: -25, targets: SpellTargets.AllEnemies, notes: "weakness on every enemy"));

        var plan = PlanFor(record, shape);
        var after = SpellPlanSimulator.Apply(shape, plan);

        Assert.Equal(new[] { new EffectAddress(0), new EffectAddress(0) }, plan.Structure!.Value.ToArray());
        Assert.Equal(new[] { ("kDamage", 470, "Balance", TemplateTarget.EnemyTeam), ("kModifyOutgoingDamage", -25, "All", TemplateTarget.EnemyTeam) },
            after.Effects.Select(e => (e.EffectTypeName, e.Param, e.DamageType, e.Target)).ToArray());
        Assert.Empty(plan.RemainingIssues);
    }

    [Fact]
    public void AConditionalContainerTheCardLackedIsDropped() {
        // Immolate: 250 to the caster and 600 to the enemy in 2009; the client's self-hit sits in a condition list.
        var inner = new TemplateEffectNode {
            Composition = TemplateComposition.Other, EffectType = "kInvalidSpellEffect", ClassName = "ConditionalSpellEffect",
            Children = [Typed("kDamage", 200, TemplateTarget.Self), Typed("kDamage", 200, TemplateTarget.Self)],
        };
        var list = new TemplateEffectNode {
            Composition = TemplateComposition.Other, EffectType = "kInvalidSpellEffect", ClassName = "EffectListSpellEffect", Children = [inner],
        };
        var shape = Shape(4, 75, list, Typed("kDamage", 600, TemplateTarget.EnemySingle));
        var record = RecordOf("fire", SpellPips.Of(4), 0.75,
            Effect(SpellEffectKind.Damage, "fire", 250, targets: SpellTargets.Self), Effect(SpellEffectKind.Damage, "fire", 600));

        var plan = PlanFor(record, shape);
        var after = SpellPlanSimulator.Apply(shape, plan);

        Assert.Equal(new[] { (250, TemplateTarget.Self), (600, TemplateTarget.EnemySingle) }, after.Effects.Select(e => (e.Param, e.Target)).ToArray());
        Assert.Empty(plan.RemainingIssues);
    }

    [Fact]
    public void ADuplicateEffectIsDroppedAndAForeignOneKept() {
        // Stun Block gave one block in 2009, two in the client; a sacrifice card keeps its minion kill.
        var stunBlock = Shape(0, 100, Typed("kStunBlock", 1, TemplateTarget.FriendlySingle, "All"), Typed("kStunBlock", 1, TemplateTarget.FriendlySingle, "All"));
        var record = RecordOf("ice", SpellPips.Of(0), 1.0, Effect(SpellEffectKind.Ward, "all", targets: SpellTargets.Single, notes: "stun block: one shield"));

        var plan = PlanFor(record, stunBlock);

        Assert.Equal(new[] { new EffectAddress(0) }, plan.Structure!.Value.ToArray());
        Assert.Empty(plan.RemainingIssues);

        var sacrifice = Shape(1, 100, Typed("kInstantKill", 10000, TemplateTarget.MinionSingle, "Ice") with { TargetName = "kMinion" },
            Typed("kModifyPips", 4, TemplateTarget.Self, "Myth"));
        var drain = PlanFor(RecordOf("fire", SpellPips.Of(1), 1.0,
            Effect(SpellEffectKind.Pip, "fire", 3, targets: SpellTargets.Self, notes: "sacrifices the caster's own minion to gain 3 pips")), sacrifice);

        Assert.Null(drain.Structure);
        Assert.Equal(new EffectChange(new EffectAddress(1), Param: 3), Assert.Single(drain.EffectChanges));
    }

    [Fact]
    public void ThreatCardsGetTheirTargetsAndTypes() {
        // Taunt draws every enemy onto the caster; Pacify protects an ally; Distract taunts one enemy.
        var self = (string type) => Shape(2, 100, Typed(type, 1, TemplateTarget.Self, "All", rounds: 1));

        var taunt = PlanFor(RecordOf("ice", SpellPips.Of(2), 1.0,
            Effect(SpellEffectKind.Threat, "ice", targets: SpellTargets.AllEnemies, notes: "draws every enemy's attention onto the caster")), self("kTaunt"));
        var pacify = PlanFor(RecordOf("death", SpellPips.Of(2), 1.0,
            Effect(SpellEffectKind.Threat, "death", notes: "lowers threat so enemies are less likely to pick the target")), self("kPacify"));
        var distract = PlanFor(RecordOf("ice", SpellPips.Of(2), 1.0,
            Effect(SpellEffectKind.Threat, "ice", notes: "cast on an enemy: raises the caster's threat")), self("kPacify"));

        Assert.Equal(new EffectChange(new EffectAddress(0), Target: TemplateTarget.EnemyTeam), Assert.Single(taunt.EffectChanges));
        Assert.Equal(new EffectChange(new EffectAddress(0), Target: TemplateTarget.FriendlySingle), Assert.Single(pacify.EffectChanges));
        Assert.Equal(new EffectChange(new EffectAddress(0), Target: TemplateTarget.EnemySingle, EffectType: "kTaunt"), Assert.Single(distract.EffectChanges));
    }

    [Fact]
    public void ARecordOfAnotherKindDoesNotClaimATreasureCardByName() {
        // r806919's damage-dealing "Fire Elemental" Treasure Card is not the 2009 Fire Elemental minion's.
        var record = new ClassicSpellRecord {
            Id = "spell.fire.fire_elemental", Name = "Fire Elemental", School = "fire", Kind = "trained",
            ClientTemplate = "Spells/Minion Fire.xml", Profiles = ["late-2009"], SourceFile = "fire/fire-elemental.yaml",
            Values = new SpellValues(SpellPips.Of(3), 1.0, null, null, null, [Effect(SpellEffectKind.Minion, targets: SpellTargets.Self)]),
        };
        var overrides = new ClassicSpellOverrides(new ClassicSpellBook("spells", [record]), ClassicDataFixture.LoadProfile("late-2009"));
        var damageTc = Shape(3, 75, Random(Typed("kDamage", 345, TemplateTarget.EnemySingle), Typed("kDamage", 405, TemplateTarget.EnemySingle)))
            with { Path = "Spells/TreasureCards/Fire Elemental.xml", Name = "Fire Elemental TC" };
        var minionTc = Shape(3, 100, Typed("kSummonCreature", 35677, TemplateTarget.Self))
            with { Path = "Spells/TreasureCards/Minion Fire TC.xml", Name = "Minion Fire TC" };

        Assert.Null(overrides.Match(damageTc));
        Assert.Null(overrides.PlanFor(damageTc));
        Assert.Equal(SpellMatch.TreasureCard, overrides.Match(minionTc)?.Match);
    }

    [Fact]
    public void TheRealOrthrusRecordRebuildsTheClientsAreaCard() {
        var record = ClassicSpellLoader.Load(System.IO.Path.Combine(ClassicDataFixture.Root, "spells")).FindByName("Orthrus")!;
        var overrides = new ClassicSpellOverrides(new ClassicSpellBook("spells", [record]), ClassicDataFixture.LoadProfile("late-2009"));
        var shape = Shape(7, 80, Typed("kDamage", 700, TemplateTarget.EnemyTeam, "Myth") with { TargetName = "kEnemyTeamAllAtOnce" })
            with { Path = "Spells/Tiered Spells/Orthrus.xml", Name = "Orthrus" };

        var plan = overrides.PlanFor(shape)!;
        var after = SpellPlanSimulator.Apply(shape, plan);

        Assert.Equal(new[] { ("kDamage", 50, "Myth", "kEnemySingle"), ("kDamage", 650, "Myth", "kEnemySingle") },
            after.Effects.Select(e => (e.EffectTypeName, e.Param, e.DamageType, e.TargetMemberName)).ToArray());
        Assert.Empty(plan.RemainingIssues);
    }

    // ---- the server's copy ----

    [Fact]
    public void TheServerCopiesEachRebuiltEffect() {
        var template = new SpellTemplate {
            m_name = "Orthrus",
            m_accuracy = 80,
            m_spellRank = new SpellRank { m_spellRank = 7 },
            m_effects = [new SpellEffect {
                m_effectType = kSpellEffects.kDamage, m_effectParam = 700, m_sDamageType = "Myth", m_effectTarget = kEffectTarget.kEnemyTeamAllAtOnce,
            }],
        };
        var record = RecordOf("myth", SpellPips.Of(7), 0.8, Effect(SpellEffectKind.Damage, "myth", 50), Effect(SpellEffectKind.Damage, "myth", 650));
        var shape = SpellTemplateEditor.ShapeOf(template, "Spells/Tiered Spells/Orthrus.xml");

        var plan = PlanFor(record, shape);
        SpellTemplateEditor.ApplyPlan(template, plan);

        Assert.Equal(2, template.m_effects.Count);
        Assert.NotSame(template.m_effects[0], template.m_effects[1]);
        Assert.Equal(new[] { (50, kEffectTarget.kEnemySingle, "Myth"), (650, kEffectTarget.kEnemySingle, "Myth") },
            template.m_effects.Select(e => (e.m_effectParam, e.m_effectTarget, e.m_sDamageType)).ToArray());
        Assert.Equal(SpellPlanSimulator.Apply(shape, plan).Effects.Select(e => (e.Param, e.TargetMemberName)),
            SpellTemplateEditor.ShapeOf(template, "x").Effects.Select(e => (e.Param, e.TargetMemberName)));
    }

    [Fact]
    public void ACopiedRollHasItsOwnChildren() {
        var roll = new RandomSpellEffect {
            m_effectType = kSpellEffects.kInvalidSpellEffect,
            m_effectList = [new SpellEffect { m_effectType = kSpellEffects.kDamage, m_effectParam = 80 }],
        };

        var copy = (RandomSpellEffect) SpellTemplateEditor.Copy(roll);
        copy.m_effectList[0].m_effectParam = 120;

        Assert.Equal(80, roll.m_effectList[0].m_effectParam);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 100)]
    [InlineData(2, 1000)]
    public void OctoberWildBoltServerAndSimulatorKeepOneRollWithReplayedNativeChoices(int choice, int damage) {
        var original = new[] { 10, 100, 100, 1000 }.Select(value => new SpellEffect {
            m_effectType = kSpellEffects.kDamage, m_effectParam = value, m_sDamageType = "Storm",
            m_effectTarget = kEffectTarget.kEnemySingle,
        }).ToList();
        var template = new SpellTemplate {
            m_accuracy = 70, m_spellRank = new SpellRank { m_spellRank = 2 },
            m_effects = [new RandomSpellEffect {
                m_effectType = kSpellEffects.kInvalidSpellEffect, m_effectParam = -1, m_pipNum = 1,
                m_effectTarget = kEffectTarget.kEnemySingle, m_effectList = original,
            }],
        };
        var effect = Effect(SpellEffectKind.Damage, "storm", 10, 1000) with { Outcomes = [10, 100, 1000] };
        var record = RecordOf("storm", SpellPips.Of(2), 0.7, effect);
        var before = SpellTemplateEditor.ShapeOf(template, "Spells/Wild Bolt.xml");
        var plan = PlanFor(record, before);
        SpellTemplateEditor.ApplyPlan(template, plan);

        var roll = Assert.IsType<RandomSpellEffect>(Assert.Single(template.m_effects));
        Assert.Equal(new[] { 10, 100, 1000 }, roll.m_effectList.Select(child => child.m_effectParam).ToArray());
        Assert.Equal(-1, roll.m_effectParam);
        Assert.Equal(1, roll.m_pipNum);
        Assert.NotSame(original[3], roll.m_effectList[2]);
        Assert.Equal(SpellPlanSimulator.Apply(before, plan).Effects[0].Children.Select(child => child.Param),
            SpellTemplateEditor.ShapeOf(template, "Spells/Wild Bolt.xml").Effects[0].Children.Select(child => child.Param));

        // The existing resolver chooses and applies one child, and sends that same four-bit index to the client.
        var stack = new Imlight.CoreLib.Game.Combat.CombatEffectStack();
        var method = typeof(Imlight.CoreLib.Game.Combat.CombatActionResolver).GetMethod("ChooseRandomEffect",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var selected = Assert.IsType<SpellEffect>(method.Invoke(null, [roll, stack, new SelectedOutcomeRandom(choice)]));
        Assert.Equal(damage, selected.m_effectParam);
        Assert.Equal((uint) choice, stack.GetStackAsUint() & 15);
    }

    private sealed class SelectedOutcomeRandom(int choice) : System.Random {
        public override int Next(int minValue, int maxValue) {
            Assert.Equal(0, minValue);
            Assert.Equal(3, maxValue);
            return choice;
        }
    }

    // ---- the target combat uses ----

    [Fact]
    public void TheCardsFirstSingleTargetEffectDecidesItsSide() {
        Assert.Equal(CastTargetSide.Enemy, CastTargeting.SideOf(["kSelf", "kEnemySingle"]));
        Assert.Equal(CastTargetSide.Friend, CastTargeting.SideOf(["kFriendlySingle", "kSelf"]));
        Assert.Equal(CastTargetSide.None, CastTargeting.SideOf(["kEnemyTeamAllAtOnce"]));
        Assert.Equal(CastTargetSide.None, CastTargeting.SideOf([]));
    }

    [Fact]
    public void BeforeMay2010AnAllEnemySpellTakesAnEnemyTarget() {
        // May 2010 Update Notes: "Spells that attack all enemies will no longer require a target."
        Assert.Equal(CastTargetSide.Enemy, CastTargeting.SideOf(["kEnemyTeamAllAtOnce"], areaNeedsTarget: true));
        Assert.Equal(CastTargetSide.Enemy, CastTargeting.SideOf(["kEnemyTeam", "kSelf"], areaNeedsTarget: true));
        Assert.Equal(CastTargetSide.Friend, CastTargeting.SideOf(["kFriendlySingle", "kEnemyTeam"], areaNeedsTarget: true));
        // All-ally spells were not part of that change.
        Assert.Equal(CastTargetSide.None, CastTargeting.SideOf(["kFriendlyTeamAllAtOnce"], areaNeedsTarget: true));
        Assert.Equal(CastTargetSide.None, CastTargeting.SideOf(["kGlobal"], areaNeedsTarget: true));
    }

    [Fact]
    public void ACastWithoutAFittingTargetGetsOne() {
        var caster = new CastCircle(0, 0, true);
        var ally = new CastCircle(1, 0, true);
        var deadEnemy = new CastCircle(4, 1, false);
        var enemy = new CastCircle(5, 1, true);
        var enemy2 = new CastCircle(6, 1, true);
        List<CastCircle> circles = [caster, ally, deadEnemy, enemy, enemy2];

        // An unpatched client casts Orthrus with no target (the caster): the first living enemy takes it.
        Assert.Equal(5, CastTargeting.Choose(CastTargetSide.Enemy, caster, caster, circles));
        Assert.Equal(6, CastTargeting.Choose(CastTargetSide.Enemy, caster, enemy2, circles));
        Assert.Equal(5, CastTargeting.Choose(CastTargetSide.Enemy, caster, deadEnemy, circles));
        Assert.Equal(0, CastTargeting.Choose(CastTargetSide.Friend, caster, enemy, circles));
        Assert.Equal(1, CastTargeting.Choose(CastTargetSide.Friend, caster, ally, circles));
        Assert.Equal(0, CastTargeting.Choose(CastTargetSide.None, caster, caster, circles));
        Assert.Equal(0, CastTargeting.Choose(CastTargetSide.Enemy, caster, caster, [caster, ally, deadEnemy]));
    }

}
