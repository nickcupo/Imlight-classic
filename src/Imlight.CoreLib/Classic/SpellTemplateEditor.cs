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
 * Reads a generated SpellTemplate as a SpellTemplateShape and applies a
 * classic plan to it: pip cost, accuracy, a rebuilt effect list, and each
 * effect's type, amount, rounds, heal share, target and school.
 *
 * USAGE EXAMPLE:
 * var plan = overrides.PlanFor(SpellTemplateEditor.ShapeOf(template, path));
 * if (plan is { ChangesTemplate: true }) SpellTemplateEditor.ApplyPlan(template, plan);
 *
 * NOTE:
 * The server (ClassicSpellTemplates) and the card overlay tool
 * (tools/overlay-wad, which compiles this file in) share this code, so the
 * card the client draws carries exactly what combat uses. It depends only
 * on the generated Imcodec types and Imlight.Classic.
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
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Spells;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// Reads and edits generated spell templates for the classic spell values.
/// </summary>
public static class SpellTemplateEditor {

    /// <summary>
    /// The template as the classic planner sees it.
    /// </summary>
    /// <param name="template">The template.</param>
    /// <param name="path">Its Root.wad path.</param>
    /// <returns>The shape.</returns>
    public static SpellTemplateShape ShapeOf(SpellTemplate template, string path) {
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
            Effects = [.. effects.Select(effect => SpellTemplateMapping.NodeOf(effect, FieldsOf, ChildrenOf))],
        };
    }

    /// <summary>
    /// The child list of a random, per-pip or effect-list effect, or a condition's effects.
    /// </summary>
    public static IReadOnlyList<SpellEffect?>? ChildrenOf(SpellEffect effect)
        => effect switch {
            RandomSpellEffect random => random.m_effectList,
            VariableSpellEffect variable => variable.m_effectList,
            EffectListSpellEffect list => list.m_effectList,
            ConditionalSpellEffect conditional => conditional.m_elements?.Select(element => element?.m_pEffect).ToList(),
            _ => null,
        };

    /// <summary>
    /// Writes <paramref name="plan"/> into <paramref name="template"/>.
    /// </summary>
    /// <param name="template">The template; changed in place.</param>
    /// <param name="plan">The plan.</param>
    public static void ApplyPlan(SpellTemplate template, SpellOverridePlan plan) {
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

        if (plan.Structure is { } structure) {
            // Every entry is its own copy, so two hits made from one template effect never share an object.
            var original = template.m_effects ?? [];
            template.m_effects = [.. structure
                .Select(address => SpellTemplateMapping.EffectAt(original, address, ChildrenOf))
                .Where(effect => effect is not null)
                .Select(effect => Copy(effect!))];
        }

        foreach (var change in plan.EffectChanges) {
            if (SpellTemplateMapping.EffectAt(template.m_effects, change.Address, ChildrenOf) is not { } effect) {
                continue;
            }

            if (change.EffectTypeName is { } type) {
                effect.m_effectType = Enum.Parse<kSpellEffects>(type);
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

            if (change.Target is { } target && SpellTemplateMapping.EffectTargetName(target) is { } targetName) {
                effect.m_effectTarget = Enum.Parse<kEffectTarget>(targetName);
            }

            if (change.DamageType is { } damageType) {
                effect.m_sDamageType = damageType;
            }
        }
    }

    /// <summary>
    /// A deep copy of an effect: its own object, and its own copies of a roll's or per-pip list's children.
    /// </summary>
    /// <param name="effect">The effect.</param>
    /// <returns>The copy, of the same class.</returns>
    public static SpellEffect Copy(SpellEffect effect) {
        var copy = effect with { };
        switch (copy) {
            case RandomSpellEffect random when random.m_effectList is { } list:
                random.m_effectList = [.. list.Select(child => child is null ? null! : Copy(child))];
                break;
            case VariableSpellEffect variable when variable.m_effectList is { } list:
                variable.m_effectList = [.. list.Select(child => child is null ? null! : Copy(child))];
                break;
        }

        return copy;
    }

    private static ClientEffectFields FieldsOf(SpellEffect effect)
        => new(TypeChainOf(effect.GetType()), effect.m_effectType.ToString(), effect.m_effectTarget.ToString(), effect.m_sDamageType,
            effect.m_effectParam, effect.m_numRounds, effect.m_pipNum, effect.m_healModifier);

    private static List<string> TypeChainOf(System.Type? type) {
        var chain = new List<string>();
        for (; type is not null && type != typeof(object); type = type.BaseType) {
            chain.Add(type.Name);
        }

        return chain;
    }

}
