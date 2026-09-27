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
 * CLASSIC ZONE MAP
 * ========================================================================
 * 
 * PURPOSE:
 * Loads classic-data/zones/worlds.yaml and the zone-overrides files it
 * includes, validates them against zones.schema.json and builds the
 * ZoneWorldMap.
 * 
 * USAGE EXAMPLE:
 * var map = ZoneWorldMapLoader.Load("/opt/w101c/classic-data/zones/worlds.yaml");
 * 
 * NOTE:
 * Every schema world needs an entry. Prefixes must be unique (ignoring
 * case) across worlds and areas, and override patterns across all files.
 * Overrides can only close a zone or reassign its world, never open one.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Zones;

/// <summary>
/// Loads and validates the zone-to-world map.
/// </summary>
public static class ZoneWorldMapLoader {

    private const string WorldsKind = "zone-worlds";
    private const string OverridesKind = "zone-overrides";

    private static readonly FrozenSet<string> s_worldsFileKeys = FrozenSet.Create(StringComparer.Ordinal,
        "kind", "version", "license_tag", "fallback_world", "includes", "worlds", "areas", "overrides", "notes");
    private static readonly string[] s_worldsFileRequired =
        ["kind", "version", "license_tag", "fallback_world", "worlds", "areas", "overrides"];
    private static readonly FrozenSet<string> s_overridesFileKeys = FrozenSet.Create(StringComparer.Ordinal,
        "kind", "version", "license_tag", "source", "overrides", "notes");
    private static readonly string[] s_overridesFileRequired = ["kind", "version", "license_tag", "source", "overrides"];
    private static readonly FrozenSet<string> s_worldKeys = FrozenSet.Create(StringComparer.Ordinal,
        "name", "hub_key", "prefixes", "notes");
    private static readonly string[] s_worldRequired = ["name", "prefixes"];
    private static readonly FrozenSet<string> s_areaKeys = FrozenSet.Create(StringComparer.Ordinal,
        "name", "prefixes", "access", "feature", "introduced", "introduced_after", "reason", "confidence", "message");
    private static readonly string[] s_areaRequired = ["name", "prefixes", "access", "reason", "confidence"];
    private static readonly FrozenSet<string> s_overrideKeys = FrozenSet.Create(StringComparer.Ordinal,
        "zone", "world", "feature", "access", "introduced", "introduced_after", "reason", "confidence", "source", "message");
    private static readonly string[] s_overrideRequired = ["zone", "reason", "confidence"];
    private static readonly string[] s_overrideEffects = ["world", "feature", "access", "introduced", "introduced_after"];
    private static readonly string[] s_licenseTags = ["own", "cc-by-nc-sa-spiraldb"];
    private static readonly string[] s_confidences = ["high", "medium", "low"];
    private static readonly string[] s_areaAccess = ["allow", "deny", "feature"];

    private static readonly Regex s_hubKey = new(@"^[A-Za-z0-9_]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex s_areaId = new(@"^[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant);
    private static readonly Regex s_includeName = new(@"^[a-z0-9][a-z0-9-]*\.yaml\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the zone map from its entry file and the files it includes.
    /// </summary>
    /// <param name="worldsYamlPath">The path of worlds.yaml.</param>
    /// <returns>The map.</returns>
    /// <exception cref="ClassicDataException">A file is missing or invalid; every error is reported.</exception>
    public static ZoneWorldMap Load(string worldsYamlPath) {
        var path = Path.GetFullPath(worldsYamlPath);
        var display = ClassicDataLocator.DisplayPath(path);
        if (!File.Exists(path)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the zone map file does not exist"));
        }

        var diagnostics = new YamlDiagnostics();
        var root = ParseFile(path, diagnostics, WorldsKind, s_worldsFileKeys, s_worldsFileRequired);
        if (root is null) {
            throw diagnostics.ToException();
        }

        var fileName = Path.GetFileName(path);
        var duplicates = new Duplicates(diagnostics);
        var worlds = ReadWorlds(root, diagnostics, duplicates);
        var areas = ReadAreas(root, diagnostics, duplicates);
        var overrides = new List<ZoneOverride>(ReadOverrides(root, fileName, diagnostics, duplicates));
        var sourceFiles = new List<string> { path };

        if (root.Find("includes") is { } includesEntry && diagnostics.ReadList(includesEntry.Value, "includes") is { } includes) {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < includes.Items.Length; i++) {
                var keyPath = YamlTree.Index("includes", i);
                var item = includes.Items[i];
                if (diagnostics.ReadString(item, keyPath) is not { } name) {
                    continue;
                }

                if (!s_includeName.IsMatch(name)) {
                    diagnostics.At(item, keyPath, $"'{name}' is not a valid include (a lowercase file name ending in .yaml)");
                    continue;
                }

                if (!seen.Add(name)) {
                    diagnostics.At(item, keyPath, $"'{name}' is included twice");
                    continue;
                }

                var includePath = Path.Combine(Path.GetDirectoryName(path)!, name);
                if (!File.Exists(includePath)) {
                    diagnostics.At(item, keyPath, $"includes '{name}', which does not exist");
                    continue;
                }

                var kind = PeekKind(includePath);
                if (!string.Equals(kind, OverridesKind, StringComparison.Ordinal)) {
                    diagnostics.At(item, keyPath, $"includes '{name}', which is a '{kind ?? "unknown"}' file, not {OverridesKind}");
                    continue;
                }

                var include = ParseFile(includePath, diagnostics, OverridesKind, s_overridesFileKeys, s_overridesFileRequired);
                if (include is null) {
                    continue;
                }

                if (include.Find("source") is { } source) {
                    _ = diagnostics.ReadString(source.Value, "source");
                }

                overrides.AddRange(ReadOverrides(include, name, diagnostics, duplicates));
                sourceFiles.Add(Path.GetFullPath(includePath));
            }
        }

        foreach (var worldId in ClassicSchema.WorldIds) {
            if (!worlds.Any(world => world.Id == worldId) && root.Find("worlds")?.Value is YMap worldsMap) {
                diagnostics.At(worldsMap, YamlTree.Join("worlds", worldId), $"world '{worldId}' has no entry; every schema world needs one");
            }
        }

        string? fallbackWorldId = null;
        if (root.Find("fallback_world") is { } fallbackEntry && diagnostics.ReadString(fallbackEntry.Value, "fallback_world") is { } fallback) {
            var fallbackWorld = worlds.FirstOrDefault(world => world.Id == fallback);
            if (!ClassicSchema.IsWorldId(fallback)) {
                diagnostics.At(fallbackEntry.Value, "fallback_world", $"unknown world id '{fallback}'");
            }
            else if (fallbackWorld is not null && fallbackWorld.HubKey is null) {
                diagnostics.At(fallbackEntry.Value, "fallback_world", $"fallback_world '{fallback}' has no hub_key");
            }

            fallbackWorldId = fallback;
        }

        var notes = ImmutableArray<string>.Empty;
        if (root.Find("notes") is { } notesEntry) {
            diagnostics.ReadStringList(notesEntry.Value, "notes");
            if (notesEntry.Value is YSeq noteList) {
                notes = [.. noteList.Items.OfType<YScalar>().Select(note => note.Value)];
            }
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new ZoneWorldMap(fallbackWorldId, [.. worlds], [.. areas], [.. overrides], [.. sourceFiles], notes, fileName);
    }

    private static YMap? ParseFile(string path, YamlDiagnostics diagnostics, string expectedKind,
                                   IReadOnlySet<string> keys, string[] required) {
        var root = YamlTree.Parse(path, ClassicDataLocator.DisplayPath(path), diagnostics);
        if (root is null) {
            return null;
        }

        if (root is not YMap map) {
            diagnostics.At(root, "", $"the root must be a mapping, got {root.Describe()}");

            return null;
        }

        diagnostics.CheckKeys(map, "", keys, required);
        if (map.Find("kind") is { } kind && diagnostics.ReadString(kind.Value, "kind") is { } kindValue
            && !string.Equals(kindValue, expectedKind, StringComparison.Ordinal)) {
            diagnostics.At(kind.Value, "kind", $"expected kind '{expectedKind}', got '{kindValue}'");
        }

        if (map.Find("version") is { } version
            && !(version.Value is YScalar { IsPlain: true, Value: "1" })) {
            diagnostics.At(version.Value, "version", $"expected version 1, got {version.Value.Describe()}");
        }

        if (map.Find("license_tag") is { } license) {
            _ = diagnostics.ReadEnum(license.Value, "license_tag", s_licenseTags);
        }

        return map;
    }

    private static string? PeekKind(string path) {
        var peek = new YamlDiagnostics();

        return YamlTree.Parse(path, ClassicDataLocator.DisplayPath(path), peek) is YMap map
            && map.Find("kind")?.Value is YScalar kind
                ? kind.Value
                : null;
    }

    private static List<WorldEntry> ReadWorlds(YMap root, YamlDiagnostics diagnostics, Duplicates duplicates) {
        var worlds = new List<WorldEntry>();
        if (root.Find("worlds") is not { } entry || diagnostics.ReadMap(entry.Value, "worlds") is not { } map) {
            return worlds;
        }

        foreach (var worldEntry in map.Entries) {
            var keyPath = YamlTree.Join("worlds", worldEntry.Key);
            if (!ClassicSchema.IsWorldId(worldEntry.Key)) {
                diagnostics.AtKey(map, worldEntry, keyPath, $"unknown world id '{worldEntry.Key}'");
                continue;
            }

            if (diagnostics.ReadMap(worldEntry.Value, keyPath) is not { } world) {
                continue;
            }

            diagnostics.CheckKeys(world, keyPath, s_worldKeys, s_worldRequired);
            var hubKey = ReadOptionalString(world, keyPath, "hub_key", diagnostics);
            if (hubKey is not null && !s_hubKey.IsMatch(hubKey)) {
                diagnostics.At(world.Find("hub_key")!.Value, YamlTree.Join(keyPath, "hub_key"),
                    $"'{hubKey}' is not a valid hub key (letters, digits and _)");
            }

            worlds.Add(new WorldEntry {
                Id = worldEntry.Key,
                Name = ReadOptionalString(world, keyPath, "name", diagnostics) ?? worldEntry.Key,
                HubKey = hubKey,
                Prefixes = ReadPrefixes(world, keyPath, minItems: 0, diagnostics, duplicates),
                Notes = ReadOptionalString(world, keyPath, "notes", diagnostics),
            });
        }

        return worlds;
    }

    private static List<AreaEntry> ReadAreas(YMap root, YamlDiagnostics diagnostics, Duplicates duplicates) {
        var areas = new List<AreaEntry>();
        if (root.Find("areas") is not { } entry || diagnostics.ReadMap(entry.Value, "areas") is not { } map) {
            return areas;
        }

        foreach (var areaEntry in map.Entries) {
            var keyPath = YamlTree.Join("areas", areaEntry.Key);
            if (!s_areaId.IsMatch(areaEntry.Key)) {
                diagnostics.AtKey(map, areaEntry, keyPath, $"'{areaEntry.Key}' is not a valid area id (lowercase letters, digits and _)");
                continue;
            }

            if (diagnostics.ReadMap(areaEntry.Value, keyPath) is not { } area) {
                continue;
            }

            diagnostics.CheckKeys(area, keyPath, s_areaKeys, s_areaRequired);
            var access = area.Find("access") is { } accessEntry
                ? diagnostics.ReadEnum(accessEntry.Value, YamlTree.Join(keyPath, "access"), s_areaAccess)
                : null;
            var feature = ReadFeature(area, keyPath, diagnostics);
            if (access == "feature" && area.Find("feature") is null) {
                diagnostics.At(area, YamlTree.Join(keyPath, "feature"), "access: feature needs a 'feature' key");
            }
            else if (access is not null && access != "feature" && area.Find("feature") is not null) {
                diagnostics.At(area.Find("feature")!.Value, YamlTree.Join(keyPath, "feature"),
                    "'feature' is only allowed with access: feature");
            }

            var (introduced, introducedAfter) = ReadDates(area, keyPath, diagnostics);
            areas.Add(new AreaEntry {
                Id = areaEntry.Key,
                Name = ReadOptionalString(area, keyPath, "name", diagnostics) ?? areaEntry.Key,
                Prefixes = ReadPrefixes(area, keyPath, minItems: 1, diagnostics, duplicates),
                Access = access switch {
                    "deny" => AreaAccess.Deny,
                    "feature" => AreaAccess.Feature,
                    _ => AreaAccess.Allow,
                },
                Feature = feature,
                Introduced = introduced,
                IntroducedAfter = introducedAfter,
                Reason = ReadOptionalString(area, keyPath, "reason", diagnostics) ?? "",
                Confidence = ReadConfidence(area, keyPath, diagnostics),
                Message = ReadOptionalString(area, keyPath, "message", diagnostics),
            });
        }

        return areas;
    }

    private static List<ZoneOverride> ReadOverrides(YMap root, string fileName, YamlDiagnostics diagnostics,
                                                    Duplicates duplicates) {
        var overrides = new List<ZoneOverride>();
        if (root.Find("overrides") is not { } entry || diagnostics.ReadList(entry.Value, "overrides") is not { } list) {
            return overrides;
        }

        for (var i = 0; i < list.Items.Length; i++) {
            var keyPath = YamlTree.Index("overrides", i);
            if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } item) {
                continue;
            }

            diagnostics.CheckKeys(item, keyPath, s_overrideKeys, s_overrideRequired);
            if (!s_overrideEffects.Any(effect => item.Find(effect) is not null)) {
                diagnostics.At(item, keyPath,
                    "an override needs an effect: world, feature, access, introduced or introduced_after");
            }

            ZonePattern? pattern = null;
            if (item.Find("zone") is { } zoneEntry) {
                pattern = ReadPattern(zoneEntry.Value, YamlTree.Join(keyPath, "zone"), diagnostics);
                if (pattern is not null) {
                    duplicates.CheckOverride(pattern, zoneEntry.Value, YamlTree.Join(keyPath, "zone"), $"{fileName} {keyPath}");
                }
            }

            string? worldId = null;
            if (item.Find("world") is { } worldEntry && diagnostics.ReadString(worldEntry.Value, YamlTree.Join(keyPath, "world")) is { } world) {
                if (ClassicSchema.IsWorldId(world)) {
                    worldId = world;
                }
                else {
                    diagnostics.At(worldEntry.Value, YamlTree.Join(keyPath, "world"), $"unknown world id '{world}'");
                }
            }

            var deny = false;
            if (item.Find("access") is { } accessEntry && diagnostics.ReadString(accessEntry.Value, YamlTree.Join(keyPath, "access")) is { } access) {
                if (access == "deny") {
                    deny = true;
                }
                else {
                    diagnostics.At(accessEntry.Value, YamlTree.Join(keyPath, "access"),
                        $"access must be 'deny', got '{access}'; overrides can only close zones");
                }
            }

            var (introduced, introducedAfter) = ReadDates(item, keyPath, diagnostics);
            overrides.Add(new ZoneOverride {
                Pattern = pattern ?? ZonePattern.Parse("invalid"),
                WorldId = worldId,
                Feature = ReadFeature(item, keyPath, diagnostics),
                Deny = deny,
                Introduced = introduced,
                IntroducedAfter = introducedAfter,
                Reason = ReadOptionalString(item, keyPath, "reason", diagnostics) ?? "",
                Confidence = ReadConfidence(item, keyPath, diagnostics),
                Source = ReadOptionalString(item, keyPath, "source", diagnostics),
                Message = ReadOptionalString(item, keyPath, "message", diagnostics),
                SourceFile = fileName,
                Index = i,
            });
        }

        return overrides;
    }

    private static ImmutableArray<ZonePattern> ReadPrefixes(YMap owner, string ownerPath, int minItems,
                                                            YamlDiagnostics diagnostics, Duplicates duplicates) {
        var keyPath = YamlTree.Join(ownerPath, "prefixes");
        if (owner.Find("prefixes") is not { } entry || diagnostics.ReadList(entry.Value, keyPath) is not { } list) {
            return [];
        }

        if (list.Items.Length < minItems) {
            diagnostics.At(list, keyPath, $"needs at least {minItems} prefix");
        }

        var prefixes = ImmutableArray.CreateBuilder<ZonePattern>();
        for (var i = 0; i < list.Items.Length; i++) {
            var itemPath = YamlTree.Index(keyPath, i);
            if (ReadPattern(list.Items[i], itemPath, diagnostics) is { } pattern) {
                duplicates.CheckPrefix(pattern, list.Items[i], itemPath);
                prefixes.Add(pattern);
            }
        }

        return prefixes.ToImmutable();
    }

    private static ZonePattern? ReadPattern(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadString(node, keyPath) is not { } text) {
            return null;
        }

        if (!ZonePattern.IsValid(text)) {
            diagnostics.At(node, keyPath,
                $"'{text}' is not a valid zone pattern (segments of letters, digits, _ . - and *, separated by single '/')");

            return null;
        }

        return ZonePattern.Parse(text);
    }

    private static string? ReadFeature(YMap owner, string ownerPath, YamlDiagnostics diagnostics) {
        var keyPath = YamlTree.Join(ownerPath, "feature");
        if (owner.Find("feature") is not { } entry || diagnostics.ReadString(entry.Value, keyPath) is not { } feature) {
            return null;
        }

        if (!ClassicFeatures.IsKnown(feature)) {
            diagnostics.At(entry.Value, keyPath, $"unknown feature '{feature}'");

            return null;
        }

        return feature;
    }

    private static (DateOnly? Introduced, DateOnly? IntroducedAfter) ReadDates(YMap owner, string ownerPath,
                                                                              YamlDiagnostics diagnostics) {
        DateOnly? introduced = null;
        DateOnly? introducedAfter = null;
        if (owner.Find("introduced") is { } introducedEntry) {
            introduced = diagnostics.ReadDate(introducedEntry.Value, YamlTree.Join(ownerPath, "introduced"), allowNull: false);
        }

        if (owner.Find("introduced_after") is { } afterEntry) {
            introducedAfter = diagnostics.ReadDate(afterEntry.Value, YamlTree.Join(ownerPath, "introduced_after"), allowNull: false);
            if (owner.Find("introduced") is not null) {
                diagnostics.At(afterEntry.Value, YamlTree.Join(ownerPath, "introduced_after"),
                    "set introduced or introduced_after, not both");
            }
        }

        return (introduced, introducedAfter);
    }

    private static string ReadConfidence(YMap owner, string ownerPath, YamlDiagnostics diagnostics)
        => owner.Find("confidence") is { } entry
            ? diagnostics.ReadEnum(entry.Value, YamlTree.Join(ownerPath, "confidence"), s_confidences) ?? ""
            : "";

    private static string? ReadOptionalString(YMap owner, string ownerPath, string key, YamlDiagnostics diagnostics)
        => owner.Find(key) is { } entry ? diagnostics.ReadString(entry.Value, YamlTree.Join(ownerPath, key)) : null;

    private sealed class Duplicates(YamlDiagnostics diagnostics) {

        private readonly Dictionary<string, string> _prefixes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

        public void CheckPrefix(ZonePattern pattern, YNode node, string keyPath) {
            if (!_prefixes.TryAdd(pattern.Text, keyPath)) {
                diagnostics.At(node, keyPath, $"prefix '{pattern.Text}' repeats {_prefixes[pattern.Text]}");
            }
        }

        public void CheckOverride(ZonePattern pattern, YNode node, string keyPath, string location) {
            if (!_overrides.TryAdd(pattern.Text, location)) {
                diagnostics.At(node, keyPath, $"zone '{pattern.Text}' repeats {_overrides[pattern.Text]}");
            }
        }

    }

}
