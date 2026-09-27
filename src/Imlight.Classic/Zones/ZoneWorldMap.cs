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
 * The zone-to-world map from classic-data/zones: which world or non-world
 * area a zone belongs to, and which zone-level overrides apply to it. The
 * classification is independent of the active profile.
 * 
 * USAGE EXAMPLE:
 * var classification = map.Classify("Grizzleheim/GH_HFjord/GH_Nordrilund");
 * // classification.EffectiveWorld is wintertusk, reassigned by an override.
 * 
 * NOTE:
 * The home is the most specific world or area prefix; declaration order
 * (worlds before areas) breaks ties. Overrides are listed most specific
 * first. ClassicRules turns a classification into an allow/deny decision.
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
using System.Linq;

namespace Imlight.Classic.Zones;

/// <summary>
/// A world of the zone map.
/// </summary>
public sealed class WorldEntry {

    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// The WorldHubZones.xml key of the world's hub, if it has one.
    /// </summary>
    public string? HubKey { get; init; }

    public required ImmutableArray<ZonePattern> Prefixes { get; init; }
    public string? Notes { get; init; }

}

/// <summary>
/// How an area's zones are opened.
/// </summary>
public enum AreaAccess {
    Allow,
    Deny,
    Feature,
}

/// <summary>
/// A non-world area of the zone map (housing, minigames, events, test zones).
/// </summary>
public sealed class AreaEntry {

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ImmutableArray<ZonePattern> Prefixes { get; init; }
    public required AreaAccess Access { get; init; }

    /// <summary>
    /// The feature path that opens the area when <see cref="Access"/> is <see cref="AreaAccess.Feature"/>.
    /// </summary>
    public string? Feature { get; init; }

    public DateOnly? Introduced { get; init; }
    public DateOnly? IntroducedAfter { get; init; }
    public required string Reason { get; init; }
    public required string Confidence { get; init; }
    public string? Source { get; init; }
    public string? Message { get; init; }

}

/// <summary>
/// A zone-level exception. It can only restrict a zone or reassign it to another world.
/// </summary>
public sealed class ZoneOverride {

    public required ZonePattern Pattern { get; init; }

    /// <summary>
    /// The world the matching zones belong to instead of their prefix world.
    /// </summary>
    public string? WorldId { get; init; }

    /// <summary>
    /// The feature path that must be on for the matching zones to open.
    /// </summary>
    public string? Feature { get; init; }

    /// <summary>
    /// True for <c>access: deny</c>.
    /// </summary>
    public bool Deny { get; init; }

    public DateOnly? Introduced { get; init; }
    public DateOnly? IntroducedAfter { get; init; }
    public required string Reason { get; init; }
    public required string Confidence { get; init; }
    public string? Source { get; init; }
    public string? Message { get; init; }

    /// <summary>
    /// The file name the override was declared in.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// The override's index in its file's <c>overrides</c> list.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Where the override is declared, e.g. <c>overrides-spiraldb.yaml overrides[0]</c>.
    /// </summary>
    public string RuleSource => $"{SourceFile} overrides[{Index}]";

}

/// <summary>
/// The world or area prefix a zone falls under.
/// </summary>
/// <param name="World">The world, when the home is a world prefix.</param>
/// <param name="Area">The area, when the home is an area prefix.</param>
/// <param name="Pattern">The matching prefix.</param>
/// <param name="PrefixIndex">The prefix's index in its world or area.</param>
/// <param name="SourceFile">The file name the prefix was declared in.</param>
public sealed record ZoneHome(WorldEntry? World, AreaEntry? Area, ZonePattern Pattern, int PrefixIndex, string SourceFile) {

    /// <summary>
    /// Where the prefix is declared, e.g. <c>worlds.yaml worlds.celestia.prefixes[0]</c>.
    /// </summary>
    public string RuleSource
        => World is not null
            ? $"{SourceFile} worlds.{World.Id}.prefixes[{PrefixIndex}]"
            : $"{SourceFile} areas.{Area!.Id}.prefixes[{PrefixIndex}]";

}

/// <summary>
/// What the zone map says about one zone name.
/// </summary>
/// <param name="Zone">The zone as given.</param>
/// <param name="Segments">The zone's segments.</param>
/// <param name="Home">The most specific matching world or area prefix, if any.</param>
/// <param name="Overrides">Every matching override, most specific first.</param>
/// <param name="EffectiveWorld">The override-assigned world, else the home world, else null.</param>
/// <param name="WorldOverride">The override that assigned the world, if any.</param>
public sealed record ZoneClassification(
    string Zone,
    ImmutableArray<string> Segments,
    ZoneHome? Home,
    ImmutableArray<ZoneOverride> Overrides,
    WorldEntry? EffectiveWorld,
    ZoneOverride? WorldOverride);

/// <summary>
/// The loaded zone-to-world map.
/// </summary>
public sealed class ZoneWorldMap {

    private readonly ImmutableArray<ZoneHome> _prefixes;
    private readonly FrozenDictionary<string, WorldEntry> _worldsById;
    private readonly FrozenDictionary<string, WorldEntry> _worldsByHubKey;

    internal ZoneWorldMap(string? fallbackWorldId,
                          ImmutableArray<WorldEntry> worlds,
                          ImmutableArray<AreaEntry> areas,
                          ImmutableArray<ZoneOverride> overrides,
                          ImmutableArray<string> sourceFiles,
                          ImmutableArray<string> notes,
                          string worldsFileName) {
        FallbackWorldId = fallbackWorldId;
        Worlds = worlds;
        Areas = areas;
        Overrides = overrides;
        SourceFiles = sourceFiles;
        Notes = notes;

        _worldsById = worlds.ToFrozenDictionary(world => world.Id, StringComparer.Ordinal);
        _worldsByHubKey = worlds
            .Where(world => world.HubKey is not null)
            .GroupBy(world => world.HubKey!, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var prefixes = ImmutableArray.CreateBuilder<ZoneHome>();
        foreach (var world in worlds) {
            for (var i = 0; i < world.Prefixes.Length; i++) {
                prefixes.Add(new ZoneHome(world, null, world.Prefixes[i], i, worldsFileName));
            }
        }

        foreach (var area in areas) {
            for (var i = 0; i < area.Prefixes.Length; i++) {
                prefixes.Add(new ZoneHome(null, area, area.Prefixes[i], i, worldsFileName));
            }
        }

        _prefixes = prefixes.ToImmutable();
    }

    /// <summary>
    /// A map with no worlds, areas or overrides. Only unrestricted profiles can use it.
    /// </summary>
    public static ZoneWorldMap Empty { get; } = new(null, [], [], [], [], [], "worlds.yaml");

    /// <summary>
    /// The world closed zones fall back to when their own world is closed.
    /// </summary>
    public string? FallbackWorldId { get; }

    /// <summary>
    /// Every world, in declaration order.
    /// </summary>
    public ImmutableArray<WorldEntry> Worlds { get; }

    /// <summary>
    /// Every area, in declaration order.
    /// </summary>
    public ImmutableArray<AreaEntry> Areas { get; }

    /// <summary>
    /// Every override, entry file first, then included files in include order.
    /// </summary>
    public ImmutableArray<ZoneOverride> Overrides { get; }

    /// <summary>
    /// The absolute paths of the files the map was loaded from, entry file first.
    /// </summary>
    public ImmutableArray<string> SourceFiles { get; }

    /// <summary>
    /// The notes of the entry file.
    /// </summary>
    public ImmutableArray<string> Notes { get; }

    /// <summary>
    /// Finds a world by id.
    /// </summary>
    /// <param name="worldId">The world id.</param>
    /// <returns>The world, or null.</returns>
    public WorldEntry? FindWorld(string? worldId)
        => worldId is not null && _worldsById.TryGetValue(worldId, out var world) ? world : null;

    /// <summary>
    /// Finds the world whose hub key is <paramref name="hubKey"/>, ignoring case.
    /// </summary>
    /// <param name="hubKey">A WorldHubZones.xml key.</param>
    /// <returns>The world, or null.</returns>
    public WorldEntry? FindWorldByHubKey(string? hubKey)
        => hubKey is not null && _worldsByHubKey.TryGetValue(hubKey, out var world) ? world : null;

    /// <summary>
    /// Classifies a zone name.
    /// </summary>
    /// <param name="zone">The zone name.</param>
    /// <returns>Its home, overrides and effective world.</returns>
    public ZoneClassification Classify(string? zone) {
        var segments = ZonePattern.SplitZone(zone);

        ZoneHome? home = null;
        foreach (var candidate in _prefixes) {
            if (candidate.Pattern.Matches(segments)
                && (home is null || candidate.Pattern.CompareSpecificity(home.Pattern) > 0)) {
                home = candidate;
            }
        }

        // OrderByDescending is stable, so declaration order breaks specificity ties.
        var overrides = Overrides
            .Where(candidate => candidate.Pattern.Matches(segments))
            .OrderByDescending(candidate => candidate.Pattern, SpecificityComparer.Instance)
            .ToImmutableArray();

        var worldOverride = overrides.FirstOrDefault(candidate => candidate.WorldId is not null);
        var effectiveWorld = worldOverride is not null ? FindWorld(worldOverride.WorldId) : home?.World;

        return new ZoneClassification(zone ?? "", segments, home, overrides, effectiveWorld, worldOverride);
    }

    private sealed class SpecificityComparer : IComparer<ZonePattern> {

        public static SpecificityComparer Instance { get; } = new();

        public int Compare(ZonePattern? x, ZonePattern? y)
            => x is null || y is null ? 0 : x.CompareSpecificity(y);

    }

}
