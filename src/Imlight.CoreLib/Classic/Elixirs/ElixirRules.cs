using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic.Elixirs;

// CLASSIC: r806919 0x142117390 caps active Elixir slots at three; 0x1421c1a30
// rejects any exact shared m_typeList token. Neither display names nor item ids define a family.
internal sealed record ElixirDefinition(uint TemplateId, uint DurationSeconds, IReadOnlyList<string> Families,
    bool CombatEnabled, bool PvpEnabled, string Provenance, IReadOnlyList<ElixirEffect> Effects = null, int Crowns = 0) {
    internal bool Valid => TemplateId > 0 && TemplateId < (1u << 28) && DurationSeconds > 0
        && Families is { Count: > 0 } && Families.All(f => !string.IsNullOrWhiteSpace(f))
        && Families.Distinct(StringComparer.Ordinal).Count() == Families.Count
        && !string.IsNullOrWhiteSpace(Provenance);
}

internal sealed record ElixirEffect(string Name, int LookupIndex, float Value);

internal static class ElixirRules {
    internal const string SlotName = "Elixir";
    internal const int MaximumActive = 3;
    private sealed class CombatContext { internal int State; } // 1 outside, 2 PvE, 3 PvP.
    private static readonly ConditionalWeakTable<Wizard, CombatContext> s_combat = new();

    // Only trusted service notifications publish this context; no client purchase field
    // determines whether a duel is PvP. An unknown in-duel context refuses activation.
    internal static void SetCombatContext(Wizard wizard, bool inCombat, bool pvp) {
        if (wizard is not null) Volatile.Write(ref s_combat.GetValue(wizard, _ => new()).State,
            !inCombat ? 1 : pvp ? 3 : 2);
    }

    internal static bool CanActivate(Wizard wizard, ElixirDefinition definition) {
        if (wizard is null || definition?.Valid != true) return false;
        var state = s_combat.TryGetValue(wizard, out var context) ? Volatile.Read(ref context.State) : 0;
        if (state is 2 or 3) return EffectsEnabled(definition, true, state == 3);
        return !wizard.IsInDuel && EffectsEnabled(definition, false, false);
    }

    // CLASSIC: February 23, 2010 Friendly Necromancer chart, corroborated by the March 24
    // Crown Shop oldid65819 and both owned native clients. Only these ten verified products
    // are approved in the bounded October profile (plus the six school Battle elixirs below);
    // Gold/XP/Regeneration and the remaining Battle elixirs remain closed. A Crown catalog row cannot approve an effect by itself.
    private const string Evidence = "https://thefriendlynecromancer.blogspot.com/2010/02/whats-point-of-those-potions-how-do-i.html; https://wizard101.fandom.com/wiki/Crown_Shop?oldid=65819";
    private static readonly FrozenDictionary<uint, ElixirDefinition> s_verified = new[] {
        Verified(191099, 1800, "PowerPip", "CanonicalPowerPip", 119, .20f, 200),
        Verified(191100, 3600, "PowerPip", "CanonicalPowerPip", 119, .20f, 350),
        Verified(191101, 1800, "Accuracy", "CanonicalAllAccuracy", 114, .15f, 200),
        Verified(191102, 3600, "Accuracy", "CanonicalAllAccuracy", 114, .15f, 350),
        Verified(191103, 1800, "Damage", "CanonicalAllDamage", 114, .15f, 225),
        Verified(191104, 3600, "Damage", "CanonicalAllDamage", 114, .15f, 375),
        Verified(191105, 1800, "MaxHealth", "CanonicalMaxHealth", 499, 500f, 150),
        Verified(191106, 3600, "MaxHealth", "CanonicalMaxHealth", 499, 500f, 225),
        Verified(191107, 1800, "MaxMana", "CanonicalMaxMana", 402, 500f, 100),
        Verified(191108, 3600, "MaxMana", "CanonicalMaxMana", 402, 500f, 175),
        // CLASSIC (2026-10-08): six school Battle elixirs, 300 Crowns and 30 minutes in the February 23, 2010 chart,
        // the March 24 (oldid 65819) and October 26, 2010 (oldid 119160) Crown Shop lists and both native clients,
        // which agree on each school's accuracy and damage points. Balance Battle (chart and clients disagree), the
        // all-school Battle pair (price conflict), Gold, XP and Regeneration stay closed.
        Battle(191117, "BattleFire", "Fire", 114, .15f, 109, .10f),
        Battle(191118, "BattleIce", "Ice", 109, .10f, 119, .20f),
        Battle(191119, "BattleStorm", "Storm", 114, .15f, 109, .10f),
        Battle(191120, "BattleMyth", "Myth", 109, .10f, 114, .15f),
        Battle(191121, "BattleLife", "Life", 104, .05f, 119, .20f),
        Battle(191122, "BattleDeath", "Death", 109, .10f, 114, .15f),
    }.ToFrozenDictionary(d => d.TemplateId);

    private const string BattleEvidence = Evidence + "; https://wizard101.fandom.com/wiki/Crown_Shop?oldid=119160";

    private static ElixirDefinition Battle(uint id, string family, string school, int accuracyIndex, float accuracy,
        int damageIndex, float damage)
        => new(id, 1800, [family], true, false, BattleEvidence,
            [new($"Canonical{school}Accuracy", accuracyIndex, accuracy), new($"Canonical{school}Damage", damageIndex, damage)], 300);

    private static ElixirDefinition Verified(uint id, uint seconds, string family, string effect, int index, float value, int crowns)
        => new(id, seconds, [family], true, false, Evidence, [new(effect, index, value)], crowns);

    internal static ElixirDefinition Approved(uint templateId)
        => ClassicRuntime.IsInitialized ? Approved(ClassicRuntime.Rules, templateId) : null;

    internal static ElixirDefinition Approved(ClassicRules rules, uint templateId)
        => rules?.Profile.Id == "october-2010-arc1" && rules.IsFeatureEnabled(ClassicFeatures.Elixirs)
            && s_verified.TryGetValue(templateId, out var definition) ? definition : null;

    internal static bool MatchesNative(ElixirDefinition definition, WizItemTemplate template) {
        if (definition?.Valid != true || template is null || template.m_templateID != definition.TemplateId
            || definition.Effects is not { Count: > 0 }) return false;
        var behaviors = template.m_behaviors?.OfType<ElixirBehaviorTemplate>().ToArray();
        var native = behaviors is { Length: 1 } ? behaviors[0] : null;
        if (native is null || native.m_timerType != TimerType.TimerType_Game
            || !uint.TryParse(native.m_expireTime, out var seconds) || seconds != definition.DurationSeconds
            || native.m_combatEnabled != definition.CombatEnabled || native.m_PvPEnabled != definition.PvpEnabled
            || native.m_typeList is null || !native.m_typeList.ToHashSet(StringComparer.Ordinal).SetEquals(definition.Families)
            || template.m_equipEffects?.Count != definition.Effects.Count) return false;
        for (var i = 0; i < definition.Effects.Count; i++) {
            var effect = definition.Effects[i];
            if (template.m_equipEffects[i] is not StatisticEffectInfo info || info.m_effectName != effect.Name
                || info.m_lookupIndex != effect.LookupIndex || !float.IsFinite(effect.Value) || effect.Value <= 0) return false;
            var actual = CanonicalStatEffects.GetCanonicalStatValue(info);
            if (!float.IsFinite(actual) || Math.Abs(actual - effect.Value) > .000001f) return false;
        }
        return true;
    }

    internal static bool IsElixir(WizClientObjectItem item, WizItemTemplate template = null)
        => item?.m_inactiveBehaviors?.Any(b => b is ClientElixirBehavior) == true
            || template?.m_behaviors?.Any(b => b is ElixirBehaviorTemplate) == true;

    internal static bool Overlaps(IEnumerable<string> first, IEnumerable<string> second)
        => first.Intersect(second, StringComparer.Ordinal).Any();

    internal static bool EffectsEnabled(ElixirDefinition definition, bool inCombat, bool pvp)
        => definition?.Valid == true && (!inCombat || definition.CombatEnabled)
            && (!pvp || definition.PvpEnabled);
}
