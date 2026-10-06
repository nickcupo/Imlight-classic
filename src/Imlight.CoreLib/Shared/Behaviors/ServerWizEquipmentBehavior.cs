/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Imlight.Classic.Collections;
using Imlight.Common;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.WizardData.Models.Player;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;

namespace Imlight.CoreLib.Shared.Behaviors;

[Serializable]
public class ServerWizEquipmentBehavior : IClientBehaviorProvider<ClientWizEquipmentBehavior> {

    [JsonIgnore] public bool NoTransfer { get; set; } = false;

    // CLASSIC: SlotList and EquippedItemIds are replaced, never changed in place, under s_writeLock.
    // An equip on the player's actor, a starter-kit or reward equip on another actor and a RavenDB save
    // serializing these lists (UpdateCharacterItems) used to meet on one List<T>; the save threw or wrote
    // a torn list, and two equips could both pass the "slot free" check.
    private static readonly Lock s_writeLock = new(); // CLASSIC: static, so it is never serialized.

    public List<EquipmentSlot> SlotList;
    public List<ulong> EquippedItemIds;

    [JsonIgnore] public CopyOnWriteList<WizClientObjectItem> EquippedItems; // CLASSIC: readers on other actors never see a write in progress.

    public bool EquipItem(WizClientObjectItem item, EquipmentSlotType slotType) {
        var itemId = item.m_globalID;

        using var writeScope = s_writeLock.EnterScope(); // CLASSIC

        // Prerequisite checks.
        if (HasItemEquipped(itemId)) {
            return false;
        }

        var existingItem = GetItemInSlot(slotType);
        if (existingItem is not null) {
            EquippedItemIds = [.. EquippedItemIds.Where(id => id != existingItem.m_globalID)]; // CLASSIC
            EquippedItems.Remove(existingItem);
        }

        // Finally, update the slot.
        UpdateEquipmentSlot(slotType, item.m_debugName, itemId);
        EquippedItemIds = [.. EquippedItemIds ?? [], itemId]; // CLASSIC
        EquippedItems.Add(item);
        
        return true;
    }

    public void ForceEquipItem(WizClientObjectItem item) {
        var itemId = item.m_globalID;
        using var writeScope = s_writeLock.EnterScope(); // CLASSIC
        if (HasItemEquipped(itemId)) {
            return; // CLASSIC: a second force-equip of the same item used to duplicate it.
        }
        EquippedItemIds = [.. EquippedItemIds ?? [], itemId]; // CLASSIC
        EquippedItems.Add(item);
    }

    public bool UnequipItem(ulong itemId) {
        using var writeScope = s_writeLock.EnterScope(); // CLASSIC

        // Prerequisite checks.
        if (!HasItemEquipped(itemId)) {
            return false;
        }

        var item = EquippedItems.FirstOrDefault(item => item.m_globalID == itemId);
        var slot = SlotList.Find(eSlot => eSlot.ItemId == itemId);

        // Finally, update the slot.
        if (slot is not null) { // CLASSIC: a force-equipped item has no slot.
            ClearEquipmentSlot(slot.SlotType);
        }
        EquippedItemIds = [.. EquippedItemIds.Where(id => id != itemId)]; // CLASSIC
        EquippedItems.Remove(item);

        return true;
    }

    public bool HasItemEquipped(ulong itemId) => EquippedItems.Any(item => item.m_globalID == itemId);

    // CLASSIC: Elixir is a multi-item native slot; ordinary gear's replace/clear-by-type rules do not apply.
    internal bool AppendElixirItem(WizClientObjectItem item) {
        using var writeScope = s_writeLock.EnterScope();
        if (item is null || HasItemEquipped(item.m_globalID)
            || SlotList.Count(s => s.SlotType == EquipmentSlotType.Elixir) >= Classic.Elixirs.ElixirRules.MaximumActive) return false;
        EquippedItemIds = [.. EquippedItemIds, (ulong)item.m_globalID];
        EquippedItems.Add(item);
        SlotList = [.. SlotList, new EquipmentSlot {
            SlotType = EquipmentSlotType.Elixir, ItemId = item.m_globalID,
            ItemName = item.m_debugName, EquippedSince = DateTime.UtcNow,
        }];
        return true;
    }

    internal bool RemoveElixirItem(ulong itemId) {
        using var writeScope = s_writeLock.EnterScope();
        if (!SlotList.Any(s => s.SlotType == EquipmentSlotType.Elixir && s.ItemId == itemId)) return false;
        var item = EquippedItems.FirstOrDefault(i => i.m_globalID == itemId);
        SlotList = SlotList.Where(s => !(s.SlotType == EquipmentSlotType.Elixir && s.ItemId == itemId)).ToList();
        EquippedItemIds = EquippedItemIds.Where(id => id != itemId).ToList();
        if (item is not null) EquippedItems.Remove(item);
        return true;
    }

    // CLASSIC: publish the committed multi-slot state without replacing concurrently read gear lists.
    internal void PublishElixirItems(IReadOnlyList<WizClientObjectItem> items) {
        using var writeScope = s_writeLock.EnterScope();
        var previous = SlotList.Where(s => s.SlotType == EquipmentSlotType.Elixir).ToDictionary(s => (ulong)s.ItemId);
        var oldIds = previous.Keys.ToHashSet();
        // CLASSIC: Raven's original holds durable remaining seconds, not this attached
        // session's effect-publication state. Preserve that transient flag only for the
        // same owned original/template while its validated timer counts down. Otherwise
        // every one-second checkpoint replaces true with persisted false and re-enables
        // the native elixir over and over despite the existing effects already being live.
        foreach (var item in items) {
            var old = EquippedItems.FirstOrDefault(i => i.m_globalID == item.m_globalID
                && i.m_characterId == item.m_characterId && i.m_templateID == item.m_templateID);
            var oldTimer = old?.m_inactiveBehaviors?.OfType<ClientElixirBehavior>().SingleOrDefault();
            var newTimer = item.m_inactiveBehaviors?.OfType<ClientElixirBehavior>().SingleOrDefault();
            if (oldIds.Contains(item.m_globalID) && oldTimer is { m_expireTime: > 0 }
                && newTimer is { m_expireTime: > 0 } && newTimer.m_expireTime <= oldTimer.m_expireTime)
                newTimer.m_statsApplied = oldTimer.m_statsApplied;
        }
        foreach (var item in EquippedItems.Where(i => oldIds.Contains(i.m_globalID)).ToArray()) EquippedItems.Remove(item);
        EquippedItemIds = [.. EquippedItemIds.Where(id => !oldIds.Contains(id)), .. items.Select(i => (ulong)i.m_globalID)];
        SlotList = [.. SlotList.Where(s => s.SlotType != EquipmentSlotType.Elixir), .. items.Select(item =>
            previous.TryGetValue(item.m_globalID, out var old) ? old : new EquipmentSlot {
                SlotType = EquipmentSlotType.Elixir, ItemId = item.m_globalID,
                ItemName = item.m_debugName, EquippedSince = DateTime.UtcNow,
            })];
        foreach (var item in items) EquippedItems.Add(item);
    }

    public bool SlotInUse(string slotName, out byte index) {
        index = 255;

        // Cast the slot name to an enum value.
        if (!Enum.TryParse(typeof(EquipmentSlotType), slotName, true, out var slot)) {
            Logger.Error("Could not parse slot name {0} to an enum value.", Logger.Args(slotName));

            return false;
        }

        return SlotInUse((EquipmentSlotType) slot, out index);
    }

    public bool SlotInUse(EquipmentSlotType slotType, out byte index) {
        index = 255;

        var slotIndex = SlotList.FindIndex(item => item.SlotType == slotType);
        if (slotIndex == -1) {
            return false;
        }

        index = (byte) slotIndex;

        return true;
    }

    public WizClientObjectItem GetItem(ulong globalId) 
        => EquippedItems.FirstOrDefault(item => item.m_globalID == globalId);

    public WizClientObjectItem GetItemInSlot(string slotName) {
        // Cast the slot name to an enum value.
        if (!Enum.TryParse(typeof(EquipmentSlotType), slotName, true, out var slot)) {
            Logger.Error("Could not parse slot name {0} to an enum value.", Logger.Args(slotName));

            return null;
        }

        return GetItemInSlot((EquipmentSlotType) slot);
    }

    public WizClientObjectItem GetItemInSlot(EquipmentSlotType slotType) {
        var slot = SlotList.Find(item => item.SlotType == slotType); // CLASSIC: one read of the list.
        if (slot is null) {
            return null;
        }

        return EquippedItems.FirstOrDefault(item => item.m_globalID == slot.ItemId);
    }

    public WizItemTemplate GetTemplateInSlot(string slotName) {
        // Cast the slot name to an enum value.
        if (!Enum.TryParse(typeof(EquipmentSlotType), slotName, true, out var slot)) {
            Logger.Error("Could not parse slot name {0} to an enum value.", Logger.Args(slotName));

            return null;
        }

        return GetTemplateInSlot((EquipmentSlotType) slot);
    }

    public WizItemTemplate GetTemplateInSlot(EquipmentSlotType slotType) {
        var slot = SlotList.Find(item => item.SlotType == slotType); // CLASSIC: one read of the list.
        if (slot is null) {
            return null;
        }

        return ItemHelper.GetItemTemplate(EquippedItems.FirstOrDefault(item => item.m_globalID == slot.ItemId));
    }

    public byte GetSlotOfItem(ulong itemId) {
        var slotIndex = SlotList.FindIndex(item => item.ItemId == itemId);
        if (slotIndex == -1) {
            return 255;
        }

        return (byte) slotIndex;
    }

    private void UpdateEquipmentSlot(EquipmentSlotType slotType, string itemName,  ulong newItemId) {
        // Find the slot in the list. If it does, remove it.
        ClearEquipmentSlot(slotType);

        // Create a new slot and add it to the list.
        var newSlot = new EquipmentSlot {
            SlotType = slotType,
            ItemName = itemName,
            ItemId = (GID) newItemId,
            EquippedSince = DateTime.Now,
        };
        SlotList = [.. SlotList ?? [], newSlot]; // CLASSIC: replaced, as a save may be serializing the old list.
    }

    private void ClearEquipmentSlot(EquipmentSlotType slotType) {
        // Find the slot in the list. If it does, remove it.
        if (SlotList is null || !SlotList.Exists(eSlot => eSlot.SlotType == slotType)) {
            return;
        }
        SlotList = [.. SlotList.Where(eSlot => eSlot.SlotType != slotType)]; // CLASSIC: replaced, not changed in place.
    }

    public ulong GetEquippedPetId() {
        var petSlot = SlotList?.FirstOrDefault(s => s.SlotType == EquipmentSlotType.Pet);
        return petSlot?.ItemId ?? 0;
    }

    public ClientWizEquipmentBehavior GetClientBehaviorInstance() => new() {
        m_equipmentSets = new List<EquipmentSet>(),
        m_slotList = SlotList?.Select(slot => slot.GetClientTypeAlternative()).ToList(),
        m_itemList = EquippedItems?.ConvertAll(item => item as CoreObject),
        m_publicItemList = CharacterHelper.GetEquipmentList(this).m_infoList,
    };
    
}
