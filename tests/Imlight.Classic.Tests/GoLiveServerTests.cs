// CLASSIC (go-live): the server halves of outside access: the tunnel's login listener, the downloads-only port,
// the admin guard, the friend-page proxy listener and the login port's bucket. Synthetic accounts only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Imlight.Classic.Net;
using Imlight.CoreLib.Auth;
using Imlight.CoreLib.Classic.Admin;
using Imlight.CoreLib.Classic.Launcher;
using Imlight.CoreLib.Classic.MinionHelper;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class GoLiveServerTests {

    public GoLiveServerTests() {
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, "[Logging]\nLogLevel=FATAL\nLogPath=" + Path.Combine(Path.GetTempPath(), "imlight-golive-tests.log") +
                                      "\n[Classic]\nTrustedProxyHeader=CF-Connecting-IP\n");
            Imlight.Common.ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
    }

    [Fact]
    public void TheTunnelListenerOnlyLogsInAndTheForwardedPortOnlyDownloadsForOutsiders() {
        var outside = IPAddress.Parse("198.51.100.2");
        var lan = IPAddress.Parse("192.168.1.20");
        Assert.Equal(MinionHelperListener.Exposure.ProxyLogin, MinionHelperListener.ExposureFor(true, IPAddress.Loopback, false));
        Assert.Equal(MinionHelperListener.Exposure.Full, MinionHelperListener.ExposureFor(false, outside, false));
        Assert.Equal(MinionHelperListener.Exposure.DownloadsOnly, MinionHelperListener.ExposureFor(false, outside, true));
        Assert.Equal(MinionHelperListener.Exposure.Full, MinionHelperListener.ExposureFor(false, lan, true));
        Assert.Equal(MinionHelperListener.Exposure.Full, MinionHelperListener.ExposureFor(false, IPAddress.Loopback, true));
        Assert.True(MinionHelperListener.PublicDownloadPath("/classic/tools/tools.json"));
        Assert.True(MinionHelperListener.PublicDownloadPath("/launcher/news"));
        Assert.False(MinionHelperListener.PublicDownloadPath("/"));
        Assert.False(MinionHelperListener.PublicDownloadPath("/ws"));
        Assert.False(MinionHelperListener.PublicDownloadPath("/index.html"));
    }

    [Fact]
    public void ThroughTheTunnelLockoutsSeeTheVisitorAndTheKeyStaysUnbound() {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Cf-Connecting-Ip"] = "203.0.113.9" };
        var (address, key) = MinionHelperListener.LoginAddresses(MinionHelperListener.Exposure.ProxyLogin, IPAddress.Loopback, headers);
        Assert.Equal("203.0.113.9", address);
        Assert.Equal("127.0.0.1", key);
        Assert.True(LoginKeyPolicy.SameClient(key, "198.51.100.77")); // the game may come from another address family
        // Directly (LAN): the header means nothing and the key is bound to the peer, as before.
        (address, key) = MinionHelperListener.LoginAddresses(MinionHelperListener.Exposure.Full, IPAddress.Parse("192.168.1.20"), headers);
        Assert.Equal("192.168.1.20", address);
        Assert.Null(key);
        // No header through the tunnel: every visitor shares loopback (still limited, never bound).
        (address, key) = MinionHelperListener.LoginAddresses(MinionHelperListener.Exposure.ProxyLogin, IPAddress.Loopback,
            new Dictionary<string, string>());
        Assert.Equal("127.0.0.1", address);
        Assert.Equal("127.0.0.1", key);
    }

    private sealed class Accounts : ILauncherAccounts {
        internal readonly List<string> KeyAddresses = [];
        public (ulong Id, string PasswordHash, bool Blocked)? Find(string username)
            => username == "ada" ? (42UL, LauncherLogin.HashPassword("wand-of-oak"), false) : null;
        public void StoreSessionKey(ulong accountId, string sessionKey, string address) => KeyAddresses.Add(address);
        public void SaveToken(string tokenHash, ulong accountId, DateTime expiresUtc) { }
        public (ulong AccountId, DateTime ExpiresUtc)? LoadToken(string tokenHash) => null;
        public void DeleteToken(string tokenHash) { }
    }

    [Fact]
    public void OneGuessingVisitorDoesNotLockOutAnotherBehindTheSameTunnel() {
        var accounts = new Accounts();
        var throttle = new LoginThrottle(new LoginThrottleOptions { AccountFailures = 100, AddressFailures = 3 });
        var login = new LauncherLogin(accounts, () => false, throttle: throttle);
        for (var i = 0; i < 3; i++) login.Handle("{\"user\":\"nobody\",\"password\":\"guess\"}", "198.51.100.66", "127.0.0.1");
        Assert.Equal("busy", JsonDocument.Parse(login.Handle("{\"user\":\"ada\",\"password\":\"wand-of-oak\"}", "198.51.100.66", "127.0.0.1"))
            .RootElement.GetProperty("error").GetString());
        var friend = JsonDocument.Parse(login.Handle("{\"user\":\"ada\",\"password\":\"wand-of-oak\"}", "203.0.113.9", "127.0.0.1")).RootElement;
        Assert.True(friend.GetProperty("ok").GetBoolean());
        Assert.Equal("127.0.0.1", Assert.Single(accounts.KeyAddresses));
        // Without a key address the key is bound to the caller, as before.
        login.Handle("{\"user\":\"ada\",\"password\":\"wand-of-oak\"}", "192.168.1.20");
        Assert.Equal("192.168.1.20", accounts.KeyAddresses[1]);
    }

    [Fact]
    public void TheAdminPageAnswersOnlyThePrivateNetworksAndNeverATunnel() {
        string? None(string _) => null;
        Assert.True(AdminDashboard.AdminAllowed(IPAddress.Parse("192.168.1.20"), None));
        Assert.True(AdminDashboard.AdminAllowed(IPAddress.Loopback, None));
        Assert.False(AdminDashboard.AdminAllowed(IPAddress.Parse("198.51.100.2"), None));
        Assert.False(AdminDashboard.AdminAllowed(null, None));
        Assert.False(AdminDashboard.AdminAllowed(IPAddress.Loopback, name => name == "CF-Connecting-IP" ? "192.168.1.20" : null));
        Assert.False(AdminDashboard.AdminAllowed(IPAddress.Parse("192.168.1.20"), name => name == "X-Forwarded-For" ? "1.2.3.4" : null));
    }

    [Fact]
    public void TheLoginPortCanHaveItsOwnBucket() {
        Assert.Null(SecuritySettings.LoginBucket(12333, 12000, 60, 20, 5)); // the game port keeps the shared one
        Assert.Null(SecuritySettings.LoginBucket(12000, 12000, 0, 20, 5));  // off
        Assert.Equal((60, 20, (byte) 5), SecuritySettings.LoginBucket(12000, 12000, 60, 20, 5));
        Assert.Equal((60, 20, (byte) 5), SecuritySettings.LoginBucket(12000, 12000, 60, 0, 0));
        Assert.Equal((60, 20, (byte) 255), SecuritySettings.LoginBucket(12000, 12000, 60, 20, 900));
    }

    [Fact]
    public async Task TheFriendProxyServesOnlyFriendPathsAndNeedsItsOwnHostHeader() {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint) reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var seen = new List<string>();
        _ = Task.Run(async () => {
            while (listener.IsListening) {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch (Exception) { break; }
                AdminDashboard.HandleFriendProxy(context, c => {
                    var path = c.Request.Url!.AbsolutePath;
                    if (!path.StartsWith("/friends", StringComparison.Ordinal)) return false;
                    lock (seen) seen.Add(path);
                    c.Response.StatusCode = 200;
                    c.Response.Close();
                    return true;
                });
            }
        });
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("friends/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("")).StatusCode);
        // The public name as Host does not reach the page: cloudflared must send httpHostHeader 127.0.0.1:<port>.
        using var foreign = new HttpRequestMessage(HttpMethod.Get, "friends/");
        foreign.Headers.Host = "w101.example.com";
        var status = (await client.SendAsync(foreign)).StatusCode;
        Assert.NotEqual(HttpStatusCode.OK, status);
        lock (seen) Assert.Equal(new[] { "/friends/" }, seen);
        listener.Stop();
    }
}
