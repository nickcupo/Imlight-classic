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
 * Applies a plan to a SpellTemplateShape, the way the server applies it to
 * the generated SpellTemplate (SpellTemplateEditor.ApplyPlan), so the
 * planner can check its own result against the record before choosing it.
 *
 * USAGE EXAMPLE:
 * var after = SpellPlanSimulator.Apply(shape, plan);
 * var issues = SpellMechanicsAudit.Compare(values, after, record.School);
 *
 * NOTE:
 * A structure copies the original effects first; the effect changes then
 * address the new list.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Immutable;
using System.Linq;

namespace Imlight.Classic.Spells;

/// <summary>
/// Applies plans to template shapes.
/// </summary>
public static class SpellPlanSimulator {

    /// <summary>
    /// The shape <paramref name="plan"/> turns <paramref name="shape"/> into.
    /// </summary>
    /// <param name="shape">The template before the plan.</param>
    /// <param name="plan">The plan; null leaves the shape as it is.</param>
    /// <returns>The template after the plan.</returns>
    public static SpellTemplateShape Apply(SpellTemplateShape shape, SpellOverridePlan? plan) {
        if (plan is null) {
            return shape;
        }

        var effects = plan.Structure is { } structure
            ? [.. structure.Select(address => NodeAt(shape.Effects, address) ?? new TemplateEffectNode { Composition = TemplateComposition.Other })]
            : shape.Effects;
        var nodes = effects.ToBuilder();
        // CLASSIC: prune a native random wrapper's choices without turning them into independent hits.
        foreach (var (index, children) in plan.RandomChildren) {
            var original = nodes[index];
            nodes[index] = original with { Children = [.. children.Select(child => original.Children[child])] };
        }

        foreach (var change in plan.EffectChanges) {
            if (change.Address.Index < 0 || change.Address.Index >= nodes.Count) {
                continue;
            }

            var top = nodes[change.Address.Index];
            if (change.Address.Child < 0) {
                nodes[change.Address.Index] = Changed(top, change);
            }
            else if (change.Address.Child < top.Children.Length) {
                nodes[change.Address.Index] = top with {
                    Children = top.Children.SetItem(change.Address.Child, Changed(top.Children[change.Address.Child], change)),
                };
            }
        }

        return shape with {
            Rank = plan.Rank ?? shape.Rank,
            SchoolPips = plan.ClearSchoolPips ? 0 : shape.SchoolPips,
            Accuracy = plan.Accuracy ?? shape.Accuracy,
            Effects = nodes.ToImmutable(),
        };
    }

    /// <summary>
    /// The node an address names in <paramref name="effects"/>.
    /// </summary>
    public static TemplateEffectNode? NodeAt(ImmutableArray<TemplateEffectNode> effects, EffectAddress address) {
        if (address.Index < 0 || address.Index >= effects.Length) {
            return null;
        }

        var node = effects[address.Index];
        if (address.Child < 0) {
            return node;
        }

        return address.Child < node.Children.Length ? node.Children[address.Child] : null;
    }

    private static TemplateEffectNode Changed(TemplateEffectNode node, EffectChange change) {
        if (change.EffectTypeName is { } type) {
            node = node with { EffectType = type, Kind = SpellTemplateMapping.KindOf(type) };
        }

        if (change.Param is { } param) {
            node = node with { Param = param };
        }

        if (change.Rounds is { } rounds) {
            node = node with { Rounds = rounds };
        }

        if (change.HealModifier is { } heal) {
            node = node with { HealModifier = heal };
        }

        if (change.Target is { } target && SpellTemplateMapping.EffectTargetName(target) is { } targetName) {
            node = node with { Target = target, TargetName = targetName };
        }

        if (change.DamageType is { } damageType) {
            node = node with { DamageType = damageType };
        }

        return node;
    }

}
