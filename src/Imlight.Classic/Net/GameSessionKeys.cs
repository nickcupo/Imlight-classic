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
 * attach consumes it. A new character select replaces the account's key. The
 * key can also be bound to the address that selected the character.
 *
 * Transfers (MSG_SERVERTRANSFER: zone change, realm transfer, attach
 * fallback): the r806919 client copies the whole transfer message over its
 * stored MSG_CHARACTERSELECTED record and builds MSG_ATTACH from it
 * (GameClient::AppSessionEstablished): each attach field is assigned from the
 * record field of the same name. It never resends the character-select key.
 * LoginKey (STR) assigned from the transfer's Key (INT) comes out EMPTY on
 * the live client (2026-10-06), while GID fields copy fine (UserID, CharID,
 * TargetPlayerID, ZoneID, SessionID). So every transfer issues a fresh proof
 * (IssueTransfer): a random non-zero 64-bit SessionID the client echoes in
 * MSG_ATTACH.SessionID, plus a random 31-bit Key/FallbackKey for clients that
 * do send it as LoginKey ("%d"). Either one consumes the same single-use
 * entry: armed for the validity window, bound to the account and the
 * address, replacing the previous key; a few wrong keys naming the account
 * disarm it (MaxTransferFailures).
 *
 * History: 2026-10-05 re-armed the select key (the client attached with
 * "0"); the first fix relied on Key alone (the client attached with "").
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/06/2026
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
        public bool IsTransfer;
        public int Failures;
        public string? AltHash; // a transfer's SessionID proof; the same entry as its Key
        public byte[]? AltKey;
    }

    /// <summary>A transfer's proofs: MSG_SERVERTRANSFER.SessionID (echoed in MSG_ATTACH.SessionID) and Key/FallbackKey.</summary>
    public readonly record struct TransferKey(int Key, ulong SessionId);

    /// <summary>Wrong keys naming an account that disarm its armed transfer key (it has only 31 random bits).</summary>
    public const int MaxTransferFailures = 3;

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
                return _byAccount.Count;
            }
        }
    }

    /// <summary>Forgets the account's entry under every hash it is filed under.</summary>
    private Entry? RemoveAccountEntry(ulong accountId) {
        if (!_byAccount.Remove(accountId, out var hash) || !_byKeyHash.Remove(hash, out var entry)) {
            return null;
        }

        if (entry.AltHash is { } alt) {
            _byKeyHash.Remove(alt);
        }

        return entry;
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
            RemoveAccountEntry(accountId);

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
    /// Fresh proofs for a MSG_SERVERTRANSFER, armed for one attach; the account's previous key (from character select
    /// or an earlier transfer) stops working. Send <see cref="TransferKey.SessionId"/> as the transfer's SessionID and
    /// <see cref="TransferKey.Key"/> as its Key and FallbackKey.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="address">The client's address (no port); the previous key's address when null.</param>
    public TransferKey IssueTransfer(ulong accountId, string? address) {
        lock (_lock) {
            Prune();
            var previousAddress = RemoveAccountEntry(accountId)?.Address;

            int value;
            ulong session;
            string text, hash, proof, proofHash;
            do {
                value = RandomNumberGenerator.GetInt32(1, int.MaxValue);
                session = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) | 1UL; // never 0 (0 = not sent)
                text = TransferKeyText(value);
                hash = HashOf(text);
                proof = SessionProofText(session);
                proofHash = HashOf(proof);
            } while (_byKeyHash.ContainsKey(hash) || _byKeyHash.ContainsKey(proofHash));

            var entry = new Entry {
                Key = Encoding.UTF8.GetBytes(text),
                AccountId = accountId,
                Address = NormalizeAddress(address) ?? previousAddress,
                ArmedUntil = _time.GetUtcNow() + Validity,
                LastActivity = _time.GetUtcNow(),
                IsTransfer = true,
                AltHash = proofHash,
                AltKey = Encoding.UTF8.GetBytes(proof),
            };
            _byKeyHash[hash] = entry;
            _byKeyHash[proofHash] = entry;
            _byAccount[accountId] = hash;
            return new TransferKey(value, session);
        }
    }

    /// <summary>The lookup text of a transfer's SessionID proof (never a LoginKey the client sends).</summary>
    private static string SessionProofText(ulong sessionId)
        => "sid:" + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The LoginKey text the r806919 client sends for a transfer key: DMLField::ToStr of an INT ("%d").</summary>
    public static string TransferKeyText(int key) => key.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether the account's current key is a transfer key issued to <paramref name="address"/> (the attach-timeout
    /// fallback only redirects a connection from the address the account was just transferred to).
    /// </summary>
    public bool HasTransferKeyFor(ulong accountId, string? address) {
        lock (_lock) {
            return _byAccount.TryGetValue(accountId, out var hash) && _byKeyHash.TryGetValue(hash, out var entry)
                && entry.IsTransfer && (!BindAddress || entry.Address is null || entry.Address == NormalizeAddress(address));
        }
    }

    /// <summary>
    /// Allows one more attach with the account's current key: the attach it just made failed after the key check
    /// (MSG_ATTACHFAILED, the client falls back with the same key), or it waited in the login queue. Transfers do not
    /// re-arm: they issue a new key (<see cref="IssueTransfer"/>).
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
            RemoveAccountEntry(accountId);
        }
    }

    /// <summary>
    /// Checks an attach: the key must exist, belong to <paramref name="accountId"/>, be armed and inside its window,
    /// and (when bound) come from the address it was issued to. An accepted key is consumed until re-armed. After a
    /// transfer the r806919 client sends an empty LoginKey and proves the transfer with the SessionID it echoes.
    /// </summary>
    /// <param name="key">MSG_ATTACH.LoginKey.</param>
    /// <param name="accountId">MSG_ATTACH.UserID.</param>
    /// <param name="address">The connection's address.</param>
    /// <param name="sessionId">MSG_ATTACH.SessionID (0 when not sent).</param>
    public GameKeyResult TryConsume(string? key, ulong accountId, string? address, ulong sessionId = 0) {
        lock (_lock) {
            var entry = Find(key);
            if (entry is null && sessionId != 0) {
                entry = Find(SessionProofText(sessionId));
            }

            if (entry is null) {
                CountTransferFailure(accountId);
                return GameKeyResult.UnknownKey;
            }

            if (entry.AccountId != accountId) {
                CountTransferFailure(accountId);
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

    /// <summary>The entry filed under <paramref name="key"/> (its Key or its SessionID proof), compared in constant time.</summary>
    private Entry? Find(string? key) {
        if (string.IsNullOrEmpty(key) || key.Length > 256) {
            return null;
        }

        var given = Encoding.UTF8.GetBytes(key);
        var hash = HashOf(key);
        if (!_byKeyHash.TryGetValue(hash, out var entry)) {
            return null;
        }

        var stored = hash == entry.AltHash ? entry.AltKey! : entry.Key;
        return CryptographicOperations.FixedTimeEquals(stored, given) ? entry : null;
    }

    /// <summary>A wrong key naming an account with an armed transfer key; enough of them disarm it.</summary>
    private void CountTransferFailure(ulong accountId) {
        if (_byAccount.TryGetValue(accountId, out var hash) && _byKeyHash.TryGetValue(hash, out var entry)
                && entry.IsTransfer && entry.ArmedUntil is not null && ++entry.Failures >= MaxTransferFailures) {
            entry.ArmedUntil = null;
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
        var stale = new HashSet<ulong>();
        foreach (var entry in _byKeyHash.Values) {
            if (entry.LastActivity < cutoff && (entry.ArmedUntil is not { } until || until < cutoff)) {
                stale.Add(entry.AccountId);
            }
        }

        foreach (var account in stale) {
            RemoveAccountEntry(account);
        }
    }
}
