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
using System.Collections.Generic;
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
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class InventoryService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new InventoryService(parentActor));

    #region Destroy/Feed Inventoryitem

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_TRASHINVENTORYITEM))]
    private void ReceiveTrashInventoryItem(GAME_5_PROTOCOL.MSG_TRASHINVENTORYITEM message) {
        try { HandleTrashInventoryItem(message); }
        catch {
            // CLASSIC: do not let a restarted actor keep spending an uncertain attached snapshot.
            if (WizardCollection.IsInventorySnapshotUncertain(GetActiveWizard())) CloseSession();
            throw;
        }
    }

    private void HandleTrashInventoryItem(GAME_5_PROTOCOL.MSG_TRASHINVENTORYITEM message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }
        if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }

        // CLASSIC: the native confirmed active-elixir dismissal reuses this message, but
        // has no backpack item. Only an exact approved active original may take this path;
        // ordinary equipped gear and forged TemplateID values remain undiscardable here.
        if (wizard.EquipmentBehavior?.GetItem(message.GlobalID)?.m_inactiveBehaviors?
            .Any(behavior => behavior is ClientElixirBehavior) == true) {
            var result = ElixirCollection.Cancel(wizard, message.GlobalID, message.TemplateID);
            if (result.Saved) {
                foreach (var cleanup in ElixirService.ExpireCommitted(wizard, result, true)) SendToSocket(cleanup);
            }
            else if (WizardCollection.IsInventorySnapshotUncertain(wizard)) CloseSession();
            else Logger.Information("Active elixir dismissal of item {0} by {1} refused.", Logger.Args(message.GlobalID, wizard.CharId));
            return;
        }

        // CLASSIC: an item the game will not let you throw away (the template's FLAG_NoDrop) stays;
        // the client knows the flag (GUI_NoDrop), so this guards against a crafted message. A trashed item's document
        // is deleted with it.
        if (!TryDiscardBackpackItem(wizard, message.GlobalID)) {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
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

    // CLASSIC: the discard policy applies to the fresh saved original, within its row/reference transaction.
    internal static bool TryDiscardBackpackItem(Wizard wizard, ulong id,
        Func<WizClientObjectItem, bool> canDiscard = null) {
        if (wizard is null || WizardCollection.IsInventorySnapshotUncertain(wizard)) return false;
        List<WizClientObjectItem> backpack = [];
        return WizardCollection.CommitCharacterMutation(wizard.CharId, (session, saved) => {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)
                || !WizardInventoryTransactions.TryReadOwnedBackpack(session, saved, out var owned)) return false;
            var item = owned.SingleOrDefault(row => row.m_globalID.Full == id);
            if (item is null || !(canDiscard is null ? !IsNoDrop(item) : canDiscard(item))) return false;
            if (!WizardInventoryTransactions.TryStageRemove(session, saved, id, destroy: true,
                out var removed, out backpack, trackedRows: owned)) return false;
            WizardInventoryTransactions.ProtectUnmodifiedRows(session, removed);
            return true;
        }, saved => WizardInventoryTransactions.PublishCommittedBackpack(wizard, saved, backpack),
            onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(wizard));
    }

    // CLASSIC: all accepted originals and the saved, pouch-capped payout commit once. Prices and eligibility
    // come from tracked saved rows; the request's quantity is never a Classic backpack stack count.
    internal static bool TrySellBackpackItems(Wizard wizard, IEnumerable<BackpackQuickSell.Request> requests,
        Func<WizClientObjectItem, double?> price, out IReadOnlyList<BackpackQuickSell.Sale> sales,
        bool singleQuantity = true) {
        sales = [];
        if (wizard is null || requests is null || price is null || WizardCollection.IsInventorySnapshotUncertain(wizard)) return false;
        var requested = requests.ToArray();
        var accepted = new List<BackpackQuickSell.Sale>();
        List<WizClientObjectItem> backpack = [];
        var success = WizardCollection.CommitCharacterMutation(wizard.CharId, (session, saved) => {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard) || saved.GameStats is null
                || !WizardInventoryTransactions.TryReadOwnedBackpack(session, saved, out var owned)) return false;
            var seen = new HashSet<ulong>();
            var removedOriginals = new List<WizClientObjectItem>();
            foreach (var request in requested) {
                if ((singleQuantity ? request.Quantity != 1 : request.Quantity <= 0) || !seen.Add(request.Id)) continue;
                var item = owned.SingleOrDefault(row => row.m_globalID.Full == request.Id);
                if (item is null) continue;
                var value = price(item);
                if (value is null || !double.IsFinite(value.Value) || value < 0 || value > int.MaxValue) continue;
                var total = Math.Ceiling(value.Value) * (singleQuantity ? 1 : request.Quantity);
                if (!double.IsFinite(total) || total > int.MaxValue) continue;
                if (!WizardInventoryTransactions.TryStageRemove(session, saved, request.Id, destroy: true,
                    out var removed, out backpack, trackedRows: owned)) continue;
                removedOriginals.Add(removed);
                accepted.Add(new(removed.m_globalID.Full, (int)total));
            }
            if (accepted.Count == 0) return false;
            saved.GameStats.m_currentGold += BackpackQuickSell.GoldToApply(accepted,
                saved.GameStats.m_currentGold, saved.GameStats.m_baseGoldPouch);
            WizardInventoryTransactions.ProtectUnmodifiedRows(session, removedOriginals.ToArray());
            return true;
        }, saved => {
            WizardInventoryTransactions.PublishCommittedBackpack(wizard, saved, backpack);
            wizard.GameStats.m_currentGold = saved.GameStats.m_currentGold;
        }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(wizard));
        if (success) sales = accepted.ToArray();
        return success;
    }

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
        try { HandleQuickSellRequest(message); }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(GetActiveWizard())) CloseSession();
            throw;
        }
    }

    private void HandleQuickSellRequest(WIZARD2_53_PROTOCOL.MSG_QUICKSELLREQUEST message) {
        var serializer = new ObjectSerializer(
            Behaviors: SerializerFlags.None
        );

        var wizard = GetActiveWizard();
        if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }

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
            TrySellBackpackItems(wizard, requests, item => {
                if (CoreObjectFactory.GetCoreTemplate(item.m_templateID) is not WizItemTemplate template
                    || !BackpackQuickSell.IsSellable(template.m_adjectiveList)) return null;
                var value = Math.Ceiling(template.m_baseCost * 0.05f);
                if (template.m_numPrimaryColors != 1 && template.m_numSecondaryColors != 0)
                    value = Math.Ceiling(value * 1.2275f);
                return value;
            }, out var sales);
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
            foreach (var sale in sales)
                SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM {
                    GlobalID = wizard.GameObjectID, ItemID = sale.Id
                });
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
                Gold = wizard.GameStats.m_currentGold, MaxGold = wizard.GameStats.m_baseGoldPouch
            });
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_QUICKSELLREQUEST());
            return;
        }

        // CLASSIC: retain the unrestricted profile's quantity pricing, but commit its originals and payout together.
        var legacyRequests = quickSellItemList.m_quickSellItemList?.Where(item => item is not null)
            .Select(item => new BackpackQuickSell.Request(item.m_sellItemGID, item.m_quantity)).ToArray() ?? [];
        TrySellBackpackItems(wizard, legacyRequests, item => {
            if (CoreObjectFactory.GetCoreTemplate(item.m_templateID) is not WizItemTemplate template) return null;
            var value = Math.Ceiling(template.m_baseCost * 0.05f);
            if (template.m_numPrimaryColors != 1 && template.m_numSecondaryColors != 0)
                value = Math.Ceiling(value * 1.2275f);
            return value;
        }, out var legacySales, singleQuantity: false);
        if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
        foreach (var sale in legacySales) {
            var quantity = legacyRequests.First(request => request.Id == sale.Id && request.Quantity > 0).Quantity;
            for (long i = 0; i < quantity; i++) {
                SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM() {
                    GlobalID = wizard.GameObjectID,
                    ItemID = sale.Id
                });
            }
        }

        // Update player with their new gold balance.
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
        // CLASSIC: a refused saved removal has no receipt. Never dereference null or announce a change.
        if (!wizard.RemoveReagent(reagent.m_globalID, out var updatedReagent) || updatedReagent is null) return;

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
