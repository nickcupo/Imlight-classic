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
 * MINIGAME REWARDS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: pays a finished minigame's rewards. Reaching the game's first score threshold refills mana (the first
 * reward slot, "mana refill (or potion, if your mana is full)", Feb 2009); what the mana globe cannot hold fills the
 * potion flasks (Health and Mana, oldid 41879). The amounts come from classic-data/rules/potions-2009.yaml.
 *
 * NOTE:
 * The r806919 client shows the mana as a ManaLootInfo in the reward window; the flasks change through
 * MSG_UPDATEPOTIONS (the HUD potion button), and mana and gold through MSG_UPDATEMANA and MSG_UPDATEGOLD.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Minigames;

internal static class MinigameRewards {

    /// <summary>How many of a game's score thresholds <paramref name="score"/> reached.</summary>
    public static int ThresholdsReached(int score, IReadOnlyList<int> thresholds)
        => thresholds is null ? 0 : thresholds.Count(threshold => score >= threshold);

    /// <summary>Pays the mana slot: mana first, the rest into the flasks. Adds the ManaLootInfo the window shows.</summary>
    public static PotionFill PayMana(IActorRef player, Wizard wizard, PotionRules rules, int thresholdsReached, LootInfoList loot) {
        if (thresholdsReached < 1 || wizard?.GameStats is not { } stats) {
            return default;
        }

        try {
            if (!WizardPotionTransactions.TryMinigameFill(wizard, rules, out var receipt)) return default;
            // CLASSIC: the historical window shows requested mana; append it only after the complete fill ACK.
            loot.m_loot.Add(new ManaLootInfo { m_lootType = LOOT_TYPE.LOOT_TYPE_MANA, m_manaAmount = receipt.ManaReward });
            foreach (var packet in receipt.Messages) player?.Tell(packet);
            return receipt.Fill;
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) player?.Tell("Close");
            throw;
        }
    }

    /// <summary>Pays the gold the reward window shows.</summary>
    public static void PayGold(IActorRef player, Wizard wizard, LootInfoList loot) {
        var gold = loot.m_goldInfo?.m_goldAmount ?? 0;
        if (gold <= 0 || wizard is null) {
            return;
        }

        wizard.AddGold(gold);
        player?.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold = wizard.GameStats.m_currentGold, MaxGold = wizard.GameStats.m_baseGoldPouch });
    }

}
