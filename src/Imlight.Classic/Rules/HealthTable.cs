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
 * CLASSIC PLAYER HEALTH
 * ========================================================================
 *
 * PURPOSE:
 * A wizard's base health (nothing equipped) at each level, per school, as it
 * was at the profile's cutoff. The server writes it into the client's
 * MagicXPConfig class tables, which the modern client raised for five schools.
 *
 * USAGE EXAMPLE:
 * var table = HealthTableLoader.Load(path);
 * var health = table.HealthOf("Life", 50);   // 1800; null outside the table
 *
 * NOTE:
 * School names are the client's class names (Fire, Ice, Storm, Myth, Life,
 * Death, Balance), compared without case.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
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
/// Base health per level and school.
/// </summary>
public sealed class HealthTable {

    /// <summary>
    /// The schools a table row gives, in file order.
    /// </summary>
    public static ImmutableArray<string> Schools { get; } = ["fire", "ice", "storm", "myth", "life", "death", "balance"];

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }

    /// <summary>
    /// The last level in the table.
    /// </summary>
    public required int MaxLevel { get; init; }

    /// <summary>
    /// Health by lower-case school, indexed by level - 1.
    /// </summary>
    public required FrozenDictionary<string, ImmutableArray<int>> BySchool { get; init; }

    public required string SourceFile { get; init; }

    /// <summary>
    /// The base health of a <paramref name="school"/> wizard at <paramref name="level"/>.
    /// </summary>
    /// <param name="school">A client class name such as "Life".</param>
    /// <param name="level">1 to <see cref="MaxLevel"/>.</param>
    /// <returns>The health, or null for another school or a level outside the table.</returns>
    public int? HealthOf(string? school, int level)
        => school is not null && level >= 1 && level <= MaxLevel
           && BySchool.TryGetValue(school.ToLowerInvariant(), out var levels) ? levels[level - 1] : null;

}

/// <summary>
/// Loads and validates classic health tables.
/// </summary>
public static class HealthTableLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "max_level", "provenance", "license_tag", "notes", "levels");
    internal static readonly FrozenSet<string> s_levelKeys = FrozenSet.Create(StringComparer.Ordinal,
        ["level", .. HealthTable.Schools, "notes"]);
    private static readonly Regex s_id = new(@"^health-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the table at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static HealthTable Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the health table does not exist"));
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

        diagnostics.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "max_level", "provenance", "license_tag", "levels"]);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be health-<name> and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        var maxLevel = map.Find("max_level") is { } maxEntry ? diagnostics.ReadInt(maxEntry.Value, "max_level", 1, 200) : null;
        var columns = new Dictionary<string, ImmutableArray<int>.Builder>(StringComparer.Ordinal);
        foreach (var school in HealthTable.Schools) {
            columns[school] = ImmutableArray.CreateBuilder<int>();
        }

        var rows = 0;
        if (map.Find("levels") is { } levelsEntry && diagnostics.ReadList(levelsEntry.Value, "levels") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("levels", i);
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } row) {
                    continue;
                }

                diagnostics.CheckKeys(row, keyPath, s_levelKeys, ["level", .. HealthTable.Schools]);
                var level = row.Find("level") is { } l ? diagnostics.ReadInt(l.Value, YamlTree.Join(keyPath, "level"), 1) : null;
                if (level is not null && level != i + 1) {
                    diagnostics.At(row, keyPath, $"level {level} is out of order; expected {i + 1} (levels run from 1 without gaps)");
                }

                foreach (var school in HealthTable.Schools) {
                    if (row.Find(school) is { } cell
                        && diagnostics.ReadInt(cell.Value, YamlTree.Join(keyPath, school), 1, 1_000_000) is { } health) {
                        columns[school].Add(health);
                    }
                }

                rows++;
            }
        }

        if (maxLevel is not null && rows != maxLevel) {
            diagnostics.At(map, "levels", $"has {rows} levels; max_level is {maxLevel}");
        }

        if (map.Find("notes") is { } notes) {
            _ = diagnostics.ReadString(notes.Value, "notes");
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        var bySchool = new Dictionary<string, ImmutableArray<int>>(StringComparer.Ordinal);
        foreach (var (school, builder) in columns) {
            bySchool[school] = builder.ToImmutable();
        }

        return new HealthTable {
            Id = id!,
            Profiles = profiles,
            MaxLevel = maxLevel!.Value,
            BySchool = bySchool.ToFrozenDictionary(StringComparer.Ordinal),
            SourceFile = display,
        };
    }

}
