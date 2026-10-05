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
 * LOGIN SESSION KEY LIFETIME
 * ========================================================================
 *
 * PURPOSE:
 * When a login session key (ClientKey2: in-client login, or the launcher's
 * key on the client's -U command line) may answer a MSG_USER_VALIDATE.
 *
 * NOTE:
 * Upstream keys never expired (the RavenDB @expires metadata had no
 * expiration feature behind it) and worked from anywhere. The r806919 client
 * keeps the key for its whole run and validates with it again when it goes
 * back to character select, and MSG_USER_VALIDATE_RSP cannot hand it a new
 * one, so a key cannot be strictly single-use. Instead:
 *  - it expires after IdleMinutes without use (a validate or a game attach
 *    refreshes it) and IssuedUtc + MaxHours at the latest;
 *  - it only answers the address it was issued to (the launcher's or the
 *    login connection's), unless that was a loopback address (a local proxy
 *    or tunnel in front of the launcher page, which hides the real one);
 *  - PassKey3 is salted per connection, so a captured validate cannot be
 *    replayed, and the key itself is now 32 random bytes.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Net;

namespace Imlight.Classic.Net;

public enum LoginKeyRefusal {
    None,
    Missing,
    Expired,
    WrongAddress,
}

public sealed record LoginKeyPolicy(TimeSpan Idle, TimeSpan Max, bool BindAddress) {

    public static LoginKeyPolicy Default { get; } = new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(12), true);

    /// <summary>The policy from settings, with sane bounds (idle 1 min..24 h, max idle..7 days).</summary>
    public static LoginKeyPolicy From(int idleMinutes, int maxHours, bool bindAddress) {
        var idle = TimeSpan.FromMinutes(Math.Clamp(idleMinutes <= 0 ? 30 : idleMinutes, 1, 24 * 60));
        var max = TimeSpan.FromHours(Math.Clamp(maxHours <= 0 ? 12 : maxHours, 1, 24 * 7));
        return new LoginKeyPolicy(idle, max < idle ? idle : max, bindAddress);
    }

    /// <summary>When a key issued or last used at the given times stops working.</summary>
    public DateTime ExpiresAt(DateTime issuedUtc, DateTime lastUsedUtc) {
        var idleEnd = lastUsedUtc + Idle;
        var maxEnd = issuedUtc + Max;
        return idleEnd < maxEnd ? idleEnd : maxEnd;
    }

    /// <summary>Whether a stored key may answer a validate now from <paramref name="address"/>.</summary>
    /// <param name="issuedUtc">When it was issued; DateTime.MinValue for a record written before expiry existed.</param>
    public LoginKeyRefusal Check(string? key, DateTime issuedUtc, DateTime lastUsedUtc, string? issuedAddress,
                                 string? address, DateTime nowUtc) {
        if (string.IsNullOrEmpty(key)) {
            return LoginKeyRefusal.Missing;
        }

        if (issuedUtc == DateTime.MinValue || nowUtc >= ExpiresAt(issuedUtc, lastUsedUtc < issuedUtc ? issuedUtc : lastUsedUtc)) {
            return LoginKeyRefusal.Expired;
        }

        if (BindAddress && !SameClient(issuedAddress, address)) {
            return LoginKeyRefusal.WrongAddress;
        }

        return LoginKeyRefusal.None;
    }

    /// <summary>
    /// Whether two addresses are the same client. An unknown or loopback issuing address (a proxy) matches any.
    /// </summary>
    public static bool SameClient(string? issued, string? now) {
        var a = GameSessionKeys.NormalizeAddress(issued);
        if (a is null || (IPAddress.TryParse(a, out var ip) && IPAddress.IsLoopback(ip))) {
            return true;
        }

        return a == GameSessionKeys.NormalizeAddress(now);
    }
}
