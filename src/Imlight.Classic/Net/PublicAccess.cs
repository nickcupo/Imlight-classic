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
 * PUBLIC ACCESS (go-live)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the pure rules for running the server for friends outside the
 * home network (docs/runbooks/go-live.md):
 *  - which addresses are "private" (the home LAN, the server's VLAN, loopback);
 *  - the real client address behind the local Cloudflare tunnel: a request
 *    from loopback (cloudflared runs in the same container) may name its
 *    visitor in one configured header (CF-Connecting-IP). Anything else is
 *    taken at face value, so a LAN or internet client cannot spoof it;
 *  - whether a request came through a proxy at all (the admin pages refuse it);
 *  - the address the game is told to connect to after login and on every
 *    server transfer: the configured (private) GameServerIP for clients on
 *    the private networks, the public IPv4 for everyone else.
 *
 * NOTE:
 * Every switch is off by default; with nothing configured, behaviour is
 * exactly as before.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/08/2026
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Imlight.Classic.Net;

public static class PublicAccess {

    /// <summary>Headers a reverse proxy or tunnel adds. A request carrying any of them did not come straight from a browser.</summary>
    public static readonly string[] ProxyHeaders = ["CF-Connecting-IP", "CF-Ray", "X-Forwarded-For", "Forwarded", "X-Real-IP"];

    /// <summary>Loopback, RFC 1918, link-local, CGNAT 100.64/10, IPv6 unique-local and link-local.</summary>
    public static bool IsPrivate(IPAddress? address) {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork) {
            return bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                   || bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        }

        return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
    }

    public static bool IsPrivate(string? address) => TryParse(address, out var ip) && IsPrivate(ip);

    public static bool IsLoopback(IPAddress? address)
        => address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    /// <summary>
    /// The client's address for rate limits, lockouts and logs. Only a loopback peer (the tunnel in this container)
    /// may name the visitor, through <paramref name="trustedHeaderValue"/> (the value of the configured header, or
    /// null when that header is not configured or absent). Returns the normalized address text.
    /// </summary>
    public static string ClientAddress(IPAddress? peer, string? trustedHeaderValue) {
        if (IsLoopback(peer) && TryParse(trustedHeaderValue?.Trim(), out var visitor)) {
            return Normalize(visitor);
        }

        return peer is null ? "?" : Normalize(peer);
    }

    /// <summary>True when any proxy header is present (non-empty) in a request.</summary>
    public static bool CameThroughProxy(Func<string, string?> header) {
        foreach (var name in ProxyHeaders) {
            if (!string.IsNullOrWhiteSpace(header(name))) return true;
        }

        return false;
    }

    /// <summary>
    /// "192.168.1.0/24, 10.50.0.0/24" (IPv4/IPv6 CIDRs or single addresses). Null for an empty or invalid list: the
    /// caller then treats every private address (IsPrivate) as private.
    /// </summary>
    public static IReadOnlyList<(IPAddress Network, int Bits)>? ParseNetworks(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var list = new List<(IPAddress, int)>();
        foreach (var raw in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)) {
            var parts = raw.Split('/');
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var network)) return null;
            if (network.IsIPv4MappedToIPv6) network = network.MapToIPv4();
            var max = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var bits = max;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out bits) || bits < 0 || bits > max)) return null;
            list.Add((network, bits));
        }

        return list.Count == 0 ? null : list;
    }

    public static bool InNetworks(IPAddress address, IReadOnlyList<(IPAddress Network, int Bits)> networks) {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        foreach (var (network, bits) in networks) {
            var net = network.GetAddressBytes();
            if (net.Length != bytes.Length) continue;
            var whole = bits / 8;
            var rest = bits % 8;
            var match = true;
            for (var i = 0; i < whole && match; i++) match = net[i] == bytes[i];
            if (match && rest > 0) {
                var mask = (byte) (0xff << (8 - rest));
                match = (net[whole] & mask) == (bytes[whole] & mask);
            }

            if (match) return true;
        }

        return false;
    }

    /// <summary>
    /// The address the client is told to connect to. <paramref name="publicIp"/> null (no public address configured
    /// or resolvable) always gives <paramref name="configuredIp"/>. Otherwise a client inside
    /// <paramref name="privateNetworks"/> (or, when that is null, any private address) gets the configured address,
    /// and everyone else (an unknown address included) the public one. Loopback is always private (local bots).
    /// </summary>
    public static string Advertise(string configuredIp, string? clientAddress, string? publicIp,
                                   IReadOnlyList<(IPAddress Network, int Bits)>? privateNetworks) {
        if (string.IsNullOrEmpty(publicIp)) return configuredIp;
        if (TryParse(clientAddress, out var client)) {
            if (IsLoopback(client)) return configuredIp;
            var inside = privateNetworks is null ? IsPrivate(client) : InNetworks(client, privateNetworks);
            if (inside) return configuredIp;
        }

        return publicIp;
    }

    /// <summary>"http://10.50.0.75:12369/V_r1" with host "203.0.113.9" -> "http://203.0.113.9:12369/V_r1". Unchanged
    /// when the URL is not absolute http(s) or the host is not an IP address.</summary>
    public static string? WithHost(string? url, string? host) {
        if (url is null || string.IsNullOrEmpty(host) || !IPAddress.TryParse(host, out var ip)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return url;
        var builder = new UriBuilder(uri) { Host = ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString() };
        var text = builder.Uri.AbsoluteUri;
        // UriBuilder adds "/" to a bare authority; keep the original's form.
        return !uri.AbsolutePath.Equals("/", StringComparison.Ordinal) || url.EndsWith('/') ? text : text.TrimEnd('/');
    }

    /// <summary>The first IPv4 address in a list (DNS answers), or null.</summary>
    public static string? FirstIPv4(IEnumerable<IPAddress> addresses) {
        foreach (var address in addresses) {
            var a = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any)) {
                return a.ToString();
            }
        }

        return null;
    }

    private static bool TryParse(string? text, out IPAddress address) {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.StartsWith('[') && value.EndsWith(']')) value = value[1..^1];
        if (!IPAddress.TryParse(value, out var parsed)) return false;
        address = parsed;
        return true;
    }

    private static string Normalize(IPAddress address)
        => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
}
