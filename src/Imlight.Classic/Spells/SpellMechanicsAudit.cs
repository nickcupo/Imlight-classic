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
 * Compares a classic-data record with a client spell template as combat
 * will use it (after the classic pass) and lists every mechanic that is
 * not the 2009 one: pip cost, school pips, accuracy, and each effect's
 * type, school, amount, rounds, target, and the number and order of hits.
 *
 * USAGE EXAMPLE:
 * var issues = SpellMechanicsAudit.Compare(values, shapeAfterPass);
 * if (issues.IsEmpty) { ... fully applied ... }
 *
 * NOTE:
 * Each template effect is one unit: a plain effect, a rolled range (a
 * random effect whose children differ only in amount), an X card's per-pip
 * list, or an opaque structure (conditions, effect lists) compared by type
 * only. Every record effect must pair with a unit, in hit order, and every
 * unit must pair with a record effect.
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

namespace Imlight.Classic.Spells;

/// <summary>
/// Why a template's mechanics differ from its record.
/// </summary>
public enum MechanicsIssueKind {
    Pips,
    SchoolPips,
    Accuracy,
    MissingEffect,
    ExtraEffect,
    EffectType,
    School,
    Amount,
    Rounds,
    HealShare,
    Target,
    HitOrder,
    Composition,
}

/// <summary>
/// One mechanic that differs.
/// </summary>
/// <param name="Kind">What differs.</param>
/// <param name="Detail">The record's value and the template's, in words.</param>
public sealed record MechanicsIssue(MechanicsIssueKind Kind, string Detail) {

    public override string ToString() => $"{Kind}: {Detail}";

}

/// <summary>
/// Compares a record's mechanics with a template's.
/// </summary>
public static class SpellMechanicsAudit {

    private static readonly ImmutableHashSet<SpellEffectKind> s_hitKinds = [
        SpellEffectKind.Damage, SpellEffectKind.Dot, SpellEffectKind.Steal, SpellEffectKind.Heal, SpellEffectKind.Hot,
    ];

    /// <summary>
    /// One template effect as the audit compares it.
    /// </summary>
    private sealed record Unit(int Index, TemplateEffectNode Node, TemplateComposition Composition, string EffectType, string DamageType,
                               TemplateTarget Target, int Min, int Max, int Rounds, float HealModifier, bool Outcome = false) {

        public bool IsZero => Composition != TemplateComposition.Other && Min == 0 && Max == 0
            && EffectType is "kDamage" or "kHeal" or "kDamageOverTime" or "kHealOverTime" or "kStealHealth" or "kModifyOutgoingDamage"
                or "kModifyIncomingDamage" or "kModifyAccuracy" or "kModifyOutgoingHeal" or "kModifyIncomingHeal" or "kAbsorbDamage"
                or "kModifyPips" or "kModifyPowerPipChance";

    }

    /// <summary>
    /// Lists the mechanics of <paramref name="shape"/> that differ from <paramref name="values"/>.
    /// </summary>
    /// <param name="values">The record's values in the active profile.</param>
    /// <param name="shape">The template as combat uses it.</param>
    /// <param name="cardSchool">The record's school, for effects that name the card's school when they work on any school.</param>
    /// <returns>The differences; empty when the template carries the 2009 mechanics.</returns>
    public static ImmutableArray<MechanicsIssue> Compare(SpellValues values, SpellTemplateShape shape, string? cardSchool = null) {
        var issues = new List<MechanicsIssue>();
        ComparePips(values, shape, issues);
        if (values.AccuracyPercent != shape.Accuracy) {
            issues.Add(new(MechanicsIssueKind.Accuracy, $"{values.AccuracyPercent}% in 2009, {shape.Accuracy}% on the template"));
        }

        var units = UnitsOf(shape, values.Effects);
        var used = new HashSet<int>();
        var lastHit = -1;
        for (var i = 0; i < values.Effects.Length; i++) {
            var effect = values.Effects[i];
            var candidates = units.Where(unit => !used.Contains(unit.Index) && SpellEffectMeaning.CanCarry(effect.Kind, unit.EffectType)).ToList();
            if (candidates.Count == 0) {
                issues.Add(new(MechanicsIssueKind.MissingEffect, $"{Describe(effect)} has no template effect"));
                continue;
            }

            var best = candidates.OrderByDescending(unit => Score(effect, unit, lastHit, cardSchool)).ThenBy(unit => unit.Index).First();
            used.Add(best.Index);
            issues.AddRange(Differences(effect, best, cardSchool));
            if (s_hitKinds.Contains(effect.Kind) && !best.Outcome) {
                if (best.Index < lastHit) {
                    issues.Add(new(MechanicsIssueKind.HitOrder, $"{Describe(effect)} lands before an earlier hit of the card"));
                }

                lastHit = Math.Max(lastHit, best.Index);
            }
        }

        // A sacrifice card kills the caster's own minion; the record says so in its notes.
        var sacrifice = values.Effects.Any(effect => (effect.Notes ?? "").Contains("sacrific", StringComparison.OrdinalIgnoreCase));
        foreach (var unit in units.Where(unit => !used.Contains(unit.Index))) {
            if (sacrifice && unit is { EffectType: "kInstantKill", Target: TemplateTarget.MinionSingle }) {
                continue;
            }

            issues.Add(new(MechanicsIssueKind.ExtraEffect, $"template effect {Describe(unit)} is not on the 2009 card"
                + (unit.IsZero ? " (zeroed, still cast)" : "")));
        }

        return [.. issues];
    }

    private static void ComparePips(SpellValues values, SpellTemplateShape shape, List<MechanicsIssue> issues) {
        if (values.Pips.IsX != shape.IsXPip) {
            issues.Add(new(MechanicsIssueKind.Pips, $"{values.Pips} pips in 2009, {(shape.IsXPip ? "X" : shape.Rank.ToString(CultureInfo.InvariantCulture))} on the template"));
        }
        else if (values.Pips.Fixed is { } pips && pips != shape.Rank) {
            issues.Add(new(MechanicsIssueKind.Pips, $"{pips} pips in 2009, {shape.Rank} on the template"));
        }

        if (shape.SchoolPips > 0) {
            issues.Add(new(MechanicsIssueKind.SchoolPips, $"the template asks for {shape.SchoolPips} school pips; 2009 cards had none"));
        }
    }

    private static int Score(SpellEffectValues effect, Unit unit, int lastHit, string? cardSchool) {
        var score = 0;
        if (TargetFits(effect, unit)) {
            score += 8;
        }

        if (SpellEffectMeaning.SchoolFits(effect, cardSchool, unit.EffectType, unit.DamageType)) {
            score += 4;
        }

        if (!AmountDiffers(effect, unit)) {
            score += 2;
        }

        if (unit.Index > lastHit) {
            score += 1;
        }

        if (string.Equals(SpellEffectMeaning.ClientTypes(effect)[0], unit.EffectType, StringComparison.Ordinal)) {
            score += 16;
        }

        return score;
    }

    private static IEnumerable<MechanicsIssue> Differences(SpellEffectValues effect, Unit unit, string? cardSchool) {
        var name = Describe(effect);
        if (!SpellEffectMeaning.ClientTypes(effect).Contains(unit.EffectType, StringComparer.Ordinal)) {
            yield return new(MechanicsIssueKind.EffectType, $"{name} is {unit.EffectType} on the template");
        }

        if (!SpellEffectMeaning.SchoolFits(effect, cardSchool, unit.EffectType, unit.DamageType)) {
            yield return new(MechanicsIssueKind.School, $"{name} is {unit.DamageType} on the template");
        }

        if (!TargetFits(effect, unit)) {
            yield return new(MechanicsIssueKind.Target,
                $"{name} targets {Category(effect)} in 2009, {Category(unit.Target)} ({unit.Node.TargetMemberName}) on the template");
        }

        if (effect.HasAmount && effect.Min != effect.Max && unit.Composition is TemplateComposition.Plain) {
            yield return new(MechanicsIssueKind.Composition, $"{name} is a rolled range in 2009, a fixed {unit.Min} on the template");
        }
        else if (AmountDiffers(effect, unit)) {
            yield return new(MechanicsIssueKind.Amount, $"{name}: template has {DescribeAmount(unit)}");
        }

        if (effect.Kind is SpellEffectKind.Dot or SpellEffectKind.Hot && effect.Rounds is { } rounds && rounds != unit.Rounds) {
            yield return new(MechanicsIssueKind.Rounds, $"{name}: {rounds} rounds in 2009, {unit.Rounds} on the template");
        }

        if (effect.Kind == SpellEffectKind.Steal && effect.HasAmount && effect.Percent is { } share
            && Math.Abs(share / 100f - unit.HealModifier) > 0.0001f) {
            yield return new(MechanicsIssueKind.HealShare, $"{name}: heals {share}% in 2009, {unit.HealModifier * 100:0}% on the template");
        }
    }

    private static bool AmountDiffers(SpellEffectValues effect, Unit unit) {
        if (!SpellEffectMeaning.ValueKinds.Contains(effect.Kind) || unit.Composition == TemplateComposition.Other) {
            return false;
        }

        if (effect.HasAmount) {
            var (min, max) = (effect.Min!.Value, effect.Max!.Value);
            if (effect.Kind == SpellEffectKind.Pip) {
                return Math.Abs(unit.Min) != min || Math.Abs(unit.Max) != max;
            }

            // Per pip on X cards, the whole effect otherwise.
            return unit.Min != min || unit.Max != max;
        }

        if (effect.Percent is { } percent) {
            return effect.Kind == SpellEffectKind.Steal ? false : unit.Min != percent || unit.Max != percent;
        }

        return false;
    }

    private static bool TargetFits(SpellEffectValues effect, Unit unit) {
        if (effect.Targets is null && effect.Kind != SpellEffectKind.Global) {
            return true;
        }

        // An enchantment or mutation is cast on a card in hand.
        if (effect.Kind is SpellEffectKind.Enchant or SpellEffectKind.Mutate && unit.Node.TargetMemberName is "kSpell" or "kSpecificSpells") {
            return effect.Targets is null or SpellTargets.Single;
        }

        return Category(effect) == Category(unit.Target);
    }

    private static string Category(SpellEffectValues effect)
        => effect.Kind == SpellEffectKind.Global
            ? "global"
            : effect.Targets switch {
                SpellTargets.Self => "self",
                SpellTargets.Single => "one target",
                SpellTargets.AllEnemies => "all enemies",
                SpellTargets.AllAllies => "all allies",
                _ => "any",
            };

    private static string Category(TemplateTarget target)
        => target switch {
            TemplateTarget.EnemySingle or TemplateTarget.FriendlySingle or TemplateTarget.MinionSingle => "one target",
            TemplateTarget.Self => "self",
            TemplateTarget.EnemyTeam => "all enemies",
            TemplateTarget.FriendlyTeam => "all allies",
            TemplateTarget.Global => "global",
            _ => "other",
        };

    private static List<Unit> UnitsOf(SpellTemplateShape shape, ImmutableArray<SpellEffectValues> effects) {
        var units = new List<Unit>();
        for (var i = 0; i < shape.Effects.Length; i++) {
            var node = shape.Effects[i];
            var children = node.Children;
            var sameType = !children.IsEmpty && children.All(child => child.Composition == TemplateComposition.Plain
                && child.EffectTypeName == children[0].EffectTypeName && child.Target == children[0].Target);
            var uniform = sameType && children.All(child => string.Equals(child.DamageType, children[0].DamageType, StringComparison.OrdinalIgnoreCase));

            // A roll between schools (Spectral Blast) is one outcome per record effect when the record lists each outcome.
            if (node.Composition == TemplateComposition.Random && sameType && !uniform
                && effects.Count(effect => SpellEffectMeaning.CanCarry(effect.Kind, children[0].EffectTypeName)) >= children.Length) {
                for (var child = 0; child < children.Length; child++) {
                    var outcome = children[child];
                    units.Add(new Unit(i * 1000 + child + 1, outcome, TemplateComposition.Plain, outcome.EffectTypeName, outcome.DamageType,
                        outcome.Target, outcome.Param, outcome.Param, outcome.Rounds, outcome.HealModifier, Outcome: true));
                }

                continue;
            }

            switch (node.Composition) {
                case TemplateComposition.Plain:
                    units.Add(new Unit(i * 1000, node, TemplateComposition.Plain, node.EffectTypeName, node.DamageType, node.Target, node.Param, node.Param,
                        node.Rounds, node.HealModifier));
                    break;
                case TemplateComposition.Random when uniform: {
                    var first = children[0];
                    units.Add(new Unit(i * 1000, first, TemplateComposition.Random, first.EffectTypeName, first.DamageType, first.Target,
                        children.Min(child => child.Param), children.Max(child => child.Param), first.Rounds, first.HealModifier));
                    break;
                }
                case TemplateComposition.PerPip when uniform: {
                    // The per-pip amount, when every tier is that amount times its pips.
                    var first = children[0];
                    var perPip = first.PipNumber > 0 ? first.Param / first.PipNumber : first.Param;
                    var linear = children.All(child => child.Param == perPip * (child.PipNumber > 0 ? child.PipNumber : 1));
                    units.Add(new Unit(i * 1000, first, TemplateComposition.PerPip, first.EffectTypeName, first.DamageType, first.Target,
                        linear ? perPip : children.Min(child => child.Param), linear ? perPip : children.Max(child => child.Param),
                        first.Rounds, first.HealModifier));
                    break;
                }
                default: {
                    // Conditions, effect lists and mixed rolls: compared by type only. A mixed roll's type is its first child's.
                    // A roll between creatures (Spectral Minion) is typed and targeted by its children.
                    var type = node.LeadType;
                    var target = sameType ? children[0].Target : node.Target == TemplateTarget.Other && !children.IsEmpty ? children[0].Target : node.Target;
                    units.Add(new Unit(i * 1000, node, TemplateComposition.Other, type, node.DamageType, target, node.Param, node.Param, node.Rounds,
                        node.HealModifier));
                    break;
                }
            }
        }

        return units;
    }

    private static string Describe(SpellEffectValues effect) {
        var amount = effect.HasAmount
            ? effect.Min == effect.Max ? $" {effect.Min}" : $" {effect.Min}-{effect.Max}"
            : "";
        var percent = effect.Percent is { } p ? $" {p:+0;-0}%" : "";
        var rounds = effect.Rounds is { } r ? $" over {r} rounds" : "";

        return $"{ClassicSpellSchema.NameOf(effect.Kind)} {effect.School}{amount}{percent}{rounds}";
    }

    private static string Describe(Unit unit)
        => $"{unit.EffectType} {DescribeAmount(unit)} {unit.DamageType} {unit.Node.TargetMemberName}".Replace("  ", " ", StringComparison.Ordinal).Trim();

    private static string DescribeAmount(Unit unit)
        => unit.Composition switch {
            TemplateComposition.Random => $"{unit.Min}-{unit.Max}",
            TemplateComposition.PerPip => $"{unit.Min} per pip",
            TemplateComposition.Other => $"({unit.Node.ClassName})",
            _ => unit.Min.ToString(CultureInfo.InvariantCulture),
        };

}
