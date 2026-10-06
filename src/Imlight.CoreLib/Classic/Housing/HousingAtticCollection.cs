using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.Classic.Housing;

internal sealed record HousingAtticResult(string Error) {
    internal AtticLedger Attic { get; init; }
    internal HousingLedger Room { get; init; }
    internal List<AtticPatch> Added { get; init; } = [];
    internal List<AtticPatch> Deleted { get; init; } = [];
    internal List<HousingDeletePatch> RoomDeleted { get; init; } = [];
    internal int RoomSlot { get; init; } = -1;
    internal HousingEntry Entry { get; init; }
    internal WizClientObjectItem Item { get; init; }
    internal ByteString ItemData { get; init; }
    internal bool FromBackpack { get; init; }
    internal bool CapacityExceeded { get; init; }
    internal bool Saved => Error is null && Attic is not null;
}

// CLASSIC: each transfer claims the character write lane and saves the original item reference,
// room ledger, attic ledger and backpack membership together. Cache ids never replace item ids.
internal static class HousingAtticCollection {
    internal static AtticLedger Load(ulong owner, bool create = false) {
        if (owner == 0) return null;
        lock (HousingCollection.PackageLock) {
            using var session = HousingCollection.Open();
            var ledger = session.Load<AtticLedger>(AtticLedger.DocumentId(owner));
            if (ledger is not null || !create) return ledger;
            session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            var allocator = session.Load<HousingPackageAllocator>(HousingPackageAllocator.DocumentId);
            var fresh = allocator is null;
            allocator ??= new HousingPackageAllocator();
            if (allocator.NextPackageNumber <= 0 || allocator.NextPackageNumber > int.MaxValue - HousingRules.AtticPackageCount)
                throw new InvalidOperationException("Housing package ids are exhausted.");
            ledger = new AtticLedger { OwnerId = owner, ContainerId = RandomGen.GenerateGUID() };
            for (var i = 0; i < HousingRules.AtticPackageCount; i++) ledger.Packages.Add(new AtticPackage {
                PackageNumber = allocator.NextPackageNumber++, UserData = HousingRules.AtticUserData(i),
            });
            if (fresh) session.Store(allocator, HousingPackageAllocator.DocumentId);
            session.Store(ledger, AtticLedger.DocumentId(owner));
            session.SaveChanges();
            return ledger;
        }
    }

    internal static HousingAtticResult MoveToAttic(Wizard live, ulong owner, ulong itemId, uint dynamicProc, int capacity,
        Func<IDocumentSession, ulong, ulong, WizClientObjectItem> findDocument = null) {
        if (!Editable(live, owner, capacity)) return Refused();
        return Mutate(live, owner, (session, wizard, room, attic) => {
            HousingEntry entry;
            WizClientObjectItem item;
            var backpack = HousingCollection.InBackpack(wizard, itemId);
            var slot = -1;
            if (backpack) {
                item = findDocument is null ? session.Query<WizClientObjectItem>(collectionName: WizardItemCollection.CollectionName)
                    .Customize(q => q.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                    .FirstOrDefault(i => i.m_globalID == itemId && i.m_characterId == owner)
                    : findDocument(session, itemId, owner);
                var liveItem = live.InventoryBehavior?.GetItem(itemId);
                if (!Owned(item, owner, itemId) || !HousingCollection.Ordinary(item)
                    || liveItem is null || liveItem.m_templateID != item.m_templateID
                    || room.Entries.Any(e => !e.Removed && e.ItemId == itemId)) return Refused();
                entry = new() { ItemId = itemId, ItemDocumentId = session.Advanced.GetDocumentId(item), TemplateId = (uint)item.m_templateID.Full };
            }
            else {
                if (!HousingRules.TrySlot(itemId, dynamicProc, room.Entries.Count, out slot) || !room.Active(slot)) return Refused();
                entry = room.Entries[slot].Copy();
                item = Original(session, wizard, owner, entry);
                if (item is null || !HousingCollection.Ordinary(item)) return Refused();
            }
            if (attic.Count >= capacity) return Full();
            if (!attic.TryAdd(entry, capacity, out var add)) return Refused();
            if (backpack) wizard.InventoryBehavior.InventoryItemIds = wizard.InventoryBehavior.InventoryItemIds.Where(id => id != entry.ItemId).ToList();
            else if (!room.TryPickup(slot)) return Refused();
            return new(null) { Attic = attic.Copy(), Room = room.Copy(), Entry = entry.Copy(), Item = item,
                FromBackpack = backpack, Added = [add], RoomDeleted = backpack ? [] : [new(room.Version, slot)] };
        }, result => {
            if (result.FromBackpack && !live.InventoryBehavior.RemoveItem(result.Entry.ItemId, out _))
                throw new InvalidOperationException("Committed attic transfer needs inventory resynchronization.");
        });
    }

    internal static HousingAtticResult MoveFromAttic(Wizard live, ulong owner, ulong syntheticId, uint dynamicProc, int capacity) {
        if (!Editable(live, owner, capacity)) return Refused();
        return Mutate(live, owner, (session, wizard, room, attic) => {
            if (!attic.TryFind(syntheticId, dynamicProc, out var p, out var slot)) return Refused();
            var entry = attic.Packages[p].Entries[slot];
            var item = Original(session, wizard, owner, entry);
            var ids = wizard.InventoryBehavior?.InventoryItemIds;
            if (item is null || ids is null || ids.Count >= ServerWizInventoryBehavior.MaxItemsAllowed
                || room.Entries.Any(e => !e.Removed && e.ItemId == entry.ItemId)) return Refused();
            var serializer = new CoreObjectSerializer(versionable: false, behaviors: SerializerFlags.None);
            if (!serializer.Serialize(item, (PropertyFlags)24, out var data) || !attic.TryRemove(p, slot, out var del)) return Refused();
            wizard.InventoryBehavior.InventoryItemIds = [.. ids, entry.ItemId];
            return new(null) { Attic = attic.Copy(), Room = room.Copy(), Entry = entry.Copy(), Item = item, ItemData = data, Deleted = [del] };
        }, result => {
            if (!live.InventoryBehavior.AddItem(result.Item)) throw new InvalidOperationException("Committed attic pickup needs inventory resynchronization.");
        });
    }

    internal static HousingAtticResult PlaceFromAttic(Wizard live, ulong owner, ulong syntheticId, uint dynamicProc, int capacity,
        float x, float y, float z, float yaw) {
        if (!Editable(live, owner, capacity) || !HousingRules.ValidPosition(x, y, z, yaw)) return Refused();
        return Mutate(live, owner, (session, wizard, room, attic) => {
            if (!attic.TryFind(syntheticId, dynamicProc, out var p, out var slot)) return Refused();
            var entry = attic.Packages[p].Entries[slot].Copy();
            var item = Original(session, wizard, owner, entry);
            if (item is null || !HousingCollection.Ordinary(item)) return Refused();
            (entry.X, entry.Y, entry.Z, entry.Yaw) = (x, y, z, yaw);
            if (!room.TryPlace(entry, out var placed) || !attic.TryRemove(p, slot, out var del)) return Refused();
            return new(null) { Attic = attic.Copy(), Room = room.Copy(), Entry = entry.Copy(), RoomSlot = placed, Deleted = [del] };
        });
    }

    internal static HousingAtticResult PickUpAll(Wizard live, ulong owner, uint dynamicProc, int capacity, ByteString exceptions) {
        if (!Editable(live, owner, capacity)) return Refused();
        return Mutate(live, owner, (session, wizard, room, attic) => {
            if (!HousingAtticCodec.TryExceptions(exceptions, room, dynamicProc, out var excluded)) return Refused();
            var slots = Enumerable.Range(0, room.Entries.Count).Where(i => room.Active(i) && !excluded.Contains(i)).ToArray();
            if (attic.Count + slots.Length > capacity) return Full();
            // Validate the entire batch before changing tracked documents. One unsupported/foreign
            // item refuses the operation; partial pickup is never reported as complete.
            foreach (var slot in slots) {
                var item = Original(session, wizard, owner, room.Entries[slot]);
                if (item is null || !HousingCollection.Ordinary(item)) return Refused();
            }
            var added = new List<AtticPatch>();
            var deleted = new List<HousingDeletePatch>();
            foreach (var slot in slots) {
                if (!attic.TryAdd(room.Entries[slot], capacity, out var add) || !room.TryPickup(slot)) return Refused();
                added.Add(add); deleted.Add(new(room.Version, slot));
            }
            return new(null) { Attic = attic.Copy(), Room = room.Copy(), Added = added, RoomDeleted = deleted };
        });
    }

    // The player can discard furniture, but the server does not permanently delete its document.
    // An inaccessible archive records the original reference in the same transaction as removal.
    internal static HousingAtticResult Discard(Wizard live, ulong owner, ulong syntheticId, uint dynamicProc, int capacity) {
        if (!Editable(live, owner, capacity)) return Refused();
        return Mutate(live, owner, (session, wizard, room, attic) => {
            if (!attic.TryFind(syntheticId, dynamicProc, out var p, out var slot)) return Refused();
            var entry = attic.Packages[p].Entries[slot];
            if (Original(session, wizard, owner, entry) is null || room.Entries.Any(e => !e.Removed && e.ItemId == entry.ItemId)
                || !attic.TryRemove(p, slot, out var del)) return Refused();
            session.Store(new DiscardedHousingItem { OwnerId = owner, Entry = entry.Copy() }, $"ClassicDiscardedHousing/{owner}/{entry.ItemId}");
            return new(null) { Attic = attic.Copy(), Room = room.Copy(), Deleted = [del] };
        });
    }

    private static HousingAtticResult Mutate(Wizard live, ulong owner,
        Func<IDocumentSession, Wizard, HousingLedger, AtticLedger, HousingAtticResult> operation,
        Action<HousingAtticResult> after = null) {
        var result = Refused();
        try {
            var saved = WizardCollection.CommitCharacterMutation(owner, (session, wizard) => {
                var room = session.Load<HousingLedger>(HousingLedger.DocumentId(owner));
                var attic = session.Load<AtticLedger>(AtticLedger.DocumentId(owner));
                if (wizard.CharId != owner || room?.OwnerId != owner || attic?.OwnerId != owner || !attic.Valid()) return false;
                result = operation(session, wizard, room, attic);
                return result.Saved;
            }, _ => after?.Invoke(result));
            return saved ? result : result.Saved ? Refused() : result;
        }
        catch (Exception ex) {
            Logger.Error("Attic transfer for {0} was not completed: {1}", Logger.Args(owner, ex.Message));
            return new("Your furniture could not be saved. Please leave the dorm and try again.");
        }
    }

    private static WizClientObjectItem Original(IDocumentSession session, Wizard wizard, ulong owner, HousingEntry entry) {
        if (wizard.InventoryBehavior?.InventoryItemIds?.Contains(entry.ItemId) != false || HousingCollection.OutsideBackpack(wizard, entry.ItemId)) return null;
        var item = session.Load<WizClientObjectItem>(entry.ItemDocumentId);
        return Owned(item, owner, entry.ItemId) && item.m_templateID == entry.TemplateId ? item : null;
    }
    private static bool Owned(WizClientObjectItem item, ulong owner, ulong itemId)
        => item is not null && item.m_characterId == owner && item.m_globalID == itemId;
    private static bool Editable(Wizard live, ulong owner, int capacity)
        => HousingRules.CanEdit(live?.CharId ?? 0, owner, live?.Zone) && !live.IsInDuel
            && capacity > 0 && capacity <= HousingRules.AtticCompatibilityCeiling;
    private static HousingAtticResult Refused() => new("That furniture cannot be moved to or from the attic here.");
    private static HousingAtticResult Full() => new("Your attic is full.") { CapacityExceeded = true };
}
