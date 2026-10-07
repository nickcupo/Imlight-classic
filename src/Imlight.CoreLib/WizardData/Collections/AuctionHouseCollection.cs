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

using System.Linq;
using System;
using System.Collections.Generic;
using Raven.Client.Documents;
using Imlight.CoreLib.WizardData.Databases;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.WizardData.Collections;

internal class AuctionHouseCollection {
    
    public const string CollectionName = "AuctionHouse";

    private static readonly IDocumentStore s_store;
    private static bool s_isInitialized;
    private static List<AuctionHouseEntry> s_entries;

    /// <summary>
    /// CLASSIC: one lock for the Bazaar's stock. Every player's session actor and the server's restock timer change the
    /// same entries; a buy or sale holds it from reading the entry to saving it.
    /// </summary>
    internal static readonly object Lock = new();

    static AuctionHouseCollection() {
        s_store = PlayerDatabase.Instance.Store;
    }

    /// <summary>
    /// Retrieves all Auction House entries available.
    /// </summary>
    /// <returns>A list of all available Auction House entries, or null if none found.</returns>
    public static List<AuctionHouseEntry> GetAllAuctionHouseEntries() {
        lock (Lock) {
            return [.. GetAllAuctionHouseEntriesUnlocked()]; // a snapshot: others may change the stock meanwhile
        }
    }

    private static List<AuctionHouseEntry> GetAllAuctionHouseEntriesUnlocked() {
        if (s_isInitialized) {
            return s_entries;
        }

        using var session = s_store.OpenSession();

        // Retrieve all Auction House entries.
        s_entries = [.. session.Query<AuctionHouseEntry>(collectionName: CollectionName)];

        if (s_entries is null) {
            s_entries = [];
        }

        s_isInitialized = true;

        return s_entries;
    }

    /// <summary>
    /// Retrieves an Auction House entry by template ID.
    /// </summary>
    /// <param name="templateID">The template ID of an object.</param>
    /// <returns>The Auction House entry, or null if not found.</returns>
    public static AuctionHouseEntry GetAuctionHouseEntry(ulong templateID) {
        lock (Lock) {
            return GetAuctionHouseEntryUnlocked(templateID);
        }
    }

    private static AuctionHouseEntry GetAuctionHouseEntryUnlocked(ulong templateID) {
        if (!s_isInitialized) {
            GetAllAuctionHouseEntriesUnlocked();
        }

        if (s_entries is null) {
            return null;
        }

        var auctionHouseEntry = s_entries.FirstOrDefault(x => x.m_templateID == templateID);

        return auctionHouseEntry;
    }

    /// <summary>
    /// Adds an Auction House entry to the collection.
    /// </summary>
    /// <param name="entry">The Auction House entry to add.</param>
    public static void AddAuctionHouseEntry(AuctionHouseEntry entry) {
        lock (Lock) {
            AddAuctionHouseEntryUnlocked(entry);
        }
    }

    private static void AddAuctionHouseEntryUnlocked(AuctionHouseEntry entry) {
        if (!s_isInitialized) {
            GetAllAuctionHouseEntriesUnlocked();
        }

        using var session = s_store.OpenSession();

        session.Store(entry);
        var metaData = session.Advanced.GetMetadataFor(entry);
        metaData[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;

        s_entries.Add(entry);

        session.SaveChanges();
    }


    /// <summary>
    /// Removes an Auction House entry record from the collection based on the specified template ID.
    /// </summary>
    /// <param name="templateID">The template ID of the object to remove the entry for.</param>
    /// <returns>True if the Auction House entry was successfully removed, false otherwise.</returns>
    public static bool RemoveAuctionHouseEntry(ulong templateID) {
        lock (Lock) {
            return RemoveAuctionHouseEntryUnlocked(templateID);
        }
    }

    private static bool RemoveAuctionHouseEntryUnlocked(ulong templateID) {
        if (!s_isInitialized) {
            GetAllAuctionHouseEntriesUnlocked();
        }

        using var session = s_store.OpenSession();

        var entry = session.Query<AuctionHouseEntry>(collectionName: CollectionName)
            .FirstOrDefault(entry => entry.m_templateID == templateID);

        // Remove the entry from the collection.
        if (entry != null) {
            session.Delete(entry);
            session.SaveChanges();
        }

        var removed = s_entries.RemoveAll(x => x.m_templateID == templateID);
        return removed != 0;
    }

    /// <summary>
    /// Updates the entry in the Auction House collection for a specific template ID.
    /// </summary>
    /// <param name="entry">The new Auction House entry to update with.</param>
    /// <returns>True if the Auction House entry was updated, false if the entry could not be found.</returns>
    public static bool UpdateAuctionHouseEntry(AuctionHouseEntry entry) {
        lock (Lock) {
            return UpdateAuctionHouseEntryUnlocked(entry);
        }
    }

    private static bool UpdateAuctionHouseEntryUnlocked(AuctionHouseEntry entry) {
        if (!s_isInitialized) {
            GetAllAuctionHouseEntriesUnlocked();
        }

        var removeSuccess = RemoveAuctionHouseEntryUnlocked(entry.m_templateID);

        if (!removeSuccess) {
            return false;
        }

        AddAuctionHouseEntryUnlocked(entry);

        return true;
    }

    /// <summary>
    /// CLASSIC: applies a whole restock in one database session: entries to add or update by template, and templates to
    /// remove. The caller holds <see cref="Lock"/>.
    /// </summary>
    internal static void ApplyStockChanges(IReadOnlyCollection<AuctionHouseEntry> upserts, IReadOnlyCollection<ulong> removals) {
        GetAllAuctionHouseEntriesUnlocked();
        using var session = s_store.OpenSession();
        var stored = session.Query<AuctionHouseEntry>(collectionName: CollectionName).Take(int.MaxValue).ToList();
        var byTemplate = stored.GroupBy(entry => entry.m_templateID.Full).ToDictionary(group => group.Key, group => group.ToList());

        foreach (var template in removals) {
            if (byTemplate.Remove(template, out var docs)) {
                docs.ForEach(session.Delete);
            }

            s_entries.RemoveAll(entry => entry.m_templateID.Full == template);
        }

        foreach (var entry in upserts) {
            var template = entry.m_templateID.Full;
            if (byTemplate.TryGetValue(template, out var docs) && docs.Count > 0) {
                docs[0].m_numForSale = entry.m_numForSale;
                docs[0].m_buyPrice = entry.m_buyPrice;
                docs[0].m_sellPrice = entry.m_sellPrice;
                docs.Skip(1).ToList().ForEach(session.Delete);
            }
            else {
                var copy = new AuctionHouseEntry {
                    m_templateID = entry.m_templateID,
                    m_numForSale = entry.m_numForSale,
                    m_buyPrice = entry.m_buyPrice,
                    m_sellPrice = entry.m_sellPrice,
                };
                session.Store(copy);
                session.Advanced.GetMetadataFor(copy)[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;
            }

            var cached = s_entries.FirstOrDefault(x => x.m_templateID.Full == template);
            if (cached is null) {
                s_entries.Add(entry);
            }
            else {
                cached.m_numForSale = entry.m_numForSale;
                cached.m_buyPrice = entry.m_buyPrice;
                cached.m_sellPrice = entry.m_sellPrice;
            }
        }

        session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
        session.SaveChanges();
    }

    /// <summary>
    /// CLASSIC: update only quote fields of one existing template; retain every document's copies and identity.
    /// An acknowledged database save precedes the cache update. Failure propagates without changing the cache.
    /// </summary>
    internal static bool ApplyPriceChanges(ulong templateId, int expectedCopies, int buy, int sell) {
        lock (Lock) {
            GetAllAuctionHouseEntriesUnlocked();
            using var session = s_store.OpenSession();
            var stored = session.Query<AuctionHouseEntry>(collectionName: CollectionName)
                .Take(int.MaxValue).ToList().Where(entry => entry.m_templateID.Full == templateId).ToList();
            return AuctionHouseQuoteCorrection.Apply(stored, s_entries, templateId, expectedCopies, buy, sell,
                session.SaveChanges);
        }
    }

}

// CLASSIC: separate the bounded field update from Raven's session for acknowledged-save/failure checks.
// This changes no document identity, copy count or server-stock ledger, even when duplicate documents exist.
internal static class AuctionHouseQuoteCorrection {
    internal static bool Apply(IReadOnlyList<AuctionHouseEntry> stored, IReadOnlyList<AuctionHouseEntry> cached,
            ulong templateId, int expectedCopies, int buy, int sell, System.Action save) {
        if (stored.Count == 0 || stored.Any(entry => entry.m_templateID.Full != templateId)
                || stored.Sum(entry => entry.m_numForSale) != expectedCopies || expectedCopies <= 0 || buy < 1 || sell < 0) {
            throw new InvalidOperationException("Bazaar quote correction found unexpected persisted stock; prices retained.");
        }

        var before = stored.Select(entry => (Entry: entry, Buy: entry.m_buyPrice, Sell: entry.m_sellPrice)).ToList();
        var changed = before.Any(entry => entry.Buy != buy || entry.Sell != sell);
        if (changed) {
            foreach (var entry in stored) {
                entry.m_buyPrice = buy;
                entry.m_sellPrice = sell;
            }

            try { save(); }
            catch {
                // A save may have committed remotely before losing its acknowledgement. Do not update the cache
                // or claim success; initialization aborts and the next attempt rereads the actual database state.
                foreach (var entry in before) {
                    entry.Entry.m_buyPrice = entry.Buy;
                    entry.Entry.m_sellPrice = entry.Sell;
                }
                throw;
            }
        }

        foreach (var entry in cached.Where(entry => entry.m_templateID.Full == templateId)) {
            entry.m_buyPrice = buy;
            entry.m_sellPrice = sell;
        }

        return changed;
    }
}
