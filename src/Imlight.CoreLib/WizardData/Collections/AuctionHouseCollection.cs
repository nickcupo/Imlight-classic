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
using Raven.Client.Documents.Linq;
using Imlight.CoreLib.WizardData.Databases;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.WizardData.Collections;

// CLASSIC: stock mutations publish snapshots only after the database acknowledges one save.
internal class AuctionHouseCollection {
    public const string CollectionName = "AuctionHouse";
    private static readonly Lazy<IDocumentStore> s_storeSource = new(() => PlayerDatabase.Instance.Store);
    private static IDocumentStore s_store => s_storeSource.Value;
    // CLASSIC: scoped tracked-session seams; tests never initialize or write the live store.
    internal static readonly System.Threading.AsyncLocal<Func<Raven.Client.Documents.Session.IDocumentSession>> TestOpenScope = new();
    internal static readonly System.Threading.AsyncLocal<Func<Raven.Client.Documents.Session.IDocumentSession, List<AuctionHouseEntry>>> TestRowsScope = new();
    private static Raven.Client.Documents.Session.IDocumentSession Open()
        => TestOpenScope.Value is { } open ? open() : s_store.OpenSession();
    private static bool s_isInitialized;
    private static List<AuctionHouseEntry> s_entries = [];
    internal static readonly object Lock = new();

    internal static AuctionHouseEntry Snapshot(AuctionHouseEntry entry) => entry is null ? null : new() {
        m_templateID = entry.m_templateID, m_numForSale = entry.m_numForSale,
        m_buyPrice = entry.m_buyPrice, m_sellPrice = entry.m_sellPrice,
    };

    public static List<AuctionHouseEntry> GetAllAuctionHouseEntries() {
        lock (Lock) return GetAllAuctionHouseEntriesUnlocked().Select(Snapshot).ToList();
    }

    private static List<AuctionHouseEntry> GetAllAuctionHouseEntriesUnlocked() {
        if (s_isInitialized) return s_entries;
        using var session = Open();
        var stored = QueryStock(session).ToList();
        s_entries = stored.Select(Snapshot).ToList();
        s_isInitialized = true;
        return s_entries;
    }

    // CLASSIC: fresh, tracked rows; never stage a transaction against detached cache entries.
    internal static IQueryable<AuctionHouseEntry> QueryStock(Raven.Client.Documents.Session.IDocumentSession session)
        => TestRowsScope.Value is { } read ? read(session).AsQueryable()
            : session.Query<AuctionHouseEntry>(collectionName: CollectionName)
                .Customize(q => q.WaitForNonStaleResults(TimeSpan.FromSeconds(5))).Take(int.MaxValue);

    public static AuctionHouseEntry GetAuctionHouseEntry(ulong templateId) {
        lock (Lock) {
            var matches = GetAllAuctionHouseEntriesUnlocked().Where(e => e.m_templateID.Full == templateId).ToList();
            // Ambiguous persisted lots are refused, never summed into a purchasable forged row.
            return matches.Count == 1 ? Snapshot(matches[0]) : null;
        }
    }

    internal static void InvalidateCache() {
        lock (Lock) { s_isInitialized = false; s_entries = []; }
    }

    internal static void PublishCommitted(AuctionHouseEntry entry) {
        lock (Lock) {
            if (!s_isInitialized) return; // The next read hydrates the acknowledged database instead.
            s_entries.RemoveAll(e => e.m_templateID.Full == entry.m_templateID.Full);
            if (entry.m_numForSale > 0) s_entries.Add(Snapshot(entry));
        }
    }

    public static void AddAuctionHouseEntry(AuctionHouseEntry entry) {
        lock (Lock) {
            try {
                using var session = Open();
                session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
                if (QueryStock(session).Any(e => e.m_templateID == entry.m_templateID))
                    throw new InvalidOperationException("Bazaar stock already exists.");
                var copy = Snapshot(entry);
                session.Store(copy);
                session.Advanced.GetMetadataFor(copy)[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;
                session.SaveChanges();
                PublishCommitted(copy);
            }
            catch { InvalidateCache(); throw; }
        }
    }

    public static bool RemoveAuctionHouseEntry(ulong templateId) {
        lock (Lock) {
            try {
                using var session = Open();
                session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
                var docs = QueryStock(session).ToList().Where(e => e.m_templateID.Full == templateId).ToList();
                if (docs.Count != 1) return false;
                session.Delete(docs[0]);
                session.SaveChanges();
                var snapshot = Snapshot(docs[0]); snapshot.m_numForSale = 0;
                PublishCommitted(snapshot);
                return true;
            }
            catch { InvalidateCache(); throw; }
        }
    }

    public static bool UpdateAuctionHouseEntry(AuctionHouseEntry entry) {
        lock (Lock) {
            try {
                using var session = Open();
                session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
                var docs = QueryStock(session).ToList().Where(e => e.m_templateID.Full == entry.m_templateID.Full).ToList();
                if (docs.Count != 1) return false;
                var tracked = docs[0]; // Preserve original Raven identity and change vector.
                tracked.m_numForSale = entry.m_numForSale; tracked.m_buyPrice = entry.m_buyPrice; tracked.m_sellPrice = entry.m_sellPrice;
                session.SaveChanges();
                PublishCommitted(tracked);
                return true;
            }
            catch { InvalidateCache(); throw; }
        }
    }

    internal static void ApplyStockChanges(IReadOnlyCollection<AuctionHouseEntry> upserts, IReadOnlyCollection<ulong> removals) {
        lock (Lock) {
            try {
                using var session = Open();
                session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
                session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
                var stored = QueryStock(session).ToList();
                var byTemplate = stored.GroupBy(e => e.m_templateID.Full).ToDictionary(g => g.Key, g => g.ToList());
                if (upserts.GroupBy(e => e.m_templateID.Full).Any(g => g.Count() != 1)
                    || upserts.Any(e => removals.Contains(e.m_templateID.Full)))
                    throw new InvalidOperationException("Ambiguous Bazaar restock plan.");
                // Restock retains its existing duplicate cleanup policy, but never touches the cache before ACK.
                foreach (var template in removals) {
                    if (byTemplate.Remove(template, out var docs)) docs.ForEach(session.Delete);
                }
                foreach (var entry in upserts) {
                    if (byTemplate.TryGetValue(entry.m_templateID.Full, out var docs) && docs.Count > 0) {
                        docs[0].m_numForSale = entry.m_numForSale; docs[0].m_buyPrice = entry.m_buyPrice; docs[0].m_sellPrice = entry.m_sellPrice;
                        docs.Skip(1).ToList().ForEach(session.Delete);
                    }
                    else {
                        var copy = Snapshot(entry); session.Store(copy);
                        session.Advanced.GetMetadataFor(copy)[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;
                    }
                }
                session.SaveChanges();
                if (s_isInitialized) {
                    foreach (var template in removals) s_entries.RemoveAll(e => e.m_templateID.Full == template);
                    foreach (var entry in upserts) PublishCommitted(entry);
                }
            }
            catch { InvalidateCache(); throw; }
        }
    }

    internal static bool ApplyPriceChanges(ulong templateId, int expectedCopies, int buy, int sell) {
        lock (Lock) {
            GetAllAuctionHouseEntriesUnlocked();
            try {
                using var session = Open();
                session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
                var stored = QueryStock(session).ToList().Where(e => e.m_templateID.Full == templateId).ToList();
                return AuctionHouseQuoteCorrection.Apply(stored, s_entries, templateId, expectedCopies, buy, sell, session.SaveChanges);
            }
            catch { InvalidateCache(); throw; }
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
