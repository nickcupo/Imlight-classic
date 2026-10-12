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
 * ADMIN DASHBOARD
 * ========================================================================
 *
 * PURPOSE:
 * A small private web page served by the game server on the LAN for the
 * owner: who is online and where, uptime and recent errors, fights in
 * progress, backups (list, safe backup), safe restart, broadcast, and the
 * [Classic] switches.
 *
 * USAGE EXAMPLE:
 * [Classic] AdminDashboardPort = 12380   ; 0 turns it off
 * open http://<server>:12380/ and log in with an Administrator account.
 *
 * NOTE:
 * Built on System.Net.HttpListener (no extra runtime dependency). Login
 * checks the account's real password hash and needs AuthLevel
 * Administrator; Classic.AnyPasswordLogin never applies here. Sessions are
 * random 256-bit cookies (HttpOnly, SameSite=Strict, 12 hours); every POST
 * also needs the X-W101C header, which a cross-site form cannot send.
 * Failed logins are limited per address. Plain HTTP: for the LAN only.
 * CLASSIC (go-live): the admin page and API answer only private addresses and
 * never a proxied request (a tunnel header present). [Classic]
 * FriendPortalProxyPort = 12381 adds a listener on 127.0.0.1 for the Cloudflare
 * tunnel that serves the friend page (/friends/) and nothing else.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Classic;
using Imlight.Classic.Admin;
using Imlight.Common;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic.Admin;

/// <summary>
/// The owner's admin web page and its JSON API.
/// </summary>
public static class AdminDashboard {

    private const string CookieName = "w101c_admin";
    private const string CsrfHeader = "X-W101C";
    private const int MaxBodyBytes = 16 * 1024;
    private static readonly TimeSpan s_sessionLifetime = TimeSpan.FromHours(12);
    private static readonly ConcurrentDictionary<string, (string User, DateTime ExpiresUtc)> s_sessions = new();
    private static readonly ConcurrentDictionary<string, Queue<DateTime>> s_failedLogins = new();
    private static readonly ConcurrentDictionary<string, Func<object?>> s_sections = new();
    private static readonly JsonSerializerOptions s_json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static HttpListener? s_listener;
    private static HttpListener? s_friendProxy;

    /// <summary>
    /// Lets a feature add its own block to the dashboard state (the Bazaar stock, holiday events, PvP circles).
    /// </summary>
    public static void AddSection(string name, Func<object?> provider) => s_sections[name] = provider;

    /// <summary>Starts the dashboard if [Classic] AdminDashboardPort is set (default 12380).</summary>
    public static void Start() {
        var port = Setting("Classic.AdminDashboardPort") is { } text && int.TryParse(text, out var value) ? value : 12380;
        if (port <= 0) {
            Logger.Information("[ADMIN] Dashboard off (Classic.AdminDashboardPort = 0).");

            return;
        }

        var address = Setting("Classic.AdminDashboardAddress") ?? Setting("Network.ListenAddress") ?? "127.0.0.1";
        var host = address is "0.0.0.0" or "*" ? "+" : address;
        try {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://{host}:{port}/");
            listener.Start();
            s_listener = listener;
            _ = Task.Run(() => AcceptLoop(listener));
            Logger.Information("[ADMIN] Dashboard on http://{Address}:{Port}/ (Administrator accounts only).",
                Logger.Args(address, port));
        }
        catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException or PlatformNotSupportedException) {
            Logger.Error("[ADMIN] The dashboard could not listen on {Address}:{Port}: {Error}",
                Logger.Args(address, port, ex.Message));
        }

        StartFriendProxy();
    }

    /// <summary>
    /// CLASSIC (go-live): the friend page for the Cloudflare tunnel in this container, on 127.0.0.1 only. The tunnel
    /// must send Host 127.0.0.1:&lt;port&gt; (cloudflared originRequest.httpHostHeader); everything but /friends/ is 404.
    /// </summary>
    private static void StartFriendProxy() {
        var port = Setting("Classic.FriendPortalProxyPort") is { } text && int.TryParse(text, out var value) && value is > 0 and < 65536 ? value : 0;
        if (port == 0) return;
        try {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            s_friendProxy = listener;
            _ = Task.Run(() => AcceptLoop(listener, friendsOnly: true));
            Logger.Information("[FRIENDS] Friend page for the tunnel on http://127.0.0.1:{Port}/friends/.", Logger.Args(port));
        }
        catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException or PlatformNotSupportedException) {
            Logger.Error("[FRIENDS] The friend page could not listen on 127.0.0.1:{Port}: {Error}", Logger.Args(port, ex.Message));
        }
    }

    /// <summary>CLASSIC (go-live): whether a request may reach the admin page or API.</summary>
    internal static bool AdminAllowed(IPAddress? peer, Func<string, string?> header)
        => Imlight.Classic.Net.PublicAccess.IsPrivate(peer) && !Imlight.Classic.Net.PublicAccess.CameThroughProxy(header);

    /// <summary>CLASSIC (go-live): the friend-proxy listener's request handling (friend paths only).</summary>
    internal static void HandleFriendProxy(HttpListenerContext context, Func<HttpListenerContext, bool> friends) {
        var response = context.Response;
        try {
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-Frame-Options"] = "DENY";
            response.Headers["Cache-Control"] = "no-store";
            if (!friends(context)) Write(response, 404, "text/plain", "not found");
        }
        catch (Exception) {
            try { response.Abort(); } catch (Exception) { }
        }
    }

    private static async Task AcceptLoop(HttpListener listener, bool friendsOnly = false) {
        while (listener.IsListening) {
            HttpListenerContext context;
            try {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (!listener.IsListening) {
                return;
            }
            catch (Exception ex) {
                Logger.Warning("[ADMIN] Dashboard accept failed: {Error}", Logger.Args(ex.Message));
                continue;
            }

            _ = friendsOnly
                ? Task.Run(() => HandleFriendProxy(context, Friends.FriendPortal.TryHandle))
                : Task.Run(() => Handle(context));
        }
    }

    private static void Handle(HttpListenerContext context) {
        var request = context.Request;
        var response = context.Response;
        try {
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-Frame-Options"] = "DENY";
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Content-Security-Policy"] =
                "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'";

            var path = request.Url?.AbsolutePath ?? "/";
            // CLASSIC: the friend page has its own scoped site sessions, before the Administrator API gate.
            if (Friends.FriendPortal.TryHandle(context)) return;
            // CLASSIC (go-live): the admin page and API never answer the internet or a tunnel.
            if (!AdminAllowed(request.RemoteEndPoint?.Address, name => request.Headers[name])) {
                Write(response, 404, "text/plain", "not found");

                return;
            }
            if (request.HttpMethod == "GET" && path == "/") {
                Write(response, 200, "text/html; charset=utf-8", AdminDashboardPage.Html);

                return;
            }

            if (!path.StartsWith("/api/", StringComparison.Ordinal)) {
                Write(response, 404, "text/plain", "not found");

                return;
            }

            if (request.HttpMethod == "POST" && request.Headers[CsrfHeader] != "1") {
                Json(response, 403, new { error = "missing request header" });

                return;
            }

            if (path == "/api/login" && request.HttpMethod == "POST") {
                Login(context);

                return;
            }

            var user = SessionUser(request);
            if (user is null) {
                Json(response, 401, new { error = "log in first" });

                return;
            }

            switch (path, request.HttpMethod) {
                case ("/api/friends/state", "GET"):
                case ("/api/friends/account", "POST"):
                    Friends.FriendPortal.HandleAdmin(context, path);
                    break;
                case ("/api/state", "GET"):
                    Json(response, 200, State(user));
                    break;
                case ("/api/logout", "POST"):
                    if (request.Cookies[CookieName]?.Value is { } token) {
                        s_sessions.TryRemove(token, out _);
                    }

                    response.Headers.Add("Set-Cookie", $"{CookieName}=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict");
                    Json(response, 200, new { ok = true });
                    break;
                case ("/api/broadcast", "POST"): {
                    var body = ReadBody(request);
                    var text = body.GetProperty("text").GetString()?.Trim() ?? "";
                    if (text.Length is 0 or > 500) {
                        Json(response, 400, new { error = "a message of 1 to 500 characters" });
                        break;
                    }

                    Logger.Information("[ADMIN] {User} broadcast from the dashboard.", Logger.Args(user));
                    Json(response, 200, new { ok = true, sent = ServerAdmin.Broadcast(text) });
                    break;
                }
                case ("/api/restart", "POST"): {
                    var body = ReadBody(request);
                    var kind = body.TryGetProperty("kind", out var k) && k.GetString() == "backup" ? RestartKind.Backup : RestartKind.Restart;
                    var minutes = body.TryGetProperty("minutes", out var m) && m.TryGetDouble(out var value) ? value : 5;
                    if (minutes is < 0 or > 240) {
                        Json(response, 400, new { error = "minutes must be 0 to 240" });
                        break;
                    }

                    var reason = body.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    Json(response, 200, new { ok = true, restart = ServerAdmin.Schedule(kind, TimeSpan.FromMinutes(minutes),
                        reason is { Length: > 200 } ? reason[..200] : reason, user + " (dashboard)") });
                    break;
                }
                case ("/api/cancel", "POST"):
                    Json(response, 200, new { ok = ServerAdmin.Cancel(user + " (dashboard)") });
                    break;
                case ("/api/settings", "POST"): {
                    var body = ReadBody(request);
                    var key = body.GetProperty("key").GetString() ?? "";
                    var value = body.TryGetProperty("value", out var v) ? v.ToString() : null;
                    if (!ClassicSettings.Store.TrySet(key, value, out var error)) {
                        Json(response, 400, new { error });
                        break;
                    }

                    if (key.StartsWith("Holiday", StringComparison.OrdinalIgnoreCase)) {
                        ClassicHolidays.Refresh(); // the events follow at once, not at the next minute
                    }

                    Logger.Information("[ADMIN] {User} set {Key} = {Value} from the dashboard.",
                        Logger.Args(user, key, string.IsNullOrEmpty(value) ? "(ini/default)" : value));
                    Json(response, 200, new { ok = true, note = error });
                    break;
                }
                default:
                    Json(response, 404, new { error = "not found" });
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) {
            TryJson(response, 400, new { error = "bad request" });
        }
        catch (Exception ex) {
            Logger.Error("[ADMIN] Dashboard request failed: {Error}", Logger.Args(ex));
            TryJson(response, 500, new { error = "server error" });
        }
    }

    private static void Login(HttpListenerContext context) {
        var request = context.Request;
        var response = context.Response;
        var address = request.RemoteEndPoint?.Address.ToString() ?? "?";
        if (TooManyFailures(address)) {
            Json(response, 429, new { error = "too many failed logins; wait a minute" });

            return;
        }

        var body = ReadBody(request);
        var username = body.GetProperty("username").GetString() ?? "";
        var password = body.GetProperty("password").GetString() ?? "";
        if (!VerifyAdministrator(username, password)) {
            RecordFailure(address);
            Logger.Warning("[ADMIN] Failed dashboard login for {User} from {Address}.", Logger.Args(username, address));
            Json(response, 401, new { error = "wrong username or password, or not an administrator" });

            return;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        s_sessions[token] = (username, DateTime.UtcNow + s_sessionLifetime);
        foreach (var expired in s_sessions.Where(pair => pair.Value.ExpiresUtc < DateTime.UtcNow).Select(pair => pair.Key).ToList()) {
            s_sessions.TryRemove(expired, out _);
        }

        response.Headers.Add("Set-Cookie",
            $"{CookieName}={token}; Path=/; Max-Age={(int) s_sessionLifetime.TotalSeconds}; HttpOnly; SameSite=Strict");
        Logger.Information("[ADMIN] {User} logged in to the dashboard from {Address}.", Logger.Args(username, address));
        Json(response, 200, new { ok = true, user = username });
    }

    /// <summary>True when the account exists, the password matches its stored hash, and it is an Administrator.</summary>
    internal static bool VerifyAdministrator(string username, string password) {
        if (username.Length is 0 or > 64 || password.Length is 0 or > 256) {
            return false;
        }

        Account? account;
        try {
            account = AccountCollection.GetAccount(username);
        }
        catch (Exception) {
            return false;
        }

        if (account is null || account.IsLocked || account.AuthLevel < AuthLevel.Administrator) {
            return false;
        }

        // CLASSIC: the PBKDF2 verifier (old accounts: the protocol hash, then upgraded), Auth/PasswordStore.
        return Auth.PasswordStore.Verify(account, password);
    }

    /// <summary>Compares a password with an account's stored hash in constant time.</summary>
    internal static bool PasswordMatches(string? storedHash, string password) {
        if (string.IsNullOrEmpty(storedHash)) {
            return false;
        }

        var given = Encoding.UTF8.GetBytes(DatabaseUtilities.CreateHashedPassword(password));
        var stored = Encoding.UTF8.GetBytes(storedHash);

        return CryptographicOperations.FixedTimeEquals(given, stored);
    }

    private static string? SessionUser(HttpListenerRequest request) {
        if (request.Cookies[CookieName]?.Value is not { Length: 64 } token
                || !s_sessions.TryGetValue(token, out var session)) {
            return null;
        }

        if (session.ExpiresUtc < DateTime.UtcNow) {
            s_sessions.TryRemove(token, out _);

            return null;
        }

        return session.User;
    }

    private static bool TooManyFailures(string address) {
        if (!s_failedLogins.TryGetValue(address, out var failures)) {
            return false;
        }

        lock (failures) {
            while (failures.Count > 0 && failures.Peek() < DateTime.UtcNow.AddMinutes(-1)) {
                failures.Dequeue();
            }

            return failures.Count >= 5;
        }
    }

    private static void RecordFailure(string address) {
        var failures = s_failedLogins.GetOrAdd(address, _ => new Queue<DateTime>());
        lock (failures) {
            failures.Enqueue(DateTime.UtcNow);
        }
    }

    private static object State(string user) {
        var uptime = DateTime.UtcNow - ServerAdmin.StartedUtc;
        var sections = new Dictionary<string, object?>();
        foreach (var (name, provider) in s_sections) {
            try {
                sections[name] = provider();
            }
            catch (Exception ex) {
                sections[name] = new { error = ex.Message };
            }
        }

        return new {
            user,
            now = DateTime.UtcNow,
            startedUtc = ServerAdmin.StartedUtc,
            uptimeSeconds = (long) uptime.TotalSeconds,
            profile = ClassicRuntime.IsActive ? ClassicRuntime.Rules.Profile.Id : "stock",
            online = ServerAdmin.OnlineWizards(),
            duels = ActiveDuels.Snapshot(),
            restart = ServerAdmin.Status(),
            warnings = RecentLogSink.Instance.WarningCount,
            errors = RecentLogSink.Instance.ErrorCount,
            recentLog = RecentLogSink.Instance.Snapshot().Take(60),
            backups = ServerAdmin.Backups().Take(30),
            backupDirectory = ServerAdmin.BackupDirectory,
            settings = ClassicSettings.Store.Snapshot().Select(entry => new {
                key = entry.Definition.Key,
                kind = entry.Definition.Kind.ToString().ToLowerInvariant(),
                group = entry.Definition.Group,
                description = entry.Definition.Description,
                defaultValue = entry.Definition.Default,
                min = entry.Definition.Min == double.MinValue ? (double?) null : entry.Definition.Min,
                max = entry.Definition.Max == double.MaxValue ? (double?) null : entry.Definition.Max,
                live = entry.Definition.Live,
                value = entry.Value,
                source = entry.Source,
            }),
            sections,
        };
    }

    private static JsonElement ReadBody(HttpListenerRequest request) {
        if (request.ContentLength64 > MaxBodyBytes) {
            throw new InvalidOperationException("body too large");
        }

        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var buffer = new char[MaxBodyBytes + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        if (read > MaxBodyBytes) {
            throw new InvalidOperationException("body too large");
        }

        using var document = JsonDocument.Parse(new string(buffer, 0, read));

        return document.RootElement.Clone();
    }

    private static void Json(HttpListenerResponse response, int status, object value)
        => Write(response, status, "application/json; charset=utf-8", JsonSerializer.Serialize(value, s_json));

    private static void TryJson(HttpListenerResponse response, int status, object value) {
        try {
            Json(response, status, value);
        }
        catch (Exception) {
            // the response may already be gone
        }
    }

    private static void Write(HttpListenerResponse response, int status, string contentType, string text) {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
        response.OutputStream.Close();
    }

    private static string? Setting(string key) {
        try {
            return ConfigurationManager.GetSetting(key) is { Length: > 0 } text ? text.Trim() : null;
        }
        catch (Exception) {
            return null;
        }
    }

    internal static void StopForTests() {
        s_listener?.Stop();
        s_listener = null;
        s_friendProxy?.Stop();
        s_friendProxy = null;
    }

}
