// CLASSIC: owner ruling 2026-10-16 ("new dark pact and old insane bolt"). Insane Bolt takes its 2011 card in
// wintertusk-2011 only: 2 pips, 100%, one roll per cast between 1000 Moon damage to the target and 10000 Moon damage to the
// caster on one cast in five (archived May 2011 notes: 20%; wiki 2011-11-12). The record is scoped (scoped: true), so every
// other profile keeps the r806919 card exactly; Dark Pact has no record and keeps the client card everywhere.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Spells;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.Common;
using Xunit;
using CombatResolver = Imlight.CoreLib.Game.Combat.CombatResolver;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class InsaneBolt2011Tests : IDisposable {

    private const string Path_ = "Spells/Insane Bolt.xml";
    private const string Id = "spell.storm.insane_bolt";
    private static readonly string[] s_otherProfiles = ["october-2010-arc1", "late-2009", "celestia-2010", "arc1-2009h1"];

    public InsaneBolt2011Tests() {
        ClassicRuntime.ResetForTests();
        var config = System.IO.Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={System.IO.Path.Combine(System.IO.Path.GetTempPath(), "imlight-insane-bolt-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    private static ClassicSpellBook Book() => ClassicSpellLoader.Load(System.IO.Path.Combine(ClassicDataFixture.Root, "spells"));

    private static ClassicSpellOverrides Overrides(string profile, ClassicSpellBook? book = null)
        => new(book ?? Book(), ClassicDataFixture.LoadProfile(profile));

    // ---------------------------------------------------------------- the record and its scope

    [Fact]
    public void TheRecordIsTheDated2011CardScopedToWintertusk() {
        var record = Assert.Single(Book().Records, r => r.Id == Id);
        Assert.Equal(Path_, record.ClientTemplate);
        Assert.True(record.Scoped);
        Assert.True(record.IsScoped);
        Assert.Equal(["wintertusk-2011"], record.Profiles);
        var values = record.ValuesFor(ClassicDataFixture.LoadProfile("wintertusk-2011").Lineage);
        Assert.Equal(2, values.Pips.Fixed);
        Assert.Equal(100, values.AccuracyPercent);
        Assert.Equal(55, values.LevelLearned);
        var effect = Assert.Single(values.Effects);
        Assert.Equal((SpellEffectKind.Damage, "moon", 1000, 1000, SpellTargets.Single),
            (effect.Kind, effect.School, effect.Min!.Value, effect.Max!.Value, effect.Targets!.Value));
        Assert.Equal(new SpellBackfire(0.2, 10000), effect.Backfire);
    }

    [Fact]
    public void NoOtherRecordIsScopedByFlagAndDarkPactHasNoRecord() {
        var book = Book();
        // CLASSIC: experiment records (School Mastery's bolts, scoped to the debug profile mastery-demo) are no historical card.
        string[] historical = ["late-2009", "arc1-2009h1", "october-2010-arc1", "celestia-2010", "wintertusk-2011"];
        Assert.Equal([Id], book.Records.Where(r => r.Scoped && historical.Any(r.IsInProfile)).Select(r => r.Id));
        Assert.DoesNotContain(book.Records, r => r.Name is "Dark Pact" or "Dark Tribute");
        Assert.DoesNotContain(book.Records, r => r.ClientTemplate == "Spells/Dark Pact.xml");
        // Wild Bolt is a different card and keeps its record exactly (not scoped, no backfire).
        var wild = Assert.Single(book.Records, r => r.Id == "spell.storm.wild_bolt");
        Assert.False(wild.IsScoped);
        Assert.All(wild.Values.Effects, e => Assert.Null(e.Backfire));
    }

    [Theory]
    [InlineData("october-2010-arc1")]
    [InlineData("late-2009")]
    [InlineData("celestia-2010")]
    [InlineData("arc1-2009h1")]
    public void OtherProfilesKeepTheClientsInsaneBoltCards(string profile) {
        var overrides = Overrides(profile);
        var record = overrides.Book.Records.Single(r => r.Id == Id);
        Assert.False(overrides.AppliesHere(record));
        Assert.False(overrides.IsTrainable(Path_));
        Assert.Null(overrides.PlanFor(ModernShape(Path_, "Insane Bolt")));
        Assert.Null(overrides.PlanFor(ModernShape("Spells/TreasureCards/Insane Bolt TC.xml", "Insane Bolt TC")));
        Assert.Null(overrides.PlanFor(OldMobShape()));
    }

    [Theory]
    [InlineData("october-2010-arc1")]
    [InlineData("late-2009")]
    [InlineData("celestia-2010")]
    [InlineData("arc1-2009h1")]
    public void OtherProfilesResolveEveryOtherRecordExactlyAsWithoutTheRecord(string profile) {
        // Golden: the book with and without the 2011 record gives every other card the same values, match and scope, so
        // these profiles' cards (server templates and the card overlay, which use the same planner) are unchanged.
        var with = Overrides(profile);
        var without = Overrides(profile, new ClassicSpellBook("spells", [.. with.Book.Records.Where(r => r.Id != Id)]));
        Assert.Equal(without.Book.Records.Where(without.AppliesHere).Select(r => r.Id),
            with.Book.Records.Where(with.AppliesHere).Select(r => r.Id));
        foreach (var record in without.Book.Records) {
            Assert.Equal(Describe(without.ValuesOf(record)), Describe(with.ValuesOf(record)));
            Assert.Equal(without.IsTrainable(record.ClientTemplate), with.IsTrainable(record.ClientTemplate));
            Assert.Equal(without.Find(record.ClientTemplate, record.Name)?.Record.Id, with.Find(record.ClientTemplate, record.Name)?.Record.Id);
        }

        foreach (var shape in new[] {
                     ModernShape(Path_, "Insane Bolt"), ModernShape("Spells/TreasureCards/Insane Bolt TC.xml", "Insane Bolt TC"), OldMobShape(),
                 }) {
            Assert.Null(without.PlanFor(shape));
            Assert.Null(with.PlanFor(shape));
        }
    }

    [Fact]
    public void AmbientStormWizardsHoldTheCardOnlyInWintertusk() {
        var book = Book();
        var record = book.Records.Single(r => r.Id == Id);
        foreach (var profile in s_otherProfiles) {
            ClassicRuntime.ResetForTests();
            ClassicRuntime.Initialize(ClassicDataFixture.RealRules(profile));
            Assert.False(AmbientCombat.InDeckPool(record, "storm", 60, profile), profile);
            // Every other record's place in the decks is as before (unscoped records ignore the profile).
            Assert.All(book.Records.Where(r => r.Id != Id),
                r => Assert.Equal(AmbientCombat.InDeckPool(r, r.School, 60, null), AmbientCombat.InDeckPool(r, r.School, 60, profile)));
        }

        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("wintertusk-2011"));
        Assert.True(AmbientCombat.InDeckPool(record, "storm", 55, "wintertusk-2011"));
        Assert.False(AmbientCombat.InDeckPool(record, "storm", 54, "wintertusk-2011"));
    }

    // ---------------------------------------------------------------- the 2011 card in wintertusk-2011

    [Fact]
    public void WintertuskBuildsTheTenChoiceRollFromTheClientsFirstRandomEffect() {
        var overrides = Overrides("wintertusk-2011");
        Assert.True(overrides.AppliesHere(overrides.Book.Records.Single(r => r.Id == Id)));
        var shape = ModernShape(Path_, "Insane Bolt");
        var plan = overrides.PlanFor(shape)!;
        Assert.Equal(SpellMatch.ClientTemplate, plan.Match);
        Assert.Empty(plan.RemainingIssues);
        Assert.Null(plan.Rank);
        Assert.Equal(100, plan.Accuracy);
        Assert.Equal([new EffectAddress(0)], plan.Structure!.Value);
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 4, 4], plan.RandomChildren[0]);

        var after = SpellPlanSimulator.Apply(shape, plan);
        Assert.Equal(100, after.Accuracy);
        Assert.Equal(2, after.Rank);
        var roll = Assert.Single(after.Effects);
        Assert.Equal(TemplateComposition.Random, roll.Composition);
        Assert.Equal(10, roll.Children.Length);
        Assert.All(roll.Children, child => Assert.Equal("Moon", child.DamageType));
        Assert.All(roll.Children, child => Assert.Equal(TemplateEffectKind.Damage, child.Kind));
        Assert.Equal(Enumerable.Repeat((TemplateTarget.EnemySingle, 1000), 8).Concat(Enumerable.Repeat((TemplateTarget.Self, 10000), 2)),
            roll.Children.Select(child => (child.Target, child.Param)));
        // The client text Spell_00001816 (the TC's pre-rework text) reads the first choice ($eA$) as the hit and the ninth ($eA9$) as the self-hit.
        Assert.Equal(TemplateTarget.EnemySingle, roll.Children[0].Target);
        Assert.Equal(TemplateTarget.Self, roll.Children[8].Target);
    }

    [Fact]
    public void TheEditorWritesThePlanIntoTheRealTemplateObjects() {
        var template = ModernTemplate();
        var plan = Overrides("wintertusk-2011").PlanFor(SpellTemplateEditor.ShapeOf(template, Path_))!;
        SpellTemplateEditor.ApplyPlan(template, plan);
        Assert.Equal(100, template.m_accuracy);
        var roll = Assert.IsType<RandomSpellEffect>(Assert.Single(template.m_effects));
        Assert.Equal(10, roll.m_effectList.Count);
        Assert.Equal(10, roll.m_effectList.Distinct(ReferenceEqualityComparer.Instance).Count()); // each choice its own object
        Assert.Equal(8, roll.m_effectList.Count(e => e.m_effectTarget == kEffectTarget.kEnemySingle && e.m_effectParam == 1000));
        Assert.Equal(2, roll.m_effectList.Count(e => e.m_effectTarget == kEffectTarget.kSelf && e.m_effectParam == 10000));
        Assert.All(roll.m_effectList, e => Assert.Equal("Moon", e.m_sDamageType));
        Assert.All(roll.m_effectList, e => Assert.Equal(kSpellEffects.kDamage, e.m_effectType));
    }

    [Fact]
    public void TheTreasureCardTakesTheSameCardAndTheOldCreatureCopyIsLeftAlone() {
        var overrides = Overrides("wintertusk-2011");
        var tc = overrides.PlanFor(ModernShape("Spells/TreasureCards/Insane Bolt TC.xml", "Insane Bolt TC"))!;
        Assert.Equal(SpellMatch.TreasureCard, tc.Match);
        Assert.Empty(tc.RemainingIssues);
        Assert.Null(overrides.PlanFor(OldMobShape()));
    }

    [Fact]
    public void ATemplateWithoutASelfHitRollKeepsItsEffectsAndTheAuditSaysSo() {
        var overrides = Overrides("wintertusk-2011");
        var plain = new SpellTemplateShape {
            Path = Path_, Name = "Insane Bolt", Rank = 2, Accuracy = 70,
            Effects = [Hit(TemplateTarget.EnemySingle, 145, "Storm")],
        };
        var plan = overrides.PlanFor(plain)!;
        Assert.Null(plan.Structure);
        Assert.Empty(plan.EffectChanges);
        Assert.Contains(plan.RemainingIssues, issue => issue.Kind == MechanicsIssueKind.Composition);
    }

    [Theory]
    [InlineData(0.2, 10, 2)]
    [InlineData(0.3, 10, 3)]
    [InlineData(0.25, 4, 1)]
    [InlineData(1.0 / 7, 7, 1)]
    public void BackfireChoicesGiveTheChanceExactly(double chance, int total, int self) {
        Assert.Equal((total, self), SpellOverridePlanner.BackfireChoices(chance));
    }

    [Fact]
    public void AChanceNoFifteenChoicesCanGiveIsRefused() => Assert.Null(SpellOverridePlanner.BackfireChoices(0.33));

    // ---------------------------------------------------------------- combat

    [Theory]
    [InlineData(0, 1000, 0)]   // a hit: 1000 Moon to the target
    [InlineData(7, 1000, 0)]
    [InlineData(8, 0, 10000)]  // a self-hit: 10000 Moon to the caster
    [InlineData(9, 0, 10000)]
    public void WintertuskCombatRollsOnceBetweenTheTargetAndTheCaster(int choice, int toTarget, int toCaster) {
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("wintertusk-2011"));
        var template = PlannedTemplate();
        var caster = Circle(30000);
        var target = Circle(30000);
        Rig(caster, choice);
        Resolve(caster, new QueuedCombatAction {
            SpellCaster = caster, SelectedTarget = target, Spell = new Spell { m_templateID = uint.MaxValue - 211, m_pipCost = new SpellRank(), m_accuracy = 100 }, SpellTemplate = template,
        });

        Assert.Equal(30000 - toTarget, target.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal(30000 - toCaster, caster.ParticipantGameStats.m_currentHitpoints);
    }

    [Fact]
    public void TheSelfHitCanDefeatTheCaster() {
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("wintertusk-2011"));
        var caster = Circle(5000);
        var target = Circle(5000);
        Rig(caster, 9);
        Resolve(caster, new QueuedCombatAction {
            SpellCaster = caster, SelectedTarget = target, Spell = new Spell { m_templateID = uint.MaxValue - 212, m_pipCost = new SpellRank(), m_accuracy = 100 }, SpellTemplate = PlannedTemplate(),
        });

        Assert.False(caster.IsAlive);
        Assert.Equal(5000, target.ParticipantGameStats.m_currentHitpoints);
    }

    [Fact]
    public void TheHitIsMoonDamageSoAStormShieldDoesNotTouchIt() {
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("wintertusk-2011"));
        var caster = Circle(30000);
        var target = Circle(30000);
        target._hangingEffects.Add(new SpellEffect {
            m_effectType = kSpellEffects.kModifyIncomingDamage, m_effectParam = -50, m_sDamageType = "Storm", m_spellTemplateID = 4242,
        });
        Rig(caster, 0);
        Resolve(caster, new QueuedCombatAction {
            SpellCaster = caster, SelectedTarget = target, Spell = new Spell { m_templateID = uint.MaxValue - 213, m_pipCost = new SpellRank(), m_accuracy = 100 }, SpellTemplate = PlannedTemplate(),
        });

        Assert.Equal(29000, target.ParticipantGameStats.m_currentHitpoints);
        Assert.Single(target._hangingEffects);
    }

    [Fact]
    public void OverManyCastsOneInFiveHitsTheCaster() {
        // The ten choices are equally likely (CombatActionResolver.ChooseRandomEffect), so the share is the record's.
        var template = PlannedTemplate();
        var roll = (RandomSpellEffect) template.m_effects[0];
        Assert.Equal(0.2, roll.m_effectList.Count(e => e.m_effectTarget == kEffectTarget.kSelf) / (double) roll.m_effectList.Count, 9);
    }

    [Fact]
    public void OutsideWintertuskTheClientCardIsCastUnchanged() {
        // The live profile's server template is the client's: three rolls of 145 Storm, 70%.
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("october-2010-arc1"));
        var template = ModernTemplate();
        Assert.Null(Overrides("october-2010-arc1").PlanFor(SpellTemplateEditor.ShapeOf(template, Path_)));
        Assert.Equal(3, template.m_effects.Count);
        Assert.Equal(70, template.m_accuracy);
    }

    // ---------------------------------------------------------------- helpers

    private static SpellTemplate PlannedTemplate() {
        var template = ModernTemplate();
        SpellTemplateEditor.ApplyPlan(template, Overrides("wintertusk-2011").PlanFor(SpellTemplateEditor.ShapeOf(template, Path_))!);
        return template;
    }

    private static string Describe(SpellValues values)
        => $"{values.Pips}|{values.AccuracyPercent}|{values.LevelLearned}|{string.Join(";", values.Effects.Select(e => $"{e.Kind},{e.School},{e.Min},{e.Max},{e.Percent},{e.Rounds},{e.Targets},{string.Join(",", e.Outcomes)},{e.Backfire}"))}";

    private static TemplateEffectNode Hit(TemplateTarget target, int param, string school) => new() {
        Composition = TemplateComposition.Plain, Kind = TemplateEffectKind.Damage, Target = target, Param = param, DamageType = school,
        EffectType = "kDamage",
    };

    private static TemplateEffectNode Roll(params TemplateEffectNode[] children) => new() {
        Composition = TemplateComposition.Random, Kind = TemplateEffectKind.Other, Target = TemplateTarget.EnemySingle, Param = -1,
        EffectType = "kInvalidSpellEffect", ClassName = "RandomSpellEffect", Children = [.. children],
    };

    /// <summary>r806919 Spells/Insane Bolt.xml (and its TC): 2 pips, 70%, three rolls of 145 Storm, one choice of each on the caster.</summary>
    private static SpellTemplateShape ModernShape(string path, string name) => new() {
        Path = path, Name = name, Rank = 2, Accuracy = 70,
        Effects = [
            Roll(Hit(TemplateTarget.EnemySingle, 145, "Storm"), Hit(TemplateTarget.EnemySingle, 145, "Storm"), Hit(TemplateTarget.EnemySingle, 145, "Storm"),
                Hit(TemplateTarget.EnemySingle, 145, "Storm"), Hit(TemplateTarget.Self, 145, "Storm")),
            Roll(Hit(TemplateTarget.EnemySingle, 145, "Storm"), Hit(TemplateTarget.EnemySingle, 145, "Storm"), Hit(TemplateTarget.EnemySingle, 145, "Storm"),
                Hit(TemplateTarget.EnemySingle, 145, "Storm"), Hit(TemplateTarget.Self, 145, "Storm")),
            Roll(Hit(TemplateTarget.EnemySingle, 145, "Storm"), Hit(TemplateTarget.EnemySingle, 145, "Storm"), Hit(TemplateTarget.EnemySingle, 145, "Storm"),
                Hit(TemplateTarget.Self, 145, "Storm")),
        ],
    };

    /// <summary>r806919 Spells/Insane Bolt - OLD - MOB ONLY.xml: 2 pips, 100%, one roll of seven 1125 hits and three 10000 self-hits, Moon.</summary>
    private static SpellTemplateShape OldMobShape() => new() {
        Path = "Spells/Insane Bolt - OLD - MOB ONLY.xml", Name = "Insane Bolt - OLD - MOB ONLY", Rank = 2, Accuracy = 100,
        Effects = [Roll([.. Enumerable.Repeat(Hit(TemplateTarget.EnemySingle, 1125, "Moon"), 7), .. Enumerable.Repeat(Hit(TemplateTarget.Self, 10000, "Moon"), 3)])],
    };

    private static SpellEffect Bolt(kEffectTarget target) => new() {
        m_effectType = kSpellEffects.kDamage, m_effectParam = 145, m_sDamageType = "Storm", m_effectTarget = target, m_act = true,
    };

    private static RandomSpellEffect RollEffect(int hits) => new() {
        m_effectType = kSpellEffects.kInvalidSpellEffect, m_effectParam = -1, m_effectTarget = kEffectTarget.kEnemySingle,
        m_effectList = [.. Enumerable.Range(0, hits).Select(_ => Bolt(kEffectTarget.kEnemySingle)), Bolt(kEffectTarget.kSelf)],
    };

    private static SpellTemplate ModernTemplate() => new() {
        m_name = "Insane Bolt", m_accuracy = 70, m_sMagicSchoolName = "Storm", m_spellRank = new SpellRank { m_spellRank = 2 },
        m_effects = [RollEffect(4), RollEffect(4), RollEffect(3)],
    };

    private sealed class FixedRandom(int choice) : Random {
        public override int Next(int minValue, int maxValue) => Math.Clamp(choice, minValue, Math.Max(minValue, maxValue - 1));
        public override int Next(int maxValue) => Math.Clamp(choice, 0, Math.Max(0, maxValue - 1));
        public override int Next() => choice;
        public override double NextDouble() => 0.999;
    }

    private static void Rig(CombatDuelSubCircle caster, int choice)
        => caster._duelActor.GetType().GetField("_rng", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(caster._duelActor, new FixedRandom(choice));

    private static void Resolve(CombatDuelSubCircle caster, QueuedCombatAction queued) {
        // The resolver looks the card's template up by id (X-pip check); serve this one from the template cache.
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<ulong, CoreTemplate>) typeof(Imlight.CoreLib.Shared.Resources.CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var id = (ulong) queued.Spell.m_templateID;
        cache[id] = queued.SpellTemplate;
        try {
            var action = new CombatAction { m_targetSubcircleList = [] };
            var time = 0f;
            Assert.True(CombatActionResolver.ProcessedQueuedCombatAction(queued, ref action, ref time));
        } finally {
            cache.TryRemove(id, out _);
        }
    }

    private static CombatDuelSubCircle Circle(int health) {
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
        var circle = duel.SubCircles[0];
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = stats.m_currentHitpoints = health;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant { m_hangingEffects = [] });
        return circle;
    }

}
