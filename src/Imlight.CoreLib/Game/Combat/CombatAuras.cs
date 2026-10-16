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
 * STAR AURAS (CELESTIA, OCTOBER 2010)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the Star school's auras, as the archived October 2010 notes give
 * them: "Auras are cast as normal spells and can only have a target of
 * Self", "can't be stacked, broken, or dispelled but they will disappear
 * over time", "Casting a subsequent Aura spell will replace the previous
 * one". An aura lives in the participant's own aura list
 * (CombatParticipant.m_auraEffects, the client's field), never among the
 * charms and wards, so nothing that removes, steals or spends those touches
 * it. It is not used up: it changes every hit while it lasts.
 *
 * USAGE EXAMPLE:
 * if (CombatAuras.IsAura(template)) CombatAuras.Apply(caster, template, spellTemplateId);
 * damage *= 1 + CombatAuras.OutgoingDamage(caster, school);
 *
 * NOTE:
 * Fortification -15% incoming, Amplification +15% outgoing, Precision +10
 * accuracy points, Conviction 90% stun resist and 20% critical block,
 * Empowerment +1 pip per rank 4+ spell used on the wizard, Vengeance +20%
 * critical chance (all from the notes). Rounds (4) are the card's. An aura
 * cast this round starts counting at the end of the next round, so it covers
 * the caster's next four turns. Conviction's block and Vengeance's critical
 * chance only act when critical and block are on: HookCriticalModel feeds
 * them to work package B's CombatCriticals.AuraCritBoostPoints and
 * AuraBlockBoostPoints.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/16/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>CLASSIC: Star auras (Celestia).</summary>
internal static class CombatAuras {

    private static readonly IReadOnlyList<SpellEffect> s_none = [];

    /// <summary>
    /// Feeds Vengeance and Conviction to work package B's critical model (CombatCriticals hooks, percent points, read every
    /// roll and never consumed). Runs once when the assembly loads; outside Celestia no aura exists, so both stay 0.
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void HookCriticalModel() {
        CombatCriticals.AuraCritBoostPoints = static caster => (int) Math.Round(CritBoostPercent(caster) * 100f);
        CombatCriticals.AuraBlockBoostPoints = static target => (int) Math.Round(CritBlockPercent(target) * 100f);
    }

    /// <summary>True for a Star aura card while a Celestia profile is active.</summary>
    internal static bool IsAura(SpellTemplate? template)
        => ClassicCelestiaRules.Active && template is { m_effects.Count: > 0 }
            && string.Equals(template.m_sTypeName, "Aura", StringComparison.OrdinalIgnoreCase)
            && template.m_effects.All(effect => effect is not null && effect.GetType() == typeof(SpellEffect)
                && effect.m_effectTarget is kEffectTarget.kSelf);

    /// <summary>
    /// Casts <paramref name="template"/> as the caster's aura: replaces any aura it has, for the card's rounds.
    /// </summary>
    internal static void Apply(CombatDuelSubCircle caster, SpellTemplate template, uint spellTemplateId) {
        if (caster?.CombatParticipant is not { } participant) {
            return;
        }

        participant.m_auraEffects = [.. template.m_effects
            .Where(effect => effect.m_effectType != kSpellEffects.kModifyOutgoingArmorPiercing) // armor piercing is later
            .Select(effect => {
                var copy = CombatEffectApplicator.CopyOf(effect);
                copy.m_spellTemplateID = spellTemplateId;
                return copy;
            })];
        participant.m_nAuraTurnLength = Math.Max(1, template.m_effects.Max(effect => effect.m_numRounds));
        participant.m_needToCleanAuras = false;
        caster.AuraCastThisRound = true;
    }

    /// <summary>The caster's aura effects while one lasts.</summary>
    internal static IReadOnlyList<SpellEffect> Of(CombatDuelSubCircle? circle)
        => circle?.CombatParticipant is { m_nAuraTurnLength: > 0, m_auraEffects: { } effects } ? effects : s_none;

    /// <summary>True while the circle has an aura.</summary>
    internal static bool HasAura(CombatDuelSubCircle? circle) => Of(circle).Count > 0;

    /// <summary>The aura's percentage of one kind for a school (an aura names All), as a fraction.</summary>
    internal static float Percent(CombatDuelSubCircle? circle, kSpellEffects kind, string? school = null)
        => Of(circle).Where(effect => effect.m_effectType == kind && Fits(effect, school)).Sum(effect => effect.m_effectParam) / 100f;

    /// <summary>Amplification: the share by which the circle's outgoing damage of <paramref name="school"/> rises.</summary>
    internal static float OutgoingDamage(CombatDuelSubCircle? caster, string? school)
        => Percent(caster, kSpellEffects.kModifyOutgoingDamage, school);

    /// <summary>Fortification: the (negative) share by which damage of <paramref name="school"/> to the circle changes.</summary>
    internal static float IncomingDamage(CombatDuelSubCircle? target, string? school)
        => Percent(target, kSpellEffects.kModifyIncomingDamage, school);

    /// <summary>Precision: accuracy points the aura adds to every spell the circle casts.</summary>
    internal static int AccuracyPoints(CombatDuelSubCircle? caster, string? school)
        => Of(caster).Where(effect => effect.m_effectType == kSpellEffects.kModifyAccuracy && Fits(effect, school))
            .Sum(effect => effect.m_effectParam);

    /// <summary>
    /// Conviction (and a Moon rework form's stun resistance): the chance, 0 to 100, that a stun on the circle fails.
    /// </summary>
    internal static int StunResistPercent(CombatDuelSubCircle? target)
        => Math.Clamp(Of(target).Where(effect => effect.m_effectType == kSpellEffects.kStunResist).Sum(effect => effect.m_effectParam)
            + (target?.Polymorph?.StunResistPercent ?? 0), 0, 100);

    /// <summary>Vengeance: critical chance the aura adds (a fraction). Read by the critical model (work package B seam).</summary>
    internal static float CritBoostPercent(CombatDuelSubCircle? caster) => Percent(caster, kSpellEffects.kCritBoost);

    /// <summary>Conviction: block chance against critical hits the aura adds (a fraction). Work package B seam.</summary>
    internal static float CritBlockPercent(CombatDuelSubCircle? target) => Percent(target, kSpellEffects.kCritBlock);

    /// <summary>Empowerment: pips the circle gains each time a rank 4 or higher spell is used on it.</summary>
    internal static int PipsPerBigSpell(CombatDuelSubCircle? target)
        => Of(target).Where(effect => effect.m_effectType == kSpellEffects.kPipConversion).Sum(effect => effect.m_effectParam);

    /// <summary>
    /// Empowerment: each listed target holding it gains its pips when <paramref name="rank"/> is 4 or more (the pips the spell
    /// cost; an X card counts the pips it spent). Gains respect the 7-pip ring.
    /// </summary>
    internal static void OnSpellUsed(IEnumerable<CombatDuelSubCircle> targets, int rank) {
        if (rank < 4) {
            return;
        }

        foreach (var target in targets.Distinct()) {
            var pips = PipsPerBigSpell(target);
            if (pips <= 0 || !target.IsAlive || target.CombatParticipant?.m_pipCount is not { } count) {
                continue;
            }

            var room = CombatDuelSubCircle.MAX_PIP_COUNT - count.m_genericPips - count.m_powerPips;
            count.m_genericPips = (byte) (count.m_genericPips + Math.Clamp(pips, 0, Math.Max(room, 0)));
        }
    }

    /// <summary>
    /// The end of a round: an aura cast before this round counts one round down and is gone at 0. An aura cast this round
    /// starts counting next round.
    /// </summary>
    internal static void EndOfRound(CombatDuelSubCircle circle) {
        if (circle?.CombatParticipant is not { } participant) {
            return;
        }

        if (circle.AuraCastThisRound) {
            circle.AuraCastThisRound = false;
            return;
        }

        if (participant.m_nAuraTurnLength <= 0) {
            return;
        }

        participant.m_nAuraTurnLength--;
        if (participant.m_nAuraTurnLength <= 0) {
            Clear(circle);
        }
    }

    /// <summary>Removes the circle's aura (timed out, or the wizard was revived without buffs).</summary>
    internal static void Clear(CombatDuelSubCircle circle) {
        if (circle?.CombatParticipant is not { } participant) {
            return;
        }

        participant.m_auraEffects?.Clear();
        participant.m_nAuraTurnLength = 0;
        participant.m_needToCleanAuras = true;
        circle.AuraCastThisRound = false;
    }

    private static bool Fits(SpellEffect effect, string? school)
        => school is null || string.IsNullOrEmpty(effect.m_sDamageType)
            || string.Equals(effect.m_sDamageType, "All", StringComparison.OrdinalIgnoreCase)
            || string.Equals(effect.m_sDamageType, school, StringComparison.OrdinalIgnoreCase);
}
