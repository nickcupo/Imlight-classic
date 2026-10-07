using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class HousingTests : IDisposable {
    private const ulong Owner = 724242;
    private const uint FurnitureTemplate = (1u << 28) - 4242;
    private const uint Dynamic = 0x1234;
    private readonly IDictionary<ulong, CoreTemplate> _cache;
    private readonly CoreTemplate? _previous;

    public HousingTests() {
        EquipmentAttachConcurrencyTests.Configure();
        _cache = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        _cache.TryGetValue(FurnitureTemplate, out _previous);
        _cache[FurnitureTemplate] = new WizItemTemplate {
            m_templateID = FurnitureTemplate, m_adjectiveList = ["Housing", "Furniture"],
            m_behaviors = [new RenderBehaviorTemplate(), new FurnitureInfoBehaviorTemplate()],
        };
    }

    public void Dispose() {
        if (_previous is null) _cache.Remove(FurnitureTemplate);
        else _cache[FurnitureTemplate] = _previous;
    }

    [Fact]
    public void NativeCachePackingIsSeparateFromExactPersistedFloatingRugPositions() {
        var ledger = new HousingLedger { OwnerId = Owner, PackageNumber = 7 };
        var crate = Entry(1, x: 11.2f, y: -12.6f, z: 0);
        var rug = Entry(2, x: 11.234375f, y: -12.625f, z: 178.765625f, yaw: 0.73123455f);
        Assert.True(ledger.TryPlace(crate, out var crateSlot));
        Assert.True(ledger.TryPlace(rug, out var rugSlot));
        var saved = JsonConvert.DeserializeObject<HousingLedger>(JsonConvert.SerializeObject(ledger))!;
        Assert.True(saved.TryPickup(crateSlot));
        Assert.Equal(BitConverter.SingleToInt32Bits(rug.X), BitConverter.SingleToInt32Bits(saved.Entries[rugSlot].X));
        Assert.Equal(BitConverter.SingleToInt32Bits(rug.Y), BitConverter.SingleToInt32Bits(saved.Entries[rugSlot].Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(rug.Z), BitConverter.SingleToInt32Bits(saved.Entries[rugSlot].Z));
        Assert.Equal(BitConverter.SingleToInt32Bits(rug.Yaw), BitConverter.SingleToInt32Bits(saved.Entries[rugSlot].Yaw));
        Assert.Equal(0x000b800du, HousingCodec.Pack(saved.Entries[rugSlot]).m_positionXY);
        Assert.Equal(rug.Z, HousingCodec.Pack(saved.Entries[rugSlot]).m_positionZ);
        Assert.Equal(0u, HousingCodec.Pack(saved.Entries[crateSlot]).m_gameObjectTemplateID);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 1)]
    [InlineData(45, 4)]
    [InlineData(315, 7)]
    public void NativeCardinalYawUsesItsActualNonSequentialByteMapping(int degrees, int expected) {
        var packed = HousingCodec.Pack(Entry(1, yaw: degrees * MathF.PI / 180));
        Assert.Equal(FurnitureTemplate, packed.m_gameObjectTemplateID);
        Assert.Equal((byte)expected, packed.m_yaw);
    }

    [Fact]
    public void ExtendedYawStoresIntegerDegreesAndTheNinthBitInTemplateFlags() {
        var packed = HousingCodec.Pack(Entry(1, yaw: 300.5f * MathF.PI / 180));
        Assert.Equal(FurnitureTemplate | 0xc0000000u, packed.m_gameObjectTemplateID);
        Assert.Equal(44u << 16, packed.m_extraData1);
    }

    [Theory]
    [InlineData(float.NaN, 0, 0, 0)]
    [InlineData(0, float.PositiveInfinity, 0, 0)]
    [InlineData(0, 0, float.NaN, 0)]
    [InlineData(32767, 0, 0, 0)]
    public void InvalidCoordinatesNeverChangeSavedVersion(float x, float y, float z, float yaw) {
        var ledger = new HousingLedger();
        Assert.False(ledger.TryPlace(Entry(1, x, y, z, yaw), out _));
        Assert.Empty(ledger.Entries);
        Assert.Equal(1u, ledger.Version);
    }

    [Fact]
    public void RemovedSlotsAreReusedWithoutRenumberingOtherFurniture() {
        var ledger = new HousingLedger();
        Assert.True(ledger.TryPlace(Entry(1), out var first));
        Assert.True(ledger.TryPlace(Entry(2), out var second));
        Assert.False(ledger.TryPlace(Entry(2), out _));
        Assert.True(ledger.TryPickup(first));
        Assert.True(ledger.TryPlace(Entry(3), out var reused));
        Assert.Equal(first, reused);
        Assert.Equal(2ul, ledger.Entries[second].ItemId);
        Assert.Equal(5u, ledger.Version);
    }

    [Fact]
    public void NativePackageBoundDoesNotWrapAndFullVersionDoesNotMutate() {
        var ledger = new HousingLedger();
        for (var i = 0; i < HousingRules.PackageSlots; i++) Assert.True(ledger.TryPlace(Entry((ulong)i + 1), out _));
        Assert.False(ledger.TryPlace(Entry(999), out _));
        ledger.Version = uint.MaxValue;
        Assert.False(ledger.TryPickup(0));
        Assert.False(ledger.TryUpdate(0, 1, 2, 3, 0));
        Assert.False(ledger.Entries[0].Removed);
    }

    [Fact]
    public void VisitorsForeignInstancesAndInventoryObjectIdsAreNotEditable() {
        Assert.True(HousingRules.CanEdit(Owner, Owner, HousingRules.DormZone));
        Assert.False(HousingRules.CanEdit(Owner + 1, Owner, HousingRules.DormZone));
        Assert.False(HousingRules.CanEdit(Owner, Owner, "WizardCity/WC_Ravenwood"));
        var id = HousingRules.PlacedGlobalId(3, Dynamic);
        Assert.Equal(0x1234090000000003ul, id);
        Assert.True(HousingRules.TrySlot(id, Dynamic, 4, out var slot));
        Assert.Equal(3, slot);
        Assert.False(HousingRules.TrySlot(id, Dynamic + 1, 4, out _));
        Assert.False(HousingRules.TrySlot(id | (1ul << 32), Dynamic, 4, out _));
        Assert.False(HousingRules.TrySlot(id, Dynamic, 3, out _));
    }

    [Fact]
    public void OnlyOrdinaryUncoloredFurnitureUsesTheProvenProxyBlob() {
        Assert.True(HousingRules.OrdinaryFurniture(["Housing", "Furniture"], ["FurnitureInfoBehaviorTemplate", "RenderBehaviorTemplate"]));
        Assert.False(HousingRules.OrdinaryFurniture(["Housing", "Furniture"], ["FurnitureInfoBehaviorTemplate", "TeleportBehaviorTemplate"]));
        Assert.False(HousingRules.OrdinaryFurniture(["Housing", "Furniture"], ["FurnitureInfoBehaviorTemplate"], primaryColors: 16));
        Assert.False(HousingRules.OrdinaryFurniture(["Housing", "Pet"], ["FurnitureInfoBehaviorTemplate"]));
    }

    [Fact]
    public void NativePlainRequestEncodingAndBoundedDecoderAgreeAndRejectWrongOwnerCacheKeys() {
        var ledger = new HousingLedger { OwnerId = Owner, PackageNumber = 11 };
        var data = HousingCodec.Encode(HousingCodec.Manifest(ledger, 12333));
        byte[] bytes = data;
        Assert.Equal(new BlobRequest().GetHash(), BitConverter.ToUInt32(bytes, 0));
        Assert.Equal((ushort)7, BitConverter.ToUInt16(bytes, 4)); // no CoreObject or BINd header
        Assert.True(HousingCodec.AcceptRequest(data, 12333, 11));
        Assert.False(HousingCodec.AcceptRequest(data, 12333, 12));
        Assert.False(HousingCodec.AcceptRequest(data, 12334, 11));
        Assert.False(HousingCodec.AcceptRequest(new byte[129], 12333, 11));
        Assert.False(HousingCodec.AcceptRequest(bytes[..^1], 12333, 11));
        Assert.False(HousingCodec.AcceptRequest(bytes.Concat(new byte[] { 0 }).ToArray(), 12333, 11));
        var malformed = (byte[])bytes.Clone();
        BitConverter.GetBytes(uint.MaxValue).CopyTo(malformed, 4 + 2 + 7 + 8);
        Assert.False(HousingCodec.AcceptRequest(malformed, 12333, 11));
        var forged = HousingCodec.Manifest(ledger, 12333);
        forged.m_blobRequestObjectList[0].m_userData = 123;
        Assert.False(HousingCodec.AcceptRequest(HousingCodec.Encode(forged), 12333, 11));
    }

    [Fact]
    public void BlobUsesRealNativePropertyClassesAndRetainsStableTombstones() {
        var ledger = new HousingLedger { PackageNumber = 9 };
        ledger.TryPlace(Entry(1), out var first);
        ledger.TryPlace(Entry(2), out _);
        ledger.TryPickup(first);
        var blob = HousingCodec.Blob(ledger, 12333);
        var packed = Assert.IsType<HousingBlob>(blob.m_data);
        Assert.Equal("Housing", blob.m_type);
        Assert.Equal("Proxy", blob.m_subType);
        Assert.Equal(9, blob.m_packageNumber);
        Assert.Equal(2, packed.m_housingBlobObjectList.Count);
        Assert.Equal(0u, packed.m_housingBlobObjectList[0].m_gameObjectTemplateID);
        Assert.Equal(FurnitureTemplate, packed.m_housingBlobObjectList[1].m_gameObjectTemplateID);
        byte[] data = HousingCodec.Encode(blob);
        Assert.Equal(new Blob().GetHash(), BitConverter.ToUInt32(data, 0));
    }

    [Fact]
    public void OwnerPackagesRemainDistinctAcrossReloadEvenWhenZoneIdAndVersionsMatch() {
        var store = new Store();
        using var scope = store.Scope();
        var first = HousingCollection.Load(Owner, create: true)!;
        var second = HousingCollection.Load(Owner + 1, create: true)!;
        Assert.NotEqual(first.PackageNumber, second.PackageNumber);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.PackageNumber, HousingCollection.Load(Owner, create: true)!.PackageNumber);
        var request = HousingCodec.Encode(HousingCodec.Manifest(first, 12333));
        Assert.False(HousingCodec.AcceptRequest(request, 12333, second.PackageNumber));
    }

    [Fact]
    public void PlacementPickupAndReconnectRetainOneOriginalItemAndExactRugPosition() {
        var store = new Store();
        using var scope = store.Scope();
        HousingCollection.Load(Owner, create: true);
        var live = store.Login();
        var result = Place(live, store);
        Assert.True(result.Saved, result.Error);
        Assert.Empty(store.SavedWizard.InventoryBehavior.InventoryItemIds);
        Assert.Empty(live.InventoryBehavior.InventoryItemIds);
        Assert.NotNull(store.Documents["item/1"]); // no delete or replacement document
        var next = store.Login();
        var saved = HousingCollection.Load(Owner)!;
        Assert.Equal(178.765625f, saved.Entries[0].Z);
        var pickup = HousingCollection.Pickup(next, Owner, HousingRules.PlacedGlobalId(0, Dynamic), Dynamic);
        Assert.True(pickup.Saved, pickup.Error);
        Assert.Equal(9001ul, Assert.Single(store.SavedWizard.InventoryBehavior.InventoryItemIds));
        Assert.Equal(9001ul, Assert.Single(next.InventoryBehavior.InventoryItemIds));
        Assert.Equal(9001ul, Assert.Single(next.InventoryBehavior.Items).m_globalID.Full);
        Assert.False(HousingCollection.Pickup(next, Owner, HousingRules.PlacedGlobalId(0, Dynamic), Dynamic).Saved);
        Assert.Equal(9001ul, Assert.Single(store.Login().InventoryBehavior.InventoryItemIds));
    }

    [Fact]
    public void FailedPlacementSaveLeavesBackpackAndSavedRoomUnchanged() {
        var store = new Store();
        using var scope = store.Scope();
        HousingCollection.Load(Owner, create: true);
        var live = store.Login();
        store.FailSave = true;
        Assert.False(Place(live, store).Saved);
        Assert.Equal(9001ul, Assert.Single(live.InventoryBehavior.InventoryItemIds));
        Assert.Equal(9001ul, Assert.Single(store.SavedWizard.InventoryBehavior.InventoryItemIds));
        Assert.Empty(HousingCollection.Load(Owner)!.Entries);
    }

    [Fact]
    public void FailedPickupSaveLeavesPlacedItemAndBackpackUnchanged() {
        var store = new Store();
        using var scope = store.Scope();
        HousingCollection.Load(Owner, create: true);
        var live = store.Login();
        Assert.True(Place(live, store).Saved);
        store.FailSave = true;
        Assert.False(HousingCollection.Pickup(live, Owner, HousingRules.PlacedGlobalId(0, Dynamic), Dynamic).Saved);
        Assert.Empty(live.InventoryBehavior.InventoryItemIds);
        Assert.Empty(store.SavedWizard.InventoryBehavior.InventoryItemIds);
        Assert.False(HousingCollection.Load(Owner)!.Entries[0].Removed);
    }

    [Fact]
    public void AStaleLiveInventoryCannotPlaceAnItemAbsentFromTheSavedBackpack() {
        var store = new Store();
        using var scope = store.Scope();
        HousingCollection.Load(Owner, create: true);
        var live = store.Login();
        store.SavedWizard.InventoryBehavior.InventoryItemIds = [];
        Assert.False(Place(live, store).Saved);
        Assert.Empty(HousingCollection.Load(Owner)!.Entries);
    }

    [Fact]
    public void AVisitorAndAForeignItemDocumentCannotChangeTheOwnersRoom() {
        var store = new Store();
        using var scope = store.Scope();
        HousingCollection.Load(Owner, create: true);
        var live = store.Login();
        Assert.False(HousingCollection.Place(live, Owner + 1, 9001, 0, 0, 0, 0, store.FindItem).Saved);
        store.Documents["item/1"] = ((WizClientObjectItem)store.Documents["item/1"]) with { m_characterId = Owner + 1 };
        Assert.False(Place(live, store).Saved);
        Assert.Equal(9001ul, Assert.Single(store.SavedWizard.InventoryBehavior.InventoryItemIds));
    }

    private static HousingResult Place(Wizard live, Store store)
        => HousingCollection.Place(live, Owner, 9001, 11.234375f, -12.625f, 178.765625f, 0.73123455f, store.FindItem);

    private static HousingEntry Entry(ulong id, float x = 0, float y = 0, float z = 0, float yaw = 0)
        => new() { ItemId = id, ItemDocumentId = $"item/{id}", TemplateId = FurnitureTemplate, X = x, Y = y, Z = z, Yaw = yaw };

    private sealed class Store {
        internal readonly Dictionary<string, object> Documents = new();
        internal bool FailSave;
        internal Wizard SavedWizard => (Wizard)Documents["wizard/1"];

        internal Store() {
            Documents["wizard/1"] = new Wizard {
                CharId = Owner, Zone = HousingRules.DormZone,
                InventoryBehavior = new() { InventoryItemIds = [], Items = new() },
                EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = new(), SlotList = [] },
                StorageBehavior = new() { BankItemIds = [], Items = new() },
            };
            Documents["item/1"] = new WizClientObjectItem { m_globalID = 9001, m_templateID = FurnitureTemplate, m_characterId = Owner };
            Assert.True(SavedWizard.InventoryBehavior.AddItem((WizClientObjectItem)Documents["item/1"]));
        }

        internal Wizard Login() {
            var live = CloneWizard(SavedWizard);
            foreach (var id in live.InventoryBehavior.InventoryItemIds) {
                live.InventoryBehavior.Items.Add((WizClientObjectItem)Documents.Values.Single(x => x is WizClientObjectItem i && i.m_globalID == id));
            }
            return live;
        }

        internal IDisposable Scope() {
            var previous = WizardCollection.TestStoreScope.Value;
            var previousRows = WizardInventoryTransactions.TestRowsScope.Value;
            WizardCollection.TestStoreScope.Value = new(Open, (session, _) => session.Load<Wizard>("wizard/1"));
            WizardInventoryTransactions.TestRowsScope.Value = session => Documents
                .Where(pair => pair.Value is WizClientObjectItem)
                .Select(pair => session.Load<WizClientObjectItem>(pair.Key)).ToList();
            return new Restore(() => {
                WizardCollection.TestStoreScope.Value = previous;
                WizardInventoryTransactions.TestRowsScope.Value = previousRows;
            });
        }

        internal IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, HousingSessionProxy>();
            var proxy = (HousingSessionProxy)(object)session;
            proxy.Saved = Documents;
            proxy.Fail = () => FailSave;
            return session;
        }

        internal WizClientObjectItem FindItem(IDocumentSession session, ulong item, ulong owner) => session.Load<WizClientObjectItem>("item/1");

        internal static Wizard CloneWizard(Wizard w) => new() {
            CharId = w.CharId, Zone = w.Zone,
            InventoryBehavior = new() { InventoryItemIds = [.. w.InventoryBehavior.InventoryItemIds], Items = new() },
            EquipmentBehavior = new() { EquippedItemIds = [.. w.EquipmentBehavior.EquippedItemIds], EquippedItems = new(), SlotList = [] },
            StorageBehavior = new() { BankItemIds = [.. w.StorageBehavior.BankItemIds], Items = new() },
        };
    }

    public class HousingSessionProxy : DispatchProxy {
        internal Dictionary<string, object> Saved = null!;
        internal readonly Dictionary<string, object> Working = new();
        internal readonly HashSet<object> Ignored = new(ReferenceEqualityComparer.Instance);
        internal Func<bool> Fail = null!;
        private IAdvancedSessionOperations? _advanced;

        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            switch (method!.Name) {
                case "get_Advanced":
                    if (_advanced is null) {
                        _advanced = DispatchProxy.Create<IAdvancedSessionOperations, HousingAdvancedProxy>();
                        ((HousingAdvancedProxy)(object)_advanced).Owner = this;
                    }
                    return _advanced;
                case "Load": {
                    var id = (string)args![0]!;
                    if (Working.TryGetValue(id, out var found)) return found;
                    if (!Saved.TryGetValue(id, out var saved)) return null;
                    return Working[id] = Clone(saved);
                }
                case "Store": Working[(string)args![1]!] = args[0]!; return null;
                case "SaveChanges":
                    if (Fail()) throw new IOException("Injected transaction failure.");
                    foreach (var pair in Working) if (!Ignored.Contains(pair.Value)) Saved[pair.Key] = Clone(pair.Value);
                    return null;
                case "Dispose": return null;
                default: throw new NotSupportedException(method.Name);
            }
        }

        private static object Clone(object value) => value switch {
            HousingLedger l => l.Copy(),
            HousingPackageAllocator a => new HousingPackageAllocator { NextPackageNumber = a.NextPackageNumber },
            WizClientObjectItem i => i with { },
            Wizard w => Store.CloneWizard(w),
            _ => throw new NotSupportedException(value.GetType().Name),
        };
    }

    public class HousingAdvancedProxy : DispatchProxy {
        internal HousingSessionProxy Owner = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "set_OptimisticConcurrencyMode" => null,
            "IgnoreChangesFor" => Ignore(args![0]!),
            "GetDocumentId" => Owner.Working.FirstOrDefault(p => ReferenceEquals(p.Value, args![0])).Key,
            _ => throw new NotSupportedException(method.Name),
        };
        private object? Ignore(object item) { Owner.Ignored.Add(item); return null; }
    }

    private sealed class Restore(System.Action undo) : IDisposable { public void Dispose() => undo(); }
}
