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
 * BANK COLLECTION
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: moves items between the backpack, the wizard's bank and the account's shared bank, and saves each move
 * in one RavenDB transaction: the character document (backpack and bank id lists), the shared bank ledger
 * (SharedBanks/{AccountId}) and the item document, whose owner id is 0 while the item is shared (so loading or
 * deleting a wizard never touches it). The live wizard changes only after the save.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Inventory;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.WizardData.Collections;

/// <summary>The outcome of a bank move: the refusal, and the item that moved.</summary>
public readonly record struct BankMoveResult(BankRefusal Refusal, WizClientObjectItem Item) {
    public bool Moved => Refusal == BankRefusal.None && Item is not null;
}

public static class BankCollection {

    private static readonly TimeSpan s_indexWait = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<ulong, object> s_accountLocks = new();
    private static IDocumentStore Store => PlayerDatabase.Instance.Store;

    /// <summary>The slots of each place: the backpack's size in effect, the bank and the shared bank.</summary>
    public static BankLimits Limits
        => new(ServerWizInventoryBehavior.MaxItemsAllowed, ServerWizStorageBehavior.BankLimit, ServerWizStorageBehavior.SharedBankLimit);

    /// <summary>The account's shared bank items (a fresh read: another wizard of the account may have changed it).</summary>
    public static List<WizClientObjectItem> LoadShared(ulong accountId) {
        using var session = Store.OpenSession();
        var ledger = session.Load<SharedBankLedger>(SharedBankLedger.DocumentId(accountId));
        if (ledger is null || ledger.Items.Count == 0) {
            return [];
        }

        var docs = session.Load<WizClientObjectItem>(ledger.Items.Select(e => e.DocumentId).Where(id => id.Length > 0));
        var items = new List<WizClientObjectItem>();
        foreach (var entry in ledger.Items) {
            if (entry.DocumentId.Length > 0 && docs.TryGetValue(entry.DocumentId, out var item) && item is not null) {
                items.Add(item);
            }
            else {
                Logger.Warning("Shared bank of account {0}: item {1} has no document.", Logger.Args(accountId, entry.ItemId));
            }
        }

        return items;
    }

    /// <summary>
    /// Moves an item between the backpack, the bank and the shared bank, saves it, and then updates the live wizard.
    /// </summary>
    public static BankMoveResult Move(Wizard wizard, ulong itemId, BankPlace from, BankPlace to) {
        ArgumentNullException.ThrowIfNull(wizard);
        wizard.StorageBehavior ??= new();
        var accountLock = s_accountLocks.GetOrAdd(wizard.AccountId, _ => new object());
        lock (accountLock) {
            var result = new BankMoveResult(BankRefusal.NotFound, null);
            WizClientObjectItem moved = null;
            try {
                WizardCollection.CommitCharacterMutation(wizard.CharId, (session, dbWizard) => {
                    result = Plan(session, wizard, dbWizard, itemId, from, to, out moved);
                    return result.Refusal == BankRefusal.None;
                }, afterCommit: _ => ApplyLive(wizard, moved, from, to));
            }
            catch (Exception ex) {
                Logger.Error("Bank move of item {0} for {1} ({2} -> {3}) was not saved: {4}",
                    Logger.Args(itemId, wizard.CharId, from, to, ex.Message));
                return new BankMoveResult(BankRefusal.NotFound, null);
            }

            return result;
        }
    }

    /// <summary>
    /// Deletes an item kept in the bank, the shared bank or the backpack (the bank window's delete), for good.
    /// </summary>
    public static BankMoveResult Delete(Wizard wizard, ulong itemId, BankPlace from) {
        ArgumentNullException.ThrowIfNull(wizard);
        wizard.StorageBehavior ??= new();
        var accountLock = s_accountLocks.GetOrAdd(wizard.AccountId, _ => new object());
        lock (accountLock) {
            var result = new BankMoveResult(BankRefusal.NotFound, null);
            try {
                WizardCollection.CommitCharacterMutation(wizard.CharId, (session, dbWizard) => {
                    var lists = ListsOf(session, wizard, out var ledger);
                    if (wizard.EquipmentBehavior?.EquippedItemIds?.Contains(itemId) == true || !lists.Of(from).Contains(itemId)) {
                        return false;
                    }

                    var doc = FindDocument(session, itemId, from == BankPlace.SharedBank ? 0 : wizard.CharId, ledger);
                    var item = from switch {
                        BankPlace.Backpack => wizard.InventoryBehavior.GetItem(itemId),
                        BankPlace.Bank => wizard.StorageBehavior.GetItem(itemId),
                        _ => doc,
                    };
                    if (item is null) {
                        return false;
                    }

                    lists.Of(from).Remove(itemId);
                    if (doc is not null) {
                        session.Delete(doc);
                    }

                    SaveLists(session, dbWizard, lists, ledger, wizard.AccountId, null);
                    result = new BankMoveResult(BankRefusal.None, item);
                    return true;
                }, afterCommit: committed => {
                    if (from == BankPlace.Backpack) wizard.InventoryBehavior.RemoveItem(itemId, out _);
                    else if (from == BankPlace.Bank) wizard.StorageBehavior.RemoveItem(itemId, out _);
                });
            }
            catch (Exception ex) {
                Logger.Error("Bank delete of item {0} for {1} was not saved: {2}", Logger.Args(itemId, wizard.CharId, ex.Message));
                return new BankMoveResult(BankRefusal.NotFound, null);
            }

            return result;
        }
    }

    private static BankLists ListsOf(IDocumentSession session, Wizard wizard, out SharedBankLedger ledger) {
        ledger = session.Load<SharedBankLedger>(SharedBankLedger.DocumentId(wizard.AccountId))
            ?? new SharedBankLedger { AccountId = wizard.AccountId };
        return new BankLists(
            [.. wizard.InventoryBehavior.InventoryItemIds ?? []],
            [.. wizard.StorageBehavior.BankItemIds ?? []],
            ledger.ItemIds(),
            wizard.EquipmentBehavior?.EquippedItemIds?.ToHashSet() ?? []);
    }

    private static BankMoveResult Plan(IDocumentSession session, Wizard wizard, Wizard dbWizard, ulong itemId,
        BankPlace from, BankPlace to, out WizClientObjectItem moved) {
        moved = null;
        var lists = ListsOf(session, wizard, out var ledger);

        // The item: the live backpack or bank object, or the shared item's document.
        WizClientObjectItem sharedDoc = from == BankPlace.SharedBank ? FindDocument(session, itemId, 0, ledger) : null;
        var item = from switch {
            BankPlace.Backpack => wizard.InventoryBehavior.GetItem(itemId),
            BankPlace.Bank => wizard.StorageBehavior.GetItem(itemId),
            _ => sharedDoc,
        };
        var adjectives = AdjectivesOf(item);
        var refusal = item is null && from != BankPlace.SharedBank && !wizard.EquipmentBehavior.EquippedItemIds.Contains(itemId)
            ? BankRefusal.NotFound
            : Banking.Move(lists, itemId, from, to, Limits, adjectives);
        if (refusal != BankRefusal.None || item is null) {
            return new BankMoveResult(refusal == BankRefusal.None ? BankRefusal.NotFound : refusal, null);
        }

        // The item document changes hands when it enters or leaves the shared bank.
        string documentId = null;
        if (to == BankPlace.SharedBank) {
            var doc = FindDocument(session, itemId, wizard.CharId, ledger);
            if (doc is null) {
                Logger.Warning("Bank: item {0} of {1} has no document; not shared.", Logger.Args(itemId, wizard.CharId));
                return new BankMoveResult(BankRefusal.NotFound, null);
            }

            doc.m_characterId = 0;
            documentId = session.Advanced.GetDocumentId(doc);
        }
        else if (from == BankPlace.SharedBank) {
            sharedDoc.m_characterId = wizard.CharId;
        }

        SaveLists(session, dbWizard, lists, ledger, wizard.AccountId,
            id => new SharedBankEntry { ItemId = id, DocumentId = documentId ?? "", DepositedBy = wizard.CharId });

        // The live object: a shared item is a copy the session no longer tracks once it is disposed.
        moved = item;
        return new BankMoveResult(BankRefusal.None, item);
    }

    private static void SaveLists(IDocumentSession session, Wizard dbWizard, BankLists lists, SharedBankLedger ledger,
        ulong accountId, Func<ulong, SharedBankEntry> added) {
        dbWizard.InventoryBehavior ??= new ServerWizInventoryBehavior { InventoryItemIds = [] };
        dbWizard.InventoryBehavior.InventoryItemIds = [.. lists.Backpack];
        dbWizard.StorageBehavior ??= new();
        dbWizard.StorageBehavior.BankItemIds = [.. lists.Bank];
        var tracked = session.Advanced.GetDocumentId(ledger) is not null;
        ledger.Reconcile(lists.Shared, added ?? (id => new SharedBankEntry { ItemId = id }));
        if (!tracked && ledger.Items.Count > 0) {
            session.Store(ledger, SharedBankLedger.DocumentId(accountId));
        }
    }

    /// <summary>The item's document: the ledger's id for a shared item, else a query on its owner and global id.</summary>
    private static WizClientObjectItem FindDocument(IDocumentSession session, ulong itemId, ulong ownerId, SharedBankLedger ledger) {
        if (ownerId == 0 && ledger.Find(itemId) is { DocumentId.Length: > 0 } entry) {
            return session.Load<WizClientObjectItem>(entry.DocumentId);
        }

        return session.Query<WizClientObjectItem>(collectionName: WizardItemCollection.CollectionName)
            .Customize(query => query.WaitForNonStaleResults(s_indexWait))
            .FirstOrDefault(x => x.m_characterId == ownerId && x.m_globalID == itemId);
    }

    private static List<string> AdjectivesOf(WizClientObjectItem item) {
        if (item is null || CoreObjectFactory.GetCoreTemplate(item.m_templateID) is not WizItemTemplate template
            || template.m_adjectiveList is null) {
            return [];
        }

        return [.. template.m_adjectiveList.Select(adjective => adjective.ToString())];
    }

    private static void ApplyLive(Wizard wizard, WizClientObjectItem item, BankPlace from, BankPlace to) {
        if (item is null) {
            return;
        }

        switch (from) {
            case BankPlace.Backpack: wizard.InventoryBehavior.RemoveItem(item.m_globalID, out _); break;
            case BankPlace.Bank: wizard.StorageBehavior.RemoveItem(item.m_globalID, out _); break;
        }

        if (to != BankPlace.SharedBank) {
            item.m_characterId = wizard.CharId;
        }

        switch (to) {
            case BankPlace.Backpack: wizard.InventoryBehavior.AddItem(item); break;
            case BankPlace.Bank: wizard.StorageBehavior.AddItem(item); break;
        }
    }

}
