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
 *
 * ========================================================================
 * WIZARD STORAGE BEHAVIOR (THE BANK)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the wizard's own bank, the dorm chest (Imlight.Classic.Inventory.Banking has the rules). The item
 * documents stay in the WizardItems collection under the wizard's id, like the backpack's; the character keeps
 * the ids. The client's half is ClientWizStorageBehavior on the player object: m_itemList is the bank,
 * m_bankLimit and m_sharedBankLimit the two sizes. The shared bank is account-wide (SharedBankCollection).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Imlight.Classic.Collections;
using Imlight.Classic.Inventory;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Shared.Behaviors;

[Serializable]
public class ServerWizStorageBehavior : IClientBehaviorProvider<ClientWizStorageBehavior> {

    [JsonIgnore] public bool NoTransfer { get; set; } = false;

    private static readonly Lock s_writeLock = new();

    public List<ulong> BankItemIds { get; set; } = [];

    [JsonIgnore] public CopyOnWriteList<WizClientObjectItem> Items { get; set; } = [];

    /// <summary>Bank slots: [Classic] BankSize when set above 0, else the 2009 100.</summary>
    [JsonIgnore] public static int BankLimit => Classic.ClassicSettings.BankSize is > 0 and var size ? size : Banking.ClassicBankSize;

    /// <summary>Shared bank slots (2009: 100).</summary>
    [JsonIgnore] public static int SharedBankLimit => Banking.ClassicSharedBankSize;

    public bool HasItem(ulong globalId) => Items.Any(item => item.m_globalID == globalId);

    public WizClientObjectItem GetItem(ulong globalId) => Items.FirstOrDefault(item => item.m_globalID == globalId);

    /// <summary>Adds an item to the bank (the caller checked the limit).</summary>
    public bool AddItem(WizClientObjectItem item) {
        ArgumentNullException.ThrowIfNull(item);
        using var writeScope = s_writeLock.EnterScope();
        if (HasItem(item.m_globalID)) {
            return false;
        }

        BankItemIds = [.. BankItemIds.Where(id => id != item.m_globalID), item.m_globalID]; // a save may be serializing the old list
        Items.Add(item);
        return true;
    }

    public bool RemoveItem(ulong globalId, out WizClientObjectItem removed) {
        using var writeScope = s_writeLock.EnterScope();
        removed = GetItem(globalId);
        if (removed is null || !Items.Remove(removed)) {
            removed = null;
            return false;
        }

        BankItemIds = [.. BankItemIds.Where(id => id != globalId)];
        return true;
    }

    public ClientWizStorageBehavior GetClientBehaviorInstance() => new() {
        m_bankLimit = BankLimit,
        m_sharedBankLimit = SharedBankLimit,
        m_itemList = Items.ConvertAll(item =>
            (item as IClientBehaviorProvider<CoreObject>)?.GetClientBehaviorInstance() ?? item),
    };

}
