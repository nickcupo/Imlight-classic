// CLASSIC: XP, level and the existing level-up refill are one fresh acknowledged character write.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Cryptography;
using Imlight.Classic;
using Imlight.CoreLib.Classic.Elixirs;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.WizardData.Collections;

internal sealed record ProgressionReceipt(int OldXp, int AppliedXp, int OldLevel, int Level,
    bool AdjustStats, bool Refill, bool ShouldSave, int Health, int Mana, float PowerPips,
    IReadOnlyList<IMessage> LevelMessages, WIZARD_12_PROTOCOL.MSG_UPDATEXP XpMessage) {
    internal ManaProgressionMaximum ManaTransition { get; init; }
}

// CLASSIC: fixtures replace table/native preparation only; persisted authority and the write lane stay real.
internal sealed class ProgressionDependencies {
    internal Func<MagicSchool, int, MagicLevelInfo> LevelInfo;
    internal Func<int, byte> LevelAtXp;
    internal Func<int, int> XpAtLevel;
    internal Func<int> MaxLevel;
    internal Func<int?> XpCeiling;
    internal Func<IMessage, bool> Prepare;
    internal Func<ulong, CoreTemplate> RentalTemplates;
    internal Func<DateTimeOffset> RentalNow;
}

internal readonly record struct RuntimeNormalization(bool NumericChanged, bool EquipmentChanged) {
    internal bool Changed => NumericChanged || EquipmentChanged;
}

internal static class WizardProgressionTransactions {
    internal static readonly AsyncLocal<ProgressionDependencies> TestScope = new();
    internal static MagicLevelInfo LevelInfo(MagicSchool school, int level)
        => TestScope.Value?.LevelInfo is { } table ? table(school, level) : MagicLevelsConfig.GetPlayerLevelInfo(school, level);
    internal static bool Prepare(IMessage message) => TestScope.Value?.Prepare?.Invoke(message) ?? true;

    internal static bool TryGainExperience(Wizard live, int requested, out ProgressionReceipt receipt, bool refill = true)
        => Commit(live, (session, saved) => TryStageExperience(live, saved, requested, out var staged, refill, session) ? staged : null, out receipt);

    internal static bool TrySetLevel(Wizard live, byte requested, bool resetMismatchedXp, out ProgressionReceipt receipt, bool refill = true)
        => Commit(live, (session, saved) => TryStageLevel(live, saved, requested, resetMismatchedXp, out var staged, refill, session) ? staged : null, out receipt);

    internal static bool TryRemoveExperience(Wizard live, int requested, out ProgressionReceipt receipt)
        => Commit(live, (session, saved) => StageExperience(live, saved, -requested, remove: true, refill: false, out var staged, session) ? staged : null, out receipt);

    private static bool Commit(Wizard live, Func<IDocumentSession, Wizard, ProgressionReceipt> stage, out ProgressionReceipt receipt) {
        receipt = null;
        if (!Usable(live)) return false;
        ProgressionReceipt prepared = null;
        var committed = WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
            if (!Usable(live)) return false;
            prepared = stage(session, saved);
            if (prepared?.ShouldSave != true) return false;
            WizardInventoryTransactions.ProtectUnmodifiedRows(session);
            return true;
        }, saved => Publish(live, saved, prepared),
            onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        if (committed || prepared is { ShouldSave: false }) { receipt = prepared; return true; }
        return false;
    }

    // CLASSIC: staging can be composed into a future quest transaction; it never saves or mutates live aliases.
    internal static bool TryStageExperience(Wizard live, Wizard saved, int requested, out ProgressionReceipt receipt,
        bool refill = true, IDocumentSession session = null)
        => StageExperience(live, saved, requested, remove: false, refill, out receipt, session);

    private static bool StageExperience(Wizard live, Wizard saved, int requested, bool remove, bool refill,
        out ProgressionReceipt receipt, IDocumentSession session = null) {
        receipt = null;
        if (!TryNormalizeAttachedRuntime(live, saved, session, out var normalization)
            || !RuntimeIdentityMatches(live, saved)) return false;
        var school = saved.MagicSchoolBehavior;
        var oldXp = school.ExperiencePoints;
        var oldLevel = school.Level;
        var ceiling = TestScope.Value?.XpCeiling is { } xpCeiling ? xpCeiling() : MagicLevelsConfig.MaxLevelXp;
        var applied = remove ? requested : ClassicRuntime.Rules.XpToApply(oldXp, requested, ceiling);
        if (!remove && applied == 0 && requested != 0) {
            receipt = new(oldXp, 0, oldLevel, oldLevel, false, false, normalization.Changed, 0, 0, 0, [], null);
            return true;
        }
        school.ExperiencePoints += applied;
        var levelAtXp = TestScope.Value?.LevelAtXp?.Invoke(school.ExperiencePoints)
            ?? MagicLevelsConfig.GetPlayerLevelAtExperience(school.ExperiencePoints);
        var changesLevel = remove ? levelAtXp < oldLevel : levelAtXp > oldLevel;
        var level = changesLevel ? Clamp(levelAtXp) : oldLevel;
        // CLASSIC: same-level XP touches no runtime offsets. A pending combat effect refresh
        // cannot discard earned numeric XP; level changes still require the complete context.
        if (changesLevel && !RuntimeContextMatches(live, saved)) return false;
        if (!Build(live, saved, oldXp, applied, oldLevel, level, changesLevel, changesLevel && !remove && refill, !remove, out receipt)) return false;
        return true;
    }

    internal static bool TryStageLevel(Wizard live, Wizard saved, byte requested, bool resetMismatchedXp,
        out ProgressionReceipt receipt, bool refill = true, IDocumentSession session = null) {
        receipt = null;
        if (!TryNormalizeAttachedRuntime(live, saved, session, out _) || !RuntimeContextMatches(live, saved)) return false;
        var school = saved.MagicSchoolBehavior;
        var oldXp = school.ExperiencePoints;
        var level = Clamp(requested);
        if (resetMismatchedXp && (TestScope.Value?.LevelAtXp?.Invoke(school.ExperiencePoints)
            ?? MagicLevelsConfig.GetPlayerLevelAtExperience(school.ExperiencePoints)) != level)
            school.ExperiencePoints = TestScope.Value?.XpAtLevel?.Invoke(level) ?? MagicLevelsConfig.GetExperiencePointsAtLevel(level);
        // CLASSIC: staff messages refill/broadcast even at the same level; direct model setters keep their old no-refill behavior.
        return Build(live, saved, oldXp, school.ExperiencePoints - oldXp, school.Level, level,
            adjustStats: true, refill, sendXp: false, out receipt);
    }

    private static byte Clamp(byte level) => (byte)ClassicRuntime.Rules.ClampLevel(level,
        TestScope.Value?.MaxLevel?.Invoke() ?? MagicLevelsConfig.MaxLevel);

    internal static int AttachedLevel(int level) => ClassicRuntime.Rules.ClampLevel(level,
        TestScope.Value?.MaxLevel?.Invoke() ?? MagicLevelsConfig.MaxLevel);

    // CLASSIC: reproduce only the existing attach clamps and proven calendar-rental cleanup.
    // The slot ledger is retained, exactly as LoadWizard retains it. Original item rows are reads;
    // the saving outer transaction must finalize them with ProtectUnmodifiedRows.
    internal static bool TryNormalizeAttachedRuntime(Wizard live, Wizard saved, IDocumentSession session,
        out RuntimeNormalization normalization) {
        normalization = default;
        if (!Usable(live) || saved?.CharId != live.CharId || saved.MagicSchoolBehavior is null) return false;
        var school = saved.MagicSchoolBehavior;
        var level = AttachedLevel(school.Level);
        var ceiling = TestScope.Value?.XpCeiling is { } xpCeiling ? xpCeiling() : MagicLevelsConfig.MaxLevelXp;
        var xp = ClassicRuntime.Rules.ClampXp(school.ExperiencePoints, ceiling);
        var numericChanged = level != school.Level || xp != school.ExperiencePoints;
        school.Level = level;
        school.ExperiencePoints = xp;
        var a = live.EquipmentBehavior; var b = saved.EquipmentBehavior;
        if (a is null || b is null) {
            normalization = new(numericChanged, false);
            return a is null && b is null;
        }
        var liveIds = a.EquippedItemIds ?? [];
        var savedIds = b.EquippedItemIds ?? [];
        if (liveIds.Any(id => id == 0) || savedIds.Any(id => id == 0)
            || liveIds.Distinct().Count() != liveIds.Count || savedIds.Distinct().Count() != savedIds.Count
            || liveIds.Except(savedIds).Any()) return false;
        var expired = savedIds.Except(liveIds).ToArray();
        var now = TestScope.Value?.RentalNow?.Invoke() ?? DateTimeOffset.UtcNow;
        foreach (var id in expired) {
            if (!WizardInventoryTransactions.TryReadOwnedItem(session, saved, id, out var item)
                || !(TestScope.Value?.RentalTemplates is { } templates
                    ? WizardItemCollection.IsExpired(item, now, templates)
                    : WizardItemCollection.IsExpired(item, now))) return false;
        }
        if (expired.Length != 0) b.EquippedItemIds = [..savedIds.Where(id => !expired.Contains(id))];
        normalization = new(numericChanged, expired.Length != 0);
        return true;
    }

    private static bool Build(Wizard live, Wizard saved, int oldXp, int applied, int oldLevel, int level,
        bool adjustStats, bool refill, bool sendXp, out ProgressionReceipt receipt) {
        receipt = null;
        var health = live.GameStats.m_baseHitpoints;
        var mana = live.GameStats.m_baseMana;
        var pips = live.GameStats.m_powerPipBase;
        ManaProgressionMaximum manaTransition = null;
        List<IMessage> messages = [];
        if (adjustStats) {
            var before = LevelInfo(saved.MagicSchoolBehavior.MagicSchool, oldLevel);
            var after = LevelInfo(saved.MagicSchoolBehavior.MagicSchool, level);
            if (before is null || after is null) return false;
            // CLASSIC: fresh Raven rows do not contain the runtime gear/effect offsets or JsonIgnore pip base.
            health += after.m_hitpoints - before.m_hitpoints;
            manaTransition = CharacterEffectHelper.PrepareManaProgressionMaximum(live.GameStats, after.m_mana - before.m_mana);
            if (manaTransition is not null) mana = manaTransition.Maximum;
            pips += after.m_pipChance - before.m_pipChance;
            saved.GameStats.m_baseHitpoints = health;
            saved.GameStats.m_baseMana = mana;
        }
        if (refill) {
            if (live.PetOwnerBehavior is null || saved.PetOwnerBehavior is null) return false;
            var after = LevelInfo(saved.MagicSchoolBehavior.MagicSchool, level);
            if (after is null) return false;
            saved.GameStats.m_currentHitpoints = health;
            saved.GameStats.m_currentMana = mana;
            saved.PetOwnerBehavior.SetEnergy(after.m_petEnergy);
            messages.Add(new WIZARD_12_PROTOCOL.MSG_LEVELUP { GlobalID = live.GameObjectID, NewLevel = level, Data = "0000000000" });
            // CLASSIC: the native client applies equipment effects separately, so packet maxima stay table-only.
            messages.Add(new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH { CharacterID = live.GameObjectID,
                NewHealth = after.m_hitpoints, NewHealthMax = after.m_hitpoints, DisplayDiff = 1 });
            messages.Add(new WIZARD_12_PROTOCOL.MSG_UPDATEMANA { Mana = after.m_mana, MaxMana = after.m_mana, DisplayDiff = 1 });
            messages.Add(new WIZARD_12_PROTOCOL.MSG_UPDATEPOWERPIP { PowerPip = after.m_pipChance });
            messages.Add(new PET_9_PROTOCOL.MSG_PETENERGYMAX { MaxEnergy = after.m_petEnergy });
        }
        saved.MagicSchoolBehavior.Level = level;
        var xpMessage = sendXp ? new WIZARD_12_PROTOCOL.MSG_UPDATEXP { GlobalID = live.GameObjectID, XP = applied, OldXP = oldXp } : null;
        try { if (messages.Any(message => !Prepare(message)) || xpMessage is not null && !Prepare(xpMessage)) return false; }
        catch (Exception) { return false; }
        receipt = new(oldXp, applied, oldLevel, level, adjustStats, refill, true, health, mana, pips, messages, xpMessage) {
            ManaTransition = manaTransition
        };
        return true;
    }

    internal static void ValidatePublication(Wizard live, ProgressionReceipt receipt)
        => CharacterEffectHelper.ValidateManaProgressionMaximum(live.GameStats, receipt.ManaTransition);

    internal static void Publish(Wizard live, Wizard saved, ProgressionReceipt receipt) {
        CharacterEffectHelper.PublishManaProgressionMaximum(live.GameStats, receipt.ManaTransition);
        live.MagicSchoolBehavior.ExperiencePoints = saved.MagicSchoolBehavior.ExperiencePoints;
        live.MagicSchoolBehavior.Level = saved.MagicSchoolBehavior.Level;
        live.GameStats.Level = saved.MagicSchoolBehavior.Level;
        if (receipt.AdjustStats) {
            live.GameStats.m_baseHitpoints = saved.GameStats.m_baseHitpoints;
            live.GameStats.m_baseMana = saved.GameStats.m_baseMana;
            live.GameStats.m_powerPipBase = receipt.PowerPips;
        }
        if (!receipt.Refill) return;
        live.GameStats.m_currentHitpoints = saved.GameStats.m_currentHitpoints;
        live.GameStats.m_currentMana = saved.GameStats.m_currentMana;
        live.PetOwnerBehavior.PublishCommittedEnergy(saved.PetOwnerBehavior);
    }

    internal static bool Usable(Wizard live)
        => live is not null && live.CharId != 0 && !WizardCollection.IsInventorySnapshotUncertain(live);

    // CLASSIC: runtime offsets are trusted only with the same saved level/school and equipment context.
    // Equip/unequip, elixir effects and health/mana mutations hold this same lane through their runtime change.
    internal static bool RuntimeContextMatches(Wizard live, Wizard saved) {
        if (!RuntimeIdentityMatches(live, saved)) return false;
        var a = live.EquipmentBehavior; var b = saved.EquipmentBehavior;
        if (a is null || b is null) return a is null && b is null && RuntimeElixirEffectsMatch(live);
        return (a.EquippedItemIds ?? []).OrderBy(id => id).SequenceEqual((b.EquippedItemIds ?? []).OrderBy(id => id))
            && (a.SlotList ?? []).Select(slot => (ItemId: slot.ItemId.Full, slot.SlotType)).OrderBy(slot => slot.ItemId).ThenBy(slot => slot.SlotType)
                .SequenceEqual((b.SlotList ?? []).Select(slot => (ItemId: slot.ItemId.Full, slot.SlotType)).OrderBy(slot => slot.ItemId).ThenBy(slot => slot.SlotType))
            && RuntimeElixirEffectsMatch(live);
    }

    private static bool RuntimeIdentityMatches(Wizard live, Wizard saved) {
        return Usable(live) && live.HasInitializedRuntimeStats && saved?.CharId == live.CharId && live.GameStats is not null && saved.GameStats is not null
            && live.MagicSchoolBehavior is not null && saved.MagicSchoolBehavior is not null
            && live.MagicSchoolBehavior.Level == saved.MagicSchoolBehavior.Level
            && live.MagicSchoolBehavior.MagicSchool == saved.MagicSchoolBehavior.MagicSchool
            && live.GameStats.MagicSchool == saved.MagicSchoolBehavior.MagicSchool
            && live.GameStats.Level == live.MagicSchoolBehavior.Level;
    }

    // CLASSIC: activation/expiry publish item references before the separate effect service catches up.
    // Refuse that transition rather than freezing an uninitialized or already expired bonus into a refill.
    private static bool RuntimeElixirEffectsMatch(Wizard live) {
        var slotHash = StringHash.Compute(ElixirRules.SlotName);
        var effects = live.GameEffects.Snapshot().Where(effect => effect.m_itemSlotID == slotHash).ToArray();
        var ids = (live.EquipmentBehavior?.SlotList ?? []).Where(slot => slot.SlotType == EquipmentSlotType.Elixir)
            .Select(slot => (ulong)slot.ItemId).ToArray();
        if (ids.Distinct().Count() != ids.Length || effects.Any(effect => !ids.Contains(effect.m_originatorID))) return false;
        foreach (var id in ids) {
            var items = (live.EquipmentBehavior.EquippedItems ?? []).Where(item => item.m_globalID == id).ToArray();
            if (items.Length != 1 || items[0].m_characterId != live.CharId) return false;
            var item = items[0];
            var timers = item.m_inactiveBehaviors?.OfType<ClientElixirBehavior>().ToArray();
            if (timers is not { Length: 1 }) return false;
            var definition = item.m_templateID.Full < (1UL << 28) ? ElixirRules.Approved((uint)item.m_templateID.Full) : null;
            var enabled = definition is not null && ElixirRules.CanActivate(live, definition)
                && ElixirRuntime.CanApplyEffects(live, item, ItemHelper.GetItemTemplate(item),
                    pvp: false, inCombat: live.IsInDuel);
            var applied = effects.Where(effect => effect.m_originatorID == id).ToArray();
            if (timers[0].m_statsApplied != enabled || applied.Length != (enabled ? definition.Effects.Count : 0)) return false;
            if (enabled && !applied.Select(effect => effect.m_effectNameID).OrderBy(hash => hash)
                .SequenceEqual(definition.Effects.Select(effect => StringHash.Compute(effect.Name)).OrderBy(hash => hash))) return false;
        }
        return true;
    }
}
