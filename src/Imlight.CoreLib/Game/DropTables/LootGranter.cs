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
 * DROP TABLES
 * ========================================================================
 * 
 * PURPOSE:
 * Grants a rolled DropTableResult to a player (gold, XP, training points,
 * items, potion slot) and shows the loot popup. Shared by the quest reward
 * path (ResDropTableHandler) and the mob-defeat path (CombatService).
 * 
 * USAGE EXAMPLE:
 * Both reward paths roll their drop tables and hand the result to
 * LootGranter.GrantAndDisplay.
 * 
 * NOTE:
 * All sends go to the player's SessionActor, which routes them to the right
 * service or socket. One implementation means both paths share the same
 * proven grant behavior.
 * 
 * TODO:
 * 
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 08/22/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.DropTables;

/// <summary>
/// Grants a rolled drop table result to a player and shows the loot popup.
/// </summary>
public static class LootGranter {

    private const uint LOOT_LIST_SERIALIZATION_FLAGS = 4;
    // CLASSIC: capture the actual grant-path packets without creating a session or contacting a game server.
    internal static readonly AsyncLocal<Action<IActorRef, object>> TestSendScope = new();
    private static void Send(IActorRef playerActor, object message) {
        if (TestSendScope.Value is { } send) send(playerActor, message);
        else playerActor.Tell(message);
    }

    /// <summary>
    /// Grants every reward in <paramref name="results"/> to the player and shows the loot popup.
    /// </summary>
    /// <param name="playerActor">The player's SessionActor, which routes the messages.</param>
    /// <param name="wizard">The player wizard data receiving the rewards.</param>
    /// <param name="results">The rolled drop table results to grant.</param>
    public static void GrantAndDisplay(IActorRef playerActor, Wizard wizard, DropTableResult results)
        => Grant(playerActor, wizard, results, showPopup: true);

    /// <summary>
    /// Grants every reward in <paramref name="results"/>; the loot popup only when <paramref name="showPopup"/> (a
    /// Second Chance chest shows its rewards in its own window). CLASSIC.
    /// </summary>
    public static void Grant(IActorRef playerActor, Wizard wizard, DropTableResult results, bool showPopup) {
        // CLASSIC: [Classic] GoldMultiplier and XpMultiplier (dashboard switches; 2009: 1) scale the gold and XP of mob
        // and quest rewards before they are granted and shown.
        results.GoldAmount = Classic.ClassicSettings.Scale(results.GoldAmount, Classic.ClassicSettings.GoldMultiplier);
        results.ExperienceAmount = Classic.ClassicSettings.Scale(results.ExperienceAmount, Classic.ClassicSettings.XpMultiplier);
        UpdateWizardGold(playerActor, wizard, results.GoldAmount);
        UpdateWizardXP(playerActor, results.ExperienceAmount);
        UpdateWizardTP(playerActor, wizard, results.TrainingPoints);
        UpdateStackRewards(playerActor, wizard, results);   // CLASSIC: one acknowledged gear/card/reagent batch
        if (showPopup) {
            SendLootInfoToClient(playerActor, results, wizard);
        }

        if (results.GrantsPotionSlot) {
            UpdateWizardPotionMax(playerActor, wizard);
        }
    }

    /// <summary>CLASSIC: adds one treasure card to the wizard's treasure book (a Bazaar purchase).</summary>
    internal static void GrantTreasureCard(IActorRef playerActor, Wizard wizard, uint templateId)
        => UpdateStackRewards(playerActor, wizard, new DropTableResult { TreasureCards = [templateId] });

    /// <summary>CLASSIC: adds reagents to the wizard's reagent bag (a Bazaar purchase).</summary>
    internal static void GrantReagent(IActorRef playerActor, Wizard wizard, ulong templateId, int quantity)
        => UpdateStackRewards(playerActor, wizard, new DropTableResult {
            Reagents = [new DropItemResult { ItemId = templateId.ToString(), ItemName = string.Empty, Quantity = quantity }]
        });

    private static void UpdateWizardGold(IActorRef playerActor, Wizard wizard, int goldDelta) {
        if (goldDelta == 0) {
            return;
        }

        // Add gold to the wizard. This will save their data, but not inform their game client.
        wizard.AddGold(goldDelta);

        // Now, inform the game client their gold has been updated.
        // This only changes the character page. It does not show a popup or anything.
        // The popup comes from the network LootInfoList.
        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD() {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        };
        Send(playerActor, networkMessage);
    }

    private static void UpdateWizardXP(IActorRef playerActor, int xpDelta) {
        if (xpDelta == 0) {
            return;
        }

        // XP is simple and we only need to inform the SessionActor.
        var internalMsg = new CHARACTER_103_PROTOCOL.MSG_GAINXP {
            XP = xpDelta
        };
        Send(playerActor, internalMsg);

        // That's it. The SessionActor will inform the game client, and level them up if needed.
    }

    private static void UpdateWizardTP(IActorRef playerActor, Wizard wizard, int tpDelta) {
        if (tpDelta == 0) {
            return;
        }

        // CLASSIC: a change of the saved count (WizardCollection.ChangeTrainingPoints), not a write of this actor's read
        // of the live count: training runs on another actor and the two used to overwrite each other.
        if (!WizardData.Collections.WizardCollection.ChangeTrainingPoints(wizard, tpDelta)) {
            return;
        }

        var newTP = wizard.MagicSchoolBehavior.TrainingPoints;

        // Inform the game client of the new TP amount.
        var msg = new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING() {
            TrainingPoints = (ushort) newTP
        };
        Send(playerActor, msg);
    }

    // CLASSIC: native packets, popup counts and Second Chance results follow the same saved receipts.
    private static void UpdateStackRewards(IActorRef playerActor, Wizard wizard, DropTableResult results) {
        StackRewardReceipt receipt;
        try {
            ClassicStackRewards.TryGrant(wizard, results.Items, results.TreasureCards, results.Reagents, out receipt);
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) Send(playerActor, "Close");
            throw; // no automatic retry/refund of a write that may already have committed
        }

        var names = results.Reagents.Where(drop => drop is not null && ulong.TryParse(drop.ItemId, out _))
            .GroupBy(drop => ulong.Parse(drop.ItemId)).ToDictionary(group => group.Key, group => group.First().ItemName);
        var itemNames = results.Items.Where(drop => drop is not null && ulong.TryParse(drop.ItemId, out _))
            .GroupBy(drop => ulong.Parse(drop.ItemId)).ToDictionary(group => group.Key, group => group.First().ItemName);
        results.Items = receipt.Items.Select(acquired => new DropItemResult {
            ItemId = acquired.Item.m_templateID.Full.ToString(),
            ItemName = itemNames.GetValueOrDefault(acquired.Item.m_templateID.Full, string.Empty), Quantity = 1,
        }).ToList();
        results.TreasureCards = receipt.Cards.Select(card => card.TemplateId).ToList();
        results.TreasureCardSpellIds = receipt.Cards.Select(card => card.SpellHash).ToList();
        results.Reagents = receipt.Reagents.Select(acquired => new DropItemResult {
            ItemId = acquired.Reagent.m_templateID.Full.ToString(),
            ItemName = names.GetValueOrDefault(acquired.Reagent.m_templateID.Full, string.Empty),
            Quantity = acquired.Acquired,
        }).ToList();

        foreach (var acquired in receipt.Items) Send(playerActor, new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
            GlobalID = wizard.GameObjectID, SerializedItem = acquired.Data,
        });
        foreach (var card in receipt.Cards) Send(playerActor, new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK {
            SpellID = (int) card.SpellHash, EnchantmentID = 0,
        });
        foreach (var acquired in receipt.Reagents) {
            Send(playerActor, new WIZARD_12_PROTOCOL.MSG_REAGENTADD {
                GlobalID = wizard.GameObjectID,
                Data = acquired.Data,
            });
            Send(playerActor, new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
                ItemGlobalID = acquired.Reagent.m_globalID,
                ItemTemplateID = (uint) acquired.Reagent.m_templateID,
                ItemLocation = 1,
            });
        }
        if (receipt.BackpackCapacityExceeded) Send(playerActor, Classic.ClassicChat.Line(
            "Your backpack is full, so a reward item could not be added. Make room and try again later."));
    }

    private static void UpdateWizardPotionMax(IActorRef playerActor, Wizard wizard) {
        var currentWizardMaxPots = wizard.GameStats.m_potionMax;
        var newWizardMaxPots = currentWizardMaxPots + 1;

        wizard.UpdatePotions(newWizardMaxPots, newWizardMaxPots);

        // Inform the player's game client that their potion charge has been updated.
        var potionChargeUpdateMsg = new WIZARD_12_PROTOCOL.MSG_UPDATEPOTIONS {
            PotionMax = newWizardMaxPots,
            PotionCharge = newWizardMaxPots
        };
        Send(playerActor, potionChargeUpdateMsg);
    }

    private static void SendLootInfoToClient(IActorRef playerActor, DropTableResult results, Wizard wizard) {
        // Inform the game client of the loot results.
        if (!DropTableConverter.HasRewards(results)) {
            return;
        }

        // Convert the loot results into something we can send over the network.
        var networkLootList = DropTableConverter.ToLootInfoList(results);
        var serializer = new ObjectSerializer(Versionable: false);
        if (!serializer.Serialize(networkLootList, LOOT_LIST_SERIALIZATION_FLAGS, out var serializedLootList)) {
            Logger.Error("Failed to serialize loot list for network transmission.");

            return;
        }

        var lootMsg = new WIZARD_12_PROTOCOL.MSG_LOOT() {
            GlobalID = wizard.GameObjectID,
            LootList = serializedLootList
        };

        Send(playerActor, lootMsg);
    }

}
