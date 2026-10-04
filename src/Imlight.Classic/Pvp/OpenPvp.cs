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
 * CLASSIC OPEN PVP
 * ========================================================================
 *
 * PURPOSE:
 * The old open PvP (owner ruling 2026-10-01): duel circles in the Wizard
 * City Arena (classic-data/pvp/open-pvp-*.yaml) that wizards walk into,
 * pick a side by the half they walk in from, and fight 1v1 up to 4v4, with
 * no ranks, tickets, restrictions or defeat penalty. This file holds the
 * data and the pure rules: which side a wizard joins, and when a circle's
 * fight starts.
 *
 * USAGE EXAMPLE:
 * var config = OpenPvpLoader.Load(path);
 * var side = OpenPvpRules.ChooseSide(distanceToA, distanceToB, seatedA, seatedB);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Imlight.Classic.Rules;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Pvp;

/// <summary>One server-placed PvP duel circle.</summary>
public sealed record OpenPvpCircle(string Tag, float X, float Y, float Z, float Yaw);

/// <summary>The open PvP circles of one zone.</summary>
public sealed class OpenPvpConfig {

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }
    public required string Zone { get; init; }
    public required uint Template { get; init; }
    public required string SigilType { get; init; }
    public required float Radius { get; init; }
    public required int LobbyTimeoutSeconds { get; init; }
    public required ImmutableArray<OpenPvpCircle> Circles { get; init; }
    public required string SourceFile { get; init; }

}

/// <summary>What a PvP circle does next.</summary>
public enum OpenPvpStart {

    /// <summary>Not both sides have a wizard: wait.</summary>
    Wait,

    /// <summary>Both sides have a wizard: count down.</summary>
    CountDown,

    /// <summary>Start the fight now (full circle, everyone ready, or the countdown ran out).</summary>
    Start

}

/// <summary>
/// The open PvP rules.
/// </summary>
public static class OpenPvpRules {

    /// <summary>Wizards per side.</summary>
    public const int SideSize = 4;

    /// <summary>
    /// The side (0 or 1) a wizard joins: the side whose half they stand nearer to, or the other one when it is full;
    /// -1 when both are full.
    /// </summary>
    public static int ChooseSide(double distanceToSide0, double distanceToSide1, int seated0, int seated1) {
        var preferred = distanceToSide0 <= distanceToSide1 ? 0 : 1;
        int Seated(int side) => side == 0 ? seated0 : seated1;
        if (Seated(preferred) < SideSize) {
            return preferred;
        }

        return Seated(1 - preferred) < SideSize ? 1 - preferred : -1;
    }

    /// <summary>When a circle with these seats starts its fight.</summary>
    /// <param name="seated0">Wizards on side 0.</param>
    /// <param name="seated1">Wizards on side 1.</param>
    /// <param name="ready">Wizards who said they are ready.</param>
    /// <param name="countdownOver">True when the countdown since both sides had a wizard has run out.</param>
    public static OpenPvpStart Decide(int seated0, int seated1, int ready, bool countdownOver) {
        if (seated0 == 0 || seated1 == 0) {
            return OpenPvpStart.Wait;
        }

        var seated = seated0 + seated1;
        if (seated == SideSize * 2 || ready >= seated || countdownOver) {
            return OpenPvpStart.Start;
        }

        return OpenPvpStart.CountDown;
    }

    /// <summary>
    /// Whether casting costs the wizard mana. 2009: "You will not lose mana when dueling in the duel arena" (wiki
    /// Health_and_Mana oldid 41879, 2009-09-13), so a classic PvP duel spends none; every other duel spends a mana per pip.
    /// </summary>
    /// <param name="pvpDuel">The duel is a PvP duel (m_bPVP).</param>
    /// <param name="classicRules">A classic profile is active.</param>
    public static bool CastingCostsMana(bool pvpDuel, bool classicRules) => !(pvpDuel && classicRules);

}

/// <summary>
/// Loads and validates open PvP circles.
/// </summary>
public static class OpenPvpLoader {

    private static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "provenance", "license_tag", "notes", "zone", "template", "sigil_type", "radius",
        "lobby_timeout_seconds", "circles");
    private static readonly FrozenSet<string> s_circleKeys = FrozenSet.Create(StringComparer.Ordinal, "tag", "x", "y", "z", "yaw", "source");
    private static readonly Regex s_id = new(@"^open-pvp-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <summary>Loads the circles at <paramref name="path"/>.</summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static OpenPvpConfig Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the open PvP file does not exist"));
        }

        var diagnostics = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, diagnostics);
        if (root is not YMap map) {
            if (root is not null) {
                diagnostics.At(root, "", $"the root must be a mapping, got {root.Describe()}");
            }

            throw diagnostics.ToException();
        }

        diagnostics.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "provenance", "license_tag", "zone", "template",
            "sigil_type", "radius", "lobby_timeout_seconds", "circles"]);
        string? Str(string key) => map.Find(key) is { } e ? diagnostics.ReadString(e.Value, key) : null;
        var id = Str("id");
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be open-pvp-<name> and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        var zone = Str("zone");
        var sigilType = Str("sigil_type");
        var template = map.Find("template") is { } t ? diagnostics.ReadInt(t.Value, "template", 1) : null;
        var radius = map.Find("radius") is { } r ? ReadFloat(r.Value, "radius", diagnostics) : null;
        if (radius is <= 0 or > 5000) {
            diagnostics.At(map.Find("radius")!.Value, "radius", "must be above 0 and at most 5000");
        }

        var timeout = map.Find("lobby_timeout_seconds") is { } lt ? diagnostics.ReadInt(lt.Value, "lobby_timeout_seconds", 10, 3600) : null;
        var circles = ImmutableArray.CreateBuilder<OpenPvpCircle>();
        var tags = new HashSet<string>(StringComparer.Ordinal);
        if (map.Find("circles") is { } circlesEntry && diagnostics.ReadList(circlesEntry.Value, "circles") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("circles", i);
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } circle) {
                    continue;
                }

                diagnostics.CheckKeys(circle, keyPath, s_circleKeys, ["tag", "x", "y", "z", "yaw", "source"]);
                var tag = circle.Find("tag") is { } tg ? diagnostics.ReadString(tg.Value, YamlTree.Join(keyPath, "tag")) : null;
                float? Coord(string key) => circle.Find(key) is { } c ? ReadFloat(c.Value, YamlTree.Join(keyPath, key), diagnostics) : null;
                var (x, y, z, yaw) = (Coord("x"), Coord("y"), Coord("z"), Coord("yaw"));
                if (circle.Find("source") is { } s) {
                    _ = diagnostics.ReadInt(s.Value, YamlTree.Join(keyPath, "source"), 0);
                }

                if (tag is not null && !tags.Add(tag)) {
                    diagnostics.At(circle, keyPath, $"tag '{tag}' is listed twice");
                }

                if (tag is not null && x is not null && y is not null && z is not null && yaw is not null) {
                    circles.Add(new OpenPvpCircle(tag, x.Value, y.Value, z.Value, yaw.Value));
                }
            }
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new OpenPvpConfig {
            Id = id!, Profiles = profiles, Zone = zone!, Template = (uint) template!.Value, SigilType = sigilType!,
            Radius = radius!.Value, LobbyTimeoutSeconds = timeout!.Value, Circles = circles.ToImmutable(), SourceFile = display,
        };
    }

    private static float? ReadFloat(YNode node, string path, YamlDiagnostics diagnostics) {
        if (node is YScalar scalar && float.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && float.IsFinite(value)) {
            return value;
        }

        diagnostics.At(node, path, "must be a number");

        return null;
    }

}
