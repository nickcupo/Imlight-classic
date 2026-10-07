using System;
using System.Collections.Generic;
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
            session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            var capacity = HousingRules.DormCapacity;
            if (room.DeedId != 0) {
                var wizard = HouseCollection.LoadWizard(session, owner);
                var house = HouseCollection.Owned(session, wizard, owner, room.DeedId, protectReadOnly: true);
                if (house is null || !HouseCatalog.TryRoom(house.TemplateId, room.Zone, out capacity)) return null;
                // CLASSIC: creating a room ledger cannot rewrite its read-only character authority.
                session.Advanced.IgnoreChangesFor(wizard);
            }
            else if (!HousingRules.IsDorm(room.Zone)) return null;
            var ledger = session.Load<HousingLedger>(HousingLedger.DocumentId(room));
            if (ledger is not null) {
                // CLASSIC: reconcile the allowance without touching entries, floats, cache slots or
                // versions. An overfull legacy dorm remains editable/pickable; only new placement stops.
                if (room.DeedId == 0 && ledger.OwnerId == owner && ledger.DeedId == 0
                    && ledger.SecondPackageNumber == 0 && ledger.Capacity != capacity) {
                    var previous = ledger.Capacity;
                    ledger.Capacity = capacity;
                    session.SaveChanges();
                    Logger.Information("Classic dorm {0} allowance reconciled {1}->{2}; retained {3} furniture entries.",
                        Logger.Args(owner, previous, capacity, ledger.Entries.Count));
                }
                return ledger;
            }
            if (!create) return null;
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
            WizardInventoryTransactions.ProtectUnmodifiedRows(session);
            session.SaveChanges();
            return ledger;
        }
    }

    internal static HousingResult Place(Wizard live, ulong owner, ulong itemId, float x, float y, float z, float yaw,
        Func<IDocumentSession, ulong, ulong, WizClientObjectItem> findDocument = null, HousingRoomIdentity room = null,
        Func<HousingResult, bool> preparePublication = null, Action<HousingResult> afterCommit = null) {
        room ??= HousingRoomIdentity.Dorm(owner);
        if (!Editable(live, room) || WizardCollection.IsInventorySnapshotUncertain(live) || live.IsInDuel
            || !HousingRules.ValidPosition(x, y, z, yaw)) return Refused();
        var item = live.InventoryBehavior?.GetItem(itemId);
        if (!Ordinary(item)) return new("This furniture type is not supported in the dorm yet.");
        HousingResult result = Refused();
        List<WizClientObjectItem> backpack = [];
        try {
            var saved = WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                if (WizardCollection.IsInventorySnapshotUncertain(live) || !Editable(live, room) || live.IsInDuel
                    || wizard.CharId != live.CharId) return false;
                var ledger = session.Load<HousingLedger>(HousingLedger.DocumentId(room));
                if (!ValidRoom(session, wizard, room, ledger)
                    || !HouseCollection.TryReadHoldings(session, wizard, out _, out backpack)) return false;
                var doc = backpack.SingleOrDefault(candidate => candidate.m_globalID.Full == itemId);
                if (doc is null || doc.m_characterId != owner || doc.m_globalID != itemId || doc.m_templateID != item.m_templateID) return false;
                if (!Ordinary(doc) || findDocument is not null && !ReferenceEquals(findDocument(session, itemId, owner), doc)) return false;
                var entry = new HousingEntry {
                    ItemId = itemId, ItemDocumentId = session.Advanced.GetDocumentId(doc), TemplateId = (uint)doc.m_templateID.Full,
                    X = x, Y = y, Z = z, Yaw = yaw,
                };
                if (!ledger.TryPlace(entry, out var slot)) return false;
                wizard.InventoryBehavior.InventoryItemIds = wizard.InventoryBehavior.InventoryItemIds.Where(id => id != itemId).ToList();
                backpack.Remove(doc);
                result = new(null, ledger.Copy(), entry.Copy(), slot, doc);
                if (!PreparePublication(result, preparePublication)) return false;
                WizardInventoryTransactions.ProtectUnmodifiedRows(session);
                return true;
            }, committed => {
                WizardInventoryTransactions.PublishCommittedBackpack(live, committed, backpack);
                afterCommit?.Invoke(result);
            }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
            return saved ? result : Refused();
        }
        catch (Exception ex) { return Failed(live, "place", ex); }
    }

    internal static HousingResult Update(Wizard live, ulong owner, ulong objectId, uint dynamicProc,
        float x, float y, float z, float yaw, HousingRoomIdentity room = null,
        Func<HousingResult, bool> preparePublication = null, Action<HousingResult> afterCommit = null) {
        room ??= HousingRoomIdentity.Dorm(owner);
        if (!Editable(live, room) || WizardCollection.IsInventorySnapshotUncertain(live) || live.IsInDuel) return Refused();
        HousingResult result = Refused();
        try {
            var saved = WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                if (WizardCollection.IsInventorySnapshotUncertain(live) || !Editable(live, room) || live.IsInDuel
                    || wizard.CharId != live.CharId) return false;
                var ledger = session.Load<HousingLedger>(HousingLedger.DocumentId(room));
                if (!ValidRoom(session, wizard, room, ledger) || !ledger.TrySlot(objectId, dynamicProc, out var slot)
                    || !ledger.TryUpdate(slot, x, y, z, yaw)) return false;
                result = new(null, ledger.Copy(), ledger.Entries[slot].Copy(), slot);
                if (!PreparePublication(result, preparePublication)) return false;
                WizardInventoryTransactions.ProtectUnmodifiedRows(session);
                // CLASSIC: moving furniture writes only the room ledger. Native deserialization
                // can normalize a tracked wizard even though this operation changed none of it.
                session.Advanced.IgnoreChangesFor(wizard);
                return true;
            }, _ => afterCommit?.Invoke(result), onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
            return saved ? result : Refused();
        }
        catch (Exception ex) { return Failed(live, "move", ex); }
    }

    internal static HousingResult Pickup(Wizard live, ulong owner, ulong objectId, uint dynamicProc, HousingRoomIdentity room = null,
        Func<HousingResult, bool> preparePublication = null, Action<HousingResult> afterCommit = null) {
        room ??= HousingRoomIdentity.Dorm(owner);
        if (!Editable(live, room) || WizardCollection.IsInventorySnapshotUncertain(live) || live.IsInDuel) return Refused();
        HousingResult result = Refused();
        List<WizClientObjectItem> backpack = [];
        try {
            var saved = WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                if (WizardCollection.IsInventorySnapshotUncertain(live) || !Editable(live, room) || live.IsInDuel
                    || wizard.CharId != live.CharId) return false;
                var ledger = session.Load<HousingLedger>(HousingLedger.DocumentId(room));
                if (!ValidRoom(session, wizard, room, ledger) || !ledger.TrySlot(objectId, dynamicProc, out var slot)
                    || !ledger.Active(slot)) return false;
                var entry = ledger.Entries[slot];
                if (!HouseCollection.TryReadHoldings(session, wizard, out var rows, out backpack)
                    || wizard.InventoryBehavior?.InventoryItemIds is not { } ids || ids.Contains(entry.ItemId)
                    || ids.Count >= ServerWizInventoryBehavior.MaxItemsAllowed || OutsideBackpack(wizard, entry.ItemId)) return false;
                var item = session.Load<WizClientObjectItem>(entry.ItemDocumentId);
                if (item is null || item.m_characterId != owner || item.m_globalID != entry.ItemId
                    || item.m_templateID != entry.TemplateId || !Ordinary(item)) return false;
                var originals = rows.Where(candidate => candidate.m_globalID.Full == entry.ItemId).ToArray();
                if (originals.Length != 1 || !ReferenceEquals(originals[0], item)) return false;
                WizardInventoryTransactions.CaptureReadRows(session, [item]);
                // Serialize before saving: a serialization failure must not strand a successfully picked up item.
                var serializer = new CoreObjectSerializer(versionable: false, behaviors: SerializerFlags.None);
                if (!serializer.Serialize(item, (PropertyFlags)24, out var data) || !ledger.TryPickup(slot)) return false;
                wizard.InventoryBehavior.InventoryItemIds = [.. ids, entry.ItemId];
                backpack.Add(item);
                result = new(null, ledger.Copy(), entry.Copy(), slot, item, data);
                if (!PreparePublication(result, preparePublication)) return false;
                WizardInventoryTransactions.ProtectUnmodifiedRows(session);
                return true;
            }, committed => {
                WizardInventoryTransactions.PublishCommittedBackpack(live, committed, backpack);
                afterCommit?.Invoke(result);
            }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
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
        if (room.DeedId == 0) {
            if (!HousingRules.IsDorm(room.Zone) || ledger.SecondPackageNumber != 0) return false;
            // Direct commands must honor the current allowance even if no room load has reconciled it.
            // The character transaction saves this value only together with a successful mutation.
            ledger.Capacity = HousingRules.DormCapacity;
            return true;
        }
        var house = HouseCollection.Owned(session, wizard, room.OwnerId, room.DeedId, protectReadOnly: true);
        return house is not null && HouseCatalog.Same(ledger.Zone, room.Zone)
            && HouseCatalog.TryRoom(house.TemplateId, room.Zone, out var capacity) && ledger.Capacity == capacity
            && ledger.PackageCount == (capacity > HousingRules.PackageSlots ? 2 : 1);
    }

    private static HousingResult Refused() => new("That furniture cannot be changed here.");
    private static bool PreparePublication(HousingResult result, Func<HousingResult, bool> prepare) {
        // CLASSIC: preparation failures are known refusals before any write, not uncertain save outcomes.
        try { return prepare?.Invoke(result) ?? true; }
        catch { return false; }
    }
    private static HousingResult Failed(Wizard wizard, string action, Exception ex) {
        Logger.Error("Housing {0} for {1} was not completed: {2}", Logger.Args(action, wizard.CharId, ex.Message));
        return new(WizardCollection.IsInventorySnapshotUncertain(wizard)
            ? "Your furniture save needs an authoritative reload. Please reconnect."
            : "Your furniture could not be saved. Please try again.");
    }
}
