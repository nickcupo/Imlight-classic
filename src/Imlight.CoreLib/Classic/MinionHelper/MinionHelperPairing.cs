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
 * MINION HELPER PAIRING
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: ties a Minion Helper (the Mac window a Myth wizard uses to pick
 * their minion's card and target) to a game account without a password.
 * The player types ".minions" in game chat; the server answers with a
 * one-time code; the player types the code into the helper, which trades it
 * for a long-lived token. The token is stored only as a SHA-256 hash, so the
 * database never holds a usable secret. Typing the code proves the helper's
 * owner is logged in to that account.
 *
 * USAGE EXAMPLE:
 * var code = MinionHelperPairing.Shared.IssueCode(account.AccountId);
 * if (MinionHelperPairing.Shared.TryRedeem(code, out var token, out var accountId)) { ... }
 * if (MinionHelperPairing.Shared.TryResolve(token, out var accountId)) { ... }
 *
 * NOTE:
 * Codes last five minutes and work once. Wrong codes count against the
 * connection that sent them (MinionHelperListener closes it after five).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Imlight.CoreLib.WizardData.Databases;

namespace Imlight.CoreLib.Classic.MinionHelper;

/// <summary>Where paired helper tokens live (hashes only).</summary>
internal interface IMinionHelperTokenStore {
    void Save(string tokenHash, ulong accountId, DateTime createdUtc);
    bool TryLoad(string tokenHash, out ulong accountId, out DateTime createdUtc);
}

/// <summary>The persisted token record (RavenDB document <c>MinionHelperTokens/&lt;sha256&gt;</c>).</summary>
internal sealed class MinionHelperToken {
    public ulong AccountId { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Tokens in the player database, so a pairing survives server restarts and deploys.</summary>
internal sealed class RavenMinionHelperTokenStore : IMinionHelperTokenStore {
    private static string DocumentId(string tokenHash) => "MinionHelperTokens/" + tokenHash;

    public void Save(string tokenHash, ulong accountId, DateTime createdUtc) {
        using var session = PlayerDatabase.Instance.Store.OpenSession();
        session.Store(new MinionHelperToken { AccountId = accountId, CreatedUtc = createdUtc }, DocumentId(tokenHash));
        session.SaveChanges();
    }

    public bool TryLoad(string tokenHash, out ulong accountId, out DateTime createdUtc) {
        using var session = PlayerDatabase.Instance.Store.OpenSession();
        var record = session.Load<MinionHelperToken>(DocumentId(tokenHash));
        accountId = record?.AccountId ?? 0;
        createdUtc = record?.CreatedUtc ?? DateTime.MinValue;
        return accountId != 0;
    }
}

/// <summary>An in-memory store for tests.</summary>
internal sealed class MemoryMinionHelperTokenStore : IMinionHelperTokenStore {
    private readonly ConcurrentDictionary<string, (ulong, DateTime)> _tokens = new();
    public void Save(string tokenHash, ulong accountId, DateTime createdUtc) => _tokens[tokenHash] = (accountId, createdUtc);
    public bool TryLoad(string tokenHash, out ulong accountId, out DateTime createdUtc) {
        var found = _tokens.TryGetValue(tokenHash, out var entry);
        (accountId, createdUtc) = found ? entry : (0UL, DateTime.MinValue);
        return found;
    }
}

internal sealed class MinionHelperPairing {
    internal static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    /// <summary>CLASSIC (M6): a paired helper pairs again after this long ([Classic] MinionHelperTokenDays, default 90).</summary>
    internal static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(Math.Clamp(
        Imlight.CoreLib.Auth.SecuritySettings.Int("Classic.MinionHelperTokenDays", 90), 1, 3650));
    /// <summary>CLASSIC (M6): wrong codes from all connections of an address (10 per 15 min, then doubling lockouts),
    /// and from everyone together (100 per 15 min): a six-digit code cannot be guessed by reconnecting.</summary>
    internal const string Everyone = "*";

    private static readonly Lazy<MinionHelperPairing> s_shared = new(() => new MinionHelperPairing(new RavenMinionHelperTokenStore()));

    /// <summary>The server's pairing state, backed by the player database.</summary>
    internal static MinionHelperPairing Shared => s_shared.Value;

    private readonly IMinionHelperTokenStore _store;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, (ulong AccountId, DateTime Expires)> _codes = new(StringComparer.Ordinal);
    // Resolved tokens, so a reconnecting helper does not hit the database every time.
    private readonly ConcurrentDictionary<string, (ulong AccountId, DateTime Created)> _resolved = new(StringComparer.Ordinal);
    private readonly Imlight.Classic.Net.LoginThrottle _wrongCodes;

    internal MinionHelperPairing(IMinionHelperTokenStore store, Func<DateTime> clock = null) {
        _store = store;
        _clock = clock ?? (() => DateTime.UtcNow);
        _wrongCodes = new Imlight.Classic.Net.LoginThrottle(new Imlight.Classic.Net.LoginThrottleOptions {
            AccountFailures = 100, AddressFailures = 10,
            Window = TimeSpan.FromMinutes(15), FirstLockout = TimeSpan.FromMinutes(1), MaxLockout = TimeSpan.FromMinutes(30),
        }, new ClockTime(_clock));
    }

    private sealed class ClockTime(Func<DateTime> clock) : TimeProvider {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(clock(), DateTimeKind.Utc));
    }

    /// <summary>A new six-digit code for this account; any older code of the account stops working.</summary>
    internal string IssueCode(ulong accountId) {
        if (accountId == 0) throw new ArgumentOutOfRangeException(nameof(accountId));
        lock (_gate) {
            Prune();
            foreach (var stale in FindCodes(accountId)) _codes.Remove(stale);
            string code;
            do {
                code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            } while (_codes.ContainsKey(code));
            _codes[code] = (accountId, _clock() + CodeLifetime);
            return code;
        }
    }

    /// <summary>Trades a live code for a new token (once). False for an unknown or expired code.</summary>
    internal bool TryRedeem(string code, out string token, out ulong accountId)
        => TryRedeem(code, null, out token, out accountId);

    /// <summary>As above, from <paramref name="address"/>; wrong codes count against it and against everyone.</summary>
    internal bool TryRedeem(string code, string address, out string token, out ulong accountId) {
        token = null;
        accountId = 0;
        if (_wrongCodes.LockedFor(Everyone, address) is not null) return false;
        var normalized = Normalize(code);
        if (normalized is null) {
            _wrongCodes.Failure(Everyone, address);
            return false;
        }

        lock (_gate) {
            Prune();
            if (!_codes.Remove(normalized, out var entry)) {
                _wrongCodes.Failure(Everyone, address);
                return false;
            }

            accountId = entry.AccountId;
        }

        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var hash = Hash(token);
        var created = _clock();
        _store.Save(hash, accountId, created);
        _resolved[hash] = (accountId, created);
        return true;
    }

    /// <summary>The account a token was paired with. False for an unknown token.</summary>
    internal bool TryResolve(string token, out ulong accountId) {
        accountId = 0;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return false;
        var hash = Hash(token);
        if (!_resolved.TryGetValue(hash, out var known)) {
            if (!_store.TryLoad(hash, out var loadedAccount, out var created)) return false;
            known = (loadedAccount, created);
            _resolved[hash] = known;
        }

        // CLASSIC (M6): tokens expire; a record without a creation time is treated as created now's lifetime ago.
        if (known.Created == DateTime.MinValue || _clock() - known.Created > TokenLifetime) {
            _resolved.TryRemove(hash, out _);
            return false;
        }

        accountId = known.AccountId;
        return true;
    }

    /// <summary>Digits only ("482 177" and "482-177" both work); null if it is not six digits.</summary>
    internal static string Normalize(string code) {
        if (code is null) return null;
        var digits = new StringBuilder(6);
        foreach (var ch in code) {
            if (char.IsAsciiDigit(ch)) digits.Append(ch);
            else if (ch is not (' ' or '-')) return null;
        }

        return digits.Length == 6 ? digits.ToString() : null;
    }

    internal static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private IEnumerable<string> FindCodes(ulong accountId) {
        var found = new List<string>();
        foreach (var (code, entry) in _codes) {
            if (entry.AccountId == accountId) found.Add(code);
        }

        return found;
    }

    private void Prune() {
        var now = _clock();
        var expired = new List<string>();
        foreach (var (code, entry) in _codes) {
            if (entry.Expires <= now) expired.Add(code);
        }

        foreach (var code in expired) _codes.Remove(code);
    }
}
