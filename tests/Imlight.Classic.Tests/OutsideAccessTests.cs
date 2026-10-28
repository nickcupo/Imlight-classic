// CLASSIC (outside-access): the off switch, the LAN-only AnyPasswordLogin rule, invite codes and the self-hosted
// HTTPS front (Imlight.Classic.Net). Pure rules plus real loopback sockets; certificates are generated per test.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Classic.Net;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class OutsideSwitchTests {

    private static readonly IReadOnlyList<(IPAddress, int)> HomeLan = PublicAccess.ParseNetworks("192.168.1.0/24,10.50.0.0/24")!;

    [Fact]
    public void WithNothingConfiguredTheSwitchIsNeverOff() {
        var outside = new OutsideSwitch(false, null, null, exists: _ => throw new InvalidOperationException("not asked"));
        Assert.False(outside.Configured);
        Assert.False(outside.IsOff);
        Assert.True(outside.Admits(IPAddress.Parse("203.0.113.9")));
        Assert.True(outside.AdmitsRequest(IPAddress.Loopback, null, behindLocalProxy: true));
    }

    [Fact]
    public void TheConfigSettingTurnsItOffForOutsideAddressesOnly() {
        var outside = new OutsideSwitch(true, null, HomeLan);
        Assert.True(outside.Configured);
        Assert.True(outside.IsOff);
        Assert.False(outside.Admits(IPAddress.Parse("203.0.113.9")));
        Assert.False(outside.Admits("2001:db8::1"));
        Assert.True(outside.Admits(IPAddress.Parse("192.168.1.20")));
        Assert.True(outside.Admits("::ffff:10.50.0.1"));
        Assert.True(outside.Admits(IPAddress.Loopback)); // local bots and services
        // LanNetworks narrows "private": another RFC 1918 network is outside.
        Assert.False(outside.Admits(IPAddress.Parse("172.16.0.5")));
        Assert.False(outside.Admits((string?) null));
    }

    [Fact]
    public void WithoutLanNetworksEveryPrivateAddressIsTheLan() {
        var outside = new OutsideSwitch(true, null, null);
        Assert.True(outside.Admits(IPAddress.Parse("172.16.0.5")));
        Assert.False(outside.Admits(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void TheOffFileIsFollowedWithinTwoSecondsWithoutARestart() {
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var present = false;
        var probes = 0;
        var outside = new OutsideSwitch(false, "/etc/w101c/outside-access-off", HomeLan, () => now,
            path => { probes++; Assert.Equal("/etc/w101c/outside-access-off", path); return present; });
        Assert.True(outside.Configured);
        Assert.False(outside.IsOff);
        present = true;
        now = now.AddSeconds(1);
        Assert.False(outside.IsOff); // cached
        now = now.AddSeconds(1.5);
        Assert.True(outside.IsOff);
        Assert.False(outside.Admits(IPAddress.Parse("198.51.100.4")));
        present = false;
        now = now.AddSeconds(3);
        Assert.False(outside.IsOff);
        Assert.Equal(3, probes);
    }

    [Fact]
    public void AnUnreadableOffFileCountsAsOff() {
        var outside = new OutsideSwitch(false, "/x", null, exists: _ => throw new UnauthorizedAccessException());
        Assert.True(outside.IsOff);
    }

    [Fact]
    public void TheRealProbeFailsClosedWhenItsFolderIsMissingOrUnreadable() {
        var folder = Directory.CreateTempSubdirectory("w101c-switch-").FullName;
        try {
            var file = Path.Combine(folder, "outside-access-off");
            Assert.False(OutsideSwitch.OffFilePresent(file));
            File.WriteAllText(file, "");
            Assert.True(OutsideSwitch.OffFilePresent(file));
            File.Delete(file);
            Assert.True(OutsideSwitch.OffFilePresent(Path.Combine(folder, "missing", "outside-access-off")));
            Assert.True(new OutsideSwitch(false, Path.Combine(folder, "missing", "x"), null).IsOff);
            Assert.False(new OutsideSwitch(false, file, null).IsOff);
            if (!OperatingSystem.IsWindows() && Environment.UserName != "root") {
                File.SetUnixFileMode(folder, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Assert.True(new OutsideSwitch(false, file, null).IsOff);
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        } finally {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void BehindALocalProxyTheVisitorDecidesAndNoVisitorIsRefused() {
        var outside = new OutsideSwitch(true, null, HomeLan);
        Assert.True(outside.AdmitsRequest(IPAddress.Loopback, "192.168.1.30", behindLocalProxy: true));
        Assert.False(outside.AdmitsRequest(IPAddress.Loopback, "203.0.113.9", behindLocalProxy: true));
        Assert.False(outside.AdmitsRequest(IPAddress.Loopback, null, behindLocalProxy: true));
        Assert.False(outside.AdmitsRequest(IPAddress.Loopback, "junk", behindLocalProxy: true));
        // A non-loopback peer cannot name a visitor.
        Assert.False(outside.AdmitsRequest(IPAddress.Parse("203.0.113.9"), "192.168.1.30", behindLocalProxy: true));
        Assert.True(outside.AdmitsRequest(IPAddress.Parse("192.168.1.30"), "203.0.113.9", behindLocalProxy: true));
        // Without a proxy in front, loopback is local.
        Assert.True(outside.AdmitsRequest(IPAddress.Loopback, null, behindLocalProxy: false));
    }

    [Theory]
    [InlineData(true, "192.168.1.20", false, true)]
    [InlineData(true, "127.0.0.1", false, true)]
    [InlineData(true, "203.0.113.9", false, false)]   // an outside address never gets it
    [InlineData(true, "192.168.1.20", true, false)]   // the HTTPS front or the tunnel never gets it
    [InlineData(true, "172.16.0.5", false, false)]    // outside LanNetworks
    [InlineData(false, "192.168.1.20", false, false)] // off
    [InlineData(true, "not an ip", false, false)]
    public void AnyPasswordLoginStaysOnTheHomeLan(bool configured, string client, bool publicListener, bool expected)
        => Assert.Equal(expected, OutsideAccess.AnyPasswordApplies(configured, client, publicListener, HomeLan));
}

public sealed class InviteCodeTests {

    [Fact]
    public void CodesAreTwelveCrockfordCharactersInThreeGroups() {
        var seen = new HashSet<string>();
        for (var i = 0; i < 200; i++) {
            var code = InviteCodes.NewCode();
            Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", code);
            Assert.True(seen.Add(code));
        }
    }

    [Theory]
    [InlineData("7k2m-qx9d-4hrt", "7K2MQX9D4HRT")]
    [InlineData(" 7K2M QX9D 4HRT ", "7K2MQX9D4HRT")]
    [InlineData("OOOO-IIII-LLLL", "000011111111")]
    [InlineData("7K2M-QX9D-4HR", null)]
    [InlineData("7K2M-QX9D-4HRTX", null)]
    [InlineData("7K2M-QX9D-4HRU", null)] // U is not in the alphabet
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TypedCodesAreNormalized(string? typed, string? expected) => Assert.Equal(expected, InviteCodes.Normalize(typed));

    [Fact]
    public void OnlyTheHashIsStoredAndTheIdIsItsPrefix() {
        var store = new MemoryInviteStore();
        var book = new InviteBook(store);
        var (code, record) = book.Create("for Sam");
        var stored = Assert.Single(store.All());
        Assert.Equal(InviteCodes.HashOf(InviteCodes.Normalize(code)!), stored.Hash);
        Assert.Equal(64, stored.Hash.Length);
        Assert.Equal(stored.Hash[..12], record.ShortId);
        Assert.DoesNotContain(InviteCodes.Normalize(code)!, stored.Hash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("for Sam", stored.Label);
    }

    [Fact]
    public void AnInviteCreatesExactlyOneAccount() {
        var book = new InviteBook(new MemoryInviteStore());
        var (code, record) = book.Create("for Sam");
        var created = new List<string>();
        Assert.Equal(InviteRedeem.Created, book.Redeem(code.ToLowerInvariant(), "sam", () => { created.Add("sam"); return true; }, out var id));
        Assert.Equal(record.ShortId, id);
        Assert.Equal(InviteRedeem.Used, book.Redeem(code, "mallory", () => { created.Add("mallory"); return true; }, out _));
        Assert.Equal(new[] { "sam" }, created);
        var listed = Assert.Single(book.List());
        Assert.Equal("sam", listed.UsedBy);
        Assert.Equal("used", listed.State(DateTime.UtcNow));
    }

    [Fact]
    public void ExpiredRevokedAndUnknownCodesCreateNothing() {
        var now = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        var book = new InviteBook(new MemoryInviteStore(), () => now);
        var (expiring, _) = book.Create("a", TimeSpan.FromDays(1));
        var (revoked, revokedRecord) = book.Create("b");
        Assert.True(book.Revoke(revokedRecord.ShortId));
        Assert.False(book.Revoke(revokedRecord.ShortId));
        Assert.False(book.Revoke("nope"));
        now = now.AddDays(2);
        Func<bool> never = () => throw new InvalidOperationException("must not create");
        Assert.Equal(InviteRedeem.Expired, book.Redeem(expiring, "x", never, out _));
        Assert.Equal(InviteRedeem.Revoked, book.Redeem(revoked, "x", never, out _));
        Assert.Equal(InviteRedeem.Unknown, book.Redeem(InviteCodes.NewCode(), "x", never, out var unknownId));
        Assert.Null(unknownId);
        Assert.Equal(InviteRedeem.Unknown, book.Redeem("not-a-code", "x", never, out _));
    }

    [Fact]
    public void AFailedAccountCreationLeavesTheInviteOpen() {
        var book = new InviteBook(new MemoryInviteStore());
        var (code, _) = book.Create("for Sam");
        Assert.Equal(InviteRedeem.NotCreated, book.Redeem(code, "taken", () => false, out _));
        Assert.Throws<IOException>(() => book.Redeem(code, "sam", () => throw new IOException("database down"), out _));
        Assert.Equal("open", Assert.Single(book.List()).State(DateTime.UtcNow));
        Assert.Equal(InviteRedeem.Created, book.Redeem(code, "sam", () => true, out _));
    }

    [Fact]
    public void TheInviteIsSpentBeforeTheAccountIsCreated() {
        var store = new MemoryInviteStore();
        var book = new InviteBook(store);
        var (code, _) = book.Create("for Sam");
        string? usedDuringCreate = null;
        book.Redeem(code, "sam", () => { usedDuringCreate = Assert.Single(store.All()).UsedBy; return true; }, out _);
        Assert.Equal("sam", usedDuringCreate);
    }

    [Fact]
    public void ConcurrentRedeemsCreateOneAccount() {
        var book = new InviteBook(new MemoryInviteStore());
        var (code, _) = book.Create("race");
        var created = 0;
        Parallel.For(0, 32, i => book.Redeem(code, "u" + i, () => { Interlocked.Increment(ref created); Thread.Sleep(1); return true; }, out _));
        Assert.Equal(1, created);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("for Sam", true)]
    [InlineData("bad\nlabel", false)]
    [InlineData("0123456789012345678901234567890123456789X", false)]
    public void LabelsAreShortAndPrintable(string label, bool ok) => Assert.Equal(ok, InviteCodes.CleanLabel(label) is not null);

    [Fact]
    public void LifetimesAreBounded() {
        var book = new InviteBook(new MemoryInviteStore());
        Assert.Throws<ArgumentOutOfRangeException>(() => book.Create("x", TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => book.Create("x", TimeSpan.FromDays(31)));
        Assert.Throws<ArgumentException>(() => book.Create(""));
    }
}

public sealed class HttpRequestHeadTests {

    private static HttpRequestHead? Parse(string text) => HttpRequestHead.Parse(Encoding.ASCII.GetBytes(text));

    [Fact]
    public void AnOrdinaryRequestParses() {
        var head = Parse("POST /launcher/login?x=1 HTTP/1.1\r\nHost: play.example.com\r\nContent-Type: application/json\r\nContent-Length: 42");
        Assert.NotNull(head);
        Assert.Equal("POST", head!.Method);
        Assert.Equal("/launcher/login", head.Path);
        Assert.Equal("/launcher/login?x=1", head.Target);
        Assert.Equal("application/json", head.Header("content-type"));
        Assert.Equal(42, head.ContentLength);
    }

    [Theory]
    [InlineData("GET / HTTP/2.0")]
    [InlineData("GET http://evil/ HTTP/1.1")]
    [InlineData("GET //evil/ HTTP/1.1")]
    [InlineData("GET /classic/../etc/passwd HTTP/1.1")]
    [InlineData("GET /classic/%2e%2e/x HTTP/1.1")]
    [InlineData("get / HTTP/1.1")]
    [InlineData("GET  / HTTP/1.1")]
    [InlineData("GET / HTTP/1.1\r\nBad Header: x")]
    [InlineData("GET / HTTP/1.1\r\nX: a\r\n folded")]
    [InlineData("GET / HTTP/1.1\r\n: empty-name")]
    public void MalformedHeadsAreRefused(string text) => Assert.Null(Parse(text));

    [Fact]
    public void NonAsciiAndNulAreRefused() {
        Assert.Null(HttpRequestHead.Parse("GET / HTTP/1.1\r\nX: é"u8.ToArray()));
        Assert.Null(HttpRequestHead.Parse(new byte[] { (byte) 'G', 0, (byte) 'T' }));
        Assert.Null(HttpRequestHead.Parse(new byte[HttpRequestHead.MaxHeadBytes + 1]));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("\r\nContent-Length: 7", 7)]
    [InlineData("\r\nTransfer-Encoding: chunked", -1)]
    [InlineData("\r\nContent-Length: 7\r\nContent-Length: 7", -1)]
    [InlineData("\r\nContent-Length: -1", -1)]
    [InlineData("\r\nContent-Length: 1e3", -1)]
    [InlineData("\r\nContent-Length: +5", -1)]
    public void BodyLengthsAreStrict(string extra, long expected)
        => Assert.Equal(expected, Parse("POST /x HTTP/1.1" + extra)!.ContentLength);

    [Fact]
    public void TheUpstreamHeadDropsForgedVisitorAndProxyHeaders() {
        var head = Parse("POST /friends/api/register HTTP/1.1\r\nHost: play.example.com\r\nOrigin: https://play.example.com\r\n" +
                         "X-W101C-Visitor: 192.168.1.5\r\nx-w101c-visitor: 10.0.0.1\r\nCF-Connecting-IP: 192.168.1.6\r\n" +
                         "X-Forwarded-For: 10.0.0.2\r\nForwarded: for=10.0.0.3\r\nX-Real-IP: 10.0.0.4\r\nConnection: keep-alive\r\n" +
                         "Content-Type: application/json\r\nContent-Length: 2\r\nX-W101C: 1")!;
        var upstream = LoopbackForwarder.UpstreamHead(head, 12381, "X-W101C-Visitor", "203.0.113.9");
        Assert.StartsWith("POST /friends/api/register HTTP/1.1\r\nHost: 127.0.0.1:12381\r\n", upstream);
        Assert.EndsWith("X-W101C-Visitor: 203.0.113.9\r\nConnection: close\r\n\r\n", upstream);
        Assert.Single(upstream.Split("\r\n"), l => l.StartsWith("X-W101C-Visitor", StringComparison.OrdinalIgnoreCase));
        foreach (var gone in new[] { "CF-Connecting-IP", "X-Forwarded-For", "Forwarded:", "X-Real-IP", "keep-alive", "play.example.com\r\nOrigin" }) {
            Assert.DoesNotContain(gone, upstream, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("Origin: https://play.example.com\r\n", upstream);
        Assert.Contains("X-W101C: 1\r\n", upstream);
        Assert.Contains("Content-Length: 2\r\n", upstream);
    }
}

public sealed class HttpsFrontTests : IDisposable {
    private readonly string _dir = Directory.CreateTempSubdirectory("w101c-tls-").FullName;

    public void Dispose() {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    /// <summary>A self-signed certificate for 127.0.0.1 / localhost, written as PEM (key mode 600).</summary>
    private (string Cert, string Key, X509Certificate2 Certificate) WriteCertificate(DateTimeOffset? notBefore = null,
                                                                                     DateTimeOffset? notAfter = null, bool privateKey = true) {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=w101c test", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var certificate = request.CreateSelfSigned(notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-5), notAfter ?? DateTimeOffset.UtcNow.AddDays(1));
        var certPath = Path.Combine(_dir, "server.crt");
        var keyPath = Path.Combine(_dir, "server.key");
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(keyPath, privateKey ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return (certPath, keyPath, X509CertificateLoader.LoadCertificate(certificate.RawData));
    }

    private static async Task<string> Echo(Stream stream, IPEndPoint peer) {
        var buffer = new byte[1024];
        var read = await stream.ReadAsync(buffer);
        var reply = Encoding.ASCII.GetBytes("you sent " + Encoding.ASCII.GetString(buffer, 0, read) + " from " + peer.Address);
        await stream.WriteAsync(reply);
        await stream.FlushAsync();
        return "";
    }

    /// <summary>Connects, trusting exactly <paramref name="pinned"/> (as a launcher pinned to the owner's CA would).</summary>
    private static async Task<SslStream> Connect(int port, X509Certificate2 pinned, SslProtocols protocols = SslProtocols.None) {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
            certificate is not null && certificate.GetCertHashString(HashAlgorithmName.SHA256) == pinned.GetCertHashString(HashAlgorithmName.SHA256));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
            TargetHost = "localhost", EnabledSslProtocols = protocols,
        }, timeout.Token);
        return tls;
    }

    private static async Task<string> RoundTrip(SslStream tls, string text) {
        await tls.WriteAsync(Encoding.ASCII.GetBytes(text));
        var buffer = new byte[1024];
        var read = await tls.ReadAsync(buffer);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    [Fact]
    public async Task ServesOverTlsWithTheOwnersCertificate() {
        var (cert, key, certificate) = WriteCertificate();
        var logs = new List<string>();
        using var front = new HttpsFront(IPAddress.Loopback, 0, new TlsCertificateSource(cert, key), _ => true, Echo, logs.Add);
        front.Start();
        await using var tls = await Connect(front.Port, certificate);
        Assert.True(tls.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13);
        Assert.Equal("you sent hello from 127.0.0.1", await RoundTrip(tls, "hello"));
    }

    [Fact]
    public async Task PlainHttpGetsNoAnswer() {
        var (cert, key, _) = WriteCertificate();
        using var front = new HttpsFront(IPAddress.Loopback, 0, new TlsCertificateSource(cert, key), _ => true, Echo);
        front.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, front.Port);
        var stream = client.GetStream();
        await stream.WriteAsync("POST /launcher/login HTTP/1.1\r\nContent-Length: 2\r\n\r\n{}"u8.ToArray());
        var buffer = new byte[256];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var read = await stream.ReadAsync(buffer, timeout.Token);
        Assert.DoesNotContain("you sent", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task AKeyOthersCanReadIsNeverServed() {
        if (OperatingSystem.IsWindows()) return;
        var (cert, key, certificate) = WriteCertificate(privateKey: false);
        var source = new TlsCertificateSource(cert, key);
        Assert.Null(source.Current(out var problem));
        Assert.Contains("chmod 600", problem);
        var logs = new List<string>();
        using var front = new HttpsFront(IPAddress.Loopback, 0, source, _ => true, Echo, logs.Add);
        front.Start();
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(front.Port, certificate));
    }

    [Fact]
    public void ExpiredAndFutureCertificatesAreRefused() {
        var (cert, key, _) = WriteCertificate(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));
        Assert.Null(new TlsCertificateSource(cert, key).Current(out var expired));
        Assert.Equal("the certificate has expired", expired);
        var (cert2, key2, _) = WriteCertificate(DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(10));
        Assert.Null(new TlsCertificateSource(cert2, key2).Current(out var future));
        Assert.Equal("the certificate is not valid yet", future);
    }

    [Fact]
    public void AMismatchedOrMissingKeyIsRefused() {
        var (cert, _, _) = WriteCertificate();
        var other = Path.Combine(_dir, "other.key");
        using (var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256)) File.WriteAllText(other, stranger.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(other, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Null(new TlsCertificateSource(cert, other).Current(out var mismatch));
        Assert.NotNull(mismatch);
        Assert.Null(new TlsCertificateSource(cert, Path.Combine(_dir, "missing.key")).Current(out var missing));
        Assert.NotNull(missing);
    }

    [Fact]
    public async Task ARenewedCertificateIsServedWithoutARestart() {
        var (cert, key, first) = WriteCertificate();
        using var front = new HttpsFront(IPAddress.Loopback, 0, new TlsCertificateSource(cert, key), _ => true, Echo);
        front.Start();
        await using (var tls = await Connect(front.Port, first)) Assert.StartsWith("you sent", await RoundTrip(tls, "a"));
        var (_, _, second) = WriteCertificate();
        File.SetLastWriteTimeUtc(cert, DateTime.UtcNow.AddSeconds(5)); // a different stamp even within one tick
        Assert.NotEqual(first.Thumbprint, second.Thumbprint);
        await using (var tls = await Connect(front.Port, second)) Assert.StartsWith("you sent", await RoundTrip(tls, "b"));
    }

    [Fact]
    public async Task TheOffSwitchRefusesNewAndClosesOpenConnections() {
        var (cert, key, certificate) = WriteCertificate();
        var admit = true;
        var logs = new List<string>();
        var release = new TaskCompletionSource();
        using var front = new HttpsFront(IPAddress.Loopback, 0, new TlsCertificateSource(cert, key), _ => admit,
            async (stream, _) => { await stream.WriteAsync("ready"u8.ToArray()); await release.Task.WaitAsync(TimeSpan.FromSeconds(20)); }, logs.Add);
        front.Start();
        await using var open = await Connect(front.Port, certificate);
        var buffer = new byte[16];
        Assert.Equal(5, await open.ReadAsync(buffer));
        admit = false;
        Assert.Equal(1, front.Sweep());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var closed = await Record.ExceptionAsync(async () => {
            var read = await open.ReadAsync(buffer, timeout.Token);
            Assert.Equal(0, read);
        });
        Assert.True(closed is null or IOException, closed?.ToString());
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(front.Port, certificate));
        release.TrySetResult();
    }

    [Fact]
    public async Task ConnectionsPerAddressAreCapped() {
        var (cert, key, certificate) = WriteCertificate();
        var release = new TaskCompletionSource();
        using var front = new HttpsFront(IPAddress.Loopback, 0, new TlsCertificateSource(cert, key), _ => true,
            async (stream, _) => { await stream.WriteAsync("x"u8.ToArray()); await release.Task.WaitAsync(TimeSpan.FromSeconds(20)); },
            maxPerAddress: 2);
        front.Start();
        await using var a = await Connect(front.Port, certificate);
        await using var b = await Connect(front.Port, certificate);
        var one = new byte[1];
        await a.ReadExactlyAsync(one);
        await b.ReadExactlyAsync(one);
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(front.Port, certificate));
        release.TrySetResult();
    }
}

public sealed class LoopbackForwarderTests {

    [Fact]
    public async Task ForwardsOneRequestWithTheVisitorAndStreamsTheAnswer() {
        var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var port = ((IPEndPoint) upstream.LocalEndpoint).Port;
        var received = Task.Run(async () => {
            using var peer = await upstream.AcceptTcpClientAsync();
            var stream = peer.GetStream();
            var buffer = new byte[8192];
            var filled = 0;
            while (!Encoding.ASCII.GetString(buffer, 0, filled).Contains("{\"a\":1}")) {
                var read = await stream.ReadAsync(buffer.AsMemory(filled));
                if (read == 0) break;
                filled += read;
            }

            await stream.WriteAsync("HTTP/1.1 201 Created\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
            return Encoding.ASCII.GetString(buffer, 0, filled);
        });
        var request = "POST /friends/api/register HTTP/1.1\r\nHost: x\r\nX-W101C-Visitor: 192.168.1.9\r\nContent-Length: 7\r\n\r\n{\"a\"";
        var bytes = Encoding.ASCII.GetBytes(request);
        var headEnd = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = HttpRequestHead.Parse(bytes.AsSpan(0, headEnd))!;
        var buffer = new byte[8192];
        bytes.CopyTo(buffer, 0);
        var client = new DuplexStream(":1}"u8.ToArray());
        Assert.True(await LoopbackForwarder.ForwardAsync(client, head, buffer, bytes.Length, headEnd + 4, port, "X-W101C-Visitor", "203.0.113.9"));
        var seen = await received;
        Assert.Contains("Host: 127.0.0.1:" + port + "\r\n", seen);
        Assert.Contains("X-W101C-Visitor: 203.0.113.9\r\n", seen);
        Assert.DoesNotContain("192.168.1.9", seen);
        Assert.EndsWith("\r\n\r\n{\"a\":1}", seen);
        Assert.EndsWith("\r\n\r\nok", Encoding.ASCII.GetString(client.Written.ToArray()));
        upstream.Stop();
    }

    [Theory]
    [InlineData("Transfer-Encoding: chunked", "411")]
    [InlineData("Content-Length: 999999", "413")]
    public async Task ChunkedAndLargeBodiesAreRefusedHere(string header, string status) {
        var head = HttpRequestHead.Parse(Encoding.ASCII.GetBytes("POST /friends/api/register HTTP/1.1\r\n" + header))!;
        var client = new DuplexStream([]);
        Assert.False(await LoopbackForwarder.ForwardAsync(client, head, new byte[16], 0, 0, 1, "X-W101C-Visitor", "203.0.113.9"));
        Assert.StartsWith("HTTP/1.1 " + status, Encoding.ASCII.GetString(client.Written.ToArray()));
    }

    [Fact]
    public async Task NoUpstreamIsABadGateway() {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint) free.LocalEndpoint).Port;
        free.Stop();
        var head = HttpRequestHead.Parse("GET /friends/ HTTP/1.1"u8)!;
        var client = new DuplexStream([]);
        Assert.False(await LoopbackForwarder.ForwardAsync(client, head, new byte[16], 0, 0, port, "X-W101C-Visitor", "203.0.113.9"));
        Assert.StartsWith("HTTP/1.1 502", Encoding.ASCII.GetString(client.Written.ToArray()));
    }

    /// <summary>Reads from a fixed input; records what is written.</summary>
    private sealed class DuplexStream(byte[] input) : Stream {
        private readonly MemoryStream _input = new(input);
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    }
}
