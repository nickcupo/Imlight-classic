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
 * SHOP SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages player shop interactions, including item buying, selling, 
 * dyeing and pet renaming within the game server session.
 * 
 * USAGE EXAMPLE:
 * Internal service handling shop-related messages and transactions 
 * for player inventory and economic interactions.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty, Joji
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.Types;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.WizBang;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.Game.Services;

internal class ShopService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const float SELL_MODIFIER = 0.05f;
    private const float DYED_ITEM_COST_MULTIPLIER = 1.225f;
    // Public equipment carries each dye layer in 5 bits.
    private const int MaxDye = 31;
    private const PropertyFlags EquippedItemMask = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;

    private static readonly CoreObjectSerializer s_itemSerializer = new(
        behaviors: Imcodec.ObjectProperty.SerializerFlags.None
    );

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new InteractService(parentActor));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_SHOPBUYREQUEST))]
    private void ReceiveShopBuyRequest(WIZARD_12_PROTOCOL.MSG_SHOPBUYREQUEST message) {
        try { HandleShopBuyRequest(message); }
        catch {
            // CLASSIC: a failed/lost ACK quarantines within the lane; close before any actor restart can continue.
            if (WizardCollection.IsInventorySnapshotUncertain(GetActiveWizard())) CloseSession();
            throw;
        }
    }

    private void HandleShopBuyRequest(WIZARD_12_PROTOCOL.MSG_SHOPBUYREQUEST message) {
        if (WizardCollection.IsInventorySnapshotUncertain(GetActiveWizard())) { CloseSession(); return; }
        // Ensure that the player has interacted with an object in the zone.
        var interactedObject = GetZoneObject(message.npcGlobalID);
        if (interactedObject is null) {
            Logger.Warning("Failed to find NPC {0} in zone for shop purchase",
                Logger.Args(message.npcGlobalID));
            var shopDenyMsg = new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM { Failure = 1 };
            SendToSocket(shopDenyMsg);

            return;
        }

        // Ensure that the interacted object is a vendor.
        // CLASSIC: and that the wizard stands by it.
        var vendorComponent = interactedObject.GetComponentOfType<InteractVendorComponent>();
        if (vendorComponent is null || !ServiceProximity.IsNear(GetActiveWizard(), interactedObject)) {
            Logger.Warning("Failed to find VendorComponent for NPC {0} in zone for shop purchase",
                Logger.Args(message.npcGlobalID));
            var shopDenyMsg = new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM { Failure = 1 };
            SendToSocket(shopDenyMsg);

            return;
        }

        // Ensure that the vendor has the item that the player is trying to purchase.
        var itemTemplateID = new GID(message.ShopID).MParts.TemplateId;
        if (!vendorComponent.HasItem((GID) message.ShopID)) {
            HandleIllegalPurchaseAttempt(itemTemplateID, message.npcGlobalID);

            return;
        }

        var playerWizard = GetActiveWizard();

        // CLASSIC: a pet snack (the Pet Pavilion's snack vendor) goes to the snack bag, not the backpack.
        if (CoreObjectFactory.GetCoreTemplate(itemTemplateID) is PetSnackItemTemplate snackTemplate) {
            BuySnack(playerWizard, snackTemplate);

            return;
        }

        var template = (WizItemTemplate) CoreObjectFactory.GetCoreTemplate(itemTemplateID);

        // CLASSIC: some vendors in open zones list a jewel; buying one follows the profile's jewels switch.
        if (template.m_adjectiveList?.Exists(adjective => string.Equals(adjective, "Jewel", StringComparison.OrdinalIgnoreCase)) == true
            && !ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.Jewels)) {
            ClassicGate.RefuseFeature(ClassicFeatures.Jewels, playerWizard?.CharId, InformGameClient);
            SendShopDenyMessage();

            return;
        }

        // CLASSIC: a pet from a vendor comes hatched, with its pet behaviors (level, stats, name), as PetFactory makes it.
        var item = PetFactory.IsPetTemplate(itemTemplateID)
            ? PetFactory.CreateHatchedPet(playerWizard.CharId, itemTemplateID)
            : (WizClientObjectItem) CoreObjectFactory.FinalizeCoreObject(itemTemplateID);
        // CLASSIC: a chosen color only on an item the shop offers colors for (its price carries the dyed markup);
        // otherwise, or out of range, the template's own.
        ApplyBuyDyes(item, template, message.texture, message.decal);

        // CLASSIC: a house's price, original deed, gold/account balance and portfolio save
        // together. Never route a deed through the ordinary charge-then-give path.
        if (ClassicRuntime.IsActive && Classic.Housing.HouseCatalog.IsDeed(template)) {
            var purchased = Classic.Housing.HouseCollection.Purchase(playerWizard, item,
                message.CurrencyType == 1 ? Classic.Housing.HouseCurrency.Crowns : Classic.Housing.HouseCurrency.Gold);
            if (!purchased.Saved) { InformGameClient(purchased.Error); SendShopDenyMessage(); return; }
            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
                GlobalID = playerWizard.GameObjectID, SerializedItem = purchased.ItemData,
            });
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
                ItemGlobalID = purchased.Item.m_globalID, ItemTemplateID = itemTemplateID, ItemLocation = 1,
            });
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
                Gold = playerWizard.GameStats.m_currentGold, MaxGold = playerWizard.GameStats.m_baseGoldPouch,
            });
            SendToSocket(ClassicCrowns.BalanceMessage(playerWizard.Account, playerWizard.CharId));
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM());
            return;
        }

        // CLASSIC: prepare privately; the saved backpack, wallet and original item row are checked together.
        item = WizardInventoryTransactions.Prepare(playerWizard, item, !PetFactory.IsPetTemplate(itemTemplateID));
        if (item is null) { SendShopDenyMessage(); return; }

        // CLASSIC: an Arena Ticket vendor (Diego, Roland Silverheart) sells for tickets, some items only from a PvP rank up.
        if (vendorComponent.SellsForTickets) {
            BuyWithTickets(playerWizard, item, template, itemTemplateID,
                (uint) interactedObject.ActiveGameObject.m_templateID.Full);

            return;
        }

        // CLASSIC: a holiday vendor's item sells at its 2009 price (classic-data/holidays). A Crowns-only item, or one the
        // wizard chose to pay for in Crowns (CurrencyType 1), costs the item's Crowns price from the account's balance.
        var holidayPrice = ClassicHolidays.PriceOf(itemTemplateID);
        var crownsOnly = template.m_adjectiveList?.Exists(adjective => adjective == "FLAG_CrownsOnly") == true;
        var crownsCost = holidayPrice?.Crowns ?? (int) template.m_creditsCost;
        if (ClassicRuntime.IsActive && crownsCost > 0 && (crownsOnly || message.CurrencyType == 1)) {
            var account = playerWizard.Account;
            if (!ClassicItemPurchases.Purchase(playerWizard, item, crownsCost, crowns: true,
                out var receipt, serialize: SerializePurchase)) {
                if (WizardCollection.IsInventorySnapshotUncertain(playerWizard)) { CloseSession(); return; }
                SendShopDenyMessage();
                return;
            }
            ProcessSuccessfulPurchase(playerWizard, receipt);
            SendToSocket(ClassicCrowns.BalanceMessage(account, playerWizard.CharId));
            Logger.Information("{Wizard} bought {Item} for {Crowns} Crowns.", Logger.Args(playerWizard.CharId, itemTemplateID, crownsCost));

            return;
        }

        var goldCost = holidayPrice?.Gold ?? CalculateItemCost(template);

        if (!ClassicItemPurchases.Purchase(playerWizard, item, goldCost, crowns: false,
            out var goldReceipt, serialize: SerializePurchase)) {
            if (WizardCollection.IsInventorySnapshotUncertain(playerWizard)) { CloseSession(); return; }
            SendShopDenyMessage();
            return;
        }
        ProcessSuccessfulPurchase(playerWizard, goldReceipt);
    }

    // CLASSIC: a purchase from an Arena Ticket vendor: the 2009 price in tickets (price_2009 where the client's template
    // carries a later price, else the template's m_arenaPointCost; the client step "tickets" puts the 2009 price in the
    // client's template, so the shop window shows what is charged),
    // the item's PvP rank (wiki item pages, 2009: "PvP Rank Sergeant Only"...), saved with payment and the item.
    private void BuyWithTickets(Wizard wizard, WizClientObjectItem item, WizItemTemplate template, uint itemTemplateID, uint npcTemplate) {
        var entry = Classic.Arena.ClassicArena.TicketItem(npcTemplate, itemTemplateID);
        var config = Classic.Arena.ClassicArena.Config;
        if (entry is null || config is null) {
            SendShopDenyMessage();

            return;
        }

        var price = entry.Price ?? template.m_arenaPointCost;
        if (price < 0) {
            Logger.Error("Rejected negative Arena Ticket price {Price} for item {Item}.", Logger.Args(price, itemTemplateID));
            SendShopDenyMessage();

            return;
        }

        var result = TryPurchaseWithTickets(wizard, item, price,
            Imlight.Classic.Pvp.ArenaRules.MinRatingOf(entry.Rank, config.Ranks), config.StartRating,
            out var receipt, SerializePurchase);
        if (result != TicketPurchaseResult.Purchased) {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
            if (result is TicketPurchaseResult.RankRequired or TicketPurchaseResult.TicketsUnavailable) {
                InformGameClient(result == TicketPurchaseResult.RankRequired
                    ? $"You need the PvP rank of {entry.Rank} for that."
                    : $"You need {price} Arena Tickets for that.");
            }
            SendShopDenyMessage();

            return;
        }
        ProcessSuccessfulPurchase(wizard, receipt);
        SendToSocket(Classic.Arena.ArenaMessages.ArenaPoints(wizard.GameStats.m_currentArenaPoints));
        SendToSocket(Classic.Arena.ArenaMessages.PvpCurrency(wizard.GameStats.m_currentPvPCurrency));
        Logger.Information("{Wizard} bought {Item} for {Tickets} Arena Tickets.", Logger.Args(wizard.CharId, itemTemplateID, price));
    }

    // CLASSIC: tickets, the fresh persisted ladder band, original item row and backpack reference share one save.
    internal static TicketPurchaseResult TryPurchaseWithTickets(Wizard wizard, WizClientObjectItem prepared,
        int price, int minimumRating, int startRating, out ItemPurchaseReceipt receipt,
        Func<WizClientObjectItem, ByteString> serialize,
        Func<IDocumentSession, ulong, ArenaLadderEntry> loadStanding = null) {
        receipt = null;
        if (price < 0) return TicketPurchaseResult.InvalidPrice;
        if (wizard is null || prepared is null || serialize is null || WizardCollection.IsInventorySnapshotUncertain(wizard))
            return TicketPurchaseResult.InventoryRefused;
        var result = TicketPurchaseResult.InventoryRefused;
        ItemPurchaseReceipt acknowledged = null;
        List<WizClientObjectItem> backpack = [];
        var success = WizardCollection.CommitCharacterMutation(wizard.CharId, (session, saved) => {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard) || saved.GameStats is null) return false;
            var standing = loadStanding is null
                ? session.Load<ArenaLadderEntry>(ArenaLadderCollection.DocumentId(saved.CharId))
                : loadStanding(session, saved.CharId);
            if (standing is not null && standing.CharId != saved.CharId) return false;
            if ((standing?.Rating ?? startRating) < minimumRating) { result = TicketPurchaseResult.RankRequired; return false; }
            if (saved.GameStats.m_currentArenaPoints < price) { result = TicketPurchaseResult.TicketsUnavailable; return false; }
            if (!WizardInventoryTransactions.TryStageGrants(session, saved, [prepared], out var admitted,
                out _, out backpack) || admitted.Count != 1) return false;
            ByteString data;
            try { data = serialize(prepared); }
            catch (Exception) { return false; }
            if (data.Length == 0) return false;
            saved.GameStats.m_currentArenaPoints -= price;
            saved.GameStats.m_currentPvPCurrency = saved.GameStats.m_currentArenaPoints;
            acknowledged = new(prepared, data);
            return true;
        }, saved => {
            WizardInventoryTransactions.PublishCommittedBackpack(wizard, saved, backpack);
            wizard.GameStats.m_currentArenaPoints = saved.GameStats.m_currentArenaPoints;
            wizard.GameStats.m_currentPvPCurrency = saved.GameStats.m_currentPvPCurrency;
        }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(wizard));
        if (!success) return result;
        receipt = acknowledged;
        return TicketPurchaseResult.Purchased;
    }

    // CLASSIC: retained callback contract for ticket wallet consumers; ordinary production item purchases use
    // the atomic overload above. Affordability comes from the saved balance;
    // a known inventory refusal refunds that debit, retaining any award saved in between. A thrown
    // grant may have committed an item, so it is deliberately never retried or refunded here.
    internal static TicketPurchaseResult TryPurchaseWithTickets(Wizard wizard, int price, int rating,
        int minimumRating, Func<bool> grant) {
        if (price < 0) return TicketPurchaseResult.InvalidPrice;
        if (rating < minimumRating) return TicketPurchaseResult.RankRequired;
        if (!WizardCollection.TrySpendArenaTickets(wizard, price)) {
            Logger.Warning("Refused saved Arena Ticket debit of {Tickets} for {Wizard}; no item was granted.",
                Logger.Args(price, wizard?.CharId));
            return TicketPurchaseResult.TicketsUnavailable;
        }
        if (grant()) return TicketPurchaseResult.Purchased;
        if (!WizardCollection.ChangeArenaTickets(wizard, price)) {
            Logger.Error("Failed to refund {Tickets} Arena Tickets to {Wizard} after a refused item purchase.",
                Logger.Args(price, wizard.CharId));
            throw new InvalidOperationException("The refused Arena Ticket purchase could not be refunded.");
        }

        return TicketPurchaseResult.InventoryRefused;
    }

    internal enum TicketPurchaseResult {
        Purchased,
        InvalidPrice,
        RankRequired,
        TicketsUnavailable,
        InventoryRefused,
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_SHOPSELLREQUEST))]
    private void ReceiveShopSellRequest(WIZARD_12_PROTOCOL.MSG_SHOPSELLREQUEST message) {
        try { HandleShopSellRequest(message); }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(GetActiveWizard())) CloseSession();
            throw;
        }
    }

    private void HandleShopSellRequest(WIZARD_12_PROTOCOL.MSG_SHOPSELLREQUEST message) {
        var wizard = GetActiveWizard();
        if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
        var item = wizard.InventoryBehavior.GetItem(message.GlobalID);

        // CLASSIC: a shop buys only from a wizard standing by it (the request's NPC, or the shop last opened).
        if (ServiceProximity.FindNear<InteractVendorComponent>(wizard, message.npcGlobalID, GetZoneObject) is null
            && ServiceProximity.FindNear<InteractReagentComponent>(wizard, message.npcGlobalID, GetZoneObject) is null) {
            Logger.Warning("{0} tried to sell item {1} away from a shop.", Logger.Args(wizard.CharId, message.GlobalID));
            ProcessFailedSale();

            return;
        }

        if (ClassicRuntime.IsActive && Classic.Housing.HouseCollection.IsDeed(item)) {
            if (!Classic.Housing.HouseCatalog.TryGet((uint)item.m_templateID.Full, out var house) || !house.ResaleAllowed) { ProcessFailedSale(); return; }
            // The ordinary vendor's resale percentage is an explicit, separately gated
            // historical ruling; calculate from the approved gold price, never a request.
            var sold = Classic.Housing.HouseCollection.Sell(wizard, message.GlobalID, (int)Math.Ceiling(house.Gold * SELL_MODIFIER));
            if (!sold.Saved) { InformGameClient(sold.Error); ProcessFailedSale(); return; }
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold = wizard.GameStats.m_currentGold, MaxGold = wizard.GameStats.m_baseGoldPouch });
            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM { GlobalID = wizard.GameObjectID, ItemID = message.GlobalID });
            foreach (var patch in sold.Added) SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHADDATTIC {
                BlobGID = sold.Attic.ContainerId, ObjectID = patch.Entry.TemplateId, SubType = Classic.Housing.HousingRules.AtticSubType,
                PackageNumber = (uint)patch.PackageNumber, VersionNumber = patch.Version, GIDID = patch.CacheIndex, Data = "", PrimaryColorIndex = 0,
            });
            if (sold.Attic is { } attic) SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_UPDATEATTICCOUNT { GlobalID = wizard.GameObjectID, ItemCount = attic.Count });
            return;
        }

        var removedItemSuccess = InventoryService.TrySellBackpackItems(wizard, [new(message.GlobalID, 1)],
            savedItem => {
                if (CoreObjectFactory.GetCoreTemplate(savedItem.m_templateID) is not WizItemTemplate template
                    || ClassicRuntime.Rules.UsesKingsIsleQuestRules
                    && !Imlight.Classic.Inventory.BackpackQuickSell.IsSellable(template.m_adjectiveList)) return null;
                return CalculateItemSellValue(template);
            }, out var sales);
        if (!removedItemSuccess) {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
            Logger.Warning("Failed to find item {0} in inventory for shop sell", Logger.Args(message.GlobalID));
            ProcessFailedSale();

            return;
        }

        ProcessSuccessfulSale(wizard, sales[0].Id);
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_DYEREQUEST))]
    private void ReceiveDyeRequest(WIZARD_12_PROTOCOL.MSG_DYEREQUEST message) {
        // CLASSIC: dyeing follows the profile's seamstress switch; refuse with the handler's own failure reply.
        if (!ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.Seamstress)) {
            ClassicGate.RefuseFeature(ClassicFeatures.Seamstress, GetActiveWizard()?.CharId, InformGameClient);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_DYECONFIRM { Failure = 1 });

            return;
        }

        var wizard = GetActiveWizard();
        var item = wizard.InventoryBehavior.GetItem(message.itemGlobalID);
        var isEquipped = false;
        if (item == null) {
            // Failsafe: item may be equipped instead
            item = wizard.EquipmentBehavior.GetItem(message.itemGlobalID);

            if (item == null) {
                Logger.Error("Failed to find item {0} in inventory for dyes", Logger.Args(message.itemGlobalID));

                var dyeDenyMsg = new WIZARD_12_PROTOCOL.MSG_DYECONFIRM { Failure = 1 };
                SendToSocket(dyeDenyMsg);

                return;
            }
            else {
                isEquipped = true;
            }
        }

        var template = CoreObjectFactory.GetCoreTemplate(item.m_templateID) as WizItemTemplate;
        if (template is null || !IsValidDye(item, template, message)) {
            Logger.Warning("Rejected dye {0}/{1}/{2} for item {3} (template {4})",
                Logger.Args(message.texture, message.decal, message.decal2, item.m_globalID, item.m_templateID.Full));

            SendToSocket(new WIZARD_12_PROTOCOL.MSG_DYECONFIRM { Failure = 1 });

            return;
        }

        // CLASSIC: checked and spent in one save (never below zero).
        var dyeCost = PriceModifiersConfig.GetDyeCost(template, message.texture, message.decal);
        if (!wizard.RemoveGold(dyeCost)) {
            var dyeDenyMsg = new WIZARD_12_PROTOCOL.MSG_DYECONFIRM { Failure = 1 };
            SendToSocket(dyeDenyMsg);
            
            return;
        }

        var goldUpdateMsg = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        };
        SendToSocket(goldUpdateMsg);

        // Apply the dyes
        var texture = (DyeColor) message.texture;
        var decal = (DyeColor) message.decal;
        var decal2 = (DyeColor) message.decal2;
        DyeMapper.ApplyAllDye(item, texture, decal, decal2);

        // If the item is equipped, update the client's local item cache with the
        // new colors IN PLACE using MSG_EQUIPMENTBEHAVIOR_EQUIPITEM. 
        if (isEquipped) {
            SendEquippedItemRefresh(wizard, item, template);
        }

        // Confirm success to the client.
        var msgConfirm = new WIZARD_12_PROTOCOL.MSG_DYECONFIRM {
            Failure = 0,
            itemGID = message.itemGlobalID,
            firstLayer = message.texture,
            secondLayer = message.decal,
            thirdLayer = message.decal2
        };
        SendToSocket(msgConfirm);

        ResummonIfEquippedPet(wizard, item);
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PETRENAMEREQUEST))]
    private void ReceivePetRenameRequest(WIZARD_12_PROTOCOL.MSG_PETRENAMEREQUEST message) {
        var wizard = GetActiveWizard();

        // The dye shop's pet name tab lists backpack and equipped pets alike.
        var pet = wizard.InventoryBehavior.GetItem(message.itemGlobalID)
            ?? wizard.EquipmentBehavior.GetItem(message.itemGlobalID);
        if (pet is null || !CoreObjectFactory.FindBehaviorInstance<ClientPetNameBehavior>(pet, out _)) {
            Logger.Warning("Player tried to rename item {0}, which is not one of their pets.",
                Logger.Args(message.itemGlobalID));

            SendPetRenameDeny();

            return;
        }

        if (!WizardNameBank.IsValidPetName(message.petName)) {
            Logger.Warning("Player tried to rename pet {0} with name keys {1}, which are not in the pet name tables.",
                Logger.Args(pet.m_globalID, message.petName));

            SendPetRenameDeny();

            return;
        }

        var renameCost = PriceModifiersConfig.GetPetRenameCost();
        if (renameCost > wizard.GameStats.m_currentGold) {
            SendPetRenameDeny();

            return;
        }

        // CLASSIC: paid (checked and spent in one save) before the name is saved; refunded if the save fails.
        if (!wizard.RemoveGold(renameCost)) {
            SendPetRenameDeny();

            return;
        }

        if (!WizardItemCollection.ApplyPetName(pet, message.petName)) {
            Logger.Error("Failed to save name keys {0} for pet {1}",
                Logger.Args(message.petName, pet.m_globalID));

            wizard.RefundGold(renameCost);
            SendPetRenameDeny();

            return;
        }

        PetFactory.TrySetPetName(pet, message.petName);

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        });

        // The client renames its own copy of the pet on success; the confirm carries no name.
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETRENAMECONFIRM { Failure = 0 });

        Logger.Information("Pet {0} renamed to {1} for {2} gold.",
            Logger.Args(pet.m_globalID, WizardNameBank.GetPetEnglishName(message.petName), renameCost));

        ResummonIfEquippedPet(wizard, pet);
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_DONESHOPPING))]
    private void ReceiveDoneShopping() {
        // A wizard has complete shopping and is leaving the shop.
        var wizard = GetActiveWizard();

        if (wizard.Zone.Contains("Phantom")) {
            return;
        }

        // Reenable player movement
        var enableMovementStateMsg = new GAME_5_PROTOCOL.MSG_ENTERSTATE() {
            GameObjectID = wizard.GameObjectID,
            State = 1685237158,
            Data = "",
            IgnoreIfCurrentStateIsOff = 0
        };
        SendToSocket(enableMovementStateMsg);

        var wizBangMsg = new GAME_5_PROTOCOL.MSG_WIZBANG() {
            GameObjectID = wizard.GameObjectID,
            WizBangID = (uint) WizBangs.None
        };
        ZoneBroadcast(wizBangMsg, false);
    }

    private void ProcessSuccessfulSale(Wizard wizard, ulong itemID) {
        // Inform the game client of the successful sale.
        var updateGoldMsg = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        };
        SendToSocket(updateGoldMsg);

        // Inform the game client of the successful sale.
        var removeItemMsg = new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM {
            GlobalID = wizard.GameObjectID,
            ItemID = itemID
        };
        SendToSocket(removeItemMsg);
    }

    private void ProcessFailedSale() {
        var shopDenyMsg = new WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM {
            Failure = 1,
        };
        SendToSocket(shopDenyMsg);
    }

    private void SendPetRenameDeny()
        => SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETRENAMECONFIRM { Failure = 1 });

    private void SendEquippedItemRefresh(Wizard wizard, WizClientObjectItem item, WizItemTemplate template) {
        var slot = ItemHelper.GetItemSlot(template);
        if (slot is null) {
            Logger.Error("Dyed item {0} has no equipment slot", Logger.Args(item.m_globalID));

            return;
        }

        // Serialize the item with new colors for the local player's cache.
        if (!s_itemSerializer.Serialize(item, EquippedItemMask, out var localData)) {
            Logger.Error("Failed to serialize dyed item {0} for local update",
                Logger.Args(item.m_globalID));

            return;
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_EQUIPITEM {
            GlobalID = wizard.GameObjectID,
            SlotName = slot.SlotType.ToString(),
            IsValid = 1,
            SerializedItem = localData
        });

        // Broadcast updated public appearance so other players see the new colors.
        var pubItem = ItemHelper.GetPublicItem(item);
        if (!s_itemSerializer.Serialize(pubItem, 1, out var pubData)) {
            Logger.Error("Failed to serialize item {0} for dye broadcast",
                Logger.Args(item.m_globalID));

            return;
        }

        ZoneBroadcast(new GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_PUBLICEQUIPITEM {
            GlobalID = wizard.GameObjectID,
            SerializedInfo = pubData
        }, false);
    }

    private void ResummonIfEquippedPet(Wizard wizard, WizClientObjectItem item) {
        // A summoned pet carries its look and name from when it was summoned.
        if (wizard.EquipmentBehavior.GetEquippedPetId() == item.m_globalID) {
            TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_RESUMMONPET { PetItemId = item.m_globalID });
        }
    }

    private static bool IsValidDye(WizClientObjectItem item, WizItemTemplate template,
                                   WIZARD_12_PROTOCOL.MSG_DYEREQUEST message) {
        if (!IsDyeInRange(message.texture) || !IsDyeInRange(message.decal) || !IsDyeInRange(message.decal2)) {
            return false;
        }

        if (!PetFactory.IsPetTemplate(template.m_templateID)) {
            return true;
        }

        // A pet's colors are its template's own texture choices, so no dye beyond them is offered.
        return IsPetLayerDye(message.texture, item.m_primaryColor, template.m_numPrimaryColors)
            && IsPetLayerDye(message.decal, item.m_secondaryColor, template.m_numSecondaryColors)
            && IsPetLayerDye(message.decal2, item.m_pattern, template.m_numPatterns);
    }

    private static bool IsDyeInRange(int dye) => dye is >= 0 and <= MaxDye;

    private static bool IsPetLayerDye(int dye, int currentDye, int templateColorCount)
        => dye == currentDye || (templateColorCount > 1 && dye < templateColorCount);

    private void SendShopDenyMessage() {
        var shopDenyMsg = new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM {
            Failure = 1,
            WebFailure = 0,
            Credits = 0
        };
        SendToSocket(shopDenyMsg);
    }

    // CLASSIC: buys one pet snack into the snack bag (MSG_PETSNACKADD for a new stack, MSG_PETSNACKUPDATE for more).
    private void BuySnack(Wizard wizard, PetSnackItemTemplate template) {
        var cost = (int) template.m_baseCost;
        if (wizard.GameStats.m_currentGold < cost) {
            SendShopDenyMessage();

            return;
        }

        // CLASSIC: paid first (checked and spent in one save), refunded if the snack bag refuses the snack.
        if (!wizard.RemoveGold(cost)) {
            SendShopDenyMessage();

            return;
        }

        var hadStack = wizard.PetSnackBehavior.GetSnack(template.m_templateID) is not null;
        if (!wizard.AddSnack(template.m_templateID, out var snack)) {
            wizard.RefundGold(cost);
            SendShopDenyMessage();

            return;
        }

        var stack = wizard.PetSnackBehavior.GetSnack(template.m_templateID) ?? snack;
        // As .mod addsnack does: a new stack is MSG_PETSNACKADD, and every buy ends with the stack's MSG_PETSNACKUPDATE.
        if (!hadStack && s_snackSerializer.Serialize(stack, (PropertyFlags) 24, out var data)) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKADD { GlobalID = wizard.GameObjectID, Data = data });
        }

        SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKUPDATE {
            GlobalID = wizard.GameObjectID, ItemID = stack.m_globalID, Quantity = stack.m_quantity
        });

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold, MaxGold = wizard.GameStats.m_baseGoldPouch
        });
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM());
        Logger.Information("{0} bought pet snack {1} for {2} gold (stack {3}).",
            Logger.Args(wizard.CharId, template.m_templateID, cost, stack.m_quantity));
    }

    private static readonly CoreObjectSerializer s_snackSerializer = new(behaviors: Imcodec.ObjectProperty.SerializerFlags.None);

    // CLASSIC: native payload and flags are prepared before SaveChanges; only the acknowledged receipt is sent.
    private static ByteString SerializePurchase(WizClientObjectItem item)
        => s_itemSerializer.Serialize(item, PetFactory.IsPetTemplate((uint)item.m_templateID.Full) ? (PropertyFlags)24 : (PropertyFlags)1,
            out var data) ? data : default;

    private void ProcessSuccessfulPurchase(Wizard playerWizard, ItemPurchaseReceipt receipt) {
        var item = receipt.Item;
        // Inform the game client that a new item has been added to the player's inventory.
        var addItemMsg = new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
            GlobalID = playerWizard.GameObjectID,
            SerializedItem = receipt.Data,
        };
        SendToSocket(addItemMsg);

        // Inform the game client that the player has acquired a new item.
        var itemAcqMsg = new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = item.m_globalID,
            ItemTemplateID = (uint)item.m_templateID.Full,
            ItemLocation = 1,
        };
        SendToSocket(itemAcqMsg);

        // Inform the game client that the player's gold has been updated.
        var goldUpdateMsg = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = playerWizard.GameStats.m_currentGold,
            MaxGold = playerWizard.GameStats.m_baseGoldPouch
        };
        SendToSocket(goldUpdateMsg);

        // Inform the game client that the purchase was successful.
        var shopConfirmMsg = new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM();
        SendToSocket(shopConfirmMsg);

    }

    // CLASSIC: the buyer's texture and decal when the shop offers colors for the item and they are valid; else the
    // finalized item keeps its template's colors. Also used by the Bazaar.
    internal static void ApplyBuyDyes(WizClientObjectItem item, WizItemTemplate template, int texture, int decal) {
        var isPet = PetFactory.IsPetTemplate(template.m_templateID);
        var dyeable = DyeRules.IsDyeable(template.m_numPrimaryColors, template.m_numSecondaryColors);
        item.m_primaryColor = DyeRules.BuyLayer(texture, item.m_primaryColor, dyeable, isPet, template.m_numPrimaryColors);
        item.m_secondaryColor = DyeRules.BuyLayer(decal, item.m_secondaryColor, dyeable, isPet, template.m_numSecondaryColors);
    }

    private void HandleIllegalPurchaseAttempt(uint itemTemplateID, ulong interactedObjectGID) {
        var account = GetActiveAccount();
        SendShopDenyMessage();

        var infractionText = $"Player tried to purchase item {itemTemplateID} from NPC {interactedObjectGID} that is not in its inventory!";
        account.AddInfraction(WizardData.Models.Misc.InfractionType.SuspiciousBehavior, infractionText);

        Logger.Warning("Player tried to purchase item {0} from an NPC that it did not have in its inventory. "
            + "This has been logged as suspicious behavior.",
            Logger.Args(itemTemplateID));
    }

    private static int CalculateItemSellValue(WizItemTemplate template) {
        var goldValue = (int) Math.Ceiling(template.m_baseCost * SELL_MODIFIER);
        if (template.m_numPrimaryColors != 1 && template.m_numSecondaryColors != 0) {
            goldValue = (int) Math.Ceiling(goldValue * DYED_ITEM_COST_MULTIPLIER) + 1;
        }

        return goldValue;
    }

    private static int CalculateItemCost(WizItemTemplate template) {
        var goldCost = (int) template.m_baseCost;
        if (template.m_numPrimaryColors != 1 && template.m_numSecondaryColors != 0) {
            goldCost = (int) Math.Ceiling(goldCost * DYED_ITEM_COST_MULTIPLIER) + 1;
        }

        return goldCost;
    }

}
