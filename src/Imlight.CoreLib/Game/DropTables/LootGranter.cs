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
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
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
    private const uint INVENTORY_ADD_SERIALIZATION_FLAGS =
        (uint) (PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit);
    private const uint REAGENT_ADD_SERIALIZATION_FLAGS = 27;   // CLASSIC: as CommandModifyProtocol's addreagent
    private static readonly CoreObjectSerializer s_itemSerializer = new(behaviors: SerializerFlags.None);

    /// <summary>
    /// Grants every reward in <paramref name="results"/> to the player and shows the loot popup.
    /// </summary>
    /// <param name="playerActor">The player's SessionActor, which routes the messages.</param>
    /// <param name="wizard">The player wizard data receiving the rewards.</param>
    /// <param name="results">The rolled drop table results to grant.</param>
    public static void GrantAndDisplay(IActorRef playerActor, Wizard wizard, DropTableResult results) {
        // CLASSIC: [Classic] GoldMultiplier and XpMultiplier (dashboard switches; 2009: 1) scale the gold and XP of mob
        // and quest rewards before they are granted and shown.
        results.GoldAmount = Classic.ClassicSettings.Scale(results.GoldAmount, Classic.ClassicSettings.GoldMultiplier);
        results.ExperienceAmount = Classic.ClassicSettings.Scale(results.ExperienceAmount, Classic.ClassicSettings.XpMultiplier);
        UpdateWizardGold(playerActor, wizard, results.GoldAmount);
        UpdateWizardXP(playerActor, results.ExperienceAmount);
        UpdateWizardTP(playerActor, wizard, results.TrainingPoints);
        UpdateCharacterItems(playerActor, wizard, results.Items);
        UpdateTreasureCards(playerActor, wizard, results);   // CLASSIC
        UpdateReagents(playerActor, wizard, results.Reagents);   // CLASSIC
        SendLootInfoToClient(playerActor, results, wizard);

        if (results.GrantsPotionSlot) {
            UpdateWizardPotionMax(playerActor, wizard);
        }
    }

    /// <summary>CLASSIC: adds one treasure card to the wizard's treasure book (a Bazaar purchase).</summary>
    internal static void GrantTreasureCard(IActorRef playerActor, Wizard wizard, uint templateId)
        => UpdateTreasureCards(playerActor, wizard, new DropTableResult { TreasureCards = [templateId] });

    /// <summary>CLASSIC: adds reagents to the wizard's reagent bag (a Bazaar purchase).</summary>
    internal static void GrantReagent(IActorRef playerActor, Wizard wizard, ulong templateId, int quantity)
        => UpdateReagents(playerActor, wizard, [new DropItemResult { ItemId = templateId.ToString(), ItemName = string.Empty, Quantity = quantity }]);

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
        playerActor.Tell(networkMessage);
    }

    private static void UpdateWizardXP(IActorRef playerActor, int xpDelta) {
        if (xpDelta == 0) {
            return;
        }

        // XP is simple and we only need to inform the SessionActor.
        var internalMsg = new CHARACTER_103_PROTOCOL.MSG_GAINXP {
            XP = xpDelta
        };
        playerActor.Tell(internalMsg);

        // That's it. The SessionActor will inform the game client, and level them up if needed.
    }

    private static void UpdateWizardTP(IActorRef playerActor, Wizard wizard, int tpDelta) {
        if (tpDelta == 0) {
            return;
        }

        // Set the new amount of training points for the wizard.
        var oldTP = wizard.MagicSchoolBehavior.TrainingPoints;
        var newTP = Math.Max(0, oldTP + tpDelta);
        if (newTP == oldTP) {
            return;
        }

        wizard.UpdateTrainingPoints(newTP);

        // Inform the game client of the new TP amount.
        var msg = new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING() {
            TrainingPoints = (ushort) newTP
        };
        playerActor.Tell(msg);
    }

    private static void UpdateCharacterItems(IActorRef playerActor, Wizard wizard, List<DropItemResult> items) {
        if (items.Count == 0) {
            return;
        }

        // Add each item to the wizard's inventory.
        foreach (var item in items) {
            if (!ulong.TryParse(item.ItemId, out var itemGuid)) {
                continue;
            }

            if (!wizard.AddItemToInventory(itemGuid, out var addedItem)) {
                Logger.Error("Failed to add item {0} to wizard {1}'s inventory.",
                    Logger.Args(item.ItemId, wizard.CharId));

                // CLASSIC: tell the player why a reward is missing; with a full backpack it used to vanish silently.
                if (wizard.InventoryBehavior?.IsFull == true) {
                    playerActor.Tell(Classic.ClassicChat.Line(
                        "Your backpack is full, so a reward item could not be added. Make room and try again later."));
                }

                continue;
            }

            // The attach payload (which carries the inventory) was already sent, so push each
            // item to the client explicitly or the reward stays invisible this session.
            SendInventoryAdd(playerActor, wizard, addedItem);
        }
    }

    // CLASSIC: adds dropped Treasure Cards to the treasure book the way a Bazaar/vendor purchase does
    // (TreasureShopService): the client learns the card by its name hash, the save keeps the spell template.
    private static void UpdateTreasureCards(IActorRef playerActor, Wizard wizard, DropTableResult results) {
        results.TreasureCardSpellIds.Clear();
        foreach (var templateId in results.TreasureCards) {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is not SpellTemplate spell) {
                Logger.Warning("Treasure Card drop {0} is not a spell template.", Logger.Args(templateId));

                continue;
            }

            var spellHash = StringHash.Compute(spell.m_name);
            playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK {
                SpellID = (int) spellHash,
                EnchantmentID = 0,
            });

            wizard.SpellbookBehavior.AddTreasureCard(templateId);
            WizardCollection.AddTreasureCard(wizard, templateId);
            results.TreasureCardSpellIds.Add(spellHash);
        }
    }

    // CLASSIC: adds dropped reagents to the reagent bag the way the addreagent command does.
    private static void UpdateReagents(IActorRef playerActor, Wizard wizard, List<DropItemResult> reagents) {
        foreach (var reagent in reagents) {
            if (!ulong.TryParse(reagent.ItemId, out var templateId)
                || CoreObjectFactory.GetCoreTemplate(templateId) is not ReagentItemTemplate) {
                continue;
            }

            ClientReagentItem added = null;
            for (var i = 0; i < Math.Max(1, reagent.Quantity); i++) {
                if (!wizard.AddReagent(templateId, out var reagentObj)) {
                    Logger.Error("Failed to add reagent {0} to wizard {1}'s reagent bag.",
                        Logger.Args(templateId, wizard.CharId));

                    break;
                }

                added = reagentObj;
            }

            if (added is null || !s_itemSerializer.Serialize(added, REAGENT_ADD_SERIALIZATION_FLAGS, out var serializedReagent)) {
                continue;
            }

            playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_REAGENTADD {
                GlobalID = wizard.GameObjectID,
                Data = serializedReagent,
            });
            playerActor.Tell(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
                ItemGlobalID = added.m_globalID,
                ItemTemplateID = (uint) added.m_templateID,
                ItemLocation = 1,
            });
        }
    }

    private static void SendInventoryAdd(IActorRef playerActor, Wizard wizard, WizClientObjectItem item) {
        if (!s_itemSerializer.Serialize(item, INVENTORY_ADD_SERIALIZATION_FLAGS, out var serializedItem)) {
            Logger.Error("Failed to serialize reward item {0} for inventory-add.",
                Logger.Args(item.m_globalID.Full));

            return;
        }

        playerActor.Tell(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
            GlobalID = wizard.GameObjectID,
            SerializedItem = serializedItem,
        });
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
        playerActor.Tell(potionChargeUpdateMsg);
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

        playerActor.Tell(lootMsg);
    }

}
