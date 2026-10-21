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
 * CLASSIC EXPERIMENTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-20): the names of the profile's `experiments` block,
 * owner extras that are fun twists and never historical (the owner's
 * "find ways to further solidify school identity ... a fun twist"). A
 * switch the profile does not set is OFF; features default on, so these
 * live in their own block and never make a profile restricted.
 *
 * USAGE EXAMPLE:
 * ClassicRuntime.Rules.IsExperimentEnabled(ClassicExperiments.SchoolFlavor)
 *
 * NOTE:
 * SchemaDriftTests pins All to profile.schema.json's experiments block.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-fable-5-1)
 * Version: KALI 1.0
 * Last Updated: 10/20/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Imlight.Classic;

/// <summary>
/// Switch names of the profile's <c>experiments</c> block.
/// </summary>
public static class ClassicExperiments {

    /// <summary>School identity in the world: ambient wizards greet and tease by school and keep to their school's tree.</summary>
    public const string SchoolFlavor = "schoolflavor";

    /// <summary>Every experiment name, in schema order.</summary>
    public static ImmutableArray<string> All { get; } = [SchoolFlavor];

    private static readonly FrozenSet<string> s_known = All.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>True when <paramref name="name"/> is one of <see cref="All"/>.</summary>
    public static bool IsKnown(string? name) => name is not null && s_known.Contains(name);

}

/// <summary>
/// CLASSIC (2026-10-20): the experiments a profile sets explicitly. Switches it does not set are OFF.
/// </summary>
public sealed class ExperimentSwitches {

    private readonly FrozenDictionary<string, bool> _explicit;

    /// <param name="explicitValues">Explicit values keyed by <see cref="ClassicExperiments"/> names.</param>
    /// <exception cref="ArgumentException">A key is not a known experiment.</exception>
    public ExperimentSwitches(System.Collections.Generic.IReadOnlyDictionary<string, bool> explicitValues) {
        foreach (var name in explicitValues.Keys) {
            if (!ClassicExperiments.IsKnown(name)) {
                throw new ArgumentException($"unknown experiment '{name}'", nameof(explicitValues));
            }
        }

        _explicit = explicitValues.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Nothing set: every experiment is off.</summary>
    public static ExperimentSwitches None { get; } = new(new System.Collections.Generic.Dictionary<string, bool>());

    /// <summary>The experiments that are on, in schema order.</summary>
    public ImmutableArray<string> Enabled
        => [.. System.Linq.ImmutableArrayExtensions.Where(ClassicExperiments.All, name => _explicit.TryGetValue(name, out var on) && on)];

    /// <summary>True when the profile sets <paramref name="name"/> to true.</summary>
    /// <exception cref="ArgumentException">The name is not a known experiment.</exception>
    public bool IsEnabled(string name) {
        if (!ClassicExperiments.IsKnown(name)) {
            throw new ArgumentException($"unknown experiment '{name}'", nameof(name));
        }

        return _explicit.TryGetValue(name, out var on) && on;
    }

}
