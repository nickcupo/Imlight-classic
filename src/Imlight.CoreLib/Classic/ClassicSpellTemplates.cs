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
 * ClassicSpellTemplates.Initialize(profile, spellsPath, accuracyTablePath);   // ClassicStartup, before resources
 * ClassicSpellTemplates.Apply(spellTemplate, path, census: true);             // wherever a SpellTemplate is deserialized
 *
 * NOTE:
 * SpellFactory and CoreObjectFactory each deserialize their own copy of a
 * template, so both call Apply. Nothing happens until Initialize runs, and
 * ClassicStartup only runs it for a restricted profile: dev-unrestricted
 * and no profile keep the client's values. Spells with no record keep them
 * too. The census counts SpellFactory's pass, which sees every template.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private static readonly SpellOverrideCensus s_census = new();

    /// <summary>
    /// Loads the spell records and the profile's accuracy table.
    /// </summary>
    /// <param name="profile">The active, restricted profile.</param>
    /// <param name="spellsPath">The classic-data/spells directory.</param>
    /// <param name="accuracyTablePath">The profile's accuracy table file, or null when it names none.</param>
    /// <exception cref="ClassicDataException">A record or the accuracy table is invalid.</exception>
    public static void Initialize(ClassicProfile profile, string spellsPath, string? accuracyTablePath) {
        if (!Directory.Exists(spellsPath)) {
            Logger.Warning("Classic spell values not loaded: {SpellsPath} does not exist, so every spell keeps the client's values.",
                Logger.Args(spellsPath));

            return;
        }

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

        var shape = ShapeOf(spell, path);
        var plan = overrides.PlanFor(shape);
        if (census) {
            s_census.Add(shape, spell.m_sMagicSchoolName, plan);
        }

        if (plan is not { ChangesTemplate: true }) {
            return;
        }

        ApplyPlan(spell, plan);
        if (census) {
            Logger.Debug("Classic spell {Record}: pips {OldPips} -> {NewPips}, accuracy {OldAccuracy} -> {NewAccuracy}, {Effects} effect changes.",
                Logger.Args(plan.Record.Id, shape.Rank, plan.Rank ?? shape.Rank, shape.Accuracy, plan.Accuracy ?? shape.Accuracy,
                    plan.EffectChanges.Length));
        }
    }

    /// <summary>
    /// Logs what the spell values changed. Call after the resources have loaded.
    /// </summary>
    public static void LogCensus() {
        if (s_overrides is not { } overrides) {
            return;
        }

        var summary = s_census.Summarize(overrides);
        Logger.Information("Classic spell census over {Templates} client spell templates: {ByPath} matched by client_template, {ByName} by name, {Unmatched} without a record keep the client's values.",
            Logger.Args(summary.Templates, summary.MatchedByClientTemplate, summary.MatchedByName, summary.Unmatched));
        Logger.Information("Classic spell values changed {Changed} templates: pip cost {Pips} (school pips cleared on {SchoolPips}), accuracy {Accuracy}, effect amounts and percentages {Params}, rounds {Rounds}, drain heal shares {Heal}, targets {Targets}.",
            Logger.Args(summary.ChangedTemplates, summary.PipsChanged, summary.SchoolPipsCleared, summary.AccuracyChanged,
                summary.EffectValuesChanged, summary.RoundsChanged, summary.HealModifiersChanged, summary.TargetsChanged));
        Logger.Information("Classic spell effects applied: {Applied}. Not applied, the server reads no such value from a template: {NotApplied}. Not applied, no number to apply: {NoValues}. Not applied, no matching template effect: {NoMatch}.",
            Logger.Args(summary.DescribeApplied(), summary.DescribeSkipped(EffectSkipReason.KindNotApplied),
                summary.DescribeSkipped(EffectSkipReason.NoValues), summary.DescribeSkipped(EffectSkipReason.NoMatchingTemplateEffect)));
        Logger.Information("Classic spell templates keep {Unmatched} effects that have no classic counterpart at their client values.",
            Logger.Args(summary.UnmatchedTemplateEffects));

        if (!summary.PipsSkipped.IsEmpty) {
            Logger.Warning("Classic spell pip costs not applied: {XOnFixed} X costs on fixed-cost templates, {FixedOnX} fixed costs on X templates.",
                Logger.Args(summary.PipsSkipped.GetValueOrDefault(PipsSkip.XOnFixedCostTemplate),
                    summary.PipsSkipped.GetValueOrDefault(PipsSkip.FixedOnXTemplate)));
        }

        if (summary.TargetsNotRepresentable > 0) {
            Logger.Warning("Classic spell targets not applied on {Count} effects: the client sends no target for them.",
                Logger.Args(summary.TargetsNotRepresentable));
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

    private static SpellTemplateShape ShapeOf(SpellTemplate template, string path) {
        var rank = template.m_spellRank;
        var effects = template.m_effects ?? [];

        return new SpellTemplateShape {
            Path = path,
            Name = template.m_name ?? "",
            Rank = rank?.m_spellRank ?? 0,
            IsXPip = rank?.m_xPipSpell == true || effects.Any(effect => effect is VariableSpellEffect),
            SchoolPips = rank is null ? 0 : rank.m_balancePips + rank.m_deathPips + rank.m_firePips + rank.m_icePips
                + rank.m_lifePips + rank.m_mythPips + rank.m_stormPips + rank.m_shadowPips,
            Accuracy = template.m_accuracy,
            Effects = [.. effects.Select(NodeOf)],
        };
    }

    private static TemplateEffectNode NodeOf(SpellEffect? effect) {
        if (effect is null) {
            return new TemplateEffectNode { Composition = TemplateComposition.Other };
        }

        var node = new TemplateEffectNode {
            Kind = KindOf(effect.m_effectType),
            Target = TargetOf(effect.m_effectTarget),
            DamageType = effect.m_sDamageType ?? "",
            Param = effect.m_effectParam,
            Rounds = effect.m_numRounds,
            PipNumber = effect.m_pipNum,
            HealModifier = effect.m_healModifier,
        };

        return effect switch {
            RandomSpellEffect random => node with {
                Composition = TemplateComposition.Random,
                Children = [.. (random.m_effectList ?? []).Select(NodeOf)],
            },
            VariableSpellEffect variable => node with {
                Composition = TemplateComposition.PerPip,
                Children = [.. (variable.m_effectList ?? []).Select(NodeOf)],
            },
            _ when effect.GetType() == typeof(SpellEffect) => node,
            _ => node with { Composition = TemplateComposition.Other },
        };
    }

    private static TemplateEffectKind KindOf(kSpellEffects type)
        => type switch {
            kSpellEffects.kDamage => TemplateEffectKind.Damage,
            kSpellEffects.kDamageOverTime => TemplateEffectKind.DamageOverTime,
            kSpellEffects.kHeal => TemplateEffectKind.Heal,
            kSpellEffects.kHealOverTime => TemplateEffectKind.HealOverTime,
            kSpellEffects.kStealHealth => TemplateEffectKind.StealHealth,
            kSpellEffects.kModifyOutgoingDamage => TemplateEffectKind.ModifyOutgoingDamage,
            kSpellEffects.kModifyIncomingDamage => TemplateEffectKind.ModifyIncomingDamage,
            kSpellEffects.kModifyAccuracy => TemplateEffectKind.ModifyAccuracy,
            kSpellEffects.kModifyOutgoingHeal => TemplateEffectKind.ModifyOutgoingHeal,
            kSpellEffects.kModifyIncomingHeal => TemplateEffectKind.ModifyIncomingHeal,
            kSpellEffects.kAbsorbDamage => TemplateEffectKind.AbsorbDamage,
            kSpellEffects.kModifyPips => TemplateEffectKind.ModifyPips,
            _ => TemplateEffectKind.Other,
        };

    private static TemplateTarget TargetOf(kEffectTarget target)
        => target switch {
            kEffectTarget.kEnemySingle => TemplateTarget.EnemySingle,
            kEffectTarget.kFriendlySingle => TemplateTarget.FriendlySingle,
            kEffectTarget.kMinion or kEffectTarget.kFriendlyMinion or kEffectTarget.kEnemyMinion
                or kEffectTarget.kCasterMinion or kEffectTarget.kTargetMinion => TemplateTarget.MinionSingle,
            kEffectTarget.kSelf => TemplateTarget.Self,
            kEffectTarget.kEnemyTeam or kEffectTarget.kEnemyTeamAllAtOnce => TemplateTarget.EnemyTeam,
            kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce => TemplateTarget.FriendlyTeam,
            kEffectTarget.kGlobal => TemplateTarget.Global,
            _ => TemplateTarget.Other,
        };

    private static kEffectTarget EffectTargetOf(TemplateTarget target)
        => target switch {
            TemplateTarget.EnemySingle => kEffectTarget.kEnemySingle,
            TemplateTarget.FriendlySingle => kEffectTarget.kFriendlySingle,
            TemplateTarget.Self => kEffectTarget.kSelf,
            TemplateTarget.EnemyTeam => kEffectTarget.kEnemyTeam,
            TemplateTarget.FriendlyTeam => kEffectTarget.kFriendlyTeam,
            TemplateTarget.Global => kEffectTarget.kGlobal,
            _ => kEffectTarget.kInvalidTarget,
        };

    private static void ApplyPlan(SpellTemplate template, SpellOverridePlan plan) {
        if (plan.Rank is { } rank) {
            template.m_spellRank ??= new SpellRank();
            template.m_spellRank.m_spellRank = (byte) rank;
        }

        if (plan.ClearSchoolPips && template.m_spellRank is { } spellRank) {
            spellRank.m_balancePips = 0;
            spellRank.m_deathPips = 0;
            spellRank.m_firePips = 0;
            spellRank.m_icePips = 0;
            spellRank.m_lifePips = 0;
            spellRank.m_mythPips = 0;
            spellRank.m_stormPips = 0;
            spellRank.m_shadowPips = 0;
        }

        if (plan.Accuracy is { } accuracy) {
            template.m_accuracy = accuracy;
        }

        foreach (var change in plan.EffectChanges) {
            if (EffectAt(template.m_effects, change.Address) is not { } effect) {
                continue;
            }

            if (change.Param is { } param) {
                effect.m_effectParam = param;
            }

            if (change.Rounds is { } rounds) {
                effect.m_numRounds = rounds;
            }

            if (change.HealModifier is { } heal) {
                effect.m_healModifier = heal;
            }

            if (change.Target is { } target) {
                effect.m_effectTarget = EffectTargetOf(target);
            }
        }
    }

    private static SpellEffect? EffectAt(List<SpellEffect>? effects, EffectAddress address) {
        if (effects is null || address.Index >= effects.Count) {
            return null;
        }

        var effect = effects[address.Index];
        if (address.Child < 0) {
            return effect;
        }

        var children = effect switch {
            RandomSpellEffect random => random.m_effectList,
            VariableSpellEffect variable => variable.m_effectList,
            _ => null,
        };

        return children is not null && address.Child < children.Count ? children[address.Child] : null;
    }

}
