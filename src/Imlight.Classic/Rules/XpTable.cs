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
 * CLASSIC XP TABLE
 * ========================================================================
 *
 * PURPOSE:
 * A profile's XP-per-level table (progression/xp-2009.yaml), loaded and
 * checked against xp-table.schema.json, and the cumulative totals the
 * server writes into the client's MagicXPConfig level table.
 *
 * USAGE EXAMPLE:
 * var table = XpTableLoader.Load(Path.Combine(classicDataRoot, profile.Rules.XpTable));
 * var toLeave = table.XpToLeave(1);   // 45: the XP total at which a level 1 wizard reaches level 2
 *
 * NOTE:
 * MagicXPConfig's m_levelInfo[i].m_xpToLevel is the XP total that ends
 * level i; XpToLeave(i) is that number. Levels past max_level keep the
 * client's values.
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
using System.Text.RegularExpressions;
using Imlight.Classic.Spells;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>
/// One level's row of an XP table.
/// </summary>
/// <param name="Level">The level.</param>
/// <param name="XpToNext">XP earned at this level before the next one.</param>
/// <param name="TotalXp">XP held on reaching this level.</param>
public sealed record XpLevel(int Level, int XpToNext, int TotalXp);

/// <summary>
/// XP needed per level.
/// </summary>
public sealed class XpTable {

    public required string Id { get; init; }
    public string? Title { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }

    /// <summary>
    /// The last level in the table.
    /// </summary>
    public required int MaxLevel { get; init; }

    /// <summary>
    /// Rows for levels 1..MaxLevel, in order.
    /// </summary>
    public required ImmutableArray<XpLevel> Levels { get; init; }

    public required string SourceFile { get; init; }

    /// <summary>
    /// The XP total at which a wizard of <paramref name="level"/> reaches the next level.
    /// </summary>
    /// <param name="level">A level from 1 to <see cref="MaxLevel"/>.</param>
    /// <returns>The total, or null outside the table.</returns>
    public int? XpToLeave(int level)
        => level >= 1 && level <= MaxLevel ? Levels[level - 1].TotalXp + Levels[level - 1].XpToNext : null;

    /// <summary>
    /// The XP total a wizard holds on reaching <paramref name="level"/>.
    /// </summary>
    /// <param name="level">A level from 1 to <see cref="MaxLevel"/>.</param>
    /// <returns>The total, or null outside the table.</returns>
    public int? XpToReach(int level)
        => level >= 1 && level <= MaxLevel ? Levels[level - 1].TotalXp : null;

    /// <summary>
    /// The level a wizard holding <paramref name="xp"/> is at, within the table.
    /// </summary>
    /// <param name="xp">The XP total.</param>
    /// <returns>1 to <see cref="MaxLevel"/>.</returns>
    public int LevelAt(int xp) {
        for (var level = MaxLevel; level > 1; level--) {
            if (xp >= Levels[level - 1].TotalXp) {
                return level;
            }
        }

        return 1;
    }

    /// <summary>
    /// Compares the table with the client's XP-to-leave totals (index = level).
    /// </summary>
    /// <param name="clientXpToLeave">The client's m_xpToLevel per level.</param>
    /// <returns>The levels whose total differs, with both totals.</returns>
    public IReadOnlyList<(int Level, int Client, int Classic)> DiffersFrom(IReadOnlyList<int> clientXpToLeave) {
        var result = new List<(int, int, int)>();
        for (var level = 1; level <= MaxLevel && level < clientXpToLeave.Count; level++) {
            var classic = XpToLeave(level)!.Value;
            if (clientXpToLeave[level] != classic) {
                result.Add((level, clientXpToLeave[level], classic));
            }
        }

        return result;
    }

}

/// <summary>
/// Loads and validates XP tables.
/// </summary>
public static class XpTableLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "max_level", "provenance", "later_changes", "license_tag", "notes", "levels");
    private static readonly string[] s_rootRequired = ["id", "profiles", "max_level", "provenance", "levels", "license_tag"];
    internal static readonly FrozenSet<string> s_levelKeys = FrozenSet.Create(StringComparer.Ordinal,
        "level", "xp_to_next", "total_xp", "first_recorded", "wiki_value", "confidence", "notes");
    private static readonly string[] s_levelRequired = ["level", "xp_to_next", "total_xp", "confidence"];
    private static readonly string[] s_confidences = ["verified", "corroborated", "inferred", "placeholder"];

    private static readonly Regex s_id = new(@"^xp-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the table at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The table file, such as classic-data/progression/xp-2009.yaml.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static XpTable Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the XP table does not exist"));
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
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be xp-<name> and match the file name '{expectedId}'");
        }

        var title = map.Find("title") is { } titleEntry ? diagnostics.ReadString(titleEntry.Value, "title") : null;
        var profiles = ReadProfiles(map, diagnostics);
        var maxLevel = map.Find("max_level") is { } maxEntry ? diagnostics.ReadInt(maxEntry.Value, "max_level", 1, 200) : null;
        var levels = ReadLevels(map, maxLevel, diagnostics);
        if (map.Find("license_tag") is { } license) {
            _ = diagnostics.ReadEnum(license.Value, "license_tag", ClassicSpellSchema.LicenseTags);
        }

        foreach (var key in new[] { "provenance", "later_changes" }) {
            if (map.Find(key) is { } list && diagnostics.ReadList(list.Value, key) is { } seq
                && key == "provenance" && seq.Items.IsEmpty) {
                diagnostics.At(seq, key, "needs at least one source");
            }
        }

        if (map.Find("notes") is { } notes) {
            _ = diagnostics.ReadString(notes.Value, "notes");
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new XpTable {
            Id = id!,
            Title = title,
            Profiles = profiles,
            MaxLevel = maxLevel!.Value,
            Levels = levels,
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

    private static ImmutableArray<XpLevel> ReadLevels(YMap map, int? maxLevel, YamlDiagnostics diagnostics) {
        var levels = ImmutableArray.CreateBuilder<XpLevel>();
        if (map.Find("levels") is not { } entry || diagnostics.ReadList(entry.Value, "levels") is not { } list) {
            return levels.ToImmutable();
        }

        var total = 0L;
        for (var i = 0; i < list.Items.Length; i++) {
            var keyPath = YamlTree.Index("levels", i);
            if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } row) {
                continue;
            }

            diagnostics.CheckKeys(row, keyPath, s_levelKeys, s_levelRequired);
            var level = row.Find("level") is { } l ? diagnostics.ReadInt(l.Value, YamlTree.Join(keyPath, "level"), 1) : null;
            var xpToNext = row.Find("xp_to_next") is { } x ? diagnostics.ReadInt(x.Value, YamlTree.Join(keyPath, "xp_to_next"), 1) : null;
            var totalXp = row.Find("total_xp") is { } t ? diagnostics.ReadInt(t.Value, YamlTree.Join(keyPath, "total_xp"), 0) : null;
            if (row.Find("confidence") is { } c) {
                _ = diagnostics.ReadEnum(c.Value, YamlTree.Join(keyPath, "confidence"), s_confidences);
            }

            if (row.Find("wiki_value") is { } w) {
                _ = diagnostics.ReadInt(w.Value, YamlTree.Join(keyPath, "wiki_value"), 1);
                if (row.Find("notes") is null) {
                    diagnostics.At(row, keyPath, "a wiki_value needs notes saying why xp_to_next differs");
                }
            }

            if (level is null || xpToNext is null || totalXp is null) {
                continue;
            }

            if (level != i + 1) {
                diagnostics.At(row, keyPath, $"level {level} is out of order; expected {i + 1} (levels run from 1 without gaps)");
            }

            if (totalXp != total) {
                diagnostics.At(row, keyPath, $"total_xp {totalXp} should be {total}, the sum of xp_to_next below level {level}");
            }

            total += xpToNext.Value;
            if (total > int.MaxValue) {
                diagnostics.At(row, keyPath, "the XP totals overflow");
                break;
            }

            levels.Add(new XpLevel(level.Value, xpToNext.Value, totalXp.Value));
        }

        if (maxLevel is not null && levels.Count != maxLevel) {
            diagnostics.At(list, "levels", $"has {levels.Count} valid levels; max_level is {maxLevel}");
        }

        return levels.ToImmutable();
    }

}
