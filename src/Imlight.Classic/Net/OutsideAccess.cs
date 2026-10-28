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
 * OUTSIDE ACCESS (off switch, LAN-only rules)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (outside-access): the server's own off switch for friends outside
 * the home network (docs/runbooks/outside-access.md), and the rule that keeps
 * [Classic] AnyPasswordLogin on the home LAN.
 *
 * SETTINGS ([Classic]):
 * OutsideAccess = off       the switch is off from start-up (default on).
 * OutsideAccessOffFile      a path (/var/lib/w101c-public/outside-access-off):
 *                           while that file exists the switch is off. Checked at
 *                           most every 2 s, so creating / moving it away takes
 *                           effect without a restart (w101c-public off / on). Its
 *                           folder missing or unreadable also counts as off: a
 *                           plain "file not found" must never mean "open".
 * LanNetworks               CIDRs that count as the home LAN (e.g.
 *                           192.168.1.0/24,10.50.0.0/24); empty = every private
 *                           address (PublicAccess.IsPrivate). Loopback always.
 *
 * While the switch is off every listener refuses a client outside LanNetworks
 * and the game servers close the ones already connected. A loopback peer that
 * stands for a visitor (the local tunnel, the HTTPS front) is judged by the
 * visitor it names; with no visitor named it is refused.
 *
 * NOTE:
 * With neither setting the switch is never off and nothing changes.
 *
 * Created by: Nick with Claude Code
 * Version: KALI 1.0
 * Last Updated: 10/28/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;

namespace Imlight.Classic.Net;

public sealed class OutsideSwitch {
    private static readonly TimeSpan s_recheck = TimeSpan.FromSeconds(2);

    private readonly bool _offByConfig;
    private readonly string? _offFile;
    private readonly IReadOnlyList<(IPAddress Network, int Bits)>? _lan;
    private readonly Func<DateTime> _clock;
    private readonly Func<string, bool> _exists;
    private readonly object _gate = new();
    private DateTime _checkedUtc = DateTime.MinValue;
    private bool _fileOff;

    public OutsideSwitch(bool offByConfig, string? offFile, IReadOnlyList<(IPAddress Network, int Bits)>? lanNetworks,
                         Func<DateTime>? clock = null, Func<string, bool>? exists = null) {
        _offByConfig = offByConfig;
        _offFile = string.IsNullOrWhiteSpace(offFile) ? null : offFile.Trim();
        _lan = lanNetworks;
        _clock = clock ?? (() => DateTime.UtcNow);
        _exists = exists ?? OffFilePresent;
    }

    /// <summary>The file is there, or its folder cannot be seen or read (fail closed).</summary>
    public static bool OffFilePresent(string path) {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return true;
        using (var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator()) {
            entries.MoveNext(); // throws when the folder cannot be read (caught by IsOff: off)
        }

        return File.Exists(path);
    }

    /// <summary>Whether the switch can ever be off (a setting names it); the game servers sweep only then.</summary>
    public bool Configured => _offByConfig || _offFile is not null;

    /// <summary>True while outside clients are refused.</summary>
    public bool IsOff {
        get {
            if (_offByConfig) return true;
            if (_offFile is null) return false;
            lock (_gate) {
                var now = _clock();
                if (now - _checkedUtc >= s_recheck || now < _checkedUtc) {
                    bool off;
                    try {
                        off = _exists(_offFile);
                    } catch (Exception) {
                        off = true; // cannot tell: fail closed
                    }

                    _fileOff = off;
                    _checkedUtc = now;
                }

                return _fileOff;
            }
        }
    }

    /// <summary>Loopback always; then LanNetworks, or every private address when that is not set.</summary>
    public bool IsLan(IPAddress? address) => OutsideAccess.IsLan(address, _lan);

    public bool IsLan(string? address) => IPAddress.TryParse(address?.Trim().Trim('[', ']'), out var ip) && IsLan(ip);

    /// <summary>Whether a direct connection from <paramref name="peer"/> may go on now.</summary>
    public bool Admits(IPAddress? peer) => !IsOff || IsLan(peer);

    public bool Admits(string? peer) => !IsOff || IsLan(peer);

    /// <summary>
    /// Whether a request may go on now. <paramref name="visitor"/> is the value of the trusted visitor header, read only
    /// when <paramref name="peer"/> is loopback (a tunnel or the HTTPS front in front of this listener).
    /// </summary>
    public bool AdmitsRequest(IPAddress? peer, string? visitor, bool behindLocalProxy) {
        if (!IsOff) return true;
        if (behindLocalProxy && PublicAccess.IsLoopback(peer)) {
            return IPAddress.TryParse(visitor?.Trim().Trim('[', ']'), out var ip) && IsLan(ip);
        }

        return IsLan(peer);
    }
}

public static class OutsideAccess {

    /// <summary>The LAN rule shared by the off switch and AnyPasswordLogin.</summary>
    public static bool IsLan(IPAddress? address, IReadOnlyList<(IPAddress Network, int Bits)>? lanNetworks) {
        if (address is null) return false;
        if (PublicAccess.IsLoopback(address)) return true;
        return lanNetworks is null ? PublicAccess.IsPrivate(address) : PublicAccess.InNetworks(address, lanNetworks);
    }

    /// <summary>
    /// [Classic] AnyPasswordLogin applies only to a client on the home LAN that reached a LAN listener: never on the
    /// HTTPS front or the tunnel's listener (<paramref name="publicListener"/>), never to an outside address.
    /// </summary>
    public static bool AnyPasswordApplies(bool configured, IPAddress? client, bool publicListener,
                                          IReadOnlyList<(IPAddress Network, int Bits)>? lanNetworks)
        => configured && !publicListener && IsLan(client, lanNetworks);

    public static bool AnyPasswordApplies(bool configured, string? client, bool publicListener,
                                          IReadOnlyList<(IPAddress Network, int Bits)>? lanNetworks)
        => configured && IPAddress.TryParse(client?.Trim().Trim('[', ']'), out var ip)
           && AnyPasswordApplies(true, ip, publicListener, lanNetworks);
}
