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
 * CHARACTER EFFECT MANAGEMENT SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Provides helper methods for managing game effects that modify character statistics,
 * handling the application and removal of various effect types to wizard characters.
 * 
 * USAGE EXAMPLE:
 * var addedEffects = CharacterEffectHelper.AddEffectsToWizard(wizard, itemTemplate);
 * var removedEffects = CharacterEffectHelper.RemoveEffectsFromWizard(wizard, itemTemplate);
 * 
 * NOTE:
 * Uses System.Text.RegularExpressions for parsing effect names to determine affected
 * magic schools. Effect changes are applied directly to character statistics objects.
 *
 * TODO:
 * 
 * Created by: Joji, Jooty
 * Version: KALI 1.0
 * Last Updated: 08/14/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.Classic;
using Imlight.CoreLib.Classic.Elixirs;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Effects;

/// <summary>
/// Helper class for adding and removing effects to/from a wizard character.
/// </summary>
/// <remarks>
/// Handles the application and removal of effects from equipment templates to character statistics,
/// including school-specific effects like damage bonuses, accuracy increases, and damage reduction.
/// Manages special effects like school masteries and temporary spell grants.
/// </remarks>
internal static class CharacterEffectHelper {

    // CLASSIC: local derived state only. Multiple arena pieces each reduce mana by100%;
    // they must keep the unreduced maximum for removing the final piece, rather than subtract/add1.
    private const string ManaPercentReduction = "CanonicalMaxManaPercentReduce";
    private static readonly ConditionalWeakTable<ServerWizGameStats, ManaAdjustment> s_manaAdjustments = new();
    private static readonly ConditionalWeakTable<ProvideSpellEffect, GrantedCards> s_grantedCards = new();
    private sealed class GrantedCards(Spell[] cards) { internal readonly Spell[] Cards = cards; }
    private sealed class ManaAdjustment(int maximum) {
        internal int UnreducedMaximum = Math.Max(0, maximum);
        internal readonly Dictionary<uint, float> Reductions = [];
    }

    // CLASSIC: only fields rebuilt by the equipment-effect loop are reset. Persistent resources,
    // wallets, ladders, preferences, level/base school stats and unrelated runtime fields stay intact.
    internal static void ResetRebuiltEquipmentEffects(ServerWizGameStats stats) {
        s_manaAdjustments.Remove(stats);
        stats.m_dmgBonusPercent = null; stats.m_dmgBonusFlat = null; stats.m_accBonusPercent = null;
        stats.m_dmgReducePercent = null; stats.m_dmgReduceFlat = null;
        stats.m_dmgBonusPercentAll = 0; stats.m_dmgBonusFlatAll = 0; stats.m_accBonusPercentAll = 0;
        stats.m_dmgReducePercentAll = 0; stats.m_dmgReduceFlatAll = 0;
        stats.m_healBonusPercentAll = 0; stats.m_healIncBonusPercentAll = 0; stats.m_powerPipBonusPercentAll = 0;
        stats.m_criticalHitRatingBySchool = null; stats.m_blockRatingBySchool = null;
        stats.m_criticalHitRatingAll = 0; stats.m_blockRatingAll = 0;
        stats.m_balanceMastery = 0; stats.m_deathMastery = 0; stats.m_fireMastery = 0; stats.m_iceMastery = 0;
        stats.m_lifeMastery = 0; stats.m_mythMastery = 0; stats.m_stormMastery = 0;
        stats.m_startingPips = 0; stats.m_startingPowerPips = 0;
    }

    // CLASSIC: the temporary book has no source-slot field. Retire exact owned references,
    // never the first same-name card from another item or an unrelated temporary grant.
    internal static void RetireProvidedSpellCards(Wizard wizard) {
        foreach (var effect in wizard.GameEffects.Snapshot().OfType<ProvideSpellEffect>())
            RemoveProvidedSpellCards(wizard, effect);
    }

    private static void RemoveProvidedSpellCards(Wizard wizard, ProvideSpellEffect effect) {
        if (!s_grantedCards.TryGetValue(effect, out var grant)) return;
        var book = wizard.SpellbookBehavior?.TemporarySpells;
        if (book is not null) {
            foreach (var card in grant.Cards) {
                var index = book.FindIndex(candidate => ReferenceEquals(candidate, card));
                if (index >= 0) book.RemoveAt(index);
            }
        }
        s_grantedCards.Remove(effect);
    }

    private static void ChangeMana(ServerWizGameStats stats, string effectName, WizStatisticEffect statistic, bool add) {
        if (effectName != ManaPercentReduction) {
            var delta = (int) statistic.m_manaBonus * (add ? 1 : -1);
            if (delta == 0) return;
            if (s_manaAdjustments.TryGetValue(stats, out var active)) {
                active.UnreducedMaximum = (int)Math.Clamp((long) active.UnreducedMaximum + delta, 0, int.MaxValue);
                ApplyManaMaximum(stats, active);
            }
            else stats.m_baseMana += delta; // Existing flat mana behavior remains unchanged.
            return;
        }
        var reduction = statistic.m_manaBonus;
        if (!float.IsFinite(reduction) || reduction < 0 || reduction > 1)
            throw new InvalidOperationException("Invalid canonical mana percentage reduction.");
        ManaAdjustment adjustment;
        if (add) {
            adjustment = s_manaAdjustments.GetValue(stats, value => new ManaAdjustment(value.m_baseMana));
            if (!adjustment.Reductions.TryAdd(statistic.m_itemSlotID, reduction)
                && adjustment.Reductions[statistic.m_itemSlotID] != reduction)
                throw new InvalidOperationException("Conflicting canonical mana reduction in an occupied equipment slot.");
        }
        else {
            if (!s_manaAdjustments.TryGetValue(stats, out adjustment)
                || !adjustment.Reductions.Remove(statistic.m_itemSlotID)) return;
        }
        ApplyManaMaximum(stats, adjustment);
        if (adjustment.Reductions.Count == 0) s_manaAdjustments.Remove(stats);
    }

    private static void ApplyManaMaximum(ServerWizGameStats stats, ManaAdjustment adjustment) {
        var reduction = Math.Clamp(adjustment.Reductions.Values.Sum(value => (double) value), 0, 1);
        stats.m_baseMana = (int)Math.Floor(adjustment.UnreducedMaximum * (1 - reduction));
        stats.m_currentMana = Math.Clamp(stats.m_currentMana, 0, stats.m_baseMana);
    }

    /// <summary>
    /// Adds effects to a wizard based on a given template.
    /// </summary>
    /// <param name="wizard">The wizard to add effects to.</param>
    /// <param name="template">The template containing the effects to be added.</param>
    /// <returns>A list of the added effects.</returns>
    internal static List<GameEffectBase> AddEffectsToWizard(Wizard wizard, WizItemTemplate template) {
        var addedEffects = new List<GameEffectBase>();
        var slotHash = ItemHelper.GetItemSlotHash(template);

        // Apply the effects from the template.
        foreach (var effectInfo in template.m_equipEffects) {
            // CLASSIC: a duplicate application cannot publish another percentage-reduction effect
            // or give its slot another ledger entry. A full rebuild first clears GameEffects.
            if (effectInfo.m_effectName == ManaPercentReduction
                && wizard.GameEffects.Find(effect => effect.m_itemSlotID == slotHash
                    && effect.m_effectNameID == StringHash.Compute(ManaPercentReduction)) is not null) continue;
            var gameEffect = GameEffectFactory.CreateEffectFromInfo(effectInfo, slotHash);
            if (gameEffect is null) {
                Logger.Warning("Could not create effect {0} from effect info.", Logger.Args(effectInfo.m_effectName));
                continue;
            }

            // CLASSIC: removing one effect must not let a later item reuse a still-live id.
            gameEffect.m_internalID = ClassicRuntime.IsActive
                ? ElixirRuntime.NextEffectId(wizard) : wizard.GameEffects.Count;
            var effect = AddGameEffectToStats(wizard.GameStats, effectInfo, slotHash);

            if (gameEffect is ProvideSpellEffect provideSpellEffect) {
                var spells = SpellFactory.CreateSpellsFromEffect(provideSpellEffect);

                if (spells is null) {
                    continue;
                }

                foreach (var spell in spells) {
                    wizard.AddTemporarySpell(spell);
                }
                s_grantedCards.Add(provideSpellEffect, new GrantedCards(spells)); // CLASSIC: local ownership, no protocol fields.
            }

            wizard.GameEffects.Add(gameEffect);
            addedEffects.Add(gameEffect);
        }

        return addedEffects;
    }

    /// <summary>
    /// Adds the effects from a WizItemTemplate to the ServerWizGameStats.
    /// </summary>
    /// <param name="stats">The ServerWizGameStats to add the effects to.</param>
    /// <param name="template">The WizItemTemplate containing the effects.</param>
    internal static void AddEffectsToGameStats(ServerWizGameStats stats, WizItemTemplate template) {
        foreach (var effectInfo in template.m_equipEffects) {
            AddGameEffectToStats(stats, effectInfo, effectInfo.m_effectName == ManaPercentReduction
                ? ItemHelper.GetItemSlotHash(template) : 0);
        }
    }

    /// <summary>
    /// Removes the effects from the game stats based on the given template.
    /// </summary>
    /// <param name="stats">The game stats to remove the effects from.</param>
    /// <param name="template">The template containing the effects to be removed.</param>
    internal static void RemoveEffectsFromGameStats(ServerWizGameStats stats, WizItemTemplate template) {
        foreach (var effectInfo in template.m_equipEffects) {
            RemoveGameEffectFromStats(stats, effectInfo, effectInfo.m_effectName == ManaPercentReduction
                ? ItemHelper.GetItemSlotHash(template) : 0);
        }
    }

    /// <summary>
    /// Removes the effects from a wizard based on a given WizItemTemplate.
    /// </summary>
    /// <param name="wizard">The wizard to remove effects from.</param>
    /// <param name="template">The WizItemTemplate containing the effects to be removed.</param>
    /// <returns>A list of GameEffectBase objects that were removed from the wizard.</returns>
    internal static List<GameEffectBase> RemoveEffectsFromWizard(Wizard wizard, WizItemTemplate template) {
        var removedEffects = new List<GameEffectBase>();
        var slotHash = ItemHelper.GetItemSlotHash(template);

        // Apply the effects from the template.
        foreach (var effectInfo in template.m_equipEffects) {
            // Find the effect in the player's list of effects.
            var nameHash = StringHash.Compute(effectInfo.m_effectName);
            var gameEffect = wizard.GameEffects.Find(e => e.m_effectNameID == nameHash && e.m_itemSlotID == slotHash);
            if (gameEffect is null) {
                // It's fine if the effect isn't found. Just continue.
                continue;
            }

            removedEffects.Add(gameEffect);
            wizard.GameEffects.Remove(gameEffect);
            RemoveGameEffectFromStats(wizard.GameStats, effectInfo, slotHash);

            if (gameEffect is ProvideSpellEffect provideSpellEffect) {
                RemoveProvidedSpellCards(wizard, provideSpellEffect); // CLASSIC: only this effect's admitted copies.
            }
        }

        return removedEffects;
    }

    /// <summary>
    /// Adds a game effect to the specified game statistics.
    /// </summary>
    /// <param name="stats">The game statistics to modify.</param>
    /// <param name="effectInfo">The effect information to apply.</param>
    /// <returns>The created game effect.</returns>
    internal static GameEffectBase AddGameEffectToStats(ServerWizGameStats stats, GameEffectInfo effectInfo, uint slot = 0) {
        var gameEffect = GameEffectFactory.CreateEffectFromInfo(effectInfo, slot);
        if (gameEffect is null) {
            Logger.Warning("Could not create effect {0} from effect info.", Logger.Args(effectInfo.m_effectName));
            return null;
        }

        if (gameEffect is WizStatisticEffect canonicalEffect) {
            var canonicalEffectName = CanonicalStatEffects.GetEffectTemplate(effectInfo.m_effectName).m_effectName;
            AddStatisticEffectToStats(stats, canonicalEffectName, canonicalEffect);
        }
        else if (gameEffect is StartingPipEffect pipEffect) {
            stats.m_startingPips += (byte) pipEffect.m_pipsGiven;
            stats.m_startingPowerPips += (byte) pipEffect.m_powerPipsGiven;
        }

        return gameEffect;
    }

    /// <summary>
    /// Adds a game effect to the specified game statistics.
    /// </summary>
    /// <param name="stats">The game statistics to modify.</param>
    /// <param name="effectInfo">The effect information to apply.</param>
    /// <returns>The created game effect.</returns>
    internal static GameEffectBase RemoveGameEffectFromStats(ServerWizGameStats stats, GameEffectInfo effectInfo, uint slot = 0) {
        var gameEffect = GameEffectFactory.CreateEffectFromInfo(effectInfo, slot);
        if (gameEffect is null) {
            Logger.Warning("Could not create effect {0} from effect info.", Logger.Args(effectInfo.m_effectName));
            return null;
        }

        if (gameEffect is WizStatisticEffect canonicalEffect) {
            var canonicalEffectName = CanonicalStatEffects.GetEffectTemplate(effectInfo.m_effectName).m_effectName;
            RemoveStatisticEffectFromStats(stats, canonicalEffectName, canonicalEffect);
        }
        else if (gameEffect is StartingPipEffect pipEffect) {
            stats.m_startingPips -= (byte) pipEffect.m_pipsGiven;
            stats.m_startingPowerPips -= (byte) pipEffect.m_powerPipsGiven;
        }

        return gameEffect;
    }

    /// <summary>
    /// Adds a game effect to the specified game statistics.
    /// </summary>
    /// <param name="stats">The game statistics to modify.</param>
    /// <param name="effectName">The category of the game effect.</param>
    /// <param name="statistic">The specific game effect to apply.</param>
    internal static void AddStatisticEffectToStats(ServerWizGameStats stats, string effectName, WizStatisticEffect statistic) {
        // Apply effects that don't require a school.
        stats.m_baseHitpoints += (int) statistic.m_hitPointBonus;
        ChangeMana(stats, effectName, statistic, add: true); // CLASSIC: percent reduction is not a flat gain.
        stats.m_powerPipBonusPercentAll += statistic.m_powerPipBonusPercent;
        stats.m_healBonusPercentAll += statistic.m_healBonusPercent;
        stats.m_healIncBonusPercentAll += statistic.m_healIncBonusPercent;

        if (effectName.Contains("Damage") && !effectName.Contains("Reduce")) {
            ApplyDamageIncrease(stats, effectName, statistic);
        }
        else if (effectName.Contains("Accuracy") && !effectName.Contains("Rating")) {
            ApplyAccuracyIncrease(stats, effectName, statistic);
        }
        else if (effectName.Contains("ReduceDamage")) {
            ApplyReduceDamageIncrease(stats, effectName, statistic);
        }
        else if (effectName.Contains("Mastery")) {
            ApplySchoolMastery(stats, effectName);
        }
        else if (effectName.Contains("CriticalHit")) {
            // Crit gear is school-based; route like damage, only falling back to the
            // universal bucket when the name carries no valid school.
            var school = effectName.Contains("All") ? null : ExtractSchoolName(effectName);
            if (school is not null && MagicSchools.GetMagicSchool(school) is not null) {
                ApplySchoolEffect(ref stats.m_criticalHitRatingBySchool, school, statistic.m_criticalHitRating);
            }
            else {
                stats.m_criticalHitRatingAll += statistic.m_criticalHitRating;
            }
        }
        else if (effectName.Contains("Block")) {
            var school = effectName.Contains("All") ? null : ExtractSchoolName(effectName);
            if (school is not null && MagicSchools.GetMagicSchool(school) is not null) {
                ApplySchoolEffect(ref stats.m_blockRatingBySchool, school, statistic.m_blockRating);
            }
            else {
                stats.m_blockRatingAll += statistic.m_blockRating;
            }
        }
    }

    /// <summary>
    /// Removes a game effect from the specified game statistics.
    /// </summary>
    /// <param name="stats">The game statistics to remove the effect from.</param>
    /// <param name="effectName">The name of the effect to remove.</param>
    /// <param name="statistic">The statistic effect to remove.</param>
    internal static void RemoveStatisticEffectFromStats(ServerWizGameStats stats, string effectName, WizStatisticEffect statistic) {
        // Remove effects that don't require a school.
        stats.m_baseHitpoints -= (int) statistic.m_hitPointBonus;
        ChangeMana(stats, effectName, statistic, add: false); // CLASSIC: restore the remaining derived maximum.
        stats.m_powerPipBonusPercentAll -= statistic.m_powerPipBonusPercent;
        stats.m_healBonusPercentAll -= statistic.m_healBonusPercent;
        stats.m_healIncBonusPercentAll -= statistic.m_healIncBonusPercent;

        if (effectName.Contains("Damage") && !effectName.Contains("Reduce")) {
            RemoveDamageIncrease(stats, effectName, statistic);
        }
        else if (effectName.Contains("Accuracy") && !effectName.Contains("Rating")) {
            RemoveAccuracyIncrease(stats, effectName, statistic);
        }
        else if (effectName.Contains("ReduceDamage")) {
            RemoveReduceDamageIncrease(stats, effectName, statistic);
        }
        else if (effectName.Contains("Mastery")) {
            RemoveSchoolMastery(stats, effectName);
        }
        else if (effectName.Contains("CriticalHit")) {
            var school = effectName.Contains("All") ? null : ExtractSchoolName(effectName);
            if (school is not null && MagicSchools.GetMagicSchool(school) is not null) {
                ApplySchoolEffect(ref stats.m_criticalHitRatingBySchool, school, -statistic.m_criticalHitRating);
            }
            else {
                stats.m_criticalHitRatingAll -= statistic.m_criticalHitRating;
            }
        }
        else if (effectName.Contains("Block")) {
            var school = effectName.Contains("All") ? null : ExtractSchoolName(effectName);
            if (school is not null && MagicSchools.GetMagicSchool(school) is not null) {
                ApplySchoolEffect(ref stats.m_blockRatingBySchool, school, -statistic.m_blockRating);
            }
            else {
                stats.m_blockRatingAll -= statistic.m_blockRating;
            }
        }
    }

    private static void ApplySchoolEffect(ref List<float> effectList, string schoolName, float value) {
        var schoolTemplate = MagicSchools.GetMagicSchool(schoolName);
        if (schoolTemplate is null) {
            Logger.Warning("Could not find magic school {0}.", Logger.Args(schoolName));
            return;
        }

        EnsureSchoolEffectCapacity(ref effectList); // CLASSIC: the highest zero-based index needs its own cell.
        var schoolIndex = schoolTemplate.m_schoolIndex;
        effectList[schoolIndex] += value;
    }

    // CLASSIC: sparse school indices still require max+1 cells. Keep unrelated and longer vectors intact.
    private static void EnsureSchoolEffectCapacity(ref List<float> effectList) {
        var requiredCount = checked((int) MagicSchools.GetMaxMagicSchoolIndex() + 1);
        effectList ??= [];
        if (effectList.Count < requiredCount)
            effectList.AddRange(Enumerable.Repeat(0f, requiredCount - effectList.Count));
    }

    private static void ApplySchoolMastery(ServerWizGameStats stats, string effectCategory) {
        switch (effectCategory) {
            case var category when category.Contains("Ice"):
                stats.m_iceMastery = 1;
                break;
            case var category when category.Contains("Life"):
                stats.m_lifeMastery = 1;
                break;
            case var category when category.Contains("Fire"):
                stats.m_fireMastery = 1;
                break;
            case var category when category.Contains("Myth"):
                stats.m_mythMastery = 1;
                break;
            case var category when category.Contains("Death"):
                stats.m_deathMastery = 1;
                break;
            case var category when category.Contains("Storm"):
                stats.m_stormMastery = 1;
                break;
            case var category when category.Contains("Balance"):
                stats.m_balanceMastery = 1;
                break;
        }
    }

    private static void ApplyDamageIncrease(ServerWizGameStats stats, string effectName, WizStatisticEffect statistic) {
        if (effectName.Contains("Flat")) {
            // Flat damage increase
            if (effectName.Contains("All")) {
                stats.m_dmgBonusFlatAll += statistic.m_damageBonusFlat;
            }
            else {
                // Extract the school name from the effect name.
                var schoolName = ExtractSchoolName(effectName);
                ApplySchoolEffect(ref stats.m_dmgBonusFlat, schoolName, statistic.m_damageBonusFlat);
            }
        }
        else {
            // Percent damage increase
            if (effectName.Contains("All")) {
                stats.m_dmgBonusPercentAll += statistic.m_damageBonusPercent;
            }
            else {
                // Extract the school name from the effect name.
                var schoolName = ExtractSchoolName(effectName);
                ApplySchoolEffect(ref stats.m_dmgBonusPercent, schoolName, statistic.m_damageBonusPercent);
            }
        }
    }

    private static void ApplyAccuracyIncrease(ServerWizGameStats stats, string effectName, WizStatisticEffect statistic) {
        // Percent accuracy increase
        if (effectName.Contains("All")) {
            stats.m_accBonusPercentAll += statistic.m_accuracyBonusPercent;
        }
        else {
            // Extract the school name from the effect name.
            var schoolName = ExtractSchoolName(effectName);
            ApplySchoolEffect(ref stats.m_accBonusPercent, schoolName, statistic.m_accuracyBonusPercent);
        }
    }

    private static void ApplyReduceDamageIncrease(ServerWizGameStats stats, string effectName, WizStatisticEffect statistic) {
        if (effectName.Contains("Flat")) {
            // Flat damage increase
            if (effectName.Contains("All")) {
                stats.m_dmgReduceFlatAll += statistic.m_damageReduceFlat;
            }
            else {
                // Extract the school name from the effect name.
                var schoolName = ExtractSchoolName(effectName);
                ApplySchoolEffect(ref stats.m_dmgReduceFlat, schoolName, statistic.m_damageReduceFlat);
            }
        }
        else {
            // Percent damage increase
            if (effectName.Contains("All")) {
                stats.m_dmgReducePercentAll += statistic.m_damageReducePercent;
            }
            else {
                // Extract the school name from the effect name.
                var schoolName = ExtractSchoolName(effectName);
                ApplySchoolEffect(ref stats.m_dmgReducePercent, schoolName, statistic.m_damageReducePercent);
            }
        }
    }

    private static void RemoveSchoolEffect(ref List<float> effectList, string schoolName, float value) {
        var index = MagicSchools.GetMagicSchool(schoolName)?.m_schoolIndex ?? -1;

        if (index == -1) {
            Logger.Warning("Could not find magic school {0}.", 
                Logger.Args(schoolName));

            return;
        }

        EnsureSchoolEffectCapacity(ref effectList); // CLASSIC: removal must preserve other schools' offsets.
        effectList[index] -= value;
    }

    private static void RemoveSchoolMastery(ServerWizGameStats stats, string effectCategory) {
        switch (effectCategory) {
            case var category when category.Contains("Ice"):
                stats.m_iceMastery = 0;
                break;
            case var category when category.Contains("Life"):
                stats.m_lifeMastery = 0;
                break;
            case var category when category.Contains("Fire"):
                stats.m_fireMastery = 0;
                break;
            case var category when category.Contains("Myth"):
                stats.m_mythMastery = 0;
                break;
            case var category when category.Contains("Death"):
                stats.m_deathMastery = 0;
                break;
            case var category when category.Contains("Storm"):
                stats.m_stormMastery = 0;
                break;
            case var category when category.Contains("Balance"):
                stats.m_balanceMastery = 0;
                break;
        }
    }

    private static void RemoveDamageIncrease(ServerWizGameStats stats, string effectName, WizStatisticEffect statistic) {
        if (effectName.Contains("Flat")) {
            if (effectName.Contains("All")) {
                stats.m_dmgBonusFlatAll -= statistic.m_damageBonusFlat;
            }
            else {
                var schoolName = ExtractSchoolName(effectName);
                RemoveSchoolEffect(ref stats.m_dmgBonusFlat, schoolName, statistic.m_damageBonusFlat);
            }
        }
        else {
            if (effectName.Contains("All")) {
                stats.m_dmgBonusPercentAll -= statistic.m_damageBonusPercent;
            }
            else {
                var schoolName = ExtractSchoolName(effectName);
                RemoveSchoolEffect(ref stats.m_dmgBonusPercent, schoolName, statistic.m_damageBonusPercent);
            }
        }
    }

    private static void RemoveAccuracyIncrease(ServerWizGameStats stats, string effectName, WizStatisticEffect statistic) {
        if (effectName.Contains("All")) {
            stats.m_accBonusPercentAll -= statistic.m_accuracyBonusPercent;
        }
        else {
            var schoolName = ExtractSchoolName(effectName);
            RemoveSchoolEffect(ref stats.m_accBonusPercent, schoolName, statistic.m_accuracyBonusPercent);
        }
    }

    private static void RemoveReduceDamageIncrease(ServerWizGameStats stats, string effectName, WizStatisticEffect statistic) {
        if (effectName.Contains("Flat")) {
            if (effectName.Contains("All")) {
                stats.m_dmgReduceFlatAll -= statistic.m_damageReduceFlat;
            }
            else {
                var schoolName = ExtractSchoolName(effectName);
                RemoveSchoolEffect(ref stats.m_dmgReduceFlat, schoolName, statistic.m_damageReduceFlat);
            }
        }
        else {
            if (effectName.Contains("All")) {
                stats.m_dmgReducePercentAll -= statistic.m_damageReducePercent;
            }
            else {
                var schoolName = ExtractSchoolName(effectName);
                RemoveSchoolEffect(ref stats.m_dmgReducePercent, schoolName, statistic.m_damageReducePercent);
            }
        }
    }

    private static string ExtractSchoolName(string input) {
        // Define a regular expression pattern to match the word after "Canonical" and before any subsequent uppercase letters
        var regex = new Regex(@"Canonical([A-Z][a-z]*)");
        var match = regex.Match(input);

        if (match.Success) {
            // Extract and return the captured group value (the school name)
            return match.Groups[1].Value;
        }

        // Return null or an appropriate value if no match is found
        return null;
    }

}
