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
 * Maps a client spell template's effects onto a SpellTemplateShape and
 * back, by the client's own type and enum member names, so the mapping is
 * testable without the generated client types.
 *
 * USAGE EXAMPLE:
 * var node = SpellTemplateMapping.NodeOf(effect, ReadFields, ChildrenOf);    // the server's adapter supplies both delegates
 * var target = SpellTemplateMapping.EffectTargetName(TemplateTarget.EnemyTeam); // "kEnemyTeam"
 *
 * NOTE:
 * The names are kSpellEffects and kEffectTarget members and effect class
 * names of the r806919 client. The server checks at startup that every
 * name listed here is still a member of its generated enums.
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
/// The fields of one client spell effect, as the server reads them from its generated type.
/// </summary>
/// <param name="TypeChain">The effect's class name and its base class names, most derived first.</param>
/// <param name="EffectType">The kSpellEffects member name, such as <c>kDamage</c>.</param>
/// <param name="Target">The kEffectTarget member name, such as <c>kEnemySingle</c>.</param>
/// <param name="DamageType">The school the effect deals or guards against.</param>
/// <param name="Param">The effect parameter.</param>
/// <param name="Rounds">The duration in rounds.</param>
/// <param name="PipNumber">The pip tier of a per-pip child.</param>
/// <param name="HealModifier">The heal share of a drain.</param>
public readonly record struct ClientEffectFields(IReadOnlyList<string> TypeChain, string? EffectType, string? Target, string? DamageType,
                                                 int Param, int Rounds, int PipNumber, float HealModifier);

/// <summary>
/// Name-based mapping between client spell effects and <see cref="TemplateEffectNode"/>.
/// </summary>
public static class SpellTemplateMapping {

    private const string PlainEffectClass = "SpellEffect";
    private const string RandomEffectClass = "RandomSpellEffect";
    private const string PerPipEffectClass = "VariableSpellEffect";

    private static readonly FrozenDictionary<string, TemplateEffectKind> s_kinds = new Dictionary<string, TemplateEffectKind> {
        ["kDamage"] = TemplateEffectKind.Damage,
        ["kDamageOverTime"] = TemplateEffectKind.DamageOverTime,
        ["kHeal"] = TemplateEffectKind.Heal,
        ["kHealOverTime"] = TemplateEffectKind.HealOverTime,
        ["kStealHealth"] = TemplateEffectKind.StealHealth,
        ["kModifyOutgoingDamage"] = TemplateEffectKind.ModifyOutgoingDamage,
        ["kModifyIncomingDamage"] = TemplateEffectKind.ModifyIncomingDamage,
        ["kModifyAccuracy"] = TemplateEffectKind.ModifyAccuracy,
        ["kModifyOutgoingHeal"] = TemplateEffectKind.ModifyOutgoingHeal,
        ["kModifyIncomingHeal"] = TemplateEffectKind.ModifyIncomingHeal,
        ["kAbsorbDamage"] = TemplateEffectKind.AbsorbDamage,
        ["kModifyPips"] = TemplateEffectKind.ModifyPips,
        ["kMaxHealthDamage"] = TemplateEffectKind.MaxHealthDamage,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, TemplateTarget> s_targets = new Dictionary<string, TemplateTarget> {
        ["kEnemySingle"] = TemplateTarget.EnemySingle,
        ["kFriendlySingle"] = TemplateTarget.FriendlySingle,
        ["kMinion"] = TemplateTarget.MinionSingle,
        ["kFriendlyMinion"] = TemplateTarget.MinionSingle,
        ["kEnemyMinion"] = TemplateTarget.MinionSingle,
        ["kCasterMinion"] = TemplateTarget.MinionSingle,
        ["kTargetMinion"] = TemplateTarget.MinionSingle,
        ["kSelf"] = TemplateTarget.Self,
        ["kEnemyTeam"] = TemplateTarget.EnemyTeam,
        ["kEnemyTeamAllAtOnce"] = TemplateTarget.EnemyTeam,
        ["kFriendlyTeam"] = TemplateTarget.FriendlyTeam,
        ["kFriendlyTeamAllAtOnce"] = TemplateTarget.FriendlyTeam,
        ["kGlobal"] = TemplateTarget.Global,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<TemplateTarget, string> s_targetNames = new Dictionary<TemplateTarget, string> {
        [TemplateTarget.EnemySingle] = "kEnemySingle",
        [TemplateTarget.FriendlySingle] = "kFriendlySingle",
        [TemplateTarget.Self] = "kSelf",
        [TemplateTarget.EnemyTeam] = "kEnemyTeam",
        [TemplateTarget.FriendlyTeam] = "kFriendlyTeam",
        [TemplateTarget.Global] = "kGlobal",
    }.ToFrozenDictionary();

    /// <summary>
    /// Every kSpellEffects member name the mapping reads or writes.
    /// </summary>
    public static ImmutableArray<string> EffectTypeNames { get; } = [.. s_kinds.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Every kEffectTarget member name the mapping reads or writes.
    /// </summary>
    public static ImmutableArray<string> TargetNames { get; } = [.. s_targets.Keys.Concat(s_targetNames.Values).Distinct().Order(StringComparer.Ordinal)];

    /// <summary>
    /// The effect kind of a kSpellEffects member.
    /// </summary>
    /// <param name="effectType">The member name, such as <c>kDamage</c>.</param>
    /// <returns>The kind; <see cref="TemplateEffectKind.Other"/> for anything the classic values do not reach.</returns>
    public static TemplateEffectKind KindOf(string? effectType)
        => effectType is not null && s_kinds.TryGetValue(effectType, out var kind) ? kind : TemplateEffectKind.Other;

    /// <summary>
    /// The kSpellEffects member of an effect kind, for a kind the plan rewrites.
    /// </summary>
    /// <param name="kind">Any kind but <see cref="TemplateEffectKind.Other"/>.</param>
    /// <returns>The member name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is <see cref="TemplateEffectKind.Other"/>.</exception>
    public static string EffectTypeName(TemplateEffectKind kind)
        => s_kinds.FirstOrDefault(pair => pair.Value == kind).Key
            ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "The kind has no client effect type.");

    /// <summary>
    /// Who a kEffectTarget member lands on.
    /// </summary>
    /// <param name="target">The member name, such as <c>kEnemyTeamAllAtOnce</c>.</param>
    /// <returns>The target; minion targets count as single targets.</returns>
    public static TemplateTarget TargetOf(string? target)
        => target is not null && s_targets.TryGetValue(target, out var value) ? value : TemplateTarget.Other;

    /// <summary>
    /// The kEffectTarget member a plan writes for a target.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <returns>The member name, or null for <see cref="TemplateTarget.MinionSingle"/> and <see cref="TemplateTarget.Other"/>.</returns>
    public static string? EffectTargetName(TemplateTarget target)
        => s_targetNames.GetValueOrDefault(target);

    /// <summary>
    /// How an effect holds its values, from its class and base classes.
    /// </summary>
    /// <param name="typeChain">The class name and its base class names, most derived first.</param>
    /// <returns>The composition; a random effect's subclasses count as random.</returns>
    public static TemplateComposition CompositionOf(IReadOnlyList<string> typeChain) {
        if (typeChain.Count > 0 && string.Equals(typeChain[0], PlainEffectClass, StringComparison.Ordinal)) {
            return TemplateComposition.Plain;
        }

        if (typeChain.Contains(RandomEffectClass, StringComparer.Ordinal)) {
            return TemplateComposition.Random;
        }

        return typeChain.Contains(PerPipEffectClass, StringComparer.Ordinal) ? TemplateComposition.PerPip : TemplateComposition.Other;
    }

    /// <summary>
    /// Builds the node of one template effect, with the children of random and per-pip effects.
    /// </summary>
    /// <typeparam name="TEffect">The server's effect type.</typeparam>
    /// <param name="effect">The effect; null gives an unmatchable node.</param>
    /// <param name="read">Reads the effect's fields.</param>
    /// <param name="childrenOf">The effect's child list, for random and per-pip effects.</param>
    /// <returns>The node.</returns>
    public static TemplateEffectNode NodeOf<TEffect>(TEffect? effect, Func<TEffect, ClientEffectFields> read,
                                                     Func<TEffect, IReadOnlyList<TEffect?>?> childrenOf) where TEffect : class {
        if (effect is null) {
            return new TemplateEffectNode { Composition = TemplateComposition.Other };
        }

        var fields = read(effect);
        var composition = CompositionOf(fields.TypeChain);
        // Containers of other kinds (effect lists, conditions) list their children too, so their types can be read; the
        // classic values only write into rolls and per-pip lists.
        var children = ImmutableArray.CreateRange((childrenOf(effect) ?? []).Select(child => NodeOf(child, read, childrenOf)));

        return new TemplateEffectNode {
            Composition = composition,
            Kind = KindOf(fields.EffectType),
            Target = TargetOf(fields.Target),
            DamageType = fields.DamageType ?? "",
            Param = fields.Param,
            Rounds = fields.Rounds,
            PipNumber = fields.PipNumber,
            HealModifier = fields.HealModifier,
            Children = children,
            EffectType = fields.EffectType ?? "",
            TargetName = fields.Target ?? "",
            ClassName = fields.TypeChain.Count > 0 ? fields.TypeChain[0] : "",
        };
    }

    /// <summary>
    /// The template effect an address names.
    /// </summary>
    /// <typeparam name="TEffect">The server's effect type.</typeparam>
    /// <param name="effects">The template's effect list.</param>
    /// <param name="address">The address.</param>
    /// <param name="childrenOf">The effect's child list, for random and per-pip effects.</param>
    /// <returns>The effect, or null when the address is out of range.</returns>
    public static TEffect? EffectAt<TEffect>(IReadOnlyList<TEffect?>? effects, EffectAddress address,
                                             Func<TEffect, IReadOnlyList<TEffect?>?> childrenOf) where TEffect : class {
        if (effects is null || address.Index < 0 || address.Index >= effects.Count || effects[address.Index] is not { } effect) {
            return null;
        }

        if (address.Child < 0) {
            return effect;
        }

        var children = childrenOf(effect);

        return children is not null && address.Child < children.Count ? children[address.Child] : null;
    }

}
