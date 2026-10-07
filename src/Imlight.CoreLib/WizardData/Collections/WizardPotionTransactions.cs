// CLASSIC: each potion operation saves its selected balance, payment and healing together.
using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.WizardData.Collections;

internal enum PotionChangeKind { Drink, Buy, AddSlot, Minigame, Set }
internal sealed record PotionReceipt(PotionChangeKind Kind, bool ShouldSave, bool HealthChanged, bool ManaChanged,
    bool PotionsChanged, bool GoldChanged, int Health, int Mana, float Charge, float Max, int Gold, int MaxGold,
    int Cost, int ManaReward, PotionFill Fill, IReadOnlyList<IMessage> Messages, bool NormalizedNumeric = false);

internal static class WizardPotionTransactions {
    internal static bool TryDrink(Wizard live, DateTime nowUtc, out PotionReceipt receipt)
        => Commit(live, (session, saved) => TryStageDrink(live, saved, nowUtc, out var staged, session) ? staged : null, out receipt);
    internal static bool TryBuy(Wizard live, bool fillAll, out PotionReceipt receipt, Func<int, int> price = null)
        => Commit(live, (_, saved) => TryStageBuy(live, saved, fillAll, out var staged, price) ? staged : null, out receipt);
    internal static bool TryAddSlotAndFill(Wizard live, out PotionReceipt receipt)
        => Commit(live, (_, saved) => TryStageAddSlotAndFill(live, saved, out var staged) ? staged : null, out receipt);
    internal static bool TryMinigameFill(Wizard live, PotionRules rules, out PotionReceipt receipt)
        => Commit(live, (session, saved) => TryStageMinigameFill(live, saved, rules, out var staged, session) ? staged : null, out receipt);
    internal static bool TrySetPotions(Wizard live, float charge, float max, out PotionReceipt receipt)
        => Commit(live, (_, saved) => TryStageSetPotions(live, saved, charge, max, out var staged) ? staged : null, out receipt);

    private static bool Commit(Wizard live, Func<IDocumentSession, Wizard, PotionReceipt> stage, out PotionReceipt receipt) {
        receipt = null;
        if (!WizardProgressionTransactions.Usable(live)) return false;
        PotionReceipt prepared = null;
        var committed = WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
            if (!WizardProgressionTransactions.Usable(live)) return false;
            prepared = stage(session, saved);
            if (prepared?.ShouldSave != true) return false;
            WizardInventoryTransactions.ProtectUnmodifiedRows(session);
            return true;
        }, saved => Publish(live, saved, prepared),
            onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        if (committed || prepared is { ShouldSave: false }) { receipt = prepared; return true; }
        return false;
    }

    private static bool Valid(Wizard live, Wizard saved)
        => WizardProgressionTransactions.Usable(live) && saved?.CharId == live.CharId
            && live.GameStats is not null && saved.GameStats is not null;

    // CLASSIC: these staging methods also support a future compound quest transaction without nested saves.
    internal static bool TryStageDrink(Wizard live, Wizard saved, DateTime nowUtc, out PotionReceipt receipt,
        IDocumentSession session = null) {
        receipt = null;
        if (!Valid(live, saved) || !WizardProgressionTransactions.TryNormalizeAttachedRuntime(live, saved, session, out var normalization)
            || !WizardProgressionTransactions.RuntimeContextMatches(live, saved)
            || !PotionService.MayDrinkNow(live, nowUtc)) return false;
        var stats = saved.GameStats; var runtime = live.GameStats;
        var healthChanged = runtime.m_currentHitpoints < runtime.m_baseHitpoints;
        var manaChanged = runtime.m_currentMana < runtime.m_baseMana;
        List<IMessage> messages = [new WIZARD_12_PROTOCOL.MSG_USEPOTION()];
        var changes = !(stats.m_potionCharge < 1.0f) && (healthChanged || manaChanged);
        if (changes) {
            var table = WizardProgressionTransactions.LevelInfo(saved.MagicSchoolBehavior.MagicSchool, saved.MagicSchoolBehavior.Level);
            if (table is null) return false;
            if (healthChanged) {
                stats.m_currentHitpoints = runtime.m_baseHitpoints;
                messages.Add(new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH { CharacterID = live.GameObjectID,
                    NewHealth = stats.m_currentHitpoints, NewHealthMax = table.m_hitpoints, DisplayDiff = 1 });
            }
            if (manaChanged) {
                stats.m_currentMana = runtime.m_baseMana;
                messages.Add(new WIZARD_12_PROTOCOL.MSG_UPDATEMANA { Mana = stats.m_currentMana,
                    MaxMana = table.m_mana, DisplayDiff = 1 });
            }
            stats.m_potionCharge -= 1.0f;
            messages.Add(PotionMessage(stats.m_potionCharge, stats.m_potionMax));
        }
        return Prepared(new(PotionChangeKind.Drink, changes || normalization.Changed, changes && healthChanged, changes && manaChanged,
            changes, false, stats.m_currentHitpoints, stats.m_currentMana, stats.m_potionCharge, stats.m_potionMax,
            stats.m_currentGold, stats.m_baseGoldPouch, 0, 0, default, messages, normalization.NumericChanged), out receipt);
    }

    internal static bool TryStageBuy(Wizard live, Wizard saved, bool fillAll, out PotionReceipt receipt,
        Func<int, int> price = null) {
        receipt = null;
        if (!Valid(live, saved) || saved.MagicSchoolBehavior is null) return false;
        var stats = saved.GameStats;
        var missing = (int)Math.Ceiling(stats.m_potionMax - stats.m_potionCharge);
        var changes = missing > 0;
        var cost = 0;
        List<IMessage> messages = [];
        if (changes) {
            var fill = fillAll ? missing : 1;
            var level = Math.Max(1, WizardProgressionTransactions.AttachedLevel(saved.MagicSchoolBehavior.Level));
            cost = (price?.Invoke(level) ?? PotionCostPerBottle(level)) * fill;
            if (cost < 0 || stats.m_currentGold < cost) return false;
            stats.m_currentGold -= cost;
            stats.m_potionCharge = Math.Min(stats.m_potionMax, stats.m_potionCharge + fill);
            messages.Add(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold = stats.m_currentGold, MaxGold = stats.m_baseGoldPouch });
            messages.Add(PotionMessage(stats.m_potionCharge, stats.m_potionMax));
        }
        messages.Add(new WIZARD_12_PROTOCOL.MSG_POTIONBUYCONFIRM { Failure = 0 });
        return Prepared(new(PotionChangeKind.Buy, changes, false, false, changes, changes,
            stats.m_currentHitpoints, stats.m_currentMana, stats.m_potionCharge, stats.m_potionMax,
            stats.m_currentGold, stats.m_baseGoldPouch, cost, 0, default, messages), out receipt);
    }

    internal static bool TryStageAddSlotAndFill(Wizard live, Wizard saved, out PotionReceipt receipt) {
        receipt = null;
        if (!Valid(live, saved)) return false;
        var max = saved.GameStats.m_potionMax + 1;
        return StageSet(live, saved, max, max, PotionChangeKind.AddSlot, out receipt);
    }

    internal static bool TryStageSetPotions(Wizard live, Wizard saved, float charge, float max, out PotionReceipt receipt)
        => StageSet(live, saved, charge, max, PotionChangeKind.Set, out receipt);

    private static bool StageSet(Wizard live, Wizard saved, float charge, float max, PotionChangeKind kind,
        out PotionReceipt receipt) {
        receipt = null;
        if (!Valid(live, saved)) return false;
        var stats = saved.GameStats;
        stats.m_potionCharge = charge; stats.m_potionMax = max;
        return Prepared(new(kind, true, false, false, true, false, stats.m_currentHitpoints, stats.m_currentMana,
            charge, max, stats.m_currentGold, stats.m_baseGoldPouch, 0, 0, default, [PotionMessage(charge, max)]), out receipt);
    }

    internal static bool TryStageMinigameFill(Wizard live, Wizard saved, PotionRules rules, out PotionReceipt receipt,
        IDocumentSession session = null) {
        receipt = null;
        if (rules is null || !Valid(live, saved)
            || !WizardProgressionTransactions.TryNormalizeAttachedRuntime(live, saved, session, out var normalization)
            || !WizardProgressionTransactions.RuntimeContextMatches(live, saved)) return false;
        var stats = saved.GameStats;
        var reward = rules.MinigameMana(live.GameStats.m_baseMana);
        var fill = rules.Fill(reward, live.GameStats.m_currentMana, live.GameStats.m_baseMana, stats.m_potionCharge, stats.m_potionMax);
        List<IMessage> messages = [];
        if (fill.ManaGained > 0) {
            var table = WizardProgressionTransactions.LevelInfo(saved.MagicSchoolBehavior.MagicSchool, saved.MagicSchoolBehavior.Level);
            if (table is null) return false;
            stats.m_currentMana = fill.Mana;
            messages.Add(new WIZARD_12_PROTOCOL.MSG_UPDATEMANA { Mana = fill.Mana, MaxMana = table.m_mana, DisplayDiff = 1 });
        }
        if (fill.FlasksGained > 0) {
            stats.m_potionCharge = fill.Flasks;
            messages.Add(PotionMessage(fill.Flasks, stats.m_potionMax));
        }
        return Prepared(new(PotionChangeKind.Minigame, normalization.Changed || fill.ManaGained > 0 || fill.FlasksGained > 0, false,
            fill.ManaGained > 0, fill.FlasksGained > 0, false, stats.m_currentHitpoints, stats.m_currentMana,
            stats.m_potionCharge, stats.m_potionMax, stats.m_currentGold, stats.m_baseGoldPouch, 0, reward, fill, messages,
            normalization.NumericChanged), out receipt);
    }

    private static bool Prepared(PotionReceipt prepared, out PotionReceipt receipt) {
        receipt = null;
        try { if (prepared.Messages.Any(message => !WizardProgressionTransactions.Prepare(message))) return false; }
        catch (Exception) { return false; }
        receipt = prepared; return true;
    }

    private static WIZARD_12_PROTOCOL.MSG_UPDATEPOTIONS PotionMessage(float charge, float max)
        => new() { PotionMax = max, PotionCharge = charge };

    internal static void Publish(Wizard live, Wizard saved, PotionReceipt receipt) {
        var stats = live.GameStats; var committed = saved.GameStats;
        if (receipt.HealthChanged) stats.m_currentHitpoints = committed.m_currentHitpoints;
        if (receipt.ManaChanged) stats.m_currentMana = committed.m_currentMana;
        if (receipt.PotionsChanged) { stats.m_potionCharge = committed.m_potionCharge; stats.m_potionMax = committed.m_potionMax; }
        if (receipt.GoldChanged) stats.m_currentGold = committed.m_currentGold;
        if (receipt.NormalizedNumeric) {
            live.MagicSchoolBehavior.Level = saved.MagicSchoolBehavior.Level;
            live.MagicSchoolBehavior.ExperiencePoints = saved.MagicSchoolBehavior.ExperiencePoints;
            stats.Level = saved.MagicSchoolBehavior.Level;
        }
    }

    // CLASSIC: retain the current profile price and stock fallback, with no new AmountEnum interpretation.
    internal static int PotionCostPerBottle(int level)
        => Classic.ClassicProgression.Potions is { } potions ? potions.ShopPrice(level)
         : level < 11 ? 100 : level < 21 ? level * 10 : level < 31 ? level * 15 : level < 41 ? level * 20 : level * 30;
}
