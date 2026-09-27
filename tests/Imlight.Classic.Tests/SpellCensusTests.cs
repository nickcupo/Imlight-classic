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
 * ClassicSpellOverrides finds records by template path, then by name, and
 * the census totals what the plans changed and skipped.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Immutable;
using System.Linq;
using Imlight.Classic.Rules;
using Imlight.Classic.Spells;
using Xunit;
using static Imlight.Classic.Tests.SpellFixture;

namespace Imlight.Classic.Tests;

public sealed class SpellCensusTests {

    private static ClassicSpellRecord Named(string id, string name, string? template, SpellPips pips, double accuracy,
                                            string[]? profiles = null, params SpellEffectValues[] effects)
        => new() {
            Id = id,
            Name = name,
            School = "fire",
            Kind = "trained",
            ClientTemplate = template,
            Profiles = [.. profiles ?? ["late-2009"]],
            Values = new SpellValues(pips, accuracy, null, null, null, [.. effects]),
            SourceFile = id,
        };

    private static readonly ClassicSpellBook s_book = new("spells", [
        Named("spell.fire.fire_cat", "Fire Cat", "Spells/Tiered Spells/Fire Cat.xml", SpellPips.Of(1), 0.75, null,
            Effect(SpellEffectKind.Damage, min: 80, max: 120)),
        Named("spell.fire.krokomummy", "Krokomummy", "Spells/TreasureCards/Krokomummy TC.xml", SpellPips.Of(3), 0.75, null,
            Effect(SpellEffectKind.Damage, min: 375), Effect(SpellEffectKind.Stun, rounds: 1)),
        Named("spell.fire.link", "Link", "Spells/Link.xml", SpellPips.Of(2), 0.75, ["late-2009"],
            Effect(SpellEffectKind.Dot, min: 180, rounds: 3)),
        Named("spell.fire.lost", "Lost Card", null, SpellPips.Of(1), 1.0, ["arc1-2009h1"]),
    ]);

    private static ClassicProfile ProfileWithId(string id)
        => new() {
            Id = id,
            Title = id,
            Status = ProfileStatus.Debug,
            Features = FeatureSwitches.None,
            Rules = ProfileRules.None,
            SourceFiles = [],
        };

    private static ClassicSpellOverrides Overrides(AccuracyTable? table = null)
        => new(s_book, ProfileWithId("test"), table);

    [Fact]
    public void FindsByTemplatePathFirstThenByName() {
        var overrides = Overrides();

        Assert.Equal(SpellMatch.ClientTemplate, overrides.Find("Spells/TreasureCards/Krokomummy TC.xml", "Krokomummy TC")!.Value.Match);
        Assert.Equal((s_book.Records[1], SpellMatch.Name), overrides.Find("Spells/Krokomummy.xml", "Krokomummy")!.Value);
        Assert.Null(overrides.Find("Spells/Fire Cat TC.xml", "Fire Cat TC"));
        Assert.Null(overrides.PlanFor(Shape(1, 75) with { Path = "Spells/Other.xml", Name = "Other" }));
    }

    [Fact]
    public void SummaryTotalsTheTemplates() {
        var overrides = Overrides(new AccuracyTable {
            Id = "accuracy-test",
            Profiles = ["test"],
            Schools = ImmutableDictionary<string, SchoolAccuracy>.Empty
                .Add("fire", new SchoolAccuracy(0.75, ImmutableDictionary<string, double>.Empty, 0.75)),
            SourceFile = "rules/accuracy-test.yaml",
        });
        var census = new SpellOverrideCensus();
        void Add(SpellTemplateShape shape) => census.Add(shape, "Fire", overrides.PlanFor(shape));

        Add(Shape(1, 75, Random(Plain(TemplateEffectKind.Damage, 80), Plain(TemplateEffectKind.Damage, 120)))
            with { Path = "Spells/Tiered Spells/Fire Cat.xml", Name = "Fire Cat" });
        Add(Shape(3, 80, Plain(TemplateEffectKind.Damage, 310), Plain(TemplateEffectKind.Other, 1))
            with { Path = "Spells/Krokomummy.xml", Name = "Krokomummy" });
        Add(Shape(2, 75, Plain(TemplateEffectKind.Damage, 30), Plain(TemplateEffectKind.DamageOverTime, 150, rounds: 4))
            with { Path = "Spells/Link.xml", Name = "Link" });
        Add(Shape(1, 70, Plain(TemplateEffectKind.Damage, 50)) with { Path = "Spells/Mob Fire.xml", Name = "Mob Fire" });
        Add(Shape(1, 75, Plain(TemplateEffectKind.Damage, 50)) with { Path = "Spells/Mob Fire 2.xml", Name = "Mob Fire 2" });
        Add(Shape(0, 100, Plain(TemplateEffectKind.ModifyIncomingDamage, -50)) with { Path = "Spells/Mob Shield.xml", Name = "Mob Shield" });
        // Counted once however often it loads.
        Add(Shape(1, 70, Plain(TemplateEffectKind.Damage, 50)) with { Path = "Spells/Mob Fire.xml", Name = "Mob Fire" });

        var summary = census.Summarize(overrides);

        Assert.Equal(6, summary.Templates);
        Assert.Equal(2, summary.MatchedByClientTemplate);
        Assert.Equal(1, summary.MatchedByName);
        Assert.Equal(3, summary.Unmatched);
        Assert.Equal(2, summary.ChangedTemplates);
        Assert.Equal(0, summary.PipsChanged);
        Assert.Equal(1, summary.AccuracyChanged);
        Assert.Equal(2, summary.EffectValuesChanged);
        Assert.Equal(1, summary.RoundsChanged);
        Assert.Equal(2, summary.UnmatchedTemplateEffects);
        Assert.Equal(new[] { "spell.fire.lost" }, summary.RecordsWithoutTemplate);
        Assert.Equal(new[] { "spell.fire.fire_cat", "spell.fire.krokomummy", "spell.fire.link", "spell.fire.lost" }, summary.RecordsNotInProfile);
        Assert.Equal("damage 2, dot 1", summary.DescribeApplied());
        Assert.Equal("stun 1", summary.DescribeSkipped(EffectSkipReason.KindNotApplied));
        Assert.Equal("none", summary.DescribeSkipped(EffectSkipReason.NoValues));
        Assert.Equal(2, summary.UnmatchedDamageTemplates);
        Assert.Equal(1, summary.UnmatchedDamageOffSchoolBase);
    }

    [Fact]
    public void RecordsNotInTheProfileAreListed() {
        var late = new ClassicSpellOverrides(s_book, ProfileWithId("late-2009"));

        Assert.Equal(new[] { "spell.fire.lost" }, late.RecordsNotInProfile.Select(record => record.Id));
        Assert.Equal(new[] { "late-2009" }, late.Lineage);
    }

}
