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
 * GAME-SERVER SESSION KEYS
 * ========================================================================
 *
 * PURPOSE:
 * The key a client carries from the login server (MSG_CHARACTERSELECTED.Key)
 * to the game server (MSG_ATTACH.LoginKey).
 *
 * NOTE:
 * Upstream made the key SHA256(accountId || SessionKeyHashInput), a public
 * function of the account id, and the attach never compared it: any
 * connection that named an account id that had selected a character in the
 * last SessionKeyValidityTime attached as that account.
 *
 * Now the key is 32 random bytes, looked up by the key the client sends, and
 * must belong to the account the attach names (constant-time compare). An
 * attach consumes it. The r806919 client keeps the key from character select
 * and sends it again on every MSG_SERVERTRANSFER (zone change, realm
 * transfer, attach fallback) because MSG_SERVERTRANSFER has no string key, so
 * the server re-arms the key exactly when it sends a transfer, for one more
 * attach within the validity window. A new character select replaces the
 * account's key. The key can also be bound to the address that selected the
 * character.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Imlight.Classic.Net;

/// <summary>Why an attach key was accepted or refused.</summary>
public enum GameKeyResult {
    Accepted,
    UnknownKey,
    WrongAccount,
    NotArmed,
    Expired,
    WrongAddress,
}

/// <summary>
/// Issues and checks the game-server attach keys. Thread-safe; one instance serves every game server of the process.
/// </summary>
public sealed class GameSessionKeys {

    /// <summary>The shortest and longest window an issued or re-armed key accepts an attach in.</summary>
    public static readonly TimeSpan MinValidity = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxValidity = TimeSpan.FromMinutes(15);

    private sealed class Entry {
        public required byte[] Key;
        public required ulong AccountId;
        public string? Address;
        public DateTimeOffset? ArmedUntil;
        public DateTimeOffset LastActivity;
    }

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _byKeyHash = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, string> _byAccount = new();

    /// <param name="validity">How long a key accepts its next attach after it is issued or re-armed.</param>
    /// <param name="bindAddress">Whether the attach must come from the address the key was issued to.</param>
    /// <param name="time">The clock; the system clock when null.</param>
    public GameSessionKeys(TimeSpan validity, bool bindAddress = true, TimeProvider? time = null) {
        Validity = ClampValidity(validity);
        BindAddress = bindAddress;
        _time = time ?? TimeProvider.System;
    }

    public TimeSpan Validity { get; }

    public bool BindAddress { get; }

    /// <summary>The configured validity, clamped to <see cref="MinValidity"/>..<see cref="MaxValidity"/> (0 = 5 minutes).</summary>
    public static TimeSpan ClampValidity(TimeSpan configured) {
        if (configured <= TimeSpan.Zero) {
            return TimeSpan.FromMinutes(5);
        }

        return configured < MinValidity ? MinValidity : configured > MaxValidity ? MaxValidity : configured;
    }

    /// <summary>Number of live keys (one per account at most).</summary>
    public int Count {
        get {
            lock (_lock) {
                return _byKeyHash.Count;
            }
        }
    }

    /// <summary>
    /// A new random key for the account, armed for one attach; the account's previous key stops working.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="address">The client's address (no port), or null to not bind it.</param>
    /// <returns>The key as the client carries it: Base64 of 32 random bytes (the length of the old SHA-256 key).</returns>
    public string Issue(ulong accountId, string? address) {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var text = Convert.ToBase64String(bytes);
        var hash = HashOf(text);

        lock (_lock) {
            Prune();
            if (_byAccount.TryGetValue(accountId, out var old)) {
                _byKeyHash.Remove(old);
            }

            _byKeyHash[hash] = new Entry {
                Key = Encoding.UTF8.GetBytes(text),
                AccountId = accountId,
                Address = NormalizeAddress(address),
                ArmedUntil = _time.GetUtcNow() + Validity,
                LastActivity = _time.GetUtcNow(),
            };
            _byAccount[accountId] = hash;
        }

        return text;
    }

    /// <summary>
    /// Allows one more attach with the account's current key (the server is about to send it a MSG_SERVERTRANSFER).
    /// </summary>
    /// <returns>False when the account has no key.</returns>
    public bool Arm(ulong accountId) {
        lock (_lock) {
            if (!_byAccount.TryGetValue(accountId, out var hash) || !_byKeyHash.TryGetValue(hash, out var entry)) {
                return false;
            }

            entry.ArmedUntil = _time.GetUtcNow() + Validity;
            entry.LastActivity = _time.GetUtcNow();
            return true;
        }
    }

    /// <summary>Forgets the account's key.</summary>
    public void Revoke(ulong accountId) {
        lock (_lock) {
            if (_byAccount.Remove(accountId, out var hash)) {
                _byKeyHash.Remove(hash);
            }
        }
    }

    /// <summary>
    /// Checks an attach: the key must exist, belong to <paramref name="accountId"/>, be armed and inside its window,
    /// and (when bound) come from the address it was issued to. An accepted key is consumed until re-armed.
    /// </summary>
    public GameKeyResult TryConsume(string? key, ulong accountId, string? address) {
        if (string.IsNullOrEmpty(key) || key.Length > 256) {
            return GameKeyResult.UnknownKey;
        }

        var hash = HashOf(key);
        var given = Encoding.UTF8.GetBytes(key);
        lock (_lock) {
            if (!_byKeyHash.TryGetValue(hash, out var entry) || !CryptographicOperations.FixedTimeEquals(entry.Key, given)) {
                return GameKeyResult.UnknownKey;
            }

            if (entry.AccountId != accountId) {
                return GameKeyResult.WrongAccount;
            }

            if (entry.ArmedUntil is not { } until) {
                return GameKeyResult.NotArmed;
            }

            if (_time.GetUtcNow() > until) {
                entry.ArmedUntil = null;
                return GameKeyResult.Expired;
            }

            if (BindAddress && entry.Address is not null && entry.Address != NormalizeAddress(address)) {
                return GameKeyResult.WrongAddress;
            }

            entry.ArmedUntil = null;
            entry.LastActivity = _time.GetUtcNow();
            return GameKeyResult.Accepted;
        }
    }

    /// <summary>"::ffff:1.2.3.4" and "1.2.3.4" are the same client; ports are dropped by the caller.</summary>
    public static string? NormalizeAddress(string? address) {
        if (string.IsNullOrWhiteSpace(address)) {
            return null;
        }

        var text = address.Trim();
        if (System.Net.IPAddress.TryParse(text, out var ip)) {
            if (ip.IsIPv4MappedToIPv6) {
                ip = ip.MapToIPv4();
            }

            return ip.ToString();
        }

        return text;
    }

    private static string HashOf(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>How long a key nobody issued, armed or used is kept (an account that left long ago).</summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(24);

    private void Prune() {
        if (_byKeyHash.Count < 1024) {
            return;
        }

        var cutoff = _time.GetUtcNow() - IdleLifetime;
        var stale = new List<(string Hash, ulong Account)>();
        foreach (var (hash, entry) in _byKeyHash) {
            if (entry.LastActivity < cutoff && (entry.ArmedUntil is not { } until || until < cutoff)) {
                stale.Add((hash, entry.AccountId));
            }
        }

        foreach (var (hash, account) in stale) {
            _byKeyHash.Remove(hash);
            _byAccount.Remove(account);
        }
    }
}
