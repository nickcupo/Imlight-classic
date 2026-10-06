// CLASSIC: dated October furniture allowance, independent of the native200-slot cache.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;
using YamlDotNet.Serialization;
using Action = System.Action;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ClassicDormCapacityTests : IDisposable {
    private const ulong Owner = 726656, BackpackItem = 9001;
    private const uint Furniture = (1u << 28) - 6656, Dynamic = 0x3456;
    private readonly IDictionary<ulong, CoreTemplate> _cache;
    private readonly CoreTemplate? _previous;

    public ClassicDormCapacityTests() {
        var config = Path.Combine(Path.GetTempPath(), $"classic-dorm-tests-{Guid.NewGuid():N}.ini");
        File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}classic-dorm-tests.log\n[Character]\nMaxInventoryItems=150\n");
        ConfigurationManager.Initialize(config); // retain the synthetic fixture
        Profile("october-2010-arc1");
        _cache = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        _cache.TryGetValue(Furniture, out _previous);
        _cache[Furniture] = new WizItemTemplate { m_templateID = Furniture,
            m_adjectiveList = ["Housing", "Furniture"],
            m_behaviors = [new RenderBehaviorTemplate(), new FurnitureInfoBehaviorTemplate()] };
    }

    public void Dispose() {
        if (_previous is null) _cache.Remove(Furniture); else _cache[Furniture] = _previous;
        ClassicRuntime.ResetForTests();
    }

    private static void Profile(string profile) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(profile));
    }

    [Fact]
    public void AuthoredDormAllowanceAgreesWithTheRuntimeRule() {
        var document = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(
            File.ReadAllText(Path.Combine(ClassicDataFixture.Root, "housing", "houses-october-2010.yaml")));
        Assert.Equal(HousingRules.OctoberDormCapacity, Convert.ToInt32(document["dorm_furniture_capacity"]));
    }

    [Theory]
    [InlineData("october-2010-arc1", 50)]
    [InlineData("late-2009", 200)]
    [InlineData("arc1-2009h1", 200)]
    [InlineData("dev-unrestricted", 200)]
    public void NewDormUsesProfileAllowanceWithoutChangingNativeCache(string profile, int capacity) {
        Profile(profile);
        var store = new Store(); using var scope = store.Scope();
        var ledger = HousingCollection.Load(Owner, create: true)!;
        Assert.Equal(capacity, ledger.Capacity);
        Assert.Equal(200, HousingRules.PackageSlots);
        Assert.Equal(1, ledger.PackageCount);
    }

    [Fact]
    public void ReconciliationRetainsOverfullEntriesExactFloatsSlotsAndVersionsAndIsIdempotent() {
        var store = new Store(); store.Seed(55);
        var original = store.Ledger.Copy(); using var scope = store.Scope();
        var updated = HousingCollection.Load(Owner)!;
        Assert.Equal(50, updated.Capacity); Assert.Equal(55, updated.Count);
        Assert.Equal(original.PackageNumber, updated.PackageNumber);
        Assert.Equal(original.Version, updated.Version);
        Assert.Equal(original.SecondVersion, updated.SecondVersion);
        for (var i = 0; i < original.Entries.Count; i++) {
            Assert.Equal(original.Entries[i].ItemId, updated.Entries[i].ItemId);
            Assert.Equal(BitConverter.SingleToInt32Bits(original.Entries[i].Z), BitConverter.SingleToInt32Bits(updated.Entries[i].Z));
            Assert.Equal(BitConverter.SingleToInt32Bits(original.Entries[i].Yaw), BitConverter.SingleToInt32Bits(updated.Entries[i].Yaw));
        }
        var saves = store.Saves;
        Assert.Equal(50, HousingCollection.Load(Owner)!.Capacity);
        Assert.Equal(saves, store.Saves);
    }

    [Theory]
    [InlineData(49, true)]
    [InlineData(50, false)]
    [InlineData(55, false)]
    public void DirectPlacementCannotBypassAllowanceUsingAnUnreconciledLedger(int count, bool permitted) {
        var store = new Store(); store.Seed(count); using var scope = store.Scope();
        var live = store.Login(); var result = Place(live, store);
        Assert.Equal(permitted, result.Saved);
        Assert.Equal(count + (permitted ? 1 : 0), store.Ledger.Count);
        Assert.Equal(!permitted, live.InventoryBehavior.InventoryItemIds.Contains(BackpackItem));
        Assert.Equal(!permitted, store.SavedWizard.InventoryBehavior.InventoryItemIds.Contains(BackpackItem));
        Assert.True(store.Documents.ContainsKey("item/backpack"));
    }

    [Fact]
    public void OverfullDormCanMoveAndPickUpAnOriginalItemInItsOriginalSlot() {
        var store = new Store(); store.Seed(55); using var scope = store.Scope(); var live = store.Login();
        var id = HousingRules.PlacedGlobalId(54, Dynamic);
        Assert.True(HousingCollection.Update(live, Owner, id, Dynamic, 11.25f, -12.5f, 178.765625f, .73123455f).Saved);
        Assert.Equal(50, store.Ledger.Capacity); Assert.Equal(55, store.Ledger.Count);
        var item = store.Ledger.Entries[54].ItemId;
        var result = HousingCollection.Pickup(live, Owner, id, Dynamic);
        Assert.True(result.Saved, result.Error);
        Assert.Equal(54, store.Ledger.Count); Assert.True(store.Ledger.Entries[54].Removed);
        Assert.Contains(item, live.InventoryBehavior.InventoryItemIds);
        Assert.Contains(item, store.SavedWizard.InventoryBehavior.InventoryItemIds);
        Assert.True(store.Documents.ContainsKey($"item/{item}"));
    }

    [Fact]
    public void RemovedSlotCanBeReusedAtTheAllowanceWithoutRenumberingOtherFurniture() {
        var store = new Store(); store.Seed(50); Assert.True(store.Ledger.TryPickup(7));
        using var scope = store.Scope(); var result = Place(store.Login(), store);
        Assert.True(result.Saved, result.Error); Assert.Equal(7, result.Slot);
        Assert.Equal(50, store.Ledger.Count); Assert.Equal(50, store.Ledger.Entries.Count);
        Assert.Equal(1049ul, store.Ledger.Entries[49].ItemId);
    }

    [Fact]
    public void FailedCapacityReconciliationPreservesAllSavedFurnitureAndAllowance() {
        var store = new Store(); store.Seed(55); store.FailSave = true; using var scope = store.Scope();
        Assert.Throws<IOException>(() => HousingCollection.Load(Owner));
        Assert.Equal(200, store.Ledger.Capacity); Assert.Equal(55, store.Ledger.Count);
    }

    [Fact]
    public void FailedPlacementSavePreservesLegacyAllowanceBackpackAndCacheVersion() {
        var store = new Store(); store.Seed(49); var version = store.Ledger.Version;
        store.FailSave = true; using var scope = store.Scope(); var live = store.Login();
        Assert.False(Place(live, store).Saved);
        Assert.Equal(200, store.Ledger.Capacity); Assert.Equal(49, store.Ledger.Count);
        Assert.Equal(version, store.Ledger.Version);
        Assert.Contains(BackpackItem, live.InventoryBehavior.InventoryItemIds);
        Assert.Contains(BackpackItem, store.SavedWizard.InventoryBehavior.InventoryItemIds);
    }

    private static HousingResult Place(Wizard live, Store store)
        => HousingCollection.Place(live, Owner, BackpackItem, 0, 0, 0, 0, store.FindItem);

    private sealed class Store {
        internal readonly Dictionary<string, object> Documents = new();
        internal bool FailSave;
        internal int Saves;
        internal Wizard SavedWizard => (Wizard)Documents["wizard/1"];
        internal HousingLedger Ledger => (HousingLedger)Documents[HousingLedger.DocumentId(Owner)];
        internal Store() {
            Documents["wizard/1"] = new Wizard { CharId = Owner, Zone = HousingRules.DormZone,
                InventoryBehavior = new() { InventoryItemIds = [], Items = new() },
                EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = new(), SlotList = [] },
                StorageBehavior = new() { BankItemIds = [], Items = new() } };
            Documents["item/backpack"] = Item(BackpackItem);
            Assert.True(SavedWizard.InventoryBehavior.AddItem((WizClientObjectItem)Documents["item/backpack"]));
        }
        internal void Seed(int count) {
            var ledger = new HousingLedger { OwnerId = Owner, Zone = HousingRules.DormZone, PackageNumber = 23 };
            for (var i = 0; i < count; i++) {
                var id = 1000ul + (ulong)i; var key = $"item/{id}";
                Documents[key] = Item(id);
                Assert.True(ledger.TryPlace(new HousingEntry { ItemId = id, ItemDocumentId = key, TemplateId = Furniture,
                    Z = 178.765625f, Yaw = .73123455f }, out _));
            }
            Documents[HousingLedger.DocumentId(Owner)] = ledger;
        }
        internal static WizClientObjectItem Item(ulong id) => new() { m_globalID = id, m_templateID = Furniture, m_characterId = Owner };
        internal Wizard Login() {
            var live = new Wizard { CharId = Owner, Zone = HousingRules.DormZone,
                InventoryBehavior = new() { InventoryItemIds = [.. SavedWizard.InventoryBehavior.InventoryItemIds], Items = new() },
                EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = new(), SlotList = [] },
                StorageBehavior = new() { BankItemIds = [], Items = new() } };
            foreach (var item in Documents.Values.OfType<WizClientObjectItem>()
                .Where(i => live.InventoryBehavior.InventoryItemIds.Contains(i.m_globalID))) live.InventoryBehavior.Items.Add(item);
            return live;
        }
        internal WizClientObjectItem FindItem(IDocumentSession session, ulong item, ulong owner) => session.Load<WizClientObjectItem>("item/backpack");
        internal IDisposable Scope() {
            var previous = WizardCollection.TestStoreScope.Value;
            WizardCollection.TestStoreScope.Value = new(Open, (session, _) => session.Load<Wizard>("wizard/1"));
            return new Restore(() => WizardCollection.TestStoreScope.Value = previous);
        }
        internal IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, CountingSession>();
            var proxy = (CountingSession)(object)session;
            proxy.Saved = Documents; proxy.Fail = () => FailSave; proxy.AfterSave = () => Saves++;
            return session;
        }
    }
    public class CountingSession : HousingTests.HousingSessionProxy {
        internal Action AfterSave = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            var result = base.Invoke(method, args);
            if (method!.Name == "SaveChanges") AfterSave();
            return result;
        }
    }
    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }
}
