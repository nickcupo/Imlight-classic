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
 * ClassicSpellLoader reads valid records and reports every invalid field
 * with its file and key path; records resolve their profile_values.
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

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Imlight.Classic.Spells;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class SpellLoaderTests : IDisposable {

    private readonly TempClassicData _data = new();

    public void Dispose() => _data.Dispose();

    private ClassicSpellBook Load() => ClassicSpellLoader.Load(_data.SpellsPath());

    private ClassicDataError SingleError(string keyPath) => ZoneFixture.SingleError(() => Load(), keyPath);

    [Fact]
    public void LoadsAValidRecord() {
        _data.WriteSpell("fire", "fire-cat", SpellFixture.RecordYaml());

        var record = Assert.Single(Load().Records);

        Assert.Equal("spell.fire.fire_cat", record.Id);
        Assert.Equal("Fire Cat", record.Name);
        Assert.Equal("fire", record.School);
        Assert.Equal("trained", record.Kind);
        Assert.Equal("Spells/Tiered Spells/Fire Cat.xml", record.ClientTemplate);
        Assert.Equal(new[] { "late-2009", "arc1-2009h1" }, record.Profiles.ToArray());
        Assert.Equal(SpellPips.Of(1), record.Values.Pips);
        Assert.Equal(0.75, record.Values.Accuracy);
        Assert.Equal(75, record.Values.AccuracyPercent);
        Assert.Equal(1, record.Values.LevelLearned);
        Assert.Equal("Dalia Falmea", record.Values.Trainer);
        var effect = Assert.Single(record.Values.Effects);
        Assert.Equal(new SpellEffectValues(SpellEffectKind.Damage, "fire", 80, 120, null, null, SpellTargets.Single, null), effect);
        Assert.Empty(record.ProfileValues);
        Assert.Equal("classic-data/spells/fire/fire-cat.yaml", record.SourceFile);
    }

    [Fact]
    public void ReadsXPipsNullFieldsAndEveryEffectField() {
        _data.WriteSpell("storm", "tempest", SpellFixture.RecordYaml(school: "storm", slug: "tempest", name: "Tempest",
            clientTemplate: "null", values: """
                values:
                  pips: X
                  accuracy: 0.7
                  level_learned: null
                  training_points: null
                  trainer: null
                  effects:
                  - {type: damage, school: storm, min: 80, max: 80, targets: all_enemies, notes: per pip}
                  - {type: dot, school: storm, min: 90, max: 90, rounds: 3, targets: single}
                  - {type: charm, school: all, percent: -25, targets: all_enemies}
                  - {type: global, school: all, percent: 35}
                """));

        var record = Assert.Single(Load().Records);

        Assert.True(record.Values.Pips.IsX);
        Assert.Null(record.ClientTemplate);
        Assert.Null(record.Values.LevelLearned);
        Assert.Null(record.Values.TrainingPoints);
        Assert.Null(record.Values.Trainer);
        Assert.Equal("per pip", record.Values.Effects[0].Notes);
        Assert.Equal(SpellTargets.AllEnemies, record.Values.Effects[0].Targets);
        Assert.Equal(3, record.Values.Effects[1].Rounds);
        Assert.Equal(-25, record.Values.Effects[2].Percent);
        Assert.Equal(SpellEffectKind.Global, record.Values.Effects[3].Kind);
        Assert.Null(record.Values.Effects[3].Targets);
    }

    [Fact]
    public void ProfileValuesOverrideOnlyTheFieldsTheyName() {
        _data.WriteSpell("ice", "balefrost", SpellFixture.RecordYaml(school: "ice", slug: "balefrost", name: "Balefrost",
            clientTemplate: "Spells/Balefrost.xml", values: """
                values:
                  pips: 2
                  accuracy: 1.0
                  level_learned: 33
                  training_points: 1
                  trainer: Lydia Greyrose
                  effects:
                  - {type: global, school: ice, percent: 35}
                """, extra: """
                profile_values:
                  arc1-2009h1:
                    pips: 4
                    trainer: null
                    effects:
                    - {type: global, school: ice, percent: 25}
                """));

        var record = Assert.Single(Load().Records);
        var late = record.ValuesFor(["late-2009"]);
        var arc1 = record.ValuesFor(["arc1-2009h1", "late-2009"]);

        Assert.Same(record.Values, late);
        Assert.Equal(SpellPips.Of(4), arc1.Pips);
        Assert.Equal(1.0, arc1.Accuracy);
        Assert.Equal(33, arc1.LevelLearned);
        Assert.Null(arc1.Trainer);
        Assert.Equal(25, Assert.Single(arc1.Effects).Percent);
        Assert.Equal(35, Assert.Single(record.Values.Effects).Percent);
    }

    [Fact]
    public void TheNearestProfileInTheChainWins() {
        var record = SpellFixture.Record(SpellPips.Of(2), 1.0);
        var withOverrides = new ClassicSpellRecord {
            Id = record.Id,
            Name = record.Name,
            School = record.School,
            Kind = record.Kind,
            Profiles = ["late-2009", "arc1-2009h1", "arc1-strict"],
            Values = record.Values,
            SourceFile = record.SourceFile,
            ProfileValues = new[] {
                ("arc1-2009h1", new SpellValuesOverride { Pips = SpellPips.Of(4), Accuracy = 0.9 }),
                ("arc1-strict", new SpellValuesOverride { Pips = SpellPips.X }),
            }.ToImmutableDictionary(pair => pair.Item1, pair => pair.Item2),
        };

        var strict = withOverrides.ValuesFor(["arc1-strict", "arc1-2009h1", "late-2009"]);
        var unrelated = withOverrides.ValuesFor(["some-other"]);

        Assert.True(strict.Pips.IsX);
        Assert.Equal(0.9, strict.Accuracy);
        Assert.Equal(SpellPips.Of(2), unrelated.Pips);
    }

    [Fact]
    public void FindsRecordsByTemplatePathAndByNameIgnoringCase() {
        _data.WriteSpell("fire", "fire-cat", SpellFixture.RecordYaml());
        var book = Load();

        Assert.NotNull(book.FindByTemplate("Spells/Tiered Spells/Fire Cat.xml"));
        Assert.Null(book.FindByTemplate("spells/tiered spells/fire cat.xml"));
        Assert.NotNull(book.FindByName("fire cat"));
        Assert.Null(book.FindByName("Fire Cat TC"));
        Assert.Null(book.FindByTemplate(null));
    }

    [Fact]
    public void MissingDirectoryIsAnError() {
        var ex = Assert.Throws<ClassicDataException>(() => Load());

        Assert.Contains("the spells directory does not exist", ex.Message);
    }

    [Fact]
    public void IdMustMatchTheFile() {
        _data.WriteSpell("fire", "fire-cat", SpellFixture.RecordYaml().Replace("id: spell.fire.fire_cat", "id: spell.fire.firecat"));

        var error = SingleError("id");

        Assert.Equal("classic-data/spells/fire/fire-cat.yaml", error.File);
        Assert.Equal(1, error.Line);
        Assert.Contains("expected 'spell.fire.fire_cat'", error.Message);
    }

    [Fact]
    public void SchoolMustMatchTheFolder() {
        _data.WriteSpell("ice", "fire-cat", SpellFixture.RecordYaml().Replace("id: spell.fire.fire_cat", "id: spell.ice.fire_cat"));

        Assert.Contains("does not match the folder 'ice'", SingleError("school").Message);
    }

    [Fact]
    public void UnknownAndMissingKeysAreErrors() {
        _data.WriteSpell("fire", "fire-cat", SpellFixture.RecordYaml().Replace("kind: trained", "kinde: trained"));

        var ex = Assert.Throws<ClassicDataException>(() => Load());

        Assert.Contains(ex.Errors, error => error.KeyPath == "kinde" && error.Message == "unknown key 'kinde'");
        Assert.Contains(ex.Errors, error => error.KeyPath == "kind" && error.Message == "required key 'kind' is missing");
    }

    [Theory]
    [InlineData("pips: 1", "pips: 15", "values.pips", "expected an integer from 0 to 14")]
    [InlineData("pips: 1", "pips: 'X2'", "values.pips", "expected an integer from 0 to 14 or X")]
    [InlineData("accuracy: 0.75", "accuracy: 75", "values.accuracy", "expected a fraction from 0 to 1")]
    [InlineData("level_learned: 1", "level_learned: -1", "values.level_learned", "expected an integer >= 0")]
    [InlineData("trainer: Dalia Falmea", "trainer: ''", "values.trainer", "the trainer is empty")]
    [InlineData("min: 80, max: 120", "min: 120, max: 80", "values.effects[0].min", "min 120 is larger than max 80")]
    [InlineData("min: 80, max: 120", "min: 80", "values.effects[0]", "min and max go together")]
    [InlineData("type: damage", "type: explode", "values.effects[0].type", "expected one of damage, dot")]
    [InlineData("targets: single", "targets: everyone", "values.effects[0].targets", "expected one of self, single")]
    [InlineData("school: fire, min", "school: shadow, min", "values.effects[0].school", "expected one of fire, ice")]
    public void InvalidValuesAreReportedAtTheirKeyPath(string find, string replace, string keyPath, string message) {
        _data.WriteSpell("fire", "fire-cat", SpellFixture.RecordYaml().Replace(find, replace));

        Assert.Contains(message, SingleError(keyPath).Message);
    }

    [Theory]
    [InlineData("{type: dot, school: fire, min: 10, max: 10, targets: single}", "rounds")]
    [InlineData("{type: damage, school: fire, targets: single}", "min, max")]
    [InlineData("{type: blade, school: fire, targets: single}", "percent")]
    [InlineData("{type: stun, school: fire, targets: single}", "rounds")]
    public void EffectTypesNeedTheirFields(string effect, string missing) {
        _data.WriteSpell("fire", "fire-cat", SpellFixture.RecordYaml().Replace(
            "{type: damage, school: fire, min: 80, max: 120, targets: single}", effect));

        Assert.Contains($"needs {missing}", SingleError("values.effects[0]").Message);
    }

    [Fact]
    public void ProfileValuesCannotNameTheCanonicalProfileOrAnUnlistedOne() {
        _data.WriteSpell("fire", "fire-cat", SpellFixture.RecordYaml(profiles: "[late-2009]", extra: """
            profile_values:
              late-2009: {pips: 2}
              arc1-2009h1: {pips: 3}
            """));

        var ex = Assert.Throws<ClassicDataException>(() => Load());

        Assert.Contains(ex.Errors, error => error.KeyPath == "profile_values.late-2009" && error.Message.Contains("canonical"));
        Assert.Contains(ex.Errors, error => error.KeyPath == "profile_values.arc1-2009h1"
            && error.Message.Contains("not in this record's profiles"));
    }

    [Fact]
    public void AnEmptyOverrideIsAnError() {
        _data.WriteSpell("fire", "fire-cat", SpellFixture.RecordYaml(extra: """
            profile_values:
              arc1-2009h1: {}
            """));

        Assert.Contains("needs at least one field", SingleError("profile_values.arc1-2009h1").Message);
    }

    [Fact]
    public void ClientTemplatesAndNamesMustBeUnique() {
        _data.WriteSpell("fire", "fire-cat", SpellFixture.RecordYaml());
        _data.WriteSpell("fire", "fire-kitten", SpellFixture.RecordYaml(slug: "fire-kitten", name: "FIRE CAT"));

        var ex = Assert.Throws<ClassicDataException>(() => Load());

        Assert.Contains(ex.Errors, error => error.KeyPath == "client_template" && error.Message.Contains("fire/fire-cat.yaml"));
        Assert.Contains(ex.Errors, error => error.KeyPath == "name" && error.Message.Contains("fire/fire-cat.yaml"));
    }

    [Fact]
    public void ClientTemplateMustBeASpellPath() {
        _data.WriteSpell("fire", "fire-cat", SpellFixture.RecordYaml(clientTemplate: "ObjectData/Fire Cat.xml"));

        Assert.Contains("is not a Root.wad spell path", SingleError("client_template").Message);
    }

    [Fact]
    public void FilesOutsideSchoolFoldersAreErrors() {
        _data.WriteSpell("fire", "fire-cat", SpellFixture.RecordYaml());
        _data.WriteSpell("shadow", "shadow-cat", SpellFixture.RecordYaml());
        File.WriteAllText(Path.Combine(_data.SpellsPath(), "loose.yaml"), "id: loose\n");
        File.WriteAllText(Path.Combine(_data.SpellsPath(), "README.md"), "# spells\n");

        var ex = Assert.Throws<ClassicDataException>(() => Load());

        Assert.Equal(2, ex.Errors.Length);
        Assert.Contains(ex.Errors, error => error.File == "classic-data/spells/loose.yaml");
        Assert.Contains(ex.Errors, error => error.File == "classic-data/spells/shadow" && error.Message.Contains("not a school folder"));
    }

    [Fact]
    public void EveryInvalidFileIsReportedTogether() {
        _data.WriteSpell("fire", "fire-cat", SpellFixture.RecordYaml().Replace("pips: 1", "pips: many"));
        _data.WriteSpell("ice", "frost-beetle", SpellFixture.RecordYaml(school: "ice", slug: "frost-beetle", name: "Frost Beetle",
            clientTemplate: "Spells/Frost Beetle.xml").Replace("license_tag: own", "license_tag: mine"));

        var ex = Assert.Throws<ClassicDataException>(() => Load());

        Assert.Contains(ex.Errors, error => error.File.EndsWith("fire-cat.yaml", StringComparison.Ordinal) && error.KeyPath == "values.pips");
        Assert.Contains(ex.Errors, error => error.File.EndsWith("frost-beetle.yaml", StringComparison.Ordinal) && error.KeyPath == "license_tag");
    }

}
