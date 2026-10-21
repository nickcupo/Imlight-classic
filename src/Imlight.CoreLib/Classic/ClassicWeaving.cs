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
 * CLASSIC OWNER EXTRA: SCHOOL WEAVING (combat side)
 * ========================================================================
 *
 * PURPOSE:
 * A Weave card (one per school, rules/weaving-*.yaml) dragged onto a trained
 * off-school single-target damage card in hand turns that card, for this
 * fight, into the weaver's school: the hand card becomes the curated client
 * face (so the client draws it, and pips, accuracy charms, blades, traps and
 * shields see the weaver's school), while the cast template is built here
 * from the ORIGINAL card plus the school's rider. The client's own Mutate
 * mechanic carries it (move type 5, kModifyCardMutation): no exe patch.
 *
 * NOTE:
 * Not 2010. Off unless features.experiments.weaving is true. Design and
 * worked numbers: ~/w101c-private/build/playbot-reports/design-weaving.md.
 *
 * Created by: Nick with Claude Code (claude-fable-5-1)
 * Version: KALI 1.0
 * Last Updated: 10/21/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Classic;

/// <summary>CLASSIC OWNER EXTRA: School Weaving.</summary>
internal static class ClassicWeaving {

    /// <summary>Resolves a client template path to its id (replaced in tests).</summary>
    internal static Func<string, ulong?> ResolveTemplate { get; set; } = CoreObjectFactory.TryGetTemplateIdByPath;

    /// <summary>Loads a template by id (replaced in tests).</summary>
    internal static Func<ulong, CoreTemplate?> LoadTemplate { get; set; } = id => CoreObjectFactory.GetCoreTemplate(id);

    /// <summary>The table in force, or Empty.</summary>
    internal static WeavingRules Rules => ClassicRuntime.IsInitialized && ClassicProgression.Weaving.AppliesTo(ClassicRuntime.Rules.Profile.Id)
        ? ClassicProgression.Weaving : WeavingRules.Empty;

    /// <summary>True while the experiment is switched on in the active profile and a table applies to it.</summary>
    internal static bool Active
        => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive && ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.ExperimentsWeaving) && Rules.Id.Length > 0;

    /// <summary>The school weave whose Weave card has template id <paramref name="templateId"/>, when weaving is active.</summary>
    internal static SchoolWeave? WeaveOfCard(ulong templateId) {
        if (!Active) return null;
        foreach (var weave in Rules.Weaves) {
            if (ResolveTemplate(weave.Card) == templateId) return weave;
        }
        return null;
    }

    /// <summary>
    /// Tries to weave <paramref name="target"/> with the Weave card <paramref name="source"/>. On success the hand card is
    /// the face template (school = weaver's school, pips = the original card's, accuracy = the original's minus the rider
    /// penalty) and <paramref name="castTemplate"/> is the original card converted to the school with the rider added.
    /// </summary>
    internal static bool TryPrepare(Spell source, Spell target, SpellTemplate original, bool pvp,
                                    out Spell? enchanted, out SpellTemplate? castTemplate, out string? refusal) {
        enchanted = null;
        castTemplate = null;
        refusal = null;
        if (WeaveOfCard(source.m_templateID) is not { } weave) return false; // not a Weave card: not ours
        var rules = Rules;
        if (pvp && !rules.Pvp) { refusal = "weaving is off in PvP"; return false; }
        if (rules.RiderFor(weave.School) is not { } rider) { refusal = "no rider"; return false; }
        var input = weave.Inputs.FirstOrDefault(pair => ResolveTemplate(pair.Input) == target.m_templateID);
        if (input is null) { refusal = "that card cannot be woven by this school"; return false; }
        if (string.Equals(original.m_sMagicSchoolName, SchoolName(weave.School), StringComparison.OrdinalIgnoreCase)) {
            refusal = "already the weaver's school"; return false;
        }
        if (ResolveTemplate(input.Face) is not { } faceId || LoadTemplate(faceId) is not SpellTemplate face) { refusal = "the face template is missing"; return false; }
        if (!string.Equals(face.m_sMagicSchoolName, SchoolName(weave.School), StringComparison.OrdinalIgnoreCase)) {
            refusal = "the face is not the weaver's school"; return false;
        }
        if (!TryBuildCast(original, rider, out var built, out refusal)) return false;

        var card = SpellFactory.GetSpell(face, unchecked((uint) faceId));
        card.m_treasureCard = false;
        card.m_itemCard = false;
        card.m_premutationSpellID = target.m_templateID;
        card.m_pipCost = original.m_spellRank; // the woven card costs what the original cost
        card.m_accuracy = (byte) Math.Clamp(target.m_accuracy - rider.AccuracyPenalty, 0, byte.MaxValue);
        card.m_enchantment = source.m_templateID;
        card.m_enchantmentSpellIsItemCard = source.m_itemCard;
        card.m_enchantedThisCombat = true;
        enchanted = card;
        castTemplate = built with { m_name = face.m_name, m_displayName = face.m_displayName, m_imageName = face.m_imageName };
        return true;
    }

    /// <summary>
    /// The cast template of a weave: every damage, damage-over-time and drain of <paramref name="original"/> becomes the
    /// rider's school at hit_percent, then the rider's own effects are added. Pure, so it is tested without a duel.
    /// </summary>
    internal static bool TryBuildCast(SpellTemplate original, WeaveRider rider, out SpellTemplate built, out string? refusal) {
        built = original;
        refusal = null;
        if (original.m_effects is not { Count: > 0 } effects) { refusal = "the card has no effects"; return false; }
        if (effects.Any(effect => effect is VariableSpellEffect || effect is null)) { refusal = "X-pip cards cannot be woven"; return false; }
        if (!effects.Any(IsDamageBearing)) { refusal = "only damage cards can be woven"; return false; }
        if (effects.Any(effect => Targets(effect).Any(target => target is not kEffectTarget.kEnemySingle))) {
            refusal = "only single-target cards can be woven"; return false;
        }
        var school = SchoolName(rider.School);
        var baseDamage = effects.Sum(AverageDamage);
        if (baseDamage <= 0) { refusal = "the card deals no damage"; return false; }

        var converted = new List<SpellEffect>();
        if (rider.FirstHitPercent > 0) converted.Add(Hit(kSpellEffects.kDamage, Scale(baseDamage, rider.FirstHitPercent), school, kEffectTarget.kEnemySingle));
        foreach (var effect in effects) converted.Add(Convert(effect, rider, school));
        if (rider.BurnPercent > 0 && rider.BurnRounds > 0) {
            var burn = Hit(kSpellEffects.kDamageOverTime, Scale(baseDamage, rider.BurnPercent), school, kEffectTarget.kEnemySingle);
            burn.m_numRounds = rider.BurnRounds;
            converted.Add(burn);
        }
        if (rider.AbsorbPercent > 0) converted.Add(Hit(kSpellEffects.kAbsorbDamage, Scale(baseDamage, rider.AbsorbPercent), "All", kEffectTarget.kSelf));
        if (rider.HealPercent > 0 && rider.HealRounds > 0) {
            var bloom = Hit(kSpellEffects.kHealOverTime, Scale(baseDamage, rider.HealPercent), school, kEffectTarget.kSelf);
            bloom.m_numRounds = rider.HealRounds;
            converted.Add(bloom);
        }
        if (rider.TrapPercent > 0) converted.Add(Hit(kSpellEffects.kModifyIncomingDamage, rider.TrapPercent, "All", kEffectTarget.kEnemySingle));

        built = original with { m_effects = converted, m_sMagicSchoolName = school, m_sTypeName = "Damage" };
        return true;
    }

    /// <summary>The School Weaving deck limit for a Weave card: (copies allowed, must the deck be this school).</summary>
    internal static (int Copies, string School)? DeckLimit(ulong templateId)
        => WeaveOfCard(templateId) is { } weave ? (Rules.DeckCopies, SchoolName(weave.School)) : null;

    private static readonly kSpellEffects[] s_damageKinds = [kSpellEffects.kDamage, kSpellEffects.kDamageOverTime, kSpellEffects.kStealHealth];

    internal static string SchoolName(string school) => school.Length == 0 ? school : char.ToUpperInvariant(school[0]) + school[1..].ToLowerInvariant();

    private static SpellEffect Hit(kSpellEffects kind, int amount, string school, kEffectTarget target) => new() {
        m_effectType = kind, m_effectParam = amount, m_sDamageType = school, m_damageType = StringHash.Compute(school),
        m_effectTarget = target, m_healModifier = 1.0f,
    };

    private static int Scale(double amount, int percent) => (int) Math.Round(amount * percent / 100.0, MidpointRounding.AwayFromZero);

    private static SpellEffect Convert(SpellEffect effect, WeaveRider rider, string school) {
        var copy = SpellTemplateEditor.Copy(effect);
        if (Children(copy) is { } children) {
            for (var i = 0; i < children.Count; i++) children[i] = Convert(children[i], rider, school);
            return copy;
        }
        if (!s_damageKinds.Contains(copy.m_effectType)) return copy; // a trap or other rider of the original stays as it was
        copy.m_effectParam = Scale(copy.m_effectParam, rider.HitPercent);
        copy.m_sDamageType = school;
        copy.m_damageType = StringHash.Compute(school);
        if (rider.DrainPercent > 0 && copy.m_effectType == kSpellEffects.kDamage) {
            copy.m_effectType = kSpellEffects.kStealHealth;
            copy.m_healModifier = rider.DrainPercent / 100f;
        } else if (copy.m_effectType == kSpellEffects.kStealHealth && rider.DrainPercent == 0) {
            copy.m_effectType = kSpellEffects.kDamage; // only Death keeps the drain
            copy.m_healModifier = 1.0f;
        }
        return copy;
    }

    private static double AverageDamage(SpellEffect effect) => effect switch {
        RandomSpellEffect random when random.m_effectList is { Count: > 0 } list => list.Average(AverageDamage),
        EffectListSpellEffect list when list.m_effectList is { Count: > 0 } items => items.Sum(AverageDamage),
        _ => effect is not null && s_damageKinds.Contains(effect.m_effectType) ? effect.m_effectParam : 0,
    };

    private static bool IsDamageBearing(SpellEffect effect) => effect switch {
        RandomSpellEffect or EffectListSpellEffect => Children(effect)?.Any(IsDamageBearing) == true,
        _ => effect is not null && s_damageKinds.Contains(effect.m_effectType),
    };

    private static IEnumerable<kEffectTarget> Targets(SpellEffect effect) => effect switch {
        RandomSpellEffect or EffectListSpellEffect => Children(effect)?.SelectMany(Targets) ?? [],
        _ => effect is not null && s_damageKinds.Contains(effect.m_effectType) ? [effect.m_effectTarget] : [],
    };

    private static List<SpellEffect>? Children(SpellEffect effect) => effect switch {
        RandomSpellEffect random => random.m_effectList,
        EffectListSpellEffect list => list.m_effectList,
        _ => null,
    };
}
