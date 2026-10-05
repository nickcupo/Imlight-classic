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
 * CONNECTION LIMITS
 * ========================================================================
 *
 * PURPOSE:
 * Unauthenticated connections cost memory and session ids. One host could
 * open hundreds of sockets and fill the login slots. These are the rules the
 * servers apply before a session exists, and before its handshake finishes.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System.Net;

namespace Imlight.Classic.Net;

public static class ConnectionLimits {

    /// <summary>Messages a session may send before its SessionAccept; the official client sends none.</summary>
    public const int DefaultPreHandshakeMessages = 16;

    /// <summary>
    /// Why a new connection from <paramref name="address"/> is refused, or null to accept it.
    /// </summary>
    /// <param name="address">The client address.</param>
    /// <param name="openFromAddress">Connections already open from that address.</param>
    /// <param name="openTotal">Connections already open on this server.</param>
    /// <param name="maxPerAddress">Limit per address; 0 or less = none.</param>
    /// <param name="maxTotal">Limit for the server; 0 or less = none.</param>
    /// <param name="limitLoopback">Whether loopback (local bots, a local proxy) counts too.</param>
    public static string? Refuse(string address, int openFromAddress, int openTotal, int maxPerAddress, int maxTotal,
                                 bool limitLoopback) {
        if (maxTotal > 0 && openTotal >= maxTotal) {
            return "server connection limit";
        }

        if (maxPerAddress <= 0) {
            return null;
        }

        if (!limitLoopback && IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip)) {
            return null;
        }

        return openFromAddress >= maxPerAddress ? "per-address connection limit" : null;
    }

    /// <summary>Whether a session that has not finished its handshake may cache one more message.</summary>
    public static bool MayCachePreHandshake(int cached, int limit)
        => cached < (limit > 0 ? limit : DefaultPreHandshakeMessages);
}
