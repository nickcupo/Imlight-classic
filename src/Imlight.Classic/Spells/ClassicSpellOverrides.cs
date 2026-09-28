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
 * (by client_template path, then as a trained card's Treasure Card, then
 * by card name for records with no client_template) and plans the
 * template's overrides with that profile's numbers.
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
    /// The record for a template: the one naming its path, else the trained card whose Treasure Card it is,
    /// else a record with its card name and no client template.
    /// </summary>
    /// <param name="templatePath">The template's Root.wad path.</param>
    /// <param name="templateName">The template's name.</param>
    /// <returns>The record and how it was found, or null.</returns>
    public (ClassicSpellRecord Record, SpellMatch Match)? Find(string? templatePath, string? templateName) {
        if (Book.FindByTemplate(templatePath) is { } byPath) {
            return (byPath, SpellMatch.ClientTemplate);
        }

        if (Book.FindTreasureCardOf(templatePath, templateName) is { } trained) {
            return (trained, SpellMatch.TreasureCard);
        }

        // A record with a client template describes that template only; another template sharing the card
        // name is a different card, such as a creature's own copy.
        if (Book.FindByName(templateName) is { ClientTemplate: null } byName) {
            return (byName, SpellMatch.Name);
        }

        return null;
    }

    /// <summary>
    /// The record for a template, as <see cref="Find"/> finds it, when the template is that card: a Treasure Card or a
    /// same-named template that carries none of the record's effects is another card (r806919's damage-dealing
    /// "Fire Elemental" Treasure Card is not the 2009 Fire Elemental minion's).
    /// </summary>
    /// <param name="shape">The template.</param>
    /// <returns>The record and how it was found, or null.</returns>
    public (ClassicSpellRecord Record, SpellMatch Match)? Match(SpellTemplateShape shape) {
        if (Find(shape.Path, shape.Name) is not var (record, match)) {
            return null;
        }

        if (match == SpellMatch.ClientTemplate) {
            return (record, match);
        }

        var effects = ValuesOf(record).Effects;
        var types = shape.Effects.SelectMany(node => node.Children.IsEmpty ? [node] : node.Children.Add(node))
            .Select(node => node.EffectTypeName)
            .ToHashSet(StringComparer.Ordinal);

        return effects.IsEmpty || effects.Any(effect => types.Any(type => SpellEffectMeaning.CanCarry(effect.Kind, type)))
            ? (record, match)
            : null;
    }

    /// <summary>
    /// True when a spell trainer may teach the template in the active profile: a trained or crossover record
    /// names it as its client template and lists the profile. Trainers in the r806919 client also teach cards
    /// from after the cutoff (Summon Sandstorm, Elemental Golem, Gearhead Destroyer, ...), which have no record.
    /// </summary>
    /// <param name="templatePath">The spell template's Root.wad path.</param>
    /// <returns>True if the card can be trained in this profile.</returns>
    public bool IsTrainable(string? templatePath)
        => Book.FindByTemplate(templatePath) is { Kind: "trained" or "crossover" } record && record.IsInProfile(ProfileId);

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
        if (Match(shape) is not var (record, match)) {
            return null;
        }

        return SpellOverridePlanner.Plan(record, match, ValuesOf(record), shape, InheritedFixedPips(record));
    }

    /// <summary>
    /// The nearest fixed pip cost a profile this one extends gives the record, for a profile whose own cost is X.
    /// </summary>
    /// <param name="record">A record of <see cref="Book"/>.</param>
    /// <returns>The cost, or null when the active profile's cost is fixed or no ancestor fixes one.</returns>
    public int? InheritedFixedPips(ClassicSpellRecord record) {
        if (!ValuesOf(record).Pips.IsX) {
            return null;
        }

        for (var i = 1; i < Lineage.Length; i++) {
            if (record.ValuesFor(Lineage[i..]).Pips.Fixed is { } cost) {
                return cost;
            }
        }

        return null;
    }

    /// <summary>
    /// The records the active profile does not list; their values still apply.
    /// </summary>
    public ImmutableArray<ClassicSpellRecord> RecordsNotInProfile
        => [.. Book.Records.Where(record => !record.IsInProfile(ProfileId))];

}
