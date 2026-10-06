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
 * The real classic-data/spells records and rules/accuracy-2009.yaml load,
 * and resolve to the numbers the records and profiles promise.
 *
 * USAGE EXAMPLE:
 * W101C_REQUIRE_CLASSIC_DATA=1 dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 * Skipped when no classic-data directory is found above the test binary,
 * unless W101C_REQUIRE_CLASSIC_DATA=1 (see ClassicDataFixture).
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.IO;
using System.Linq;
using Imlight.Classic.Rules;
using Imlight.Classic.Spells;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class RealSpellsTests {

    private static string SpellsPath => Path.Combine(ClassicDataFixture.Root, "spells");

    private static ClassicSpellBook LoadBook() => ClassicSpellLoader.Load(SpellsPath);

    [Fact]
    public void EveryRecordLoads() {
        var book = LoadBook();
        var files = Directory.GetFiles(SpellsPath, "*.yaml", SearchOption.AllDirectories);

        Assert.Equal(files.Length, book.Records.Length);
        Assert.All(book.Records, record => Assert.Contains(record.School, ClassicSpellSchema.Schools));
        Assert.All(ClassicSpellSchema.PlayerSchools, school => Assert.Contains(book.Records, record => record.School == school));
    }

    [Fact]
    public void FireCat() {
        var record = LoadBook().FindByTemplate("Spells/Tiered Spells/Fire Cat.xml");

        Assert.NotNull(record);
        Assert.Equal("spell.fire.fire_cat", record.Id);
        Assert.Equal(SpellPips.Of(1), record.Values.Pips);
        Assert.Equal(75, record.Values.AccuracyPercent);
        Assert.Equal(new SpellEffectValues(SpellEffectKind.Damage, "fire", 80, 120, null, null, SpellTargets.Single, null),
            Assert.Single(record.Values.Effects));
    }

    [Fact]
    public void BalefrostHasItsPreUpdateNumbersInArc1() {
        var record = LoadBook().FindByName("Balefrost")!;
        var late = record.ValuesFor(ClassicDataFixture.LoadProfile("late-2009").Lineage);
        var arc1 = record.ValuesFor(ClassicDataFixture.LoadProfile("arc1-2009h1").Lineage);

        Assert.Equal(SpellPips.Of(2), late.Pips);
        Assert.Equal(35, Assert.Single(late.Effects).Percent);
        Assert.Equal(SpellPips.Of(4), arc1.Pips);
        Assert.Equal(25, Assert.Single(arc1.Effects).Percent);
    }

    [Fact]
    public void TauntIsAnXCardInArc1Only() {
        var record = LoadBook().FindByName("Taunt")!;

        Assert.Equal(SpellPips.Of(2), record.ValuesFor(ClassicDataFixture.LoadProfile("late-2009").Lineage).Pips);
        Assert.True(record.ValuesFor(ClassicDataFixture.LoadProfile("arc1-2009h1").Lineage).Pips.IsX);
    }

    [Fact]
    public void TauntChargesItsLate2009CostInArc1() {
        // The client's Taunt costs 3 and cannot be an X card, so arc1-2009h1 falls back to the 2 late-2009 charges.
        var overrides = new ClassicSpellOverrides(LoadBook(), ClassicDataFixture.LoadProfile("arc1-2009h1"));
        var shape = new SpellTemplateShape { Path = "Spells/Taunt.xml", Name = "Taunt", Rank = 3, Accuracy = 100 };

        var plan = overrides.PlanFor(shape)!;

        Assert.Equal(2, plan.Rank);
        Assert.True(plan.PipsInherited);
        Assert.Equal(PipsSkip.XOnFixedCostTemplate, plan.PipsSkip);
        Assert.Null(new ClassicSpellOverrides(LoadBook(), ClassicDataFixture.LoadProfile("late-2009")).InheritedFixedPips(plan.Record));
    }

    [Fact]
    public void TreasureCardsOfRecordedCardsFindTheirRecord() {
        var book = LoadBook();

        Assert.Equal("spell.ice.colossus", book.FindTreasureCardOf("Spells/TreasureCards/Colossus TC.xml", "Colossus TC")?.Id);
        Assert.Equal("spell.storm.wild_bolt", book.FindTreasureCardOf("Spells/TreasureCards/Wild Bolt TC.xml", "Wild Bolt TC")?.Id);
    }

    [Theory]
    [InlineData("late-2009")]
    [InlineData("october-2010-arc1")]
    public void OwnerSelectedWildBoltKeepsOriginalAccuracyAndFlatDamage(string profileId) {
        var overrides = new ClassicSpellOverrides(LoadBook(), ClassicDataFixture.LoadProfile(profileId));
        var record = overrides.Book.FindByTemplate("Spells/Wild Bolt.xml")!;
        var values = overrides.ValuesOf(record);
        Assert.Equal(0.1, values.Accuracy);
        Assert.Equal(1000, Assert.Single(values.Effects).Min);
        Assert.Equal(1000, Assert.Single(values.Effects).Max);
        Assert.Empty(Assert.Single(values.Effects).Outcomes);

        var shape = SpellFixture.Shape(2, 70, SpellFixture.Random([.. new[] { 10, 100, 100, 1000 }.Select(value =>
            SpellFixture.Plain(TemplateEffectKind.Damage, value, damageType: "Storm"))])) with {
            Path = "Spells/Wild Bolt.xml", Name = "Wild Bolt",
        };
        var plan = overrides.PlanFor(shape)!;
        var result = SpellPlanSimulator.Apply(shape, plan);
        Assert.Equal(10, result.Accuracy);
        Assert.All(Assert.Single(result.Effects).Children, child => Assert.Equal(1000, child.Param));
        Assert.Empty(plan.RandomChildren);
        Assert.Empty(plan.RemainingIssues);
    }

    [Fact]
    public void EveryNamedProfileExistsAndTheCanonicalOneIsTheSchemas() {
        var profiles = ClassicProfileLoader.LoadAll(ClassicDataFixture.ProfilesPath);
        var ids = profiles.Select(profile => profile.Id).ToHashSet();

        Assert.Equal(ClassicSpellSchema.CanonicalProfileId, Assert.Single(profiles, profile => profile.Status == ProfileStatus.Canonical).Id);
        foreach (var record in LoadBook().Records) {
            Assert.All(record.Profiles, id => Assert.Contains(id, ids));
        }
    }

    [Fact]
    public void VerifiedOctoberUtilitySpellIsAvailableOnlyInTheNewProfile() {
        var book = LoadBook();
        var october = new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile("october-2010-arc1"));
        var late = new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile("late-2009"));
        var early = new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile("arc1-2009h1"));
        var spell = book.FindByName("Strangle")!;
        Assert.Equal("Mildred Farseer", spell.Values.Trainer);
        Assert.Equal(SpellPips.Of(2), spell.Values.Pips);
        Assert.Equal(22, spell.Values.LevelLearned);
        Assert.True(october.IsTrainable(spell.ClientTemplate));
        Assert.False(late.IsTrainable(spell.ClientTemplate));
        Assert.False(early.IsTrainable(spell.ClientTemplate));
        Assert.True(october.IsTrainable("Spells/Tiered Spells/Fire Cat.xml"));
        Assert.False(october.IsTrainable("Spells/Entangle.xml"));
        Assert.False(october.IsTrainable("Spells/Vaporize.xml"));
        Assert.False(october.IsTrainable("Spells/Unbalance.xml"));
    }

    [Theory]
    [InlineData("Cloak", "sun", "Spells/Cloak.xml", 0)]
    public void ApprovedDiegoCardsAreOctoberOnly(string name, string school, string template, int level) {
        var book = LoadBook();
        var spell = book.FindByTemplate(template)!;
        Assert.Equal(name, spell.Name);
        Assert.Equal(school, spell.School);
        Assert.Equal("Diego the Duelmaster", spell.Values.Trainer);
        Assert.Equal(SpellPips.Of(0), spell.Values.Pips);
        Assert.Equal(level, spell.Values.LevelLearned);
        Assert.Equal(1, spell.Values.TrainingPoints);
        Assert.True(new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile("october-2010-arc1")).IsTrainable(template));
        Assert.False(new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile("late-2009")).IsTrainable(template));
        Assert.False(new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile("arc1-2009h1")).IsTrainable(template));
    }

    [Fact]
    public void CloakPlanPreservesTheClientCardTargetAndEffect() {
        var overrides = new ClassicSpellOverrides(LoadBook(), ClassicDataFixture.LoadProfile("october-2010-arc1"));
        var shape = new SpellTemplateShape {
            Path = "Spells/Cloak.xml", Name = "Cloak", Rank = 0, Accuracy = 100,
            Effects = [new TemplateEffectNode {
                EffectType = "kModifyCardCloak", TargetName = "kSpell", DamageType = "All", Param = 0,
            }],
        };

        var plan = overrides.PlanFor(shape)!;
        var result = SpellPlanSimulator.Apply(shape, plan);

        Assert.Empty(plan.RemainingIssues);
        Assert.False(plan.ChangesTemplate);
        Assert.Equal("kModifyCardCloak", Assert.Single(result.Effects).EffectTypeName);
        Assert.Equal("kSpell", Assert.Single(result.Effects).TargetMemberName);
    }

    [Fact]
    public void ConvictionIsWithheldWithTheCriticalSystem() {
        var book = LoadBook();
        var overrides = new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile("october-2010-arc1"));

        Assert.Null(book.FindByTemplate("Spells/Conviction.xml"));
        Assert.False(overrides.IsTrainable("Spells/Conviction.xml"));
        Assert.False(ClassicDataFixture.LoadProfile("october-2010-arc1").Features.IsEnabled(ClassicFeatures.CriticalAndBlock));
    }

    [Theory]
    [InlineData("late-2009")]
    [InlineData("october-2010-arc1")]
    public void UnresolvedOctoberNumericChangesDoNotBorrowTheLaterClientValues(string profileId) {
        var book = LoadBook();
        var profile = ClassicDataFixture.LoadProfile(profileId);
        var poison = book.FindByName("Poison")!.ValuesFor(profile.Lineage);
        var hound = book.FindByName("Heck Hound")!.ValuesFor(profile.Lineage);

        Assert.Equal(SpellPips.Of(4), poison.Pips);
        Assert.Equal(0.85, poison.Accuracy);
        Assert.Equal(35, poison.Effects[0].Min);
        Assert.Equal(390, poison.Effects[1].Min);
        Assert.Equal(3, poison.Effects[1].Rounds);
        Assert.Equal(SpellPips.X, hound.Pips);
        Assert.Equal(0.75, hound.Accuracy);
        Assert.Equal(120, Assert.Single(hound.Effects).Min);
        Assert.Equal(3, Assert.Single(hound.Effects).Rounds);
    }

    [Fact]
    public void TheCanonicalAccuracyTableLoads() {
        var profile = ClassicDataFixture.LoadProfile("late-2009");
        var table = AccuracyTableLoader.Load(Path.Combine(ClassicDataFixture.Root, profile.Rules.AccuracyTable!));

        Assert.Equal("accuracy-2009", table.Id);
        Assert.Contains("late-2009", table.Profiles);
        Assert.Equal(0.75, table.BaseAccuracy("fire", profile.Lineage));
        Assert.Equal(0.9, table.BaseAccuracy("life", profile.Lineage));
        Assert.All(ClassicSpellSchema.PlayerSchools, school => Assert.NotNull(table.BaseAccuracy(school, profile.Lineage)));
    }

    [Fact]
    public void ArcOneLineageListsItsParent() {
        Assert.Equal(new[] { "arc1-2009h1", "late-2009" }, ClassicDataFixture.LoadProfile("arc1-2009h1").Lineage);
        Assert.Equal(new[] { "late-2009" }, ClassicDataFixture.LoadProfile("late-2009").Lineage);
    }

}
