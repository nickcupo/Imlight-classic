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
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Shared.Behaviors;

[Serializable]
public class ServerWizInventoryBehavior : IClientBehaviorProvider<ClientWizInventoryBehavior> {

    [JsonIgnore] public bool NoTransfer { get; set; } = false;

    private static int s_iniMaxItemsAllowed = ConfigurationManager.Settings["Character.MaxInventoryItems"].AsInt();

    // CLASSIC: [Classic] BackpackSize (dashboard switch) overrides Character.MaxInventoryItems when set above 0.
    private static int s_maxItemsAllowed {
        get => Classic.ClassicSettings.BackpackSize is > 0 and var size ? size : s_iniMaxItemsAllowed;
        set => s_iniMaxItemsAllowed = value;
    }
    private static readonly int s_maxJewelsAllowed = ConfigurationManager.Settings["Character.MaxJewelsAllowed"].AsInt();
    private static readonly int s_maxItemsAllowedFallback = 20;
    private static readonly Lock s_writeLock = new(); // CLASSIC: static, so it is never serialized.

    public List<ulong> InventoryItemIds { get; set; }

    // CLASSIC: true when the backpack holds as many items as the ini allows (Character.MaxInventoryItems).
    [JsonIgnore] public bool IsFull => s_maxItemsAllowed > 0 && Items is not null && Items.Count >= s_maxItemsAllowed;

    [JsonIgnore] public CopyOnWriteList<WizClientObjectItem> Items { get; set; } // CLASSIC: other services' actors read it while one of them writes.

    /// <summary>
    /// Adds an item to the player's inventory.
    /// </summary>
    /// <param name="item">The item to be added.</param>
    /// <returns>True if the item was successfully added, false otherwise.</returns>
    public bool AddItem(WizClientObjectItem item) {
        if (s_maxItemsAllowed <= 0) {
            Logger.Warning("Configuration states that {0} is not allowed to have any items in their inventory! "
                + "This is not possible, and causes many game-breaking bugs. Defaulting to {1} items.",
                Logger.Args(nameof(s_maxItemsAllowed), s_maxItemsAllowedFallback));

            s_maxItemsAllowed = s_maxItemsAllowedFallback;
        }

        using var writeScope = s_writeLock.EnterScope(); // CLASSIC: services on other actors add to the same inventory.
        if (Items.Count >= s_maxItemsAllowed) {
            Logger.Debug("Player inventory is full. Cannot add item with global id {0}.", Logger.Args(item.m_globalID));

            return false;
        }

        if (HasItem(item.m_globalID)) {
            Logger.Error("Item with same global id {0} already exists in player inventory.", Logger.Args(item.m_globalID));

            return false;
        }

        InventoryItemIds = [.. InventoryItemIds, item.m_globalID]; // CLASSIC: a save on another actor may be serializing the old list.
        Items.Add(item);
        
        return true;
    }

    /// <summary>
    /// Removes an item from the player's inventory based on its unique identifier.
    /// </summary>
    /// <param name="itemId">The unique identifier of the item to be removed.</param>
    /// <returns><c>true</c> if the item was successfully removed; otherwise, <c>false</c>.</returns>
    public bool RemoveItem(ulong itemId, out WizClientObjectItem removedItem) {
        removedItem = null;

        // Get the actual item from the inventory.
        removedItem = Items.Find(i => i.m_globalID == itemId);
        if (removedItem is null) {
            Logger.Debug("Tried to remove item with global id {0} that does not exist in player inventory.",
                Logger.Args(itemId));

            return false;
        }

        return RemoveItem(removedItem);
    }

    /// <summary>
    /// Removes an item from the player's inventory.
    /// </summary>
    /// <param name="item">The item to be removed.</param>
    /// <returns><c>true</c> if the item was successfully removed; otherwise, <c>false</c>.</returns>
    public bool RemoveItem(WizClientObjectItem item) {
        if (item is null) {
            throw new NullReferenceException("Item cannot be null.");
        }
        using var writeScope = s_writeLock.EnterScope(); // CLASSIC
        if (!Items.Remove(item)) {
            Logger.Debug("Tried to remove item with global id {0} that does not exist in player inventory.",
                Logger.Args(item.m_globalID));

            return false;
        }

        var remainingIds = new List<ulong>(InventoryItemIds); // CLASSIC: as in AddItem, a save may be serializing the old list.
        if (!remainingIds.Remove(item.m_globalID)) {
            Logger.Debug("Tried to remove item with global id {0} that does not exist in player inventory.",
                Logger.Args(item.m_globalID));

            return false;
        }
        InventoryItemIds = remainingIds; // CLASSIC

        return true;
    }

    /// <summary>
    /// Checks if the inventory contains an item with the specified global ID.
    /// </summary>
    /// <param name="globalId">The global ID of the item to check.</param>
    /// <returns>True if the inventory contains an item with the specified global ID, otherwise false.</returns>
    public bool HasItem(ulong globalId) 
        => Items.Any(item => item.m_globalID == globalId);

    /// <summary>
    /// Represents an item in the wizard's inventory.
    /// </summary>
    public WizClientObjectItem GetItem(ulong globalId) => Items.FirstOrDefault(item => item.m_globalID == globalId);

    public ClientWizInventoryBehavior GetClientBehaviorInstance() => new() {
        m_numItemsAllowed = s_maxItemsAllowed,
        m_numJewelsAllowed = s_maxJewelsAllowed,
        m_itemList = Items.ConvertAll(item =>
            (item as IClientBehaviorProvider<CoreObject>)?.GetClientBehaviorInstance() ?? item)

    };

}
