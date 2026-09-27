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
 * CLASSIC SPELL VALUES
 * ========================================================================
 *
 * PURPOSE:
 * One classic-data/spells record, and the lookup of every record by client
 * template path, by the Treasure Card copy of a trained card, and by card
 * name.
 *
 * USAGE EXAMPLE:
 * var record = book.FindByTemplate("Spells/Tiered Spells/Fire Cat.xml") ?? book.FindByName(template.m_name);
 * var values = record?.ValuesFor(profile.Lineage);
 *
 * NOTE:
 * values holds the canonical profile's numbers. A profile_values entry of
 * the active profile or any profile it extends overrides them, the nearest
 * profile last so it wins. A trained or crossover record also covers the
 * card's Treasure Card, which the client names "<card> TC" in
 * Spells/TreasureCards/ (README: one record per card, the trained one).
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace Imlight.Classic.Spells;

/// <summary>
/// A spell card as classic-data records it.
/// </summary>
public sealed class ClassicSpellRecord {

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string School { get; init; }
    public required string Kind { get; init; }

    /// <summary>
    /// The matching template's path in the client's Root.wad, or null when the client has none.
    /// </summary>
    public string? ClientTemplate { get; init; }

    /// <summary>
    /// The profiles the card exists in.
    /// </summary>
    public required ImmutableArray<string> Profiles { get; init; }

    /// <summary>
    /// The canonical profile's numbers.
    /// </summary>
    public required SpellValues Values { get; init; }

    /// <summary>
    /// The fields that differ in other profiles, keyed by profile id.
    /// </summary>
    public ImmutableDictionary<string, SpellValuesOverride> ProfileValues { get; init; }
        = ImmutableDictionary<string, SpellValuesOverride>.Empty;

    /// <summary>
    /// The record's file, relative to the classic-data parent when possible.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// True when the card exists in <paramref name="profileId"/>.
    /// </summary>
    /// <param name="profileId">A profile id.</param>
    /// <returns>True if the record lists the profile.</returns>
    public bool IsInProfile(string profileId)
        => Profiles.Contains(profileId, StringComparer.Ordinal);

    /// <summary>
    /// The card's numbers in the profile whose extends chain is <paramref name="lineage"/>.
    /// </summary>
    /// <param name="lineage">Profile ids, child first (<see cref="ClassicProfile.Lineage"/>).</param>
    /// <returns><see cref="Values"/> with every applicable <c>profile_values</c> entry applied.</returns>
    public SpellValues ValuesFor(IReadOnlyList<string> lineage) {
        var values = Values;
        for (var i = lineage.Count - 1; i >= 0; i--) {
            if (ProfileValues.TryGetValue(lineage[i], out var entry)) {
                values = entry.ApplyTo(values);
            }
        }

        return values;
    }

}

/// <summary>
/// Every classic spell record, found by client template path or by card name.
/// </summary>
public sealed class ClassicSpellBook {

    private const string TreasureCardFolder = "Spells/TreasureCards/";
    private const string TreasureCardSuffix = " TC";

    private readonly FrozenDictionary<string, ClassicSpellRecord> _byTemplate;
    private readonly FrozenDictionary<string, ClassicSpellRecord> _byName;
    private readonly FrozenDictionary<string, ClassicSpellRecord> _byTreasureCardName;

    /// <summary>
    /// Creates the book. Template paths must be unique, and names unique ignoring case.
    /// </summary>
    /// <param name="sourceDirectory">The spells directory the records came from.</param>
    /// <param name="records">The records.</param>
    /// <exception cref="ArgumentException">Two records share a template path or a name.</exception>
    public ClassicSpellBook(string sourceDirectory, IEnumerable<ClassicSpellRecord> records) {
        SourceDirectory = sourceDirectory;
        Records = [.. records.OrderBy(record => record.Id, StringComparer.Ordinal)];
        _byTemplate = Records.Where(record => record.ClientTemplate is not null)
            .ToFrozenDictionary(record => record.ClientTemplate!, StringComparer.Ordinal);
        _byName = Records.ToFrozenDictionary(record => record.Name, StringComparer.OrdinalIgnoreCase);
        _byTreasureCardName = TreasureCardNames(Records);
    }

    /// <summary>
    /// A book with no records.
    /// </summary>
    public static ClassicSpellBook Empty { get; } = new("", []);

    public string SourceDirectory { get; }

    /// <summary>
    /// The records, ordered by id.
    /// </summary>
    public ImmutableArray<ClassicSpellRecord> Records { get; }

    /// <summary>
    /// The record whose <c>client_template</c> is <paramref name="templatePath"/>.
    /// </summary>
    /// <param name="templatePath">A Root.wad path such as <c>Spells/Tiered Spells/Fire Cat.xml</c>.</param>
    /// <returns>The record, or null.</returns>
    public ClassicSpellRecord? FindByTemplate(string? templatePath)
        => templatePath is not null && _byTemplate.TryGetValue(templatePath, out var record) ? record : null;

    /// <summary>
    /// The record whose card name is <paramref name="name"/>, ignoring case.
    /// </summary>
    /// <param name="name">A card name.</param>
    /// <returns>The record, or null.</returns>
    public ClassicSpellRecord? FindByName(string? name)
        => name is not null && _byName.TryGetValue(name, out var record) ? record : null;

    /// <summary>
    /// The trained or crossover record whose Treasure Card is the template at <paramref name="templatePath"/>.
    /// </summary>
    /// <param name="templatePath">A Root.wad path such as <c>Spells/TreasureCards/Fire Cat TC.xml</c>.</param>
    /// <param name="templateName">The template's name, such as <c>Fire Cat TC</c>.</param>
    /// <returns>The record whose name, or whose client template's file name, is the card name without " TC"; or null.</returns>
    public ClassicSpellRecord? FindTreasureCardOf(string? templatePath, string? templateName) {
        if (templatePath is null || templateName is null
            || !templatePath.StartsWith(TreasureCardFolder, StringComparison.Ordinal)
            || !templateName.EndsWith(TreasureCardSuffix, StringComparison.Ordinal)) {
            return null;
        }

        return _byTreasureCardName.GetValueOrDefault(templateName[..^TreasureCardSuffix.Length]);
    }

    private static FrozenDictionary<string, ClassicSpellRecord> TreasureCardNames(IEnumerable<ClassicSpellRecord> records) {
        // A renamed card's Treasure Card may follow either its cutoff name or its client template; a name two
        // records could claim matches neither.
        var candidates = records
            .Where(record => record.ClientTemplate is not null && record.Kind is "trained" or "crossover")
            .SelectMany(record => new[] { record.Name, Path.GetFileNameWithoutExtension(record.ClientTemplate!) }
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => (Name: name, Record: record)));

        return candidates
            .GroupBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(candidate => candidate.Record.Id).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToFrozenDictionary(group => group.Key, group => group.First().Record, StringComparer.OrdinalIgnoreCase);
    }

}
