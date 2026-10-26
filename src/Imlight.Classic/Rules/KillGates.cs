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
 * CLASSIC DOOR KILL GATES
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: reads rules.kill_gates (classic-data/rules/kill-gates-2009.yaml):
 * zone triggers that open a door on Monster_Killed but whose kill
 * requirement names a creature the room never spawns, with the creature
 * to wait for instead (the Dragonspyre Detention and Necropolis gauntlets'
 * third room).
 *
 * USAGE EXAMPLE:
 * var gates = KillGatesLoader.Load(path);
 * gates.For(zonePath) // the fixes of that zone
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/26/2026
 */

#nullable enable

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>One trigger's kill-requirement entry <see cref="From"/> becomes <see cref="To"/>.</summary>
public sealed record KillGateFix(string Zone, string Trigger, string From, string To);

/// <summary>The kill-gate fixes of a profile.</summary>
public sealed record KillGates(string Id, ImmutableArray<string> Profiles, ImmutableArray<KillGateFix> Fixes, string SourceFile) {

    public static KillGates Empty { get; } = new("", [], [], "");

    /// <summary>The fixes of <paramref name="zonePath"/>.</summary>
    public IEnumerable<KillGateFix> For(string? zonePath)
        => Fixes.Where(fix => string.Equals(fix.Zone, zonePath, StringComparison.OrdinalIgnoreCase));

}

/// <summary>
/// Loads and validates the kill-gate fixes.
/// </summary>
public static class KillGatesLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "license_tag", "notes", "fixes", "provenance");
    private static readonly FrozenSet<string> s_fixKeys = FrozenSet.Create(StringComparer.Ordinal,
        "zone", "trigger", "from", "to", "confidence", "status", "note");
    private static readonly Regex s_id = new(@"^kill-gates-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);
    private static readonly Regex s_adjRef = new(@"^[A-Za-z0-9_-]+\.AdjRef\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the fixes at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static KillGates Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the kill-gate fixes do not exist"));
        }

        var diagnostics = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, diagnostics);
        if (root is not YMap map) {
            if (root is not null) {
                diagnostics.At(root, "", $"the root must be a mapping, got {root.Describe()}");
            }

            throw diagnostics.ToException();
        }

        diagnostics.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "license_tag", "fixes", "provenance"]);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be kill-gates-<name> and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        var fixes = ImmutableArray.CreateBuilder<KillGateFix>();
        var seen = new HashSet<(string, string, string)>();
        if (map.Find("fixes") is { } f && diagnostics.ReadList(f.Value, "fixes") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("fixes", i);
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } row) {
                    continue;
                }

                diagnostics.CheckKeys(row, keyPath, s_fixKeys, ["zone", "trigger", "from", "to", "confidence", "status", "note"]);
                string? Read(string key) => row.Find(key) is { } e ? diagnostics.ReadString(e.Value, YamlTree.Join(keyPath, key)) : null;
                var zone = Read("zone");
                var trigger = Read("trigger");
                var from = Read("from");
                var to = Read("to");
                _ = Read("confidence");
                _ = Read("note");
                if (row.Find("status") is { } status) {
                    _ = diagnostics.ReadEnum(status.Value, YamlTree.Join(keyPath, "status"), ["verified", "unverified"]);
                }

                foreach (var (value, key) in new[] { (from, "from"), (to, "to") }) {
                    if (value is not null && !s_adjRef.IsMatch(value)) {
                        diagnostics.At(row, YamlTree.Join(keyPath, key), $"'{value}' must be <object name>.AdjRef");
                    }
                }

                if (zone is null || trigger is null || from is null || to is null) {
                    continue;
                }

                if (!seen.Add((zone.ToLowerInvariant(), trigger, from))) {
                    diagnostics.At(row, keyPath, $"{zone} {trigger} {from} is listed twice");
                }

                fixes.Add(new KillGateFix(zone, trigger, from, to));
            }
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new KillGates(id!, profiles, fixes.ToImmutable(), display);
    }

}
