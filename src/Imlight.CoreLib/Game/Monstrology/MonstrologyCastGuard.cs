using System;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Monstrology;

internal static class MonstrologyCastGuard {
    internal static bool IsTemplate(SpellTemplate template, string path = null) {
        if (template?.m_effects == null) return false;
        var effects = template.m_effects;
        if (effects.Any(e => e?.m_effectType == kSpellEffects.kCollectEssence && e.m_effectParam > 0
            && e.m_effectTarget is kEffectTarget.kSpell or kEffectTarget.kSpecificSpells)
            && template.m_adjectives?.Any(a => a?.StartsWith("Collect_",StringComparison.Ordinal) == true) == true)
            return true;
        if (!template.m_Treasure) return false;
        var summon = effects.Any(e => e?.m_effectType == kSpellEffects.kSummonCreature && e.m_effectParam > 0);
        var kill = effects.Any(e => e?.m_effectType == kSpellEffects.kKillCreature && e.m_effectParam > 0);
        if (!summon && !kill) return false;
        if ((summon && template.m_adjectives?.Contains("MonsterMagicSummon") == true)
            || (kill && template.m_adjectives?.Contains("MonsterMagicKill") == true)) return true;
        return path?.Replace('\\','/').Split('/').Contains("MonsterMagicTC",StringComparer.OrdinalIgnoreCase) == true;
    }
    private static bool IsInstalled(uint id, SpellTemplate template = null) {
        template ??= CoreObjectFactory.GetCoreTemplate(id) as SpellTemplate;
        if (IsTemplate(template)) return true;
        // Only creature-specific TC effects need the manifest identity fallback. Ordinary Myth minion effects do not.
        if (template?.m_Treasure != true || template.m_effects?.Any(e => e?.m_effectParam > 0
            && e.m_effectType is kSpellEffects.kSummonCreature or kSpellEffects.kKillCreature) != true) return false;
        return IsTemplate(template,CoreObjectFactory.GetTemplatePath(id));
    }
    internal static bool RequiresPermission(Spell card, SpellTemplate castTemplate = null)
        => card != null && (IsInstalled(card.m_templateID,castTemplate)
            || (card.m_enchantment != 0 && IsInstalled(card.m_enchantment)));
    internal static bool Run(bool requiresPermission, MonstrologySessionPolicy policy, bool globallyEnabled, System.Action action) {
        if (!requiresPermission) { action(); return true; }
        return policy?.WithPermission(globallyEnabled, () => { action(); return MonstrologyResult.Applied; }) == MonstrologyResult.Applied;
    }
}
