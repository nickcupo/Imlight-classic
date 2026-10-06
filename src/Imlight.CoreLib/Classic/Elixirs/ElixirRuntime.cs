using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic.Elixirs;

internal readonly record struct ElixirTimer(ulong ItemId, uint RemainingSeconds);

// CLASSIC: protocol metadata never authorizes historical numeric effects. Every effect must
// match the separately approved name/index/value before the canonical stat path is used.
internal static class ElixirRuntime {
    private sealed class EffectSequence { internal int Last = -1; }
    private sealed class ValidatedState { internal ElixirLedger Ledger; }
    private static readonly ConditionalWeakTable<Wizard, EffectSequence> s_effectIds = new();
    private static readonly ConditionalWeakTable<Wizard, ValidatedState> s_active = new();
    internal static bool LoadValidated(Wizard wizard) => ElixirCollection.LoadValidated(wizard);
    internal static void Invalidate(Wizard wizard) {
        if (wizard is not null && s_active.TryGetValue(wizard, out var state)) Volatile.Write(ref state.Ledger, null);
    }
    internal static void PublishValidated(Wizard wizard, ElixirLedger ledger)
        => Volatile.Write(ref s_active.GetValue(wizard, _ => new ValidatedState()).Ledger, ledger.Copy());
    internal static bool HasValidatedEntry(Wizard wizard, WizClientObjectItem item) {
        if (wizard is null || item is null || !s_active.TryGetValue(wizard, out var state)) return false;
        var ledger = Volatile.Read(ref state.Ledger);
        var entry = ledger?.OwnerId == wizard.CharId ? ledger.Active.FirstOrDefault(e => e.ItemId == item.m_globalID) : null;
        return entry is not null && item.m_characterId == wizard.CharId && entry.TemplateId == item.m_templateID.Full
            && entry.RemainingSeconds > 0
            && item.m_inactiveBehaviors?.OfType<ClientElixirBehavior>().SingleOrDefault()?.m_expireTime == entry.RemainingSeconds;
    }
    internal static bool IsElixir(WizItemTemplate template)
        => template?.m_behaviors?.Any(b => b is ElixirBehaviorTemplate) == true;

    internal static bool CanApplyEffects(Wizard wizard, WizClientObjectItem item, WizItemTemplate template,
        bool? pvp = null, bool? inCombat = null) {
        if (!HasValidatedEntry(wizard, item) || item.m_characterId != wizard.CharId
            || item.m_templateID.Full >= (1UL << 28) || !IsElixir(template)
            || !wizard.EquipmentBehavior.HasItemEquipped(item.m_globalID)) return false;
        var definition = ElixirRules.Approved((uint)item.m_templateID.Full);
        if (!ElixirRules.EffectsEnabled(definition, inCombat ?? wizard.IsInDuel, pvp ?? wizard.IsInDuel)
            || definition.Effects is not { Count: > 0 }
            || item.m_inactiveBehaviors?.OfType<ClientElixirBehavior>().SingleOrDefault() is not { m_expireTime: > 0 }) return false;
        var native = template.m_behaviors.OfType<ElixirBehaviorTemplate>().SingleOrDefault();
        if (native is null || native.m_timerType != TimerType.TimerType_Game
            || native.m_combatEnabled != definition.CombatEnabled || native.m_PvPEnabled != definition.PvpEnabled
            || native.m_typeList is null || !native.m_typeList.ToHashSet(StringComparer.Ordinal).SetEquals(definition.Families)
            || template.m_equipEffects?.Count != definition.Effects.Count) return false;
        for (var i = 0; i < definition.Effects.Count; i++) {
            var approved = definition.Effects[i];
            if (template.m_equipEffects[i] is not StatisticEffectInfo info || info.m_effectName != approved.Name
                || info.m_lookupIndex != approved.LookupIndex || !float.IsFinite(approved.Value)
                || approved.Value <= 0)
                return false;
            var actual = CanonicalStatEffects.GetCanonicalStatValue(info);
            if (!float.IsFinite(actual) || Math.Abs(actual - approved.Value) > .000001f) return false;
        }
        return true;
    }

    internal static List<GameEffectBase> AddApprovedEffects(Wizard wizard, WizClientObjectItem item,
        WizItemTemplate template, bool? pvp = null, bool? inCombat = null) {
        if (!CanApplyEffects(wizard, item, template, pvp, inCombat)) return [];
        if (wizard.GameEffects.Snapshot().Any(e => e.m_originatorID == item.m_globalID
            && e.m_itemSlotID == StringHash.Compute(ElixirRules.SlotName))) {
            item.m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_statsApplied = true;
            return [];
        }
        var infos = template.m_equipEffects.OfType<StatisticEffectInfo>().ToArray();
        var effects = infos.Select(info => GameEffectFactory.CreateEffectFromInfo(info,
            StringHash.Compute(ElixirRules.SlotName))).ToArray();
        if (effects.Any(e => e is not WizStatisticEffect)) return [];
        var added = new List<GameEffectBase>();
        for (var i = 0; i < infos.Length; i++) {
            var effect = effects[i];
            effect.m_internalID = NextEffectId(wizard);
            effect.m_originatorID = item.m_globalID;
            CharacterEffectHelper.AddGameEffectToStats(wizard.GameStats, infos[i]);
            wizard.GameEffects.Add(effect);
            added.Add(effect);
        }
        item.m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_statsApplied = true;
        return added;
    }

    internal static List<GameEffectBase> RemoveItemEffects(Wizard wizard, ulong itemId, WizItemTemplate template) {
        var owned = wizard.GameEffects.Snapshot().Where(e => e.m_originatorID == itemId
            && e.m_itemSlotID == StringHash.Compute(ElixirRules.SlotName)).ToArray();
        var infos = template?.m_equipEffects?.OfType<StatisticEffectInfo>().ToArray() ?? [];
        foreach (var effect in owned) {
            if (effect is not WizStatisticEffect statistic) continue;
            var info = infos.FirstOrDefault(i => StringHash.Compute(i.m_effectName) == effect.m_effectNameID);
            var canonical = info is null ? null : CanonicalStatEffects.GetEffectTemplate(info.m_effectName);
            if (canonical is null) continue; // never subtract an inferred stat or another item's effect.
            CharacterEffectHelper.RemoveStatisticEffectFromStats(wizard.GameStats, canonical.m_effectName, statistic);
            wizard.GameEffects.Remove(effect);
        }
        var removed = owned.Where(e => !wizard.GameEffects.Snapshot().Contains(e)).ToList();
        if (removed.Count == owned.Length
            && wizard.EquipmentBehavior.GetItem(itemId)?.m_inactiveBehaviors?.OfType<ClientElixirBehavior>().FirstOrDefault() is { } timed)
            timed.m_statsApplied = false;
        return removed;
    }

    internal static int NextEffectId(Wizard wizard) {
        var sequence = s_effectIds.GetValue(wizard, _ => new EffectSequence());
        lock (sequence) {
            var maximum = wizard.GameEffects.Snapshot().Select(e => e.m_internalID).DefaultIfEmpty(-1).Max();
            sequence.Last = Math.Max(sequence.Last, maximum);
            if (sequence.Last == int.MaxValue) throw new InvalidOperationException("Effect ids are exhausted.");
            return ++sequence.Last;
        }
    }

    internal static ElixirTimer[] RemainingTimers(Wizard wizard, Func<uint, ElixirDefinition> definitions = null)
        => wizard?.EquipmentBehavior?.EquippedItems?.Where(item => item.m_characterId == wizard.CharId
            && HasValidatedEntry(wizard, item)
            && item.m_templateID.Full < (1UL << 28)
            && (definitions ?? ElixirRules.Approved)((uint)item.m_templateID.Full)?.Valid == true)
            .Select(item => new ElixirTimer(item.m_globalID,
                item.m_inactiveBehaviors?.OfType<ClientElixirBehavior>().SingleOrDefault()?.m_expireTime ?? 0))
            .Where(timer => timer.RemainingSeconds > 0).ToArray() ?? [];

    internal static bool CalendarExpired(uint expireTime, TimerType timerType, DateTimeOffset now)
        => timerType == TimerType.TimerType_Calendar && expireTime != 0 && expireTime <= now.ToUnixTimeSeconds();
}
