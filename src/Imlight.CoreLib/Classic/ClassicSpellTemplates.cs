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
 * Writes the classic profile's spell values (pip cost, accuracy, effect
 * amounts, percentages, rounds and targets) into client spell templates as
 * they load, so combat reads 2009 numbers without knowing about classic.
 *
 * USAGE EXAMPLE:
 * ClassicSpellTemplates.Initialize(profile, spellsPath, configured, accuracyTablePath); // ClassicStartup, before resources
 * ClassicSpellTemplates.Apply(spellTemplate, path, census: true);                       // wherever a SpellTemplate is deserialized
 *
 * NOTE:
 * SpellFactory and CoreObjectFactory each deserialize their own copy of a
 * template, so both call Apply. Nothing happens until Initialize runs, and
 * ClassicStartup only runs it for a restricted profile: dev-unrestricted
 * and no profile keep the client's values. Spells with no record keep them
 * too. The census counts SpellFactory's pass, which sees every template,
 * and is dropped once logged. SpellTemplateMapping (Imlight.Classic) holds
 * the mapping by client type and enum names; this file only reads and
 * writes the generated fields.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.Classic.Spells;
using Imlight.Common;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// Applies classic spell values to client spell templates at load.
/// </summary>
public static class ClassicSpellTemplates {

    private static volatile ClassicSpellOverrides? s_overrides;
    private static SpellOverrideCensus? s_census = new();

    /// <summary>
    /// Loads the spell records and the profile's accuracy table.
    /// </summary>
    /// <param name="profile">The active, restricted profile.</param>
    /// <param name="spellsPath">The classic-data/spells directory.</param>
    /// <param name="spellsPathConfigured">True when <c>[Classic] SpellsPath</c> names the directory, rather than the default next to the profiles.</param>
    /// <param name="accuracyTablePath">The profile's accuracy table file, or null when it names none.</param>
    /// <exception cref="ClassicDataException">A record or the accuracy table is invalid, or a configured SpellsPath does not exist.</exception>
    /// <exception cref="InvalidOperationException">The client's generated enums lack a name the mapping uses.</exception>
    public static void Initialize(ClassicProfile profile, string spellsPath, bool spellsPathConfigured, string? accuracyTablePath) {
        if (!Directory.Exists(spellsPath)) {
            if (spellsPathConfigured) {
                throw new ClassicDataException(new ClassicDataError(spellsPath, "Classic.SpellsPath", null,
                    "the configured spells directory does not exist"));
            }

            Logger.Warning("Classic spell values not loaded: {SpellsPath} does not exist, so every spell keeps the client's values.",
                Logger.Args(spellsPath));

            return;
        }

        CheckClientNames();

        var book = ClassicSpellLoader.Load(spellsPath);
        AccuracyTable? accuracyTable = null;
        if (accuracyTablePath is not null && File.Exists(accuracyTablePath)) {
            accuracyTable = AccuracyTableLoader.Load(accuracyTablePath);
        }

        s_overrides = new ClassicSpellOverrides(book, profile, accuracyTable);
        Logger.Information("Classic spell values: {Records} records from {SpellsPath} for profile {Profile}.",
            Logger.Args(book.Records.Length, spellsPath, profile.Id));
    }

    /// <summary>
    /// Writes the active profile's values into <paramref name="template"/> when a record matches it.
    /// </summary>
    /// <param name="template">A freshly deserialized template; anything else is ignored.</param>
    /// <param name="path">The template's Root.wad path.</param>
    /// <param name="census">True to count the template in the startup census.</param>
    public static void Apply(CoreTemplate? template, string? path, bool census = false) {
        if (s_overrides is not { } overrides || template is not SpellTemplate spell || path is null) {
            return;
        }

        if (!census && overrides.Find(path, spell.m_name) is null) {
            return;
        }

        var shape = SpellTemplateEditor.ShapeOf(spell, path);
        var plan = overrides.PlanFor(shape);
        if (census) {
            s_census?.Add(shape, spell.m_sMagicSchoolName, plan);
        }

        if (plan is not { ChangesTemplate: true }) {
            return;
        }

        SpellTemplateEditor.ApplyPlan(spell, plan);
        if (census) {
            Logger.Debug("Classic spell {Record}: pips {OldPips} -> {NewPips}, accuracy {OldAccuracy} -> {NewAccuracy}, {Effects} effect changes.",
                Logger.Args(plan.Record.Id, shape.Rank, plan.Rank ?? shape.Rank, shape.Accuracy, plan.Accuracy ?? shape.Accuracy,
                    plan.EffectChanges.Length));
        }
    }

    /// <summary>
    /// True when a spell trainer may offer the spell template at <paramref name="path"/>: always when no classic
    /// spell records are loaded, else only when a record of the active profile teaches it (see
    /// <see cref="ClassicSpellOverrides.IsTrainable"/>).
    /// </summary>
    /// <param name="path">The template's Root.wad path, or null when unknown.</param>
    /// <returns>True if the spell may be trained.</returns>
    public static bool IsTrainable(string? path)
        => s_overrides is not { } overrides || overrides.IsTrainable(path);

    /// <summary>
    /// True when spell records restrict what trainers teach.
    /// </summary>
    public static bool RestrictsTraining => s_overrides is not null;

    /// <summary>CLASSIC: the classic spell records in force (empty without a restricted profile).</summary>
    internal static IReadOnlyList<ClassicSpellRecord> Records
        => s_overrides?.Book.Records is { IsDefault: false } records ? records : [];

    /// <summary>
    /// Logs what the spell values changed. Call after the resources have loaded.
    /// </summary>
    public static void LogCensus() {
        if (s_overrides is not { } overrides || Interlocked.Exchange(ref s_census, null) is not { } census) {
            return;
        }

        var summary = census.Summarize(overrides);
        Logger.Information("Classic spell census over {Templates} client spell templates: {ByPath} matched by client_template, {ByTreasureCard} as the Treasure Card of a trained card, {ByName} by name, {Unmatched} without a record keep the client's values.",
            Logger.Args(summary.Templates, summary.MatchedByClientTemplate, summary.MatchedByTreasureCard, summary.MatchedByName, summary.Unmatched));
        Logger.Information("Classic spell values changed {Changed} templates: pip cost {Pips} (school pips cleared on {SchoolPips}), accuracy {Accuracy}, effect lists rebuilt {Structures}, effect amounts and percentages {Params}, rounds {Rounds}, drain heal shares {Heal}, targets {Targets}, effect types {Kinds}, schools {Schools}.",
            Logger.Args(summary.ChangedTemplates, summary.PipsChanged, summary.SchoolPipsCleared, summary.AccuracyChanged, summary.StructuresRebuilt,
                summary.EffectValuesChanged, summary.RoundsChanged, summary.HealModifiersChanged, summary.TargetsChanged, summary.KindsChanged,
                summary.DamageTypesChanged));
        Logger.Information("Classic spell mechanics: {Full} of {Matched} matched templates carry every 2009 mechanic of their record (see classic-data/spells/APPLIED.md).",
            Logger.Args(summary.TemplatesFullyApplied, summary.Templates - summary.Unmatched));
        Logger.Information("Classic spell effects applied: {Applied}. Carried by the client's own effect, with no number to write (the mechanics check covers their type and target): {NotApplied}. Not applied, no number to apply: {NoValues}. Not applied, no matching template effect: {NoMatch}. Not applied, the pip cost did not fit the template: {PipsNotApplied}.",
            Logger.Args(summary.DescribeApplied(), summary.DescribeSkipped(EffectSkipReason.KindNotApplied),
                summary.DescribeSkipped(EffectSkipReason.NoValues), summary.DescribeSkipped(EffectSkipReason.NoMatchingTemplateEffect),
                summary.DescribeSkipped(EffectSkipReason.PipsNotApplied)));
        Logger.Information("Classic spell templates zero {Zeroed} effects the classic card did not have and keep {Unmatched} effects that have no classic counterpart at their client values.",
            Logger.Args(summary.ZeroedTemplateEffects, summary.UnmatchedTemplateEffects));

        if (!summary.PipsSkipped.IsEmpty) {
            Logger.Warning("Classic spell pip costs not applied: {XOnFixed} X costs on fixed-cost templates ({Inherited} charge the fixed cost of a profile this one extends), {FixedOnX} fixed costs on X templates.",
                Logger.Args(summary.PipsSkipped.GetValueOrDefault(PipsSkip.XOnFixedCostTemplate), summary.PipsInherited,
                    summary.PipsSkipped.GetValueOrDefault(PipsSkip.FixedOnXTemplate)));
        }

        if (!summary.RecordsNotFullyApplied.IsEmpty) {
            Logger.Warning("Classic spell records whose templates still differ from the 2009 card (see classic-data/spells/APPLIED.md): {Records}.",
                Logger.Args(string.Join(", ", summary.RecordsNotFullyApplied)));
        }

        if (!summary.RecordsWithoutTemplate.IsEmpty) {
            Logger.Warning("Classic spell records that match no client template: {Records}.",
                Logger.Args(string.Join(", ", summary.RecordsWithoutTemplate)));
        }

        if (!summary.RecordsNotInProfile.IsEmpty) {
            Logger.Information("Classic spell records not listed for profile {Profile} still apply their values: {Count}.",
                Logger.Args(overrides.ProfileId, summary.RecordsNotInProfile.Length));
        }

        if (overrides.AccuracyTable is { } table) {
            Logger.Information("Classic accuracy table {Table}: {Differ} of {Total} damage spells without a record differ from their school's base; they keep the client's accuracy.",
                Logger.Args(table.Id, summary.UnmatchedDamageOffSchoolBase, summary.UnmatchedDamageTemplates));
        }
    }

    private static void CheckClientNames() {
        var missing = SpellTemplateMapping.EffectTypeNames.Concat(SpellEffectMeaning.AllClientTypes)
            .Where(name => !Enum.TryParse<kSpellEffects>(name, out _))
            .Concat(SpellTemplateMapping.TargetNames.Where(name => !Enum.TryParse<kEffectTarget>(name, out _)))
            .ToList();
        if (missing.Count > 0) {
            throw new InvalidOperationException(
                $"The client's spell effect enums lack names the classic spell values use: {string.Join(", ", missing)}.");
        }
    }

}
