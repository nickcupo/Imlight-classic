using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Imcodec.IO;
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
public sealed class HousingAtticTests : IDisposable {
    private const ulong Owner = 724555;
    private const uint Template = (1u << 28) - 4555;
    private const uint Dynamic = 0x2345;
    private const int Capacity = 300; // explicit fixture, not a historical allowance
    private readonly IDictionary<ulong, CoreTemplate> _cache;
    private readonly CoreTemplate? _previous;

    public HousingAtticTests() {
        EquipmentAttachConcurrencyTests.Configure();
        _cache = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        _cache.TryGetValue(Template, out _previous);
        _cache[Template] = new WizItemTemplate { m_templateID = Template, m_adjectiveList = ["Housing", "Furniture"],
            m_behaviors = [new RenderBehaviorTemplate(), new FurnitureInfoBehaviorTemplate()] };
    }
    public void Dispose() {
        if (_previous is null) _cache.Remove(Template);
        else _cache[Template] = _previous;
    }

    [Fact]
    public void BackpackAtticBackpackRetainsOriginalIdDocumentAndSurvivesReconnect() {
        var store = new Store(); using var scope = store.Scope();
        var live = store.Login();
        var moved = ToAttic(store, live);
        Assert.True(moved.Saved);
        Assert.Empty(store.Wizard.InventoryBehavior.InventoryItemIds);
        Assert.Empty(live.InventoryBehavior.Items);
        Assert.Equal(9001ul, Assert.Single(moved.Attic.Packages[0].Entries).ItemId);
        live = store.Login();
        var taken = HousingAtticCollection.MoveFromAttic(live, Owner, Id(moved.Added[0]), Dynamic, Capacity);
        Assert.True(taken.Saved);
        Assert.Equal(9001ul, Assert.Single(store.Wizard.InventoryBehavior.InventoryItemIds));
        Assert.Equal(9001ul, (ulong)Assert.Single(live.InventoryBehavior.Items).m_globalID);
        Assert.Single(store.Documents.Values.OfType<WizClientObjectItem>());
        Assert.False(HousingAtticCollection.MoveFromAttic(live, Owner, Id(moved.Added[0]), Dynamic, Capacity).Saved);
    }

    [Fact]
    public void PlaceFromAtticUsesOriginalDocumentAndExactRequestedFloatingCoordinates() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login();
        var moved = ToAttic(store, live);
        var placed = HousingAtticCollection.PlaceFromAttic(live, Owner, Id(moved.Added[0]), Dynamic, Capacity,
            11.234375f, -12.625f, 178.765625f, 0.73123455f);
        Assert.True(placed.Saved);
        Assert.Empty(store.Wizard.InventoryBehavior.InventoryItemIds);
        Assert.Equal(0, placed.Attic.Count);
        Assert.Equal(9001ul, placed.Entry.ItemId);
        Assert.Equal("item/9001", placed.Entry.ItemDocumentId);
        Exact(placed.Entry, store.Room.Entries[placed.RoomSlot]);
        Assert.Single(store.Documents.Values.OfType<WizClientObjectItem>());
    }

    [Fact]
    public void RoomAtticTransferPreservesUnpackedCoordinatesInLedger() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login();
        var placed = HousingCollection.Place(live, Owner, 9001, 11.234375f, -12.625f, 178.765625f, .73123455f, store.Find);
        Assert.True(placed.Saved);
        var moved = HousingAtticCollection.MoveToAttic(live, Owner, HousingRules.PlacedGlobalId((uint)placed.Slot, Dynamic), Dynamic, Capacity);
        Assert.True(moved.Saved);
        Assert.True(store.Room.Entries[placed.Slot].Removed);
        Exact(placed.Entry, moved.Attic.Packages[0].Entries[0]);
        Assert.False(moved.FromBackpack);
        Assert.Empty(live.InventoryBehavior.Items);
    }

    [Fact]
    public void PickUpAllExcludesExactRugAndEmitsConsecutiveCacheVersions() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login();
        store.AddPlaced(9002); store.AddPlaced(9003); store.AddPlaced(9004);
        var rug = store.Room.Entries[1];
        (rug.X, rug.Y, rug.Z, rug.Yaw) = (11.234375f, -12.625f, 178.765625f, .73123455f);
        var exact = rug.Copy(); var roomVersion = store.Room.Version; var atticVersion = store.Attic.Packages[0].Version;
        var result = HousingAtticCollection.PickUpAll(live, Owner, Dynamic, Capacity, Exceptions(1));
        Assert.True(result.Saved);
        Assert.Equal(2, result.Added.Count);
        Assert.Equal(new[] { roomVersion + 1, roomVersion + 2 }, result.RoomDeleted.Select(p => p.Version));
        Assert.Equal(new[] { atticVersion + 1, atticVersion + 2 }, result.Added.Select(p => p.Version));
        Assert.Equal(new[] { 0, 2 }, result.RoomDeleted.Select(p => p.Slot));
        Assert.False(store.Room.Entries[1].Removed); Exact(exact, store.Room.Entries[1]);
        Assert.True(store.Room.Entries[0].Removed); Assert.True(store.Room.Entries[2].Removed);
        Assert.Equal(2, store.Attic.Count);
        Assert.Equal(9001ul, Assert.Single(store.Wizard.InventoryBehavior.InventoryItemIds));
    }

    [Fact]
    public void PickUpAllCapacityFailureDoesNotPartiallyMoveAnything() {
        var store = new Store(); using var scope = store.Scope(); store.AddPlaced(9002); store.AddPlaced(9003);
        var version = store.Room.Version;
        var result = HousingAtticCollection.PickUpAll(store.Login(), Owner, Dynamic, 1, Exceptions());
        Assert.False(result.Saved); Assert.True(result.CapacityExceeded);
        Assert.Equal(0, store.Attic.Count); Assert.Equal(version, store.Room.Version);
        Assert.All(store.Room.Entries, e => Assert.False(e.Removed));
    }

    [Fact]
    public void PickUpAllSaveFailureRollsBackBothLedgersAndBackpack() {
        var store = new Store(); using var scope = store.Scope(); store.AddPlaced(9002); store.AddPlaced(9003);
        var version = store.Room.Version; store.FailSave = true;
        var result = HousingAtticCollection.PickUpAll(store.Login(), Owner, Dynamic, Capacity, Exceptions());
        Assert.False(result.Saved); Assert.Equal(0, store.Attic.Count); Assert.Equal(version, store.Room.Version);
        Assert.All(store.Room.Entries, e => Assert.False(e.Removed));
        Assert.Equal(9001ul, Assert.Single(store.Wizard.InventoryBehavior.InventoryItemIds));
    }

    [Fact]
    public void BackpackAtticSaveFailureDoesNotRemoveLiveItem() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login(); store.FailSave = true;
        Assert.False(ToAttic(store, live).Saved);
        Assert.Equal(9001ul, Assert.Single(store.Wizard.InventoryBehavior.InventoryItemIds));
        Assert.Equal(9001ul, (ulong)Assert.Single(live.InventoryBehavior.Items).m_globalID);
        Assert.Equal(0, store.Attic.Count);
    }

    [Fact]
    public void FullBackpackCannotLoseAtticItem() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login(); var moved = ToAttic(store, live);
        store.Wizard.InventoryBehavior.InventoryItemIds = Enumerable.Range(0, ServerWizInventoryBehavior.MaxItemsAllowed).Select(i => (ulong)i + 100000).ToList();
        Assert.False(HousingAtticCollection.MoveFromAttic(live, Owner, Id(moved.Added[0]), Dynamic, Capacity).Saved);
        Assert.Equal(1, store.Attic.Count);
        Assert.Equal(2u, store.Attic.Packages[0].Version);
    }

    [Fact]
    public void ForeignOriginalDocumentRefusesWholeBatch() {
        var store = new Store(); using var scope = store.Scope(); store.AddPlaced(9002); store.AddPlaced(9003);
        ((WizClientObjectItem)store.Documents["item/9003"]).m_characterId = Owner + 1;
        Assert.False(HousingAtticCollection.PickUpAll(store.Login(), Owner, Dynamic, Capacity, Exceptions()).Saved);
        Assert.Equal(0, store.Attic.Count); Assert.All(store.Room.Entries, e => Assert.False(e.Removed));
    }

    [Theory]
    [InlineData("backpack")]
    [InlineData("equipment")]
    [InlineData("bank")]
    public void ItemAlreadyClaimedElsewhereCannotReturnFromAttic(string location) {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login(); var moved = ToAttic(store, live);
        var ids = location switch { "backpack" => store.Wizard.InventoryBehavior.InventoryItemIds,
            "equipment" => store.Wizard.EquipmentBehavior.EquippedItemIds, _ => store.Wizard.StorageBehavior.BankItemIds };
        ids.Add(9001);
        Assert.False(HousingAtticCollection.MoveFromAttic(live, Owner, Id(moved.Added[0]), Dynamic, Capacity).Saved);
        Assert.Equal(1, store.Attic.Count);
    }

    [Fact]
    public void VisitorWrongDynamicProcAndMalformedSyntheticIdsCannotClaimAttic() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login(); var moved = ToAttic(store, live); var id = Id(moved.Added[0]);
        Assert.False(HousingAtticCollection.MoveFromAttic(live, Owner + 1, id, Dynamic, Capacity).Saved);
        Assert.False(HousingAtticCollection.MoveFromAttic(live, Owner, id, Dynamic + 1, Capacity).Saved);
        Assert.False(HousingAtticCollection.MoveFromAttic(live, Owner, id | (1ul << 32), Dynamic, Capacity).Saved);
        Assert.False(HousingAtticCollection.MoveFromAttic(live, Owner, HousingRules.PlacedGlobalId(0, Dynamic), Dynamic, Capacity).Saved);
        Assert.Equal(1, store.Attic.Count);
    }

    [Fact]
    public void CapacityZeroGateRefusesWithoutChangingSavedData() {
        var store = new Store(); using var scope = store.Scope();
        Assert.False(HousingAtticCollection.MoveToAttic(store.Login(), Owner, 9001, Dynamic, 0, store.Find).Saved);
        Assert.Equal(0, store.Attic.Count); Assert.Equal(9001ul, Assert.Single(store.Wizard.InventoryBehavior.InventoryItemIds));
    }

    [Fact]
    public void NativePackagesCrossBoundaryWithoutCollidingWithRoomAndReuseTombstones() {
        var attic = EmptyAttic();
        for (var i = 0; i < 201; i++) Assert.True(attic.TryAdd(Entry((ulong)i + 1), Capacity, out _));
        Assert.Equal(200, attic.Packages[0].Entries.Count); Assert.Single(attic.Packages[1].Entries);
        Assert.True(attic.TryFind(HousingRules.PlacedGlobalId(400, Dynamic), Dynamic, out var p, out var slot));
        Assert.Equal(1, p); Assert.Equal(0, slot);
        Assert.True(attic.TryRemove(0, 50, out var deleted));
        Assert.True(attic.TryAdd(Entry(999), Capacity, out var added));
        Assert.Equal(deleted.CacheIndex, added.CacheIndex); Assert.Equal(250u, added.CacheIndex);
        Assert.Equal(201ul, attic.Packages[1].Entries[0].ItemId);
        Assert.False(attic.TryFind(HousingRules.PlacedGlobalId(50, Dynamic), Dynamic, out _, out _));
    }

    [Fact]
    public void VersionExhaustionNeverWrapsAndDoesNotRemoveExistingItem() {
        var attic = EmptyAttic(); Assert.True(attic.TryAdd(Entry(1), Capacity, out _));
        attic.Packages[0].Version = uint.MaxValue;
        Assert.False(attic.TryRemove(0, 0, out _)); Assert.False(attic.Packages[0].Entries[0].Removed);
        attic.Packages[1].Version = uint.MaxValue;
        Assert.False(attic.TryAdd(Entry(2), Capacity, out _)); Assert.Equal(1, attic.Count);
    }

    [Fact]
    public void AtticManifestAndBlobUseActualSubtypeContainerOffsetsAndZeroDisplayCoordinates() {
        var attic = EmptyAttic(); Assert.True(attic.TryAdd(Entry(1), Capacity, out _));
        var request = HousingAtticCodec.Manifest(attic);
        Assert.Equal(attic.ContainerId, (ulong)request.m_associatedGID);
        Assert.True(HousingAtticCodec.TryRequests(HousingCodec.Encode(request), attic, out var indices));
        Assert.Equal(new[] { 0, 1 }, indices);
        var blob = HousingAtticCodec.Blob(attic, attic.Packages[0]);
        Assert.Equal("Attic", blob.m_subType);
        var display = Assert.Single(Assert.IsType<HousingBlob>(blob.m_data).m_housingBlobObjectList);
        Assert.Equal(Template, display.m_gameObjectTemplateID);
        Assert.Equal(0u, display.m_positionXY); Assert.Equal(0f, display.m_positionZ);
    }

    [Fact]
    public void AtticRequestsRejectWrongOwnerContainerPackagesOffsetsCountsAndSuffix() {
        var attic = EmptyAttic(); var req = HousingAtticCodec.Manifest(attic); req.m_associatedGID = attic.ContainerId + 1;
        Assert.False(HousingAtticCodec.TryRequests(HousingCodec.Encode(req), attic, out _));
        req = HousingAtticCodec.Manifest(attic); req.m_blobRequestObjectList[0].m_packageNumber++;
        Assert.False(HousingAtticCodec.TryRequests(HousingCodec.Encode(req), attic, out _));
        req = HousingAtticCodec.Manifest(attic); req.m_blobRequestObjectList[0].m_userData = 0;
        Assert.False(HousingAtticCodec.TryRequests(HousingCodec.Encode(req), attic, out _));
        req = HousingAtticCodec.Manifest(attic); req.m_blobRequestObjectList[0].m_objectCount = uint.MaxValue;
        Assert.False(HousingAtticCodec.TryRequests(HousingCodec.Encode(req), attic, out _));
        byte[] good = HousingCodec.Encode(HousingAtticCodec.Manifest(attic));
        Assert.False(HousingAtticCodec.TryRequests(good[..^1], attic, out _));
        Assert.False(HousingAtticCodec.TryRequests(good.Concat(new byte[] { 0 }).ToArray(), attic, out _));
        Assert.False(HousingAtticCodec.TryRequests(new byte[257], attic, out _));
    }

    [Fact]
    public void NativeExceptionsRejectForeignStaleDuplicateUnboundedAndTruncatedIds() {
        var room = new HousingLedger { OwnerId = Owner, PackageNumber = 3 }; room.TryPlace(Entry(1), out _);
        Assert.True(HousingAtticCodec.TryExceptions(Exceptions(0), room, Dynamic, out var excluded)); Assert.Contains(0, excluded);
        Assert.False(HousingAtticCodec.TryExceptions(Exceptions(0, 0), room, Dynamic, out _));
        var foreign = HousingCodec.Encode(new HousingItemList { m_housingItemGIDList = [HousingRules.PlacedGlobalId(0, Dynamic + 1)] });
        Assert.False(HousingAtticCodec.TryExceptions(foreign, room, Dynamic, out _));
        byte[] good = Exceptions(0); Assert.False(HousingAtticCodec.TryExceptions(good[..^1], room, Dynamic, out _));
        Assert.False(HousingAtticCodec.TryExceptions(good.Concat(new byte[] { 0 }).ToArray(), room, Dynamic, out _));
        var huge = (byte[])good.Clone(); BitConverter.GetBytes(uint.MaxValue).CopyTo(huge, 4);
        Assert.False(HousingAtticCodec.TryExceptions(huge, room, Dynamic, out _));
        huge[0] ^= 1; Assert.False(HousingAtticCodec.TryExceptions(huge, room, Dynamic, out _));
        room.TryPickup(0); Assert.False(HousingAtticCodec.TryExceptions(good, room, Dynamic, out _));
    }

    [Fact]
    public void DiscardArchivesOriginalDocumentAndMakesItsCacheIdUnavailable() {
        var store = new Store(); using var scope = store.Scope(); var live = store.Login(); var moved = ToAttic(store, live);
        var result = HousingAtticCollection.Discard(live, Owner, Id(moved.Added[0]), Dynamic, Capacity);
        Assert.True(result.Saved); Assert.Equal(0, store.Attic.Count);
        var archive = Assert.Single(store.Documents.Values.OfType<DiscardedHousingItem>());
        Assert.Equal(Owner, archive.OwnerId); Assert.Equal("item/9001", archive.Entry.ItemDocumentId);
        Assert.Single(store.Documents.Values.OfType<WizClientObjectItem>());
        Assert.False(HousingAtticCollection.MoveFromAttic(live, Owner, Id(moved.Added[0]), Dynamic, Capacity).Saved);
    }

    [Fact]
    public void PersistentAtticPackageAllocatorSurvivesReloadAndNeverReusesRoomKeys() {
        var store = new Store(); using var scope = store.Scope();
        var room = HousingCollection.Load(Owner + 1, true);
        var attic = HousingAtticCollection.Load(Owner + 1, true);
        var again = HousingAtticCollection.Load(Owner + 1, true);
        Assert.Equal(attic.ContainerId, again.ContainerId);
        Assert.Equal(attic.Packages.Select(p => p.PackageNumber), again.Packages.Select(p => p.PackageNumber));
        Assert.DoesNotContain(room.PackageNumber, attic.Packages.Select(p => p.PackageNumber));
        Assert.Equal(3, new[] { room.PackageNumber }.Concat(attic.Packages.Select(p => p.PackageNumber)).Distinct().Count());
    }

    private static HousingAtticResult ToAttic(Store store, Wizard live)
        => HousingAtticCollection.MoveToAttic(live, Owner, 9001, Dynamic, Capacity, store.Find);
    private static ulong Id(AtticPatch patch) => HousingRules.PlacedGlobalId(patch.CacheIndex, Dynamic);
    private static HousingEntry Entry(ulong id) => new() { ItemId = id, TemplateId = Template, ItemDocumentId = $"item/{id}",
        X = 11.234375f, Y = -12.625f, Z = 178.765625f, Yaw = .73123455f };
    private static AtticLedger EmptyAttic() => new() { OwnerId = Owner, ContainerId = 987654321, Packages = [
        new() { PackageNumber = 4, UserData = 200 }, new() { PackageNumber = 5, UserData = 400 }] };
    private static ByteString Exceptions(params int[] slots) => HousingCodec.Encode(new HousingItemList {
        m_housingItemGIDList = slots.Select(i => (GID)HousingRules.PlacedGlobalId((uint)i, Dynamic)).ToList(),
    });
    private static void Exact(HousingEntry expected, HousingEntry actual) {
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.X), BitConverter.SingleToInt32Bits(actual.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Y), BitConverter.SingleToInt32Bits(actual.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Z), BitConverter.SingleToInt32Bits(actual.Z));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Yaw), BitConverter.SingleToInt32Bits(actual.Yaw));
    }

    private sealed class Store {
        internal readonly Dictionary<string, object> Documents = new(); internal bool FailSave;
        internal Wizard Wizard => (Wizard)Documents["wizard/1"];
        internal HousingLedger Room => (HousingLedger)Documents[HousingLedger.DocumentId(Owner)];
        internal AtticLedger Attic => (AtticLedger)Documents[AtticLedger.DocumentId(Owner)];
        internal Store() {
            Documents["wizard/1"] = new Wizard { CharId = Owner, Zone = HousingRules.DormZone,
                InventoryBehavior = new() { InventoryItemIds = [], Items = new() },
                EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = new(), SlotList = [] },
                StorageBehavior = new() { BankItemIds = [], Items = new() } };
            var item = new WizClientObjectItem { m_globalID = 9001, m_templateID = Template, m_characterId = Owner };
            Documents["item/9001"] = item; Assert.True(Wizard.InventoryBehavior.AddItem(item));
            Documents[HousingLedger.DocumentId(Owner)] = new HousingLedger { OwnerId = Owner, PackageNumber = 3 };
            Documents[AtticLedger.DocumentId(Owner)] = EmptyAttic();
            Documents[HousingPackageAllocator.DocumentId] = new HousingPackageAllocator { NextPackageNumber = 6 };
        }
        internal void AddPlaced(ulong id) {
            Documents[$"item/{id}"] = new WizClientObjectItem { m_globalID = id, m_templateID = Template, m_characterId = Owner };
            Assert.True(Room.TryPlace(Entry(id), out _));
        }
        internal Wizard Login() {
            var live = CloneWizard(Wizard);
            foreach (var id in live.InventoryBehavior.InventoryItemIds) live.InventoryBehavior.Items.Add((WizClientObjectItem)Documents[$"item/{id}"]);
            return live;
        }
        internal WizClientObjectItem Find(IDocumentSession session, ulong id, ulong owner) => session.Load<WizClientObjectItem>($"item/{id}");
        internal IDisposable Scope() {
            var previous = WizardCollection.TestStoreScope.Value;
            var previousRows = WizardInventoryTransactions.TestRowsScope.Value;
            WizardCollection.TestStoreScope.Value = new(Open, (s, _) => s.Load<Wizard>("wizard/1"));
            WizardInventoryTransactions.TestRowsScope.Value = session => Documents
                .Where(pair => pair.Value is WizClientObjectItem)
                .Select(pair => session.Load<WizClientObjectItem>(pair.Key)).ToList();
            return new Restore(() => {
                WizardCollection.TestStoreScope.Value = previous;
                WizardInventoryTransactions.TestRowsScope.Value = previousRows;
            });
        }
        private IDocumentSession Open() {
            var s = DispatchProxy.Create<IDocumentSession, AtticSessionProxy>(); var proxy = (AtticSessionProxy)(object)s;
            proxy.Saved = Documents; proxy.Fail = () => FailSave; return s;
        }
        internal static Wizard CloneWizard(Wizard w) => new() { CharId = w.CharId, Zone = w.Zone,
            InventoryBehavior = new() { InventoryItemIds = [.. w.InventoryBehavior.InventoryItemIds], Items = new() },
            EquipmentBehavior = new() { EquippedItemIds = [.. w.EquipmentBehavior.EquippedItemIds], EquippedItems = new(), SlotList = [] },
            StorageBehavior = new() { BankItemIds = [.. w.StorageBehavior.BankItemIds], Items = new() } };
    }
    public class AtticSessionProxy : DispatchProxy {
        internal Dictionary<string, object> Saved = null!; internal readonly Dictionary<string, object> Working = new();
        internal readonly HashSet<object> Ignored = new(ReferenceEqualityComparer.Instance);
        internal Func<bool> Fail = null!; private IAdvancedSessionOperations? _advanced;
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            switch (method!.Name) {
                case "get_Advanced":
                    if (_advanced is null) { _advanced = DispatchProxy.Create<IAdvancedSessionOperations, AtticAdvancedProxy>();
                        ((AtticAdvancedProxy)(object)_advanced).Owner = this; } return _advanced;
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
                    foreach (var pair in Working) if (!Ignored.Contains(pair.Value)) Saved[pair.Key] = Clone(pair.Value); return null;
                case "Dispose": return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
        private static object Clone(object value) => value switch {
            HousingLedger l => l.Copy(), AtticLedger a => a.Copy(),
            HousingPackageAllocator a => new HousingPackageAllocator { NextPackageNumber = a.NextPackageNumber },
            DiscardedHousingItem d => new DiscardedHousingItem { OwnerId = d.OwnerId, Entry = d.Entry.Copy() },
            WizClientObjectItem i => i with { }, Wizard w => Store.CloneWizard(w),
            _ => throw new NotSupportedException(value.GetType().Name),
        };
    }
    public class AtticAdvancedProxy : DispatchProxy {
        internal AtticSessionProxy Owner = null!;
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
