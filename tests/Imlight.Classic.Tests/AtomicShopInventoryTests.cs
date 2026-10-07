// CLASSIC: production ticket purchases, sales and trash use fresh tracked originals and ACK-only receipts.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Inventory;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class AtomicShopInventoryTests {
    public AtomicShopInventoryTests() => EquipmentAttachConcurrencyTests.Configure("[Character]\nMaxInventoryItems=150\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");

    [Fact]
    public void TicketPurchaseUsesSavedWalletAndBandAndPublishesOnlyAfterOneSave() {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        live.GameStats.m_currentArenaPoints = 1; live.GameStats.m_currentPvPCurrency = 999;
        var candidate = Fixture.Item(11); candidate.m_primaryColor = 7;
        candidate.m_inactiveBehaviors = [new ClientPetItemBehavior { m_level = 4 }];
        f.BeforeSave = () => { Assert.Empty(live.InventoryBehavior.Items); Assert.Equal(1, live.GameStats.m_currentArenaPoints); };
        Assert.Equal(ShopService.TicketPurchaseResult.Purchased, f.Buy(live, candidate, out var receipt));
        Assert.Same(candidate, receipt.Item); Assert.Equal(new byte[] { 7, 4 }, (byte[])receipt.Data);
        Assert.Equal(1, f.Saves); Assert.Equal(30, f.Saved.GameStats.m_currentArenaPoints);
        Assert.Equal(30, live.GameStats.m_currentArenaPoints); Assert.Equal(30, live.GameStats.m_currentPvPCurrency);
        Assert.Equal(11ul, Assert.Single(f.Saved.InventoryBehavior.InventoryItemIds));
        Assert.Equal(7, Assert.Single(f.Rows.Values).m_primaryColor);
    }

    [Theory]
    [InlineData("rank", "RankRequired")]
    [InlineData("poor", "TicketsUnavailable")]
    [InlineData("full", "InventoryRefused")]
    [InlineData("serialize", "InventoryRefused")]
    [InlineData("foreign-row", "InventoryRefused")]
    public void RefusedTicketPurchaseCannotDebitSaveOrProduceReceipt(string reason, string expected) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        switch (reason) {
            case "rank": f.Standing.Rating = 499; break;
            case "poor": f.Saved.GameStats.m_currentArenaPoints = 69; break;
            case "full": f.Fill(); break;
            case "foreign-row": f.Rows["foreign"] = Fixture.Item(11) with { m_characterId = Fixture.Owner + 1 }; break;
        }
        live.GameStats.m_currentArenaPoints = 999;
        Assert.Equal(expected, f.Buy(live, Fixture.Item(11), out var receipt, reason == "serialize").ToString());
        Assert.Null(receipt); Assert.Equal(0, f.Saves); Assert.Equal(999, live.GameStats.m_currentArenaPoints);
        Assert.Empty(live.InventoryBehavior.Items); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrLostTicketAcknowledgementDoesNotPublishRefundOrRetry(bool durable) {
        var f = new Fixture { Fail = true, Durable = durable }; using var scope = f.Scope(); var live = f.Live();
        ItemPurchaseReceipt receipt = null!;
        Assert.Throws<InvalidOperationException>(() => f.Buy(live, Fixture.Item(11), out receipt));
        Assert.Null(receipt); Assert.Empty(live.InventoryBehavior.Items); Assert.Equal(100, live.GameStats.m_currentArenaPoints);
        Assert.Equal(durable ? 30 : 100, f.Saved.GameStats.m_currentArenaPoints); Assert.Equal(durable ? 1 : 0, f.Rows.Count);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.Equal(ShopService.TicketPurchaseResult.InventoryRefused, f.Buy(live, Fixture.Item(12), out _));
        Assert.Equal(1, f.Saves);
    }

    [Fact]
    public async Task CompetingTicketPurchasesCannotSpendTheSameSavedWallet() {
        var f = new Fixture(); using var scope = f.Scope();
        var attached = Enumerable.Range(1, 8).Select(i => (Live: f.Live(), Id: (ulong)i)).ToArray();
        var results = await Task.WhenAll(attached.Select(pair => Task.Run(() =>
            f.Buy(pair.Live, Fixture.Item(pair.Id), out _), TestContext.Current.CancellationToken)));
        Assert.Single(results.Where(result => result == ShopService.TicketPurchaseResult.Purchased));
        Assert.Equal(1, f.Saves); Assert.Equal(30, f.Saved.GameStats.m_currentArenaPoints); Assert.Single(f.Rows);
    }

    [Fact]
    public void QuickSellUsesFreshPricesOriginalIdsAndSavedPouchCapInOneSave() {
        var f = new Fixture(); f.Add(11); f.Add(12); f.Add(13); f.Add(14);
        f.Saved.GameStats.m_currentGold = 95; f.Saved.GameStats.m_baseGoldPouch = 100;
        f.Saved.SpellbookBehavior.DeckTreasureCards[13] = new() { [77] = 2 };
        f.Rows["original/12"].m_templateID = 88; // the live cache deliberately has the old template
        using var scope = f.Scope(); var live = f.Live(); live.GameStats.m_currentGold = 1;
        live.InventoryBehavior.GetItem(12).m_templateID = Fixture.Template;
        f.BeforeSave = () => { Assert.Equal(4, live.InventoryBehavior.Items.Count); Assert.Equal(1, live.GameStats.m_currentGold); };
        Assert.True(InventoryService.TrySellBackpackItems(live,
            [new(11, 1), new(11, 1), new(12, 1), new(13, 1), new(14, 99), new(999, 1)],
            item => item.m_templateID.Full == 88 ? 20 : 7, out var sales));
        Assert.Equal(new ulong[] { 11, 12 }, sales.Select(sale => sale.Id)); Assert.Equal(new[] { 7, 20 }, sales.Select(sale => sale.Gold));
        Assert.Equal(1, f.Saves); Assert.Equal(100, live.GameStats.m_currentGold); Assert.Equal(100, f.Saved.GameStats.m_currentGold);
        Assert.Equal(new[] { "original/13", "original/14" }, f.Rows.Keys); Assert.Equal(new ulong[] { 13, 14 }, f.Saved.InventoryBehavior.InventoryItemIds);
        Assert.Equal(2, f.Saved.SpellbookBehavior.DeckTreasureCards[13][77]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrLostSaleAcknowledgementPublishesNeitherRemovalPayoutNorReceipt(bool durable) {
        var f = new Fixture { Fail = true, Durable = durable }; f.Add(11); using var scope = f.Scope(); var live = f.Live();
        IReadOnlyList<BackpackQuickSell.Sale> sales = null!;
        Assert.Throws<InvalidOperationException>(() => InventoryService.TrySellBackpackItems(live, [new(11, 1)], _ => 7, out sales));
        Assert.Empty(sales); Assert.Single(live.InventoryBehavior.Items); Assert.Equal(10, live.GameStats.m_currentGold);
        Assert.Equal(durable ? 17 : 10, f.Saved.GameStats.m_currentGold); Assert.Equal(durable ? 0 : 1, f.Rows.Count);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.False(InventoryService.TrySellBackpackItems(live, [new(11, 1)], _ => 7, out _)); Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData("bank")]
    [InlineData("foreign")]
    [InlineData("missing")]
    [InlineData("price")]
    public void UnownedOrIneligibleSavedOriginalCannotPayFromStaleLiveInventory(string reason) {
        var f = new Fixture(); f.Add(11); var live = f.Live();
        switch (reason) {
            case "bank": f.Saved.InventoryBehavior.InventoryItemIds = []; f.Saved.StorageBehavior.BankItemIds = [11]; break;
            case "foreign": f.Rows["original/11"].m_characterId = Fixture.Owner + 1; break;
            case "missing": f.Rows.Clear(); break;
        }
        using var scope = f.Scope();
        Assert.False(InventoryService.TrySellBackpackItems(live, [new(11, 1)], _ => reason == "price" ? null : 7, out var sales));
        Assert.Empty(sales); Assert.Equal(0, f.Saves); Assert.Equal(10, f.Saved.GameStats.m_currentGold); Assert.Single(live.InventoryBehavior.Items);
    }

    [Fact]
    public void TrashChecksFreshOriginalPolicyAndDeletesItsExactRowAfterAcknowledgement() {
        var f = new Fixture(); f.Add(11); var live = f.Live(); f.Rows["original/11"].m_templateID = 88;
        using var scope = f.Scope();
        Assert.False(InventoryService.TryDiscardBackpackItem(live, 11, item => item.m_templateID.Full != 88)); Assert.Equal(0, f.Saves);
        f.BeforeSave = () => Assert.Single(live.InventoryBehavior.Items);
        Assert.True(InventoryService.TryDiscardBackpackItem(live, 11, _ => true));
        Assert.Empty(f.Rows); Assert.Empty(live.InventoryBehavior.Items); Assert.Equal(1, f.Saves);
        Assert.Equal(10, f.Saved.GameStats.m_currentGold);
    }

    private sealed class Fixture {
        internal const ulong Owner = 823411; internal const uint Template = 77881;
        internal Wizard Saved = new() { CharId = Owner, GameStats = new(default, 1) { m_currentGold = 10, m_baseGoldPouch = 100,
                m_currentArenaPoints = 100, m_currentPvPCurrency = 100 },
            InventoryBehavior = new() { InventoryItemIds = [], Items = [] }, EquipmentBehavior = new() { EquippedItemIds = [] },
            StorageBehavior = new() { BankItemIds = [] }, SpellbookBehavior = new() { DeckTreasureCards = [] } };
        internal ArenaLadderEntry Standing = new() { CharId = Owner, Rating = 500 };
        internal Dictionary<string, WizClientObjectItem> Rows = [];
        internal bool Fail, Durable; internal int Saves; internal System.Action? BeforeSave;
        internal static WizClientObjectItem Item(ulong id) => new() { m_globalID = id, m_templateID = Template, m_characterId = Owner, m_inactiveBehaviors = [] };
        internal void Add(ulong id) { Saved.InventoryBehavior.InventoryItemIds.Add(id); Rows["original/" + id] = Item(id); }
        internal void Fill() { for (ulong id = 100; Saved.InventoryBehavior.InventoryItemIds.Count < ServerWizInventoryBehavior.MaxItemsAllowed; id++) Add(id); }
        internal Wizard Live() { var live = Clone(Saved); live.InventoryBehavior.Items = [..Rows.Values.Where(item => live.InventoryBehavior.InventoryItemIds.Contains(item.m_globalID.Full)).Select(item => item with { })]; return live; }
        internal ShopService.TicketPurchaseResult Buy(Wizard live, WizClientObjectItem candidate, out ItemPurchaseReceipt receipt, bool bad = false)
            => ShopService.TryPurchaseWithTickets(live, candidate, 70, 500, 900, out receipt,
                bad ? _ => default : item => new ByteString(new[] { (byte)item.m_primaryColor, item.m_inactiveBehaviors.OfType<ClientPetItemBehavior>().FirstOrDefault()?.m_level ?? (byte)0 }),
                (session, _) => Session(session).Standing);
        internal IDisposable Scope() {
            var oldStore = WizardCollection.TestStoreScope.Value; var oldRows = WizardInventoryTransactions.TestRowsScope.Value;
            var cache = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory)
                .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var previousTemplates = new Dictionary<ulong, CoreTemplate?>();
            foreach (var id in new ulong[] { Template, 88 }) {
                cache.TryGetValue(id, out var previous); previousTemplates[id] = previous;
                cache[id] = new WizItemTemplate { m_templateID = (uint)id, m_adjectiveList = [] };
            }
            WizardCollection.TestStoreScope.Value = new(Open, (session, _) => Session(session).Wizard);
            WizardInventoryTransactions.TestRowsScope.Value = session => Session(session).Rows.Values.ToList();
            return new Restore(() => {
                WizardCollection.TestStoreScope.Value = oldStore; WizardInventoryTransactions.TestRowsScope.Value = oldRows;
                foreach (var (id, previous) in previousTemplates) { if (previous is null) cache.Remove(id); else cache[id] = previous; }
            });
        }
        private IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, ItemSession>(); var proxy = Session(session);
            proxy.Wizard = Clone(Saved); proxy.Rows = Rows.ToDictionary(pair => pair.Key, pair => pair.Value with { });
            proxy.Standing = new() { CharId = Standing.CharId, Rating = Standing.Rating };
            proxy.Save = () => {
                Assert.True(WizardCollection.HoldsWriteLane); Saves++; BeforeSave?.Invoke();
                if (Fail && !Durable) throw new InvalidOperationException("fixture refused write");
                Saved = Clone(proxy.Wizard); Rows = proxy.Rows.ToDictionary(pair => pair.Key, pair => pair.Value with { });
                if (Fail) throw new InvalidOperationException("fixture lost acknowledgement");
            };
            return session;
        }
        private static ItemSession Session(IDocumentSession session) => (ItemSession)(object)session;
        private static Wizard Clone(Wizard wizard) => new() { CharId = wizard.CharId,
            GameStats = wizard.GameStats.CloneSnapshotWithGold(wizard.GameStats.m_currentGold),
            InventoryBehavior = new() { InventoryItemIds = [..wizard.InventoryBehavior.InventoryItemIds], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [..wizard.EquipmentBehavior.EquippedItemIds] },
            StorageBehavior = new() { BankItemIds = [..wizard.StorageBehavior.BankItemIds] },
            SpellbookBehavior = new() { DeckTreasureCards = ServerWizSpellbookBehavior.CopyLedger(wizard.SpellbookBehavior.DeckTreasureCards) } };
        private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
    }

    public class ItemSession : DispatchProxy {
        internal Wizard Wizard = null!; internal ArenaLadderEntry Standing = null!;
        internal Dictionary<string, WizClientObjectItem> Rows = [];
        internal System.Action Save = null!;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, ItemInventoryPersistenceTests.ItemAdvanced>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "get_Advanced" => _advanced, "Store" => Store((WizClientObjectItem)args![0]!),
            "Delete" => Delete((WizClientObjectItem)args![0]!), "SaveChanges" => SaveNow(), "Dispose" => null,
            _ => throw new NotSupportedException(method.Name),
        };
        private object? Store(WizClientObjectItem item) { Rows.Add("new/" + item.m_globalID.Full, item); return null; }
        private object? Delete(WizClientObjectItem item) { var original = Rows.Single(pair => ReferenceEquals(pair.Value, item)); Assert.True(Rows.Remove(original.Key)); return null; }
        private object? SaveNow() { Save(); return null; }
    }
}
