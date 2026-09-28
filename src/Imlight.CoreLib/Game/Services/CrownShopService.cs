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
 * CROWN SHOP SERVICE (CLASSIC)
 * ========================================================================
 *
 * PURPOSE:
 * The Crown Shop window: segmentation data, the catalog, price locks and
 * purchases. The catalog is the profile's classic-data Crown Shop (what the
 * shop sold at the cutoff); Crowns come from ClassicCrowns.
 *
 * NOTE:
 * The message flow (segmentation data first, then the list; a price lock
 * before a purchase; the balance re-sent afterwards) follows Revive101's
 * feat/crown-shop branch (Phill030). A henchman is bought only during a duel
 * and is charged once the duel confirms it joined (MSG_HENCHMANHIRED).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5), after Phill030's upstream service
 * Version: KALI 1.0
 * Last Updated: 09/28/2026
 */

using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Services;

internal class CrownShopService(SessionActor sessionActor) : MessageService(sessionActor) {

    // The client's own tab and category ids (GUI/CrownShop); the names are its locale keys.
    private const int FeaturedTab = 37, MountsTab = 39, GameplayTab = 45;
    private const int FeaturedCategory = 0, PermanentMountsCategory = 2, RentalMountsCategory = 3, HenchmenCategory = 18,
        EverythingCategory = 19;
    private const int WishlistMaxSize = 30, WishlistExpansionSize = 10;
    private const byte CurrencyGold = 0; // CurrencyTab: Gold = 0, Crowns = 1.

    private static readonly Lazy<(FrozenDictionary<ulong, CrownShopEntry> Items, ByteString Data)> s_catalog = new(BuildCatalog);

    private PendingHire _pendingHire;

    private sealed record PendingHire(WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_REQUEST Request, CrownShopEntry Item, bool PayWithGold);

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new CrownShopService(parentActor));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_SEGDATA_REQUEST))]
    private void ReceiveSegmentationRequest(WIZARD_12_PROTOCOL.MSG_PCS_SEGDATA_REQUEST message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        var school = wizard.MagicSchoolBehavior.MagicSchool.ToString();
        var input = new SegmentationInputData {
            m_bIsValidSegmentationData = true,
            m_playerLevel = wizard.MagicSchoolBehavior.Level,
            m_playerSchoolOfFocus = school.Length >= 2 ? school[..2] : school,
            m_accountNDaysAged = (int) (DateTime.Now - wizard.Account.CreationTime).TotalDays,
            m_accountNCrownsInWallet = wizard.Account.Crowns,
            m_accountNDaysSinceItemPurchased = [],
            m_numOfParticularItemInInventory = [],
            m_numItemsOfCategoryInInventory = [],
            m_playerHasBadge = [],
        };

        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        if (!serializer.Serialize(input, PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit, out var data)) {
            Logger.Error("Crown Shop: could not serialize the segmentation data.");

            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_SEGDATA_RESPONSE { Success = 1, Data = data });
        // The shop shows 0 Crowns until the balance is sent again here.
        SendToSocket(ClassicCrowns.BalanceMessage(wizard.Account, wizard.CharId));
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_LIST_REQUEST))]
    private void ReceiveListRequest(WIZARD_12_PROTOCOL.MSG_PCS_LIST_REQUEST message)
        => SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_LIST_RESPONSE {
            Data = s_catalog.Value.Data,
            Updates = "",
            UpdateID = message.UpdateID == 0 ? 1 : message.UpdateID + 1,
            Error = 0,
        });

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_UPDATEUSERWISHLIST))]
    private void ReceiveWishlistUpdate(WIZARD_12_PROTOCOL.MSG_PCS_UPDATEUSERWISHLIST message) { }

    // Whether the item can be bought at all; affordability is checked at purchase.
    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_PRICE_LOCK_REQUEST))]
    private void ReceivePriceLock(WIZARD_12_PROTOCOL.MSG_PCS_PRICE_LOCK_REQUEST message) {
        var found = s_catalog.Value.Items.TryGetValue(message.Item, out var item);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PRICE_LOCK_RESPONSE {
            Item = message.Item,
            CostCrowns = found ? item!.Crowns : 0,
            CostGold = found ? item!.Gold : 0,
            CostTickets = 0,
            Error = (byte) (found ? 0 : 1),
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_REQUEST))]
    private void ReceivePurchase(WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_REQUEST message) {
        var wizard = GetActiveWizard();
        if (wizard is null || !s_catalog.Value.Items.TryGetValue(message.Item, out var item)) {
            Fail(message, "not in the catalog");

            return;
        }

        // CurrencyTab: 0 pays gold. An item with one price is paid in that currency.
        var payWithGold = item.Crowns == 0 || (item.Gold > 0 && message.Type == CurrencyGold);
        Logger.Information("[CROWNSHOP] {0} buys {1} x{2} ({3}), request type {4}.",
            Logger.Args(wizard.CharId, item.Name, message.Count, payWithGold ? "gold" : "Crowns", message.Type));

        // One at a time: a rental, a henchman and a mount are bought singly.
        if (message.Count != 1) {
            Fail(message, "only one at a time");

            return;
        }

        if (message.Recipient != 0) {
            Fail(message, "gifts are not offered");

            return;
        }

        if (wizard.MagicSchoolBehavior.Level < item.MinLevel) {
            Fail(message, $"needs level {item.MinLevel}");

            return;
        }

        if (!CanPay(wizard, item, payWithGold)) {
            Fail(message, "cannot afford it");

            return;
        }

        if (item.Category == CrownShopCategories.Henchmen) {
            if (_pendingHire is not null) {
                Fail(message, "a henchman is already being hired");

                return;
            }

            // Charged once the duel confirms the henchman joined (ReceiveHenchmanHired).
            _pendingHire = new PendingHire(message, item, payWithGold);
            TellOtherServices(new COMBAT_106_PROTOCOL.MSG_HIREHENCHMAN { CreatureTid = (uint) item.Template });

            return;
        }

        if (!wizard.AddItemToInventory(item.Template, out var added)) {
            Fail(message, "the backpack refused it");

            return;
        }

        if (item.RentalDays is { } days) {
            WizardItemCollection.SetExpireTime(added, (uint) DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeSeconds());
        }

        var serializer = new CoreObjectSerializer(behaviors: SerializerFlags.None);
        if (serializer.Serialize(added, 24, out var serialized)) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM { GlobalID = wizard.GameObjectID, SerializedItem = serialized });
        }

        Complete(wizard, message, item, payWithGold);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_HENCHMANHIRED))]
    private void ReceiveHenchmanHired(COMBAT_106_PROTOCOL.MSG_HENCHMANHIRED message) {
        var pending = _pendingHire;
        _pendingHire = null;
        if (pending is null || pending.Item.Template != message.CreatureTid || GetActiveWizard() is not { } wizard) {
            return;
        }

        if (!message.Success) {
            Fail(pending.Request, "henchmen are hired during a duel with a free place on your side");

            return;
        }

        Complete(wizard, pending.Request, pending.Item, pending.PayWithGold);
    }

    private static bool CanPay(Wizard wizard, CrownShopEntry item, bool payWithGold)
        => payWithGold ? item.Gold > 0 && wizard.GameStats.m_currentGold >= item.Gold : item.Crowns > 0 && wizard.Account.Crowns >= item.Crowns;

    private void Complete(Wizard wizard, WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_REQUEST request, CrownShopEntry item, bool payWithGold) {
        int cost;
        if (payWithGold) {
            cost = item.Gold;
            wizard.AddGold(-cost);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold = wizard.GameStats.m_currentGold, MaxGold = wizard.GameStats.m_baseGoldPouch });
        }
        else {
            cost = item.Crowns;
            ClassicCrowns.Add(wizard.Account, -cost);
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_RESPONSE {
            Item = request.Item, Error = 0, Cost = cost, Count = request.Count, Gifted = 0, Type = request.Type,
        });
        SendToSocket(ClassicCrowns.BalanceMessage(wizard.Account, wizard.CharId));
    }

    private void Fail(WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_REQUEST request, string reason) {
        Logger.Information("[CROWNSHOP] purchase of {0} refused: {1}.", Logger.Args(request.Item, reason));
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_RESPONSE {
            Item = request.Item, Error = 1, Cost = 0, Count = request.Count, Gifted = 0, Type = request.Type,
        });
    }

    // The catalog the profile allows, in the client's tabs.
    private static (FrozenDictionary<ulong, CrownShopEntry>, ByteString) BuildCatalog() {
        var offered = ClassicProgression.CrownShop?.Offered(ClassicRuntime.Rules.IsFeatureEnabled)
            ?? FrozenDictionary<ulong, CrownShopEntry>.Empty;

        return (offered, SerializeCatalog(offered.Values));
    }

    // The client's CrownShopData for these items.
    internal static ByteString SerializeCatalog(IEnumerable<CrownShopEntry> offered) {

        static int CategoryOf(string category) => category switch {
            CrownShopCategories.PermanentMounts => PermanentMountsCategory,
            CrownShopCategories.RentalMounts => RentalMountsCategory,
            CrownShopCategories.Henchmen => HenchmenCategory,
            _ => EverythingCategory,
        };

        var position = 0;
        var items = offered
            .OrderBy(item => CategoryOf(item.Category)).ThenBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => {
                position++;
                var category = CategoryOf(item.Category);
                var featured = item.Category == CrownShopCategories.PermanentMounts ? $",{FeaturedCategory}:{position}" : "";

                return new CrownShopItem {
                    m_itemTemplateId = (GID) item.Template,
                    m_itemFlags = 0,
                    m_goldCost = item.Gold,
                    m_crownsCost = item.Crowns,
                    m_ticketCost = 0,
                    m_displayPriority = $"{category}:{position},{EverythingCategory}:{position}{featured}",
                    m_strikethruCrowns = 0,
                    m_strikethruGold = 0,
                    m_description = "",
                    m_saleID = 1,
                    m_recommendIfOwned = false,
                    m_combatOnly = item.CombatOnly,
                    m_noGift = true,
                    m_segReqsStatement = "",
                    m_segReqsPoolsStatements = [],
                };
            })
            .ToList();

        static CrownShopCategory Category(int id, int tab, string name, string icon, string tags = "None",
                                          bool multiple = false, bool single = false, bool everything = false) => new() {
            m_ID = id, m_parentTabID = tab, m_name = name, m_description = "0", m_iconResource = icon, m_tags = tags,
            m_allowMultipleBuy = multiple, m_forceDisallowMultipleBuy = single, m_dontFilterOwnedRecoItems = false,
            m_isHousesCategory = false, m_isEverythingCategory = everything, m_isGroupElixirsCategory = false,
        };

        const string icons = "GUI/CrownShopIcons/Categories/";
        var layout = new CrownShopLayout {
            m_tabs = [
                new CrownShopCategoryMenu { m_ID = FeaturedTab, m_name = "CrownShopSWF_MenuFeatured", m_iconResource = icons + "Sale_Items.dds",
                    m_categoryIDs = [FeaturedCategory], m_tags = "", m_description = "Featured" },
                new CrownShopCategoryMenu { m_ID = MountsTab, m_name = "CrownShopSWF_MenuMounts", m_iconResource = icons + "Mounts_Permanent.dds",
                    m_categoryIDs = [PermanentMountsCategory, RentalMountsCategory], m_tags = "", m_description = "0" },
                new CrownShopCategoryMenu { m_ID = GameplayTab, m_name = "CrownShopSWF_MenuGameplay", m_iconResource = icons + "Everything.dds",
                    m_categoryIDs = [HenchmenCategory, EverythingCategory], m_tags = "", m_description = "0" },
            ],
            m_categories = [
                Category(FeaturedCategory, FeaturedTab, "CrownShopSWF_CategoryFeatured", icons + "Sale_Items.dds", "Featured"),
                Category(PermanentMountsCategory, MountsTab, "CrownShopSWF_CategoryPermanentMounts", icons + "Mounts_Permanent.dds", multiple: true),
                Category(RentalMountsCategory, MountsTab, "CrownShopSWF_CategoryRentalMounts", icons + "Mounts_Permanent.dds", single: true),
                Category(HenchmenCategory, GameplayTab, "CrownShopSWF_CategoryHenchmen", icons + "Everything.dds", "OpenToDuringCombat", single: true),
                Category(EverythingCategory, GameplayTab, "CrownShopSWF_CategoryEverything", icons + "Everything.dds", everything: true),
            ],
        };

        var data = new CrownShopData {
            m_items = items,
            m_crownShopLayout = layout,
            m_wishlistMaxSize = WishlistMaxSize,
            m_wishlistSBExpansionSize = WishlistExpansionSize,
            m_crownShopSegReqsSummary = new(),
            m_recomendedItems = new LevelData(),
        };

        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.SerializeFlags | SerializerFlags.Compress);
        if (!serializer.Serialize(data, PropertyFlags.Prop_Save | PropertyFlags.Prop_Public, out var serialized)) {
            Logger.Error("Crown Shop: could not serialize the catalog.");
            serialized = new ByteString();
        }

        Logger.Information("Crown Shop: {0} items offered.", Logger.Args(items.Count));

        return serialized;
    }

}
