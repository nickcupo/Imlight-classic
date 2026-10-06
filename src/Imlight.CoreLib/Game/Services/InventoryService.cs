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
 * INVENTORY SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages player inventory interactions, including item management, 
 * quick selling, and special item handling.
 * 
 * USAGE EXAMPLE:
 * Internal service handling various inventory-related messages within 
 * the game server session.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty, Joji
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System;
using System.Linq;
using Imlight.Classic.Inventory;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Elixirs;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Utilities;

namespace Imlight.CoreLib.Game.Services;

internal class InventoryService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new InventoryService(parentActor));

    #region Destroy/Feed Inventoryitem

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_TRASHINVENTORYITEM))]
    private void ReceiveTrashInventoryItem(GAME_5_PROTOCOL.MSG_TRASHINVENTORYITEM message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        // CLASSIC: the native confirmed active-elixir dismissal reuses this message, but
        // has no backpack item. Only an exact approved active original may take this path;
        // ordinary equipped gear and forged TemplateID values remain undiscardable here.
        if (wizard.EquipmentBehavior?.GetItem(message.GlobalID)?.m_inactiveBehaviors?
            .Any(behavior => behavior is ClientElixirBehavior) == true) {
            var result = ElixirCollection.Cancel(wizard, message.GlobalID, message.TemplateID);
            if (result.Saved) {
                foreach (var cleanup in ElixirService.ExpireCommitted(wizard, result, true)) SendToSocket(cleanup);
            }
            else Logger.Information("Active elixir dismissal of item {0} by {1} refused.", Logger.Args(message.GlobalID, wizard.CharId));
            return;
        }

        // CLASSIC: an item the game will not let you throw away (the template's FLAG_NoDrop) stays;
        // the client knows the flag (GUI_NoDrop), so this guards against a crafted message. A trashed item's document
        // is deleted with it.
        var item = wizard.InventoryBehavior.GetItem(message.GlobalID);
        if (item is null || IsNoDrop(item) || !wizard.DestroyInventoryItem(message.GlobalID)) {
            Logger.Information("Trash of item {0} by {1} refused (not in the backpack, or no-discard).",
                Logger.Args(message.GlobalID, wizard.CharId));

            return;
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM() {
            GlobalID = wizard.GameObjectID,
            ItemID = message.GlobalID
        });
    }

    // CLASSIC: the template's no-discard flag (the client's own FLAG_NoDrop).
    internal static bool IsNoDrop(WizClientObjectItem item)
        => CoreObjectFactory.GetCoreTemplate(item.m_templateID) is WizItemTemplate template
           && template.m_adjectiveList?.Exists(adjective => string.Equals(adjective, "FLAG_NoDrop", StringComparison.OrdinalIgnoreCase)) == true;

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_FEEDINVENTORYITEM))]
    private void ReceiveFeedInventoryItem(GAME_5_PROTOCOL.MSG_FEEDINVENTORYITEM message) {
        SendToSocket(new GAME_5_PROTOCOL.MSG_FEEDINVENTORYITEM() {
            FedObjectID = message.FedObjectID,
            PetID = message.PetID,
        });
    }

    #endregion

    #region Quicksell from Inventory

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_REQUESTQUICKSELL))]
    private void ReceiveRequestQuickSell(WIZARD_12_PROTOCOL.MSG_REQUESTQUICKSELL message) {
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_REQUESTQUICKSELL() {
            FromTemplateID = message.FromTemplateID,
            Section = message.Section,
            SellModifier = message.SellModifier + 0.05f, // (?) Live server uses ~0.05f.
        });
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_QUICKSELLREQUEST))]
    private void ReceiveQuickSellRequest(WIZARD2_53_PROTOCOL.MSG_QUICKSELLREQUEST message) {
        var serializer = new ObjectSerializer(
            Behaviors: SerializerFlags.None
        );

        var wizard = GetActiveWizard();
        int goldSum = 0;

        if (!serializer.Deserialize<QuickSellItemList>(message.Data, 4, out var quickSellItemList)) {
            Logger.Log.Error("Failed to deserialize quicksell item list.");

            return;
        }

        // CLASSIC: only successfully removed, uniquely owned backpack objects earn gold.
        if (ClassicRuntime.Rules.UsesKingsIsleQuestRules) {
            var requests = quickSellItemList.m_quickSellItemList?
                .Where(item => item is not null)
                .Select(item => new BackpackQuickSell.Request(item.m_sellItemGID, item.m_quantity))
                ?? Enumerable.Empty<BackpackQuickSell.Request>();
            var sales = BackpackQuickSell.Execute(requests, id => {
                var item = wizard.InventoryBehavior.GetItem(id);
                if (item is null || CoreObjectFactory.GetCoreTemplate(item.m_templateID) is not WizItemTemplate template
                    || !BackpackQuickSell.IsSellable(template.m_adjectiveList)) return null;
                var value = Math.Ceiling(template.m_baseCost * 0.05f);
                if (template.m_numPrimaryColors != 1 && template.m_numSecondaryColors != 0)
                    value = Math.Ceiling(value * 1.2275f);
                return value;
            }, wizard.DestroyInventoryItem);
            foreach (var sale in sales)
                SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM {
                    GlobalID = wizard.GameObjectID, ItemID = sale.Id
                });
            var applied = BackpackQuickSell.GoldToApply(sales, wizard.GameStats.m_currentGold, wizard.GameStats.m_baseGoldPouch);
            if (applied > 0) wizard.AddGold(applied);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
                Gold = wizard.GameStats.m_currentGold, MaxGold = wizard.GameStats.m_baseGoldPouch
            });
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_QUICKSELLREQUEST());
            return;
        }

        // Remove items from inventory and equipment, tally up gold sum.
        foreach (QuickSellItem quickSellItem in quickSellItemList.m_quickSellItemList) {
            var item = wizard.InventoryBehavior.GetItem(quickSellItem.m_sellItemGID);
            var template = (WizItemTemplate) CoreObjectFactory.GetCoreTemplate(item.m_templateID);

            if (!wizard.DestroyInventoryItem(item.m_globalID)) continue; // CLASSIC: only what really left the backpack pays

            // Some items (snack, reagents) are stackable.
            for (int i = 0; i < quickSellItem.m_quantity; i++) {
                SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM() {
                    GlobalID = wizard.GameObjectID,
                    ItemID = quickSellItem.m_sellItemGID
                });

                var value = (int) Math.Ceiling(template.m_baseCost * 0.05f);
                if (template.m_numPrimaryColors != 1 && template.m_numSecondaryColors != 0) {
                    value = (int) Math.Ceiling(value * 1.2275f); // Dyed items are more expensive.
                }

                goldSum += value;
            }
        }

        // Update player with their new gold balance.
        wizard.AddGold(goldSum);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD() {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch,
        });

        // End quicksell process with empty message.
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_QUICKSELLREQUEST());
    }

    #endregion

    #region Jewels

    // JEWELS
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_EQUIPJEWELREQUEST))]
    private void ReceiveEquipJewelRequest(WIZARD2_53_PROTOCOL.MSG_EQUIPJEWELREQUEST message) {
        // CLASSIC: jewel sockets follow the profile's jewels switch.
        if (!ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.Jewels)) {
            ClassicGate.RefuseFeature(ClassicFeatures.Jewels, GetActiveWizard()?.CharId, InformGameClient);

            return;
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_EQUIPJEWELREQUEST() {
            ItemGID = message.ItemGID,
            JewelGID = message.JewelGID,
            SocketNumber = message.SocketNumber,
        });

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_EQUIPJEWELTOITEM() {
            ItemGID = message.ItemGID,
            JewelGID = message.JewelGID,
            SocketNumber = message.SocketNumber,
            GlobalID = RandomGen.GenerateGUID()
        });
    }

    #endregion

    #region Snacks

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETSNACKREMOVEREQUEST))]
    private void ReceivePetSnackRemoveRequest(PET_9_PROTOCOL.MSG_PETSNACKREMOVEREQUEST message) {
        var wizard = GetActiveWizard();

        var hasSnack = wizard.PetSnackBehavior.HasSnackID(message.GlobalID);
        if (!hasSnack) {
            Logger.Log.Debug("Tried to remove snack with global id {0} that does not exist in player snack bag.",
                Logger.Args(message.GlobalID));

            return;
        }

        wizard.RemoveSnack(message.GlobalID, out var updatedSnack);

        if (updatedSnack.m_quantity > 0) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKUPDATE() {
                GlobalID = wizard.GameObjectID,
                ItemID = updatedSnack.m_globalID,
                Quantity = updatedSnack.m_quantity
            });

            return;
        }

        SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKREMOVE() {
            GlobalID = wizard.GameObjectID,
            ItemID = updatedSnack.m_globalID,
        });
    }

    #endregion

    #region Reagents

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_REAGENTREMOVEREQUEST))]
    private void ReceiveReagentRemoveRequest(WIZARD_12_PROTOCOL.MSG_REAGENTREMOVEREQUEST message) {
        var wizard = GetActiveWizard();

        var hasReagent = wizard.AlchemyBehavior.HasReageant(message.GlobalID);
        if (!hasReagent) {
            Logger.Log.Debug("Tried to remove reagent with global id {0} that does not exist in player reagent bag.",
                Logger.Args(message.GlobalID));

            return;
        }

        var reagent = wizard.AlchemyBehavior.GetReagent(message.GlobalID);
        wizard.RemoveReagent(reagent.m_globalID, out var updatedReagent);

        if (updatedReagent.m_quantity > 0) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE() {
                GlobalID = wizard.GameObjectID,
                ItemID = updatedReagent.m_globalID,
                Quantity = updatedReagent.m_quantity
            });

            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTREMOVE() {
            GlobalID = wizard.GameObjectID,
            ItemID = updatedReagent.m_globalID,
        });
    }

    #endregion

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PLAYERWIZBANG))]
    private void ReceivePlayerWizbang(WIZARD_12_PROTOCOL.MSG_PLAYERWIZBANG message) {
        var wizard = GetActiveWizard();

        switch (message.StateName) {
            case "SpellbookWizbang":
                ZoneBroadcast(new GAME_5_PROTOCOL.MSG_WIZBANG() {
                    GameObjectID = wizard.GameObjectID,
                    WizBangID = StringHash.Compute("Registrar")
                }, false);
                break;
            default:
                ZoneBroadcast(new GAME_5_PROTOCOL.MSG_WIZBANG() {
                    GameObjectID = wizard.GameObjectID,
                    WizBangID = 0
                }, false);
                break;
        }
    }

}
