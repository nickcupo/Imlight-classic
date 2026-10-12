// CLASSIC: an October pet swap is one fresh, acknowledged reference write. Live bags, owner state and
// passive receipts stay untouched until SaveChanges succeeds; lost ACKs require an authoritative reload.
using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Pet;

internal static class ClassicPetEquipmentTransactions {
    internal static bool Equip(Wizard live, ulong id, out List<GameEffectBase> added, out List<GameEffectBase> removed)
        => Transfer(live, id, true, out added, out removed);

    internal static bool Unequip(Wizard live, ulong id, out List<GameEffectBase> removed)
        => Transfer(live, id, false, out _, out removed);

    private static bool Transfer(Wizard live, ulong id, bool equip,
        out List<GameEffectBase> added, out List<GameEffectBase> removed) {
        added = null; removed = null;
        if (live is null || id == 0 || WizardCollection.IsInventorySnapshotUncertain(live)) return false;
        WizClientObjectItem selected = null, replaced = null, selectedAlias = null, replacedAlias = null;
        WizItemTemplate selectedTemplate = null, replacedTemplate = null;
        PetTalentReceipt prepared = null;
        List<GameEffectBase> admitted = null, retired = null;
        var committed = WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
            if (WizardCollection.IsInventorySnapshotUncertain(live) || !SameReferences(live, saved)
                || saved.PetOwnerBehavior is null || live.PetOwnerBehavior is null
                || !WizardInventoryTransactions.TryReadOwnedItem(session, saved, id, out selected)
                || !WizardInventoryTransactions.CanPublishOwnedItem(live, saved, selected)
                || PetProgress.Behavior(selected) is not { m_level: > 0 }
                || (selectedTemplate = ItemHelper.GetItemTemplate(selected)) is null
                || ItemHelper.GetItemSlot(selectedTemplate)?.SlotType != EquipmentSlotType.Pet) return false;
            selectedAlias = equip ? live.InventoryBehavior.GetItem(id) : live.EquipmentBehavior.GetItem(id);
            var oldId = saved.EquipmentBehavior.GetEquippedPetId();
            if (equip) {
                if (saved.InventoryBehavior.InventoryItemIds.Count(itemId => itemId == id) != 1
                    || live.InventoryBehavior.Items.Count(item => item is not null && item.m_globalID.Full == id) != 1
                    || live.EquipmentBehavior.EquippedItems.Any(item => item is not null && item.m_globalID.Full == id)
                    || live.StorageBehavior?.Items?.Any(item => item is not null && item.m_globalID.Full == id) == true) return false;
                if (oldId != 0) {
                    if (!PetTalentRuntime.HasExactEquippedPet(live, oldId)
                        || !WizardInventoryTransactions.TryReadOwnedItem(session, saved, oldId, out replaced)
                        || !WizardInventoryTransactions.CanPublishOwnedItem(live, saved, replaced)) return false;
                    replacedAlias = live.EquipmentBehavior.GetItem(oldId);
                    replacedTemplate = ItemHelper.GetItemTemplate(replaced);
                    if (replacedTemplate is null || ItemHelper.GetItemSlot(replacedTemplate)?.SlotType != EquipmentSlotType.Pet) return false;
                }
                // Native loading/card construction is detached and may refuse before touching a durable or live reference.
                prepared = PetTalentRuntime.Prepare(live, selected);
                saved.InventoryBehavior.InventoryItemIds = [..saved.InventoryBehavior.InventoryItemIds.Where(itemId => itemId != id),
                    ..(oldId == 0 ? Array.Empty<ulong>() : new[] { oldId })];
                saved.EquipmentBehavior.EquippedItemIds = [..saved.EquipmentBehavior.EquippedItemIds.Where(itemId => itemId != oldId), id];
                saved.EquipmentBehavior.SlotList = [..saved.EquipmentBehavior.SlotList.Where(slot => slot.SlotType != EquipmentSlotType.Pet),
                    new EquipmentSlot { SlotType = EquipmentSlotType.Pet, ItemId = id,
                        ItemName = selected.m_debugName, EquippedSince = DateTime.UtcNow }];
                saved.PetOwnerBehavior.EquipPet(selectedTemplate, selected);
            }
            else {
                if (oldId != id || !PetTalentRuntime.HasExactEquippedPet(live, id)
                    || saved.InventoryBehavior.InventoryItemIds.Count >= ServerWizInventoryBehavior.MaxItemsAllowed) return false;
                saved.InventoryBehavior.InventoryItemIds = [..saved.InventoryBehavior.InventoryItemIds, id];
                saved.EquipmentBehavior.EquippedItemIds = [..saved.EquipmentBehavior.EquippedItemIds.Where(itemId => itemId != id)];
                saved.EquipmentBehavior.SlotList = [..saved.EquipmentBehavior.SlotList.Where(slot => slot.SlotType != EquipmentSlotType.Pet)];
                saved.PetOwnerBehavior.UnequipPet();
            }
            // This transaction moves references only. Loaded native rows must never be accidentally rewritten.
            WizardInventoryTransactions.ProtectUnmodifiedRows(session);
            return true;
        }, saved => {
            var movedIds = new HashSet<ulong> { id };
            if (replaced is not null) movedIds.Add(replaced.m_globalID.Full);
            var backpack = live.InventoryBehavior.Items.Where(item => !movedIds.Contains(item.m_globalID.Full)).ToList();
            var equipment = live.EquipmentBehavior.EquippedItems.Where(item => !movedIds.Contains(item.m_globalID.Full)).ToList();
            if (equip) { equipment.Add(selectedAlias); if (replacedAlias is not null) backpack.Add(replacedAlias); }
            else backpack.Add(selectedAlias);
            live.InventoryBehavior.InventoryItemIds = [..saved.InventoryBehavior.InventoryItemIds];
            live.InventoryBehavior.Items = [..backpack];
            live.EquipmentBehavior.EquippedItemIds = [..saved.EquipmentBehavior.EquippedItemIds];
            live.EquipmentBehavior.SlotList = [..saved.EquipmentBehavior.SlotList];
            live.EquipmentBehavior.EquippedItems = [..equipment];
            WizardInventoryTransactions.PublishCommittedOwnedItem(live, saved, selected);
            if (replaced is not null) WizardInventoryTransactions.PublishCommittedOwnedItem(live, saved, replaced);
            if (equip) live.PetOwnerBehavior.EquipPet(selectedTemplate, selectedAlias);
            else live.PetOwnerBehavior.UnequipPet();
            retired = [];
            var old = equip ? replaced : selected;
            var oldTemplate = equip ? replacedTemplate : selectedTemplate;
            if (old is not null) {
                retired.AddRange(CharacterEffectHelper.RemoveEffectsFromWizard(live, oldTemplate));
                retired.AddRange(PetTalentRuntime.Remove(live, old.m_globalID.Full));
            }
            admitted = equip ? CharacterEffectHelper.AddEffectsToWizard(live, selectedTemplate) : [];
            if (equip) admitted.AddRange(PetTalentRuntime.Publish(live, selectedAlias, prepared));
        }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        if (committed) { added = admitted; removed = retired; }
        return committed;
    }

    private static bool SameReferences(Wizard live, Wizard saved) {
        if (live.CharId != saved.CharId || live.InventoryBehavior?.InventoryItemIds is not { } bag
            || saved.InventoryBehavior?.InventoryItemIds is not { } savedBag
            || live.EquipmentBehavior?.EquippedItemIds is not { } equipped
            || saved.EquipmentBehavior?.EquippedItemIds is not { } savedEquipped
            || live.EquipmentBehavior.SlotList is not { } slots || saved.EquipmentBehavior.SlotList is not { } savedSlots
            || slots.Count(slot => slot.SlotType == EquipmentSlotType.Pet) > 1
            || savedSlots.Count(slot => slot.SlotType == EquipmentSlotType.Pet) > 1) return false;
        return bag.Order().SequenceEqual(savedBag.Order()) && equipped.Order().SequenceEqual(savedEquipped.Order())
            && slots.OrderBy(slot => slot.SlotType).Select(slot => (slot.SlotType, slot.ItemId.Full))
                .SequenceEqual(savedSlots.OrderBy(slot => slot.SlotType).Select(slot => (slot.SlotType, slot.ItemId.Full)))
            && (live.StorageBehavior?.BankItemIds ?? []).Order().SequenceEqual((saved.StorageBehavior?.BankItemIds ?? []).Order());
    }
}
