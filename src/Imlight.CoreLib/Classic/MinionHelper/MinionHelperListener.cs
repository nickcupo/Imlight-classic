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
 * MINION HELPER LISTENER
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the TCP port the Minion Helper app connects to. The stock 2009-era
 * client has no screen for choosing another participant's card, so a Myth
 * wizard picks their minion's card and target in a small Mac window instead.
 * The protocol is one JSON object per line (UTF-8, at most 8 KB a line):
 *
 *   helper -> server
 *     {"op":"pair","code":"482177"}            trade a ".minions" chat code for a token
 *     {"op":"hello","version":1,"token":"..."} reconnect with a saved token
 *     {"op":"refresh"}                         send the current state again
 *     {"op":"control","enabled":true}          take (or give back) the minions this duel
 *     {"op":"order","duel":D,"round":R,"minion":M,"request":N,
 *      "move":"cast"|"pass"|"ai"|"undo","card":I,"target":S}   S = sigil slot, -1 for none
 *     {"op":"ping"}
 *   server -> helper
 *     {"op":"paired","token":"..."}  {"op":"welcome","version":1}  {"op":"pong"}
 *     {"op":"error","reason":"..."}  {"op":"state",...}  (see CombatDuelComponent.MinionHelper.cs)
 *
 * CLASSIC: the same port answers plain HTTP for the players' launcher: the
 * client patches (GET /classic/..., Classic/ClientPatches/ClientPatchFiles.cs)
 * and the launcher login (POST /launcher/login, Classic/Launcher/LauncherLogin.cs).
 *
 * USAGE EXAMPLE:
 * MinionHelperListener.StartOnce();   // GameServer start-up; [Classic] MinionHelperPort
 *
 * CLASSIC (go-live, docs/runbooks/go-live.md):
 *   [Classic] LauncherProxyPort = 12091     a second listener on 127.0.0.1 only, for the
 *       Cloudflare tunnel in this container: POST /launcher/login and nothing else.
 *   [Classic] TrustedProxyHeader = CF-Connecting-IP   the visitor's address on that
 *       listener (lockouts and logs); only ever read from a loopback peer.
 *   [Classic] PublicHttpDownloadsOnly = true   on this (port-forwarded) port, a client
 *       outside the private networks gets only GET /classic/... and /launcher/...:
 *       the password login is refused ("https-required") and the helper is closed.
 *   All three are off by default.
 *
 * NOTE:
 * Plain TCP for a home LAN. The token is the only secret; it is checked on
 * every connection, and every order is re-checked by the duel.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Imlight.Common;
using Imlight.CoreLib.Classic.ClientPatches;
using Imlight.CoreLib.Shared.Networking;
using static Imlight.CoreLib.Shared.Packets.COMBAT_106_PROTOCOL;

namespace Imlight.CoreLib.Classic.MinionHelper;

/// <summary>
/// One helper's conversation, independent of the socket so tests can drive it.
/// </summary>
internal sealed class MinionHelperSession : IMinionHelperConnection {
    internal const int Version = 1;
    internal const int MaxLineBytes = 8 * 1024;
    internal const int MaxBadCodes = 5;

    private readonly Action<string> _send;
    private readonly MinionHelperHub _hub;
    private readonly MinionHelperPairing _pairing;
    private readonly string _address; // CLASSIC (M6): wrong codes count per address across connections
    private int _badCodes;

    internal ulong AccountId { get; private set; }

    internal MinionHelperSession(Action<string> send, MinionHelperHub hub, MinionHelperPairing pairing, string address = null) {
        _send = send;
        _hub = hub;
        _pairing = pairing;
        _address = address;
    }

    public void Send(string line) {
        try {
            _send(line);
        } catch (Exception) {
            // The socket closed; the read loop notices and detaches.
        }
    }

    /// <summary>Handles one line. False means close the connection.</summary>
    internal bool HandleLine(string line) {
        JsonElement root;
        try {
            using var document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
        } catch (JsonException) {
            Error("not JSON");
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("op", out var opElement)
            || opElement.ValueKind != JsonValueKind.String) {
            Error("no op");
            return false;
        }

        var op = opElement.GetString();
        switch (op) {
            case "ping":
                Send("""{"op":"pong"}""");
                return true;
            case "pair":
                return Pair(String(root, "code"));
            case "hello":
                return Hello(String(root, "token"));
        }

        if (AccountId == 0) {
            Error("pair or hello first");
            return false;
        }

        switch (op) {
            case "refresh":
                Route(new MSG_MINIONHELPERORDER { Query = true }, 0);
                return true;
            case "control":
                var enabled = root.TryGetProperty("enabled", out var flag) && flag.ValueKind == JsonValueKind.True;
                if (!_hub.Forward(AccountId, new MSG_MINIONHELPERCONTROL { Enabled = enabled })) Send(MinionHelperHub.Offline());
                return true;
            case "order":
                return Order(root);
            default:
                Error("unknown op");
                return true;
        }
    }

    /// <summary>The connection closed.</summary>
    internal void Closed() {
        if (AccountId != 0) _hub.DetachHelper(AccountId, this);
        AccountId = 0;
    }

    private bool Pair(string code) {
        if (AccountId != 0) {
            Error("already paired");
            return true;
        }

        if (!_pairing.TryRedeem(code, _address, out var token, out var accountId)) {
            _badCodes++;
            Error("That code is wrong or expired. Type .minions in game chat for a new one.");
            return _badCodes < MaxBadCodes;
        }

        Send(JsonSerializer.Serialize(new { op = "paired", token }));
        Attach(accountId);
        return true;
    }

    private bool Hello(string token) {
        if (AccountId != 0) return true;
        if (!_pairing.TryResolve(token, out var accountId)) {
            Error("unpaired");
            return false;
        }

        Attach(accountId);
        return true;
    }

    private void Attach(ulong accountId) {
        AccountId = accountId;
        Send(JsonSerializer.Serialize(new { op = "welcome", version = Version }));
        _hub.AttachHelper(accountId, this);
    }

    private bool Order(JsonElement root) {
        var request = UInt(root, "request");
        var move = String(root, "move");
        byte moveType;
        switch (move) {
            case "cast": moveType = (byte) Imlight.CoreLib.Game.Combat.CombatMoveType.Attack; break;
            case "pass": moveType = (byte) Imlight.CoreLib.Game.Combat.CombatMoveType.Pass; break;
            case "undo": moveType = (byte) Imlight.CoreLib.Game.Combat.CombatMoveType.ChangeMind; break;
            case "ai": moveType = OwnedMinionAiMove; break;
            default:
                Ack(request, "InvalidMove");
                return true;
        }

        var card = root.TryGetProperty("card", out var cardElement) && cardElement.TryGetInt32(out var c) && c is >= 0 and < 256 ? (byte) c : (byte) 0;
        var target = root.TryGetProperty("target", out var targetElement) && targetElement.TryGetInt32(out var t) && t >= 0 ? (uint) t : uint.MaxValue;
        var message = new MSG_MINIONHELPERORDER {
            DuelID = ULong(root, "duel"), Round = (int) Math.Clamp(Long(root, "round"), int.MinValue, int.MaxValue),
            MinionID = ULong(root, "minion"), RequestID = request,
            MoveType = moveType, SpellSelection = card, SpellTarget = target,
        };
        Route(message, request);
        return true;
    }

    private void Route(MSG_MINIONHELPERORDER message, uint request) {
        if (!_hub.Forward(AccountId, message)) {
            if (request != 0) Ack(request, "Offline");
            Send(MinionHelperHub.Offline());
        }
    }

    private void Ack(uint request, string status)
        => Send(JsonSerializer.Serialize(new { op = "ack", request, accepted = false, status }));

    private void Error(string reason) => Send(JsonSerializer.Serialize(new { op = "error", reason }));

    private static string String(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static ulong ULong(JsonElement root, string name) {
        if (!root.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), out var parsed) ? parsed : 0;
    }

    private static long Long(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;

    private static uint UInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number) ? number : 0;
}

internal static class MinionHelperListener {
    /// <summary>Default port; [Classic] MinionHelperPort overrides it, and 0 turns the helper port off.</summary>
    internal const int DefaultPort = 12090;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(75);
    private static int s_started;

    /// <summary>The configured port, or 0 when the helper is off (or owned-minion control is).</summary>
    internal static int ConfiguredPort {
        get {
            // CLASSIC: the same port serves the client patches (/classic/...), so it stays on for them alone.
            if (!EnhancedGameplaySettings.Enabled && ClientPatchFiles.ConfiguredRoot is null
                && Launcher.LauncherPatchServer.ConfiguredPort == 0) return 0; // CLASSIC: also KingsIsle's launcher
            var value = ConfigurationManager.Settings["Classic.MinionHelperPort"].AsString();
            if (string.IsNullOrWhiteSpace(value)) return DefaultPort;
            return int.TryParse(value, out var port) && port is > 0 and < 65536 ? port : 0;
        }
    }

    /// <summary>Starts the listener the first time it is called (several game servers may call it).</summary>
    internal static void StartOnce() {
        if (Interlocked.Exchange(ref s_started, 1) == 1) return;
        var port = ConfiguredPort;
        if (port == 0) {
            Logger.Information("Minion Helper port is off.");
            return;
        }

        TcpListener listener;
        try {
            listener = new TcpListener(TcpListenerActor.ParseListenAddress(ConfigurationManager.GetSetting("Network.ListenAddress")), port);
            listener.Start();
        } catch (Exception ex) {
            Logger.Error("Minion Helper could not listen on port {0}: {1}", Logger.Args(port, ex.Message));
            return;
        }

        Logger.Information("Minion Helper listening on port {0}.", Logger.Args(port));
        _ = Task.Run(() => AcceptLoop(listener, viaProxy: false));
        StartProxyListener();
    }

    /// <summary>CLASSIC (go-live): [Classic] LauncherProxyPort, or 0 (off).</summary>
    internal static int ProxyPort {
        get {
            var value = ConfigurationManager.GetSetting("Classic.LauncherProxyPort");
            return int.TryParse(value?.Trim(), out var port) && port is > 0 and < 65536 ? port : 0;
        }
    }

    /// <summary>CLASSIC (go-live): the header naming the visitor behind the local tunnel, or null.</summary>
    internal static string TrustedProxyHeader
        => ConfigurationManager.GetSetting("Classic.TrustedProxyHeader")?.Trim() is { Length: > 0 } name ? name : null;

    private static readonly Lazy<bool> s_downloadsOnly = new(() => Imlight.CoreLib.Auth.SecuritySettings.Bool("Classic.PublicHttpDownloadsOnly", false));

    /// <summary>CLASSIC (go-live): the loopback-only listener the tunnel connects to.</summary>
    private static void StartProxyListener() {
        var port = ProxyPort;
        if (port == 0) return;
        try {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            Logger.Information("Launcher login for the tunnel listening on 127.0.0.1:{0} (header {1}).",
                Logger.Args(port, TrustedProxyHeader ?? "none"));
            _ = Task.Run(() => AcceptLoop(listener, viaProxy: true));
        } catch (Exception ex) {
            Logger.Error("Launcher login for the tunnel could not listen on 127.0.0.1:{0}: {1}", Logger.Args(port, ex.Message));
        }
    }

    /// <summary>What one connection may use (go-live). Pure, for tests.</summary>
    internal enum Exposure { Full, DownloadsOnly, ProxyLogin }

    internal static Exposure ExposureFor(bool viaProxy, IPAddress peer, bool downloadsOnly)
        => viaProxy ? Exposure.ProxyLogin
            : downloadsOnly && !Imlight.Classic.Net.PublicAccess.IsPrivate(peer) ? Exposure.DownloadsOnly : Exposure.Full;

    /// <summary>Whether a GET path is one an outside client may fetch on the downloads-only port.</summary>
    internal static bool PublicDownloadPath(string path)
        => path.StartsWith(ClientPatchFiles.Prefix, StringComparison.Ordinal) || path.StartsWith("/launcher/", StringComparison.Ordinal);

    private static async Task AcceptLoop(TcpListener listener, bool viaProxy) {
        while (true) {
            TcpClient client;
            try {
                client = await listener.AcceptTcpClientAsync();
            } catch (Exception ex) {
                Logger.Error("Minion Helper accept failed: {0}", Logger.Args(ex.Message));
                await Task.Delay(1000);
                continue;
            }

            // CLASSIC (M7): at most MaxConnections at once and MaxPerAddress per client address (loopback exempt: a
            // local tunnel or proxy in front of the page shows every visitor as loopback).
            var address = Imlight.Classic.Net.GameSessionKeys.NormalizeAddress(
                (client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString()) ?? "?";
            var refusal = Imlight.Classic.Net.ConnectionLimits.Refuse(address, s_perAddress.GetValueOrDefault(address),
                Volatile.Read(ref s_open), MaxPerAddress, MaxConnections, limitLoopback: false);
            if (refusal is not null) {
                if (Interlocked.Increment(ref s_refused) % 100 == 1) {
                    Logger.Warning("Minion Helper port refused {0}: {1}", Logger.Args(address, refusal));
                }

                client.Dispose();
                continue;
            }

            Interlocked.Increment(ref s_open);
            s_perAddress.AddOrUpdate(address, 1, (_, n) => n + 1);
            _ = Task.Run(async () => {
                try {
                    await Serve(client, viaProxy);
                } finally {
                    Interlocked.Decrement(ref s_open);
                    if (s_perAddress.AddOrUpdate(address, 0, (_, n) => n - 1) <= 0) {
                        s_perAddress.TryRemove(new KeyValuePair<string, int>(address, 0));
                    }
                }
            });
        }
    }

    private const int MaxConnections = 128;
    private const int MaxPerAddress = 16;
    private static int s_open;
    private static long s_refused;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> s_perAddress = new(StringComparer.Ordinal);

    private static async Task Serve(TcpClient client, bool viaProxy) {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        var remote = client.Client.RemoteEndPoint?.ToString();
        var peer = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        var exposure = ExposureFor(viaProxy, peer, s_downloadsOnly.Value); // CLASSIC (go-live)
        var outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });
        var session = new MinionHelperSession(line => outbox.Writer.TryWrite(line), MinionHelperHub.Shared, MinionHelperPairing.Shared,
            Imlight.Classic.Net.GameSessionKeys.NormalizeAddress((client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString()));
        try {
            // A browser (the helper page or its WebSocket) starts with "GET "; the line protocol starts with "{".
            var head = new byte[MinionHelperSession.MaxLineBytes];
            var filled = await ReadSomeAsync(stream, head, 0);
            if (filled == 0) return;
            if (filled >= 4 && head[0] == 'G' && head[1] == 'E' && head[2] == 'T' && head[3] == ' ') {
                if (exposure == Exposure.ProxyLogin) {
                    await Respond(stream, "404 Not Found", "text/plain", "Not found"u8.ToArray());
                } else {
                    await ServeHttp(stream, head, filled, session, outbox, exposure);
                }
            } else if (filled >= 5 && head[0] == 'P' && head[1] == 'O' && head[2] == 'S' && head[3] == 'T' && head[4] == ' ') {
                await ServePost(stream, head, filled, peer, exposure);
            } else if (exposure != Exposure.Full) {
                return; // CLASSIC (go-live): no helper line protocol from outside or through the tunnel
            } else if (!EnhancedGameplaySettings.Enabled) {
                return; // the port is open for the client patches only
            } else {
                await ServeLines(stream, head, filled, session, outbox);
            }
        } catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException
                                         or WebSocketException) {
            // Gone or idle; nothing to report.
        } finally {
            outbox.Writer.TryComplete();
            if (session.AccountId != 0) Logger.Debug("Minion Helper from {0} left.", Logger.Args(remote));
            session.Closed();
        }
    }

    private static async Task<int> ReadSomeAsync(Stream stream, byte[] buffer, int offset) {
        using var idle = new CancellationTokenSource(IdleTimeout);
        return await stream.ReadAsync(buffer.AsMemory(offset), idle.Token);
    }

    private static async Task ServeLines(NetworkStream stream, byte[] buffer, int filled, MinionHelperSession session, Channel<string> outbox) {
        var writer = Task.Run(async () => {
            await foreach (var line in outbox.Reader.ReadAllAsync()) {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await stream.WriteAsync(bytes);
            }
        });
        try {
            while (true) {
                int newline;
                var keep = true;
                while (keep && (newline = Array.IndexOf(buffer, (byte) '\n', 0, filled)) >= 0) {
                    var line = Encoding.UTF8.GetString(buffer, 0, newline).Trim();
                    Buffer.BlockCopy(buffer, newline + 1, buffer, 0, filled - newline - 1);
                    filled -= newline + 1;
                    if (line.Length > 0) keep = session.HandleLine(line);
                }

                if (!keep || filled == buffer.Length) break; // closed by the protocol, or a line over 8 KB
                var read = await ReadSomeAsync(stream, buffer, filled);
                if (read == 0) break;
                filled += read;
            }
        } finally {
            outbox.Writer.TryComplete();
            await Task.WhenAny(writer, Task.Delay(1000)); // let a last error line out
        }
    }

    private static async Task ServeHttp(NetworkStream stream, byte[] buffer, int filled, MinionHelperSession session, Channel<string> outbox,
                                        Exposure exposure = Exposure.Full) {
        int end;
        while ((end = IndexOf(buffer, filled, "\r\n\r\n"u8)) < 0) {
            if (filled == buffer.Length) return;
            var read = await ReadSomeAsync(stream, buffer, filled);
            if (read == 0) return;
            filled += read;
        }

        var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        var parts = lines[0].Split(' ');
        var path = parts.Length > 1 ? parts[1].Split('?')[0] : "/";
        var headers = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1)) {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (exposure == Exposure.DownloadsOnly && !PublicDownloadPath(path)) {
            // CLASSIC (go-live): the helper page and its socket stay on the home network.
            await Respond(stream, "404 Not Found", "text/plain", "Not found"u8.ToArray());
            return;
        }

        if (path.StartsWith(ClientPatchFiles.Prefix, StringComparison.Ordinal)) {
            await ServePatchFile(stream, path, headers);
            return;
        }

        // CLASSIC: KingsIsle's own launcher (Classic/Launcher): its news page, art and nothing-to-patch file list.
        if (path.StartsWith("/launcher/", StringComparison.Ordinal)) {
            await ServeLauncher(stream, path, headers);
            return;
        }

        if (!EnhancedGameplaySettings.Enabled) {
            await Respond(stream, "404 Not Found", "text/plain", "Not found"u8.ToArray());
            return;
        }

        if (path == "/ws" && headers.TryGetValue("Upgrade", out var upgrade) && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)
            && headers.TryGetValue("Sec-WebSocket-Key", out var key)) {
            // Only the helper page itself (served from this host and port) may open the socket.
            if (headers.TryGetValue("Origin", out var origin) && headers.TryGetValue("Host", out var host)
                && !string.Equals(origin, "http://" + host, StringComparison.OrdinalIgnoreCase)) {
                await Respond(stream, "403 Forbidden", "text/plain", "Forbidden"u8.ToArray());
                return;
            }

            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var handshake = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake));
            using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions {
                IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20),
            });
            await ServeWebSocket(socket, session, outbox);
            return;
        }

        if (path is "/" or "/index.html") {
            await Respond(stream, "200 OK", "text/html; charset=utf-8", Page.Value);
            return;
        }

        await Respond(stream, "404 Not Found", "text/plain", "Not found"u8.ToArray());
    }

    /// <summary>CLASSIC: POST /launcher/login (Classic/Launcher/LauncherLogin.cs); a small JSON body.</summary>
    private static async Task ServePost(NetworkStream stream, byte[] buffer, int filled, IPAddress peer, Exposure exposure) {
        int end;
        while ((end = IndexOf(buffer, filled, "\r\n\r\n"u8)) < 0) {
            if (filled == buffer.Length) return;
            var read = await ReadSomeAsync(stream, buffer, filled);
            if (read == 0) return;
            filled += read;
        }

        var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        var parts = lines[0].Split(' ');
        var path = parts.Length > 1 ? parts[1].Split('?')[0] : "/";
        var length = -1;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1)) {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            if (line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[(colon + 1)..].Trim(), out var parsed)) length = parsed;
        }

        if (path != "/launcher/login") {
            await Respond(stream, "404 Not Found", "text/plain", "Not found"u8.ToArray());
            return;
        }

        // CLASSIC (go-live): passwords cross the internet only over HTTPS (the tunnel's listener).
        if (exposure == Exposure.DownloadsOnly) {
            Logger.Information("Launcher: plain-HTTP login from {0} refused (outside the home network; use HTTPS)",
                Logger.Args(Imlight.Classic.Net.PublicAccess.ClientAddress(peer, null)));
            await Respond(stream, "403 Forbidden", "application/json", "{\"ok\":false,\"error\":\"https-required\"}"u8.ToArray());
            return;
        }

        var (address, keyAddress) = LoginAddresses(exposure, peer, headers);

        var bodyStart = end + 4;
        if (length is < 0 or > 4096 || bodyStart + length > buffer.Length) {
            await Respond(stream, "400 Bad Request", "application/json", "{\"ok\":false,\"error\":\"bad-request\"}"u8.ToArray());
            return;
        }

        while (filled < bodyStart + length) {
            var read = await ReadSomeAsync(stream, buffer, filled);
            if (read == 0) return;
            filled += read;
        }

        var body = Encoding.UTF8.GetString(buffer, bodyStart, length);
        string answer;
        try {
            answer = await Task.Run(() => Launcher.LauncherLogin.Shared.Handle(body, address, keyAddress));
        } catch (Exception ex) {
            // CLASSIC: a failing login must say so in the log (it used to drop the connection silently).
            // Exception messages can include account input or database connection details.
            Logger.Error("Launcher login failed with {0}", Logger.Args(ex.GetType().Name));
            await Respond(stream, "500 Internal Server Error", "application/json", "{\"ok\":false,\"error\":\"server-error\"}"u8.ToArray());
            return;
        }

        await Respond(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(answer));
    }

    /// <summary>
    /// CLASSIC (go-live): (the address for lockouts and logs, the address the session key is bound to). Through the
    /// tunnel: the visitor named by the trusted header, and the loopback peer (the key stays unbound, see
    /// LauncherLogin.Handle). Directly: the peer for both, as before.
    /// </summary>
    internal static (string Address, string KeyAddress) LoginAddresses(Exposure exposure, IPAddress peer,
                                                                        IReadOnlyDictionary<string, string> headers) {
        var direct = peer is null ? "?" : (peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer).ToString();
        if (exposure != Exposure.ProxyLogin) return (direct, null);
        var header = TrustedProxyHeader;
        var visitor = header is not null && headers.TryGetValue(header, out var value) ? value : null;
        return (Imlight.Classic.Net.PublicAccess.ClientAddress(peer, visitor), direct);
    }

    /// <summary>CLASSIC: what KingsIsle's own launcher fetches from us (LauncherPatchServer, LauncherNewsPage).</summary>
    private static async Task ServeLauncher(NetworkStream stream, string path,
                                            System.Collections.Generic.Dictionary<string, string> headers) {
        if (path == "/launcher/" + Launcher.LauncherFileList.FileName) {
            await Respond(stream, "200 OK", "application/octet-stream", Launcher.LauncherFileList.Bytes);
        } else if (path == "/launcher/news" || path.StartsWith("/launcher/news/", StringComparison.Ordinal)) {
            // The launcher also opens <NewsURL>/New/patchComplete when it has patched.
            await Respond(stream, "200 OK", "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(Launcher.LauncherNewsPage.Render(ClientPatchFiles.ConfiguredRoot)));
        } else if (path.StartsWith(Launcher.LauncherNewsPage.ArtPrefix, StringComparison.Ordinal)) {
            var root = ClientPatchFiles.ConfiguredRoot;
            var manifest = root is null ? null : Path.Combine(root, "manifest.json");
            var art = Launcher.LauncherNewsPage.ArtUrls(manifest is not null && File.Exists(manifest) ? await File.ReadAllTextAsync(manifest) : null);
            var name = path[Launcher.LauncherNewsPage.ArtPrefix.Length..];
            var file = art.TryGetValue(name, out var url) ? ClientPatchFiles.Resolve(root, url) : null;
            if (file is null) await Respond(stream, "404 Not Found", "text/plain", "Not found"u8.ToArray());
            else await Respond(stream, "200 OK", "image/png", await File.ReadAllBytesAsync(file));
        } else if (path == "/launcher/blank" || path.StartsWith("/launcher/blank/", StringComparison.Ordinal)) {
            // Every other page the launcher may open (home, account, error, metrics, fail-safe check).
            await Respond(stream, "200 OK", "text/html; charset=utf-8", "<html><body></body></html>"u8.ToArray());
        } else {
            await Respond(stream, "404 Not Found", "text/plain", "Not found"u8.ToArray());
        }
    }

    /// <summary>CLASSIC: one published client-patch file, whole or from a byte offset (resumed downloads).</summary>
    private static async Task ServePatchFile(NetworkStream stream, string path,
                                             System.Collections.Generic.Dictionary<string, string> headers) {
        var file = ClientPatchFiles.Resolve(ClientPatchFiles.ConfiguredRoot, path);
        if (file is null) {
            await Respond(stream, "404 Not Found", "text/plain", "Not found"u8.ToArray());
            return;
        }

        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, true);
        var length = input.Length;
        headers.TryGetValue("Range", out var rangeHeader);
        var range = ClientPatchFiles.ParseRange(rangeHeader, length);
        if (range is { Start: -1 }) {
            var refuse = $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(refuse));
            return;
        }

        var (start, end) = range ?? (0, length - 1);
        var count = length == 0 ? 0 : end - start + 1;
        var status = range is null ? "200 OK" : "206 Partial Content";
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {ClientPatchFiles.ContentType(file)}\r\nContent-Length: {count}\r\n" +
                   (range is null ? "" : $"Content-Range: bytes {start}-{end}/{length}\r\n") +
                   "Accept-Ranges: bytes\r\nCache-Control: no-cache\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        input.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[1 << 16];
        while (count > 0) {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int) Math.Min(buffer.Length, count)));
            if (read == 0) break;
            using var stall = new CancellationTokenSource(IdleTimeout);
            await stream.WriteAsync(buffer.AsMemory(0, read), stall.Token);
            count -= read;
        }
    }

    private static async Task ServeWebSocket(WebSocket socket, MinionHelperSession session, Channel<string> outbox) {
        var writer = Task.Run(async () => {
            await foreach (var line in outbox.Reader.ReadAllAsync()) {
                if (socket.State != WebSocketState.Open) break;
                await socket.SendAsync(Encoding.UTF8.GetBytes(line), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        });
        var buffer = new byte[MinionHelperSession.MaxLineBytes];
        try {
            while (socket.State == WebSocketState.Open) {
                var filled = 0;
                ValueWebSocketReceiveResult result;
                do {
                    using var idle = new CancellationTokenSource(IdleTimeout);
                    result = await socket.ReceiveAsync(buffer.AsMemory(filled), idle.Token);
                    filled += result.Count;
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (!result.EndOfMessage && filled == buffer.Length) return; // over 8 KB
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;
                var line = Encoding.UTF8.GetString(buffer, 0, filled).Trim();
                if (line.Length > 0 && !session.HandleLine(line)) break;
            }
        } finally {
            outbox.Writer.TryComplete();
            await Task.WhenAny(writer, Task.Delay(1000));
            if (socket.State == WebSocketState.Open) {
                try {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                } catch (Exception) {
                    // Already gone.
                }
            }
        }
    }

    private static async Task Respond(NetworkStream stream, string status, string type, byte[] body) {
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                   "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
    }

    private static int IndexOf(byte[] buffer, int length, ReadOnlySpan<byte> pattern)
        => buffer.AsSpan(0, length).IndexOf(pattern);

    /// <summary>The helper page (Classic/MinionHelper/MinionHelper.html), embedded in this assembly.</summary>
    private static readonly Lazy<byte[]> Page = new(() => {
        using var resource = typeof(MinionHelperListener).Assembly.GetManifestResourceStream("MinionHelper.html");
        if (resource is null) return "Minion Helper page missing from this build."u8.ToArray();
        using var copy = new MemoryStream();
        resource.CopyTo(copy);
        return copy.ToArray();
    });
}
