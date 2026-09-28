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
 * Counts what the classic spell values did to the client's spell templates
 * at load: templates matched and unmatched, fields changed, effects applied
 * and skipped, for the startup log.
 *
 * USAGE EXAMPLE:
 * census.Add(shape, plan);                       // once per template the server loads
 * var summary = census.Summarize(overrides);
 *
 * NOTE:
 * Templates are keyed by path, so a template counted twice counts once.
 * Record ids and counts are our data; template paths come from the client,
 * so the summary names records, never templates.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Imlight.Classic.Spells;

/// <summary>
/// The totals of one census.
/// </summary>
public sealed record SpellCensusSummary {

    public int Templates { get; init; }
    public int MatchedByClientTemplate { get; init; }
    public int MatchedByTreasureCard { get; init; }
    public int MatchedByName { get; init; }
    public int Unmatched { get; init; }
    public int ChangedTemplates { get; init; }
    public int PipsChanged { get; init; }
    public int SchoolPipsCleared { get; init; }
    public int AccuracyChanged { get; init; }
    public int EffectValuesChanged { get; init; }
    public int RoundsChanged { get; init; }
    public int HealModifiersChanged { get; init; }
    public int TargetsChanged { get; init; }

    /// <summary>
    /// Templates whose effect list the plan rebuilt (hits added, removed or reordered).
    /// </summary>
    public int StructuresRebuilt { get; init; }

    /// <summary>
    /// Changed effect schools.
    /// </summary>
    public int DamageTypesChanged { get; init; }

    /// <summary>
    /// Matched templates that still differ from their record after the plan (see <see cref="SpellMechanicsAudit"/>), by record id.
    /// </summary>
    public ImmutableArray<string> RecordsNotFullyApplied { get; init; } = [];

    /// <summary>
    /// Matched templates that carry every mechanic of their record after the plan.
    /// </summary>
    public int TemplatesFullyApplied { get; init; }
    public int UnmatchedTemplateEffects { get; init; }

    /// <summary>
    /// Template effects zeroed because the record has no such effect.
    /// </summary>
    public int ZeroedTemplateEffects { get; init; }

    /// <summary>
    /// Template effects whose type the plan rewrote (a share of max health to a flat hit).
    /// </summary>
    public int KindsChanged { get; init; }

    /// <summary>
    /// Templates charged a fixed cost inherited from a profile the active one extends, instead of the active profile's X.
    /// </summary>
    public int PipsInherited { get; init; }

    /// <summary>
    /// Records no template path or name matched, by id.
    /// </summary>
    public ImmutableArray<string> RecordsWithoutTemplate { get; init; } = [];

    /// <summary>
    /// Records the active profile does not list (their values still apply), by id.
    /// </summary>
    public ImmutableArray<string> RecordsNotInProfile { get; init; } = [];

    public ImmutableDictionary<PipsSkip, int> PipsSkipped { get; init; } = ImmutableDictionary<PipsSkip, int>.Empty;
    public ImmutableDictionary<SpellEffectKind, int> AppliedEffects { get; init; } = ImmutableDictionary<SpellEffectKind, int>.Empty;
    public ImmutableDictionary<SkippedEffect, int> SkippedEffects { get; init; } = ImmutableDictionary<SkippedEffect, int>.Empty;

    /// <summary>
    /// Unmatched damage templates whose accuracy differs from their school's base in the accuracy table.
    /// </summary>
    public int UnmatchedDamageOffSchoolBase { get; init; }

    /// <summary>
    /// Unmatched damage templates of a school the accuracy table covers.
    /// </summary>
    public int UnmatchedDamageTemplates { get; init; }

    /// <summary>
    /// The applied effect counts, such as <c>damage 150, heal 15</c>, in schema order.
    /// </summary>
    public string DescribeApplied()
        => Describe(AppliedEffects.OrderBy(pair => pair.Key).Select(pair => (ClassicSpellSchema.NameOf(pair.Key), pair.Value)));

    /// <summary>
    /// The skipped effect counts for one reason, such as <c>minion 11, stun 6</c>, in schema order.
    /// </summary>
    /// <param name="reason">The reason.</param>
    public string DescribeSkipped(EffectSkipReason reason)
        => Describe(SkippedEffects.Where(pair => pair.Key.Reason == reason)
            .OrderBy(pair => pair.Key.Kind)
            .Select(pair => (ClassicSpellSchema.NameOf(pair.Key.Kind), pair.Value)));

    private static string Describe(IEnumerable<(string Name, int Count)> counts) {
        var text = string.Join(", ", counts.Select(pair => $"{pair.Name} {pair.Count.ToString(CultureInfo.InvariantCulture)}"));

        return text.Length == 0 ? "none" : text;
    }

}

/// <summary>
/// Collects the plans of every loaded template.
/// </summary>
public sealed class SpellOverrideCensus {

    private sealed record Entry(SpellOverridePlan? Plan, string School, int Accuracy, bool DealsDamage);

    private readonly Dictionary<string, Entry> _templates = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>
    /// Records one template and its plan (null when no record matched it).
    /// </summary>
    /// <param name="shape">The template as it was before the plan was applied.</param>
    /// <param name="school">The template's school name, such as <c>Fire</c>.</param>
    /// <param name="plan">The plan, or null.</param>
    public void Add(SpellTemplateShape shape, string? school, SpellOverridePlan? plan) {
        var entry = new Entry(plan, school ?? "", shape.Accuracy, DealsDamage(shape.Effects));
        lock (_lock) {
            _templates[shape.Path] = entry;
        }
    }

    /// <summary>
    /// Totals the census.
    /// </summary>
    /// <param name="overrides">The spell values the plans came from.</param>
    /// <returns>The summary.</returns>
    public SpellCensusSummary Summarize(ClassicSpellOverrides overrides) {
        List<Entry> entries;
        lock (_lock) {
            entries = [.. _templates.Values];
        }

        var plans = entries.Select(entry => entry.Plan).OfType<SpellOverridePlan>().ToList();
        var changes = plans.SelectMany(plan => plan.EffectChanges).ToList();
        var matchedIds = plans.Select(plan => plan.Record.Id).ToHashSet(StringComparer.Ordinal);
        var unmatchedDamage = entries
            .Where(entry => entry.Plan is null && entry.DealsDamage)
            .Select(entry => (Entry: entry, Base: overrides.AccuracyTable?.BaseAccuracy(entry.School.ToLowerInvariant(), overrides.Lineage)))
            .Where(pair => pair.Base is not null)
            .ToList();

        return new SpellCensusSummary {
            Templates = entries.Count,
            MatchedByClientTemplate = plans.Count(plan => plan.Match == SpellMatch.ClientTemplate),
            MatchedByTreasureCard = plans.Count(plan => plan.Match == SpellMatch.TreasureCard),
            MatchedByName = plans.Count(plan => plan.Match == SpellMatch.Name),
            Unmatched = entries.Count - plans.Count,
            ChangedTemplates = plans.Count(plan => plan.ChangesTemplate),
            PipsChanged = plans.Count(plan => plan.Rank is not null),
            SchoolPipsCleared = plans.Count(plan => plan.ClearSchoolPips),
            AccuracyChanged = plans.Count(plan => plan.Accuracy is not null),
            EffectValuesChanged = changes.Count(change => change.Param is not null),
            RoundsChanged = changes.Count(change => change.Rounds is not null),
            HealModifiersChanged = changes.Count(change => change.HealModifier is not null),
            TargetsChanged = changes.Count(change => change.Target is not null),
            StructuresRebuilt = plans.Count(plan => plan.Structure is not null),
            TemplatesFullyApplied = plans.Count(plan => plan.RemainingIssues.IsEmpty),
            DamageTypesChanged = changes.Count(change => change.DamageType is not null),
            RecordsNotFullyApplied = [.. plans.Where(plan => !plan.RemainingIssues.IsEmpty).Select(plan => plan.Record.Id).Distinct().Order(StringComparer.Ordinal)],
            UnmatchedTemplateEffects = plans.Sum(plan => plan.UnmatchedTemplateEffects),
            ZeroedTemplateEffects = plans.Sum(plan => plan.ZeroedTemplateEffects),
            KindsChanged = changes.Count(change => change.EffectTypeName is not null),
            PipsInherited = plans.Count(plan => plan.PipsInherited),
            RecordsWithoutTemplate = [.. overrides.Book.Records.Where(record => !matchedIds.Contains(record.Id)).Select(record => record.Id)],
            RecordsNotInProfile = [.. overrides.RecordsNotInProfile.Select(record => record.Id)],
            PipsSkipped = plans.Where(plan => plan.PipsSkip != PipsSkip.None)
                .GroupBy(plan => plan.PipsSkip)
                .ToImmutableDictionary(group => group.Key, group => group.Count()),
            AppliedEffects = plans.SelectMany(plan => plan.AppliedEffects)
                .GroupBy(kind => kind)
                .ToImmutableDictionary(group => group.Key, group => group.Count()),
            SkippedEffects = plans.SelectMany(plan => plan.SkippedEffects)
                .GroupBy(skip => skip)
                .ToImmutableDictionary(group => group.Key, group => group.Count()),
            UnmatchedDamageTemplates = unmatchedDamage.Count,
            UnmatchedDamageOffSchoolBase = unmatchedDamage.Count(pair => (int) Math.Round(pair.Base!.Value * 100, MidpointRounding.AwayFromZero) != pair.Entry.Accuracy),
        };
    }

    private static bool DealsDamage(ImmutableArray<TemplateEffectNode> effects)
        => effects.Any(effect => effect.Kind is TemplateEffectKind.Damage or TemplateEffectKind.DamageOverTime
            || DealsDamage(effect.Children));

}
