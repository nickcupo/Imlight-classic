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
 * CLASSIC LEVEL STATS (MANA, POWER PIP CHANCE, TRAINING POINTS)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: per-level mana, power pip chance and training points for a level
 * range (progression/level-stats-*.yaml, rules.level_stats), written into the
 * client's shared MagicXPConfig level table like the XP and health tables.
 * The celestia-2010 file covers levels 51-60 with the client's values; its
 * status (unverified until the owner rules, decision D7) is logged at startup
 * so the values are easy to find and replace.
 *
 * USAGE EXAMPLE:
 * var stats = LevelStatsLoader.Load(path);
 * var row = stats.Row(55);   // mana 120, pip chance 0.40, 1 training point
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/16/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>One level's mana, power pip chance and training points.</summary>
public sealed record LevelStatsRow(int Level, int Mana, double PowerPipChance, int TrainingPoints);

/// <summary>CLASSIC: mana, power pip chance and training points for a run of levels.</summary>
public sealed class LevelStats {

    /// <summary>The statuses a table may declare: unverified (client values awaiting a ruling), accepted (the owner
    /// accepted them), verified (a dated source gives them).</summary>
    public static ImmutableArray<string> Statuses { get; } = ["unverified", "accepted", "verified"];

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }
    public required string Status { get; init; }
    public string? Decision { get; init; }
    public required int MinLevel { get; init; }
    public required int MaxLevel { get; init; }
    public required ImmutableArray<LevelStatsRow> Rows { get; init; }
    public required string SourceFile { get; init; }

    /// <summary>The row of <paramref name="level"/>, or null outside the table.</summary>
    public LevelStatsRow? Row(int level)
        => level >= MinLevel && level <= MaxLevel ? Rows[level - MinLevel] : null;

}

/// <summary>Loads and validates <see cref="LevelStats"/> files.</summary>
public static class LevelStatsLoader {

    private static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "status", "decision", "min_level", "max_level", "provenance", "license_tag", "notes", "levels");
    private static readonly FrozenSet<string> s_levelKeys = FrozenSet.Create(StringComparer.Ordinal,
        "level", "mana", "power_pip_chance", "training_points", "notes");
    private static readonly Regex s_id = new(@"^level-stats-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <summary>Loads the table at <paramref name="path"/>.</summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static LevelStats Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the level stats table does not exist"));
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

        diagnostics.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "status", "min_level", "max_level", "provenance", "license_tag", "levels"]);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be level-stats-<name> and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        var status = map.Find("status") is { } s ? diagnostics.ReadEnum(s.Value, "status", LevelStats.Statuses) : null;
        var decision = map.Find("decision") is { } dec ? diagnostics.ReadString(dec.Value, "decision") : null;
        var minLevel = map.Find("min_level") is { } minEntry ? diagnostics.ReadInt(minEntry.Value, "min_level", 1, 200) : null;
        var maxLevel = map.Find("max_level") is { } maxEntry ? diagnostics.ReadInt(maxEntry.Value, "max_level", 1, 200) : null;
        if (minLevel is not null && maxLevel is not null && maxLevel < minLevel) {
            diagnostics.At(map.Find("max_level")!.Value, "max_level", $"max_level {maxLevel} is below min_level {minLevel}");
        }

        var rows = ImmutableArray.CreateBuilder<LevelStatsRow>();
        var count = 0;
        if (map.Find("levels") is { } levelsEntry && diagnostics.ReadList(levelsEntry.Value, "levels") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("levels", i);
                count++;
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } row) {
                    continue;
                }

                diagnostics.CheckKeys(row, keyPath, s_levelKeys, ["level", "mana", "power_pip_chance", "training_points"]);
                string At(string key) => YamlTree.Join(keyPath, key);
                var level = row.Find("level") is { } l ? diagnostics.ReadInt(l.Value, At("level"), 1, 200) : null;
                if (level is not null && minLevel is not null && level != minLevel + i) {
                    diagnostics.At(row, keyPath, $"level {level} is out of order; expected {minLevel + i} (levels run from min_level without gaps)");
                }

                var mana = row.Find("mana") is { } m ? diagnostics.ReadInt(m.Value, At("mana"), 0, 100_000) : null;
                var pips = row.Find("power_pip_chance") is { } p ? diagnostics.ReadFraction(p.Value, At("power_pip_chance")) : null;
                var points = row.Find("training_points") is { } t ? diagnostics.ReadInt(t.Value, At("training_points"), 0, 10) : null;
                if (row.Find("notes") is { } notes) {
                    _ = diagnostics.ReadString(notes.Value, At("notes"));
                }

                if (level is not null && mana is not null && pips is not null && points is not null) {
                    rows.Add(new LevelStatsRow(level.Value, mana.Value, (double) pips.Value, points.Value));
                }
            }
        }

        if (minLevel is not null && maxLevel is not null && count != maxLevel - minLevel + 1) {
            diagnostics.At(map, "levels", $"has {count} levels; min_level {minLevel} to max_level {maxLevel} needs {maxLevel - minLevel + 1}");
        }

        if (map.Find("notes") is { } fileNotes) {
            _ = diagnostics.ReadString(fileNotes.Value, "notes");
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new LevelStats {
            Id = id!,
            Profiles = profiles,
            Status = status!,
            Decision = decision,
            MinLevel = minLevel!.Value,
            MaxLevel = maxLevel!.Value,
            Rows = rows.ToImmutable(),
            SourceFile = display,
        };
    }

}
