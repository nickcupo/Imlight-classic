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
 * PUBLIC GAME ADDRESS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (go-live): the r806919 client does no name lookups; after login and
 * on every server transfer it connects to the IP address the server names,
 * and its in-game patcher downloads from the URL the patch service names. A
 * friend outside the home network must be told the public address, the
 * owner's LAN client the private one (and the friend's own network guard
 * allows only the address its launcher resolved).
 *
 * SETTINGS:
 * [Game Server] PublicGameServerHost  an IPv4 address or a host name (DNS-only
 *   A record, e.g. play.gardenpay.com) resolved here at most once a minute;
 *   empty (default) = off, every client gets GameServerIP as before.
 * [Game Server] PrivateClientNetworks  CIDRs whose clients get GameServerIP
 *   (e.g. 192.168.1.0/24,10.50.0.0/24); empty = every private address.
 *
 * NOTE:
 * A failed lookup keeps the last good address; with none, the configured one.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/08/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Imlight.Classic.Net;
using Imlight.Common;

namespace Imlight.CoreLib.Classic;

internal static class PublicGameAddress {
    private static readonly TimeSpan s_refresh = TimeSpan.FromMinutes(1);
    private static readonly object s_gate = new();
    private static string? s_last;
    private static DateTime s_checkedUtc = DateTime.MinValue;
    private static int s_refreshing;

    private static readonly Lazy<string?> s_host = new(() => Auth.SecuritySettings.Text("Game Server.PublicGameServerHost")?.Trim() is { Length: > 0 } h ? h : null);
    private static readonly Lazy<IReadOnlyList<(IPAddress Network, int Bits)>?> s_networks = new(() => {
        var text = Auth.SecuritySettings.Text("Game Server.PrivateClientNetworks");
        var parsed = PublicAccess.ParseNetworks(text);
        if (parsed is null && !string.IsNullOrWhiteSpace(text)) {
            Logger.Warning("Game Server.PrivateClientNetworks '{0}' is not a CIDR list; every private address counts as private.",
                Logger.Args(text));
        }

        return parsed;
    });

    internal static bool Enabled => s_host.Value is not null;

    /// <summary>The IP to give a client at <paramref name="clientAddress"/> in place of <paramref name="configuredIp"/>.</summary>
    internal static string For(string configuredIp, string? clientAddress) {
        if (!Enabled) return configuredIp;
        return PublicAccess.Advertise(configuredIp, clientAddress, Current(), s_networks.Value);
    }

    /// <summary>A patch URL with its host replaced the same way (unchanged for private clients).</summary>
    internal static string? Url(string? url, string? clientAddress) {
        if (!Enabled || url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var chosen = For(uri.Host, clientAddress);
        return chosen == uri.Host ? url : PublicAccess.WithHost(url, chosen);
    }

    /// <summary>The public IPv4 now (cached; a lookup runs in the background once the cache is a minute old).</summary>
    internal static string? Current() {
        var host = s_host.Value;
        if (host is null) return null;
        if (IPAddress.TryParse(host, out var literal)) return literal.ToString();
        bool stale;
        string? last;
        lock (s_gate) {
            last = s_last;
            stale = DateTime.UtcNow - s_checkedUtc > s_refresh;
        }

        if (last is null) {
            Refresh(host); // the first time, wait for it: a login must not get the private address by accident
            lock (s_gate) return s_last;
        }

        if (stale && Interlocked.Exchange(ref s_refreshing, 1) == 0) {
            ThreadPool.QueueUserWorkItem(_ => {
                try { Refresh(host); } finally { Volatile.Write(ref s_refreshing, 0); }
            });
        }

        return last;
    }

    private static void Refresh(string host) {
        string? found = null;
        try {
            found = PublicAccess.FirstIPv4(Dns.GetHostAddresses(host));
        } catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException) {
            Logger.Warning("Public game address: {0} could not be resolved ({1}); keeping {2}.",
                Logger.Args(host, ex.GetType().Name, s_last ?? "the configured address"));
        }

        lock (s_gate) {
            s_checkedUtc = DateTime.UtcNow;
            if (found is null || found == s_last) return;
            Logger.Information("Public game address: {0} = {1} (was {2}).", Logger.Args(host, found, s_last ?? "unset"));
            s_last = found;
        }
    }
}
