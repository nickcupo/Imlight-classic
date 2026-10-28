// CLASSIC: a password-protected friend page on the existing LAN dashboard listener.
// The site verifier and allowlisted installer/logo files are operator-managed private files, never game credentials.
// CLASSIC (outside-access): [Classic] FriendSignupInviteRequired = true makes every sign-up spend a per-friend invite
// code (FriendInvites.cs; made on the admin page). The off switch (OutsideSwitch) closes the page to outside visitors.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Imlight.Classic.Net;
using Imlight.Common;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;
using Microsoft.Win32.SafeHandles;

namespace Imlight.CoreLib.Classic.Friends;

internal static class FriendPortal {
    internal const string CookieName = "w101c_friends";
    internal const int MaxBodyBytes = 4096;
    private static readonly object s_configurationGate = new();
    private static readonly SemaphoreSlim s_requests = new(16, 16);
    private static FriendPortalBackend? s_backend;
    private static string? s_configurationIdentity;

    internal static bool TryHandle(HttpListenerContext context) {
        var path = context.Request.Url?.AbsolutePath ?? "";
        if (path != "/friends" && !path.StartsWith("/friends/", StringComparison.Ordinal)) return false;
        Handle(context, Backend());
        return true;
    }

    internal static void Handle(HttpListenerContext context, FriendPortalBackend backend) {
        if (!s_requests.Wait(0)) { Json(context.Response, 429, new { error = "Please wait a moment and try again." }); return; }
        try {
            var request = context.Request;
            var response = context.Response;
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-Frame-Options"] = "DENY";
            response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (!backend.AllowsAddress(request.RemoteEndPoint?.Address)
                || !OriginAllowed(request.Headers["Origin"], request.Url, backend.PublicOrigin)) {
                Json(response, 403, new { error = "This request is not allowed." }); return;
            }
            // CLASSIC (outside-access): while outside access is off, only the home LAN (behind the tunnel or the HTTPS
            // front: the visitor it names) gets the page.
            var visitorHeader = Setting("Classic.TrustedProxyHeader");
            if (!Imlight.CoreLib.Auth.SecuritySettings.Outside.Value.AdmitsRequest(request.RemoteEndPoint?.Address,
                    visitorHeader is null ? null : request.Headers[visitorHeader], behindLocalProxy: backend.PublicOrigin is not null)) {
                Json(response, 503, new { error = "The friend page is offline right now." }); return;
            }
            var path = request.Url?.AbsolutePath ?? "";
            var method = request.HttpMethod;
            if (method is not ("GET" or "POST")) { MethodNotAllowed(response); return; }
            if (method == "POST" && request.Headers["X-W101C"] != "1") {
                Json(response, 403, new { error = "This request is not allowed." }); return;
            }
            var token = request.Cookies[CookieName]?.Value;
            var address = ClientAddress(request); // CLASSIC (go-live): the visitor behind the local tunnel
            switch (path, method) {
                case ("/friends", "GET"):
                    response.StatusCode = 302; response.RedirectLocation = "/friends/"; response.Close(); break;
                case ("/friends/", "GET"):
                    Write(response, 200, "text/html; charset=utf-8", FriendPortalPage.Html); break;
                case ("/friends/spiral.png", "GET"):
                    ServeFile(context, backend, "spiral.png", false); break;
                case ("/friends/api/state", "GET"):
                    Json(response, 200, new { unlocked = backend.IsUnlocked(token), downloads = backend.Downloads(), invite = backend.InviteRequired }); break;
                case ("/friends/api/unlock", "POST"): {
                    var body = ReadBody(request);
                    if (!HasFields(body, "password") || Text(body, "password") is not { Length: > 0 and <= 128 } password
                        || password.Any(char.IsControl)) {
                        backend.RecordUnlockFailure(address);
                        Json(response, 400, new { error = "Enter the site password." }); break;
                    }
                    var result = backend.Unlock(password, address, token, out var createdToken);
                    if (result == 200) {
                        SetCookie(response, createdToken!, backend.SecureCookie(request.IsSecureConnection), FriendPortalBackend.SessionLifetime);
                        Json(response, 200, new { unlocked = true });
                    } else Json(response, result, new { error = result == 503 ? "Friend access is not configured." : result == 429 ? "Too many attempts. Wait a minute and try again." : "The site password is incorrect." });
                    break;
                }
                case ("/friends/api/register", "POST"):
                    if (!backend.IsUnlocked(token)) { Json(response, 401, new { error = "Unlock the friend page first." }); break; }
                    Register(context, backend); break;
                case ("/friends/api/logout", "POST"):
                    ReadBody(request); // the same bounded JSON contract applies to logout
                    backend.Logout(token);
                    SetCookie(response, "", backend.SecureCookie(request.IsSecureConnection), TimeSpan.Zero);
                    Json(response, 200, new { unlocked = false }); break;
                case ("/friends/download/mac", "GET"):
                case ("/friends/download/windows", "GET"):
                    if (!backend.IsUnlocked(token)) { Json(response, 401, new { error = "Unlock the friend page first." }); break; }
                    ServeFile(context, backend, path.EndsWith("/mac", StringComparison.Ordinal) ? "Wizard101-Classic.dmg" : "Wizard101-Classic-Setup.exe", true); break;
                default:
                    if (KnownPath(path)) MethodNotAllowed(response);
                    else Json(response, 404, new { error = "Not found." });
                    break;
            }
        } catch (PortalRequestException ex) {
            TryJson(context.Response, ex.Status, new { error = ex.Status == 413 ? "The request is too large." : "Check the form and try again." });
        } catch (JsonException) {
            TryJson(context.Response, 400, new { error = "Check the form and try again." });
        } catch (Exception) {
            // Never log exception objects, request bodies, configured paths or passwords from these routes.
            Logger.Warning("[FRIENDS] A friend-page request could not be completed.");
            try { context.Response.Abort(); } catch (Exception) { }
        } finally { s_requests.Release(); }
    }

    // AdminDashboard calls this only after its existing Administrator session and CSRF-header checks.
    internal static void HandleAdmin(HttpListenerContext context, string path) {
        var backend = Backend();
        if (!OriginAllowed(context.Request.Headers["Origin"], context.Request.Url, null)) {
            Json(context.Response, 403, new { error = "This request is not allowed." }); return;
        }
        try {
            if (path == "/api/friends/state" && context.Request.HttpMethod == "GET") {
                Json(context.Response, 200, new { enabled = backend.Enabled, downloads = backend.Downloads() });
            } else if (path == "/api/friends/account" && context.Request.HttpMethod == "POST") {
                Register(context, backend, byAdministrator: true);
            } else if (path == "/api/friends/invites" && context.Request.HttpMethod == "GET") {
                // CLASSIC (outside-access): ids, labels, dates and states only; codes are shown once, at creation.
                var now = DateTime.UtcNow;
                Json(context.Response, 200, new {
                    required = FriendInvites.Required,
                    invites = FriendInvites.Book.Value.List().Select(r => new {
                        id = r.ShortId, label = r.Label, createdUtc = r.CreatedUtc, expiresUtc = r.ExpiresUtc,
                        state = r.State(now), usedBy = r.UsedBy,
                    }).ToArray(),
                });
            } else if (path == "/api/friends/invite" && context.Request.HttpMethod == "POST") {
                var body = ReadBody(context.Request);
                var label = InviteCodes.CleanLabel(HasFields(body, "label", "days") ? Text(body, "label") : null);
                var days = !body.TryGetProperty("days", out var d) ? 7 : d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) ? n : 0;
                if (label is null || days is < 1 or > 30) {
                    Json(context.Response, 400, new { error = "Give a name of 1–40 characters and 1–30 days." }); return;
                }
                var (code, record) = FriendInvites.Book.Value.Create(label, TimeSpan.FromDays(days));
                Logger.Information("[FRIENDS] Invite {0} made for {1}, valid {2} day(s).", Logger.Args(record.ShortId, record.Label, days));
                Json(context.Response, 201, new { code, id = record.ShortId, label = record.Label, expiresUtc = record.ExpiresUtc });
            } else if (path == "/api/friends/invite/revoke" && context.Request.HttpMethod == "POST") {
                var body = ReadBody(context.Request);
                var id = HasFields(body, "id") ? Text(body, "id") : null;
                if (id is null || !FriendInvites.Book.Value.Revoke(id)) {
                    Json(context.Response, 404, new { error = "No open invite with that id." }); return;
                }
                Logger.Information("[FRIENDS] Invite {0} revoked.", Logger.Args(id));
                Json(context.Response, 200, new { id, state = "revoked" });
            } else MethodNotAllowed(context.Response);
        } catch (PortalRequestException ex) {
            TryJson(context.Response, ex.Status, new { error = ex.Status == 413 ? "The request is too large." : "Check the form and try again." });
        } catch (JsonException) {
            TryJson(context.Response, 400, new { error = "Check the form and try again." });
        } catch (Exception) {
            Logger.Warning("[FRIENDS] Account creation could not be completed.");
            TryJson(context.Response, 503, new { error = "Account creation is temporarily unavailable." });
        }
    }

    private static void Register(HttpListenerContext context, FriendPortalBackend backend, bool byAdministrator = false) {
        var result = backend.Register(ReadBody(context.Request), ClientAddress(context.Request), byAdministrator);
        if (result.Status == 201) Json(context.Response, 201, new { username = result.Username });
        else Json(context.Response, result.Status, new { error = result.Error });
    }

    /// <summary>
    /// CLASSIC (go-live): the address for rate limits. A loopback peer (the Cloudflare tunnel in this container) may
    /// name its visitor in [Classic] TrustedProxyHeader; access control still uses the peer (AllowsAddress).
    /// </summary>
    internal static string ClientAddress(HttpListenerRequest request) {
        var header = Setting("Classic.TrustedProxyHeader");
        return PublicAccess.ClientAddress(request.RemoteEndPoint?.Address, header is null ? null : request.Headers[header]);
    }

    private static bool KnownPath(string path) => path is "/friends" or "/friends/" or "/friends/spiral.png"
        or "/friends/api/state" or "/friends/api/unlock" or "/friends/api/register" or "/friends/api/logout"
        or "/friends/download/mac" or "/friends/download/windows";

    internal static bool OriginAllowed(string? origin, Uri? requestUrl, Uri? publicOrigin) {
        if (origin is null) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")
            || parsed.AbsolutePath != "/" || parsed.Query.Length != 0 || parsed.Fragment.Length != 0 || parsed.UserInfo.Length != 0) return false;
        static bool Same(Uri a, Uri b) => a.Scheme == b.Scheme && a.Port == b.Port && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase);
        return publicOrigin is not null ? Same(parsed, publicOrigin) : requestUrl is not null && Same(parsed, requestUrl);
    }

    internal static JsonElement ReadJson(Stream stream, long contentLength, string? contentType) {
        if (contentLength > MaxBodyBytes) throw new PortalRequestException(413);
        if (contentType?.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase) != true) throw new PortalRequestException(400);
        var bytes = new byte[MaxBodyBytes + 1];
        var count = 0;
        while (count < bytes.Length) {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxBodyBytes) throw new PortalRequestException(413);
        using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new PortalRequestException(400);
        return document.RootElement.Clone();
    }

    private static JsonElement ReadBody(HttpListenerRequest request) => ReadJson(request.InputStream, request.ContentLength64, request.ContentType);
    internal static string? Text(JsonElement body, string key) => body.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static bool HasFields(JsonElement body, params string[] allowed) {
        if (body.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return body.EnumerateObject().All(property => allowed.Contains(property.Name, StringComparer.Ordinal) && seen.Add(property.Name));
    }

    private static void SetCookie(HttpListenerResponse response, string token, bool secure, TimeSpan lifetime)
        => response.Headers.Add("Set-Cookie", $"{CookieName}={token}; Path=/friends/; Max-Age={(int) lifetime.TotalSeconds}; HttpOnly; SameSite=Strict{(secure ? "; Secure" : "")}");

    private static void ServeFile(HttpListenerContext context, FriendPortalBackend backend, string name, bool download) {
        using var file = backend.OpenFile(name);
        if (file is null) { Json(context.Response, 404, new { error = "This download is not ready yet." }); return; }
        var response = context.Response;
        // An unrecognized If-Range validator means send the whole current file, as required by HTTP.
        var rangeHeader = download && context.Request.Headers["If-Range"] is null ? context.Request.Headers["Range"] : null;
        if (!TryRange(rangeHeader, file.Length, out var range)) {
            response.Headers["Content-Range"] = $"bytes */{file.Length}";
            Json(response, 416, new { error = "The requested range is not available." }); return;
        }
        response.StatusCode = range.Partial ? 206 : 200;
        response.ContentType = download ? "application/octet-stream" : "image/png";
        response.ContentLength64 = range.Length;
        if (download) {
            response.Headers["Accept-Ranges"] = "bytes";
            var visibleName = name == "Wizard101-Classic.dmg" ? "Wizard101 Classic.dmg" : "Wizard101 Classic Setup.exe";
            response.Headers["Content-Disposition"] = $"attachment; filename=\"{visibleName}\"";
        }
        if (range.Partial) response.Headers["Content-Range"] = $"bytes {range.Start}-{range.Start + range.Length - 1}/{file.Length}";
        file.Position = range.Start;
        var remaining = range.Length;
        var buffer = new byte[64 * 1024];
        while (remaining > 0) {
            var read = file.Read(buffer, 0, (int) Math.Min(buffer.Length, remaining));
            if (read == 0) throw new IOException("File changed during download.");
            response.OutputStream.Write(buffer, 0, read);
            remaining -= read;
        }
        response.Close();
    }

    internal readonly record struct DownloadRange(long Start, long Length, bool Partial);
    internal static bool TryRange(string? header, long length, out DownloadRange range) {
        range = new(0, length, false);
        if (header is null) return true;
        if (length <= 0 || header.Length > 100 || !header.StartsWith("bytes=", StringComparison.Ordinal) || header.Contains(',')) return false;
        var parts = header[6..].Split('-');
        if (parts.Length != 2) return false;
        static bool Number(string value, out long number) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        if (parts[0].Length == 0) {
            if (!Number(parts[1], out var suffix) || suffix <= 0) return false;
            var count = Math.Min(suffix, length);
            range = new(length - count, count, true); return true;
        }
        if (!Number(parts[0], out var start) || start >= length) return false;
        var end = length - 1;
        if (parts[1].Length > 0 && (!Number(parts[1], out end) || end < start)) return false;
        end = Math.Min(end, length - 1);
        range = new(start, end - start + 1, true); return true;
    }

    private static FriendPortalBackend Backend() {
        var verifier = LoadVerifier(Setting("Classic.FriendPortalPasswordFile"));
        var files = Setting("Classic.FriendPortalFilesPath");
        var origin = ParsePublicOrigin(Setting("Classic.FriendPortalPublicOrigin"));
        var invites = FriendInvites.Required; // CLASSIC (outside-access)
        var identity = string.Join('\n', verifier, files, origin?.AbsoluteUri, invites);
        lock (s_configurationGate) {
            if (s_backend is null || identity != s_configurationIdentity) {
                s_backend = new FriendPortalBackend(verifier, files, origin, invites: invites ? FriendInvites.Book.Value : null);
                s_configurationIdentity = identity; // rotation discards every old site session immediately
            }
            return s_backend;
        }
    }

    internal static string? LoadVerifier(string? path) {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return null;
        try {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaxBodyBytes) return null;
            using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (!HasFields(root, "passwordVerifier")) return null;
            var verifier = Text(root, "passwordVerifier");
            return FriendPortalBackend.ValidVerifier(verifier) ? verifier : null;
        } catch (Exception) { return null; }
    }

    internal static Uri? ParsePublicOrigin(string? text)
        => Uri.TryCreate(text, UriKind.Absolute, out var origin) && origin.Scheme == "https"
            && origin.AbsolutePath == "/" && origin.UserInfo.Length == 0 && origin.Query.Length == 0 && origin.Fragment.Length == 0 ? origin : null;

    private static string? Setting(string key) {
        try { return ConfigurationManager.GetSetting(key) is { Length: > 0 } value ? value.Trim() : null; }
        catch (Exception) { return null; }
    }
    private static void MethodNotAllowed(HttpListenerResponse response) { response.Headers["Allow"] = "GET, POST"; Json(response, 405, new { error = "This method is not allowed." }); }
    private static void Json(HttpListenerResponse response, int status, object body) => Write(response, status, "application/json; charset=utf-8", JsonSerializer.Serialize(body));
    private static void TryJson(HttpListenerResponse response, int status, object body) { try { Json(response, status, body); } catch (Exception) { try { response.Abort(); } catch (Exception) { } } }
    private static void Write(HttpListenerResponse response, int status, string contentType, string body) {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status; response.ContentType = contentType; response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes); response.Close();
    }
    internal sealed class PortalRequestException(int status) : Exception { internal int Status { get; } = status; }
}

internal sealed class FriendPortalBackend {
    internal static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    internal const int MaxSessions = 128, MaxRateKeys = 256;
    private static readonly SemaphoreSlim s_passwordWork = new(4, 4);
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTime>> _rates = new(StringComparer.Ordinal);
    private readonly string? _verifier, _filesRoot;
    private readonly Func<DateTime> _now;
    private readonly Func<string, string, AuthLevel, bool> _create;
    private readonly InviteBook? _invites; // CLASSIC (outside-access): null = no invite needed
    internal Uri? PublicOrigin { get; }
    internal bool Enabled => _verifier is not null;
    internal bool InviteRequired => _invites is not null;

    internal FriendPortalBackend(string? verifier, string? filesRoot, Uri? publicOrigin = null,
        Func<DateTime>? now = null, Func<string, string, AuthLevel, bool>? create = null, InviteBook? invites = null) {
        _verifier = ValidVerifier(verifier) ? verifier : null;
        _filesRoot = filesRoot;
        PublicOrigin = publicOrigin;
        _invites = invites;
        _now = now ?? (() => DateTime.UtcNow);
        _create = create ?? ((username, password, auth) => DatabaseUtilities.CreateEmbeddedDatabaseAccount(username, "", password, auth) is not null);
    }

    internal bool SecureCookie(bool nativeHttps) => nativeHttps || PublicOrigin is not null;
    // A future HTTPS endpoint may proxy here only over loopback, never by trusting forwarded headers.
    internal bool AllowsAddress(IPAddress? address) => PublicOrigin is not null
        ? address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
        : IsLanAddress(address);
    internal static bool IsLanAddress(IPAddress? address) {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 169 && bytes[1] == 254
            : address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
    }

    internal static bool ValidVerifier(string? verifier) {
        if (verifier is null || verifier.Length > 1024) return false;
        var parts = verifier.Split('$');
        if (parts.Length != 4 || parts[0] != PasswordHashing.VerifierPrefix
            || !int.TryParse(parts[1], out var iterations) || iterations < PasswordHashing.DefaultIterations || iterations > 1_000_000) return false;
        try { return Convert.FromBase64String(parts[2]).Length is >= 16 and <= 64 && Convert.FromBase64String(parts[3]).Length == 64; }
        catch (FormatException) { return false; }
    }

    internal int Unlock(string password, string address, string? previousToken, out string? token) {
        token = null;
        if (!Enabled) return 503;
        lock (_gate) { Cleanup(); if (Limited("u:" + address) || Limited("u:global", 30)) return 429; }
        if (!s_passwordWork.Wait(0)) return 429;
        bool matches;
        try { matches = PasswordHashing.Verify(_verifier, password); } finally { s_passwordWork.Release(); }
        if (!matches) { RecordUnlockFailure(address); return 401; }
        lock (_gate) {
            Cleanup();
            if (previousToken is not null) _sessions.Remove(previousToken);
            if (_sessions.Count >= MaxSessions) return 429;
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _sessions.Add(token, _now() + SessionLifetime);
        }
        return 200;
    }

    internal void RecordUnlockFailure(string address) {
        lock (_gate) { Cleanup(); Record("u:" + address); Record("u:global"); }
    }
    internal bool IsUnlocked(string? token) {
        lock (_gate) {
            Cleanup();
            return Enabled && token is { Length: 64 } && _sessions.TryGetValue(token, out var expiry) && expiry > _now();
        }
    }
    internal void Logout(string? token) { lock (_gate) { if (token is not null) _sessions.Remove(token); } }
    internal (int Sessions, int RateKeys) Counts { get { lock (_gate) { Cleanup(); return (_sessions.Count, _rates.Count); } } }

    internal readonly record struct RegistrationResult(int Status, string? Username, string? Error);
    /// <param name="byAdministrator">The admin page's own account form: no invite is needed there.</param>
    internal RegistrationResult Register(JsonElement body, string address, bool byAdministrator = false) {
        lock (_gate) {
            Cleanup();
            if (Limited("r:" + address) || Limited("r:global", 30)) return new(429, null, "Too many attempts. Wait a minute and try again.");
            Record("r:" + address); Record("r:global"); // successful requests count too, bounding account/hash work
        }
        var error = ValidateSignup(body, out var username, out var password);
        if (error is not null) return new(400, null, error);
        if (!s_passwordWork.Wait(0)) return new(429, null, "Please wait a moment and try again.");
        try {
            if (_invites is not null && !byAdministrator) return RegisterWithInvite(FriendPortal.Text(body, "invite"), username!, password!, address);
            return _create(username!, password!, AuthLevel.None) ? new(201, username, null) : new(409, null, "That username is unavailable.");
        } catch (Exception) {
            Logger.Warning("[FRIENDS] Account creation could not be completed.");
            return new(503, null, "Account creation is temporarily unavailable.");
        } finally { s_passwordWork.Release(); }
    }

    /// <summary>CLASSIC (outside-access): one account per invite; the code is spent only when the account was created.</summary>
    private RegistrationResult RegisterWithInvite(string? invite, string username, string password, string address) {
        var outcome = _invites!.Redeem(invite, username, () => _create(username, password, AuthLevel.None), out var id);
        switch (outcome) {
            case InviteRedeem.Created:
                Logger.Information("[FRIENDS] Invite {0} made account {1}.", Logger.Args(id, username));
                return new(201, username, null);
            case InviteRedeem.NotCreated:
                return new(409, null, "That username is unavailable.");
            default:
                // Never the code itself; the id only when the code was a real one.
                Logger.Information("[FRIENDS] Sign-up for {0} from {1} refused: invite {2} ({3}).",
                    Logger.Args(username, address, id ?? "unknown", outcome.ToString().ToLowerInvariant()));
                return new(403, null, "That invite code is not valid (used, expired or mistyped). Ask the owner for a new one.");
        }
    }

    internal static string? ValidateSignup(JsonElement body, out string? username, out string? password) {
        username = null; password = null;
        // A caller-provided role is ignored: every creation below uses the fixed AuthLevel.None.
        // CLASSIC (outside-access): "invite" is the friend's invite code (checked by RegisterWithInvite when required).
        if (!FriendPortal.HasFields(body, "username", "password", "passwordConfirm", "role", "invite")) return "Check the form and try again.";
        username = FriendPortal.Text(body, "username"); password = FriendPortal.Text(body, "password");
        if (username is not { Length: >= 3 and <= 24 } || username.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')))
            return "Use a username of 3–24 lowercase letters, numbers, underscores or hyphens.";
        // CLASSIC: owner ruling 2026-10-09, game passwords need at least 6 characters (was 12).
        if (password is not { Length: >= 6 and <= 128 } || password.Any(char.IsControl)) return "Use a password of 6–128 characters without control characters.";
        if (!string.Equals(password, FriendPortal.Text(body, "passwordConfirm"), StringComparison.Ordinal)) return "The passwords do not match.";
        return null;
    }

    internal object Downloads() => new { mac = Enabled && FileAvailable("Wizard101-Classic.dmg"), windows = Enabled && FileAvailable("Wizard101-Classic-Setup.exe") };
    private bool FileAvailable(string name) { using var file = OpenFile(name); return file is not null; }
    internal FileStream? OpenFile(string name) {
        if (name is not ("Wizard101-Classic.dmg" or "Wizard101-Classic-Setup.exe" or "spiral.png") || string.IsNullOrEmpty(_filesRoot)) return null;
        try {
            if (!Path.IsPathFullyQualified(_filesRoot)) return null;
            var root = new DirectoryInfo(Path.GetFullPath(_filesRoot));
            // Exact basenames only, and no symlink/reparse-point directory or file may escape the configured root.
            for (var directory = root; directory is not null; directory = directory.Parent)
                if (!directory.Exists || directory.LinkTarget is not null || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var path = Path.Combine(root.FullName, name);
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            return OpenWithoutSymlinks(root.FullName, name);
        } catch (Exception) { return null; }
    }

    // Anchor every path component to directory descriptors, with O_NOFOLLOW on each open. This also rejects
    // a symlink swapped in between the checks above and the actual open; friends never choose a pathname.
    private static FileStream? OpenWithoutSymlinks(string root, string name) {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return null;
        var noFollow = OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
        var directoryFlag = OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;
        var closeOnExec = OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
        var directoryFlags = noFollow | directoryFlag | closeOnExec;
        var currentDirectory = OperatingSystem.IsMacOS() ? -2 : -100; // AT_FDCWD from each platform's fcntl.h
        var descriptor = OpenAt(currentDirectory, "/", directoryFlags);
        if (descriptor < 0) return null;
        SafeFileHandle current = new((IntPtr) descriptor, true);
        try {
            foreach (var component in root.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
                var next = OpenAt(current.DangerousGetHandle().ToInt32(), component, directoryFlags);
                if (next < 0) return null;
                current.Dispose();
                current = new SafeFileHandle((IntPtr) next, true);
            }
            var file = OpenAt(current.DangerousGetHandle().ToInt32(), name, noFollow | closeOnExec);
            if (file < 0) return null;
            var handle = new SafeFileHandle((IntPtr) file, true);
            try { return new FileStream(handle, FileAccess.Read, 64 * 1024, false); }
            catch (Exception) { handle.Dispose(); return null; }
        } finally { current.Dispose(); }
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directoryDescriptor, string path, int flags);

    private void Cleanup() {
        var now = _now();
        foreach (var token in _sessions.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray()) _sessions.Remove(token);
        foreach (var key in _rates.Keys.ToArray()) {
            var times = _rates[key];
            while (times.Count > 0 && times.Peek() <= now.AddMinutes(-1)) times.Dequeue();
            if (times.Count == 0) _rates.Remove(key);
        }
    }
    private bool Limited(string key, int limit = 5) => _rates.TryGetValue(key, out var times) ? times.Count >= limit : _rates.Count >= MaxRateKeys;
    private void Record(string key) {
        if (!_rates.TryGetValue(key, out var times)) {
            if (_rates.Count >= MaxRateKeys) return;
            _rates[key] = times = new Queue<DateTime>();
        }
        if (times.Count < 30) times.Enqueue(_now());
    }
}
