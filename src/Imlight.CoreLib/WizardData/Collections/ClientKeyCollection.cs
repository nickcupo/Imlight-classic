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
using System.Linq;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Imlight.CoreLib.WizardData.Databases;
using Imcodec.IO;
using Imlight.Classic.Net;

namespace Imlight.CoreLib.WizardData.Collections;

public class ClientKeyPair(ulong accountId, ulong machineId, string clientKey2) {

    public ulong AccountId { get; set; } = accountId;
    public ulong MachineId { get; set; } = machineId;
    public string ClientKey2 { get; set; } = clientKey2 
        ?? throw new ArgumentNullException(nameof(clientKey2));

    // CLASSIC: expiry and address binding (Imlight.Classic.Net.LoginKeyPolicy).
    public DateTime IssuedUtc { get; set; }
    public DateTime LastUsedUtc { get; set; }
    public string Address { get; set; }

}

public static class ClientKeyCollection {

    private const string CollectionName = SessionKeyDocument.CollectionName; // CLASSIC

    private static readonly IDocumentStore Store;

    // CLASSIC: [Login Server] LoginKeyIdleMinutes (30), LoginKeyMaxHours (12), LoginKeyBindIp (true).
    internal static readonly Lazy<LoginKeyPolicy> Policy = new(() => LoginKeyPolicy.From(
        Imlight.CoreLib.Auth.SecuritySettings.Int("Login Server.LoginKeyIdleMinutes", 30),
        Imlight.CoreLib.Auth.SecuritySettings.Int("Login Server.LoginKeyMaxHours", 12),
        Imlight.CoreLib.Auth.SecuritySettings.Bool("Login Server.LoginKeyBindIp", true)));

    static ClientKeyCollection() {
        Store = PlayerDatabase.Instance.Store;
    }

    /// <summary>
    /// Adds a new session key to the database.
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="machineId"></param>
    /// <param name="key"></param>
    public static void AddSessionKey(ulong accountId, ulong machineId, string key, string address) {
        using var session = Store.OpenSession();

        // CLASSIC: one document per account, stored under a fixed id, replaces upstream's asynchronous
        // delete-by-query plus a new random-id document. The delete could still be running when the new
        // key was stored, and GetSessionKey's index query could miss the new key or return an older one,
        // so the validate right after a login failed now and then (ValidateFailed).
        var now = DateTime.UtcNow;
        var pair = new ClientKeyPair(accountId, machineId, key) {
            IssuedUtc = now,
            LastUsedUtc = now,
            Address = GameSessionKeys.NormalizeAddress(address),
        };
        var expiry = now + Policy.Value.Max;

        // Store and set the metadata of the new document.
        session.Store(pair, SessionKeyDocument.IdFor(accountId)); // CLASSIC: overwrites the previous key.
        var metadata = session.Advanced.GetMetadataFor(pair);
        metadata[Constants.Documents.Metadata.Collection] = CollectionName;
        metadata[Constants.Documents.Metadata.Expires] = expiry;

        session.SaveChanges();
    }

    /// <summary>
    /// Gets the session key from the database.
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="machineId"></param>
    /// <returns></returns>
    public static ByteString GetSessionKey(ulong accountId, ulong machineId, string address) {
        using var session = Store.OpenSession();

        // CLASSIC: a load by id is ACID in RavenDB, unlike the index query it replaces.
        var pair = session.Load<ClientKeyPair>(SessionKeyDocument.IdFor(accountId));
        if (pair is null
                || !SessionKeyDocument.Answers(pair.AccountId, pair.MachineId, pair.ClientKey2, accountId, machineId)) {
            return (string) null;
        }

        // CLASSIC: expiry and address binding.
        var refusal = Policy.Value.Check(pair.ClientKey2, pair.IssuedUtc, pair.LastUsedUtc, pair.Address, address,
            DateTime.UtcNow);
        if (refusal != LoginKeyRefusal.None) {
            Imlight.Common.Logger.Information("Validate: session key for account {0} refused ({1}) from {2}",
                Imlight.Common.Logger.Args(accountId, refusal, address));
            return (string) null;
        }

        return pair.ClientKey2;
    }

    /// <summary>
    /// CLASSIC: the key was used (a validate or a game attach): it stays valid for another idle window.
    /// </summary>
    public static void Touch(ulong accountId) {
        try {
            using var session = Store.OpenSession();
            var pair = session.Load<ClientKeyPair>(SessionKeyDocument.IdFor(accountId));
            if (pair is null || pair.IssuedUtc == DateTime.MinValue) {
                return;
            }

            pair.LastUsedUtc = DateTime.UtcNow;
            session.SaveChanges();
        }
        catch (Exception ex) {
            Imlight.Common.Logger.Warning("Session key touch for {0} failed: {1}", Imlight.Common.Logger.Args(accountId, ex.Message));
        }
    }

    /// <summary>CLASSIC: forgets the account's login key (a password change).</summary>
    public static void Revoke(ulong accountId) {
        using var session = Store.OpenSession();
        session.Delete(SessionKeyDocument.IdFor(accountId));
        session.SaveChanges();
    }
    
}
