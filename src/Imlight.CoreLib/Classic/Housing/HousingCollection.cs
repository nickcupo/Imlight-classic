using System;
using System.Linq;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.Classic.Housing;

internal readonly record struct HousingResult(string Error, HousingLedger Ledger = null, HousingEntry Entry = null,
    int Slot = -1, WizClientObjectItem Item = null, ByteString ItemData = default) {
    internal bool Saved => Error is null && Ledger is not null;
}

// CLASSIC: placement transfers the original item out of the backpack in the same Raven transaction
// as the room ledger. Its existing item document is retained, and pickup returns that exact id.
internal static class HousingCollection {
    internal static readonly object PackageLock = new();
    internal static IDocumentSession Open()
        => WizardCollection.TestStoreScope.Value?.Open() ?? PlayerDatabase.Instance.Store.OpenSession();

    internal static HousingLedger Load(ulong owner, bool create = false) => Load(HousingRoomIdentity.Dorm(owner), create);

    internal static HousingLedger Load(HousingRoomIdentity room, bool create = false) {
        var owner = room?.OwnerId ?? 0;
        if (owner == 0) return null;
        lock (PackageLock) {
            using var session = Open();
            var capacity = HousingRules.PackageSlots;
            if (room.DeedId != 0) {
                var wizard = HouseCollection.LoadWizard(session, owner);
                var house = HouseCollection.Owned(session, wizard, owner, room.DeedId);
                if (house is null || !HouseCatalog.TryRoom(house.TemplateId, room.Zone, out capacity)) return null;
            }
            else if (!HousingRules.IsDorm(room.Zone)) return null;
            var ledger = session.Load<HousingLedger>(HousingLedger.DocumentId(room));
            if (ledger is not null || !create) return ledger;
            session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            var allocator = session.Load<HousingPackageAllocator>(HousingPackageAllocator.DocumentId);
            var freshAllocator = allocator is null;
            allocator ??= new HousingPackageAllocator();
            var packages = capacity > HousingRules.PackageSlots ? 2 : 1;
            if (allocator.NextPackageNumber <= 0 || allocator.NextPackageNumber > int.MaxValue - packages)
                throw new InvalidOperationException("Housing package ids are exhausted.");
            ledger = new HousingLedger { OwnerId = owner, DeedId = room.DeedId, Zone = room.Zone,
                Capacity = capacity, PackageNumber = allocator.NextPackageNumber++ };
            if (packages == 2) ledger.SecondPackageNumber = allocator.NextPackageNumber++;
            if (freshAllocator) session.Store(allocator, HousingPackageAllocator.DocumentId);
            session.Store(ledger, HousingLedger.DocumentId(room));
            session.SaveChanges();
            return ledger;
        }
    }

    internal static HousingResult Place(Wizard live, ulong owner, ulong itemId, float x, float y, float z, float yaw,
        Func<IDocumentSession, ulong, ulong, WizClientObjectItem> findDocument = null, HousingRoomIdentity room = null) {
        room ??= HousingRoomIdentity.Dorm(owner);
        if (!Editable(live, room) || live.IsInDuel
            || !HousingRules.ValidPosition(x, y, z, yaw)) return Refused();
        var item = live.InventoryBehavior?.GetItem(itemId);
        if (!Ordinary(item)) return new("This furniture type is not supported in the dorm yet.");
        HousingResult result = Refused();
        try {
            var saved = WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                var ledger = session.Load<HousingLedger>(HousingLedger.DocumentId(room));
                if (!ValidRoom(session, wizard, room, ledger) || !InBackpack(wizard, itemId)) return false;
                var doc = findDocument is null ? session.Query<WizClientObjectItem>(collectionName: WizardItemCollection.CollectionName)
                    .Customize(q => q.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                    .FirstOrDefault(i => i.m_globalID == itemId && i.m_characterId == owner)
                    : findDocument(session, itemId, owner);
                if (doc is null || doc.m_characterId != owner || doc.m_globalID != itemId || doc.m_templateID != item.m_templateID) return false;
                var entry = new HousingEntry {
                    ItemId = itemId, ItemDocumentId = session.Advanced.GetDocumentId(doc), TemplateId = (uint)doc.m_templateID.Full,
                    X = x, Y = y, Z = z, Yaw = yaw,
                };
                if (!ledger.TryPlace(entry, out var slot)) return false;
                wizard.InventoryBehavior.InventoryItemIds = wizard.InventoryBehavior.InventoryItemIds.Where(id => id != itemId).ToList();
                result = new(null, ledger.Copy(), entry.Copy(), slot, item);
                return true;
            }, committed => live.InventoryBehavior.RemoveItem(itemId, out _));
            return saved ? result : Refused();
        }
        catch (Exception ex) { return Failed(live, "place", ex); }
    }

    internal static HousingResult Update(Wizard live, ulong owner, ulong objectId, uint dynamicProc,
        float x, float y, float z, float yaw, HousingRoomIdentity room = null) {
        room ??= HousingRoomIdentity.Dorm(owner);
        if (!Editable(live, room) || live.IsInDuel) return Refused();
        HousingResult result = Refused();
        try {
            var saved = WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                var ledger = session.Load<HousingLedger>(HousingLedger.DocumentId(room));
                if (!ValidRoom(session, wizard, room, ledger) || !ledger.TrySlot(objectId, dynamicProc, out var slot)
                    || !ledger.TryUpdate(slot, x, y, z, yaw)) return false;
                result = new(null, ledger.Copy(), ledger.Entries[slot].Copy(), slot);
                return true;
            }, null);
            return saved ? result : Refused();
        }
        catch (Exception ex) { return Failed(live, "move", ex); }
    }

    internal static HousingResult Pickup(Wizard live, ulong owner, ulong objectId, uint dynamicProc, HousingRoomIdentity room = null) {
        room ??= HousingRoomIdentity.Dorm(owner);
        if (!Editable(live, room) || live.IsInDuel) return Refused();
        HousingResult result = Refused();
        try {
            var saved = WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                var ledger = session.Load<HousingLedger>(HousingLedger.DocumentId(room));
                if (!ValidRoom(session, wizard, room, ledger) || !ledger.TrySlot(objectId, dynamicProc, out var slot)
                    || !ledger.Active(slot)) return false;
                var entry = ledger.Entries[slot];
                if (wizard.InventoryBehavior?.InventoryItemIds is not { } ids || ids.Contains(entry.ItemId)
                    || ids.Count >= ServerWizInventoryBehavior.MaxItemsAllowed || OutsideBackpack(wizard, entry.ItemId)) return false;
                var item = session.Load<WizClientObjectItem>(entry.ItemDocumentId);
                if (item is null || item.m_characterId != owner || item.m_globalID != entry.ItemId
                    || item.m_templateID != entry.TemplateId) return false;
                // Serialize before saving: a serialization failure must not strand a successfully picked up item.
                var serializer = new CoreObjectSerializer(versionable: false, behaviors: SerializerFlags.None);
                if (!serializer.Serialize(item, (PropertyFlags)24, out var data) || !ledger.TryPickup(slot)) return false;
                wizard.InventoryBehavior.InventoryItemIds = [.. ids, entry.ItemId];
                result = new(null, ledger.Copy(), entry.Copy(), slot, item, data);
                return true;
            }, _ => {
                // Capacity and duplicate checks use the committed document under the character's write lane.
                // Adding the same item object/id never manufactures a replacement item document.
                if (!live.InventoryBehavior.AddItem(result.Item))
                    throw new InvalidOperationException("Committed housing pickup needs inventory resynchronization.");
            });
            return saved ? result : Refused();
        }
        catch (Exception ex) { return Failed(live, "pick up", ex); }
    }

    internal static bool InBackpack(Wizard wizard, ulong item)
        => wizard.InventoryBehavior?.InventoryItemIds?.Contains(item) == true && !OutsideBackpack(wizard, item);

    internal static bool OutsideBackpack(Wizard wizard, ulong item)
        => wizard.EquipmentBehavior?.EquippedItemIds?.Contains(item) == true
            || wizard.StorageBehavior?.BankItemIds?.Contains(item) == true;

    internal static bool Ordinary(WizClientObjectItem item) {
        if (item is null || item.m_templateID.Full >= (1UL << 28)
            || CoreObjectFactory.GetCoreTemplate(item.m_templateID) is not WizItemTemplate template) return false;
        return HousingRules.OrdinaryFurniture(template.m_adjectiveList, template.m_behaviors?.Select(b => b?.GetType().Name),
            template.m_numPrimaryColors, template.m_numSecondaryColors);
    }

    internal static bool Editable(Wizard live, HousingRoomIdentity room) => live is not null && room is not null
        && live.CharId == room.OwnerId && HouseCatalog.Same(live.Zone, room.Zone)
        && (room.DeedId == 0 ? HousingRules.IsDorm(room.Zone) : HouseCatalog.IsApprovedRoom(room.Zone));

    internal static bool ValidRoom(IDocumentSession session, Wizard wizard, HousingRoomIdentity room, HousingLedger ledger) {
        if (ledger?.OwnerId != room.OwnerId || ledger.DeedId != room.DeedId) return false;
        if (room.DeedId == 0) return HousingRules.IsDorm(room.Zone) && ledger.SecondPackageNumber == 0;
        var house = HouseCollection.Owned(session, wizard, room.OwnerId, room.DeedId);
        return house is not null && HouseCatalog.Same(ledger.Zone, room.Zone)
            && HouseCatalog.TryRoom(house.TemplateId, room.Zone, out var capacity) && ledger.Capacity == capacity
            && ledger.PackageCount == (capacity > HousingRules.PackageSlots ? 2 : 1);
    }

    private static HousingResult Refused() => new("That furniture cannot be changed here.");
    private static HousingResult Failed(Wizard wizard, string action, Exception ex) {
        Logger.Error("Housing {0} for {1} was not completed: {2}", Logger.Args(action, wizard.CharId, ex.Message));
        return new("Your furniture could not be saved. Please leave the dorm and try again.");
    }
}
