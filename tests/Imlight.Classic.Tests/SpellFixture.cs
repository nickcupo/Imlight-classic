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
 * Builders for spell record YAML, hand-made records and template shapes,
 * shared by the spell loader, planner and census tests.
 *
 * USAGE EXAMPLE:
 * data.WriteSpell("fire", "fire-cat", SpellFixture.RecordYaml());
 * var shape = SpellFixture.Shape(rank: 1, accuracy: 75, SpellFixture.Plain(TemplateEffectKind.Damage, 100));
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
using System.IO;
using System.Linq;
using System.Text;
using Imlight.Classic.Spells;

namespace Imlight.Classic.Tests;

internal static class SpellFixture {

    public const string DefaultValues = """
        values:
          pips: 1
          accuracy: 0.75
          level_learned: 1
          training_points: 1
          trainer: Dalia Falmea
          effects:
          - {type: damage, school: fire, min: 80, max: 120, targets: single}
        """;

    public static string SpellsPath(this TempClassicData data) => Path.Combine(data.Root, "spells");

    public static string RulesPath(this TempClassicData data) => Path.Combine(data.Root, "rules");

    public static string WriteSpell(this TempClassicData data, string school, string slug, string yaml) {
        var folder = Path.Combine(data.SpellsPath(), school);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, slug + ".yaml");
        File.WriteAllText(path, yaml.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));

        return path;
    }

    public static string WriteRule(this TempClassicData data, string name, string yaml) {
        Directory.CreateDirectory(data.RulesPath());
        var path = Path.Combine(data.RulesPath(), name);
        File.WriteAllText(path, yaml.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));

        return path;
    }

    /// <summary>
    /// A complete, valid record; <paramref name="values"/> and <paramref name="extra"/> are whole top-level blocks.
    /// </summary>
    public static string RecordYaml(string school = "fire", string slug = "fire-cat", string name = "Fire Cat",
                                    string clientTemplate = "Spells/Tiered Spells/Fire Cat.xml",
                                    string profiles = "[late-2009, arc1-2009h1]", string values = DefaultValues,
                                    string extra = "")
        => $$"""
            id: spell.{{school}}.{{slug.Replace('-', '_')}}
            name: {{name}}
            school: {{school}}
            kind: trained
            client_template: {{clientTemplate}}
            profiles: {{profiles}}
            {{values.TrimEnd()}}
            {{extra.TrimEnd()}}
            introduced: launch
            provenance:
            - source: https://example.invalid/fire-cat
              source_date: '2010-05-04'
              retrieved: '2026-09-27'
              covers: [pips, accuracy, effects]
              confidence: verified
            later_changes: []
            modern_values: {pips: 1, accuracy: 0.75, effects_summary: test}
            license_tag: own
            notes: A test record.
            """;

    public static SpellEffectValues Effect(SpellEffectKind kind, string school = "fire", int? min = null, int? max = null,
                                           int? percent = null, int? rounds = null, SpellTargets? targets = SpellTargets.Single)
        => new(kind, school, min, max ?? min, percent, rounds, targets, null);

    public static ClassicSpellRecord Record(SpellPips pips, double accuracy, params SpellEffectValues[] effects)
        => new() {
            Id = "spell.fire.test",
            Name = "Test",
            School = "fire",
            Kind = "trained",
            ClientTemplate = "Spells/Test.xml",
            Profiles = ["late-2009"],
            Values = new SpellValues(pips, accuracy, null, null, null, [.. effects]),
            SourceFile = "classic-data/spells/fire/test.yaml",
        };

    public static SpellOverridePlan Plan(SpellTemplateShape shape, SpellPips pips, double accuracy, params SpellEffectValues[] effects) {
        var record = Record(pips, accuracy, effects);

        return SpellOverridePlanner.Plan(record, SpellMatch.ClientTemplate, record.Values, shape);
    }

    public static SpellTemplateShape Shape(int rank, int accuracy, params TemplateEffectNode[] effects)
        => new() {
            Path = "Spells/Test.xml",
            Name = "Test",
            Rank = rank,
            Accuracy = accuracy,
            Effects = [.. effects],
        };

    public static TemplateEffectNode Plain(TemplateEffectKind kind, int param, TemplateTarget target = TemplateTarget.EnemySingle,
                                           string damageType = "Fire", int rounds = 0, int pipNumber = 0, float healModifier = 1)
        => new() {
            Kind = kind,
            Param = param,
            Target = target,
            DamageType = damageType,
            Rounds = rounds,
            PipNumber = pipNumber,
            HealModifier = healModifier,
        };

    public static TemplateEffectNode Random(params TemplateEffectNode[] children)
        => new() { Composition = TemplateComposition.Random, Param = -1, Children = [.. children] };

    public static TemplateEffectNode PerPip(params TemplateEffectNode[] children)
        => new() { Composition = TemplateComposition.PerPip, Children = [.. children] };

    public static ImmutableArray<EffectChange> ChangesAt(SpellOverridePlan plan, int index)
        => [.. plan.EffectChanges.Where(change => change.Address.Index == index)];

}
