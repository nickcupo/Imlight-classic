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
 * ONLINE PLAYER COLLECTION
 * ========================================================================
 * 
 * PURPOSE:
 * Manages the collection of online players, including adding, removing,
 * and retrieving player data from the database.
 * 
 * USAGE EXAMPLE:
 * AddOnlinePlayer(onlinePlayer);
 * RemoveOnlinePlayer(accountId);
 * GetOnlinePlayers();
 * GetOnlinePlayer(characterId);
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 04/28/2025
 */

using System;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Misc;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.WizardData.Collections;

public static class OnlinePlayerCollection {

    public const string CollectionName = "OnlinePlayers";

    // CLASSIC: opened on first database use, so the in-memory list works without a database (tests, early start).
    private static IDocumentStore Store => PlayerDatabase.Instance.Store;

    // CLASSIC: the authority. The Director hosts every server in one process and clears the list at start, so this
    // cache holds every online player. The database copy is a mirror for outside tools only: reading it back returned
    // players whose sessions were gone (a removal query that ran before RavenDB indexed the add, or an entry left by a
    // crash), and a teleport-to-friend then asked a dead session (rig-pg-ms 2026-10-04).
    private static readonly ConcurrentDictionary<ulong, OnlinePlayer> s_onlinePlayerCache = new();

    private static void DeleteOnlinePlayer(IDocumentSession session, OnlinePlayer onlinePlayer) {
        var documentId = session.Advanced.GetDocumentId(onlinePlayer);
        session.Advanced.Evict(onlinePlayer);
        session.Delete(documentId);
    }

    /// <summary>
    /// Adds an online player to the collection.
    /// </summary>
    /// <param name="onlinePlayer">The online player to add.</param>
    public static void AddOnlinePlayer(OnlinePlayer onlinePlayer) {
        s_onlinePlayerCache.AddOrUpdate(
            onlinePlayer.AccountId,
            onlinePlayer,
            (_, _) => onlinePlayer
        );

        // Store the online player in the database. CLASSIC: one document per account (a fixed id), so a login, every
        // zone change and the logout touch the same row. Stored without an id, each of them added a new row and the
        // removal (an index query, often stale right after the store) missed some: offline wizards stayed "online"
        // in the database, and a cache miss (GetOnlinePlayer) read them back (lost whispers, friends shown online).
        using var session = Store.OpenSession();

        session.Store(onlinePlayer, DocumentIdFor(onlinePlayer.AccountId));
        var metadata = session.Advanced.GetMetadataFor(onlinePlayer);
        metadata[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;

        session.SaveChanges();
    }

    /// <summary>
    /// CLASSIC: lists an ambient wizard as online (memory only, never the database), keyed by its character id.
    /// </summary>
    public static void SetVirtualOnlinePlayer(OnlinePlayer onlinePlayer)
        => s_onlinePlayerCache[onlinePlayer.AccountId] = onlinePlayer;

    /// <summary>CLASSIC: takes an ambient wizard off the online list.</summary>
    public static void RemoveVirtualOnlinePlayer(ulong key) => s_onlinePlayerCache.TryRemove(key, out _);

    /// <summary>
    /// Removes an online player from the collection based on the specified account ID.
    /// </summary>
    /// <param name="accountId">The character ID of the online player to remove.</param>
    public static void RemoveOnlinePlayer(ulong accountId) {
        // Remove from the cache.
        s_onlinePlayerCache.TryRemove(accountId, out _);

        using var session = Store.OpenSession();

        session.Delete(DocumentIdFor(accountId)); // CLASSIC: by its fixed id (no index query)
        session.SaveChanges();
    }

    /// <summary>CLASSIC: the database id of an account's online row.</summary>
    internal static string DocumentIdFor(ulong accountId) => $"{CollectionName}/{accountId}";

    /// <summary>
    /// Removes an online player from the collection based on the specified session ID.
    /// </summary>
    /// <param name="sessionId">The session ID of the online player to remove.</param>
    public static void RemoveOnlinePlayer(ushort sessionId) {
        // Scan the snapshot and remove every entry with a matching SessionId.
        var accounts = new List<ulong>();
        foreach (var kvp in s_onlinePlayerCache) {
            if (kvp.Value.SessionId == sessionId && !Classic.Ambient.AmbientWizards.IsAmbientChar(kvp.Value.CharacterId)) { // CLASSIC
                if (s_onlinePlayerCache.TryRemove(kvp)) {
                    accounts.Add(kvp.Key);
                }
            }
        }

        // CLASSIC: the account's row, by its fixed id, and only while it is still this session's (a zone change's new
        // session may have written it already). A session no longer in the cache has nothing left to remove.
        if (accounts.Count == 0) {
            return;
        }

        using var session = Store.OpenSession();
        var changed = false;
        foreach (var accountId in accounts) {
            var row = session.Load<OnlinePlayer>(DocumentIdFor(accountId));
            if (row is not null && row.SessionId == sessionId) {
                DeleteOnlinePlayer(session, row);
                changed = true;
            }
        }

        if (changed) {
            session.SaveChanges();
        }
    }

    /// <summary>
    /// CLASSIC: takes every entry of a disposed session off the list, by its session actor's path (session ids are
    /// reused and differ between the login and game servers; the path is the session itself). Memory at once, the
    /// database mirror in the background so a disposing session is not held up by it.
    /// </summary>
    /// <param name="actorPath">The session actor's path, as stored in <see cref="OnlinePlayer.ActorPath"/>.</param>
    /// <returns>How many entries left the in-memory list.</returns>
    public static int RemoveSessionByActorPath(string actorPath) {
        if (string.IsNullOrEmpty(actorPath)) {
            return 0;
        }

        var removed = 0;
        foreach (var kvp in s_onlinePlayerCache) {
            if (kvp.Value.ActorPath == actorPath && !Classic.Ambient.AmbientWizards.IsAmbientChar(kvp.Value.CharacterId)
                    && s_onlinePlayerCache.TryRemove(kvp)) {
                removed++;
            }
        }

        if (!PlayerDatabase.IsCreated) {
            return removed;
        }

        _ = System.Threading.Tasks.Task.Run(() => {
            try {
                using var session = Store.OpenSession();
                foreach (var stale in session.Query<OnlinePlayer>(collectionName: CollectionName)
                             .Where(x => x.ActorPath == actorPath).ToList()) {
                    DeleteOnlinePlayer(session, stale);
                }

                session.SaveChanges();
            }
            catch (Exception ex) {
                Imlight.Common.Logger.Debug("Online player mirror cleanup for {0} failed: {1}", Imlight.Common.Logger.Args(actorPath, ex.Message));
            }
        });

        return removed;
    }

    /// <summary>
    /// Retrieves all online players from the collection.
    /// </summary>
    /// <returns>An array of online players.</returns>
    public static OnlinePlayer[] GetOnlinePlayers()
        => s_onlinePlayerCache.Values.ToArray();

    /// <summary>
    /// Retrieves the online player with the specified character ID from the collection.
    /// </summary>
    /// <param name="characterId">The character ID of the online player to retrieve.</param>
    /// <returns>The online player with the specified character ID, or null if not found.</returns>
    public static OnlinePlayer GetOnlinePlayer(ulong characterId)
        => s_onlinePlayerCache.Values.FirstOrDefault(x => x.CharacterId == characterId);

    /// <summary>
    /// Retrieves all online players in the specified zone from the collection.
    /// </summary>
    /// <param name="zone">The zone to filter by.</param>
    /// <returns>An array of online players in the specified zone.</returns>
    public static OnlinePlayer[] GetPlayersInZone(string zone)
        => s_onlinePlayerCache.Values.Where(x => x.CurrentZone == zone).ToArray();

    /// <summary>
    /// Retrieves all online players in the specified realm from the collection.
    /// </summary>
    /// <param name="realm">The realm to filter by.</param>
    /// <returns>An array of online players in the specified realm.</returns>
    public static OnlinePlayer[] GetPlayersInRealm(string realm)
        => s_onlinePlayerCache.Values.Where(x => x.CurrentRealm == realm).ToArray();

    public static void Clear() {
        // Clear the cache.
        s_onlinePlayerCache.Clear();

        using var session = Store.OpenSession();

        var onlinePlayers = session
            .Query<OnlinePlayer>(collectionName: CollectionName)
            .ToArray();
        foreach (var onlinePlayer in onlinePlayers) {
            DeleteOnlinePlayer(session, onlinePlayer);
        }

        session.SaveChanges();
    }
    
}
