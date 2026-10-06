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
 * A profile's per-school base accuracy table (rules/accuracy-2009.yaml),
 * loaded and checked against accuracy.schema.json.
 *
 * USAGE EXAMPLE:
 * var table = AccuracyTableLoader.Load(Path.Combine(classicDataRoot, profile.Rules.AccuracyTable));
 * var fire = table.BaseAccuracy("fire", profile.Lineage);   // 0.75
 *
 * NOTE:
 * A spell record's own accuracy always wins for its card. The table is the
 * school default for cards with no record; the server keeps those cards'
 * client accuracy and only reports how they compare.
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
using Imlight.Classic.Spells;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>
/// One school's entry in an accuracy table.
/// </summary>
/// <param name="Accuracy">The canonical profile's base accuracy, as a fraction.</param>
/// <param name="ProfileValues">Other profiles' base accuracy, keyed by profile id.</param>
/// <param name="ModernAccuracy">The school's base accuracy in the modern client, when recorded.</param>
public sealed record SchoolAccuracy(double Accuracy, ImmutableDictionary<string, double> ProfileValues, double? ModernAccuracy);

/// <summary>
/// Base spell accuracy per school.
/// </summary>
public sealed class AccuracyTable {

    public required string Id { get; init; }
    public string? Title { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }

    /// <summary>
    /// Every school's entry, keyed by school id.
    /// </summary>
    public required ImmutableDictionary<string, SchoolAccuracy> Schools { get; init; }

    public required string SourceFile { get; init; }

    /// <summary>
    /// The base accuracy of <paramref name="school"/> in the profile whose extends chain is <paramref name="lineage"/>.
    /// </summary>
    /// <param name="school">A school id such as <c>fire</c>.</param>
    /// <param name="lineage">Profile ids, child first.</param>
    /// <returns>The fraction, or null for an unknown school.</returns>
    public double? BaseAccuracy(string school, IReadOnlyList<string> lineage) {
        if (!Schools.TryGetValue(school, out var entry)) {
            return null;
        }

        foreach (var profileId in lineage) {
            if (entry.ProfileValues.TryGetValue(profileId, out var accuracy)) {
                return accuracy;
            }
        }

        return entry.Accuracy;
    }

}

/// <summary>
/// Loads and validates accuracy tables.
/// </summary>
public static class AccuracyTableLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "schools", "provenance", "later_changes", "license_tag", "notes");
    private static readonly string[] s_rootRequired = ["id", "profiles", "schools", "license_tag"];
    internal static readonly FrozenSet<string> s_schoolKeys = FrozenSet.Create(StringComparer.Ordinal,
        "accuracy", "profile_values", "provenance", "later_changes", "modern_accuracy", "notes");
    private static readonly string[] s_schoolRequired = ["accuracy", "provenance", "later_changes"];

    private static readonly Regex s_id = new(@"^accuracy-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the table at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The table file, such as classic-data/rules/accuracy-2009.yaml.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static AccuracyTable Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the accuracy table does not exist"));
        }

        var diagnostics = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, diagnostics);
        if (root is null) {
            throw diagnostics.ToException();
        }

        if (root is not YMap map) {
            diagnostics.At(root, "", $"the root must be a mapping, got {root.Describe()}");

            throw diagnostics.ToException();
        }

        diagnostics.CheckKeys(map, "", s_rootKeys, s_rootRequired);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be accuracy-<name> and match the file name '{expectedId}'");
        }

        var title = map.Find("title") is { } titleEntry ? diagnostics.ReadString(titleEntry.Value, "title") : null;
        var profiles = ReadProfiles(map, diagnostics);
        var schools = ReadSchools(map, profiles, diagnostics);
        if (map.Find("license_tag") is { } license) {
            _ = diagnostics.ReadEnum(license.Value, "license_tag", ClassicSpellSchema.LicenseTags);
        }

        foreach (var key in new[] { "provenance", "later_changes" }) {
            if (map.Find(key) is { } list) {
                _ = diagnostics.ReadList(list.Value, key);
            }
        }

        if (map.Find("notes") is { } notes) {
            _ = diagnostics.ReadString(notes.Value, "notes");
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new AccuracyTable {
            Id = id!,
            Title = title,
            Profiles = profiles,
            Schools = schools,
            SourceFile = display,
        };
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

            if (!ClassicSchema.IsValidId(profile) || profiles.Contains(profile)) {
                diagnostics.At(list.Items[i], keyPath, $"'{profile}' is not a valid, unrepeated profile id");
                continue;
            }

            profiles.Add(profile);
        }

        return profiles.ToImmutable();
    }

    private static ImmutableDictionary<string, SchoolAccuracy> ReadSchools(YMap map, ImmutableArray<string> profiles,
                                                                         YamlDiagnostics diagnostics) {
        var schools = ImmutableDictionary.CreateBuilder<string, SchoolAccuracy>(StringComparer.Ordinal);
        if (map.Find("schools") is not { } entry || diagnostics.ReadMap(entry.Value, "schools") is not { } schoolMap) {
            return schools.ToImmutable();
        }

        diagnostics.CheckKeys(schoolMap, "schools", ClassicSpellSchema.PlayerSchools.ToFrozenSet(StringComparer.Ordinal),
            ClassicSpellSchema.PlayerSchools);
        foreach (var schoolEntry in schoolMap.Entries) {
            var keyPath = YamlTree.Join("schools", schoolEntry.Key);
            if (!ClassicSpellSchema.PlayerSchools.Contains(schoolEntry.Key, StringComparer.Ordinal)
                || diagnostics.ReadMap(schoolEntry.Value, keyPath) is not { } school) {
                continue;
            }

            diagnostics.CheckKeys(school, keyPath, s_schoolKeys, s_schoolRequired);
            var accuracy = school.Find("accuracy") is { } accuracyEntry
                ? diagnostics.ReadFraction(accuracyEntry.Value, YamlTree.Join(keyPath, "accuracy"))
                : null;
            double? modern = school.Find("modern_accuracy") is { Value: not YNull } modernEntry
                ? diagnostics.ReadFraction(modernEntry.Value, YamlTree.Join(keyPath, "modern_accuracy"))
                : null;
            var profileValues = ReadSchoolProfileValues(school, keyPath, profiles, diagnostics);
            if (school.Find("provenance") is { } provenance
                && diagnostics.ReadList(provenance.Value, YamlTree.Join(keyPath, "provenance")) is { Items.IsEmpty: true } empty) {
                diagnostics.At(empty, YamlTree.Join(keyPath, "provenance"), "needs at least one source");
            }

            if (school.Find("later_changes") is { } changes) {
                _ = diagnostics.ReadList(changes.Value, YamlTree.Join(keyPath, "later_changes"));
            }

            if (accuracy is not null) {
                schools[schoolEntry.Key] = new SchoolAccuracy(accuracy.Value, profileValues, modern);
            }
        }

        return schools.ToImmutable();
    }

    private static ImmutableDictionary<string, double> ReadSchoolProfileValues(YMap school, string schoolPath,
                                                                              ImmutableArray<string> profiles,
                                                                              YamlDiagnostics diagnostics) {
        var result = ImmutableDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
        var keyPath = YamlTree.Join(schoolPath, "profile_values");
        if (school.Find("profile_values") is not { } entry || diagnostics.ReadMap(entry.Value, keyPath) is not { } entries) {
            return result.ToImmutable();
        }

        foreach (var profileEntry in entries.Entries) {
            var profilePath = YamlTree.Join(keyPath, profileEntry.Key);
            if (!profiles.Contains(profileEntry.Key, StringComparer.Ordinal)
                || string.Equals(profileEntry.Key, ClassicSpellSchema.CanonicalProfileId, StringComparison.Ordinal)) {
                diagnostics.AtKey(entries, profileEntry, profilePath,
                    $"'{profileEntry.Key}' must be one of the table's profiles and not the canonical profile");
                continue;
            }

            if (diagnostics.ReadMap(profileEntry.Value, profilePath) is not { } values) {
                continue;
            }

            diagnostics.CheckKeys(values, profilePath, FrozenSet.Create(StringComparer.Ordinal, "accuracy"), ["accuracy"]);
            if (values.Find("accuracy") is { } accuracy
                && diagnostics.ReadFraction(accuracy.Value, YamlTree.Join(profilePath, "accuracy")) is { } fraction) {
                result[profileEntry.Key] = fraction;
            }
        }

        return result.ToImmutable();
    }

}
