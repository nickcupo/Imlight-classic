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
 * LAUNCHER LOGIN
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the Wizard101 Classic launcher window logs the player in before the
 * game starts, the way KingsIsle's launcher did, and starts the pinned client
 * already signed in (-U ..USERID SESSIONKEY USERNAME). POST /launcher/login on
 * the patch port (12090), JSON both ways:
 *
 *   {"user":"name","password":"..."}            the password the player typed
 *   {"user":"name","token":"..."}               a "remember me" token from an earlier login
 *   + "remember":true                           also issue a token (30 days)
 *   {"user":"name","token":"...","forget":true} drop that token
 *
 *   -> {"ok":true,"userId":"123","user":"name","sessionKey":"...","token":"..."}
 *   -> {"ok":false,"error":"bad-login"|"locked"|"busy"|"bad-request"}
 *
 * The password is checked like the game's own login (SHA-512 hash in the
 * account, or [Classic] AnyPasswordLogin); the session key is stored for the
 * account with machine id 0, which is what the r806919 client reports when
 * it validates a key it was given on its command line. Tokens are kept as
 * SHA-256 hashes only (RavenDB LauncherTokens/<hash>).
 *
 * NOTE:
 * Never logs a password, key or token. Ten failures a minute from one address
 * and that address waits ("busy").
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic.Launcher;

/// <summary>What the login needs from the account database (tests use their own).</summary>
internal interface ILauncherAccounts {
    /// <summary>(account id, password hash, locked or banned), or null for no such user.</summary>
    (ulong Id, string PasswordHash, bool Blocked)? Find(string username);
    /// <summary>CLASSIC: whether the password is the account's (the Raven store checks the PBKDF2 verifier and
    /// upgrades old records, Auth/PasswordStore); by default against the stored protocol hash.</summary>
    bool CheckPassword(string username, string password)
        => Find(username) is { } account && LauncherLogin.FixedTimeEquals(LauncherLogin.HashPassword(password), account.PasswordHash);
    /// <summary>Stores the login session key the game client validates, bound to the address that signed in.</summary>
    void StoreSessionKey(ulong accountId, string sessionKey, string address);
    void SaveToken(string tokenHash, ulong accountId, DateTime expiresUtc);
    (ulong AccountId, DateTime ExpiresUtc)? LoadToken(string tokenHash);
    void DeleteToken(string tokenHash);
}

/// <summary>The persisted token record (RavenDB document <c>LauncherTokens/&lt;sha256&gt;</c>).</summary>
internal sealed class LauncherToken {
    public ulong AccountId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
}

internal sealed class RavenLauncherAccounts : ILauncherAccounts {
    private static string TokenId(string hash) => "LauncherTokens/" + hash;

    public (ulong Id, string PasswordHash, bool Blocked)? Find(string username) {
        var account = AccountCollection.GetAccount(username);
        if (account is null) return null;
        return (account.AccountId, account.PasswordHash ?? "",
            account.IsLocked || account.InfractionHistory?.IsCurrentlyBanned == true);
    }

    public bool CheckPassword(string username, string password) {
        var account = AccountCollection.GetAccount(username);
        return account is not null && Imlight.CoreLib.Auth.PasswordStore.Verify(account, password);
    }

    public void StoreSessionKey(ulong accountId, string sessionKey, string address)
        => ClientKeyCollection.AddSessionKey(accountId, LauncherLogin.LauncherMachineId, sessionKey, address);

    public void SaveToken(string tokenHash, ulong accountId, DateTime expiresUtc) {
        using var session = PlayerDatabase.Instance.Store.OpenSession();
        session.Store(new LauncherToken { AccountId = accountId, CreatedUtc = DateTime.UtcNow, ExpiresUtc = expiresUtc },
            TokenId(tokenHash));
        session.SaveChanges();
    }

    public (ulong AccountId, DateTime ExpiresUtc)? LoadToken(string tokenHash) {
        using var session = PlayerDatabase.Instance.Store.OpenSession();
        var record = session.Load<LauncherToken>(TokenId(tokenHash));
        return record is null ? null : (record.AccountId, record.ExpiresUtc);
    }

    public void DeleteToken(string tokenHash) {
        using var session = PlayerDatabase.Instance.Store.OpenSession();
        session.Delete(TokenId(tokenHash));
        session.SaveChanges();
    }
}

internal sealed class LauncherLogin {
    /// <summary>The machine id the r806919 client sends when validating a key from its command line.</summary>
    internal const ulong LauncherMachineId = 0;
    internal static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(30);
    private const int FailuresPerMinute = 10;

    private static readonly Lazy<LauncherLogin> s_shared = new(() => new LauncherLogin(new RavenLauncherAccounts(),
        () => ConfigurationManager.Settings["Classic.AnyPasswordLogin"].AsBool(false),
        throttle: Imlight.CoreLib.Auth.SecuritySettings.Logins.Value));

    internal static LauncherLogin Shared => s_shared.Value;

    private readonly ILauncherAccounts _accounts;
    private readonly Func<bool> _anyPassword;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _failures = new(StringComparer.Ordinal);
    // CLASSIC: per-account lockouts shared with the in-client login (Auth/SecuritySettings.Logins).
    private readonly Imlight.Classic.Net.LoginThrottle? _throttle;

    internal LauncherLogin(ILauncherAccounts accounts, Func<bool> anyPassword, Func<DateTime>? clock = null,
                           Imlight.Classic.Net.LoginThrottle? throttle = null) {
        _accounts = accounts;
        _anyPassword = anyPassword;
        _clock = clock ?? (() => DateTime.UtcNow);
        _throttle = throttle;
    }

    /// <summary>Answers one request body from <paramref name="remote"/> (an address, for the failure limit).</summary>
    /// <param name="keyAddress">CLASSIC (go-live): the address the session key is bound to, when it differs from
    /// <paramref name="remote"/>. Behind the local tunnel <paramref name="remote"/> is the visitor (CF-Connecting-IP,
    /// for lockouts and logs) and this is loopback: the visitor's game may connect over IPv4 while its HTTPS login came
    /// over IPv6, so the key stays unbound there (LoginKeyPolicy.SameClient) instead of failing every attach.</param>
    internal string Handle(string body, string remote, string? keyAddress = null) {
        string user, password, token;
        bool remember, forget;
        try {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            user = Text(root, "user").Trim();
            password = Text(root, "password");
            token = Text(root, "token");
            remember = Flag(root, "remember");
            forget = Flag(root, "forget");
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) {
            return Error("bad-request");
        }

        if (user.Length is 0 or > 64 || password.Length > 256 || token.Length > 128 || (password.Length == 0 && token.Length == 0)) {
            return Error("bad-request");
        }

        if (Throttled(remote)) return Error("busy");
        if (_throttle?.LockedFor(user, remote) is not null) {
            Logger.Information("Launcher: login for {0} from {1} refused (locked out after failures)", Logger.Args(user, remote));
            return Error("busy");
        }

        var account = _accounts.Find(user);
        ulong accountId = 0;
        string? tokenHash = token.Length > 0 ? Hash(token) : null;
        if (account is { } found && tokenHash is not null) {
            var saved = _accounts.LoadToken(tokenHash);
            if (saved is { } record && record.AccountId == found.Id && record.ExpiresUtc > _clock()) {
                if (forget) {
                    _accounts.DeleteToken(tokenHash);
                    Logger.Information("Launcher: {0} forgot a saved login", Logger.Args(user));
                    return JsonSerializer.Serialize(new { ok = true });
                }

                accountId = found.Id;
            }
        } else if (account is { } withPassword && password.Length > 0
                   && (_anyPassword() || _accounts.CheckPassword(user, password))) {
            accountId = withPassword.Id;
        }

        if (accountId == 0 || account is null) {
            Fail(remote);
            if (_throttle?.Failure(account is null ? null : user, remote) == true) {
                Logger.Warning("Launcher: {0} / {1} locked out after repeated failures", Logger.Args(user, remote));
            }

            Logger.Information("Launcher: login for {0} from {1} refused", Logger.Args(user, remote));
            return Error("bad-login");
        }

        if (account.Value.Blocked) {
            Logger.Information("Launcher: login for locked account {0}", Logger.Args(user));
            return Error("locked");
        }

        _throttle?.Success(user);
        var sessionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _accounts.StoreSessionKey(accountId, sessionKey, keyAddress ?? remote);
        string? newToken = null;
        if (remember && tokenHash is null) {
            newToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            _accounts.SaveToken(Hash(newToken), accountId, _clock() + TokenLifetime);
        }

        Logger.Information("Launcher: {0} signed in from {1}{2}", Logger.Args(user, remote, tokenHash is null ? "" : " (saved login)"));
        return JsonSerializer.Serialize(new {
            ok = true, userId = accountId.ToString(), user, sessionKey,
            token = newToken ?? (tokenHash is null ? null : token),
        });
    }

    /// <summary>The account's stored form: Base64(SHA-512(UTF-8 password)), as DatabaseUtilities.CreateHashedPassword.</summary>
    internal static string HashPassword(string password)
        => Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes(password)));

    internal static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    internal static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b ?? ""));

    private bool Throttled(string remote) {
        if (!_failures.TryGetValue(remote, out var times)) return false;
        lock (times) {
            var cutoff = _clock() - TimeSpan.FromMinutes(1);
            while (times.Count > 0 && times.Peek() < cutoff) times.Dequeue();
            if (times.Count == 0) {
                // CLASSIC: forget an address whose failures are over (the table grew without bound).
                _failures.TryRemove(new KeyValuePair<string, Queue<DateTime>>(remote, times));
                return false;
            }

            return times.Count >= FailuresPerMinute;
        }
    }

    private void Fail(string remote) {
        if (_failures.Count > 1024) {
            // CLASSIC: drop addresses with no failure in the last minute, so the table stays small.
            var cutoff = _clock() - TimeSpan.FromMinutes(1);
            foreach (var (address, queue) in _failures) {
                lock (queue) {
                    if (queue.Count == 0 || queue.Last() < cutoff) {
                        _failures.TryRemove(new KeyValuePair<string, Queue<DateTime>>(address, queue));
                    }
                }
            }
        }

        var times = _failures.GetOrAdd(remote, _ => new Queue<DateTime>());
        lock (times) times.Enqueue(_clock());
    }

    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool Flag(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Error(string code) => JsonSerializer.Serialize(new { ok = false, error = code });
}
