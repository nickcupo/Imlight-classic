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
 * Decides how a classic record's numbers land on a client spell template:
 * the pip cost, the accuracy, and for each classic effect the template
 * effect it matches and the values that effect takes.
 *
 * USAGE EXAMPLE:
 * var plan = SpellOverridePlanner.Plan(record, SpellMatch.ClientTemplate, record.ValuesFor(lineage), shape);
 *
 * NOTE:
 * Matching never adds, removes or reorders template effects: the client
 * replays the server's effect choices by index against its own template.
 * A classic effect matches a template effect of the same kind in four
 * passes, strictest first: school and target, then school alone, then
 * target alone, then kind alone. The looser passes need the pairing to be
 * the only one left for that kind, and the last two only apply to amounts
 * (damage, heals, drains, pips, wards) whose template school says little.
 * A template effect the record leaves out is zeroed only where the record
 * clearly has no such effect: an up-front hit or heal beside the record's
 * damage or heal on the same targets (Link, Helping Hands), or a global
 * whose meaning the record does not share (Power Play).
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
using System.Linq;

namespace Imlight.Classic.Spells;

/// <summary>
/// How a template found its record.
/// </summary>
public enum SpellMatch {
    ClientTemplate,

    /// <summary>
    /// The template is the Treasure Card of a trained or crossover record.
    /// </summary>
    TreasureCard,

    /// <summary>
    /// The card name of a record with no client template.
    /// </summary>
    Name,
}

/// <summary>
/// Why a record's pip cost did not reach its template.
/// </summary>
public enum PipsSkip {
    None,

    /// <summary>
    /// The record is X but the template has a fixed cost; the server decides X by the template's structure.
    /// The nearest fixed cost of a profile the active one extends is charged instead, when there is one.
    /// </summary>
    XOnFixedCostTemplate,

    /// <summary>
    /// The record has a fixed cost but the template is an X card.
    /// </summary>
    FixedOnXTemplate,
}

/// <summary>
/// Why a classic effect did not reach the template.
/// </summary>
public enum EffectSkipReason {

    /// <summary>
    /// The kind carries nothing the server reads from a template (minions, stuns, threat, enchantments and similar).
    /// </summary>
    KindNotApplied,

    /// <summary>
    /// The effect has no number to apply, such as a steal of a charm rather than health.
    /// </summary>
    NoValues,

    /// <summary>
    /// No template effect of the same kind, school and sign could be paired with it.
    /// </summary>
    NoMatchingTemplateEffect,

    /// <summary>
    /// An amount whose pip cost did not reach the template: an X card's amount is per pip and a fixed card's
    /// is the whole effect, so neither fits the other's template.
    /// </summary>
    PipsNotApplied,

}

/// <summary>
/// A classic effect that was not applied, and why.
/// </summary>
public sealed record SkippedEffect(SpellEffectKind Kind, EffectSkipReason Reason);

/// <summary>
/// What the classic values change on one template.
/// </summary>
public sealed class SpellOverridePlan {

    public required ClassicSpellRecord Record { get; init; }
    public required SpellMatch Match { get; init; }

    /// <summary>
    /// The new pip cost, or null when it is unchanged or not applied.
    /// </summary>
    public int? Rank { get; init; }

    /// <summary>
    /// True when the template's school pip requirements are cleared (2009 cards had none).
    /// </summary>
    public bool ClearSchoolPips { get; init; }

    public PipsSkip PipsSkip { get; init; }

    /// <summary>
    /// True when <see cref="Rank"/> or the kept template cost is a fixed cost inherited from a profile the active one
    /// extends, because the active profile's X cost did not fit the template.
    /// </summary>
    public bool PipsInherited { get; init; }

    /// <summary>
    /// The new accuracy as a whole percentage, or null when it is unchanged.
    /// </summary>
    public int? Accuracy { get; init; }

    public ImmutableArray<EffectChange> EffectChanges { get; init; } = [];

    /// <summary>
    /// The classic effects that were paired with a template effect, changed or not.
    /// </summary>
    public ImmutableArray<SpellEffectKind> AppliedEffects { get; init; } = [];

    public ImmutableArray<SkippedEffect> SkippedEffects { get; init; } = [];

    /// <summary>
    /// Template effects no classic effect was paired with and that were not zeroed (each outcome of a mixed roll
    /// counts); they keep their client values.
    /// </summary>
    public int UnmatchedTemplateEffects { get; init; }

    /// <summary>
    /// Paired effects whose classic target could not be written, because the client would not send a target for it.
    /// </summary>
    public int TargetsNotRepresentable { get; init; }

    /// <summary>
    /// Template effects the record has no counterpart for that are zeroed (an up-front hit or heal, a global with another meaning).
    /// </summary>
    public int ZeroedTemplateEffects { get; init; }

    /// <summary>
    /// True when applying the plan changes anything.
    /// </summary>
    public bool ChangesTemplate => Rank is not null || ClearSchoolPips || Accuracy is not null || !EffectChanges.IsEmpty;

}

/// <summary>
/// Pairs classic effects with template effects and computes the new values.
/// </summary>
public static class SpellOverridePlanner {

    private enum SlotShape {
        Single,
        Range,
        PerPip,
    }

    private enum TargetCategory {
        Other,
        Self,
        Single,
        AllEnemies,
        AllAllies,
        Global,
    }

    private sealed record Slot(SlotShape Shape, ImmutableArray<EffectAddress> Addresses, ImmutableArray<TemplateEffectNode> Effects) {

        public TemplateEffectKind Kind => Effects[0].Kind;
        public TemplateTarget Target => Effects[0].Target;
        public string DamageType => Effects[0].DamageType;

    }

    private static readonly FrozenDictionary<SpellEffectKind, TemplateEffectKind[]> s_templateKinds =
        new Dictionary<SpellEffectKind, TemplateEffectKind[]> {
            [SpellEffectKind.Damage] = [TemplateEffectKind.Damage, TemplateEffectKind.MaxHealthDamage],
            [SpellEffectKind.Dot] = [TemplateEffectKind.DamageOverTime],
            [SpellEffectKind.Heal] = [TemplateEffectKind.Heal],
            [SpellEffectKind.Hot] = [TemplateEffectKind.HealOverTime],
            [SpellEffectKind.Steal] = [TemplateEffectKind.StealHealth],
            [SpellEffectKind.Pip] = [TemplateEffectKind.ModifyPips],
            [SpellEffectKind.Ward] = [TemplateEffectKind.AbsorbDamage],
            [SpellEffectKind.Blade] = [TemplateEffectKind.ModifyOutgoingDamage],
            [SpellEffectKind.Charm] = [TemplateEffectKind.ModifyOutgoingDamage, TemplateEffectKind.ModifyAccuracy, TemplateEffectKind.ModifyOutgoingHeal],
            [SpellEffectKind.Trap] = [TemplateEffectKind.ModifyIncomingDamage],
            [SpellEffectKind.Shield] = [TemplateEffectKind.ModifyIncomingDamage],
            [SpellEffectKind.Global] = [
                TemplateEffectKind.ModifyOutgoingDamage, TemplateEffectKind.ModifyOutgoingHeal, TemplateEffectKind.ModifyAccuracy,
                TemplateEffectKind.ModifyIncomingDamage, TemplateEffectKind.ModifyIncomingHeal
            ],
        }.ToFrozenDictionary();

    private static readonly FrozenSet<SpellEffectKind> s_amountKinds = FrozenSet.Create(
        SpellEffectKind.Damage, SpellEffectKind.Dot, SpellEffectKind.Heal, SpellEffectKind.Hot, SpellEffectKind.Steal,
        SpellEffectKind.Pip, SpellEffectKind.Ward);

    /// <summary>
    /// The classic effect kinds the planner applies to templates.
    /// </summary>
    public static ImmutableArray<SpellEffectKind> AppliedKinds { get; } = [.. s_templateKinds.Keys.Order()];

    /// <summary>
    /// Plans how <paramref name="values"/> land on <paramref name="shape"/>.
    /// </summary>
    /// <param name="record">The record the values come from.</param>
    /// <param name="match">How the template found the record.</param>
    /// <param name="values">The record's values in the active profile.</param>
    /// <param name="shape">The template.</param>
    /// <param name="inheritedFixedPips">The fixed cost to charge when <paramref name="values"/> is X and the template is not.</param>
    /// <returns>The plan; empty changes when the template already carries the classic numbers.</returns>
    public static SpellOverridePlan Plan(ClassicSpellRecord record, SpellMatch match, SpellValues values, SpellTemplateShape shape,
                                         int? inheritedFixedPips = null) {
        var (rank, clearSchoolPips, pipsSkip) = PlanPips(values.Pips, shape, inheritedFixedPips);
        var accuracy = values.AccuracyPercent;
        var slots = BuildSlots(shape.Effects);
        var pairs = Pair(values.Effects, slots, amountsFit: pipsSkip == PipsSkip.None, out var skipped);

        var changes = new List<EffectChange>();
        var targetsNotRepresentable = 0;
        foreach (var (effectIndex, slotIndex) in pairs) {
            var effect = values.Effects[effectIndex];
            var slot = slots[slotIndex];
            var target = NewTarget(effect, slot, out var representable);
            if (!representable) {
                targetsNotRepresentable++;
            }

            changes.AddRange(ChangesFor(effect, slot, target));
        }

        var zeroed = SlotsToZero(values.Effects, slots, pairs);
        foreach (var slotIndex in zeroed) {
            var slot = slots[slotIndex];
            changes.AddRange(slot.Addresses.Where((_, i) => slot.Effects[i].Param != 0).Select(address => new EffectChange(address, Param: 0)));
        }

        return new SpellOverridePlan {
            Record = record,
            Match = match,
            Rank = rank,
            ClearSchoolPips = clearSchoolPips,
            PipsSkip = pipsSkip,
            PipsInherited = pipsSkip == PipsSkip.XOnFixedCostTemplate && inheritedFixedPips is not null,
            Accuracy = accuracy == shape.Accuracy ? null : accuracy,
            EffectChanges = [.. changes],
            AppliedEffects = [.. pairs.OrderBy(pair => pair.Effect).Select(pair => values.Effects[pair.Effect].Kind)],
            SkippedEffects = skipped,
            UnmatchedTemplateEffects = slots.Count - pairs.Count - zeroed.Count,
            TargetsNotRepresentable = targetsNotRepresentable,
            ZeroedTemplateEffects = zeroed.Count,
        };
    }

    private static (int? Rank, bool ClearSchoolPips, PipsSkip Skip) PlanPips(SpellPips pips, SpellTemplateShape shape, int? inheritedFixedPips) {
        if (pips.Fixed is not { } cost) {
            if (shape.IsXPip) {
                return (null, false, PipsSkip.None);
            }

            return inheritedFixedPips is { } inherited
                ? (inherited == shape.Rank ? null : inherited, shape.SchoolPips > 0, PipsSkip.XOnFixedCostTemplate)
                : (null, false, PipsSkip.XOnFixedCostTemplate);
        }

        if (shape.IsXPip) {
            return (null, false, PipsSkip.FixedOnXTemplate);
        }

        return (cost == shape.Rank ? null : cost, shape.SchoolPips > 0, PipsSkip.None);
    }

    private static List<Slot> BuildSlots(ImmutableArray<TemplateEffectNode> effects) {
        // One slot per matchable unit; unmatchable effects still get one, so they count as unmatched.
        var slots = new List<Slot>();
        for (var i = 0; i < effects.Length; i++) {
            var node = effects[i];
            var children = node.Children;
            switch (node.Composition) {
                case TemplateComposition.Plain:
                    slots.Add(new Slot(SlotShape.Single, [new EffectAddress(i)], [node]));
                    break;
                case TemplateComposition.Random when IsUniform(children):
                    slots.Add(new Slot(SlotShape.Range, [.. children.Select((_, child) => new EffectAddress(i, child))], children));
                    break;
                case TemplateComposition.Random:
                    // Children that differ by kind or school are separate outcomes (Spectral Blast), matched one by one.
                    for (var child = 0; child < children.Length; child++) {
                        if (children[child].Composition == TemplateComposition.Plain) {
                            slots.Add(new Slot(SlotShape.Single, [new EffectAddress(i, child)], [children[child]]));
                        }
                    }
                    break;
                case TemplateComposition.PerPip when IsUniform(children):
                    slots.Add(new Slot(SlotShape.PerPip, [.. children.Select((_, child) => new EffectAddress(i, child))], children));
                    break;
                default:
                    slots.Add(new Slot(SlotShape.Single, [new EffectAddress(i)], [node with { Kind = TemplateEffectKind.Other }]));
                    break;
            }
        }

        return slots;
    }

    private static bool IsUniform(ImmutableArray<TemplateEffectNode> children)
        => !children.IsEmpty
            && children.All(child => child.Composition == TemplateComposition.Plain
                && child.Kind == children[0].Kind
                && CategoryOf(child.Target) == CategoryOf(children[0].Target)
                && string.Equals(child.DamageType, children[0].DamageType, StringComparison.OrdinalIgnoreCase));

    private static List<(int Effect, int Slot)> Pair(ImmutableArray<SpellEffectValues> effects, List<Slot> slots, bool amountsFit,
                                                     out ImmutableArray<SkippedEffect> skipped) {
        var pairs = new List<(int Effect, int Slot)>();
        var usedSlots = new HashSet<int>();
        var open = new List<int>();
        var skips = new List<(int Effect, SkippedEffect Skip)>();
        for (var i = 0; i < effects.Length; i++) {
            var effect = effects[i];
            if (!s_templateKinds.ContainsKey(effect.Kind)) {
                skips.Add((i, new SkippedEffect(effect.Kind, EffectSkipReason.KindNotApplied)));
            }
            else if (s_amountKinds.Contains(effect.Kind) ? !effect.HasAmount : effect.Percent is null) {
                skips.Add((i, new SkippedEffect(effect.Kind, EffectSkipReason.NoValues)));
            }
            else if (s_amountKinds.Contains(effect.Kind) && !amountsFit) {
                skips.Add((i, new SkippedEffect(effect.Kind, EffectSkipReason.PipsNotApplied)));
            }
            else {
                open.Add(i);
            }
        }

        for (var pass = 0; pass < 4; pass++) {
            foreach (var effectIndex in open.ToList()) {
                var effect = effects[effectIndex];
                var free = Enumerable.Range(0, slots.Count)
                    .Where(slot => !usedSlots.Contains(slot) && IsCandidate(effect, slots[slot]))
                    .ToList();
                var unique = free.Count == 1 && open.Count(other => effects[other].Kind == effect.Kind) == 1;
                var loose = s_amountKinds.Contains(effect.Kind);
                foreach (var slotIndex in free) {
                    var slot = slots[slotIndex];
                    var school = SchoolFits(effect, slot);
                    var target = CategoryOf(effect) == CategoryOf(slot.Target);
                    var fits = pass switch {
                        0 => school && target,
                        1 => school && unique,
                        2 => loose && target && unique,
                        _ => loose && unique,
                    };
                    if (fits) {
                        pairs.Add((effectIndex, slotIndex));
                        usedSlots.Add(slotIndex);
                        open.Remove(effectIndex);
                        break;
                    }
                }
            }
        }

        skips.AddRange(open.Select(i => (i, new SkippedEffect(effects[i].Kind, EffectSkipReason.NoMatchingTemplateEffect))));
        skipped = [.. skips.OrderBy(skip => skip.Effect).Select(skip => skip.Skip)];

        return pairs;
    }

    private static List<int> SlotsToZero(ImmutableArray<SpellEffectValues> effects, List<Slot> slots, List<(int Effect, int Slot)> pairs) {
        var pairedEffects = pairs.Select(pair => pair.Effect).ToHashSet();
        var pairedSlots = pairs.Select(pair => pair.Slot).ToHashSet();
        var zero = new List<int>();

        // Every amount the record gives has landed, so a remaining up-front hit or heal on targets the record's
        // own damage or heal already covers is one the card did not have.
        var amountsLanded = Enumerable.Range(0, effects.Length)
            .Where(i => s_amountKinds.Contains(effects[i].Kind) && effects[i].HasAmount)
            .All(pairedEffects.Contains);
        var covered = pairs
            .Select(pair => (Family: FamilyOf(slots[pair.Slot].Kind), Targets: CategoryOf(slots[pair.Slot].Target)))
            .Where(cover => cover.Family is not null)
            .ToHashSet();

        // A global the record gives but no template global means is a different bubble; the template's own
        // meaning (Power Play's Balance boost) was not in the card.
        var globalsDiffer = effects.Any(effect => effect.Kind == SpellEffectKind.Global && effect.Percent is not null)
            && !Enumerable.Range(0, effects.Length).Any(i => effects[i].Kind == SpellEffectKind.Global && pairedEffects.Contains(i));

        for (var i = 0; i < slots.Count; i++) {
            var slot = slots[i];
            if (pairedSlots.Contains(i) || slot.Effects.All(node => node.Param == 0)) {
                continue;
            }

            var upFront = amountsLanded
                && slot is { Shape: SlotShape.Single, Kind: TemplateEffectKind.Damage or TemplateEffectKind.Heal }
                && slot.Addresses[0].Child < 0
                && covered.Contains((FamilyOf(slot.Kind), CategoryOf(slot.Target)));
            var otherGlobal = globalsDiffer
                && CategoryOf(slot.Target) == TargetCategory.Global
                && s_templateKinds[SpellEffectKind.Global].Contains(slot.Kind);
            if (upFront || otherGlobal) {
                zero.Add(i);
            }
        }

        return zero;
    }

    private static SpellEffectKind? FamilyOf(TemplateEffectKind kind)
        => kind switch {
            TemplateEffectKind.Damage or TemplateEffectKind.DamageOverTime or TemplateEffectKind.StealHealth
                or TemplateEffectKind.MaxHealthDamage => SpellEffectKind.Damage,
            TemplateEffectKind.Heal or TemplateEffectKind.HealOverTime => SpellEffectKind.Heal,
            _ => null,
        };

    private static bool IsCandidate(SpellEffectValues effect, Slot slot) {
        if (!s_templateKinds[effect.Kind].Contains(slot.Kind)) {
            return false;
        }

        if ((effect.Kind == SpellEffectKind.Global) != (CategoryOf(slot.Target) == TargetCategory.Global)) {
            return false;
        }

        // A share of max health only turns into a flat hit on the same targets (Empower's cost to the caster).
        if (slot.Kind == TemplateEffectKind.MaxHealthDamage && CategoryOf(effect) != CategoryOf(slot.Target)) {
            return false;
        }

        // A percent never flips the sign of the template effect it replaces: a trap stays a trap, a shield a shield.
        if (s_amountKinds.Contains(effect.Kind) || effect.Percent is not { } percent || percent == 0) {
            return true;
        }

        return slot.Effects.All(node => node.Param == 0 || (node.Param > 0) == (percent > 0));
    }

    private static bool SchoolFits(SpellEffectValues effect, Slot slot)
        => string.Equals(slot.DamageType, effect.School, StringComparison.OrdinalIgnoreCase)
            || (effect.Kind != SpellEffectKind.Global && string.Equals(slot.DamageType, "All", StringComparison.OrdinalIgnoreCase))
            // A healing bubble changes every heal whatever its school; the client files them under Life (Doom and Gloom).
            || (effect.Kind == SpellEffectKind.Global && slot.Kind is TemplateEffectKind.ModifyOutgoingHeal or TemplateEffectKind.ModifyIncomingHeal);

    private static TargetCategory CategoryOf(TemplateTarget target)
        => target switch {
            TemplateTarget.EnemySingle or TemplateTarget.FriendlySingle or TemplateTarget.MinionSingle => TargetCategory.Single,
            TemplateTarget.Self => TargetCategory.Self,
            TemplateTarget.EnemyTeam => TargetCategory.AllEnemies,
            TemplateTarget.FriendlyTeam => TargetCategory.AllAllies,
            TemplateTarget.Global => TargetCategory.Global,
            _ => TargetCategory.Other,
        };

    private static TargetCategory CategoryOf(SpellEffectValues effect)
        => effect.Kind == SpellEffectKind.Global
            ? TargetCategory.Global
            : effect.Targets switch {
                SpellTargets.Self => TargetCategory.Self,
                SpellTargets.Single => TargetCategory.Single,
                SpellTargets.AllEnemies => TargetCategory.AllEnemies,
                SpellTargets.AllAllies => TargetCategory.AllAllies,
                _ => TargetCategory.Other,
            };

    private static TemplateTarget? NewTarget(SpellEffectValues effect, Slot slot, out bool representable) {
        representable = true;
        var have = CategoryOf(slot.Target);
        var want = CategoryOf(effect);
        if (effect.Targets is null || want == have || have is TargetCategory.Global or TargetCategory.Other) {
            return null;
        }

        // The client asks for a target only when its own template is single-target, so a card the client
        // casts without one cannot become single-target on the server.
        TemplateTarget? target = want switch {
            TargetCategory.Self => TemplateTarget.Self,
            TargetCategory.AllEnemies => TemplateTarget.EnemyTeam,
            TargetCategory.AllAllies => TemplateTarget.FriendlyTeam,
            _ => null,
        };
        representable = target is not null;

        return target;
    }

    private static IEnumerable<EffectChange> ChangesFor(SpellEffectValues effect, Slot slot, TemplateTarget? target) {
        var values = NewParams(effect, slot);
        TemplateEffectKind? kind = slot.Kind == TemplateEffectKind.MaxHealthDamage ? TemplateEffectKind.Damage : null;
        for (var i = 0; i < slot.Effects.Length; i++) {
            var node = slot.Effects[i];
            int? param = values[i] == node.Param ? null : values[i];
            int? rounds = effect.Kind is SpellEffectKind.Dot or SpellEffectKind.Hot && effect.Rounds is { } r && r != node.Rounds ? r : null;
            float? heal = effect.Kind == SpellEffectKind.Steal && effect.Percent is { } p && Math.Abs(p / 100f - node.HealModifier) > 0.0001f
                ? p / 100f
                : null;
            var newTarget = target is { } t && t != node.Target ? target : null;
            if (param is not null || rounds is not null || heal is not null || newTarget is not null || kind is not null) {
                yield return new EffectChange(slot.Addresses[i], kind is null ? param : values[i], rounds, heal, newTarget, kind);
            }
        }
    }

    private static int[] NewParams(SpellEffectValues effect, Slot slot) {
        var nodes = slot.Effects;
        var values = new int[nodes.Length];
        if (!s_amountKinds.Contains(effect.Kind)) {
            Array.Fill(values, effect.Percent!.Value);

            return values;
        }

        var min = effect.Min!.Value;
        var max = effect.Max!.Value;
        var mean = (min + max) / 2.0;
        switch (slot.Shape) {
            case SlotShape.Range when nodes.Length > 1:
                // Spread the range over the rolled children, smallest template value first, so each index
                // the client replays still means "low roll" or "high roll".
                var order = Enumerable.Range(0, nodes.Length).OrderBy(i => nodes[i].Param).ThenBy(i => i).ToArray();
                for (var rank = 0; rank < order.Length; rank++) {
                    values[order[rank]] = Round(min + (max - min) * rank / (double) (order.Length - 1));
                }
                break;
            case SlotShape.PerPip:
                for (var i = 0; i < nodes.Length; i++) {
                    var pips = nodes[i].PipNumber > 0 ? nodes[i].PipNumber : i + 1;
                    values[i] = Round(mean * pips);
                }
                break;
            default:
                Array.Fill(values, Round(mean));
                break;
        }

        if (effect.Kind == SpellEffectKind.Pip) {
            for (var i = 0; i < values.Length; i++) {
                values[i] = nodes[i].Param < 0 ? -values[i] : values[i];
            }
        }

        return values;
    }

    private static int Round(double value)
        => (int) Math.Round(value, MidpointRounding.AwayFromZero);

}
