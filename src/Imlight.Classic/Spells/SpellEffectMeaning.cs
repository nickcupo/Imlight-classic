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
 * What a classic-data effect means in the r806919 client's terms: which
 * kSpellEffects members can carry it, which side it lands on, and which
 * kEffectTarget member a rebuilt effect gets. Shared by the mechanics audit
 * and the structural planner so both read a record the same way.
 *
 * USAGE EXAMPLE:
 * var types = SpellEffectMeaning.ClientTypes(effect);   // ["kModifyAccuracy"] for an accuracy charm
 * var target = SpellEffectMeaning.TargetFor(effect);    // TemplateTarget.EnemySingle for a single-target hit
 *
 * NOTE:
 * A charm or global states its meaning in its notes (accuracy, heal, power
 * pip chance); anything else is a damage change. The schema has no field
 * for it, so the notes are the record's own words for the distinction.
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

namespace Imlight.Classic.Spells;

/// <summary>
/// Reads a classic effect in the client's effect vocabulary.
/// </summary>
public static class SpellEffectMeaning {

    private static readonly FrozenDictionary<SpellEffectKind, ImmutableArray<string>> s_types = new Dictionary<SpellEffectKind, ImmutableArray<string>> {
        [SpellEffectKind.Damage] = ["kDamage", "kMaxHealthDamage"],
        [SpellEffectKind.Dot] = ["kDamageOverTime"],
        [SpellEffectKind.Heal] = ["kHeal"],
        [SpellEffectKind.Hot] = ["kHealOverTime"],
        [SpellEffectKind.Steal] = ["kStealHealth", "kStealCharm", "kStealWard"],
        [SpellEffectKind.Pip] = ["kModifyPips", "kModifyPowerPips"],
        [SpellEffectKind.Minion] = ["kSummonCreature"],
        [SpellEffectKind.Beguile] = ["kMindControl"],
        [SpellEffectKind.Stun] = ["kStun"],
        [SpellEffectKind.Threat] = ["kTaunt", "kPacify"],
        [SpellEffectKind.Blade] = ["kModifyOutgoingDamage"],
        [SpellEffectKind.Charm] = ["kModifyOutgoingDamage", "kModifyAccuracy", "kModifyOutgoingHeal"],
        [SpellEffectKind.Enchant] = ["kModifyCardAccuracy", "kModifyCardDamage"],
        [SpellEffectKind.Trap] = ["kModifyIncomingDamage"],
        [SpellEffectKind.Shield] = ["kModifyIncomingDamage"],
        [SpellEffectKind.Ward] = ["kAbsorbDamage", "kStunBlock"],
        [SpellEffectKind.Prism] = ["kModifyIncomingDamageType"],
        [SpellEffectKind.Global] = [
            "kModifyOutgoingDamage", "kModifyOutgoingHeal", "kModifyIncomingHeal", "kModifyIncomingDamage", "kModifyAccuracy",
            "kModifyPowerPipChance"
        ],
        [SpellEffectKind.Mutate] = ["kModifyCardMutation"],
        [SpellEffectKind.Dispel] = ["kDispel"],
        [SpellEffectKind.RemoveCharm] = ["kRemoveCharm"],
        [SpellEffectKind.RemoveWard] = ["kRemoveWard"],
        [SpellEffectKind.Reshuffle] = ["kReshuffle"],
        [SpellEffectKind.Cloak] = ["kModifyCardCloak"],
        [SpellEffectKind.StunResist] = ["kStunResist"],
        [SpellEffectKind.CriticalBlock] = ["kCritBlock"],
    }.ToFrozenDictionary();

    /// <summary>
    /// Kinds whose amount or percentage the template stores in the effect parameter.
    /// </summary>
    public static FrozenSet<SpellEffectKind> ValueKinds { get; } = FrozenSet.Create(
        SpellEffectKind.Damage, SpellEffectKind.Dot, SpellEffectKind.Heal, SpellEffectKind.Hot, SpellEffectKind.Steal,
        SpellEffectKind.Pip, SpellEffectKind.Ward, SpellEffectKind.Blade, SpellEffectKind.Charm, SpellEffectKind.Trap,
        SpellEffectKind.Shield, SpellEffectKind.Global, SpellEffectKind.Enchant, SpellEffectKind.StunResist, SpellEffectKind.CriticalBlock);

    /// <summary>
    /// The kSpellEffects members that can carry <paramref name="effect"/>, the one a rebuilt effect gets first.
    /// </summary>
    /// <param name="effect">A classic effect.</param>
    /// <returns>The member names; never empty.</returns>
    public static ImmutableArray<string> ClientTypes(SpellEffectValues effect) {
        var notes = effect.Notes ?? "";
        bool Says(string word) => notes.Contains(word, StringComparison.OrdinalIgnoreCase);

        switch (effect.Kind) {
            case SpellEffectKind.Charm when Says("accuracy"):
                return ["kModifyAccuracy"];
            case SpellEffectKind.Charm when Says("heal"):
                return ["kModifyOutgoingHeal"];
            case SpellEffectKind.Charm:
                return ["kModifyOutgoingDamage"];
            case SpellEffectKind.Global when Says("power pip"):
                return ["kModifyPowerPipChance"];
            case SpellEffectKind.Global when Says("accuracy"):
                return ["kModifyAccuracy"];
            case SpellEffectKind.Global when Says("heal"):
                return ["kModifyOutgoingHeal", "kModifyIncomingHeal"];
            case SpellEffectKind.Global:
                return ["kModifyOutgoingDamage", "kModifyIncomingDamage"];
            case SpellEffectKind.Enchant when effect.HasAmount:
                return ["kModifyCardDamage"];
            case SpellEffectKind.Enchant:
                return ["kModifyCardAccuracy"];
            case SpellEffectKind.Ward when !effect.HasAmount && Says("stun"):
                return ["kStunBlock"];
            case SpellEffectKind.Ward:
                return ["kAbsorbDamage"];
            case SpellEffectKind.Steal when !effect.HasAmount && Says("ward"):
                return ["kStealWard"];
            case SpellEffectKind.Steal when !effect.HasAmount:
                return ["kStealCharm"];
            case SpellEffectKind.Steal:
                return ["kStealHealth"];
            case SpellEffectKind.Threat:
                // A threat card either draws enemies onto the caster or makes a wizard a less likely pick.
                return Says("lower") ? ["kPacify"] : ["kTaunt"];
            case SpellEffectKind.Damage:
                // A share of max health is not a flat amount; it only carries a record's hit until it is retyped.
                return ["kDamage"];
            case SpellEffectKind.Pip:
                return ["kModifyPips"];
            default:
                return s_types[effect.Kind];
        }
    }

    /// <summary>
    /// True when <paramref name="effectType"/> can carry <paramref name="kind"/> in some record.
    /// </summary>
    public static bool CanCarry(SpellEffectKind kind, string effectType)
        => s_types[kind].Contains(effectType, StringComparer.Ordinal);

    /// <summary>
    /// Every kSpellEffects member a classic effect can mean.
    /// </summary>
    public static FrozenSet<string> AllClientTypes { get; } = FrozenSet.Create(StringComparer.Ordinal,
        [.. SelectMany(s_types.Values)]);

    private static IEnumerable<string> SelectMany(IEnumerable<ImmutableArray<string>> lists) {
        foreach (var list in lists) {
            foreach (var item in list) {
                yield return item;
            }
        }
    }

    /// <summary>
    /// True when a single-target <paramref name="effect"/> lands on an enemy rather than on the caster's side.
    /// </summary>
    /// <param name="effect">A classic effect.</param>
    /// <returns>True for hostile effects.</returns>
    public static bool IsHostile(SpellEffectValues effect) {
        var notes = effect.Notes ?? "";

        // Threat cards cast on an enemy raise the caster's threat with it; the others protect the wizard they are cast on.
        if (effect.Kind == SpellEffectKind.Threat) {
            return effect.Targets == SpellTargets.AllEnemies || notes.Contains("cast on an enemy", StringComparison.OrdinalIgnoreCase);
        }

        if (notes.Contains("friendly", StringComparison.OrdinalIgnoreCase) || notes.Contains("ally", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if (notes.Contains("enemy", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return effect.Kind switch {
            SpellEffectKind.Damage or SpellEffectKind.Dot or SpellEffectKind.Steal or SpellEffectKind.Beguile or SpellEffectKind.Stun
                or SpellEffectKind.Trap or SpellEffectKind.Prism or SpellEffectKind.Dispel or SpellEffectKind.RemoveCharm => true,
            SpellEffectKind.Charm => effect.Percent is < 0,
            SpellEffectKind.RemoveWard => true,
            _ => false,
        };
    }

    /// <summary>
    /// The target a rebuilt effect gets.
    /// </summary>
    /// <param name="effect">A classic effect.</param>
    /// <returns>The target; <see cref="TemplateTarget.Global"/> for globals.</returns>
    public static TemplateTarget TargetFor(SpellEffectValues effect)
        => effect.Kind == SpellEffectKind.Global
            ? TemplateTarget.Global
            : effect.Targets switch {
                SpellTargets.Self => TemplateTarget.Self,
                SpellTargets.AllEnemies => TemplateTarget.EnemyTeam,
                SpellTargets.AllAllies => TemplateTarget.FriendlyTeam,
                SpellTargets.Single => IsHostile(effect) ? TemplateTarget.EnemySingle : TemplateTarget.FriendlySingle,
                _ => IsHostile(effect) ? TemplateTarget.EnemySingle : TemplateTarget.Self,
            };

    /// <summary>
    /// True when a template effect of <paramref name="effectType"/> and <paramref name="damageType"/> carries the school of
    /// <paramref name="effect"/>.
    /// </summary>
    /// <param name="effect">A classic effect.</param>
    /// <param name="cardSchool">The card's school, or null when unknown.</param>
    /// <param name="effectType">The template effect's kSpellEffects member.</param>
    /// <param name="damageType">The template effect's school, such as <c>Fire</c> or <c>All</c>.</param>
    /// <returns>True when the schools agree.</returns>
    /// <remarks>
    /// Heals, pips and enchantments are filed under whatever school the client likes, and so are healing and power pip
    /// bubbles. A charm, ward or steal that works on any school often names the card's own school in its record, so the
    /// template's <c>All</c> fits it. A health cost the caster pays (Sacrifice) has no school to compare.
    /// </remarks>
    public static bool SchoolFits(SpellEffectValues effect, string? cardSchool, string effectType, string damageType) {
        if (!ValueKinds.Contains(effect.Kind)
            || effect.Kind is SpellEffectKind.Heal or SpellEffectKind.Hot or SpellEffectKind.Pip or SpellEffectKind.Enchant
            || effectType is "kModifyOutgoingHeal" or "kModifyIncomingHeal" or "kModifyPowerPipChance") {
            return true;
        }

        if (string.Equals(damageType, DamageTypeOf(effect.School), StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (effect.Kind is SpellEffectKind.Charm or SpellEffectKind.Ward or SpellEffectKind.Steal
            && string.Equals(damageType, "All", StringComparison.OrdinalIgnoreCase)
            && string.Equals(effect.School, cardSchool, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return effect is { Kind: SpellEffectKind.Damage, Targets: SpellTargets.Self }
            && (effect.Notes ?? "").Contains("pays", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The client's damage type name for a classic school, such as <c>Fire</c> or <c>All</c>.
    /// </summary>
    public static string DamageTypeOf(string school)
        => school.Length == 0 ? "" : char.ToUpperInvariant(school[0]) + school[1..].ToLowerInvariant();

}
