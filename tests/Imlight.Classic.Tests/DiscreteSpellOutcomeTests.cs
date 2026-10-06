// CLASSIC: discrete rolls stay one native random effect; the client and server share its child indices.
using System;
using System.Linq;
using Imlight.Classic.Spells;
using Xunit;
using static Imlight.Classic.Tests.SpellFixture;

namespace Imlight.Classic.Tests;

public sealed class DiscreteSpellOutcomeTests {

    private static SpellEffectValues OctoberDamage => Effect(SpellEffectKind.Damage, "storm", 10, 1000) with {
        Outcomes = [10, 100, 1000],
    };

    private static SpellTemplateShape Native(params int[] choices)
        => Shape(2, 70, Random([.. choices.Select(value => Plain(TemplateEffectKind.Damage, value, damageType: "Storm"))]));

    [Fact]
    public void ModernDuplicateChoiceIsRemovedWithoutAddingHitsOrInterpolating() {
        var before = Native(10, 100, 100, 1000);
        var plan = Plan(before, SpellPips.Of(2), 0.7, OctoberDamage);
        var after = SpellPlanSimulator.Apply(before, plan);

        Assert.Equal(new[] { 0, 1, 3 }, plan.RandomChildren[0].ToArray());
        Assert.Null(plan.Structure);
        var roll = Assert.Single(after.Effects);
        Assert.Equal(TemplateComposition.Random, roll.Composition);
        Assert.Equal(new[] { 10, 100, 1000 }, roll.Children.Select(child => child.Param).ToArray());
        Assert.Equal(70, after.Accuracy);
        Assert.Empty(plan.RemainingIssues);
        Assert.Empty(plan.EffectChanges);
        Assert.Equal(4, before.Effects[0].Children.Length);
        Assert.False(Plan(after, SpellPips.Of(2), 0.7, OctoberDamage).ChangesTemplate);
    }

    [Fact]
    public void ThreeNativeChoicesAlreadyCarryTheOctoberSpell() {
        var plan = Plan(Native(10, 100, 1000), SpellPips.Of(2), 0.7, OctoberDamage);
        Assert.False(plan.ChangesTemplate);
        Assert.Empty(plan.RandomChildren);
        Assert.Empty(plan.RemainingIssues);
    }

    [Fact]
    public void AuditDetectsDuplicateWeightAndWrongMiddleValueEvenWithCorrectBounds() {
        var values = Record(SpellPips.Of(2), 0.7, OctoberDamage).Values;
        Assert.Contains(SpellMechanicsAudit.Compare(values, Native(10, 100, 100, 1000), "storm"),
            issue => issue.Kind == MechanicsIssueKind.Amount);
        Assert.Contains(SpellMechanicsAudit.Compare(values, Native(10, 505, 1000), "storm"),
            issue => issue.Kind == MechanicsIssueKind.Amount);
    }

    [Fact]
    public void AContinuousRangeOrSeparateHitsCannotMasqueradeAsDiscreteChoices() {
        Assert.Throws<InvalidOperationException>(() => Plan(Native(10, 505, 1000), SpellPips.Of(2), 0.7, OctoberDamage));
        var hits = Shape(2, 70, [.. new[] { 10, 100, 1000 }.Select(value =>
            Plain(TemplateEffectKind.Damage, value, damageType: "Storm"))]);
        Assert.Throws<InvalidOperationException>(() => Plan(hits, SpellPips.Of(2), 0.7, OctoberDamage));
    }

    [Fact]
    public void OldProfileAndIndirectTreasureMatchKeepTheirPreviousOverrides() {
        var before = Native(15, 150, 150, 1150);
        var canonical = Record(SpellPips.Of(2), 0.1, Effect(SpellEffectKind.Damage, "storm", 1000));
        var october = canonical.Values with { Accuracy = 0.7, Effects = [OctoberDamage] };
        var old = SpellOverridePlanner.Plan(canonical, SpellMatch.ClientTemplate, canonical.Values, before);
        var tc = SpellOverridePlanner.Plan(canonical, SpellMatch.TreasureCard, october, before);

        foreach (var plan in new[] { old, tc }) {
            var after = SpellPlanSimulator.Apply(before, plan);
            Assert.Equal(10, after.Accuracy);
            Assert.Empty(plan.RandomChildren);
            Assert.Equal(new[] { 1000, 1000, 1000, 1000 }, Assert.Single(after.Effects).Children.Select(child => child.Param).ToArray());
        }
    }

    [Fact]
    public void LoaderReadsTheExplicitOutcomeList() {
        using var data = new TempClassicData();
        data.WriteSpell("fire", "fire-cat", RecordYaml(values: DefaultValues.Replace(
            "min: 80, max: 120", "min: 10, max: 1000, outcomes: [10, 100, 1000]")));
        var effect = Assert.Single(Assert.Single(ClassicSpellLoader.Load(data.SpellsPath()).Records).Values.Effects);
        Assert.Equal(new[] { 10, 100, 1000 }, effect.Outcomes.ToArray());
    }

    [Theory]
    [InlineData("[10, 100, 100]", "damage", 100)]
    [InlineData("[10]", "damage", 10)]
    [InlineData("[10, 100, 1000]", "heal", 1000)]
    [InlineData("[10, 100, 1000]", "damage", 999)]
    public void LoaderRejectsAmbiguousOrUnsupportedOutcomeLists(string outcomes, string kind, int max) {
        using var data = new TempClassicData();
        var yaml = DefaultValues.Replace("type: damage", $"type: {kind}")
            .Replace("min: 80, max: 120", $"min: 10, max: {max}, outcomes: {outcomes}");
        data.WriteSpell("fire", "fire-cat", RecordYaml(values: yaml));
        Assert.Throws<ClassicDataException>(() => ClassicSpellLoader.Load(data.SpellsPath()));
    }
}
