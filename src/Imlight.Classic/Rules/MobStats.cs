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
 * CLASSIC MOB STATS
 * ========================================================================
 *
 * PURPOSE:
 * A creature's health as it was at the profile's cutoff, keyed by the client
 * templates that place it. The server starts the creature with this health
 * instead of its template's m_nStartingHealth. An entry may also name the
 * creature's school at the cutoff (the school a duel shows for it) where it
 * differs from the template's m_schoolOfFocus.
 *
 * USAGE EXAMPLE:
 * var stats = MobStatsLoader.Load(path);
 * var health = stats.HealthOf(templateId);   // null: keep the template's
 * var school = stats.SchoolOf(templateId);   // null: keep the template's
 *
 * NOTE:
 * Every entry names the dated page it came from. Creatures without a dated
 * value keep their template health.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>
/// Creature health at the cutoff, by client template id.
/// </summary>
public sealed class MobStats {

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }

    /// <summary>
    /// Health by template id.
    /// </summary>
    public required FrozenDictionary<ulong, int> HealthByTemplate { get; init; }

    /// <summary>
    /// The creature's school at the cutoff, by template id, for the entries that name one.
    /// </summary>
    public FrozenDictionary<ulong, string> SchoolByTemplate { get; init; } = FrozenDictionary<ulong, string>.Empty;

    public required string SourceFile { get; init; }

    /// <summary>
    /// The creature's school at the cutoff (a <c>MagicSchool</c> name), or null when the table names none for
    /// <paramref name="templateId"/>.
    /// </summary>
    public string? SchoolOf(ulong templateId)
        => SchoolByTemplate.TryGetValue(templateId, out var school) ? school : null;

    /// <summary>
    /// The creature's health at the cutoff, or null when the table has none for <paramref name="templateId"/>.
    /// </summary>
    public int? HealthOf(ulong templateId)
        => HealthByTemplate.TryGetValue(templateId, out var health) ? health : null;

}

/// <summary>
/// Loads and validates classic mob stats.
/// </summary>
public static class MobStatsLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "provenance", "license_tag", "notes", "mobs");
    internal static readonly FrozenSet<string> s_mobKeys = FrozenSet.Create(StringComparer.Ordinal,
        "name", "templates", "health", "modern_health", "rank", "school", "source", "source_date", "confidence", "notes", "profiles");
    internal static readonly FrozenSet<string> s_schools = FrozenSet.Create(StringComparer.Ordinal,
        "Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance");
    private static readonly Regex s_id = new(@"^mob-stats-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the stats at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static MobStats Load(string path) => Load(path, null);

    /// <summary>
    /// CLASSIC: loads the creatures for <paramref name="profileId"/>: an entry with its own <c>profiles</c> list (a dated
    /// value of a later profile, e.g. october-2010-arc1) counts only for those profiles, and overrides no other entry.
    /// </summary>
    public static MobStats Load(string path, string? profileId) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the mob stats do not exist"));
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

        diagnostics.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "provenance", "license_tag", "mobs"]);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be mob-stats-<name> and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        var health = new Dictionary<ulong, int>();
        var schools = new Dictionary<ulong, string>();
        if (map.Find("mobs") is { } mobsEntry && diagnostics.ReadList(mobsEntry.Value, "mobs") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("mobs", i);
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } mob) {
                    continue;
                }

                diagnostics.CheckKeys(mob, keyPath, s_mobKeys, ["name", "templates", "health", "source", "source_date"]);
                if (mob.Find("profiles") is { } only) {
                    if (diagnostics.ReadList(only.Value, YamlTree.Join(keyPath, "profiles")) is not { } entryProfiles) {
                        continue;
                    }
                    var listed = entryProfiles.Items.Select(item => diagnostics.ReadString(item, YamlTree.Join(keyPath, "profiles"))).ToList();
                    if (profileId is null || !listed.Contains(profileId, StringComparer.Ordinal)) {
                        continue;
                    }
                }

                var hp = mob.Find("health") is { } h ? diagnostics.ReadInt(h.Value, YamlTree.Join(keyPath, "health"), 1, 10_000_000) : null;
                if (mob.Find("templates") is not { } templatesEntry
                    || diagnostics.ReadList(templatesEntry.Value, YamlTree.Join(keyPath, "templates")) is not { } templates
                    || hp is null) {
                    continue;
                }

                string? school = null;
                if (mob.Find("school") is { } schoolEntry
                    && diagnostics.ReadString(schoolEntry.Value, YamlTree.Join(keyPath, "school")) is { } schoolName) {
                    if (s_schools.Contains(schoolName)) {
                        school = schoolName;
                    }
                    else {
                        diagnostics.At(schoolEntry.Value, YamlTree.Join(keyPath, "school"),
                            $"school '{schoolName}' must be one of {string.Join(", ", s_schools.Order(StringComparer.Ordinal))}");
                    }
                }

                for (var t = 0; t < templates.Items.Length; t++) {
                    var tidPath = YamlTree.Index(YamlTree.Join(keyPath, "templates"), t);
                    if (diagnostics.ReadInt(templates.Items[t], tidPath, 1, int.MaxValue) is not { } tid) {
                        continue;
                    }

                    if (!health.TryAdd((ulong) tid, hp.Value)) {
                        diagnostics.At(templates.Items[t], tidPath, $"template {tid} is listed twice");
                    }
                    else if (school is not null) {
                        schools[(ulong) tid] = school;
                    }
                }
            }
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new MobStats {
            Id = id!,
            Profiles = profiles,
            HealthByTemplate = health.ToFrozenDictionary(),
            SchoolByTemplate = schools.ToFrozenDictionary(),
            SourceFile = display,
        };
    }

}
