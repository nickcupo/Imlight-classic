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
 * The spell values of one profile: finds the record for a client template
 * (by client_template path, else by card name) and plans the template's
 * overrides with that profile's numbers.
 *
 * USAGE EXAMPLE:
 * var overrides = new ClassicSpellOverrides(ClassicSpellLoader.Load(spellsDir), profile, accuracyTable);
 * if (overrides.PlanFor(shape) is { ChangesTemplate: true } plan) { ... }
 *
 * NOTE:
 * Immutable once built, so templates can be planned from any thread.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Linq;
using Imlight.Classic.Rules;

namespace Imlight.Classic.Spells;

/// <summary>
/// Classic spell values resolved for one profile.
/// </summary>
public sealed class ClassicSpellOverrides {

    private readonly FrozenDictionary<string, SpellValues> _values;

    /// <summary>
    /// Resolves every record's values for <paramref name="profile"/>.
    /// </summary>
    /// <param name="book">The records.</param>
    /// <param name="profile">The active profile.</param>
    /// <param name="accuracyTable">The profile's accuracy table, if it has one.</param>
    public ClassicSpellOverrides(ClassicSpellBook book, ClassicProfile profile, AccuracyTable? accuracyTable = null) {
        Book = book;
        ProfileId = profile.Id;
        Lineage = profile.Lineage;
        AccuracyTable = accuracyTable;
        _values = book.Records.ToFrozenDictionary(record => record.Id, record => record.ValuesFor(Lineage), StringComparer.Ordinal);
    }

    public ClassicSpellBook Book { get; }
    public string ProfileId { get; }

    /// <summary>
    /// The active profile's extends chain, child first.
    /// </summary>
    public ImmutableArray<string> Lineage { get; }

    public AccuracyTable? AccuracyTable { get; }

    /// <summary>
    /// The record for a template: the one naming its path, else the one with its card name.
    /// </summary>
    /// <param name="templatePath">The template's Root.wad path.</param>
    /// <param name="templateName">The template's name.</param>
    /// <returns>The record and how it was found, or null.</returns>
    public (ClassicSpellRecord Record, SpellMatch Match)? Find(string? templatePath, string? templateName) {
        if (Book.FindByTemplate(templatePath) is { } byPath) {
            return (byPath, SpellMatch.ClientTemplate);
        }

        if (Book.FindByName(templateName) is { } byName) {
            return (byName, SpellMatch.Name);
        }

        return null;
    }

    /// <summary>
    /// The record's values in the active profile.
    /// </summary>
    /// <param name="record">A record of <see cref="Book"/>.</param>
    /// <returns>The resolved values.</returns>
    public SpellValues ValuesOf(ClassicSpellRecord record)
        => _values.TryGetValue(record.Id, out var values) ? values : record.ValuesFor(Lineage);

    /// <summary>
    /// Plans the overrides of one template.
    /// </summary>
    /// <param name="shape">The template.</param>
    /// <returns>The plan, or null when no record matches the template.</returns>
    public SpellOverridePlan? PlanFor(SpellTemplateShape shape) {
        if (Find(shape.Path, shape.Name) is not var (record, match)) {
            return null;
        }

        return SpellOverridePlanner.Plan(record, match, ValuesOf(record), shape);
    }

    /// <summary>
    /// The records the active profile does not list; their values still apply.
    /// </summary>
    public ImmutableArray<ClassicSpellRecord> RecordsNotInProfile
        => [.. Book.Records.Where(record => !record.IsInProfile(ProfileId))];

}
