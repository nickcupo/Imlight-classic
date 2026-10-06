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
    private static readonly object s_packageLock = new();
    private static IDocumentSession Open()
        => WizardCollection.TestStoreScope.Value?.Open() ?? PlayerDatabase.Instance.Store.OpenSession();

    internal static HousingLedger Load(ulong owner, bool create = false) {
        if (owner == 0) return null;
        lock (s_packageLock) {
            using var session = Open();
            var ledger = session.Load<HousingLedger>(HousingLedger.DocumentId(owner));
            if (ledger is not null || !create) return ledger;
            session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            var allocator = session.Load<HousingPackageAllocator>(HousingPackageAllocator.DocumentId);
            var freshAllocator = allocator is null;
            allocator ??= new HousingPackageAllocator();
            if (allocator.NextPackageNumber <= 0 || allocator.NextPackageNumber == int.MaxValue)
                throw new InvalidOperationException("Housing package ids are exhausted.");
            ledger = new HousingLedger { OwnerId = owner, PackageNumber = allocator.NextPackageNumber++ };
            if (freshAllocator) session.Store(allocator, HousingPackageAllocator.DocumentId);
            session.Store(ledger, HousingLedger.DocumentId(owner));
            session.SaveChanges();
            return ledger;
        }
    }

    internal static HousingResult Place(Wizard live, ulong owner, ulong itemId, float x, float y, float z, float yaw,
        Func<IDocumentSession, ulong, ulong, WizClientObjectItem> findDocument = null) {
        if (!HousingRules.CanEdit(live?.CharId ?? 0, owner, live?.Zone) || live.IsInDuel
            || !HousingRules.ValidPosition(x, y, z, yaw)) return Refused();
        var item = live.InventoryBehavior?.GetItem(itemId);
        if (!Ordinary(item)) return new("This furniture type is not supported in the dorm yet.");
        HousingResult result = Refused();
        try {
            var saved = WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                var ledger = session.Load<HousingLedger>(HousingLedger.DocumentId(owner));
                if (ledger is null || ledger.OwnerId != owner || !InBackpack(wizard, itemId)) return false;
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
        float x, float y, float z, float yaw) {
        if (!HousingRules.CanEdit(live?.CharId ?? 0, owner, live?.Zone) || live.IsInDuel) return Refused();
        HousingResult result = Refused();
        try {
            var saved = WizardCollection.CommitCharacterMutation(live.CharId, (session, _) => {
                var ledger = session.Load<HousingLedger>(HousingLedger.DocumentId(owner));
                if (ledger is null || ledger.OwnerId != owner || !HousingRules.TrySlot(objectId, dynamicProc, ledger.Entries.Count, out var slot)
                    || !ledger.TryUpdate(slot, x, y, z, yaw)) return false;
                result = new(null, ledger.Copy(), ledger.Entries[slot].Copy(), slot);
                return true;
            }, null);
            return saved ? result : Refused();
        }
        catch (Exception ex) { return Failed(live, "move", ex); }
    }

    internal static HousingResult Pickup(Wizard live, ulong owner, ulong objectId, uint dynamicProc) {
        if (!HousingRules.CanEdit(live?.CharId ?? 0, owner, live?.Zone) || live.IsInDuel) return Refused();
        HousingResult result = Refused();
        try {
            var saved = WizardCollection.CommitCharacterMutation(live.CharId, (session, wizard) => {
                var ledger = session.Load<HousingLedger>(HousingLedger.DocumentId(owner));
                if (ledger is null || ledger.OwnerId != owner || !HousingRules.TrySlot(objectId, dynamicProc, ledger.Entries.Count, out var slot)
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

    private static bool OutsideBackpack(Wizard wizard, ulong item)
        => wizard.EquipmentBehavior?.EquippedItemIds?.Contains(item) == true
            || wizard.StorageBehavior?.BankItemIds?.Contains(item) == true;

    private static bool Ordinary(WizClientObjectItem item) {
        if (item is null || item.m_templateID.Full >= (1UL << 28)
            || CoreObjectFactory.GetCoreTemplate(item.m_templateID) is not WizItemTemplate template) return false;
        return HousingRules.OrdinaryFurniture(template.m_adjectiveList, template.m_behaviors?.Select(b => b?.GetType().Name),
            template.m_numPrimaryColors, template.m_numSecondaryColors);
    }

    private static HousingResult Refused() => new("That furniture cannot be changed here.");
    private static HousingResult Failed(Wizard wizard, string action, Exception ex) {
        Logger.Error("Housing {0} for {1} was not completed: {2}", Logger.Args(action, wizard.CharId, ex.Message));
        return new("Your furniture could not be saved. Please leave the dorm and try again.");
    }
}
