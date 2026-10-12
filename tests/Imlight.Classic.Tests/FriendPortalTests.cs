// CLASSIC: real local HTTP requests exercise the friend gate; files and credentials here are synthetic fixtures.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Imlight.Classic.Net;
using Imlight.Common;
using Imlight.CoreLib.Classic.Friends;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class FriendPortalTests {
    private const string TestPassword = "synthetic test password";
    private static readonly Lazy<string> Verifier = new(() => PasswordHashing.CreateVerifier(TestPassword));

    public FriendPortalTests() {
        var config = Path.Combine(NewRoot(), "test.ini");
        File.WriteAllText(config, "[Logging]\nLogLevel=FATAL\nLogPath=/private/tmp/friend-portal-tests.log\n");
        ConfigurationManager.Initialize(config);
    }

    [Fact]
    public async Task LockedDownloadsAndRegistrationCannotReachFilesOrCreateAccounts() {
        var creates = 0;
        using var server = new HttpFixture(new FriendPortalBackend(Verifier.Value, Files(), create: (_, _, _) => { creates++; return true; }));
        using var mac = await server.Send(HttpMethod.Get, "friends/download/mac");
        using var windows = await server.Send(HttpMethod.Get, "friends/download/windows");
        using var signup = await server.Send(HttpMethod.Post, "friends/api/register", Signup());
        Assert.Equal(HttpStatusCode.Unauthorized, mac.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, windows.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, signup.StatusCode);
        Assert.Equal(0, creates);
        using var state = await server.Send(HttpMethod.Get, "friends/api/state");
        using var body = JsonDocument.Parse(await state.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("unlocked").GetBoolean());
        Assert.True(body.RootElement.GetProperty("downloads").GetProperty("mac").GetBoolean());
        Assert.Equal(new[] { "downloads", "unlocked" }, body.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public async Task UnlockCookieIgnoresSpoofedForwardingAndLogoutInvalidatesIt() {
        using var server = new HttpFixture(new FriendPortalBackend(Verifier.Value, Files()));
        using var unlocked = await server.Send(HttpMethod.Post, "friends/api/unlock", new { password = TestPassword },
            extraHeaders: new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https", ["Forwarded"] = "proto=https" });
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        var cookieHeader = Assert.Single(unlocked.Headers.GetValues("Set-Cookie"));
        Assert.Contains("Path=/friends/", cookieHeader); Assert.Contains("HttpOnly", cookieHeader); Assert.Contains("SameSite=Strict", cookieHeader);
        Assert.DoesNotContain("; Secure", cookieHeader);
        var cookie = cookieHeader.Split(';')[0];
        Assert.Equal(64, cookie.Split('=')[1].Length);
        using var state = await server.Send(HttpMethod.Get, "friends/api/state", cookie: cookie);
        Assert.Contains("\"unlocked\":true", await state.Content.ReadAsStringAsync());
        using var logout = await server.Send(HttpMethod.Post, "friends/api/logout", new { }, cookie);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Contains("Max-Age=0", Assert.Single(logout.Headers.GetValues("Set-Cookie")));
        using var denied = await server.Send(HttpMethod.Get, "friends/download/mac", cookie: cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task SignupForcesOrdinaryRoleAndReturnsOnlyTheUsername() {
        AuthLevel? createdRole = null;
        string? createdUsername = null;
        using var server = new HttpFixture(new FriendPortalBackend(Verifier.Value, Files(), create: (username, _, role) => {
            createdUsername = username; createdRole = role; return true;
        }));
        var cookie = await server.Unlock();
        using var signup = await server.Send(HttpMethod.Post, "friends/api/register", Signup(role: "Administrator"), cookie);
        Assert.Equal(HttpStatusCode.Created, signup.StatusCode);
        Assert.Equal(AuthLevel.None, createdRole);
        Assert.Equal("friend_one", createdUsername);
        using var result = JsonDocument.Parse(await signup.Content.ReadAsStringAsync());
        Assert.Equal("friend_one", result.RootElement.GetProperty("username").GetString());
        Assert.Single(result.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task WritesRequireHeaderSameOriginJsonAndBoundedBytes() {
        using var server = new HttpFixture(new FriendPortalBackend(Verifier.Value, Files()));
        using var noHeader = await server.Send(HttpMethod.Post, "friends/api/unlock", new { password = TestPassword }, includeHeader: false);
        using var foreign = await server.Send(HttpMethod.Post, "friends/api/unlock", new { password = TestPassword }, origin: "https://foreign.example");
        using var tooLarge = await server.Send(HttpMethod.Post, "friends/api/unlock", rawBody: new string('x', FriendPortal.MaxBodyBytes + 1));
        using var nonObject = await server.Send(HttpMethod.Post, "friends/api/unlock", rawBody: "[]");
        using var malformed = await server.Send(HttpMethod.Post, "friends/api/unlock", rawBody: "{");
        using var wrongType = await server.Send(HttpMethod.Post, "friends/api/unlock", rawBody: "{}", contentType: "text/plain");
        using var wrongMethod = await server.Send(HttpMethod.Put, "friends/api/unlock");
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode); Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nonObject.StatusCode); Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode); Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.False(noHeader.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task DownloadStreamsAuthenticatedRangesWithHumanInstallerName() {
        using var server = new HttpFixture(new FriendPortalBackend(Verifier.Value, Files()));
        var cookie = await server.Unlock();
        using var range = await server.Send(HttpMethod.Get, "friends/download/mac", cookie: cookie,
            extraHeaders: new Dictionary<string, string> { ["Range"] = "bytes=2-5" });
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(new byte[] { 2, 3, 4, 5 }, await range.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes 2-5/10", range.Content.Headers.ContentRange!.ToString());
        Assert.Contains("Wizard101 Classic.dmg", range.Content.Headers.ContentDisposition!.ToString());
        using var windows = await server.Send(HttpMethod.Get, "friends/download/windows", cookie: cookie);
        Assert.Contains("Wizard101 Classic Setup.exe", windows.Content.Headers.ContentDisposition!.ToString());
        using var multiple = await server.Send(HttpMethod.Get, "friends/download/mac", cookie: cookie,
            extraHeaders: new Dictionary<string, string> { ["Range"] = "bytes=0-1,3-4" });
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, multiple.StatusCode);
        using var ifRange = await server.Send(HttpMethod.Get, "friends/download/mac", cookie: cookie,
            extraHeaders: new Dictionary<string, string> { ["Range"] = "bytes=2-5", ["If-Range"] = "unknown-validator" });
        Assert.Equal(HttpStatusCode.OK, ifRange.StatusCode);
        Assert.Equal(10, (await ifRange.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task PublicLogoAndPageWorkWhileVerifierIsDisabled() {
        using var server = new HttpFixture(new FriendPortalBackend(null, Files()));
        using var page = await server.Send(HttpMethod.Get, "friends/");
        using var logo = await server.Send(HttpMethod.Get, "friends/spiral.png");
        using var unlock = await server.Send(HttpMethod.Post, "friends/api/unlock", new { password = TestPassword });
        Assert.Equal(HttpStatusCode.OK, page.StatusCode); Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/png", logo.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unlock.StatusCode);
        Assert.Contains("frame-ancestors 'none'", Assert.Single(page.Headers.GetValues("Content-Security-Policy")));
        using var state = await server.Send(HttpMethod.Get, "friends/api/state");
        Assert.Contains("\"mac\":false", await state.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("192.168.1.9", true)] [InlineData("10.0.0.2", true)] [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)] [InlineData("172.32.0.1", false)] [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", true)] [InlineData("::1", true)] [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("fd00::1", true)] [InlineData("2001:4860:4860::8888", false)]
    public void DefaultFriendAccessAcceptsOnlyLocalAddresses(string address, bool expected)
        => Assert.Equal(expected, FriendPortalBackend.IsLanAddress(IPAddress.Parse(address)));

    [Fact]
    public void ExplicitPublicOriginRequiresLoopbackProxyAndThatOriginAlone() {
        var origin = FriendPortal.ParsePublicOrigin("https://friends.example");
        Assert.NotNull(origin);
        var backend = new FriendPortalBackend(Verifier.Value, null, origin);
        Assert.True(backend.AllowsAddress(IPAddress.Loopback)); Assert.True(backend.AllowsAddress(IPAddress.IPv6Loopback));
        Assert.False(backend.AllowsAddress(IPAddress.Parse("192.168.1.9"))); Assert.False(backend.AllowsAddress(IPAddress.Parse("8.8.8.8")));
        Assert.True(backend.SecureCookie(false));
        var request = new Uri("http://127.0.0.1:8082/friends/api/unlock");
        Assert.True(FriendPortal.OriginAllowed("https://friends.example", request, origin));
        Assert.False(FriendPortal.OriginAllowed("http://127.0.0.1:8082", request, origin));
        Assert.False(FriendPortal.OriginAllowed("https://other.example", request, origin));
        // Administrator writes continue to check only their actual dashboard origin.
        Assert.False(FriendPortal.OriginAllowed("https://friends.example", request, null));
        Assert.True(FriendPortal.OriginAllowed("http://127.0.0.1:8082", request, null));
        Assert.Null(FriendPortal.ParsePublicOrigin("http://friends.example"));
        Assert.Null(FriendPortal.ParsePublicOrigin("https://friends.example/path"));
        Assert.Null(FriendPortal.ParsePublicOrigin("https://name@friends.example"));
        Assert.False(FriendPortal.OriginAllowed("null", request, null));
    }

    [Fact]
    public void MissingMalformedWeakAndExtraFieldVerifierFilesDisableFriendAccess() {
        var root = NewRoot();
        Assert.Null(FriendPortal.LoadVerifier(Path.Combine(root, "missing.json")));
        Assert.Null(FriendPortal.LoadVerifier("relative.json"));
        foreach (var text in new[] { "{", "{}", "[]", "{\"passwordVerifier\":\"plaintext\"}",
            JsonSerializer.Serialize(new { passwordVerifier = Verifier.Value, password = "must not be accepted" }) }) {
            var path = Path.Combine(root, Guid.NewGuid() + ".json"); File.WriteAllText(path, text);
            Assert.Null(FriendPortal.LoadVerifier(path));
        }
        Assert.False(FriendPortalBackend.ValidVerifier(Verifier.Value.Replace("$210000$", "$1000$", StringComparison.Ordinal)));
        var valid = Path.Combine(root, "valid.json"); File.WriteAllText(valid, JsonSerializer.Serialize(new { passwordVerifier = Verifier.Value }));
        Assert.Equal(Verifier.Value, FriendPortal.LoadVerifier(valid));
    }

    [Fact]
    public void FailedUnlocksThrottleExpireAndRateStateRemainsBounded() {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var backend = new FriendPortalBackend(Verifier.Value, null, now: () => now);
        for (var i = 0; i < 5; i++) backend.RecordUnlockFailure("one-address");
        Assert.Equal(429, backend.Unlock(TestPassword, "one-address", null, out _));
        now = now.AddMinutes(1);
        Assert.Equal(200, backend.Unlock(TestPassword, "one-address", null, out var token));
        Assert.True(backend.IsUnlocked(token));
        now += FriendPortalBackend.SessionLifetime;
        Assert.False(backend.IsUnlocked(token)); Assert.Equal(0, backend.Counts.Sessions);
        for (var i = 0; i < 1000; i++) backend.RecordUnlockFailure("address-" + i);
        Assert.InRange(backend.Counts.RateKeys, 1, FriendPortalBackend.MaxRateKeys);
        Assert.Equal(429, backend.Unlock(TestPassword, "another-address", null, out _));
        now = now.AddMinutes(1); Assert.Equal(0, backend.Counts.RateKeys);
    }

    [Fact]
    public void SessionCapacityAndReplacementStayBounded() {
        var now = DateTime.UtcNow;
        var backend = new FriendPortalBackend(Verifier.Value, null, now: () => now);
        var sessions = (Dictionary<string, DateTime>) typeof(FriendPortalBackend).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend)!;
        for (var i = 0; i < FriendPortalBackend.MaxSessions; i++) sessions.Add(i.ToString("X64"), now.AddHours(8));
        Assert.Equal(429, backend.Unlock(TestPassword, "local", null, out _));
        Assert.Equal(FriendPortalBackend.MaxSessions, backend.Counts.Sessions);
        var previous = sessions.Keys.First();
        Assert.Equal(200, backend.Unlock(TestPassword, "local", previous, out var replacement));
        Assert.False(backend.IsUnlocked(previous)); Assert.True(backend.IsUnlocked(replacement));
        Assert.Equal(FriendPortalBackend.MaxSessions, backend.Counts.Sessions);
        backend.Logout(replacement); Assert.Equal(FriendPortalBackend.MaxSessions - 1, backend.Counts.Sessions);
    }

    [Fact]
    public void SignupAttemptsThrottleDuplicatesAndSanitizeBackendFailures() {
        var now = DateTime.UtcNow;
        var creates = 0;
        var backend = new FriendPortalBackend(Verifier.Value, null, now: () => now, create: (_, _, _) => { creates++; return false; });
        var body = Element(Signup());
        for (var i = 0; i < 5; i++) Assert.Equal(409, backend.Register(body, "local").Status);
        Assert.Equal(429, backend.Register(body, "local").Status); Assert.Equal(5, creates);
        now = now.AddMinutes(1); Assert.Equal(409, backend.Register(body, "local").Status);
        var failed = new FriendPortalBackend(Verifier.Value, null, create: (_, password, _) => throw new IOException("secret=" + password));
        var result = failed.Register(body, "local");
        Assert.Equal(503, result.Status); Assert.DoesNotContain(TestPassword, result.Error!);
    }

    [Theory]
    [InlineData("ab", false)] [InlineData("Friend", false)] [InlineData(" friend", false)]
    [InlineData("friend ", false)] [InlineData("frïend", false)] [InlineData("friend.one", false)]
    [InlineData("friend_one-2", true)] [InlineData("abcdefghijklmnopqrstuvwx", true)]
    [InlineData("abcdefghijklmnopqrstuvwxy", false)]
    public void SignupRejectsNoncanonicalUsernamesWithoutLowercasingOrTrimming(string username, bool valid) {
        var error = FriendPortalBackend.ValidateSignup(Element(Signup(username)), out var received, out _);
        Assert.Equal(valid, error is null); Assert.Equal(username, received);
    }

    [Theory]
    [InlineData(5, false)] [InlineData(6, true)] [InlineData(12, true)] [InlineData(128, true)] [InlineData(129, false)]
    public void SignupEnforcesTheLauncherCompatiblePasswordBounds(int length, bool valid) {
        var password = new string('x', length);
        Assert.Equal(valid, FriendPortalBackend.ValidateSignup(Element(new { username = "friend", password, passwordConfirm = password }), out _, out _) is null);
    }

    [Fact]
    public void SignupRejectsControlsMismatchUnexpectedFieldsAndDuplicateFields() {
        Assert.NotNull(FriendPortalBackend.ValidateSignup(Element(new { username = "friend", password = "long password\n", passwordConfirm = "long password\n" }), out _, out _));
        Assert.NotNull(FriendPortalBackend.ValidateSignup(Element(new { username = "friend", password = TestPassword, passwordConfirm = TestPassword + " " }), out _, out _));
        Assert.NotNull(FriendPortalBackend.ValidateSignup(Element(new { username = "friend", password = TestPassword, passwordConfirm = TestPassword, email = "extra" }), out _, out _));
        using var duplicates = JsonDocument.Parse("{\"username\":\"first\",\"username\":\"second\"}");
        Assert.NotNull(FriendPortalBackend.ValidateSignup(duplicates.RootElement, out _, out _));
        using var array = JsonDocument.Parse("[]");
        Assert.NotNull(FriendPortalBackend.ValidateSignup(array.RootElement, out _, out _));
    }

    [Fact]
    public void JsonLimitCountsBytesAndAlsoGuardsUnknownLengthStreams() {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { password = new string('é', 3000) }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        using var stream = new MemoryStream(bytes);
        Assert.Equal(413, Assert.Throws<FriendPortal.PortalRequestException>(() => FriendPortal.ReadJson(stream, -1, "application/json")).Status);
        using var small = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        Assert.Equal(413, Assert.Throws<FriendPortal.PortalRequestException>(() => FriendPortal.ReadJson(small, FriendPortal.MaxBodyBytes + 1, "application/json")).Status);
    }

    [Fact]
    public void FilesAreExactAllowlistAndSymlinksInFilesOrParentsAreDenied() {
        var root = Files(); var backend = new FriendPortalBackend(Verifier.Value, root);
        using var valid = backend.OpenFile("Wizard101-Classic.dmg"); Assert.NotNull(valid);
        Assert.Null(backend.OpenFile("../Wizard101-Classic.dmg")); Assert.Null(backend.OpenFile(Path.Combine(root, "Wizard101-Classic.dmg")));
        Assert.Null(backend.OpenFile("other.dmg"));
        var fileLinkRoot = NewRoot();
        File.CreateSymbolicLink(Path.Combine(fileLinkRoot, "Wizard101-Classic.dmg"), Path.Combine(root, "Wizard101-Classic.dmg"));
        Assert.Null(new FriendPortalBackend(Verifier.Value, fileLinkRoot).OpenFile("Wizard101-Classic.dmg"));
        var link = Path.Combine(NewRoot(), "linked-root"); Directory.CreateSymbolicLink(link, root);
        Assert.Null(new FriendPortalBackend(Verifier.Value, link).OpenFile("Wizard101-Classic.dmg"));
    }

    [Theory]
    [InlineData(null, true, 0, 10)] [InlineData("bytes=0-3", true, 0, 4)] [InlineData("bytes=2-", true, 2, 8)]
    [InlineData("bytes=-3", true, 7, 3)] [InlineData("bytes=-30", true, 0, 10)] [InlineData("bytes=2-99", true, 2, 8)]
    [InlineData("bytes=10-", false, 0, 0)] [InlineData("bytes=4-2", false, 0, 0)] [InlineData("bytes=-0", false, 0, 0)]
    [InlineData("bytes=0-1,3-4", false, 0, 0)] [InlineData("bytes=9223372036854775808-", false, 0, 0)]
    [InlineData("items=0-3", false, 0, 0)] [InlineData("bytes=+1-3", false, 0, 0)]
    public void SingleByteRangesAreClampedAndInvalidRangesRejected(string? header, bool valid, long start, long length) {
        Assert.Equal(valid, FriendPortal.TryRange(header, 10, out var range));
        if (valid) { Assert.Equal(start, range.Start); Assert.Equal(length, range.Length); Assert.Equal(header is not null, range.Partial); }
    }

    private static object Signup(string username = "friend_one", string role = "None") => new { username, password = TestPassword, passwordConfirm = TestPassword, role };
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
    private static string NewRoot() {
        // /var and /tmp are symlinks on macOS; the production opener intentionally rejects any symlink parent.
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "w101c-friends-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static string Files() {
        var root = NewRoot(); var bytes = Enumerable.Range(0, 10).Select(i => (byte) i).ToArray();
        foreach (var name in new[] { "Wizard101-Classic.dmg", "Wizard101-Classic-Setup.exe", "spiral.png" }) File.WriteAllBytes(Path.Combine(root, name), bytes);
        return root;
    }

    private sealed class HttpFixture : IDisposable {
        private readonly HttpListener _listener = new();
        private readonly HttpClient _client;
        internal HttpFixture(FriendPortalBackend backend) {
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            var port = ((IPEndPoint) reservation.LocalEndpoint).Port; reservation.Stop();
            var url = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(url.AbsoluteUri); _listener.Start();
            _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = url, Timeout = TimeSpan.FromSeconds(10) };
            _ = Task.Run(async () => {
                while (_listener.IsListening) {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch (HttpListenerException) { break; } catch (ObjectDisposedException) { break; }
                    _ = Task.Run(() => FriendPortal.Handle(context, backend));
                }
            });
        }
        internal async Task<string> Unlock() {
            using var response = await Send(HttpMethod.Post, "friends/api/unlock", new { password = TestPassword });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
        }
        internal async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, string? cookie = null,
            bool includeHeader = true, string? origin = null, Dictionary<string, string>? extraHeaders = null, string? rawBody = null, string contentType = "application/json") {
            using var request = new HttpRequestMessage(method, path);
            if (includeHeader) request.Headers.Add("X-W101C", "1");
            if (cookie is not null) request.Headers.Add("Cookie", cookie);
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (extraHeaders is not null) foreach (var pair in extraHeaders) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            if (body is not null || rawBody is not null) request.Content = new StringContent(rawBody ?? JsonSerializer.Serialize(body), Encoding.UTF8, contentType);
            return await _client.SendAsync(request);
        }
        public void Dispose() { _client.Dispose(); _listener.Close(); }
    }
}
