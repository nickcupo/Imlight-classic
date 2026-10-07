// CLASSIC: failure-atomic Bazaar checks against detached, tracked Raven-style session fixtures.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class BazaarTransactionPersistenceTests {
    public BazaarTransactionPersistenceTests() {
        // Retain the authored fixture file; the owner permanently deletes files.
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "[Logging]\nLogLevel=FATAL\nLogPath=/private/tmp/bazaar-transaction-tests.log\n[Character]\nMaxInventoryItems=150\n[Classic]\nBackpackSize=150\n");
        ConfigurationManager.Initialize(path);
    }

    [Fact]
    public void PurchaseUsesSavedWalletAndCommitsBookAndStockOnceBeforePublication() {
        var f = new Fixture(2); using var scope = f.Scope();
        var live = f.Live(); live.GameStats.m_currentGold = 0;
        f.BeforeSave = () => {
            Assert.Equal(0, live.GameStats.m_currentGold);
            Assert.Empty(live.SpellbookBehavior.TreasureCardTemplateIds);
            Assert.Empty(f.Published);
        };
        Assert.True(ClassicBazaarTransactions.Buy(live, 2, Fixture.Tid, 2, 0, 0, out var receipt));
        Assert.Equal(200, receipt.Cost); Assert.Equal(700, f.Saved.GameStats.m_currentGold);
        Assert.Equal(700, live.GameStats.m_currentGold);
        Assert.Equal(new uint[] { (uint)Fixture.Tid, (uint)Fixture.Tid }, f.Saved.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(3, Assert.Single(f.Stocks.Values).m_numForSale);
        Assert.Equal("stock/original", Assert.Single(f.Stocks.Keys));
        Assert.Equal(1, f.Saves); Assert.Single(f.Published); Assert.Equal(0, f.Deletes);
    }

    [Fact]
    public void StaleAffordabilityNeverSpendsOrDeliversAnything() {
        var f = new Fixture(2); using var scope = f.Scope();
        f.Saved.GameStats.m_currentGold = 5;
        var live = f.Live(); live.GameStats.m_currentGold = 9999;
        Assert.False(ClassicBazaarTransactions.Buy(live, 2, Fixture.Tid, 1, 0, 0, out _));
        Assert.Equal(5, f.Saved.GameStats.m_currentGold); Assert.Empty(f.Saved.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(5, Assert.Single(f.Stocks.Values).m_numForSale); Assert.Equal(0, f.Saves); Assert.Empty(f.Published);
    }

    [Fact]
    public void QuarantineRaisedBeforeStagingIsRecheckedInsideTheWriteLane() {
        var f = new Fixture(2); using var scope = f.Scope();
        var live = f.Live();
        f.Dependencies.LoadWizard = (session, _) => {
            Assert.True(WizardCollection.HoldsWriteLane);
            WizardCollection.MarkInventorySnapshotUncertain(live);
            return Fixture.Session(session).Wizard;
        };
        Assert.False(ClassicBazaarTransactions.Buy(live, 2, Fixture.Tid, 1, 0, 0, out _));
        Assert.Equal(0, f.Saves); Assert.Equal(900, f.Saved.GameStats.m_currentGold);
        Assert.Empty(f.Saved.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(5, Assert.Single(f.Stocks.Values).m_numForSale);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void BackpackAndHousingPurchasePreparePacketsAndSaveExactNewOwnedDocuments(int kind) {
        var f = new Fixture(kind); using var scope = f.Scope();
        var live = f.Live();
        Assert.True(ClassicBazaarTransactions.Buy(live, kind, Fixture.Tid, 2, 0, 0, out var receipt));
        Assert.Equal(2, receipt.AddedItems.Count); Assert.Equal(2, f.Items.Count);
        Assert.All(receipt.AddedItems, p => Assert.True(p.Data.Length > 0));
        Assert.All(f.Items.Values, i => Assert.Equal(Fixture.Char, i.m_characterId.Full));
        Assert.Equal(f.Saved.InventoryBehavior.InventoryItemIds, live.InventoryBehavior.InventoryItemIds);
        Assert.Equal(2, live.InventoryBehavior.Items.Count); Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void ValidLargeItemBatchCompletesBeyondTheDefaultSessionRequestCeiling(int kind) {
        var f = new Fixture(kind); using var scope = f.Scope();
        f.Saved.GameStats.m_currentGold = 9000;
        f.Stocks.Values.Single().m_numForSale = 50;
        Assert.True(ClassicBazaarTransactions.Buy(f.Live(), kind, Fixture.Tid, 35, 0, 0, out var receipt));
        Assert.True(f.LastRequestCount > 30);
        Assert.Equal(35, receipt.AddedItems.Count); Assert.Equal(35, f.Items.Count);
        Assert.Equal(5500, f.Saved.GameStats.m_currentGold); Assert.Equal(15, Assert.Single(f.Stocks.Values).m_numForSale);
        Assert.Equal(1, f.Saves);
    }

    [Fact]
    public void PreparationFailureOccursBeforePaymentSessionAndCapacityFailureDoesNotSave() {
        var f = new Fixture(0); using var scope = f.Scope();
        var live = f.Live(); f.Dependencies.Serialize = _ => default;
        Assert.False(ClassicBazaarTransactions.Buy(live, 0, Fixture.Tid, 1, 0, 0, out _));
        Assert.Equal(0, f.Opens); Assert.Equal(0, f.Saves); Assert.Equal(900, f.Saved.GameStats.m_currentGold);
        f.Dependencies.Serialize = _ => new ByteString(new byte[] { 1 });
        for (var n = 0; n < 150; n++) f.AddItem((ulong)(3000 + n));
        live = f.Live();
        Assert.False(ClassicBazaarTransactions.Buy(live, 0, Fixture.Tid, 1, 0, 0, out _));
        Assert.Equal(0, f.Saves); Assert.Equal(150, f.Items.Count);
    }

    [Fact]
    public void TreasureCapacityPreservesExistingFreeBookLimitAndProtectsDeckCardsOnSale() {
        var f = new Fixture(2); using var scope = f.Scope();
        f.Saved.SpellbookBehavior.TreasureCardTemplateIds = Enumerable.Repeat((uint)Fixture.Tid, 999).ToList();
        f.Saved.SpellbookBehavior.DeckTreasureCards = new() { [77] = new() { [(uint)Fixture.Tid] = 3 } };
        var live = f.Live();
        Assert.False(ClassicBazaarTransactions.Buy(live, 2, Fixture.Tid, 1, 0, 0, out _));
        Assert.True(ClassicBazaarTransactions.Sell(live, 2, Fixture.Tid, 0, 2, out var receipt));
        Assert.Equal(100, receipt.Cost); Assert.Equal(997, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(3, f.Saved.SpellbookBehavior.DeckTreasureCount(77, (uint)Fixture.Tid));
        f.Saved.SpellbookBehavior.TreasureCardTemplateIds = [];
        Assert.False(ClassicBazaarTransactions.Sell(f.Live(), 2, Fixture.Tid, 0, 1, out _));
        Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void ConfirmedItemSaleDeletesOnlyExactOwnedOriginalAndRetainsStockIdentity(int kind) {
        var f = new Fixture(kind); f.AddItem(123); using var scope = f.Scope();
        var live = f.Live();
        Assert.True(ClassicBazaarTransactions.Quote(live, true, kind, Fixture.Tid, 123, 1, out var quote));
        Assert.Equal(50, quote.Cost); Assert.Equal(0, f.Saves); Assert.Single(f.Items);
        Assert.True(ClassicBazaarTransactions.Sell(live, kind, Fixture.Tid, 123, 1, out var receipt));
        Assert.Equal(new ulong[] { 123 }, receipt.RemovedItemIds); Assert.Empty(f.Items);
        Assert.Empty(f.Saved.InventoryBehavior.InventoryItemIds); Assert.Empty(live.InventoryBehavior.Items);
        Assert.Equal(950, f.Saved.GameStats.m_currentGold); Assert.Equal("stock/original", Assert.Single(f.Stocks.Keys));
        Assert.Equal(6, Assert.Single(f.Stocks.Values).m_numForSale); Assert.Equal(1, f.Deletes);
    }

    [Fact]
    public void LegacyOverfullTreasureBookCanSellOwnedCardsButCannotAcquireMore() {
        var f = new Fixture(2); using var scope = f.Scope();
        f.Saved.SpellbookBehavior.TreasureCardTemplateIds = Enumerable.Repeat((uint)Fixture.Tid, 1001).ToList();
        f.Saved.SpellbookBehavior.DeckTreasureCards = new() { [77] = new() { [(uint)Fixture.Tid] = 3 } };
        var live = f.Live();
        Assert.True(ClassicBazaarTransactions.Quote(live, true, 2, Fixture.Tid, 0, 1, out var quote));
        Assert.Equal(50, quote.Cost); Assert.Equal(0, f.Saves);
        Assert.False(ClassicBazaarTransactions.Buy(live, 2, Fixture.Tid, 1, 0, 0, out _));
        Assert.Equal(0, f.Saves);
        Assert.True(ClassicBazaarTransactions.Sell(live, 2, Fixture.Tid, 0, 1, out _));
        Assert.Equal(1000, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(3, f.Saved.SpellbookBehavior.DeckTreasureCount(77, (uint)Fixture.Tid));
        Assert.Equal(950, f.Saved.GameStats.m_currentGold);
        Assert.False(ClassicBazaarTransactions.Buy(live, 2, Fixture.Tid, 1, 0, 0, out _));
        Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void EveryConfirmedSaleProtectsStockOnlyAfterAcknowledgementUnderTheStockLock(int kind) {
        var f = new Fixture(kind); using var scope = f.Scope();
        var id = 0UL;
        if (kind is 0 or 1) { id = 123; f.AddItem(id); }
        else if (kind == 3) { id = 456; f.AddReagent(id, 1); }
        else f.Saved.SpellbookBehavior.TreasureCardTemplateIds = [(uint)Fixture.Tid];
        f.BeforeSave = () => Assert.Empty(f.NotedSales);
        Assert.True(ClassicBazaarTransactions.Sell(f.Live(), kind, Fixture.Tid, id, 1, out _));
        Assert.Equal(Fixture.Tid, Assert.Single(f.NotedSales));
    }

    [Fact]
    public void ForgedOwnershipDuplicateStockAndMissingBackpackReferencesAreRefused() {
        var f = new Fixture(0); f.AddItem(123); using var scope = f.Scope();
        f.Items.Values.Single().m_characterId = Fixture.Char + 1;
        Assert.False(ClassicBazaarTransactions.Sell(f.Live(), 0, Fixture.Tid, 123, 1, out _));
        Assert.False(ClassicBazaarTransactions.Buy(f.Live(), 0, Fixture.Tid, 1, 0, 0, out _));
        f.Items.Values.Single().m_characterId = Fixture.Char;
        f.Stocks["stock/duplicate"] = AuctionHouseCollection.Snapshot(f.Stocks.Values.Single());
        Assert.False(ClassicBazaarTransactions.Buy(f.Live(), 0, Fixture.Tid, 1, 0, 0, out _));
        Assert.False(ClassicBazaarTransactions.Sell(f.Live(), 0, Fixture.Tid, 123, 1, out _));
        Assert.Equal(0, f.Saves); Assert.Equal(900, f.Saved.GameStats.m_currentGold);
    }

    [Fact]
    public void DuplicateOwnedItemRowsAndDeckHoldingTreasureCardsCannotBeSold() {
        var f = new Fixture(0); f.AddItem(123); using var scope = f.Scope();
        f.Items["items/duplicate/123"] = f.Items.Values.Single() with { };
        Assert.False(ClassicBazaarTransactions.Quote(f.Live(), true, 0, Fixture.Tid, 123, 1, out _));
        Assert.False(ClassicBazaarTransactions.Sell(f.Live(), 0, Fixture.Tid, 123, 1, out _));
        f.Items.Remove("items/duplicate/123");
        ((WizItemTemplate)f.Dependencies.Template(Fixture.Tid)).m_adjectiveList = ["Deck"];
        f.Saved.SpellbookBehavior.DeckTreasureCards = new() { [123] = new() { [(uint)Fixture.Tid] = 2 } };
        Assert.False(ClassicBazaarTransactions.Quote(f.Live(), true, 0, Fixture.Tid, 123, 1, out _));
        Assert.False(ClassicBazaarTransactions.Sell(f.Live(), 0, Fixture.Tid, 123, 1, out _));
        Assert.Equal(0, f.Saves); Assert.Single(f.Items); Assert.Equal(900, f.Saved.GameStats.m_currentGold);
    }

    [Fact]
    public void ReagentPurchaseAndSaleKeepOriginalRowAndExactCountInOneCommit() {
        var f = new Fixture(3); f.AddReagent(456, 5); using var scope = f.Scope();
        var live = f.Live();
        Assert.True(ClassicBazaarTransactions.Buy(live, 3, Fixture.Tid, 2, 0, 0, out var buy));
        Assert.Equal(456UL, buy.ReagentUpdate.m_globalID.Full); Assert.Equal(7, buy.ReagentUpdate.m_quantity);
        Assert.True(buy.ReagentData.Length > 0); Assert.Equal(7, Assert.Single(f.Reagents).m_quantity);
        Assert.Equal(new ulong[] { 456 }, f.Saved.AlchemyBehavior.ReagentItemIds);
        Assert.True(ClassicBazaarTransactions.Quote(live, true, 3, Fixture.Tid, 456, 3, out var quote));
        Assert.Equal(50, quote.Cost); Assert.Equal(1, f.Saves);
        Assert.True(ClassicBazaarTransactions.Sell(live, 3, Fixture.Tid, 456, 3, out var sell));
        Assert.Equal(4, sell.ReagentUpdate.m_quantity); Assert.Equal(4, Assert.Single(f.Reagents).m_quantity);
        Assert.Equal(850, f.Saved.GameStats.m_currentGold); Assert.Equal(2, f.Saves);
    }

    [Fact]
    public void NewReagentPurchaseAndLastCopySaleSaveAndRemoveTheSameBagIdentity() {
        var f = new Fixture(3); using var scope = f.Scope();
        var live = f.Live();
        Assert.True(ClassicBazaarTransactions.Buy(live, 3, Fixture.Tid, 3, 0, 0, out var buy));
        var id = buy.ReagentUpdate.m_globalID.Full;
        Assert.Equal(3, Assert.Single(f.Reagents).m_quantity);
        Assert.Equal(new[] { id }, f.Saved.AlchemyBehavior.ReagentItemIds);
        Assert.True(ClassicBazaarTransactions.Sell(live, 3, Fixture.Tid, id, 3, out var sell));
        Assert.Equal(id, sell.ReagentUpdate.m_globalID.Full); Assert.Equal(0, sell.ReagentUpdate.m_quantity);
        Assert.Empty(f.Reagents); Assert.Empty(f.Saved.AlchemyBehavior.ReagentItemIds);
        Assert.Empty(live.AlchemyBehavior.Reagents); Assert.Empty(live.AlchemyBehavior.ReagentItemIds);
        Assert.Equal("stock/original", Assert.Single(f.Stocks.Keys));
        Assert.Equal(5, Assert.Single(f.Stocks.Values).m_numForSale);
        Assert.Equal(750, f.Saved.GameStats.m_currentGold); Assert.Equal(2, f.Saves);
    }

    [Fact]
    public void ReagentOverflowAndPacketPreparationFailureNeverDebitOrMutateStock() {
        var f = new Fixture(3); f.AddReagent(456, 998); using var scope = f.Scope();
        Assert.False(ClassicBazaarTransactions.Buy(f.Live(), 3, Fixture.Tid, 2, 0, 0, out _));
        f.Dependencies.SerializeReagent = _ => default;
        Assert.False(ClassicBazaarTransactions.Buy(f.Live(), 3, Fixture.Tid, 1, 0, 0, out _));
        Assert.Equal(998, Assert.Single(f.Reagents).m_quantity); Assert.Equal(900, f.Saved.GameStats.m_currentGold);
        Assert.Equal(5, Assert.Single(f.Stocks.Values).m_numForSale); Assert.Equal(0, f.Saves);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void WriteFailureOrLostAcknowledgementPublishesNothingNeverRefundsAndQuarantinesOriginal(bool didCommit) {
        var f = new Fixture(2) { FailSave = true, CommitBeforeFailure = didCommit }; using var scope = f.Scope();
        var live = f.Live();
        Assert.False(ClassicBazaarTransactions.Buy(live, 2, Fixture.Tid, 1, 0, 0, out var receipt));
        Assert.Null(receipt); Assert.Equal(900, live.GameStats.m_currentGold); Assert.Empty(live.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Empty(f.Published); Assert.True(ClassicBazaarTransactions.IsQuarantined(live));
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.Equal(didCommit ? 800 : 900, f.Saved.GameStats.m_currentGold);
        Assert.Equal(didCommit ? 1 : 0, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(didCommit ? 4 : 5, Assert.Single(f.Stocks.Values).m_numForSale);
        Assert.False(ClassicBazaarTransactions.Buy(live, 2, Fixture.Tid, 1, 0, 0, out _));
        Assert.Equal(1, f.SaveAttempts); // no compensation and no ambiguous retry
        var relog = f.Live(); Assert.False(ClassicBazaarTransactions.IsQuarantined(relog));
        Assert.Equal(f.Saved.GameStats.m_currentGold, relog.GameStats.m_currentGold);
        Assert.Equal(f.Saved.SpellbookBehavior.TreasureCardTemplateIds, relog.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.True(f.Invalidations > 0); Assert.False(WizardCollection.HoldsWriteLane);
    }

    [Fact]
    public async Task LastCopyContentionHasExactlyOneDebitAndDelivery() {
        var f = new Fixture(2); f.Stocks.Values.Single().m_numForSale = 1; using var scope = f.Scope();
        var first = f.Live(); var second = f.Live();
        var results = await Task.WhenAll(Task.Run(() => ClassicBazaarTransactions.Buy(first, 2, Fixture.Tid, 1, 0, 0, out _)),
            Task.Run(() => ClassicBazaarTransactions.Buy(second, 2, Fixture.Tid, 1, 0, 0, out _)));
        Assert.Single(results.Where(success => success)); Assert.Equal(800, f.Saved.GameStats.m_currentGold);
        Assert.Single(f.Saved.SpellbookBehavior.TreasureCardTemplateIds); Assert.Empty(f.Stocks);
        Assert.Equal(1, f.Saves); Assert.Single(f.Published);
    }

    [Fact]
    public void PouchOverflowSaleAndUnsupportedNativeKindsAreRefusedWithoutSave() {
        var f = new Fixture(2); using var scope = f.Scope();
        f.Saved.GameStats.m_currentGold = 9990; f.Saved.SpellbookBehavior.TreasureCardTemplateIds = [(uint)Fixture.Tid];
        Assert.False(ClassicBazaarTransactions.Sell(f.Live(), 2, Fixture.Tid, 0, 1, out _));
        Assert.False(ClassicBazaarTransactions.Buy(f.Live(), 4, Fixture.Tid, 1, 0, 0, out _));
        Assert.False(ClassicBazaarTransactions.Buy(f.Live(), 5, Fixture.Tid, 1, 0, 0, out _));
        Assert.Equal(0, f.Saves); Assert.Single(f.Saved.SpellbookBehavior.TreasureCardTemplateIds);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RestockAndAddNeverPublishCacheBeforeAcknowledgedSaveAndRereadAfterAmbiguity(bool restock) {
        var f = new Fixture(2); using var scope = f.Scope();
        var previousOpen = AuctionHouseCollection.TestOpenScope.Value;
        var previousRows = AuctionHouseCollection.TestRowsScope.Value;
        AuctionHouseCollection.TestOpenScope.Value = f.Open;
        AuctionHouseCollection.TestRowsScope.Value = session => Fixture.Session(session).Stocks.Values.ToList();
        try {
            AuctionHouseCollection.InvalidateCache();
            Assert.Equal(5, Assert.Single(AuctionHouseCollection.GetAllAuctionHouseEntries()).m_numForSale);
            f.FailSave = true; f.CommitBeforeFailure = true;
            var desired = new AuctionHouseEntry { m_templateID = restock ? Fixture.Tid : Fixture.Tid + 1,
                m_numForSale = 7, m_buyPrice = 107, m_sellPrice = 50 };
            // This non-character operation has no character lane requirement.
            f.RequireLane = false;
            f.BeforeSave = () => Assert.Equal(5, Assert.Single(AuctionHouseCollection.GetAllAuctionHouseEntries()).m_numForSale);
            Assert.Throws<IOException>(() => {
                if (restock) AuctionHouseCollection.ApplyStockChanges(new[] { desired }, Array.Empty<ulong>());
                else AuctionHouseCollection.AddAuctionHouseEntry(desired);
            });
            f.FailSave = false; f.BeforeSave = null;
            var reread = AuctionHouseCollection.GetAllAuctionHouseEntries();
            Assert.Equal(restock ? 1 : 2, reread.Count);
            Assert.Equal(7, reread.Single(s => s.m_templateID.Full == desired.m_templateID.Full).m_numForSale);
            Assert.Equal("stock/original", f.Stocks.Keys.Single(k => f.Stocks[k].m_templateID.Full == Fixture.Tid));
        }
        finally {
            AuctionHouseCollection.InvalidateCache();
            AuctionHouseCollection.TestOpenScope.Value = previousOpen;
            AuctionHouseCollection.TestRowsScope.Value = previousRows;
        }
    }

    [Fact]
    public void ExistingStockUpdateRetainsDocumentIdentityAndDetachedSnapshotsCannotMutateCache() {
        var f = new Fixture(2) { RequireLane = false }; using var scope = f.Scope();
        var previousOpen = AuctionHouseCollection.TestOpenScope.Value;
        var previousRows = AuctionHouseCollection.TestRowsScope.Value;
        AuctionHouseCollection.TestOpenScope.Value = f.Open;
        AuctionHouseCollection.TestRowsScope.Value = session => Fixture.Session(session).Stocks.Values.ToList();
        try {
            AuctionHouseCollection.InvalidateCache();
            var detached = AuctionHouseCollection.GetAuctionHouseEntry(Fixture.Tid);
            detached.m_numForSale = 999;
            Assert.Equal(5, AuctionHouseCollection.GetAuctionHouseEntry(Fixture.Tid).m_numForSale);
            Assert.True(AuctionHouseCollection.UpdateAuctionHouseEntry(new() { m_templateID = Fixture.Tid,
                m_numForSale = 4, m_buyPrice = 104, m_sellPrice = 50 }));
            Assert.Equal("stock/original", Assert.Single(f.Stocks.Keys)); Assert.Equal(0, f.Deletes);
            Assert.Equal(4, AuctionHouseCollection.GetAuctionHouseEntry(Fixture.Tid).m_numForSale);
        }
        finally {
            AuctionHouseCollection.InvalidateCache();
            AuctionHouseCollection.TestOpenScope.Value = previousOpen;
            AuctionHouseCollection.TestRowsScope.Value = previousRows;
        }
    }

    internal sealed class Fixture {
        internal const ulong Char = 773081, Tid = 773082;
        internal Wizard Saved;
        internal Dictionary<string, AuctionHouseEntry> Stocks = new() { ["stock/original"] = new() {
            m_templateID = Tid, m_numForSale = 5, m_buyPrice = 100, m_sellPrice = 50,
        } };
        internal Dictionary<string, WizClientObjectItem> Items = [];
        internal List<ClientReagentItem> Reagents = [];
        internal BazaarTransactionDependencies Dependencies;
        internal List<AuctionHouseEntry> Published = [];
        internal List<ulong> NotedSales = [];
        internal int Saves, SaveAttempts, Opens, Deletes, Invalidations;
        internal int LastRequestCount;
        internal bool FailSave, CommitBeforeFailure;
        internal bool RequireLane = true;
        internal System.Action? BeforeSave;
        private long _nextId = 2000;

        internal Fixture(int kind) {
            Saved = new Wizard { CharId = Char, GameStats = new ServerWizGameStats(default, 1) { m_currentGold = 900, m_baseGoldPouch = 10000 },
                InventoryBehavior = new() { InventoryItemIds = [], Items = [] }, EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = [] },
                StorageBehavior = new() { BankItemIds = [], Items = [] },
                SpellbookBehavior = new() { TreasureCardTemplateIds = [], DeckTreasureCards = [] },
                AlchemyBehavior = new() { ReagentItemIds = [], Reagents = [] },
            };
            CoreTemplate template = kind switch {
                2 => new SpellTemplate { m_name = "Fixture TC", m_baseCost = 100 },
                3 => new ReagentItemTemplate { m_templateID = (uint)Tid, m_baseCost = 100 },
                _ => new WizItemTemplate { m_templateID = (uint)Tid, m_baseCost = 100, m_adjectiveList = [kind == 1 ? "Housing" : "Hat"] },
            };
            Dependencies = new() {
                Open = Open, LoadWizard = (session, owner) => { Assert.Equal(Char, owner); return Session(session).Wizard; },
                LoadStock = (session, id) => Session(session).Stocks.Values.Where(s => s.m_templateID.Full == id).ToList(),
                LoadItems = (session, owner) => Session(session).Items.Values.Where(i => i.m_characterId.Full == owner).ToList(),
                ItemGlobalExists = (session, id) => {
                    Session(session).RegisterRequest();
                    return Session(session).Items.Values.Any(i => i.m_globalID.Full == id);
                },
                Template = id => id == Tid ? template : null!, IsTreasure = id => id == Tid,
                Create = id => kind == 3 ? new ClientReagentItem { m_globalID = (ulong)Interlocked.Increment(ref _nextId), m_templateID = id }
                    : new WizClientObjectItem { m_globalID = (ulong)Interlocked.Increment(ref _nextId), m_templateID = id, m_inactiveBehaviors = [] },
                Serialize = _ => new ByteString(new byte[] { 1, 2, 3 }), SerializeReagent = _ => new ByteString(new byte[] { 4, 5, 6 }),
                Prices = (_, count) => (100 + count, 50), StockCap = _ => 99,
                PublishStock = stock => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(Saves > 0); Published.Add(stock); },
                NoteRealSale = id => {
                    Assert.True(WizardCollection.HoldsWriteLane); Assert.True(Monitor.IsEntered(AuctionHouseCollection.Lock));
                    Assert.True(Saves > 0); NotedSales.Add(id);
                },
                InvalidateStock = () => Invalidations++,
            };
        }
        internal void AddItem(ulong id) { Items[$"items/original/{id}"] = new() { m_globalID = id, m_templateID = Tid, m_characterId = Char, m_inactiveBehaviors = [] }; Saved.InventoryBehavior.InventoryItemIds.Add(id); }
        internal void AddReagent(ulong id, int quantity) { Reagents.Add(new() { m_globalID = id, m_permID = id, m_templateID = Tid, m_characterId = Char, m_quantity = quantity }); Saved.AlchemyBehavior.ReagentItemIds.Add(id); }
        internal Wizard Live() {
            var live = Clone(Saved);
            live.InventoryBehavior.Items = [..Items.Values.Where(i => live.InventoryBehavior.InventoryItemIds.Contains(i.m_globalID.Full)).Select(i => i with { })];
            live.AlchemyBehavior.Reagents = [..Reagents.Select(r => r with { })];
            return live;
        }
        internal IDisposable Scope() {
            var previous = ClassicBazaarTransactions.TestScope.Value;
            var previousRows = WizardReagentCollection.TestRowsScope.Value;
            ClassicBazaarTransactions.TestScope.Value = Dependencies;
            WizardReagentCollection.TestRowsScope.Value = session => Session(session).Reagents;
            return new Restore(() => { ClassicBazaarTransactions.TestScope.Value = previous; WizardReagentCollection.TestRowsScope.Value = previousRows; });
        }
        internal IDocumentSession Open() {
            Opens++;
            var session = DispatchProxy.Create<IDocumentSession, SessionProxy>();
            var p = Session(session); p.Owner = this; p.Wizard = Clone(Saved);
            p.Stocks = Stocks.ToDictionary(kv => kv.Key, kv => AuctionHouseCollection.Snapshot(kv.Value));
            p.Items = Items.ToDictionary(kv => kv.Key, kv => kv.Value with { });
            p.Reagents = Reagents.Select(r => r with { }).ToList();
            return session;
        }
        internal static SessionProxy Session(IDocumentSession s) => (SessionProxy)(object)s;
        internal static Wizard Clone(Wizard w) => new() { CharId = w.CharId,
            GameStats = w.GameStats.CloneSnapshotWithGold(w.GameStats.m_currentGold),
            InventoryBehavior = new() { InventoryItemIds = [..w.InventoryBehavior.InventoryItemIds], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [..w.EquipmentBehavior.EquippedItemIds], EquippedItems = [] },
            StorageBehavior = new() { BankItemIds = [..w.StorageBehavior.BankItemIds], Items = [] },
            SpellbookBehavior = new() { TreasureCardTemplateIds = [..w.SpellbookBehavior.TreasureCardTemplateIds],
                DeckTreasureCards = ServerWizSpellbookBehavior.CopyLedger(w.SpellbookBehavior.DeckTreasureCards),
                DeckTreasureLedgerVersion = w.SpellbookBehavior.DeckTreasureLedgerVersion },
            AlchemyBehavior = new() { ReagentItemIds = [..w.AlchemyBehavior.ReagentItemIds], Reagents = [] },
        };
        private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
    }
    public class SessionProxy : DispatchProxy {
        internal Fixture Owner = null!; internal Wizard Wizard = null!;
        internal Dictionary<string, AuctionHouseEntry> Stocks = [];
        internal Dictionary<string, WizClientObjectItem> Items = [];
        internal List<ClientReagentItem> Reagents = [];
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, AdvancedProxy>();
        private int _requests;
        internal void RegisterRequest() {
            Owner.LastRequestCount = ++_requests;
            if (_requests > ((AdvancedProxy)(object)_advanced).MaxRequests)
                throw new InvalidOperationException("Fixture session request ceiling exceeded.");
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            switch (method!.Name) {
                case "get_Advanced": return _advanced;
                case "Load": RegisterRequest(); return Items.GetValueOrDefault((string)args![0]!);
                case "Store": {
                    switch (args![0]) {
                        case ClientReagentItem reagent: Reagents.Add(reagent); break;
                        case WizClientObjectItem item: Items[(string)args[1]!] = item; break;
                        case AuctionHouseEntry stock: Stocks[$"stock/new/{stock.m_templateID.Full}"] = stock; break;
                        default: throw new NotSupportedException();
                    }
                    return null;
                }
                case "Delete": {
                    Owner.Deletes++;
                    switch (args![0]) {
                        case ClientReagentItem reagent: Assert.True(Reagents.Remove(reagent)); break;
                        case WizClientObjectItem item: Items.Remove(Items.Single(kv => ReferenceEquals(kv.Value, item)).Key); break;
                        case AuctionHouseEntry stock: Stocks.Remove(Stocks.Single(kv => ReferenceEquals(kv.Value, stock)).Key); break;
                        default: throw new NotSupportedException();
                    }
                    return null;
                }
                case "SaveChanges": {
                    if (Owner.RequireLane) Assert.True(WizardCollection.HoldsWriteLane); Owner.SaveAttempts++; Owner.BeforeSave?.Invoke();
                    if (Owner.FailSave && !Owner.CommitBeforeFailure) throw new IOException("Injected refused Bazaar commit");
                    Owner.Saved = Fixture.Clone(Wizard); Owner.Stocks = Stocks.ToDictionary(kv => kv.Key, kv => AuctionHouseCollection.Snapshot(kv.Value));
                    Owner.Items = Items.ToDictionary(kv => kv.Key, kv => kv.Value with { }); Owner.Reagents = Reagents.Select(r => r with { }).ToList();
                    Owner.Saves++;
                    if (Owner.FailSave) throw new IOException("Injected lost Bazaar acknowledgement");
                    return null;
                }
                case "Dispose": return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
    public class AdvancedProxy : DispatchProxy {
        private readonly IMetadataDictionary _metadata = DispatchProxy.Create<IMetadataDictionary, MetadataProxy>();
        internal int MaxRequests = 30;
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            switch (method!.Name) {
                case "set_OptimisticConcurrencyMode":
                case "IgnoreChangesFor": return null;
                case "set_MaxNumberOfRequestsPerSession": MaxRequests = (int)args![0]!; return null;
                case "GetMetadataFor": return _metadata;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
    public class MetadataProxy : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            Assert.Equal("set_Item", method!.Name); Assert.Equal(Raven.Client.Constants.Documents.Metadata.Collection, args![0]);
            Assert.Contains((string)args[1]!, new[] { WizardItemCollection.CollectionName, AuctionHouseCollection.CollectionName, WizardReagentCollection.CollectionName });
            return null;
        }
    }
}
