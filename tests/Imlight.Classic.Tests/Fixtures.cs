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
 * CLASSIC RULES TESTS
 * ========================================================================
 * 
 * PURPOSE:
 * Shared fixtures: the monorepo's real classic-data, temporary classic-data
 * trees for inline YAML, and hand-built profiles.
 * 
 * USAGE EXAMPLE:
 * var profiles = ClassicDataFixture.ProfilesPath;   // skips outside the monorepo
 * using var data = new TempClassicData();
 * data.WriteProfile("child", "...yaml...");
 * 
 * NOTE:
 * With W101C_REQUIRE_CLASSIC_DATA=1 (set by CI) a missing classic-data
 * directory fails the test instead of skipping it.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Imlight.Classic.Zones;
using Xunit;

namespace Imlight.Classic.Tests;

/// <summary>
/// The monorepo's real classic-data directory.
/// </summary>
internal static class ClassicDataFixture {

    private static readonly Lazy<string?> s_root = new(() => ClassicDataLocator.FindClassicDataRoot(AppContext.BaseDirectory));

    public static string Root {
        get {
            if (s_root.Value is { } root) {
                return root;
            }

            var message = $"classic-data not found above {AppContext.BaseDirectory}; run from the Wizard101 Classic monorepo checkout";
            if (Environment.GetEnvironmentVariable("W101C_REQUIRE_CLASSIC_DATA") == "1") {
                Assert.Fail(message);
            }

            Assert.Skip(message);

            return "";
        }
    }

    public static string ProfilesPath => Path.Combine(Root, "profiles");
    public static string WorldsPath => Path.Combine(Root, "zones", "worlds.yaml");
    public static string SchemaPath(string name) => Path.Combine(Root, "schema", name);

    public static ClassicProfile LoadProfile(string id)
        => ClassicProfileLoader.Load(ProfilesPath, id);

    public static ClassicRules RealRules(string profileId)
        => new(LoadProfile(profileId), ZoneWorldMapLoader.Load(WorldsPath));

}

/// <summary>
/// A temporary <c>classic-data</c> tree for inline YAML fixtures.
/// </summary>
internal sealed class TempClassicData : IDisposable {

    private readonly string _top;

    public TempClassicData() {
        _top = Path.Combine(Path.GetTempPath(), "imlight-classic-tests-" + Guid.NewGuid().ToString("N"));
        Root = Path.Combine(_top, "classic-data");
        Directory.CreateDirectory(ProfilesPath);
        Directory.CreateDirectory(ZonesPath);
    }

    public string Root { get; }
    public string ProfilesPath => Path.Combine(Root, "profiles");
    public string ZonesPath => Path.Combine(Root, "zones");
    public string WorldsPath => Path.Combine(ZonesPath, "worlds.yaml");

    public string WriteProfile(string id, string yaml) {
        var path = Path.Combine(ProfilesPath, id + ".yaml");
        File.WriteAllText(path, yaml.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));

        return path;
    }

    public string WriteZoneFile(string name, string yaml) {
        var path = Path.Combine(ZonesPath, name);
        File.WriteAllText(path, yaml.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));

        return path;
    }

    public void Dispose() {
        try {
            Directory.Delete(_top, recursive: true);
        }
        catch (IOException) {
            // A leftover temp directory is harmless.
        }
    }

}

/// <summary>
/// Builders for zone-map YAML and hand-made profiles.
/// </summary>
internal static class ZoneFixture {

    private static readonly Regex s_worldKey = new(@"^  ([a-z_]+):", RegexOptions.Multiline);

    /// <summary>
    /// A complete worlds.yaml: the given world lines, then every other schema world with no prefixes.
    /// </summary>
    public static string WorldsYaml(string worlds, string areas = "", string overrides = "",
                                    string fallback = "wizard_city", string includes = "", string extraRoot = "") {
        var present = s_worldKey.Matches(worlds).Select(match => match.Groups[1].Value).ToHashSet();
        var text = new StringBuilder();
        text.Append("kind: zone-worlds\nversion: 1\nlicense_tag: own\n");
        text.Append("fallback_world: ").Append(fallback).Append('\n');
        if (includes.Length > 0) {
            text.Append("includes:\n").Append(includes.TrimEnd()).Append('\n');
        }

        text.Append(extraRoot);
        text.Append("worlds:\n").Append(worlds.TrimEnd()).Append('\n');
        foreach (var worldId in ClassicSchema.WorldIds.Where(id => !present.Contains(id))) {
            text.Append("  ").Append(worldId).Append(": { name: ").Append(worldId).Append(", prefixes: [] }\n");
        }

        text.Append(areas.Length > 0 ? "areas:\n" + areas.TrimEnd() + "\n" : "areas: {}\n");
        text.Append(overrides.Length > 0 ? "overrides:\n" + overrides.TrimEnd() + "\n" : "overrides: []\n");

        return text.ToString();
    }

    public static ClassicProfile Profile(DateOnly? cutoff = null,
                                         string[]? worlds = null,
                                         Dictionary<string, bool>? features = null,
                                         int? levelCap = null)
        => new() {
            Id = "test",
            Title = "Test",
            Status = ProfileStatus.Debug,
            Cutoff = cutoff,
            LevelCap = levelCap,
            Worlds = worlds?.ToImmutableArray(),
            Features = new FeatureSwitches(features ?? new Dictionary<string, bool>()),
            Rules = ProfileRules.None,
            SourceFiles = [],
        };

    /// <summary>
    /// A map with only Wizard City and its hub, for rules that do not look at zones.
    /// </summary>
    public static ZoneWorldMap MinimalMap()
        => new("wizard_city",
            [new WorldEntry { Id = "wizard_city", Name = "Wizard City", HubKey = "WizardCity", Prefixes = [ZonePattern.Parse("WizardCity")] }],
            [], [], [], [], "worlds.yaml");

    public static ZoneWorldMap LoadMap(TempClassicData data, string worldsYaml) {
        data.WriteZoneFile("worlds.yaml", worldsYaml);

        return ZoneWorldMapLoader.Load(data.WorldsPath);
    }

    public static ClassicDataError SingleError(Action load, string keyPath) {
        var ex = Assert.Throws<ClassicDataException>(load);
        var matches = ex.Errors.Where(error => error.KeyPath == keyPath).ToList();
        Assert.True(matches.Count == 1,
            $"expected one error at '{keyPath}', got:{Environment.NewLine}{string.Join(Environment.NewLine, ex.Errors)}");

        return matches[0];
    }

}
