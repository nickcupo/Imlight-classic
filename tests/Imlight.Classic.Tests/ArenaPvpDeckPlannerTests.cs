// CLASSIC: deck strategy cannot bypass researched profile/level eligibility or native deck limits.
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Imlight.Classic.Pvp;
using Imlight.Classic.Spells;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ArenaPvpDeckPlannerTests {
    private const string Profile = "october-2010-arc1";
    private static readonly ArenaDeckLimits Starter = new("", 14, 3, 3, -1, -1);
    private static ArenaDeckSpell Spell(uint id, string school = "Fire", int? level = 1, int rank = 1,
        int copies = 0, string kind = "trained", string? recordId = null, string[]? profiles = null,
        SpellEffectKind effect = SpellEffectKind.Damage, SpellTargets target = SpellTargets.Single) {
        var record = new ClassicSpellRecord { Id = recordId ?? $"fixture.{id}", Name = $"fixture {id}", School = school,
            Kind = kind, ClientTemplate = $"fixture/{id}.xml", Profiles = [.. (profiles ?? [Profile])], SourceFile = "test fixture",
            Values = new(SpellPips.Of(rank), 1, level, 0, "fixture", [new(effect, school, 100, 100, null, null, target, null)]) };
        return new(record, id, rank, copies);
    }

    [Theory]
    [InlineData("fire")][InlineData("ice")][InlineData("storm")][InlineData("myth")]
    [InlineData("life")][InlineData("death")][InlineData("balance")]
    public void EverySchoolAndLevelOneThroughFiftyHasOnlyKnownEligibleCards(string school) {
        var profile = ClassicDataFixture.LoadProfile(Profile);
        var records = ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "spells")).Records;
        // Template IDs/ranks here are fixture stand-ins; runtime separately rechecks the actual native templates.
        var available = records.Select((r, i) => new ArenaDeckSpell(r, (uint) (i + 1), r.ValuesFor(profile.Lineage).Pips.Fixed ?? 0, 0)).ToList();
        for (var level = 1; level <= 50; level++) {
            var plan = ArenaPvpDeckPlanner.Plan(available, school, level, Profile, profile.Lineage, Starter);
            Assert.NotEmpty(plan);
            Assert.InRange(plan.Sum(p => p.Copies), 1, 14);
            foreach (var entry in plan) {
                Assert.InRange(entry.Copies, 1, 3);
                Assert.True(entry.Spell.Record.IsInProfile(Profile));
                Assert.InRange(entry.Spell.Record.ValuesFor(profile.Lineage).LevelLearned!.Value, 1, level);
                Assert.True(entry.Spell.Record.Kind == "trained" && entry.Spell.Record.School.Equals(school, StringComparison.OrdinalIgnoreCase)
                    || entry.Spell.Record.Id == "spell.balance.reshuffle");
            }
            Assert.Contains(plan, p => p.Spell.Record.ValuesFor(profile.Lineage).Effects.Any(e => e.HasAmount
                && (e.Kind is SpellEffectKind.Damage or SpellEffectKind.Dot or SpellEffectKind.Steal) && e.Targets != SpellTargets.Self));
        }
    }

    [Fact]
    public void ProfileAndLevelFailClosedAndOnlyResearchedStandaloneCrossoverIsIncluded() {
        var spells = new[] { Spell(1), Spell(2, level: 51), Spell(3, level: null),
            Spell(4, profiles: ["dev-unrestricted"]), Spell(5, school: "Life"), Spell(6, kind: "item"),
            Spell(7, kind: "crossover", recordId: "unresearched.extra"),
            Spell(8, school: "Balance", level: 20, rank: 4, kind: "crossover", recordId: "spell.balance.reshuffle", effect: SpellEffectKind.Reshuffle),
            Spell(9) with { Supported = false } };
        var before = ArenaPvpDeckPlanner.Plan(spells, "Fire", 19, Profile, [Profile], Starter);
        Assert.Equal(1u, Assert.Single(before).Spell.TemplateId);
        var after = ArenaPvpDeckPlanner.Plan(spells, "Fire", 20, Profile, [Profile], Starter);
        Assert.Equal(new uint[] { 1, 8 }, after.Select(p => p.Spell.TemplateId).Order().ToArray());
        Assert.Empty(ArenaPvpDeckPlanner.Plan(spells, "Fire", 51, Profile, [Profile], Starter));
    }

    [Fact]
    public void RealDeckSchoolRankCopyAndCardOwnLimitsAllApply() {
        var limits = new ArenaDeckLimits("Fire", 8, 4, 2, 5, 3);
        var spells = new[] { Spell(1, copies: 1), Spell(2, rank: 5), Spell(3, rank: 6),
            Spell(4, school: "Balance", level: 20, rank: 4, kind: "crossover", recordId: "spell.balance.reshuffle", effect: SpellEffectKind.Reshuffle) };
        var plan = ArenaPvpDeckPlanner.Plan(spells, "Fire", 50, Profile, [Profile], limits);
        Assert.Equal(1, Assert.Single(plan, p => p.Spell.TemplateId == 1).Copies);
        Assert.Equal(4, Assert.Single(plan, p => p.Spell.TemplateId == 2).Copies);
        Assert.DoesNotContain(plan, p => p.Spell.TemplateId is 3 or 4);
        Assert.True(plan.Sum(p => p.Copies) <= limits.MaxCards);
    }

    [Fact]
    public void ProfileOverridesDetermineLearnedLevelAndDamageStrength() {
        var first = Spell(1, level: 40);
        var overridden = first with { Record = new ClassicSpellRecord { Id = first.Record.Id, Name = first.Record.Name,
            School = first.Record.School, Kind = first.Record.Kind, ClientTemplate = first.Record.ClientTemplate,
            Profiles = first.Record.Profiles, Values = first.Record.Values, SourceFile = first.Record.SourceFile,
            ProfileValues = ImmutableDictionary<string, SpellValuesOverride>.Empty.Add(Profile,
                new() { SetsLevelLearned = true, LevelLearned = 10 }) } };
        Assert.Empty(ArenaPvpDeckPlanner.Plan([first], "Fire", 10, Profile, [Profile], Starter));
        Assert.Single(ArenaPvpDeckPlanner.Plan([overridden], "Fire", 10, Profile, [Profile], Starter));
    }

    [Fact]
    public void DoesNotTreatSelfDamageCostAsAnEnemyAttack() {
        var selfCostOnly = Spell(1, effect: SpellEffectKind.Damage, target: SpellTargets.Self);
        Assert.Empty(ArenaPvpDeckPlanner.Plan([selfCostOnly], "Fire", 50, Profile, [Profile], Starter));
    }
}
