// CLASSIC: one acknowledged character/stock/delivery commit for every native Bazaar transaction.
// Lock order is Bazaar stock -> wizard write lane. Failed or uncertain saves never refund or retry.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Classic.Bazaar;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.Classic;

internal sealed record BazaarPreparedItem(WizClientObjectItem Item, ByteString Data);
internal sealed record BazaarTransactionQuote(int Cost, int AvailableCopies, ulong TemplateId, int Kind);
internal sealed record BazaarTransactionReceipt(int Cost, int Quantity, int Gold, int MaxGold, int Kind,
    ulong TemplateId, AuctionHouseEntry Stock, IReadOnlyList<BazaarPreparedItem> AddedItems,
    IReadOnlyList<ulong> RemovedItemIds, ClientReagentItem ReagentUpdate, bool IsSell, ByteString ReagentData);

// CLASSIC: detached dependency seams let persistence tests use fresh tracked sessions, never a live database.
internal sealed class BazaarTransactionDependencies {
    internal Func<IDocumentSession> Open;
    internal Func<IDocumentSession, ulong, Wizard> LoadWizard;
    internal Func<IDocumentSession, ulong, List<AuctionHouseEntry>> LoadStock;
    internal Func<IDocumentSession, ulong, List<WizClientObjectItem>> LoadItems;
    internal Func<IDocumentSession, ulong, bool> ItemGlobalExists;
    internal Func<ulong, CoreTemplate> Template;
    internal Func<ulong, CoreObject> Create;
    internal Func<WizClientObjectItem, ByteString> Serialize;
    internal Func<ClientReagentItem, ByteString> SerializeReagent;
    internal Func<ulong, int, (int Buy, int Sell)> Prices;
    internal Func<CoreTemplate, int> StockCap;
    internal Func<ulong, bool> IsTreasure;
    internal Action<AuctionHouseEntry> PublishStock;
    internal Action<ulong> NoteRealSale;
    internal System.Action InvalidateStock;
}

internal static class ClassicBazaarTransactions {
    internal static readonly AsyncLocal<BazaarTransactionDependencies> TestScope = new();
    private static readonly ConditionalWeakTable<Wizard, object> s_uncertain = new();
    internal static bool IsQuarantined(Wizard live) => live is not null
        && (s_uncertain.TryGetValue(live, out _) || WizardCollection.IsInventorySnapshotUncertain(live));
    private static BazaarTransactionDependencies Dependencies => TestScope.Value ?? new();

    internal static bool Buy(Wizard live, int nativeKind, ulong templateId, int quantity, int texture, int decal,
        out BazaarTransactionReceipt receipt) {
        receipt = null;
        if (!ValidRequest(live, nativeKind, quantity) || templateId == 0) return false;
        var d = Dependencies;
        var template = Resolve(d, templateId);
        if (!MatchesKind(d, templateId, template, nativeKind)) return false;
        List<BazaarPreparedItem> prepared;
        ClientReagentItem reagent;
        // Prepare native objects and every item packet before opening the payment transaction.
        try {
            prepared = []; reagent = null;
            if (nativeKind is 0 or 1) {
                for (var i = 0; i < quantity; i++) {
                    if (Create(d, templateId) is not WizClientObjectItem item || item.m_globalID == 0
                        || item.m_templateID.Full != templateId) return false;
                    item.m_characterId = live.CharId;
                    ShopService.ApplyBuyDyes(item, (WizItemTemplate)template, texture, decal);
                    var data = Serialize(d, item);
                    if (data.Length == 0) return false;
                    prepared.Add(new(item, data));
                }
                if (prepared.Select(p => p.Item.m_globalID.Full).Distinct().Count() != quantity) return false;
            }
            else if (nativeKind == 3) {
                reagent = Create(d, templateId) as ClientReagentItem;
                if (reagent is null || reagent.m_globalID == 0 || reagent.m_templateID.Full != templateId) return false;
                reagent.m_characterId = live.CharId;
            }
        }
        catch { return false; }

        BazaarTransactionReceipt committed = null;
        List<WizClientObjectItem> itemsForPublish = null;
        var staged = false;
        lock (AuctionHouseCollection.Lock) {
            if (IsQuarantined(live)) return false;
            try {
                var ok = Commit(d, live, (session, saved) => {
                    if (IsQuarantined(live) || !ValidWallet(saved, live.CharId)) return false;
                    var lots = LoadStock(d, session, templateId);
                    if (lots.Count != 1 || !ValidStock(lots[0], templateId) || lots[0].m_numForSale < quantity) return false;
                    var stock = lots[0];
                    var cost = (long) stock.m_buyPrice * quantity;
                    if (stock.m_buyPrice <= 0 || cost > int.MaxValue || saved.GameStats.m_currentGold < cost) return false;
                    List<WizClientObjectItem> items = null;
                    ClientReagentItem updated = null;
                    if (nativeKind is 0 or 1) {
                        items = LoadItems(d, session, saved.CharId);
                        if (!ValidBackpack(saved, items) || saved.InventoryBehavior.InventoryItemIds.Count + quantity
                            > ServerWizInventoryBehavior.MaxItemsAllowed) return false;
                        // Each prepared item needs a uniqueness query and an explicit document-ID check.
                        session.Advanced.MaxNumberOfRequestsPerSession = checked(quantity * 2 + 8);
                        foreach (var p in prepared) {
                            if (items.Any(i => i.m_globalID == p.Item.m_globalID) || GlobalExists(d, session, p.Item.m_globalID.Full)) return false;
                        }
                        foreach (var p in prepared) {
                            var id = $"ClassicBazaarItems/{saved.CharId}/{p.Item.m_globalID.Full}";
                            if (session.Load<WizClientObjectItem>(id) is not null) return false;
                            session.Store(p.Item, id);
                            session.Advanced.GetMetadataFor(p.Item)[Raven.Client.Constants.Documents.Metadata.Collection] = WizardItemCollection.CollectionName;
                        }
                        saved.InventoryBehavior.InventoryItemIds = [..saved.InventoryBehavior.InventoryItemIds, ..prepared.Select(p => p.Item.m_globalID.Full)];
                        items.AddRange(prepared.Select(p => p.Item));
                        itemsForPublish = items;
                    }
                    else if (nativeKind == 2) {
                        if (!ValidTreasureBook(saved, quantity)) return false;
                        saved.SpellbookBehavior.TreasureCardTemplateIds = [..saved.SpellbookBehavior.TreasureCardTemplateIds,
                            ..Enumerable.Repeat((uint)templateId, quantity)];
                    }
                    else if (!WizardReagentCollection.TryStageAdd(session, saved, reagent, quantity, out updated)) return false;
                    var reagentData = updated is null ? default : SerializeReagent(d, updated);
                    if (updated is not null && reagentData.Length == 0) return false;
                    saved.GameStats.m_currentGold -= (int)cost;
                    stock.m_numForSale -= quantity;
                    if (stock.m_numForSale == 0) session.Delete(stock);
                    else SetQuotes(d, stock, templateId, stock.m_numForSale);
                    committed = new((int)cost, quantity, saved.GameStats.m_currentGold, saved.GameStats.m_baseGoldPouch,
                        nativeKind, templateId, AuctionHouseCollection.Snapshot(stock), prepared, [], updated, false, reagentData);
                    staged = true;
                    return true;
                }, saved => {
                    PublishWalletAndBook(live, saved);
                    if (itemsForPublish is not null) PublishBackpack(live, saved, itemsForPublish);
                    if (committed.ReagentUpdate is not null)
                        committed = committed with { ReagentUpdate = WizardReagentCollection.PublishCommittedBag(live, saved, committed.ReagentUpdate) };
                    PublishStock(d, committed.Stock);
                });
                if (!ok) return false;
                receipt = committed;
                return true;
            }
            catch {
                if (staged) { s_uncertain.GetValue(live, _ => new object()); WizardCollection.MarkInventorySnapshotUncertain(live); }
                Invalidate(d);
                return false;
            }
        }
    }

    internal static bool Sell(Wizard live, int nativeKind, ulong templateId, ulong ownedGlobalId, int quantity,
        out BazaarTransactionReceipt receipt) {
        receipt = null;
        if (!ValidRequest(live, nativeKind, quantity) || nativeKind is 0 or 1 && (quantity != 1 || ownedGlobalId == 0)
            || nativeKind == 3 && ownedGlobalId == 0) return false;
        var d = Dependencies;
        BazaarTransactionReceipt committed = null;
        List<WizClientObjectItem> itemsForPublish = null;
        var staged = false;
        lock (AuctionHouseCollection.Lock) {
            if (IsQuarantined(live)) return false;
            try {
                var ok = Commit(d, live, (session, saved) => {
                    if (IsQuarantined(live) || !ValidWallet(saved, live.CharId)) return false;
                    var actualTemplate = templateId;
                    List<WizClientObjectItem> items = null;
                    WizClientObjectItem owned = null;
                    if (nativeKind is 0 or 1) {
                        items = LoadItems(d, session, saved.CharId);
                        if (!ValidBackpack(saved, items)) return false;
                        var matches = items.Where(i => i.m_globalID.Full == ownedGlobalId).ToList();
                        if (matches.Count != 1 || !saved.InventoryBehavior.InventoryItemIds.Contains(ownedGlobalId)) return false;
                        owned = matches[0];
                        if (actualTemplate != 0 && actualTemplate != owned.m_templateID.Full) return false;
                        actualTemplate = owned.m_templateID.Full;
                        if (saved.SpellbookBehavior?.DeckTreasureTotal(ownedGlobalId) > 0) return false;
                    }
                    var template = Resolve(d, actualTemplate);
                    if (actualTemplate == 0 || !MatchesKind(d, actualTemplate, template, nativeKind)) return false;
                    if (nativeKind == 2 && (!ValidTreasureBook(saved, 0)
                        || saved.SpellbookBehavior.TreasureCardTemplateIds.Count(id => id == actualTemplate) < quantity)) return false;
                    var lots = LoadStock(d, session, actualTemplate);
                    if (lots.Count > 1 || lots.Count == 1 && !ValidStock(lots[0], actualTemplate)) return false;
                    var held = lots.Count == 0 ? 0 : lots[0].m_numForSale;
                    var unit = Prices(d, actualTemplate, held).Sell;
                    var cost = (long)unit * quantity;
                    if (unit < 0 || cost > int.MaxValue || saved.GameStats.m_currentGold + cost > saved.GameStats.m_baseGoldPouch) return false;
                    ClientReagentItem updated = null;
                    if (owned is not null) {
                        session.Delete(owned); // Exact tracked original document, never a forged replacement.
                        saved.InventoryBehavior.InventoryItemIds = saved.InventoryBehavior.InventoryItemIds.Where(id => id != ownedGlobalId).ToList();
                        items.Remove(owned); itemsForPublish = items;
                    }
                    else if (nativeKind == 2) {
                        var remaining = saved.SpellbookBehavior.TreasureCardTemplateIds.ToList();
                        for (var i = 0; i < quantity; i++) if (!remaining.Remove((uint)actualTemplate)) return false;
                        saved.SpellbookBehavior.TreasureCardTemplateIds = remaining;
                    }
                    else if (!WizardReagentCollection.TryStageRemove(session, saved, ownedGlobalId, quantity, out updated)
                        || updated.m_templateID.Full != actualTemplate) return false;
                    var reagentData = updated is null || updated.m_quantity == 0 ? default : SerializeReagent(d, updated);
                    if (updated is not null && updated.m_quantity > 0 && reagentData.Length == 0) return false;
                    var cap = d.StockCap?.Invoke(template) ?? ClassicBazaar.Rules?.CapFor(ClassicBazaar.KindOf(template) ?? BazaarKind.Gear) ?? 99;
                    if (cap < 1 || held > cap) return false;
                    var stock = lots.Count == 1 ? lots[0] : new AuctionHouseEntry { m_templateID = (GID)actualTemplate };
                    stock.m_numForSale = (int)Math.Min(cap, (long)held + quantity);
                    SetQuotes(d, stock, actualTemplate, stock.m_numForSale);
                    if (lots.Count == 0) {
                        session.Store(stock);
                        session.Advanced.GetMetadataFor(stock)[Raven.Client.Constants.Documents.Metadata.Collection] = AuctionHouseCollection.CollectionName;
                    }
                    saved.GameStats.m_currentGold += (int)cost;
                    committed = new((int)cost, quantity, saved.GameStats.m_currentGold, saved.GameStats.m_baseGoldPouch, nativeKind,
                        actualTemplate, AuctionHouseCollection.Snapshot(stock), [], owned is null ? [] : new[] { ownedGlobalId }, updated, true, reagentData);
                    staged = true;
                    return true;
                }, saved => {
                    PublishWalletAndBook(live, saved);
                    if (itemsForPublish is not null) PublishBackpack(live, saved, itemsForPublish);
                    if (committed.ReagentUpdate is not null)
                        committed = committed with { ReagentUpdate = WizardReagentCollection.PublishCommittedBag(live, saved, committed.ReagentUpdate) };
                    // The player-sale shield is part of ACK publication while the stock lock is held.
                    if (d.NoteRealSale is not null) d.NoteRealSale(committed.TemplateId);
                    else ClassicBazaar.NoteRealSale(committed.TemplateId);
                    PublishStock(d, committed.Stock);
                });
                if (!ok) return false;
                receipt = committed;
                return true;
            }
            catch {
                if (staged) { s_uncertain.GetValue(live, _ => new object()); WizardCollection.MarkInventorySnapshotUncertain(live); }
                Invalidate(d);
                return false;
            }
        }
    }

    // CLASSIC: a native lock/quote request is read-only, including wallet and stock documents.
    internal static bool Quote(Wizard live, bool isSell, int nativeKind, ulong templateId, ulong ownedGlobalId, int quantity,
        out BazaarTransactionQuote quote) {
        quote = null;
        if (!ValidRequest(live, nativeKind, quantity)) return false;
        var d = Dependencies;
        BazaarTransactionQuote found = null;
        lock (AuctionHouseCollection.Lock) {
            try {
                var ok = WizardCollection.WithCharacterLock(live.CharId, () => {
                    if (IsQuarantined(live)) return false;
                    using var session = Open(d);
                    var saved = LoadWizard(d, session, live.CharId);
                    if (!ValidWallet(saved, live.CharId)) return false;
                    var actualTemplate = templateId;
                    var available = 0;
                    if (isSell && nativeKind is 0 or 1) {
                        if (ownedGlobalId == 0 || quantity != 1) return false;
                        var items = LoadItems(d, session, saved.CharId);
                        if (!ValidBackpack(saved, items)) return false;
                        var matches = items.Where(i => i.m_globalID.Full == ownedGlobalId).ToList();
                        if (matches.Count != 1 || !saved.InventoryBehavior.InventoryItemIds.Contains(ownedGlobalId)) return false;
                        if (actualTemplate != 0 && matches[0].m_templateID.Full != actualTemplate) return false;
                        actualTemplate = matches[0].m_templateID.Full; available = 1;
                        if (saved.SpellbookBehavior?.DeckTreasureTotal(ownedGlobalId) > 0) return false;
                    }
                    var template = Resolve(d, actualTemplate);
                    if (!MatchesKind(d, actualTemplate, template, nativeKind)) return false;
                    if (isSell && nativeKind == 2) {
                        if (!ValidTreasureBook(saved, 0)) return false;
                        available = saved.SpellbookBehavior.TreasureCardTemplateIds.Count(id => id == actualTemplate);
                    }
                    if (isSell && nativeKind == 3) {
                        // Read-only validation; no staging helper is called for a quote.
                        if (!WizardReagentCollection.TryReadOwnedBag(session, saved, out var bag)) return false;
                        var rows = bag.Where(r => r.m_globalID.Full == ownedGlobalId && r.m_templateID.Full == actualTemplate).ToList();
                        if (rows.Count != 1) return false;
                        available = rows[0].m_quantity;
                    }
                    var lots = LoadStock(d, session, actualTemplate);
                    if (lots.Count > 1 || lots.Count == 1 && !ValidStock(lots[0], actualTemplate)) return false;
                    var held = lots.Count == 0 ? 0 : lots[0].m_numForSale;
                    if (!isSell) available = held;
                    if (available < quantity) return false;
                    var cost = isSell ? Prices(d, actualTemplate, held).Sell : lots[0].m_buyPrice;
                    if (cost < 0 || !isSell && cost == 0) return false;
                    found = new(cost, available, actualTemplate, nativeKind);
                    return true;
                });
                if (!ok) return false;
                quote = found;
                return true;
            }
            catch { Invalidate(d); return false; }
        }
    }

    private static bool ValidRequest(Wizard live, int kind, int quantity)
        => live?.CharId is > 0 && !IsQuarantined(live) && kind is >= 0 and <= 3 && quantity is > 0 and <= 999;
    private static bool ValidWallet(Wizard saved, ulong owner)
        => saved?.CharId == owner && saved.GameStats is not null && saved.GameStats.m_currentGold >= 0
            && saved.GameStats.m_baseGoldPouch > 0 && saved.GameStats.m_currentGold <= saved.GameStats.m_baseGoldPouch;
    private static bool ValidStock(AuctionHouseEntry stock, ulong template)
        => stock.m_templateID.Full == template && stock.m_numForSale > 0 && stock.m_buyPrice > 0 && stock.m_sellPrice >= 0;
    private static bool MatchesKind(BazaarTransactionDependencies d, ulong id, CoreTemplate template, int kind) {
        var classified = ClassicBazaar.KindOf(template);
        return kind switch {
            0 => template is WizItemTemplate && classified == BazaarKind.Gear,
            1 => template is WizItemTemplate && classified == BazaarKind.Housing,
            2 => id <= uint.MaxValue && template is SpellTemplate spell && !string.IsNullOrEmpty(spell.m_name)
                && classified == BazaarKind.TreasureCard
                && (d.IsTreasure?.Invoke(id) ?? CoreObjectFactory.GetTemplatePath(id)?.StartsWith("Spells/TreasureCards/", StringComparison.OrdinalIgnoreCase) == true),
            3 => template is ReagentItemTemplate && classified == BazaarKind.Reagent,
            _ => false,
        };
    }
    private static bool ValidTreasureBook(Wizard saved, int addition) {
        var book = saved.SpellbookBehavior;
        if (book?.TreasureCardTemplateIds is null || book.TreasureCardTemplateIds.Any(id => id == 0)
            || book.DeckTreasureCards is null) return false;
        foreach (var deck in book.DeckTreasureCards) {
            if (deck.Key == 0 || deck.Value is null || deck.Value.Any(p => p.Key == 0 || p.Value <= 0)) return false;
        }
        return addition == 0 ? book.TreasureCardTemplateIds.Count <= 999 : WizardCollection.CanReceiveTreasureCards(saved, addition);
    }
    private static bool ValidBackpack(Wizard saved, List<WizClientObjectItem> items) {
        var ids = saved.InventoryBehavior?.InventoryItemIds;
        if (ids is null || ids.Any(id => id == 0) || ids.Distinct().Count() != ids.Count || items is null
            || items.Any(i => i is null || i.m_characterId.Full != saved.CharId || i.m_globalID == 0)
            || items.GroupBy(i => i.m_globalID.Full).Any(g => g.Count() != 1)) return false;
        var known = items.Select(i => i.m_globalID.Full).ToHashSet();
        if (ids.Any(id => !known.Contains(id))) return false;
        var elsewhere = (saved.EquipmentBehavior?.EquippedItemIds ?? []).Concat(saved.StorageBehavior?.BankItemIds ?? []).ToHashSet();
        return !ids.Any(elsewhere.Contains);
    }
    private static CoreTemplate Resolve(BazaarTransactionDependencies d, ulong id) => d.Template is null ? CoreObjectFactory.GetCoreTemplate(id) : d.Template(id);
    private static CoreObject Create(BazaarTransactionDependencies d, ulong id) => d.Create is null ? CoreObjectFactory.FinalizeCoreObject(id) : d.Create(id);
    private static ByteString Serialize(BazaarTransactionDependencies d, WizClientObjectItem item) {
        if (d.Serialize is not null) return d.Serialize(item);
        return new CoreObjectSerializer(behaviors: SerializerFlags.None).Serialize(item, 1, out var data) ? data : default;
    }
    private static ByteString SerializeReagent(BazaarTransactionDependencies d, ClientReagentItem reagent) {
        if (d.SerializeReagent is not null) return d.SerializeReagent(reagent);
        return new CoreObjectSerializer(behaviors: SerializerFlags.None).Serialize(reagent, 27, out var data) ? data : default;
    }
    private static IDocumentSession Open(BazaarTransactionDependencies d)
        => d.Open is not null ? d.Open() : WizardCollection.TestStoreScope.Value is { } test ? test.Open() : PlayerDatabase.Instance.Store.OpenSession();
    private static Wizard LoadWizard(BazaarTransactionDependencies d, IDocumentSession session, ulong owner)
        => d.LoadWizard is not null ? d.LoadWizard(session, owner) : WizardCollection.TestStoreScope.Value is { } test ? test.Load(session, owner)
            : session.Query<Wizard>(collectionName: WizardCollection.CollectionName).Customize(q => q.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .FirstOrDefault(w => w.CharId == owner);
    private static bool Commit(BazaarTransactionDependencies d, Wizard live, Func<IDocumentSession, Wizard, bool> stage, Action<Wizard> publish)
        => WizardCollection.CommitCharacterMutation(live.CharId, stage, publish, d.Open, d.LoadWizard,
            onSaveFailure: _ => {
                s_uncertain.GetValue(live, _ => new object());
                WizardCollection.MarkInventorySnapshotUncertain(live);
                Invalidate(d);
            });
    private static List<AuctionHouseEntry> LoadStock(BazaarTransactionDependencies d, IDocumentSession session, ulong template)
        => d.LoadStock?.Invoke(session, template) ?? AuctionHouseCollection.QueryStock(session).ToList().Where(s => s.m_templateID.Full == template).ToList();
    private static List<WizClientObjectItem> LoadItems(BazaarTransactionDependencies d, IDocumentSession session, ulong owner)
        => d.LoadItems?.Invoke(session, owner) ?? session.Query<WizClientObjectItem>(collectionName: WizardItemCollection.CollectionName)
            .Customize(q => q.WaitForNonStaleResults(TimeSpan.FromSeconds(5))).Where(i => i.m_characterId == owner).Take(int.MaxValue).ToList();
    private static bool GlobalExists(BazaarTransactionDependencies d, IDocumentSession session, ulong id)
        => d.ItemGlobalExists?.Invoke(session, id) ?? session.Query<WizClientObjectItem>(collectionName: WizardItemCollection.CollectionName)
            .Customize(q => q.WaitForNonStaleResults(TimeSpan.FromSeconds(5))).Any(i => i.m_globalID == id);
    private static (int Buy, int Sell) Prices(BazaarTransactionDependencies d, ulong id, int copies) {
        if (d.Prices is not null) return d.Prices(id, copies);
        if (ClassicBazaar.Rules is not null) return ClassicBazaar.PricesFor(id, copies);
        var cost = ClassicBazaar.BaseCostOf(Resolve(d, id));
        return (checked(cost * 2), (int)Math.Ceiling(cost * .5));
    }
    private static void SetQuotes(BazaarTransactionDependencies d, AuctionHouseEntry stock, ulong id, int copies) {
        var prices = Prices(d, id, copies);
        if (prices.Buy <= 0 || prices.Sell < 0) throw new InvalidOperationException("Invalid Bazaar quote.");
        stock.m_buyPrice = prices.Buy; stock.m_sellPrice = prices.Sell;
    }
    private static void PublishWalletAndBook(Wizard live, Wizard saved) {
        live.GameStats.m_currentGold = saved.GameStats.m_currentGold;
        if (live.SpellbookBehavior is not null && saved.SpellbookBehavior is not null)
            WizardCollection.PublishTreasureCards(live, saved);
    }
    private static void PublishBackpack(Wizard live, Wizard saved, List<WizClientObjectItem> items) {
        live.InventoryBehavior.InventoryItemIds = saved.InventoryBehavior.InventoryItemIds.ToList();
        var ids = live.InventoryBehavior.InventoryItemIds.ToHashSet();
        live.InventoryBehavior.Items = [..items.Where(i => ids.Contains(i.m_globalID.Full))];
    }
    private static void PublishStock(BazaarTransactionDependencies d, AuctionHouseEntry stock) {
        if (d.PublishStock is not null) d.PublishStock(AuctionHouseCollection.Snapshot(stock));
        else AuctionHouseCollection.PublishCommitted(stock);
    }
    private static void Invalidate(BazaarTransactionDependencies d) {
        if (d.InvalidateStock is not null) d.InvalidateStock();
        else AuctionHouseCollection.InvalidateCache();
    }
}
