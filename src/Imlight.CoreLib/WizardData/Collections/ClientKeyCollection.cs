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

}

public static class ClientKeyCollection {

    private const string CollectionName = SessionKeyDocument.CollectionName; // CLASSIC

    private static readonly IDocumentStore Store;
    private const uint KeyExpireTimeInHours = 30;

    static ClientKeyCollection() {
        Store = PlayerDatabase.Instance.Store;
    }

    /// <summary>
    /// Adds a new session key to the database.
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="machineId"></param>
    /// <param name="key"></param>
    public static void AddSessionKey(ulong accountId, ulong machineId, string key) {
        using var session = Store.OpenSession();

        // CLASSIC: one document per account, stored under a fixed id, replaces upstream's asynchronous
        // delete-by-query plus a new random-id document. The delete could still be running when the new
        // key was stored, and GetSessionKey's index query could miss the new key or return an older one,
        // so the validate right after a login failed now and then (ValidateFailed).
        var pair = new ClientKeyPair(accountId, machineId, key);
        var expiry = DateTime.UtcNow.AddHours(KeyExpireTimeInHours);

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
    public static ByteString GetSessionKey(ulong accountId, ulong machineId) {
        using var session = Store.OpenSession();

        // CLASSIC: a load by id is ACID in RavenDB, unlike the index query it replaces.
        var pair = session.Load<ClientKeyPair>(SessionKeyDocument.IdFor(accountId));
        string key = pair is not null
            && SessionKeyDocument.Answers(pair.AccountId, pair.MachineId, pair.ClientKey2, accountId, machineId)
            ? pair.ClientKey2
            : null;

        return key;
    }
    
}
