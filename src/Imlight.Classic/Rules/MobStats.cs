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
 * instead of its template's m_nStartingHealth.
 *
 * USAGE EXAMPLE:
 * var stats = MobStatsLoader.Load(path);
 * var health = stats.HealthOf(templateId);   // null: keep the template's
 *
 * NOTE:
 * Every entry names the dated page it came from. Creatures without a dated
 * value keep their template health.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/28/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
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

    public required string SourceFile { get; init; }

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
        "name", "templates", "health", "modern_health", "rank", "school", "source", "source_date", "confidence", "notes");
    private static readonly Regex s_id = new(@"^mob-stats-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the stats at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static MobStats Load(string path) {
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
        if (map.Find("mobs") is { } mobsEntry && diagnostics.ReadList(mobsEntry.Value, "mobs") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("mobs", i);
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } mob) {
                    continue;
                }

                diagnostics.CheckKeys(mob, keyPath, s_mobKeys, ["name", "templates", "health", "source", "source_date"]);
                var hp = mob.Find("health") is { } h ? diagnostics.ReadInt(h.Value, YamlTree.Join(keyPath, "health"), 1, 10_000_000) : null;
                if (mob.Find("templates") is not { } templatesEntry
                    || diagnostics.ReadList(templatesEntry.Value, YamlTree.Join(keyPath, "templates")) is not { } templates
                    || hp is null) {
                    continue;
                }

                for (var t = 0; t < templates.Items.Length; t++) {
                    var tidPath = YamlTree.Index(YamlTree.Join(keyPath, "templates"), t);
                    if (diagnostics.ReadInt(templates.Items[t], tidPath, 1, int.MaxValue) is not { } tid) {
                        continue;
                    }

                    if (!health.TryAdd((ulong) tid, hp.Value)) {
                        diagnostics.At(templates.Items[t], tidPath, $"template {tid} is listed twice");
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
            SourceFile = display,
        };
    }

}
