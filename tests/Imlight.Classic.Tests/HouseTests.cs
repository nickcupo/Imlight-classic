using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class HouseTests : IDisposable {
    private const ulong Owner = 724656, AccountId = 74656;
    private const uint Template = (1u << 28) - 4656, Furniture = Template - 1, Dynamic = 0x2356;
    private const string Exterior = "Housing/WizardCity/WC_Tier1_Exterior", Interior = "Housing/WizardCity/WC_Tier1_Interior";
    private readonly IDictionary<ulong, CoreTemplate> _cache;
    private readonly Dictionary<ulong, CoreTemplate?> _previous = new();
    private readonly IReadOnlyDictionary<uint, HouseDefinition>? _previousDefinitions;
    private readonly FieldInfo _inventoryLimit = typeof(ServerWizInventoryBehavior)
        .GetField("s_iniMaxItemsAllowed", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly int _previousInventoryLimit;
    private static HouseDefinition Definition => new(Template, Template - 2, "Fixture house", Exterior, Interior,
        Exterior + "_Preview", 250, 250, 8000, 10000, 2, true, true); // explicit fixture allowances, not a historical ruling

    public HouseTests() {
        EquipmentAttachConcurrencyTests.Configure();
        // CLASSIC: the resource-free configuration has no Character.MaxInventoryItems. Set and restore an
        // explicit positive fixture limit so initial purchases do not depend on another test's AddItem fallback.
        _previousInventoryLimit = (int)_inventoryLimit.GetValue(null)!;
        _inventoryLimit.SetValue(null, Imlight.Classic.Inventory.Banking.ClassicBackpackSize);
        Assert.True(ServerWizInventoryBehavior.MaxItemsAllowed > 0, "House fixture needs a positive backpack limit.");
        _cache = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (var id in new[] { Template, Furniture }) { _cache.TryGetValue(id, out var old); _previous[id] = old; }
        _cache[Template] = new WizItemTemplate { m_templateID = Template, m_adjectiveList = ["Islands", "Deed"],
            m_behaviors = [new DeedBehaviorTemplate { m_behaviorName = "Deed" }] };
        _cache[Furniture] = new WizItemTemplate { m_templateID = Furniture, m_adjectiveList = ["Housing", "Furniture"],
            m_behaviors = [new RenderBehaviorTemplate(), new FurnitureInfoBehaviorTemplate()] };
        _previousDefinitions = HouseCatalog.TestDefinitions.Value;
        HouseCatalog.TestDefinitions.Value = new Dictionary<uint, HouseDefinition> { [Template] = Definition };
    }
    public void Dispose() {
        HouseCatalog.TestDefinitions.Value = _previousDefinitions;
        foreach (var pair in _previous) { if (pair.Value is null) _cache.Remove(pair.Key); else _cache[pair.Key] = pair.Value; }
        _inventoryLimit.SetValue(null, _previousInventoryLimit);
    }

    [Fact]
    public void GoldPurchaseCommitsOriginalFullDeedAndOwnershipWithOneDebit() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login();
        var bought = Buy(live, 9101);
        Assert.True(bought.Saved, bought.Error); Assert.Equal(92000, store.Wizard.GameStats.m_currentGold);
        Assert.Equal(92000, live.GameStats.m_currentGold);
        Assert.Equal(9101ul, bought.Record.DeedId); Assert.NotEqual(0ul, bought.Record.LotInstanceId);
        Assert.Equal($"ClassicHouseItems/{Owner}/9101", bought.Record.ItemDocumentId);
        var original = Assert.IsType<WizClientObjectItem>(store.Documents[bought.Record.ItemDocumentId]);
        Assert.Equal(Owner, (ulong)original.m_characterId);
        Assert.Equal(bought.Record.LotInstanceId, (ulong)Assert.Single(original.m_inactiveBehaviors.OfType<DeedBehavior>()).m_lotInstanceGID);
        Assert.NotEmpty((byte[])bought.ItemData);
        Assert.True(HouseCollection.TryGetOwned(Owner, 9101, out var owned));
        Assert.Equal(bought.Record.LotInstanceId, owned.LotInstanceId);
        Assert.Single(store.Documents.Values.OfType<WizClientObjectItem>());
        Assert.False(Buy(live, 9101).Saved); Assert.Equal(92000, store.Wizard.GameStats.m_currentGold);
    }

    [Fact]
    public void SaveFailureCannotDebitGoldOrLeaveDeedOrPortfolio() {
        var store = new Store { FailSave = true }; using var scope = store.Scope(); var live = store.Login();
        Assert.False(Buy(live, 9101).Saved);
        Assert.Equal(100000, store.Wizard.GameStats.m_currentGold); Assert.Equal(100000, live.GameStats.m_currentGold);
        Assert.Empty(store.Documents.Values.OfType<HouseRecord>()); Assert.Empty(store.Documents.Values.OfType<WizClientObjectItem>());
        Assert.Empty(live.InventoryBehavior.Items);
    }

    [Fact]
    public void CrownsPurchaseAndSaveFailureUseSameAccountCharacterTransaction() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login();
        var bought = HouseCollection.Purchase(live, Deed(9101), HouseCurrency.Crowns, store.LoadAccount);
        Assert.True(bought.Saved); Assert.Equal(20000, store.Account.Crowns); Assert.Equal(20000, live.Account.Crowns);
        store.FailSave = true;
        Assert.False(HouseCollection.Purchase(live, Deed(9102), HouseCurrency.Crowns, store.LoadAccount).Saved);
        Assert.Equal(20000, store.Account.Crowns); Assert.Equal(20000, live.Account.Crowns);
        Assert.Single(store.Documents.Values.OfType<HouseRecord>());
    }

    [Fact]
    public async Task TwoStaleSessionsCanBuyOnlyOneThirdHouse() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login();
        var first = Buy(live, 9101); var second = Buy(live, 9102);
        Assert.True(first.Saved, first.Error); Assert.True(second.Saved, second.Error);
        var a = store.Login(); var b = store.Login();
        var results = await Task.WhenAll(Task.Run(() => Buy(a, 9103)), Task.Run(() => Buy(b, 9104)));
        Assert.Single(results.Where(r => r.Saved)); Assert.Equal(3, store.Portfolio.DeedIds.Count);
        Assert.Equal(76000, store.Wizard.GameStats.m_currentGold);
    }

    [Fact]
    public void DuplicateTemplatesHaveDistinctOriginalLotsRoomsAndCachePackages() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login();
        var a = Buy(live, 9101); var b = Buy(live, 9102); Assert.True(a.Saved, a.Error); Assert.True(b.Saved, b.Error);
        Assert.Equal($"ClassicHouseItems/{Owner}/9101", a.Record.ItemDocumentId);
        Assert.Equal($"ClassicHouseItems/{Owner}/9102", b.Record.ItemDocumentId);
        Assert.Equal(2, store.Documents.Values.OfType<WizClientObjectItem>().Count());
        Assert.True(HouseCollection.TryGetOwned(Owner, 9101, out _));
        Assert.True(HouseCollection.TryGetOwned(Owner, 9102, out _));
        Assert.NotEqual(a.Record.LotInstanceId, b.Record.LotInstanceId);
        var outsideA = HousingCollection.Load(new HousingRoomIdentity(Owner, 9101, Exterior), true);
        var insideA = HousingCollection.Load(new HousingRoomIdentity(Owner, 9101, Interior), true);
        var outsideB = HousingCollection.Load(new HousingRoomIdentity(Owner, 9102, Exterior), true);
        Assert.Equal(3, new[] { outsideA.PackageNumber, insideA.PackageNumber, outsideB.PackageNumber }.Distinct().Count());
        Assert.Equal(3, new[] { outsideA.SecondPackageNumber, insideA.SecondPackageNumber, outsideB.SecondPackageNumber }.Distinct().Count());
        Assert.All(new[] { outsideA, insideA, outsideB }, r => Assert.Equal(250, r.Capacity));
        Assert.Equal(3, store.Documents.Values.OfType<HousingLedger>().Count(r => r.DeedId != 0));
        Assert.Equal("ClassicHousing/724656/dorm", HousingLedger.DocumentId(Owner));
    }

    [Fact]
    public void SelectionAndSwapPreserveAllOriginalItemsAndSingleIslandSlot() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login();
        var first = Buy(live, 9101); var second = Buy(live, 9102);
        Assert.True(first.Saved, first.Error); Assert.True(second.Saved, second.Error);
        Assert.True(HouseCollection.SetEquipped(live, 9101, true).Saved);
        Assert.Equal(9101ul, (ulong)Assert.Single(store.Wizard.EquipmentBehavior.SlotList).ItemId);
        var swapped = HouseCollection.SetEquipped(live, 9102, true);
        Assert.True(swapped.Saved); Assert.Equal(9101ul, (ulong)swapped.Replaced.m_globalID);
        Assert.Equal(9101ul, Assert.Single(store.Wizard.InventoryBehavior.InventoryItemIds));
        Assert.Equal(9102ul, Assert.Single(store.Wizard.EquipmentBehavior.EquippedItemIds));
        Assert.Equal(9102ul, (ulong)Assert.Single(live.EquipmentBehavior.EquippedItems).m_globalID);
        Assert.True(HouseCollection.TryGetEquipped(store.Login(), out var selected)); Assert.Equal(9102ul, selected.DeedId);
        Assert.True(HouseCollection.SetEquipped(live, 9102, false).Saved);
        Assert.Empty(store.Wizard.EquipmentBehavior.EquippedItemIds); Assert.Equal(2, store.Wizard.InventoryBehavior.InventoryItemIds.Count);
        Assert.Equal(2, store.Documents.Values.OfType<WizClientObjectItem>().Count());
    }

    [Fact]
    public void FullBackpackRefusesUnequipWithoutLosingSelectedDeed() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login();
        Assert.True(Buy(live, 9101).Saved); Assert.True(HouseCollection.SetEquipped(live, 9101, true).Saved);
        store.Wizard.InventoryBehavior.InventoryItemIds = Enumerable.Range(1, ServerWizInventoryBehavior.MaxItemsAllowed).Select(i => (ulong)i).ToList();
        Assert.False(HouseCollection.SetEquipped(live, 9101, false).Saved);
        Assert.Equal(9101ul, Assert.Single(store.Wizard.EquipmentBehavior.EquippedItemIds));
        Assert.Equal(9101ul, (ulong)Assert.Single(live.EquipmentBehavior.EquippedItems).m_globalID);
    }

    [Fact]
    public void SelectionSaveFailureLeavesLiveAndSavedMembershipUnchanged() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login();
        Assert.True(Buy(live, 9101).Saved); store.FailSave = true;
        Assert.False(HouseCollection.SetEquipped(live, 9101, true).Saved);
        Assert.Equal(9101ul, Assert.Single(store.Wizard.InventoryBehavior.InventoryItemIds));
        Assert.Empty(store.Wizard.EquipmentBehavior.EquippedItemIds); Assert.Single(live.InventoryBehavior.Items);
    }

    [Fact]
    public void ExplicitPickupThenEmptySalePreservesFurnitureAndArchivesOriginalDeedAtomically() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login(); Assert.True(Buy(live, 9101).Saved);
        var outsideIdentity = new HousingRoomIdentity(Owner, 9101, Exterior);
        var insideIdentity = new HousingRoomIdentity(Owner, 9101, Interior);
        var outside = HousingCollection.Load(outsideIdentity, true);
        var inside = HousingCollection.Load(insideIdentity, true);
        store.Place(outside, 9301); store.Place(inside, 9302); var exact = inside.Entries[0].Copy();
        // CLASSIC: empty the two areas explicitly; sale itself cannot move furniture to the attic.
        var noExceptions = HousingCodec.Encode(new HousingItemList { m_housingItemGIDList = [] });
        store.Wizard.Zone = Exterior; live.Zone = Exterior;
        Assert.True(HousingAtticCollection.PickUpAll(live, Owner, Dynamic, 100, noExceptions, outsideIdentity).Saved);
        store.Wizard.Zone = Interior; live.Zone = Interior;
        Assert.True(HousingAtticCollection.PickUpAll(live, Owner, Dynamic, 100, noExceptions, insideIdentity).Saved);
        Assert.Equal(2, store.Attic.Count);
        var outsideVersion = store.Room(9101, Exterior).Version;
        var insideVersion = store.Room(9101, Interior).Version;
        var atticVersions = store.Attic.Packages.Select(p => p.Version).ToArray();
        var sold = HouseCollection.Sell(live, 9101, 400, 0); // empty sale does not require attic space
        Assert.True(sold.Saved, sold.Error); Assert.Empty(sold.Added); Assert.Null(sold.Attic); Assert.Equal(2, store.Attic.Count);
        Assert.Equal(92400, store.Wizard.GameStats.m_currentGold); Assert.Empty(store.Wizard.InventoryBehavior.InventoryItemIds);
        Assert.Equal(92400, live.GameStats.m_currentGold); Assert.Empty(live.InventoryBehavior.Items);
        Assert.Empty(store.Portfolio.DeedIds); Assert.True(store.House(9101).Sold);
        Assert.True(store.Room(9101, Exterior).Entries[0].Removed); Assert.True(store.Room(9101, Interior).Entries[0].Removed);
        Assert.Equal(outsideVersion, store.Room(9101, Exterior).Version);
        Assert.Equal(insideVersion, store.Room(9101, Interior).Version);
        Assert.Equal(atticVersions, store.Attic.Packages.Select(p => p.Version).ToArray());
        var returned = store.Attic.Packages.SelectMany(p => p.Entries).Single(e => e.ItemId == 9302);
        Exact(exact, returned); Assert.Contains(sold.Record.ItemDocumentId, store.Documents.Keys);
        Assert.False(HouseCollection.TryGetOwned(Owner, 9101, out _)); Assert.False(HouseCollection.Sell(live, 9101, 400, 100).Saved);
        Assert.Equal(92400, store.Wizard.GameStats.m_currentGold);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void SaleRefusesFurnitureInEitherAreaWithoutChangingAnySavedOrLiveState(bool furnishedOutside, bool furnishedInside, bool missingFurnitureOriginal) {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login(); Assert.True(Buy(live, 9101).Saved);
        var outside = HousingCollection.Load(new HousingRoomIdentity(Owner, 9101, Exterior), true);
        var inside = HousingCollection.Load(new HousingRoomIdentity(Owner, 9101, Interior), true);
        if (furnishedOutside) store.Place(outside, 9301);
        if (furnishedInside) store.Place(inside, 9302);
        if (missingFurnitureOriginal) store.Documents.Remove("item/9301");
        var beforeOutside = store.Room(9101, Exterior).Copy();
        var beforeInside = store.Room(9101, Interior).Copy();
        var atticVersions = store.Attic.Packages.Select(p => p.Version).ToArray();
        var documents = store.Documents.Keys.OrderBy(k => k).ToArray();
        var sale = HouseCollection.Sell(live, 9101, 400, 1000); // even a large attic never permits a furnished sale
        Assert.False(sale.Saved); Assert.Contains("empty", sale.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.Attic.Count); Assert.False(store.House(9101).Sold);
        Assert.Null(store.House(9101).SoldAt);
        Assert.Equal(beforeOutside.Version, store.Room(9101, Exterior).Version);
        Assert.Equal(beforeInside.Version, store.Room(9101, Interior).Version);
        Assert.Equal(atticVersions, store.Attic.Packages.Select(p => p.Version).ToArray());
        foreach (var (before, after) in new[] { (beforeOutside, store.Room(9101, Exterior)), (beforeInside, store.Room(9101, Interior)) }) {
            Assert.Equal(before.Count, after.Count);
            for (var i = 0; i < before.Entries.Count; i++) {
                Assert.False(after.Entries[i].Removed);
                Assert.Equal(before.Entries[i].ItemId, after.Entries[i].ItemId);
                Exact(before.Entries[i], after.Entries[i]);
            }
        }
        Assert.Equal(documents, store.Documents.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(92000, store.Wizard.GameStats.m_currentGold); Assert.Equal(92000, live.GameStats.m_currentGold);
        Assert.Equal(9101ul, Assert.Single(store.Wizard.InventoryBehavior.InventoryItemIds));
        Assert.Equal(9101ul, Assert.Single(store.Portfolio.DeedIds));
        Assert.Equal(9101ul, Assert.Single(live.InventoryBehavior.Items).m_globalID.Full);
        Assert.Equal(30000, store.Account.Crowns);
    }

    [Fact]
    public void EmptySaleSaveFailureCannotPayOrArchiveTheOriginalDeed() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login(); Assert.True(Buy(live, 9101).Saved);
        store.FailSave = true;
        Assert.False(HouseCollection.Sell(live, 9101, 400, 0).Saved);
        Assert.Equal(0, store.Attic.Count); Assert.False(store.House(9101).Sold); Assert.Null(store.House(9101).SoldAt);
        Assert.Equal(92000, store.Wizard.GameStats.m_currentGold); Assert.Equal(92000, live.GameStats.m_currentGold);
        Assert.Equal(9101ul, Assert.Single(store.Wizard.InventoryBehavior.InventoryItemIds));
        Assert.Equal(9101ul, Assert.Single(store.Portfolio.DeedIds)); Assert.Single(live.InventoryBehavior.Items);
        Assert.True(HouseCollection.TryGetOwned(Owner, 9101, out _));
    }

    [Fact]
    public void MissingOriginalDeedCannotBeSoldEvenWhenBothRoomsAreEmpty() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login(); var bought = Buy(live, 9101); Assert.True(bought.Saved);
        store.Documents.Remove(bought.Record.ItemDocumentId);
        Assert.False(HouseCollection.Sell(live, 9101, 400, 0).Saved);
        Assert.False(store.House(9101).Sold); Assert.Equal(9101ul, Assert.Single(store.Portfolio.DeedIds));
        Assert.Equal(92000, store.Wizard.GameStats.m_currentGold); Assert.Equal(92000, live.GameStats.m_currentGold);
        Assert.Single(live.InventoryBehavior.Items); Assert.Equal(0, store.Attic.Count);
    }

    [Fact]
    public void ResaleApprovalIsIndependentOfPurchaseApproval() {
        HouseCatalog.TestDefinitions.Value = new Dictionary<uint, HouseDefinition> { [Template] = Definition with { ResaleAllowed = false } };
        var store = new Store(); using var scope = store.Scope(); var live = store.Login(); Assert.True(Buy(live, 9101).Saved);
        Assert.False(HouseCollection.Sell(live, 9101, 400, 100).Saved); Assert.False(store.House(9101).Sold);
    }

    [Fact]
    public void ForeignOriginalDeedOrFurnitureCannotBeSoldOrEntered() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login(); var bought = Buy(live, 9101); Assert.True(bought.Saved);
        Assert.True(HouseCollection.MayEnter(Owner, Owner, 9101, Exterior));
        Assert.False(HouseCollection.MayEnter(Owner, Owner + 1, 9101, Exterior));
        Assert.False(HouseCollection.MayEnter(Owner, Owner, 9101, "Housing/School/Fire_Exterior"));
        ((WizClientObjectItem)store.Documents[bought.Record.ItemDocumentId]).m_characterId = Owner + 1;
        Assert.False(HouseCollection.MayEnter(Owner, Owner, 9101, Exterior)); Assert.False(HouseCollection.Sell(live, 9101, 400, 100).Saved);
    }

    [Fact]
    public void LastPhysicalLotSurvivesChangingEquippedIdenticalTemplateAndSoldLotNeverFallsBack() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login();
        var first = Buy(live, 9101); var second = Buy(live, 9102);
        Assert.True(first.Saved, first.Error); Assert.True(second.Saved, second.Error);
        Assert.True(HouseCollection.SetEquipped(live, 9101, true).Saved);
        Assert.True(HouseCollection.RecordLocation(Owner, Owner, 9101, Exterior));
        Assert.True(HouseCollection.SetEquipped(live, 9102, true).Saved);
        Assert.True(HouseCollection.TryGetSavedLocation(Owner, Owner, Exterior, out var actual)); Assert.Equal(9101ul, actual);
        Assert.True(HouseCollection.Sell(live, 9101, 400, 100).Saved);
        Assert.True(HouseCollection.HasSavedLocation(Owner, Owner, Exterior));
        Assert.False(HouseCollection.TryGetSavedLocation(Owner, Owner, Exterior, out _));
        Assert.False(HouseCollection.MayEnter(Owner, Owner, 9101, Exterior));
        Assert.True(HouseCollection.RecordLocation(Owner, Owner, 0, HousingRules.DormZone));
        Assert.False(HouseCollection.HasSavedLocation(Owner, Owner, Exterior));
    }

    [Fact]
    public void VisitorsCannotRecoverAStaleSavedHouseWithoutPendingTransportProof() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login(); Assert.True(Buy(live, 9101).Saved);
        store.Documents[HouseLocation.DocumentId(Owner + 1)] = new HouseLocation { CharacterId = Owner + 1, OwnerId = Owner, DeedId = 9101, Zone = Exterior };
        Assert.False(HouseCollection.TryGetSavedLocation(Owner + 1, Owner, Exterior, out _));
    }

    [Fact]
    public void TwoRoomPackagesPermit250WithNonoverlappingAtticKeysAndIndependentVersionChains() {
        var room = new HousingLedger { OwnerId = Owner, DeedId = 9101, Zone = Interior, Capacity = 250, PackageNumber = 11, SecondPackageNumber = 12 };
        for (var i = 0; i < 250; i++) Assert.True(room.TryPlace(Entry((ulong)i + 9400), out _));
        Assert.False(room.TryPlace(Entry(9800), out _));
        Assert.Equal(201u, room.Version); Assert.Equal(51u, room.SecondVersion);
        Assert.Equal(649u, room.CacheIndex(249)); Assert.Equal(12, room.PackageForSlot(249));
        Assert.True(room.TrySlot(HousingRules.PlacedGlobalId(649, Dynamic), Dynamic, out var slot)); Assert.Equal(249, slot);
        Assert.False(room.TrySlot(HousingRules.PlacedGlobalId(400, Dynamic), Dynamic, out _));
        Assert.False(room.TrySlot(HousingRules.PlacedGlobalId(649, Dynamic + 1), Dynamic, out _));
        var manifest = HousingCodec.Manifest(room, 12345);
        Assert.Equal(new uint[] { 0, 600 }, manifest.m_blobRequestObjectList.Select(p => p.m_userData));
        Assert.Equal(new uint[] { 200, 50 }, manifest.m_blobRequestObjectList.Select(p => p.m_objectCount));
        Assert.Equal(50, Assert.IsType<HousingBlob>(HousingCodec.Blob(room, 12345, 1).m_data).m_housingBlobObjectList.Count);
        Assert.True(HousingCodec.TryRequests(HousingCodec.Encode(manifest), 12345, room, out var packages)); Assert.Equal(new[] { 0, 1 }, packages);
        manifest.m_blobRequestObjectList[1].m_userData = 400;
        Assert.False(HousingCodec.TryRequests(HousingCodec.Encode(manifest), 12345, room, out _));
    }

    [Fact]
    public void SecondPackageCannotOverflowItsVersionOrMoveOtherFloatingItems() {
        var room = new HousingLedger { OwnerId = Owner, Capacity = 250, PackageNumber = 11, SecondPackageNumber = 12 };
        for (var i = 0; i < 201; i++) Assert.True(room.TryPlace(Entry((ulong)i + 9400), out _));
        var rug = room.Entries[0].Copy(); room.SecondVersion = uint.MaxValue;
        Assert.False(room.TryUpdate(200, 1, 2, 3, 4)); Assert.False(room.TryPickup(200));
        Assert.False(room.TryPlace(Entry(9900), out _)); Exact(rug, room.Entries[0]);
        Assert.True(room.TryPickup(0)); Assert.True(room.TryPlace(Entry(9900), out var replaced)); Assert.Equal(0, replaced);
    }

    [Fact]
    public void HouseRoomPickUpAllIncludesSecondPackageAndPreservesExactExceptions() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login(); Assert.True(Buy(live, 9101).Saved);
        var identity = new HousingRoomIdentity(Owner, 9101, Interior); var room = HousingCollection.Load(identity, true);
        for (var i = 0; i < 201; i++) store.Place(room, (ulong)i + 10001);
        store.Wizard.Zone = Interior; live.Zone = Interior;
        var except = HousingCodec.Encode(new HousingItemList { m_housingItemGIDList = [(GID)HousingRules.PlacedGlobalId(600, Dynamic)] });
        var last = room.Entries[200].Copy();
        var picked = HousingAtticCollection.PickUpAll(live, Owner, Dynamic, 300, except, identity);
        Assert.True(picked.Saved); Assert.Equal(200, picked.Added.Count);
        Assert.Equal(2u, store.Room(9101, Interior).SecondVersion); // excluded second package unchanged
        Assert.False(store.Room(9101, Interior).Entries[200].Removed); Exact(last, store.Room(9101, Interior).Entries[200]);
        Assert.Equal(200, store.Attic.Count);
    }

    [Fact]
    public void DisabledOrIncompleteDefinitionsNeverOfferOrAuthorizeHousePurchase() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login();
        foreach (var d in new[] { Definition with { Enabled = false }, Definition with { MinimumLevel = 0 }, Definition with { InteriorCapacity = 0 } }) {
            HouseCatalog.TestDefinitions.Value = new Dictionary<uint, HouseDefinition> { [Template] = d };
            Assert.Empty(HouseCatalog.Approved); Assert.False(Buy(live, 9101).Saved);
        }
        Assert.Equal(100000, store.Wizard.GameStats.m_currentGold); Assert.Empty(store.Documents.Values.OfType<HouseRecord>());
    }

    private static HousePurchaseResult Buy(Wizard live, ulong id) => HouseCollection.Purchase(live, Deed(id), HouseCurrency.Gold);
    private static WizClientObjectItem Deed(ulong id) => new() { m_globalID = id, m_templateID = Template,
        m_debugName = "Fixture deed", m_inactiveBehaviors = [new DeedBehavior()] };
    private static HousingEntry Entry(ulong id) => new() { ItemId = id, ItemDocumentId = $"item/{id}", TemplateId = Furniture,
        X = 11.234375f, Y = -12.625f, Z = 178.765625f, Yaw = .73123455f };
    private static void Exact(HousingEntry a, HousingEntry b) {
        Assert.Equal(BitConverter.SingleToInt32Bits(a.X), BitConverter.SingleToInt32Bits(b.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Y), BitConverter.SingleToInt32Bits(b.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Z), BitConverter.SingleToInt32Bits(b.Z));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Yaw), BitConverter.SingleToInt32Bits(b.Yaw));
    }

    private sealed class Store {
        internal readonly Dictionary<string, object> Documents = new(); internal bool FailSave;
        internal Wizard Wizard => (Wizard)Documents["wizard/1"];
        internal Account Account => (Account)Documents["account/1"];
        internal HousePortfolio Portfolio => (HousePortfolio)Documents[HousePortfolio.DocumentId(Owner)];
        internal AtticLedger Attic => (AtticLedger)Documents[AtticLedger.DocumentId(Owner)];
        internal HouseRecord House(ulong id) => (HouseRecord)Documents[HouseRecord.DocumentId(Owner, id)];
        internal HousingLedger Room(ulong id, string zone) => (HousingLedger)Documents[HousingLedger.DocumentId(new HousingRoomIdentity(Owner, id, zone))];
        internal Store() {
            Documents["wizard/1"] = new Wizard { CharId = Owner, AccountId = AccountId, Zone = HousingRules.DormZone,
                GameStats = new ServerWizGameStats(MagicSchool.Fire, 50) { m_currentGold = 100000, m_baseGoldPouch = 1000000 }, MagicSchoolBehavior = new() { Level = 50 },
                InventoryBehavior = new() { InventoryItemIds = [], Items = new() },
                EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = new(), SlotList = [] },
                StorageBehavior = new() { BankItemIds = [], Items = new() } };
            var account = new Account { Crowns = 30000 }; typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(account, AccountId);
            account.CharacterIds.Add(Owner); Documents["account/1"] = account;
            Documents[HousingPackageAllocator.DocumentId] = new HousingPackageAllocator { NextPackageNumber = 15 };
            Documents[AtticLedger.DocumentId(Owner)] = new AtticLedger { OwnerId = Owner, ContainerId = 814814,
                Packages = [new() { PackageNumber = 13, UserData = 200 }, new() { PackageNumber = 14, UserData = 400 }] };
        }
        internal void Place(HousingLedger room, ulong id) {
            Documents[$"item/{id}"] = new WizClientObjectItem { m_globalID = id, m_templateID = Furniture, m_characterId = Owner };
            Assert.True(room.TryPlace(Entry(id), out _));
            Documents[HousingLedger.DocumentId(new HousingRoomIdentity(Owner, room.DeedId, room.Zone))] = room.Copy();
        }
        internal Wizard Login() {
            var live = CloneWizard(Wizard); live.Account = CloneAccount(Account);
            foreach (var id in live.InventoryBehavior.InventoryItemIds) live.InventoryBehavior.Items.Add(FindItem(id));
            foreach (var id in live.EquipmentBehavior.EquippedItemIds) live.EquipmentBehavior.EquippedItems.Add(FindItem(id));
            return live;
        }
        private WizClientObjectItem FindItem(ulong id) => Documents.Values.OfType<WizClientObjectItem>().Single(i => i.m_globalID == id);
        internal Account LoadAccount(IDocumentSession session, ulong id) { Assert.Equal(AccountId, id); return session.Load<Account>("account/1"); }
        internal IDisposable Scope() {
            var old = WizardCollection.TestStoreScope.Value;
            WizardCollection.TestStoreScope.Value = new(Open, (s, id) => id == Owner ? s.Load<Wizard>("wizard/1") : null!);
            return new Restore(() => WizardCollection.TestStoreScope.Value = old);
        }
        private IDocumentSession Open() {
            var s = DispatchProxy.Create<IDocumentSession, HouseSessionProxy>(); var proxy = (HouseSessionProxy)(object)s;
            proxy.Saved = Documents; proxy.Fail = () => FailSave; return s;
        }
        internal static Wizard CloneWizard(Wizard w) => new() { CharId = w.CharId, AccountId = w.AccountId, Zone = w.Zone,
            GameStats = w.GameStats.CloneSnapshotWithGold(w.GameStats.m_currentGold), MagicSchoolBehavior = new() { Level = w.MagicSchoolBehavior.Level },
            InventoryBehavior = new() { InventoryItemIds = [.. w.InventoryBehavior.InventoryItemIds], Items = new() },
            EquipmentBehavior = new() { EquippedItemIds = [.. w.EquipmentBehavior.EquippedItemIds], EquippedItems = new(),
                SlotList = w.EquipmentBehavior.SlotList.Select(s => new EquipmentSlot { ItemId = s.ItemId, ItemName = s.ItemName,
                    SlotType = s.SlotType, EquippedSince = s.EquippedSince }).ToList() },
            StorageBehavior = new() { BankItemIds = [.. w.StorageBehavior.BankItemIds], Items = new() } };
        internal static Account CloneAccount(Account a) {
            var copy = new Account { Crowns = a.Crowns }; typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(copy, a.AccountId);
            copy.CharacterIds.AddRange(a.CharacterIds); return copy;
        }
    }
    public class HouseSessionProxy : DispatchProxy {
        internal Dictionary<string, object> Saved = null!; internal readonly Dictionary<string, object> Working = new();
        internal Func<bool> Fail = null!; private IAdvancedSessionOperations? _advanced;
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            switch (method!.Name) {
                case "get_Advanced":
                    if (_advanced is null) { _advanced = DispatchProxy.Create<IAdvancedSessionOperations, HouseAdvancedProxy>();
                        ((HouseAdvancedProxy)(object)_advanced).Owner = this; } return _advanced;
                case "Load":
                    if (args![0] is IEnumerable<string> ids) {
                        return ids.ToDictionary(key => key, key => {
                            if (Working.TryGetValue(key, out var loaded)) return (WizClientObjectItem)loaded;
                            return Saved.TryGetValue(key, out var original) ? (WizClientObjectItem)(Working[key] = Clone(original)) : null!;
                        });
                    }
                    var id = (string)args![0]!;
                    if (Working.TryGetValue(id, out var found)) return found;
                    return Saved.TryGetValue(id, out var saved) ? Working[id] = Clone(saved) : null;
                case "Store": Working[(string)args![1]!] = args[0]!; return null;
                case "SaveChanges":
                    if (Fail()) throw new IOException("Injected transaction failure.");
                    foreach (var pair in Working) Saved[pair.Key] = Clone(pair.Value); return null;
                case "Dispose": return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
        private static object Clone(object value) => value switch {
            HouseRecord h => h.Copy(), HousePortfolio p => p.Copy(), HouseLocation l => l.Copy(), HousingLedger r => r.Copy(), AtticLedger a => a.Copy(),
            HousingPackageAllocator p => new HousingPackageAllocator { NextPackageNumber = p.NextPackageNumber },
            WizClientObjectItem i => i with { }, Wizard w => Store.CloneWizard(w), Account a => Store.CloneAccount(a),
            _ => throw new NotSupportedException(value.GetType().Name),
        };
    }
    public class HouseAdvancedProxy : DispatchProxy {
        internal HouseSessionProxy Owner = null!;
        private readonly IMetadataDictionary _metadata = DispatchProxy.Create<IMetadataDictionary, HouseMetadataProxy>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "set_OptimisticConcurrencyMode" => null, "GetMetadataFor" => _metadata,
            "GetDocumentId" => Owner.Working.FirstOrDefault(p => ReferenceEquals(p.Value, args![0])).Key,
            _ => throw new NotSupportedException(method.Name),
        };
    }
    public class HouseMetadataProxy : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            if (method!.Name != "set_Item") throw new NotSupportedException(method.Name);
            Assert.Equal(Raven.Client.Constants.Documents.Metadata.Collection, args![0]);
            Assert.Equal(WizardItemCollection.CollectionName, args[1]); return null;
        }
    }
    private sealed class Restore(System.Action undo) : IDisposable { public void Dispose() => undo(); }
}
