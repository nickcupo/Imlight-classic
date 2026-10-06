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
 * CLASSIC SPELL VALUES
 * ========================================================================
 *
 * PURPOSE:
 * Loads classic-data/spells/<school>/*.yaml, validates each record against
 * spell.schema.json and builds the ClassicSpellBook.
 *
 * USAGE EXAMPLE:
 * var book = ClassicSpellLoader.Load("/opt/w101c/classic-data/spells");
 *
 * NOTE:
 * The fields the server reads (values, profile_values, client_template,
 * name) are checked in full; provenance, later_changes and modern_values
 * only for shape, since tools/ci/validate-classic-data.py checks them in
 * depth. Every error of every file is reported together.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Spells;

/// <summary>
/// The names classic-data/schema/spell.schema.json defines.
/// </summary>
public static class ClassicSpellSchema {

    /// <summary>
    /// The profile whose numbers a record's <c>values</c> holds.
    /// </summary>
    public const string CanonicalProfileId = "late-2009";

    public static ImmutableArray<string> PlayerSchools { get; } = ["fire", "ice", "storm", "myth", "life", "death", "balance"];

    // CLASSIC: October 2010 secondary-school records do not add base player accuracy tables.
    public static ImmutableArray<string> Schools { get; } = [.. PlayerSchools, "sun", "star"];

    /// <summary>
    /// The schools an effect may name: every school, and <c>all</c>.
    /// </summary>
    public static ImmutableArray<string> EffectSchools { get; } = [.. Schools, "all"];

    public static ImmutableArray<string> Kinds { get; } = ["trained", "crossover", "treasure_card", "item", "pet"];

    /// <summary>
    /// The effect types, in schema order; index i is <see cref="SpellEffectKind"/> value i.
    /// </summary>
    public static ImmutableArray<string> EffectTypes { get; } = [
        "damage", "dot", "heal", "hot", "steal", "pip", "minion", "beguile", "stun", "threat",
        "blade", "charm", "enchant", "trap", "shield", "ward", "prism", "global", "mutate",
        "dispel", "remove_charm", "remove_ward", "reshuffle", "cloak", "stun_resist", "critical_block"
    ];

    /// <summary>
    /// The effect targets, in schema order; index i is <see cref="SpellTargets"/> value i.
    /// </summary>
    public static ImmutableArray<string> Targets { get; } = ["self", "single", "all_enemies", "all_allies"];

    public static ImmutableArray<string> LicenseTags { get; } = ["own", "cc-by-nc-sa-spiraldb"];

    /// <summary>
    /// The schema name of <paramref name="kind"/>.
    /// </summary>
    /// <param name="kind">An effect kind.</param>
    /// <returns>For example <c>remove_charm</c>.</returns>
    public static string NameOf(SpellEffectKind kind) => EffectTypes[(int) kind];

}

/// <summary>
/// Loads and validates classic spell records.
/// </summary>
public static class ClassicSpellLoader {

    internal static readonly FrozenSet<string> s_recordKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "name", "school", "kind", "client_template", "profiles", "values", "profile_values", "introduced",
        "provenance", "later_changes", "modern_values", "license_tag", "notes");
    private static readonly string[] s_recordRequired = [.. s_recordKeys.Where(key => key != "profile_values")];
    internal static readonly FrozenSet<string> s_valuesKeys = FrozenSet.Create(StringComparer.Ordinal,
        "pips", "accuracy", "level_learned", "training_points", "trainer", "effects");
    internal static readonly FrozenSet<string> s_effectKeys = FrozenSet.Create(StringComparer.Ordinal,
        "type", "school", "min", "max", "outcomes", "percent", "rounds", "targets", "notes");
    private static readonly string[] s_effectRequired = ["type", "school"];
    internal static readonly FrozenSet<string> s_modernValuesKeys = FrozenSet.Create(StringComparer.Ordinal,
        "pips", "accuracy", "effects_summary");
    internal static readonly FrozenSet<string> s_provenanceKeys = FrozenSet.Create(StringComparer.Ordinal,
        "source", "source_date", "retrieved", "covers", "confidence");
    internal static readonly FrozenSet<string> s_laterChangeKeys = FrozenSet.Create(StringComparer.Ordinal,
        "date", "change", "source");

    private const int MaxPips = 14;
    private static readonly SpellEffectKind[] s_needsAmount = [SpellEffectKind.Damage, SpellEffectKind.Heal, SpellEffectKind.Dot, SpellEffectKind.Hot];
    private static readonly SpellEffectKind[] s_needsRounds = [SpellEffectKind.Dot, SpellEffectKind.Hot, SpellEffectKind.Stun, SpellEffectKind.Beguile,
        SpellEffectKind.StunResist, SpellEffectKind.CriticalBlock];
    private static readonly SpellEffectKind[] s_needsPercent = [
        SpellEffectKind.Blade, SpellEffectKind.Charm, SpellEffectKind.Trap, SpellEffectKind.Shield, SpellEffectKind.Global,
        SpellEffectKind.StunResist, SpellEffectKind.CriticalBlock
    ];

    private static readonly Regex s_slug = new(@"^[a-z0-9]+(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant);
    private static readonly Regex s_clientTemplate = new(@"^Spells/.+\.xml\z", RegexOptions.CultureInvariant);
    private static readonly Regex s_laterDate = new(@"^\d{4}(-\d{2})?\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads every record under <paramref name="spellsDirectory"/>.
    /// </summary>
    /// <param name="spellsDirectory">The classic-data/spells directory.</param>
    /// <returns>The book.</returns>
    /// <exception cref="ClassicDataException">The directory is missing, or a record is invalid; every error is reported.</exception>
    public static ClassicSpellBook Load(string spellsDirectory) {
        var directory = Path.GetFullPath(spellsDirectory);
        if (!Directory.Exists(directory)) {
            throw new ClassicDataException(new ClassicDataError(ClassicDataLocator.DisplayPath(directory), "", null,
                "the spells directory does not exist"));
        }

        var diagnostics = new YamlDiagnostics();
        foreach (var stray in Directory.GetFiles(directory, "*.yaml").Order(StringComparer.Ordinal)) {
            diagnostics.Add(new ClassicDataError(ClassicDataLocator.DisplayPath(stray), "", null,
                "spell records live in spells/<school>/, not in spells/ itself"));
        }

        var records = new List<ClassicSpellRecord>();
        foreach (var folder in Directory.GetDirectories(directory).Order(StringComparer.Ordinal)) {
            var school = Path.GetFileName(folder);
            if (!ClassicSpellSchema.Schools.Contains(school, StringComparer.Ordinal)) {
                diagnostics.Add(new ClassicDataError(ClassicDataLocator.DisplayPath(folder), "", null,
                    $"'{school}' is not a school folder (expected one of {string.Join(", ", ClassicSpellSchema.Schools)})"));
                continue;
            }

            foreach (var file in Directory.GetFiles(folder, "*.yaml").Order(StringComparer.Ordinal)) {
                if (ReadRecord(file, school, diagnostics) is { } record) {
                    records.Add(record);
                }
            }
        }

        CheckUnique(records, record => record.ClientTemplate, StringComparer.Ordinal, "client_template", diagnostics);
        CheckUnique(records, record => record.Name, StringComparer.OrdinalIgnoreCase, "name", diagnostics);
        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new ClassicSpellBook(directory, records);
    }

    private static ClassicSpellRecord? ReadRecord(string file, string folderSchool, YamlDiagnostics diagnostics) {
        var display = ClassicDataLocator.DisplayPath(file);
        var errorsBefore = diagnostics.Errors.Count;
        var root = YamlTree.Parse(file, display, diagnostics);
        if (root is null) {
            return null;
        }

        if (root is not YMap map) {
            diagnostics.At(root, "", $"the root must be a mapping, got {root.Describe()}");

            return null;
        }

        diagnostics.CheckKeys(map, "", s_recordKeys, s_recordRequired);
        var slug = Path.GetFileNameWithoutExtension(file);
        if (!s_slug.IsMatch(slug)) {
            diagnostics.Add(new ClassicDataError(display, "", null,
                $"'{slug}.yaml' is not a valid record file name (lowercase words joined by -)"));
        }

        var expectedId = $"spell.{folderSchool}.{slug.Replace('-', '_')}";
        var id = ReadString(map, "id", diagnostics);
        if (id is not null && !string.Equals(id, expectedId, StringComparison.Ordinal)) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' does not match the file; expected '{expectedId}'");
        }

        var name = ReadString(map, "name", diagnostics);
        if (name is { Length: 0 }) {
            diagnostics.At(map.Find("name")!.Value, "name", "the name is empty");
        }

        var schoolEntry = map.Find("school");
        var school = schoolEntry is null ? null : diagnostics.ReadEnum(schoolEntry.Value, "school", ClassicSpellSchema.Schools);
        if (school is not null && !string.Equals(school, folderSchool, StringComparison.Ordinal)) {
            diagnostics.At(schoolEntry!.Value, "school", $"school '{school}' does not match the folder '{folderSchool}'");
        }

        var kind = map.Find("kind") is { } kindEntry ? diagnostics.ReadEnum(kindEntry.Value, "kind", ClassicSpellSchema.Kinds) : null;
        var clientTemplate = ReadClientTemplate(map, diagnostics);
        var profiles = ReadProfiles(map, diagnostics);
        var values = map.Find("values") is { } valuesEntry ? ReadValues(valuesEntry.Value, "values", diagnostics) : null;
        var profileValues = ReadProfileValues(map, profiles, diagnostics);
        CheckIntroduced(map, diagnostics);
        CheckProvenance(map, diagnostics);
        CheckLaterChanges(map, diagnostics);
        CheckModernValues(map, diagnostics);
        if (map.Find("license_tag") is { } license) {
            _ = diagnostics.ReadEnum(license.Value, "license_tag", ClassicSpellSchema.LicenseTags);
        }

        _ = ReadString(map, "notes", diagnostics);
        if (diagnostics.Errors.Count > errorsBefore || id is null || name is null || school is null || kind is null || values is null) {
            return null;
        }

        return new ClassicSpellRecord {
            Id = id,
            Name = name,
            School = school,
            Kind = kind,
            ClientTemplate = clientTemplate,
            Profiles = profiles,
            Values = values,
            ProfileValues = profileValues,
            SourceFile = display,
        };
    }

    private static string? ReadClientTemplate(YMap map, YamlDiagnostics diagnostics) {
        if (map.Find("client_template") is not { } entry || entry.Value is YNull) {
            return null;
        }

        if (diagnostics.ReadString(entry.Value, "client_template") is not { } path) {
            return null;
        }

        if (!s_clientTemplate.IsMatch(path)) {
            diagnostics.At(entry.Value, "client_template", $"'{path}' is not a Root.wad spell path (Spells/....xml)");

            return null;
        }

        return path;
    }

    private static ImmutableArray<string> ReadProfiles(YMap map, YamlDiagnostics diagnostics) {
        if (map.Find("profiles") is not { } entry || diagnostics.ReadList(entry.Value, "profiles") is not { } list) {
            return [];
        }

        if (list.Items.IsEmpty) {
            diagnostics.At(list, "profiles", "needs at least one profile");
        }

        var profiles = ImmutableArray.CreateBuilder<string>();
        for (var i = 0; i < list.Items.Length; i++) {
            var keyPath = YamlTree.Index("profiles", i);
            if (diagnostics.ReadString(list.Items[i], keyPath) is not { } profile) {
                continue;
            }

            if (!ClassicSchema.IsValidId(profile)) {
                diagnostics.At(list.Items[i], keyPath, $"'{profile}' is not a valid profile id (expected {ClassicSchema.IdPattern})");
            }
            else if (profiles.Contains(profile)) {
                diagnostics.At(list.Items[i], keyPath, $"profile '{profile}' is listed twice");
            }
            else {
                profiles.Add(profile);
            }
        }

        return profiles.ToImmutable();
    }

    private static SpellValues? ReadValues(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadMap(node, keyPath) is not { } map) {
            return null;
        }

        diagnostics.CheckKeys(map, keyPath, s_valuesKeys, s_valuesKeys);
        var pips = map.Find("pips") is { } pipsEntry ? ReadPips(pipsEntry.Value, YamlTree.Join(keyPath, "pips"), diagnostics) : null;
        var accuracy = map.Find("accuracy") is { } accuracyEntry
            ? diagnostics.ReadFraction(accuracyEntry.Value, YamlTree.Join(keyPath, "accuracy"))
            : null;
        var levelLearned = ReadOptionalCount(map, keyPath, "level_learned", diagnostics);
        var trainingPoints = ReadOptionalCount(map, keyPath, "training_points", diagnostics);
        var trainer = ReadTrainer(map, keyPath, diagnostics);
        var effects = map.Find("effects") is { } effectsEntry
            ? ReadEffects(effectsEntry.Value, YamlTree.Join(keyPath, "effects"), diagnostics)
            : null;
        if (pips is null || accuracy is null || effects is null) {
            return null;
        }

        return new SpellValues(pips.Value, accuracy.Value, levelLearned, trainingPoints, trainer, effects.Value);
    }

    private static ImmutableDictionary<string, SpellValuesOverride> ReadProfileValues(YMap map, ImmutableArray<string> profiles,
                                                                                    YamlDiagnostics diagnostics) {
        var result = ImmutableDictionary.CreateBuilder<string, SpellValuesOverride>(StringComparer.Ordinal);
        if (map.Find("profile_values") is not { } entry || diagnostics.ReadMap(entry.Value, "profile_values") is not { } entries) {
            return result.ToImmutable();
        }

        if (entries.Entries.IsEmpty) {
            diagnostics.At(entries, "profile_values", "needs at least one profile");
        }

        foreach (var profileEntry in entries.Entries) {
            var keyPath = YamlTree.Join("profile_values", profileEntry.Key);
            if (!ClassicSchema.IsValidId(profileEntry.Key)) {
                diagnostics.AtKey(entries, profileEntry, keyPath, $"'{profileEntry.Key}' is not a valid profile id");
                continue;
            }

            if (string.Equals(profileEntry.Key, ClassicSpellSchema.CanonicalProfileId, StringComparison.Ordinal)) {
                diagnostics.AtKey(entries, profileEntry, keyPath,
                    $"'{ClassicSpellSchema.CanonicalProfileId}' is the canonical profile; its numbers belong in values");
                continue;
            }

            if (!profiles.Contains(profileEntry.Key, StringComparer.Ordinal)) {
                diagnostics.AtKey(entries, profileEntry, keyPath, $"profile '{profileEntry.Key}' is not in this record's profiles");
            }

            if (ReadOverride(profileEntry.Value, keyPath, diagnostics) is { } values) {
                result[profileEntry.Key] = values;
            }
        }

        return result.ToImmutable();
    }

    private static SpellValuesOverride? ReadOverride(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadMap(node, keyPath) is not { } map) {
            return null;
        }

        diagnostics.CheckKeys(map, keyPath, s_valuesKeys, []);
        if (map.Entries.IsEmpty) {
            diagnostics.At(map, keyPath, "needs at least one field");
        }

        var effects = map.Find("effects") is { } effectsEntry
            ? ReadEffects(effectsEntry.Value, YamlTree.Join(keyPath, "effects"), diagnostics)
            : null;

        return new SpellValuesOverride {
            Pips = map.Find("pips") is { } pips ? ReadPips(pips.Value, YamlTree.Join(keyPath, "pips"), diagnostics) : null,
            Accuracy = map.Find("accuracy") is { } accuracy ? diagnostics.ReadFraction(accuracy.Value, YamlTree.Join(keyPath, "accuracy")) : null,
            SetsLevelLearned = map.Find("level_learned") is not null,
            LevelLearned = ReadOptionalCount(map, keyPath, "level_learned", diagnostics),
            SetsTrainingPoints = map.Find("training_points") is not null,
            TrainingPoints = ReadOptionalCount(map, keyPath, "training_points", diagnostics),
            SetsTrainer = map.Find("trainer") is not null,
            Trainer = ReadTrainer(map, keyPath, diagnostics),
            Effects = effects,
        };
    }

    private static SpellPips? ReadPips(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (node is YScalar { Value: "X" }) {
            return SpellPips.X;
        }

        if (node is YScalar { IsPlain: true } && diagnostics.ReadInt(node, keyPath, 0, MaxPips) is { } pips) {
            return SpellPips.Of(pips);
        }

        if (node is not YScalar { IsPlain: true }) {
            diagnostics.At(node, keyPath, $"expected an integer from 0 to {MaxPips} or X, got {node.Describe()}");
        }

        return null;
    }

    private static int? ReadOptionalCount(YMap map, string keyPath, string key, YamlDiagnostics diagnostics) {
        if (map.Find(key) is not { } entry || entry.Value is YNull) {
            return null;
        }

        return diagnostics.ReadInt(entry.Value, YamlTree.Join(keyPath, key), 0);
    }

    private static string? ReadTrainer(YMap map, string keyPath, YamlDiagnostics diagnostics) {
        if (map.Find("trainer") is not { } entry || entry.Value is YNull) {
            return null;
        }

        var trainer = diagnostics.ReadString(entry.Value, YamlTree.Join(keyPath, "trainer"));
        if (trainer is { Length: 0 }) {
            diagnostics.At(entry.Value, YamlTree.Join(keyPath, "trainer"), "the trainer is empty; use null");
        }

        return trainer;
    }

    private static ImmutableArray<SpellEffectValues>? ReadEffects(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadList(node, keyPath) is not { } list) {
            return null;
        }

        if (list.Items.IsEmpty) {
            diagnostics.At(list, keyPath, "needs at least one effect");
        }

        var effects = ImmutableArray.CreateBuilder<SpellEffectValues>();
        var valid = true;
        for (var i = 0; i < list.Items.Length; i++) {
            if (ReadEffect(list.Items[i], YamlTree.Index(keyPath, i), diagnostics) is { } effect) {
                effects.Add(effect);
            }
            else {
                valid = false;
            }
        }

        return valid ? effects.ToImmutable() : null;
    }

    private static SpellEffectValues? ReadEffect(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadMap(node, keyPath) is not { } map) {
            return null;
        }

        var errorsBefore = diagnostics.Errors.Count;
        diagnostics.CheckKeys(map, keyPath, s_effectKeys, s_effectRequired);
        var typeName = map.Find("type") is { } type
            ? diagnostics.ReadEnum(type.Value, YamlTree.Join(keyPath, "type"), ClassicSpellSchema.EffectTypes)
            : null;
        var school = map.Find("school") is { } schoolEntry
            ? diagnostics.ReadEnum(schoolEntry.Value, YamlTree.Join(keyPath, "school"), ClassicSpellSchema.EffectSchools)
            : null;
        int? Read(string key, int min)
            => map.Find(key) is { } entry ? diagnostics.ReadInt(entry.Value, YamlTree.Join(keyPath, key), min) : null;
        var min = Read("min", 0);
        var max = Read("max", 0);
        var percent = Read("percent", int.MinValue);
        var rounds = Read("rounds", 1);
        var targetName = map.Find("targets") is { } targets
            ? diagnostics.ReadEnum(targets.Value, YamlTree.Join(keyPath, "targets"), ClassicSpellSchema.Targets)
            : null;
        var notes = map.Find("notes") is { } notesEntry ? diagnostics.ReadString(notesEntry.Value, YamlTree.Join(keyPath, "notes")) : null;

        if ((map.Find("min") is null) != (map.Find("max") is null)) {
            diagnostics.At(map, keyPath, "min and max go together");
        }
        else if (min > max) {
            diagnostics.At(map.Find("min")!.Value, YamlTree.Join(keyPath, "min"), $"min {min} is larger than max {max}");
        }

        if (typeName is null || school is null || diagnostics.Errors.Count > errorsBefore) {
            return null;
        }

        var kind = (SpellEffectKind) ClassicSpellSchema.EffectTypes.IndexOf(typeName);
        // CLASSIC: a discrete roll must never be interpreted as every integer between min and max.
        var outcomes = ImmutableArray<int>.Empty;
        if (map.Find("outcomes") is { } outcomesEntry) {
            var outcomesPath = YamlTree.Join(keyPath, "outcomes");
            if (diagnostics.ReadList(outcomesEntry.Value, outcomesPath) is { } list) {
                var parsed = ImmutableArray.CreateBuilder<int>();
                for (var i = 0; i < list.Items.Length; i++) {
                    if (diagnostics.ReadInt(list.Items[i], YamlTree.Index(outcomesPath, i), 0) is { } value) {
                        parsed.Add(value);
                    }
                }

                outcomes = parsed.ToImmutable();
                if (list.Items.Length is < 2 or > 15 || outcomes.Distinct().Count() != outcomes.Length) {
                    diagnostics.At(list, outcomesPath, "needs 2 to 15 distinct outcomes (four-bit index 15 is the no-choice sentinel)");
                }
                if (!outcomes.IsEmpty && (min != outcomes.Min() || max != outcomes.Max())) {
                    diagnostics.At(list, outcomesPath, "min and max must bound the explicit outcomes exactly");
                }
            }

            if (kind != SpellEffectKind.Damage) {
                diagnostics.At(outcomesEntry.Value, outcomesPath, "explicit outcomes currently require a damage effect");
            }
        }

        var missing = new List<string>();
        if (s_needsAmount.Contains(kind)) {
            missing.AddRange(new[] { "min", "max", "targets" }.Where(key => map.Find(key) is null));
        }

        if (s_needsRounds.Contains(kind) && map.Find("rounds") is null) {
            missing.Add("rounds");
        }

        if (s_needsPercent.Contains(kind) && map.Find("percent") is null) {
            missing.Add("percent");
        }

        if (missing.Count > 0) {
            diagnostics.At(map, keyPath, $"a {typeName} effect needs {string.Join(", ", missing)}");

            return null;
        }

        SpellTargets? target = targetName is null ? null : (SpellTargets) ClassicSpellSchema.Targets.IndexOf(targetName);

        return diagnostics.Errors.Count > errorsBefore ? null
            : new SpellEffectValues(kind, school, min, max, percent, rounds, target, notes) { Outcomes = outcomes };
    }

    private static void CheckIntroduced(YMap map, YamlDiagnostics diagnostics) {
        if (map.Find("introduced") is not { } entry || diagnostics.ReadString(entry.Value, "introduced") is not { } value) {
            return;
        }

        if (value is not ("launch" or "unknown")) {
            _ = diagnostics.ReadDate(entry.Value, "introduced", allowNull: false);
        }
    }

    private static void CheckProvenance(YMap map, YamlDiagnostics diagnostics) {
        if (map.Find("provenance") is not { } entry || diagnostics.ReadList(entry.Value, "provenance") is not { } list) {
            return;
        }

        if (list.Items.IsEmpty) {
            diagnostics.At(list, "provenance", "needs at least one source");
        }

        for (var i = 0; i < list.Items.Length; i++) {
            var keyPath = YamlTree.Index("provenance", i);
            if (diagnostics.ReadMap(list.Items[i], keyPath) is { } source) {
                diagnostics.CheckKeys(source, keyPath, s_provenanceKeys, s_provenanceKeys);
            }
        }
    }

    private static void CheckLaterChanges(YMap map, YamlDiagnostics diagnostics) {
        if (map.Find("later_changes") is not { } entry || diagnostics.ReadList(entry.Value, "later_changes") is not { } list) {
            return;
        }

        for (var i = 0; i < list.Items.Length; i++) {
            var keyPath = YamlTree.Index("later_changes", i);
            if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } change) {
                continue;
            }

            diagnostics.CheckKeys(change, keyPath, s_laterChangeKeys, s_laterChangeKeys);
            if (change.Find("date")?.Value is YScalar { Value: var date } dateNode
                && date != "unknown" && !s_laterDate.IsMatch(date)) {
                _ = diagnostics.ReadDate(dateNode, YamlTree.Join(keyPath, "date"), allowNull: false);
            }
        }
    }

    private static void CheckModernValues(YMap map, YamlDiagnostics diagnostics) {
        if (map.Find("modern_values") is { } entry && diagnostics.ReadMap(entry.Value, "modern_values") is { } modern) {
            diagnostics.CheckKeys(modern, "modern_values", s_modernValuesKeys, s_modernValuesKeys);
        }
    }

    private static string? ReadString(YMap map, string key, YamlDiagnostics diagnostics)
        => map.Find(key) is { } entry ? diagnostics.ReadString(entry.Value, key) : null;

    private static void CheckUnique(List<ClassicSpellRecord> records, Func<ClassicSpellRecord, string?> key,
                                    StringComparer comparer, string keyName, YamlDiagnostics diagnostics) {
        var seen = new Dictionary<string, ClassicSpellRecord>(comparer);
        foreach (var record in records) {
            if (key(record) is not { } value) {
                continue;
            }

            if (!seen.TryAdd(value, record)) {
                diagnostics.Add(new ClassicDataError(record.SourceFile, keyName, null,
                    $"{keyName} '{value}' is also used by {seen[value].SourceFile}"));
            }
        }
    }

}
