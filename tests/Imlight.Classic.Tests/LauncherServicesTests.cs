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
 * LAUNCHER SERVICES TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: what the players' launcher gets from the patch port: published
 * client-patch files (paths kept inside the folder, byte ranges for resumed
 * downloads) and the launcher login (password, saved-login tokens, refusals,
 * the failure limit, nothing secret kept in clear).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Imlight.CoreLib.Classic.ClientPatches;
using Imlight.CoreLib.Classic.Launcher;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class LauncherServicesTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "w101c-patches-" + Guid.NewGuid().ToString("N"));

    public LauncherServicesTests() {
        // The login logs; Logger's type initializer needs the configuration, and if it fails the whole test run
        // loses its logger.
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-launcher-tests.log")}\n");
            Imlight.Common.ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }

        Directory.CreateDirectory(Path.Combine(_root, "files"));
        File.WriteAllText(Path.Combine(_root, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "files", "abc123"), "data");
        File.WriteAllText(Path.Combine(_root, ".hidden"), "no");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "w101c-outside.txt"), "no");
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("/classic/manifest.json", "manifest.json")]
    [InlineData("/classic/files/abc123", "files/abc123")]
    [InlineData("/classic/files%2Fabc123", "files/abc123")]
    public void PublishedFilesResolveInsideTheFolder(string url, string expected)
        => Assert.Equal(Path.GetFullPath(Path.Combine(_root, expected)), ClientPatchFiles.Resolve(_root, url));

    [Theory]
    [InlineData("/classic/../w101c-outside.txt")]
    [InlineData("/classic/%2e%2e/w101c-outside.txt")]
    [InlineData("/classic/files/../../w101c-outside.txt")]
    [InlineData("/classic/.hidden")]
    [InlineData("/classic/files")]
    [InlineData("/classic/")]
    [InlineData("/classic//etc/passwd")]
    [InlineData("/classic/files\\abc123")]
    [InlineData("/other/manifest.json")]
    [InlineData("/classic/missing.json")]
    public void NothingElseResolves(string url) => Assert.Null(ClientPatchFiles.Resolve(_root, url));

    [Fact]
    public void NoFolderServesNothing() => Assert.Null(ClientPatchFiles.Resolve(null, "/classic/manifest.json"));

    [Fact]
    public void RangesForResumedDownloads() {
        Assert.Null(ClientPatchFiles.ParseRange(null, 100));
        Assert.Null(ClientPatchFiles.ParseRange("items=1-2", 100));
        Assert.Equal((40L, 99L), ClientPatchFiles.ParseRange("bytes=40-", 100));
        Assert.Equal((0L, 9L), ClientPatchFiles.ParseRange("bytes=0-9", 100));
        Assert.Equal((90L, 99L), ClientPatchFiles.ParseRange("bytes=-10", 100));
        Assert.Equal((50L, 99L), ClientPatchFiles.ParseRange("bytes=50-500", 100));
        Assert.Equal((-1L, -1L), ClientPatchFiles.ParseRange("bytes=100-", 100));
        Assert.Equal((-1L, -1L), ClientPatchFiles.ParseRange("bytes=9-3", 100));
        Assert.Null(ClientPatchFiles.ParseRange("bytes=0-1,5-6", 100));
    }

    // ------------------------------------------------------------ launcher login

    private sealed class FakeAccounts : ILauncherAccounts {
        internal readonly Dictionary<string, (ulong, string, bool)> Users = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, (ulong, DateTime)> Tokens = new(StringComparer.Ordinal);
        internal readonly List<(ulong Account, string Key)> Keys = [];
        internal readonly List<string> KeyAddresses = [];
        public (ulong Id, string PasswordHash, bool Blocked)? Find(string username) => Users.TryGetValue(username, out var u) ? u : null;
        public void StoreSessionKey(ulong accountId, string sessionKey, string address) {
            Keys.Add((accountId, sessionKey));
            KeyAddresses.Add(address);
        }
        public void SaveToken(string tokenHash, ulong accountId, DateTime expiresUtc) => Tokens[tokenHash] = (accountId, expiresUtc);
        public (ulong AccountId, DateTime ExpiresUtc)? LoadToken(string tokenHash) => Tokens.TryGetValue(tokenHash, out var t) ? t : null;
        public void DeleteToken(string tokenHash) => Tokens.Remove(tokenHash);
    }

    private readonly FakeAccounts _accounts = new();
    private DateTime _now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private bool _anyPassword;

    private LauncherLogin Login(Imlight.Classic.Net.LoginThrottle? throttle = null) {
        _accounts.Users["ada"] = (42, LauncherLogin.HashPassword("wand-of-oak"), false);
        _accounts.Users["locked"] = (43, LauncherLogin.HashPassword("pw"), true);
        return new LauncherLogin(_accounts, () => _anyPassword, () => _now, throttle);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void TheRightPasswordGivesASessionKeyStoredForTheGame() {
        var answer = Parse(Login().Handle("{\"user\":\"ada\",\"password\":\"wand-of-oak\"}", "10.0.0.2"));
        Assert.True(answer.GetProperty("ok").GetBoolean());
        Assert.Equal("42", answer.GetProperty("userId").GetString());
        var key = answer.GetProperty("sessionKey").GetString();
        Assert.Equal((42UL, key), Assert.Single(_accounts.Keys));
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("token").ValueKind);
        Assert.Equal(0UL, LauncherLogin.LauncherMachineId);
    }

    [Fact]
    public void AWrongPasswordOrUserIsRefusedTheSameWay() {
        var login = Login();
        Assert.Equal("bad-login", Parse(login.Handle("{\"user\":\"ada\",\"password\":\"nope\"}", "a")).GetProperty("error").GetString());
        Assert.Equal("bad-login", Parse(login.Handle("{\"user\":\"bob\",\"password\":\"nope\"}", "a")).GetProperty("error").GetString());
        Assert.Equal("locked", Parse(login.Handle("{\"user\":\"locked\",\"password\":\"pw\"}", "a")).GetProperty("error").GetString());
        Assert.Equal("bad-request", Parse(login.Handle("not json", "a")).GetProperty("error").GetString());
        Assert.Equal("bad-request", Parse(login.Handle("{\"user\":\"ada\"}", "a")).GetProperty("error").GetString());
        Assert.Empty(_accounts.Keys);
    }

    [Fact]
    public void AnyPasswordLoginStillNeedsTheAccount() {
        _anyPassword = true;
        var login = Login();
        Assert.True(Parse(login.Handle("{\"user\":\"ada\",\"password\":\"x\"}", "a")).GetProperty("ok").GetBoolean());
        Assert.False(Parse(login.Handle("{\"user\":\"bob\",\"password\":\"x\"}", "a")).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void RememberMeIssuesATokenKeptOnlyAsAHashThatExpiresAndCanBeForgotten() {
        var login = Login();
        var first = Parse(login.Handle("{\"user\":\"ada\",\"password\":\"wand-of-oak\",\"remember\":true}", "a"));
        var token = first.GetProperty("token").GetString()!;
        Assert.DoesNotContain(token, _accounts.Tokens.Keys);
        Assert.Contains(LauncherLogin.Hash(token), _accounts.Tokens.Keys);

        var again = Parse(login.Handle($"{{\"user\":\"ada\",\"token\":\"{token}\"}}", "a"));
        Assert.True(again.GetProperty("ok").GetBoolean());
        Assert.Equal(token, again.GetProperty("token").GetString());
        Assert.NotEqual(first.GetProperty("sessionKey").GetString(), again.GetProperty("sessionKey").GetString());

        Assert.False(Parse(login.Handle($"{{\"user\":\"locked\",\"token\":\"{token}\"}}", "a")).GetProperty("ok").GetBoolean());
        _now += LauncherLogin.TokenLifetime + TimeSpan.FromMinutes(1);
        Assert.False(Parse(login.Handle($"{{\"user\":\"ada\",\"token\":\"{token}\"}}", "b")).GetProperty("ok").GetBoolean());
        _now -= LauncherLogin.TokenLifetime;
        Assert.True(Parse(login.Handle($"{{\"user\":\"ada\",\"token\":\"{token}\",\"forget\":true}}", "c")).GetProperty("ok").GetBoolean());
        Assert.Empty(_accounts.Tokens);
    }

    [Fact]
    public void TenFailuresAMinuteMakeThatAddressWait() {
        var login = Login();
        for (var i = 0; i < 10; i++) login.Handle("{\"user\":\"ada\",\"password\":\"guess\"}", "10.0.0.9");
        Assert.Equal("busy", Parse(login.Handle("{\"user\":\"ada\",\"password\":\"wand-of-oak\"}", "10.0.0.9")).GetProperty("error").GetString());
        Assert.True(Parse(login.Handle("{\"user\":\"ada\",\"password\":\"wand-of-oak\"}", "10.0.0.10")).GetProperty("ok").GetBoolean());
        _now += TimeSpan.FromMinutes(2);
        Assert.True(Parse(login.Handle("{\"user\":\"ada\",\"password\":\"wand-of-oak\"}", "10.0.0.9")).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void TheLauncherKeyIsBoundToTheSigningInAddress() {
        Assert.True(Parse(Login().Handle("{\"user\":\"ada\",\"password\":\"wand-of-oak\"}", "10.0.0.2")).GetProperty("ok").GetBoolean());
        Assert.Equal("10.0.0.2", Assert.Single(_accounts.KeyAddresses));
    }

    [Fact]
    public void RepeatedFailuresLockTheAccountForEveryAddress() {
        var throttle = new Imlight.Classic.Net.LoginThrottle(new Imlight.Classic.Net.LoginThrottleOptions { AccountFailures = 3 });
        var login = Login(throttle);
        for (var i = 0; i < 3; i++) {
            Assert.Equal("bad-login", Parse(login.Handle("{\"user\":\"ada\",\"password\":\"guess\"}", $"10.1.0.{i}")).GetProperty("error").GetString());
        }

        // Even the right password from a fresh address waits out the lockout.
        Assert.Equal("busy", Parse(login.Handle("{\"user\":\"ada\",\"password\":\"wand-of-oak\"}", "10.2.0.1")).GetProperty("error").GetString());
        Assert.Empty(_accounts.Keys);
    }
}
