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
 * CLASSIC INSTANCE RESET RULES
 * ========================================================================
 *
 * PURPOSE:
 * When a wizard's dungeon copy starts fresh, and which zones form one
 * dungeon (classic-data/rules/instance-resets-2009.yaml): the 2009 help pages
 * Help_Instances00-03 and the dated wiki revisions in the file's provenance.
 * The groups are generated from client zone data by
 * tools/datamine/instance_groups.py.
 *
 * USAGE EXAMPLE:
 * var rules = InstanceResetRulesLoader.Load(path);
 * var golem = rules.GroupOf("WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_3");   // kind Gauntlet, 5 zones
 *
 * NOTE:
 * InstanceResetRules.BuiltIn (the Golem Tower only, 30 minutes) is what a
 * profile without rules.instance_resets uses, as before the general rule.
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

/// <summary>How a dungeon resets: an ordinary dungeon keeps a copy left "any other way" for a while, a gauntlet never.</summary>
public enum InstanceKind {
    Dungeon,
    Gauntlet,
}

/// <summary>One dungeon: the instanced zones a wizard moves between without leaving it.</summary>
public sealed record InstanceGroup(string Id, InstanceKind Kind, ImmutableArray<string> Zones) {

    /// <summary>True when <paramref name="zone"/> is one of the group's zones (any case).</summary>
    public bool Contains(string? zone) => zone is not null && Zones.Contains(zone, StringComparer.OrdinalIgnoreCase);

}

/// <summary>
/// The dungeon reset rules: the dungeons and how long an empty or left copy is kept.
/// </summary>
public sealed class InstanceResetRules {

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }
    public required TimeSpan EmptyLifetime { get; init; }
    public required TimeSpan ReturnWindow { get; init; }
    public required ImmutableArray<InstanceGroup> Groups { get; init; }
    public required string SourceFile { get; init; }

    private FrozenDictionary<string, InstanceGroup>? _byZone;

    /// <summary>The dungeon <paramref name="zone"/> belongs to, or null when it is not a dungeon zone.</summary>
    public InstanceGroup? GroupOf(string? zone) {
        if (string.IsNullOrEmpty(zone)) {
            return null;
        }

        _byZone ??= Groups.SelectMany(g => g.Zones.Select(z => (z, g)))
            .ToFrozenDictionary(p => p.z, p => p.g, StringComparer.OrdinalIgnoreCase);

        return _byZone.GetValueOrDefault(zone);
    }

    /// <summary>The Golem Tower's five floors, bottom to top (the one gauntlet the server knew before the general rule).</summary>
    public static readonly ImmutableArray<string> GolemTowerFloors = [
        "WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_1",
        "WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_2",
        "WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_3",
        "WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_4",
        "WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_5",
    ];

    /// <summary>The rules of a profile that names no file: the Golem Tower gauntlet, 30-minute windows.</summary>
    public static InstanceResetRules BuiltIn { get; } = new() {
        Id = "built-in",
        Profiles = [],
        EmptyLifetime = TimeSpan.FromMinutes(30),
        ReturnWindow = TimeSpan.FromMinutes(30),
        Groups = [new InstanceGroup("WC_Golem_Tower", InstanceKind.Gauntlet, GolemTowerFloors)],
        SourceFile = "",
    };

}

/// <summary>
/// Loads and validates the dungeon reset rules.
/// </summary>
public static class InstanceResetRulesLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "license_tag", "notes", "empty_lifetime_minutes", "return_window_minutes", "groups",
        "provenance");
    private static readonly FrozenSet<string> s_groupKeys = FrozenSet.Create(StringComparer.Ordinal, "id", "kind", "source", "zones");
    private static readonly string[] s_kinds = ["dungeon", "gauntlet"];
    private static readonly Regex s_id = new(@"^instance-resets-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the rules at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static InstanceResetRules Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the instance reset rules do not exist"));
        }

        var diagnostics = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, diagnostics);
        if (root is not YMap map) {
            if (root is not null) {
                diagnostics.At(root, "", $"the root must be a mapping, got {root.Describe()}");
            }

            throw diagnostics.ToException();
        }

        diagnostics.CheckKeys(map, "", s_rootKeys,
            ["id", "profiles", "license_tag", "empty_lifetime_minutes", "return_window_minutes", "groups", "provenance"]);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be instance-resets-<name> and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        var empty = map.Find("empty_lifetime_minutes") is { } e ? diagnostics.ReadInt(e.Value, "empty_lifetime_minutes", 1, 1440) : null;
        var window = map.Find("return_window_minutes") is { } w ? diagnostics.ReadInt(w.Value, "return_window_minutes", 0, 1440) : null;

        var groups = ImmutableArray.CreateBuilder<InstanceGroup>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var zoneOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (map.Find("groups") is { } g && diagnostics.ReadList(g.Value, "groups") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("groups", i);
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } row) {
                    continue;
                }

                diagnostics.CheckKeys(row, keyPath, s_groupKeys, ["id", "kind", "zones"]);
                var gid = row.Find("id") is { } gi ? diagnostics.ReadString(gi.Value, YamlTree.Join(keyPath, "id")) : null;
                var kind = row.Find("kind") is { } k ? diagnostics.ReadEnum(k.Value, YamlTree.Join(keyPath, "kind"), s_kinds) : null;
                var source = row.Find("source") is { } s ? diagnostics.ReadString(s.Value, YamlTree.Join(keyPath, "source")) : null;
                var zones = ImmutableArray.CreateBuilder<string>();
                if (row.Find("zones") is { } zn && diagnostics.ReadList(zn.Value, YamlTree.Join(keyPath, "zones")) is { } zl) {
                    for (var j = 0; j < zl.Items.Length; j++) {
                        var zonePath = YamlTree.Index(YamlTree.Join(keyPath, "zones"), j);
                        if (diagnostics.ReadString(zl.Items[j], zonePath) is not { } zone) {
                            continue;
                        }

                        if (zoneOwner.TryGetValue(zone, out var other)) {
                            diagnostics.At(zl.Items[j], zonePath, $"zone {zone} is already in group {other}");
                            continue;
                        }

                        zoneOwner[zone] = gid ?? "?";
                        zones.Add(zone);
                    }

                    if (zl.Items.IsEmpty) {
                        diagnostics.At(zl, YamlTree.Join(keyPath, "zones"), "needs at least one zone");
                    }
                }

                if (gid is not null && !ids.Add(gid)) {
                    diagnostics.At(row, keyPath, $"group {gid} is listed twice");
                }

                if (kind == "gauntlet" && string.IsNullOrWhiteSpace(source)) {
                    diagnostics.At(row, keyPath, $"gauntlet {gid} needs the dated source that calls it one");
                }

                if (gid is not null && kind is not null) {
                    groups.Add(new InstanceGroup(gid, kind == "gauntlet" ? InstanceKind.Gauntlet : InstanceKind.Dungeon,
                        zones.ToImmutable()));
                }
            }
        }

        if (map.Find("notes") is { } notes) {
            _ = diagnostics.ReadString(notes.Value, "notes");
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new InstanceResetRules {
            Id = id!,
            Profiles = profiles,
            EmptyLifetime = TimeSpan.FromMinutes(empty!.Value),
            ReturnWindow = TimeSpan.FromMinutes(window!.Value),
            Groups = groups.ToImmutable(),
            SourceFile = display,
        };
    }

}
