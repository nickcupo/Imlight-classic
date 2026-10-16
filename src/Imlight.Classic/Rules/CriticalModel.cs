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
 * CLASSIC CRITICAL AND BLOCK MODEL (2010-2014 PERCENT MODEL)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the pre-2015 critical and block model of rules/critical-*.yaml
 * (rules.critical), as the April 2014 client (Wizard_1_240) computes it:
 * - every equipped stat effect converts its own rating to a chance with the
 *   wearer's level: max(0, rating * per_rating - max(0, level - from_level)
 *   * level_penalty), at least min_piece_chance when the rating is not 0;
 *   the pieces add up (all-school pieces and per-school pieces apart);
 * - one roll per cast, uniform in [0, max(roll_max, chance)) percent points:
 *   the cast crits when its crit chance is above the roll;
 * - each damage target blocks when its block chance is above the SAME roll,
 *   so a block at or above the caster's crit always blocks; heals are never
 *   blocked;
 * - a landed, unblocked critical multiplies the effect by crit_multiplier;
 *   no PvP difference.
 * Client functions: 0x012e0260 / 0x012e0330 (rating to chance), 0x012e0400
 * (per-effect sum), 0x010b4430 (the roll, "Critical roll: %.2f chance: %.2f"),
 * 0x010ae010 (the block test and the x2 through 0x010ad350 with 0x100/256).
 * See docs/research/crit-block-2010.md.
 *
 * USAGE EXAMPLE:
 * var model = CriticalModelLoader.Load(path);
 * var chance = model.CritPieceChance(30, 55); // 0.0625 (30 * 0.00225 - 5 * 0.001)
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/16/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.IO;
using System.Text.RegularExpressions;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>CLASSIC: the 2010-2014 percent model of critical and block (rules.critical).</summary>
public sealed class CriticalModel {

    /// <summary>The statuses a file may declare: unverified (client values with no source dated in the window),
    /// accepted (the owner accepted them), verified (a dated source gives them).</summary>
    public static ImmutableArray<string> Statuses { get; } = ["unverified", "accepted", "verified"];

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }
    public required string Status { get; init; }
    public string? Decision { get; init; }
    public required string SourceFile { get; init; }

    /// <summary>Chance per point of critical rating at or below <see cref="CritPenaltyFromLevel"/> (0.00225).</summary>
    public required double CritPerRating { get; init; }

    /// <summary>Chance per point of block rating at or below <see cref="BlockPenaltyFromLevel"/> (0.00275).</summary>
    public required double BlockPerRating { get; init; }

    /// <summary>Chance taken off each critical piece per level above <see cref="CritPenaltyFromLevel"/> (0.001).</summary>
    public required double CritLevelPenalty { get; init; }

    /// <summary>Chance taken off each block piece per level above <see cref="BlockPenaltyFromLevel"/> (0.001).</summary>
    public required double BlockLevelPenalty { get; init; }

    public required int CritPenaltyFromLevel { get; init; }
    public required int BlockPenaltyFromLevel { get; init; }

    /// <summary>The least chance a piece with a rating gives (0.01).</summary>
    public required double MinPieceChance { get; init; }

    /// <summary>A landed, unblocked critical multiplies the effect by this (2).</summary>
    public required int CritMultiplier { get; init; }

    /// <summary>The roll is uniform in [0, max(RollMax, chance)) percent points (99).</summary>
    public required int RollMax { get; init; }

    /// <summary>The chance (0-1) one critical stat effect gives a wearer of <paramref name="level"/>.</summary>
    public double CritPieceChance(double rating, int level)
        => PieceChance(rating, level, CritPerRating, CritLevelPenalty, CritPenaltyFromLevel);

    /// <summary>The chance (0-1) one block stat effect gives a wearer of <paramref name="level"/>.</summary>
    public double BlockPieceChance(double rating, int level)
        => PieceChance(rating, level, BlockPerRating, BlockLevelPenalty, BlockPenaltyFromLevel);

    private double PieceChance(double rating, int level, double perRating, double penalty, int fromLevel) {
        // The client also floors a negative rating to the minimum; no 2010 item has one, so it gives nothing here.
        if (rating <= 0) {
            return 0;
        }

        var chance = Math.Max(0, rating * perRating - Math.Max(0, level - fromLevel) * penalty);

        return chance < MinPieceChance ? MinPieceChance : chance;
    }

    /// <summary>
    /// The crit roll: uniform in [0, max(RollMax, chance)) percent points, from <paramref name="unit"/> in [0, 1).
    /// </summary>
    public double Roll(double critChancePoints, double unit) => unit * Math.Max(RollMax, critChancePoints);

    /// <summary>The cast crits when its chance (percent points) is above the roll.</summary>
    public static bool Crits(double critChancePoints, double roll) => critChancePoints > roll;

    /// <summary>A damage target blocks a landed critical when its block chance (percent points) is above the same roll.</summary>
    public static bool Blocks(double blockChancePoints, double roll) => blockChancePoints > roll;

}

/// <summary>Loads and validates <see cref="CriticalModel"/> files.</summary>
public static class CriticalModelLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "status", "decision", "provenance", "license_tag", "notes",
        "crit_per_rating", "block_per_rating", "crit_level_penalty", "block_level_penalty",
        "crit_penalty_from_level", "block_penalty_from_level", "min_piece_chance", "crit_multiplier", "roll_max");
    private static readonly string[] s_required = ["id", "profiles", "status", "provenance", "license_tag",
        "crit_per_rating", "block_per_rating", "crit_level_penalty", "block_level_penalty",
        "crit_penalty_from_level", "block_penalty_from_level", "min_piece_chance", "crit_multiplier", "roll_max"];
    private static readonly Regex s_id = new(@"^critical-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <summary>Loads the model at <paramref name="path"/>.</summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static CriticalModel Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the critical model does not exist"));
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

        diagnostics.CheckKeys(map, "", s_rootKeys, s_required);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be critical-<name> and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        var status = map.Find("status") is { } s ? diagnostics.ReadEnum(s.Value, "status", CriticalModel.Statuses) : null;
        var decision = map.Find("decision") is { } dec ? diagnostics.ReadString(dec.Value, "decision") : null;
        double? Fraction(string key) => map.Find(key) is { } e ? diagnostics.ReadFraction(e.Value, key) : null;
        int? Int(string key, int min, int max) => map.Find(key) is { } e ? diagnostics.ReadInt(e.Value, key, min, max) : null;
        var critPer = Fraction("crit_per_rating");
        var blockPer = Fraction("block_per_rating");
        var critPenalty = Fraction("crit_level_penalty");
        var blockPenalty = Fraction("block_level_penalty");
        var critFrom = Int("crit_penalty_from_level", 0, 200);
        var blockFrom = Int("block_penalty_from_level", 0, 200);
        var minPiece = Fraction("min_piece_chance");
        var multiplier = Int("crit_multiplier", 1, 10);
        var rollMax = Int("roll_max", 1, 1000);
        if (map.Find("notes") is { } notes) {
            _ = diagnostics.ReadString(notes.Value, "notes");
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new CriticalModel {
            Id = id!,
            Profiles = profiles,
            Status = status!,
            Decision = decision,
            SourceFile = display,
            CritPerRating = critPer!.Value,
            BlockPerRating = blockPer!.Value,
            CritLevelPenalty = critPenalty!.Value,
            BlockLevelPenalty = blockPenalty!.Value,
            CritPenaltyFromLevel = critFrom!.Value,
            BlockPenaltyFromLevel = blockFrom!.Value,
            MinPieceChance = minPiece!.Value,
            CritMultiplier = multiplier!.Value,
            RollMax = rollMax!.Value,
        };
    }

}
