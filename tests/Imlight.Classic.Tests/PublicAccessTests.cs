// CLASSIC (go-live): the pure rules for friends outside the home network (Imlight.Classic.Net.PublicAccess).
using System.Collections.Generic;
using System.Net;
using Imlight.Classic.Net;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class PublicAccessTests {

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.50.0.1", true)]
    [InlineData("172.16.4.4", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.64.3.2", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("fd00::5", true)]
    [InlineData("fe80::1", true)]
    [InlineData("203.0.113.9", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("8.8.8.8", false)]
    public void PrivateAddresses(string address, bool expected) => Assert.Equal(expected, PublicAccess.IsPrivate(address));

    [Fact]
    public void NothingIsPrivateWithoutAnAddress() {
        Assert.False(PublicAccess.IsPrivate((string?) null));
        Assert.False(PublicAccess.IsPrivate("not an address"));
    }

    [Fact]
    public void OnlyALoopbackPeerMayNameItsVisitor() {
        Assert.Equal("203.0.113.9", PublicAccess.ClientAddress(IPAddress.Loopback, "203.0.113.9"));
        Assert.Equal("2001:db8::7", PublicAccess.ClientAddress(IPAddress.IPv6Loopback, " 2001:db8::7 "));
        Assert.Equal("203.0.113.9", PublicAccess.ClientAddress(IPAddress.Parse("::ffff:127.0.0.1"), "203.0.113.9"));
        // A LAN or internet peer cannot spoof the header.
        Assert.Equal("192.168.1.20", PublicAccess.ClientAddress(IPAddress.Parse("192.168.1.20"), "203.0.113.9"));
        Assert.Equal("198.51.100.2", PublicAccess.ClientAddress(IPAddress.Parse("198.51.100.2"), "127.0.0.1"));
        // No header, or junk: the peer itself.
        Assert.Equal("127.0.0.1", PublicAccess.ClientAddress(IPAddress.Loopback, null));
        Assert.Equal("127.0.0.1", PublicAccess.ClientAddress(IPAddress.Loopback, "evil, 1.2.3.4"));
        Assert.Equal("?", PublicAccess.ClientAddress(null, "203.0.113.9"));
    }

    [Fact]
    public void AnyProxyHeaderMarksARequestAsProxied() {
        var headers = new Dictionary<string, string?>(System.StringComparer.OrdinalIgnoreCase);
        Assert.False(PublicAccess.CameThroughProxy(name => headers.GetValueOrDefault(name)));
        foreach (var name in new[] { "cf-connecting-ip", "CF-Ray", "X-Forwarded-For", "Forwarded", "X-Real-IP" }) {
            headers.Clear();
            headers[name] = "x";
            Assert.True(PublicAccess.CameThroughProxy(n => headers.GetValueOrDefault(n)), name);
        }

        headers.Clear();
        headers["CF-Ray"] = "  ";
        Assert.False(PublicAccess.CameThroughProxy(n => headers.GetValueOrDefault(n)));
    }

    [Fact]
    public void NetworksParseAndMatch() {
        var networks = PublicAccess.ParseNetworks("192.168.1.0/24, 10.50.0.0/24;fd00::/8 203.0.113.9");
        Assert.NotNull(networks);
        Assert.True(PublicAccess.InNetworks(IPAddress.Parse("192.168.1.200"), networks!));
        Assert.True(PublicAccess.InNetworks(IPAddress.Parse("::ffff:10.50.0.75"), networks!));
        Assert.True(PublicAccess.InNetworks(IPAddress.Parse("fd12::1"), networks!));
        Assert.True(PublicAccess.InNetworks(IPAddress.Parse("203.0.113.9"), networks!));
        Assert.False(PublicAccess.InNetworks(IPAddress.Parse("203.0.113.10"), networks!));
        Assert.False(PublicAccess.InNetworks(IPAddress.Parse("192.168.2.1"), networks!));
        Assert.False(PublicAccess.InNetworks(IPAddress.Parse("10.50.1.1"), networks!));
        Assert.True(PublicAccess.InNetworks(IPAddress.Parse("172.20.5.5"), PublicAccess.ParseNetworks("172.16.0.0/12")!));
        Assert.Null(PublicAccess.ParseNetworks(""));
        Assert.Null(PublicAccess.ParseNetworks("192.168.1.0/33"));
        Assert.Null(PublicAccess.ParseNetworks("lan"));
    }

    [Fact]
    public void TheAdvertisedAddressSplitsPrivateFromPublicClients() {
        const string lan = "10.50.0.75", wan = "203.0.113.9";
        // Off: everyone keeps the configured address.
        Assert.Equal(lan, PublicAccess.Advertise(lan, "198.51.100.2", null, null));
        Assert.Equal(lan, PublicAccess.Advertise(lan, "198.51.100.2", "", null));
        // Default split: private stays private.
        Assert.Equal(lan, PublicAccess.Advertise(lan, "192.168.1.20", wan, null));
        Assert.Equal(lan, PublicAccess.Advertise(lan, "127.0.0.1", wan, null));
        Assert.Equal(wan, PublicAccess.Advertise(lan, "198.51.100.2", wan, null));
        Assert.Equal(wan, PublicAccess.Advertise(lan, "2001:db8::2", wan, null));
        Assert.Equal(wan, PublicAccess.Advertise(lan, null, wan, null));
        // Configured networks: a hairpinned friend (seen as the gateway 10.50.0.1) is public, the owner's LAN private.
        var networks = PublicAccess.ParseNetworks("192.168.1.0/24");
        Assert.Equal(lan, PublicAccess.Advertise(lan, "192.168.1.20", wan, networks));
        Assert.Equal(wan, PublicAccess.Advertise(lan, "10.50.0.1", wan, networks));
        Assert.Equal(lan, PublicAccess.Advertise(lan, "127.0.0.1", wan, networks));
    }

    [Theory]
    [InlineData("http://10.50.0.75:12369/V_r806919.Wizard_1_610", "203.0.113.9", "http://203.0.113.9:12369/V_r806919.Wizard_1_610")]
    [InlineData("http://10.50.0.75:12369/V_r1/LatestFileList.bin", "203.0.113.9", "http://203.0.113.9:12369/V_r1/LatestFileList.bin")]
    [InlineData("http://10.50.0.75:12369", "203.0.113.9", "http://203.0.113.9:12369")]
    [InlineData("http://10.50.0.75:12369/", "203.0.113.9", "http://203.0.113.9:12369/")]
    [InlineData("not a url", "203.0.113.9", "not a url")]
    [InlineData("http://10.50.0.75:12369/x", "play.example.com", "http://10.50.0.75:12369/x")]
    public void PatchUrlsTakeTheChosenHost(string url, string host, string expected)
        => Assert.Equal(expected, PublicAccess.WithHost(url, host));

    [Fact]
    public void TheFirstUsableIPv4IsChosen() {
        Assert.Equal("203.0.113.9", PublicAccess.FirstIPv4([IPAddress.Parse("2001:db8::1"), IPAddress.Loopback, IPAddress.Parse("203.0.113.9")]));
        Assert.Null(PublicAccess.FirstIPv4([IPAddress.Parse("2001:db8::1")]));
    }
    [Fact]
    public void ALauncherUsesThePublicHostWhenItSaysSoOrSignedInThroughTheTunnel() {
        const string host = "play.example.com", wan = "203.0.113.9";
        Assert.True(PublicAccess.LauncherUsesPublicHost("play.example.com", false, host, wan));
        Assert.True(PublicAccess.LauncherUsesPublicHost(" PLAY.Example.com. ", false, host, wan));
        Assert.True(PublicAccess.LauncherUsesPublicHost("203.0.113.9", false, host, wan));
        Assert.False(PublicAccess.LauncherUsesPublicHost("10.50.0.75", true, host, wan)); // it said: the LAN address
        Assert.False(PublicAccess.LauncherUsesPublicHost("203.0.113.9", true, host, null));
        // An older launcher that does not say: the public sign-in means the public host (friend builds).
        Assert.True(PublicAccess.LauncherUsesPublicHost(null, true, host, wan));
        Assert.False(PublicAccess.LauncherUsesPublicHost("", false, host, wan));
    }

    [Fact]
    public void AHairpinnedLanMachineIsRememberedFromItsLoginUntilAnotherLoginSaysOtherwise() {
        var clients = new HairpinClients();
        clients.LauncherKey(7, "key-public", usesPublicHost: true);
        Assert.False(clients.UsesPublicHost("192.168.1.86"));
        // The game login validates that key from the LAN machine (the router kept its LAN address).
        Assert.True(clients.Validated(7, "key-public", "::ffff:192.168.1.86", out var nowPublic));
        Assert.True(nowPublic);
        Assert.True(clients.UsesPublicHost("192.168.1.86"));
        Assert.False(clients.Validated(7, "key-public", "192.168.1.86", out _)); // no change, no second log line
        Assert.False(clients.UsesPublicHost("192.168.1.20"));                    // other machines keep the address rule
        // A newer sign-in from a launcher set to the LAN address, or an in-client login with its own key, clears it.
        clients.LauncherKey(7, "key-lan", usesPublicHost: false);
        Assert.True(clients.Validated(7, "key-lan", "192.168.1.86", out nowPublic));
        Assert.False(nowPublic);
        Assert.False(clients.UsesPublicHost("192.168.1.86"));
        // Another account's key never counts, and a stale key no longer does.
        clients.LauncherKey(8, "key-8", usesPublicHost: true);
        Assert.False(clients.Validated(7, "key-8", "192.168.1.86", out _));
        Assert.False(clients.Validated(7, "key-public", "192.168.1.86", out _));
        Assert.False(clients.Validated(8, "key-8", "not an address", out _));
        Assert.False(clients.UsesPublicHost(null));
    }
}
