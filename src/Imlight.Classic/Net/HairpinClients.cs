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
 * HAIRPIN CLIENTS (go-live)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: a machine on the home LAN whose launcher uses the public host
 * (play.nickcupo.com) reaches the server through UniFi's hairpin NAT, which
 * keeps the machine's LAN source address. By address alone the server would
 * tell it the private game address, and its launcher's network watchdog
 * (which allows only the address the launcher resolved) kills the game.
 *
 * The launcher sign-in knows which host the launcher uses; the game login
 * that follows knows the machine's address. This links the two: the session
 * key a public-host launcher was given marks the address that validates it,
 * and that address then gets the public game address (transfers and the
 * patch service included) until a login from it says otherwise.
 *
 * NOTE:
 * In memory only. After a server restart a machine is back to the address
 * rule until its next login (the patch service runs before the login).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

using System;
using System.Collections.Concurrent;

namespace Imlight.Classic.Net;

public sealed class HairpinClients {
    private const int MaxEntries = 4096;

    // Account -> the launcher session key it was given by a launcher that uses the public host.
    private readonly ConcurrentDictionary<ulong, string> _publicKeys = new();
    // Normalized client address -> true while its last login came from a public-host launcher.
    private readonly ConcurrentDictionary<string, bool> _publicAddresses = new(StringComparer.Ordinal);

    /// <summary>A launcher sign-in gave <paramref name="accountId"/> <paramref name="sessionKey"/>.</summary>
    public void LauncherKey(ulong accountId, string sessionKey, bool usesPublicHost) {
        if (usesPublicHost) {
            if (_publicKeys.Count >= MaxEntries) _publicKeys.Clear();
            _publicKeys[accountId] = sessionKey;
        } else {
            _publicKeys.TryRemove(accountId, out _);
        }
    }

    /// <summary>
    /// A game login from <paramref name="clientAddress"/> validated <paramref name="sessionKey"/>. Returns true when
    /// the address's state changed (for one log line), with <paramref name="nowPublic"/> the new state.
    /// </summary>
    public bool Validated(ulong accountId, string? sessionKey, string? clientAddress, out bool nowPublic) {
        nowPublic = false;
        var address = PublicAccess.NormalizeAddress(clientAddress);
        if (address is null) return false;
        nowPublic = !string.IsNullOrEmpty(sessionKey) && _publicKeys.TryGetValue(accountId, out var key)
                    && string.Equals(key, sessionKey, StringComparison.Ordinal);
        if (nowPublic) {
            if (_publicAddresses.Count >= MaxEntries) _publicAddresses.Clear();
            return _publicAddresses.TryAdd(address, true);
        }

        return _publicAddresses.TryRemove(address, out _);
    }

    /// <summary>Whether <paramref name="clientAddress"/>'s last login came from a public-host launcher.</summary>
    public bool UsesPublicHost(string? clientAddress)
        => PublicAccess.NormalizeAddress(clientAddress) is { } address && _publicAddresses.ContainsKey(address);
}
