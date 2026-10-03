/*
 * CLASSIC: KingsIsle's own launcher against our server (Classic/Launcher): the nothing-to-patch file list,
 * the launcher patch server's answer, and the news page.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Last Updated: 10/02/2026
 */
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Imlight.CoreLib.Classic.Launcher;
using Xunit;

namespace Imlight.Classic.Tests;

public class KingsIsleLauncherTests {

    [Fact]
    public void TheFileListNamesAboutAndEachPackage() {
        Assert.Equal(new[] { "About", "Base", "PatchClient" }, LauncherFileList.TableNames(LauncherFileList.Bytes));
    }

    [Fact]
    public void TheFileListIsKingsIslesRecordFormat() {
        var data = LauncherFileList.Build(["Base"]);
        // u32 count (About + Base), then the _TableList schema record: 0x02, kind 1, u16 length 40.
        Assert.Equal(2u, BitConverter.ToUInt32(data, 0));
        Assert.Equal(new byte[] { 2, 1, 40, 0, 4, 0 }, data[4..10]);
        Assert.Equal("Name", Encoding.ASCII.GetString(data, 10, 4));
        Assert.Equal(new byte[] { 9, 0x28 }, data[14..16]);
        // The package tables are empty: the file ends with Base's schema, whose last field is the table name.
        Assert.EndsWith("\u0004\0Base", Encoding.ASCII.GetString(data));
        Assert.Contains("HeaderCRC", Encoding.ASCII.GetString(data));
    }

    [Fact]
    public void EveryRecordLengthAddsUp() {
        var data = LauncherFileList.Bytes;
        var at = 0;
        var tables = 0;
        while (at < data.Length) {
            var count = BitConverter.ToUInt32(data, at);
            at += 4;
            for (var i = 0; i <= count; i++) {
                Assert.Equal(2, data[at]);
                at += BitConverter.ToUInt16(data, at + 2);
            }
            tables++;
        }
        Assert.Equal(data.Length, at);
        Assert.Equal(4, tables); // _TableList, About, Base, PatchClient
    }

    [Fact]
    public void ThePatchServerSendsTheLauncherToOurHttpPort() {
        var answer = LauncherPatchServer.Answer("192.168.1.75", 12090, "English", 1234);
        Assert.Equal("http://192.168.1.75:12090/launcher/LatestFileList.bin", answer.ListFileURL);
        Assert.Equal("http://192.168.1.75:12090/launcher", answer.URLPrefix);
        Assert.Equal((uint) LauncherFileList.Bytes.Length, answer.ListFileSize);
        Assert.Equal(LauncherFileList.Crc, answer.ListFileCRC);
        Assert.Equal(1u, answer.ListFileType);
        Assert.Equal("English", answer.Locale);
    }

    [Theory]
    [InlineData("12501", 12501)]
    [InlineData(" 12501 ", 12501)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("0", 0)]
    [InlineData("70000", 0)]
    [InlineData("x", 0)]
    public void ThePortIsOffUnlessSet(string? value, int port) => Assert.Equal(port, LauncherPatchServer.ParsePort(value));

    [Fact]
    public void TheUrlHostIsTheAddressTheLauncherReached() {
        Assert.Equal("192.168.1.75", LauncherPatchServer.AddressText(new IPEndPoint(IPAddress.Parse("192.168.1.75"), 12501)));
        Assert.Equal("10.0.0.2", LauncherPatchServer.AddressText(new IPEndPoint(IPAddress.Parse("::ffff:10.0.0.2"), 1)));
        Assert.Equal("[::1]", LauncherPatchServer.AddressText(new IPEndPoint(IPAddress.IPv6Loopback, 1)));
        Assert.Equal("127.0.0.1", LauncherPatchServer.AddressText(null));
    }

    [Fact]
    public void NewsItemsAreSplitByBlankLines() {
        var items = LauncherNewsPage.Parse("# First\nicon: star\nLine one\nline two\n\n\r\nSecond\r\n");
        Assert.Equal(2, items.Count);
        Assert.Equal(new LauncherNewsPage.Item("First", "Line one line two"), items[0]);
        Assert.Equal(new LauncherNewsPage.Item("Second", ""), items[1]);
    }

    [Fact]
    public void ThePageEscapesTheNewsAndNamesNoOutsideHost() {
        var html = LauncherNewsPage.Render(LauncherNewsPage.Parse("<b>Hi</b>\nA & B"),
            new Dictionary<string, string> { ["logo"] = "/classic/files/x", ["world"] = "/classic/files/y" });
        Assert.Contains("&lt;b&gt;Hi&lt;/b&gt;", html);
        Assert.Contains("A &amp; B", html);
        Assert.Contains("Ravenwood News", html);
        Assert.Contains("src=\"/launcher/art/logo\"", html);
        Assert.Contains("url('/launcher/art/world')", html);
        Assert.DoesNotContain("parchment", html); // not published: drawn in CSS
        Assert.DoesNotContain("http:", html);
        Assert.DoesNotContain("https:", html);
        Assert.DoesNotContain("//", html);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void ArtComesFromThePublishedManifest() {
        var sha = new string('a', 64);
        var urls = LauncherNewsPage.ArtUrls("{\"launcher_art\":[{\"name\":\"logo\",\"sha256\":\"" + sha + "\"},"
                                            + "{\"name\":\"bad\",\"sha256\":\"../../etc\"}]}");
        Assert.Equal(new Dictionary<string, string> { ["logo"] = "/classic/files/" + sha }, urls);
        Assert.Empty(LauncherNewsPage.ArtUrls("not json"));
        Assert.Empty(LauncherNewsPage.ArtUrls(null));
    }

    [Fact]
    public void ThePageReadsThePublishedNewsOrAWelcome() {
        Assert.Contains("Welcome to Wizard101 Classic!", LauncherNewsPage.Render((string?) null));
        var dir = Directory.CreateTempSubdirectory("w101c-news-").FullName;
        try {
            File.WriteAllText(Path.Combine(dir, "news.txt"), "Double XP Weekend\nAll weekend long.");
            var html = LauncherNewsPage.Render(dir);
            Assert.Contains("Double XP Weekend", html);
            Assert.DoesNotContain("Welcome to Wizard101 Classic!", html);
        } finally {
            Directory.Delete(dir, true);
        }
    }
}
