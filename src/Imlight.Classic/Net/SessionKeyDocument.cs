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
 * LOGIN SESSION KEYS
 * ========================================================================
 *
 * PURPOSE:
 * Where an account's login session key lives, and whether a stored key
 * answers a MSG_USER_VALIDATE.
 *
 * USAGE EXAMPLE:
 * session.Store(pair, SessionKeyDocument.IdFor(accountId));
 * var pair = session.Load<ClientKeyPair>(SessionKeyDocument.IdFor(accountId));
 *
 * NOTE:
 * Upstream stored each key under a random document id, deleted the old ones
 * with an asynchronous delete-by-query and read the key back through an
 * index query. The validate that follows a login within milliseconds could
 * read a stale index (no key yet, or an older key for the same machine), and
 * the delete could still be running when the new key was stored, so login
 * failed intermittently with ValidateFailed. One document per account, read
 * by id, is ACID in RavenDB: the validate always sees the newest key.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Globalization;

namespace Imlight.Classic.Net;

/// <summary>
/// The login session key's document id and match rule.
/// </summary>
public static class SessionKeyDocument {

    /// <summary>
    /// The RavenDB collection that holds session keys.
    /// </summary>
    public const string CollectionName = "SessionKeys";

    /// <summary>
    /// The one document id for an account's session key.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <returns>The document id.</returns>
    public static string IdFor(ulong accountId)
        => $"{CollectionName}/{accountId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Whether a stored key answers a validate from <paramref name="machineId"/>.
    /// </summary>
    /// <param name="storedAccountId">The account id stored with the key.</param>
    /// <param name="storedMachineId">The machine id stored with the key.</param>
    /// <param name="storedKey">The stored key.</param>
    /// <param name="accountId">The account that is validating.</param>
    /// <param name="machineId">The machine that is validating.</param>
    /// <returns>True if the key may be used.</returns>
    public static bool Answers(ulong storedAccountId, ulong storedMachineId, string? storedKey,
                               ulong accountId, ulong machineId)
        => storedAccountId == accountId && !string.IsNullOrEmpty(storedKey)
           // CLASSIC: a client started with a key on its command line (-U ..USERID KEY USER, from KingsIsle's own
           // launcher) validates as machine 0; the key was stored for the launcher's machine. The key still has to
           // answer the PassKey3 challenge.
           && (storedMachineId == machineId || machineId == CommandLineMachineId);

    /// <summary>
    /// The machine id the r806919 client reports when it validates a key given on its command line.
    /// </summary>
    public const ulong CommandLineMachineId = 0;

}
