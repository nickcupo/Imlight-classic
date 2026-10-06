using System;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Native classic extension: MSG_COMBATMOVE type 5 uses two zero-based hand indices.
/// This is not the conflicting historical move numbering in WizCombatMessages.xml.
/// Preparation is side-effect free; unsupported shapes leave both cards untouched.
/// </summary>
internal static class ClassicHandEnchantment {
    internal const byte MoveType = 5;

    internal static bool TryPrepare(Spell source, Spell target, out Spell enchanted, out SpellTemplate castTemplate, bool pvp = false) {
        enchanted = null;
        castTemplate = null;
        if (!ClassicRuntime.IsActive || source is null || target is null || ReferenceEquals(source, target)
            || source.m_enchantment != 0 || target.m_enchantment != 0 || target.m_enchantedThisCombat
            || target.m_treasureCard || target.m_itemCard || target.m_battleCard
            || CoreObjectFactory.GetCoreTemplate(source.m_templateID) is not SpellTemplate enchantment
            || CoreObjectFactory.GetCoreTemplate(target.m_templateID) is not SpellTemplate original
            || enchantment.m_spellRank?.m_spellRank != 0 || enchantment.m_effects is not { Count: > 0 }
            || (pvp ? original.m_noPvPEnchant : original.m_noPvEEnchant)
            || (pvp && !AllowsPvpEnchantment(source))) { // CLASSIC: only dated October Cloak is enabled in PvP.
            return false;
        }
        var collection = enchantment.m_effects.Where(e => e?.m_effectType == kSpellEffects.kCollectEssence).ToArray();
        var modifiers = enchantment.m_effects.Where(e => e?.m_effectType != kSpellEffects.kCollectEssence).ToArray();
        if (collection.Length > 0) {
            if (!Imlight.CoreLib.Game.Monstrology.MonstrologyService.Enabled || collection.Length != 1
                || collection[0].m_effectParam != 100 || collection[0].m_effectTarget != kEffectTarget.kSpell
                || !Imlight.CoreLib.Game.Monstrology.MonstrologyPendingExtraction.TryFamily(enchantment, out _)
                || modifiers.Length > 1 || modifiers.Any(e => e == null || e.m_effectType != kSpellEffects.kModifyCardDamage)
                || original.m_effects?.Any(IsDamageBearing) != true) return false;
        } else if (modifiers.Length != 1) return false;
        var effect = modifiers.Length == 0 ? collection[0] : modifiers[0];
        if (effect is null || effect.m_effectTarget is not (kEffectTarget.kSpell or kEffectTarget.kSpecificSpells)
            || (enchantment.m_validTargetSpells is { Count: > 0 } valid && !valid.Contains(target.m_templateID))
            || (effect.m_effectTarget == kEffectTarget.kSpecificSpells && enchantment.m_validTargetSpells is not { Count: > 0 })
            || original.m_effects is not { Count: > 0 }
            || original.m_effects.Any(e => e.m_effectTarget is kEffectTarget.kSpell or kEffectTarget.kSpecificSpells)) {
            return false;
        }

        enchanted = target with { };
        castTemplate = original;
        switch (effect.m_effectType) {
            case kSpellEffects.kModifyCardCloak when ClassicOctoberRules.Active:
                // CLASSIC: Cloak disguises a charm or ward. Keep the spell's effect and numbers intact, and use
                // the real client's cloak flags on the hand card and its cast effects; never change the shared template.
                if (!string.Equals(original.m_sTypeName, "Charm", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(original.m_sTypeName, "Ward", StringComparison.OrdinalIgnoreCase)) {
                    return false;
                }
                enchanted.m_cloaked = true;
                castTemplate = original with {
                    m_cloaked = true,
                    m_effects = original.m_effects.Select(originalEffect => {
                        var cloaked = SpellTemplateEditor.Copy(originalEffect);
                        cloaked.m_cloaked = true;
                        cloaked.m_enchantmentSpellTemplateID = source.m_templateID;
                        return cloaked;
                    }).ToList(),
                };
                break;
            case kSpellEffects.kCollectEssence when collection.Length == 1:
                break; // Collection stays identified by the authoritative enchantment template; never a client award.
            case kSpellEffects.kModifyCardAccuracy when effect.m_effectParam > 0:
                enchanted.m_accuracy = (byte) Math.Clamp((long) target.m_accuracy + effect.m_effectParam, 0, byte.MaxValue);
                break;
            case kSpellEffects.kModifyCardDamage when effect.m_effectParam > 0: {
                // 2009: "Add 175 (total) damage to any non-Treasure Card Damage spell" (wiki Monstrous Treasure
                // Card, oldid 54390, 2009-12-20). The bonus is added once per cast: to a damage-over-time total
                // (spread over its ticks), to a drain's damage, and on a card with several hits to its first damage
                // hit (inferred from "total"). Alternatives (a roll or a per-pip list) each get it; one resolves.
                var index = original.m_effects.FindIndex(e => CanAdjustDamage(e, effect.m_effectParam));
                if (index < 0 || original.m_effects.Take(index).Any(IsDamageBearing)) {
                    return false;
                }
                var adjusted = SpellTemplateEditor.Copy(original.m_effects[index]);
                AddDamage(adjusted, effect.m_effectParam);
                var effects = original.m_effects.ToList();
                effects[index] = adjusted;
                castTemplate = original with { m_effects = effects };
                enchanted.m_regularAdjust = effect.m_effectParam;
                break;
            }
            case kSpellEffects.kModifyCardMutation:
                // Offline manifest check: mutation's effect parameter is the output template ID;
                // validTargetSpells contains input template IDs. Require the explicit input list.
                if (enchantment.m_validTargetSpells is not { Count: > 0 }
                    || CoreObjectFactory.GetCoreTemplate(unchecked((uint) effect.m_effectParam)) is not SpellTemplate mutation
                    || mutation.m_effects is not { Count: > 0 }) {
                    return false;
                }
                enchanted = SpellFactory.GetSpell(mutation, unchecked((uint) effect.m_effectParam));
                // This is a transformed regular hand copy, not an entry in the persistent TC vault.
                enchanted.m_treasureCard = false;
                enchanted.m_premutationSpellID = target.m_templateID;
                castTemplate = mutation;
                break;
            default:
                return false;
        }
        enchanted.m_enchantment = source.m_templateID;
        enchanted.m_enchantmentSpellIsItemCard = source.m_itemCard;
        enchanted.m_enchantedThisCombat = true;
        return true;
    }

    // CLASSIC: do not open the existing PvE enchantment extensions or Monstrology to PvP.
    internal static bool AllowsPvpEnchantment(Spell source)
        => ClassicOctoberRules.Active && source is not null
            && CoreObjectFactory.GetCoreTemplate(source.m_templateID) is SpellTemplate { m_effects.Count: 1 } template
            && template.m_effects[0]?.m_effectType == kSpellEffects.kModifyCardCloak;

    private static readonly kSpellEffects[] s_damageKinds = [kSpellEffects.kDamage, kSpellEffects.kDamageOverTime, kSpellEffects.kStealHealth];

    private static bool CanAdjustDamage(SpellEffect effect, int amount) => effect switch {
        RandomSpellEffect or VariableSpellEffect or EffectListSpellEffect => Children(effect) is { Count: > 0 } children
            && children.All(e => e is not (RandomSpellEffect or VariableSpellEffect or EffectListSpellEffect) && CanAdjustDamage(e, amount)),
        _ => effect is not null && effect.GetType() == typeof(SpellEffect)
            && s_damageKinds.Contains(effect.m_effectType) && effect.m_effectParam >= 0
            && effect.m_effectParam <= int.MaxValue - amount,
    };

    // Any damage the card deals, whether or not the enchantment could be added to it.
    private static bool IsDamageBearing(SpellEffect effect) => effect switch {
        RandomSpellEffect or VariableSpellEffect or EffectListSpellEffect => Children(effect)?.Any(IsDamageBearing) == true,
        _ => effect is not null && s_damageKinds.Contains(effect.m_effectType),
    };

    private static System.Collections.Generic.List<SpellEffect> Children(SpellEffect effect) => effect switch {
        RandomSpellEffect random => random.m_effectList,
        VariableSpellEffect variable => variable.m_effectList,
        EffectListSpellEffect list => list.m_effectList,
        _ => null,
    };

    private static void AddDamage(SpellEffect effect, int amount) {
        if (Children(effect) is { } children) {
            foreach (var child in children) AddDamage(child, amount);
        } else {
            effect.m_effectParam += amount;
        }
    }
}
