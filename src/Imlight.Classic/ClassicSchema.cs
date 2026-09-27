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
 * CLASSIC RULES SCHEMA
 * ========================================================================
 * 
 * PURPOSE:
 * The names classic-data/schema/profile.schema.json defines: world ids,
 * profile statuses and the enums under a profile's rules block.
 * 
 * USAGE EXAMPLE:
 * ClassicSchema.IsWorldId("celestia") while validating a profile or zone map.
 * 
 * NOTE:
 * SchemaDriftTests pins every list here to the schema file, so a schema edit
 * without a matching edit here fails the tests.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Imlight.Classic;

/// <summary>
/// The world ids, statuses and rule enums of the classic-data profile schema.
/// </summary>
public static class ClassicSchema {

    /// <summary>
    /// The pattern a profile id must match, as written in profile.schema.json.
    /// </summary>
    public const string IdPattern = "^[a-z0-9][a-z0-9-]*$";

    // \z rather than $: a trailing newline must not pass, because the id becomes a file name.
    private static readonly Regex s_idRegex = new(@"^[a-z0-9][a-z0-9-]*\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Every world id the schema knows, in schema order.
    /// </summary>
    public static ImmutableArray<string> WorldIds { get; } = [
        "wizard_city", "krokotopia", "marleybone", "mooshu", "dragonspyre", "grizzleheim",
        "celestia", "wintertusk", "zafaria", "avalon", "azteca", "khrysalis", "polaris",
        "mirage", "empyrea", "karamelle", "lemuria", "novus", "wallaru", "selenopolis",
        "darkmoor", "aquila", "arcanum", "wysteria"
    ];

    /// <summary>
    /// The values of a profile's <c>status</c>.
    /// </summary>
    public static ImmutableArray<string> ProfileStatuses { get; } = ["canonical", "optional", "debug"];

    /// <summary>
    /// The values of <c>rules.power_pips_from_rank</c>. The schema also allows null.
    /// </summary>
    public static ImmutableArray<string> PowerPipRanks { get; } = [
        "novice", "apprentice", "initiate", "journeyman", "adept", "magus", "master", "grandmaster"
    ];

    /// <summary>
    /// The values of <c>rules.dragonspyre_difficulty</c>.
    /// </summary>
    public static ImmutableArray<string> DragonspyreDifficulties { get; } = ["pre-2010-07", "post-2010-07"];

    /// <summary>
    /// The values of <c>rules.tutorial</c>.
    /// </summary>
    public static ImmutableArray<string> Tutorials { get; } = ["unicorn-way-classic", "modern-2019"];

    private static readonly FrozenSet<string> s_worldIds = WorldIds.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// True when <paramref name="id"/> is a valid profile id.
    /// </summary>
    /// <param name="id">The candidate id.</param>
    /// <returns>True if the id matches <see cref="IdPattern"/>.</returns>
    public static bool IsValidId(string? id)
        => id is not null && s_idRegex.IsMatch(id);

    /// <summary>
    /// True when <paramref name="id"/> is one of <see cref="WorldIds"/>.
    /// </summary>
    /// <param name="id">The candidate world id.</param>
    /// <returns>True if the schema knows the world.</returns>
    public static bool IsWorldId(string? id)
        => id is not null && s_worldIds.Contains(id);

}
