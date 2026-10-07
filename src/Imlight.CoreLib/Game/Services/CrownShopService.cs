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
 * feat/crown-shop branch (Phill030). A henchman is bought only during a duel;
 * it is paid when asked for and refunded unless the duel confirms it joined
 * (MSG_HENCHMANHIRED) within 15 s.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5), after Phill030's upstream service
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
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
using Imlight.CoreLib.Classic.Elixirs;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Services;

internal class CrownShopService(SessionActor sessionActor) : MessageService(sessionActor) {

    // The existing tab/category layout and client's locale keys. Housing's new ids below
    // are unique server-provided layout references, not a claim about historic retail ids.
    private const int FeaturedTab = 37, MountsTab = 39, GameplayTab = 45;
    private const int HousingTab = 46, HousesCategory = 20, ElixirsTab = 47, ElixirsCategory = 21;
    private const int FeaturedCategory = 0, PermanentMountsCategory = 2, RentalMountsCategory = 3, HenchmenCategory = 18,
        EverythingCategory = 19;
    private const int WishlistMaxSize = 30, WishlistExpansionSize = 10;
    private const byte CurrencyGold = 0; // CurrencyTab: Gold = 0, Crowns = 1.

    private static readonly Lazy<(FrozenDictionary<ulong, CrownShopEntry> Items, ByteString Data)> s_catalog = new(BuildCatalog);

    private PendingHire _pendingHire;

    private sealed record PendingHire(WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_REQUEST Request, CrownShopEntry Item, bool PayWithGold);

    // CLASSIC: delayed shop notifications cannot replace CombatService's current trusted mode.
    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL))]
    private void ElixirDuelEntered(COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL message)
        => ElixirDuelLeft();
    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATWIN))]
    private void ElixirWon(COMBAT_106_PROTOCOL.MSG_COMBATWIN message) => ElixirDuelLeft();
    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT))]
    private void ElixirLost(COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT message) => ElixirDuelLeft();
    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE))]
    private void ElixirReleased(CLASSIC_FEATURES_PROTOCOL.MSG_PVPRELEASE message) => ElixirDuelLeft();
    private void ElixirDuelLeft() {
        if (GetActiveWizard() is { } wizard)
            TellOtherServices(new CLASSIC_FEATURES_PROTOCOL.MSG_ELIXIRCHANGED { CharacterId = wizard.CharId });
    }

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

        if (item.Category == CrownShopCategories.Henchmen && _pendingHire is not null) {
            Fail(message, "a henchman is already being hired");

            return;
        }

        // CLASSIC: native Use Now maps to PurchaseElixirEquipNow=1 (r806919
        // 0x140a61bb0 -> same +0x52c choice -> 0x140a5f08f -> 0x14217e450).
        // Save Later is 0 and is outside the dated immediately-active purchase policy.
        var purchaseTemplate = CoreObjectFactory.GetCoreTemplate((uint)item.Template) as WizItemTemplate;
        if (item.Category == CrownShopCategories.Elixirs || ElixirRuntime.IsElixir(purchaseTemplate)) {
            var definition = ElixirRules.Approved((uint)item.Template);
            if (item.Category != CrownShopCategories.Elixirs || definition is null || payWithGold
                || item.Gold != 0 || item.Crowns != definition.Crowns || !ElixirUseNow(message)
                || !ElixirRules.CanActivate(wizard, definition)) {
                Fail(message, "that elixir must be used now outside PvP");
                return;
            }
            var fresh = CoreObjectFactory.FinalizeCoreObject((uint)item.Template) as WizClientObjectItem;
            var purchased = ElixirCollection.Purchase(wizard, fresh, purchaseTemplate, true);
            if (!purchased.Saved) {
                if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
                Fail(message, purchased.Error);
                return;
            }
            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
                GlobalID = wizard.GameObjectID, SerializedItem = purchased.ItemData,
            });
            SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPITEM {
                ItemID = fresh.m_globalID, SlotName = ElixirRules.SlotName, IsEquip = 1,
            });
            // CLASSIC: account->character ACK and runtime effects complete together; the client must see the equipped original first.
            foreach (var packet in purchased.Activation.RuntimeMessages) SendToSocket(packet);
            TellOtherServices(new CLASSIC_FEATURES_PROTOCOL.MSG_ELIXIRCHANGED { CharacterId = wizard.CharId });
            Complete(wizard, message, item, false);
            return;
        }

        // CLASSIC: approved houses atomically save the account/gold debit, original deed
        // and portfolio. No ordinary payment or inventory give may run a second time.
        if (ClassicRuntime.IsActive && CoreObjectFactory.GetCoreTemplate((uint)item.Template) is WizItemTemplate deedTemplate
            && Classic.Housing.HouseCatalog.IsDeed(deedTemplate)) {
            var deed = CoreObjectFactory.FinalizeCoreObject((uint)item.Template) as WizClientObjectItem;
            var purchased = Classic.Housing.HouseCollection.Purchase(wizard, deed,
                payWithGold ? Classic.Housing.HouseCurrency.Gold : Classic.Housing.HouseCurrency.Crowns);
            if (!purchased.Saved) { Fail(message, purchased.Error); return; }
            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
                GlobalID = wizard.GameObjectID, SerializedItem = purchased.ItemData,
            });
            Complete(wizard, message, item, payWithGold);
            return;
        }

        if (item.Category == CrownShopCategories.Henchmen) {
            if (!TryPay(wizard, item, payWithGold)) {
                Fail(message, "cannot afford it");
                return;
            }
            // Paid now; the duel confirms the henchman joined (ReceiveHenchmanHired) or the payment is refunded, also
            // when no answer comes (the duel ended first).
            _pendingHire = new PendingHire(message, item, payWithGold);
            Timers.StartSingleTimer(HireTimeoutKey, new HireTimedOut(_pendingHire), HireTimeout);
            TellOtherServices(new COMBAT_106_PROTOCOL.MSG_HIREHENCHMAN { CreatureTid = (uint) item.Template });

            return;
        }

        // CLASSIC: payment, initialized item, rental expiry and saved backpack reference are one
        // acknowledged transaction. A lost acknowledgement is never a reason to refund or repeat it.
        var prepared = WizardInventoryTransactions.Prepare(wizard,
            CoreObjectFactory.FinalizeCoreObject((uint)item.Template) as WizClientObjectItem, true);
        if (prepared is null) { Fail(message, "the item could not be prepared"); return; }
        if (item.RentalDays is { } days) {
            if (!CoreObjectFactory.FindBehaviorInstance<ClientTimedItemBehavior>(prepared, out var timed)) {
                Fail(message, "the rental timer could not be prepared");
                return;
            }
            timed.m_expireTime = (uint)DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeSeconds();
        }
        try {
            if (!ClassicItemPurchases.Purchase(wizard, prepared, payWithGold ? item.Gold : item.Crowns,
                !payWithGold, out var purchased, item.MinLevel)) {
                if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
                Fail(message, "the saved balance or backpack refused it");
                return;
            }
            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
                GlobalID = wizard.GameObjectID, SerializedItem = purchased.Data,
            });
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) CloseSession();
            throw;
        }

        Complete(wizard, message, item, payWithGold);
    }

    private const string HireTimeoutKey = "henchman-hire-timeout";
    private static readonly TimeSpan HireTimeout = TimeSpan.FromSeconds(15);

    private sealed record HireTimedOut(PendingHire Hire);

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_HENCHMANHIRED))]
    private void ReceiveHenchmanHired(COMBAT_106_PROTOCOL.MSG_HENCHMANHIRED message) {
        var pending = _pendingHire;
        if (pending is null || pending.Item.Template != message.CreatureTid || GetActiveWizard() is not { } wizard) {
            return;
        }

        _pendingHire = null;
        Timers.Cancel(HireTimeoutKey);
        if (!message.Success) {
            Refund(wizard, pending.Item, pending.PayWithGold);
            Fail(pending.Request, "henchmen are hired during a duel with a free place on your side");

            return;
        }

        Complete(wizard, pending.Request, pending.Item, pending.PayWithGold);
    }

    [MessageHandler(typeof(HireTimedOut))]
    private void ReceiveHireTimedOut(HireTimedOut message) {
        if (!ReferenceEquals(_pendingHire, message.Hire) || GetActiveWizard() is not { } wizard) {
            return;
        }

        _pendingHire = null;
        Refund(wizard, message.Hire.Item, message.Hire.PayWithGold);
        Fail(message.Hire.Request, "the duel did not answer");
    }

    // A hire still waiting when the session closes is refunded.
    protected override void OnDispose() {
        if (_pendingHire is { } pending && GetActiveWizard() is { } wizard) {
            _pendingHire = null;
            Refund(wizard, pending.Item, pending.PayWithGold);
        }

        base.OnDispose();
    }

    private static bool TryPay(Wizard wizard, CrownShopEntry item, bool payWithGold)
        => payWithGold
            ? item.Gold > 0 && wizard.RemoveGold(item.Gold)
            : item.Crowns > 0 && wizard.Account is { } account && ClassicCrowns.TrySpend(account, item.Crowns);

    private static void Refund(Wizard wizard, CrownShopEntry item, bool payWithGold) {
        if (payWithGold) {
            wizard.RefundGold(item.Gold);
        }
        else if (wizard.Account is { } account) {
            ClassicCrowns.Add(account, item.Crowns);
        }

        Logger.Information("[CROWNSHOP] {0} refunded for {1}.", Logger.Args(wizard.CharId, item.Name));
    }

    // The purchase is paid already; tells the client.
    private void Complete(Wizard wizard, WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_REQUEST request, CrownShopEntry item, bool payWithGold) {
        var cost = payWithGold ? item.Gold : item.Crowns;
        if (payWithGold) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold = wizard.GameStats.m_currentGold, MaxGold = wizard.GameStats.m_baseGoldPouch });
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
        var offered = ClassicProgression.CrownShop?.Offered(ClassicRuntime.Rules.IsFeatureEnabled, ClassicRuntime.Rules.Profile.Id)
            ?? FrozenDictionary<ulong, CrownShopEntry>.Empty;
        // Only a dated/owner-approved definition with an actual Crowns price is projected;
        // gold-only world vendor houses do not acquire a fabricated Crowns price.
        var houses = ClassicRuntime.IsActive ? Classic.Housing.HouseCatalog.Approved.Where(h => h.Crowns > 0)
            .Select(h => new CrownShopEntry(h.Name, h.TemplateId, CrownShopCategories.Houses, h.Crowns,
                h.Gold, null, h.MinimumLevel, false, null)) : [];
        var shown = ForClient(ForProfile(offered.Values.Concat(houses), ClassicRuntime.Rules)).ToFrozenDictionary(item => item.Template);

        return (shown, SerializeCatalog(shown.Values));
    }

    /// <summary>
    /// CLASSIC: the catalog as the r806919 client can show it (owner client log 2026-10-04 03:12, Crown Shop open).
    /// <list type="bullet">
    /// <item>Henchmen are left out: the client's PermanentShop rejects every henchman template ("templateID N not
    /// found"; it lists items, and a henchman is a creature), and the client has no other way to hire one (no henchman
    /// message in its protocol), so an offered henchman could never be seen or bought.</item>
    /// <item>A gold-only item (the 2009 1-day mount rentals) gets a Crowns price: the client drops a row without one
    /// ("Invalid Crowns Price Recieved!"). It is the gold price / 10, rounded up: the ratio of every 2009 rental sold
    /// for both (7-day Enchanted Broom 1,000 Crowns or 10,000 gold). The gold price stays as 2009 had it.</item>
    /// </list>
    /// </summary>
    internal static IEnumerable<CrownShopEntry> ForClient(IEnumerable<CrownShopEntry> offered)
        => offered
            .Where(item => item.Category != CrownShopCategories.Henchmen)
            .Select(item => item.Crowns <= 0 && item.Gold > 0 ? item with { Crowns = (item.Gold + 9) / 10 } : item);

    // Catalog feature flags alone never authorize an unverified elixir or its price.
    internal static IEnumerable<CrownShopEntry> ForProfile(IEnumerable<CrownShopEntry> offered, ClassicRules rules)
        => offered.Where(item => item.Category != CrownShopCategories.Elixirs
            || item.Template < (1UL << 28) && ElixirRules.Approved(rules, (uint)item.Template) is { } definition
                && item.Gold == 0 && item.Crowns == definition.Crowns);

    internal static bool ElixirUseNow(WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_REQUEST request)
        => request.PurchaseElixirEquipNow == 1;

    // The client's CrownShopData for these items.
    internal static ByteString SerializeCatalog(IEnumerable<CrownShopEntry> offered) {

        static int CategoryOf(string category) => category switch {
            CrownShopCategories.PermanentMounts => PermanentMountsCategory,
            CrownShopCategories.RentalMounts => RentalMountsCategory,
            CrownShopCategories.Henchmen => HenchmenCategory,
            CrownShopCategories.Houses => HousesCategory,
            CrownShopCategories.Elixirs => ElixirsCategory,
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
        if (items.Any(item => item.m_displayPriority.StartsWith($"{HousesCategory}:"))) {
            // CLASSIC: category/menu ids arrive in CrownShopLayout. Native names/icon and
            // m_isHousesCategory select its existing house UI; no program patch is needed.
            layout.m_tabs.Add(new CrownShopCategoryMenu {
                m_ID = HousingTab, m_name = "CrownShopSWF_MenuHousing", m_iconResource = icons + "Housing.dds",
                m_categoryIDs = [HousesCategory], m_tags = "", m_description = "CrownShopSWF_MenuHousing_Desc",
            });
            var housesCategory = Category(HousesCategory, HousingTab, "CrownShopSWF_CategoryHouses", icons + "Housing.dds", single: true);
            housesCategory.m_isHousesCategory = true;
            housesCategory.m_description = "CrownShopSWF_CategoryHouses_Desc";
            layout.m_categories.Add(housesCategory);
        }
        if (items.Any(item => item.m_displayPriority.StartsWith($"{ElixirsCategory}:"))) {
            // Native locale keys select this menu. The dedicated icon path is unproved;
            // retain the existing Everything icon rather than invent a resource name.
            layout.m_tabs.Add(new CrownShopCategoryMenu {
                m_ID = ElixirsTab, m_name = "CrownShopSWF_MenuElixirs", m_iconResource = icons + "Everything.dds",
                m_categoryIDs = [ElixirsCategory], m_tags = "", m_description = "CrownShopSWF_MenuElixirs_Desc",
            });
            layout.m_categories.Add(Category(ElixirsCategory, ElixirsTab, "CrownShopSWF_CategoryElixirs",
                icons + "Everything.dds", single: true));
        }

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
