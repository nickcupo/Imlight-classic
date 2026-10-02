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
    void Save(string tokenHash, ulong accountId);
    bool TryLoad(string tokenHash, out ulong accountId);
}

/// <summary>The persisted token record (RavenDB document <c>MinionHelperTokens/&lt;sha256&gt;</c>).</summary>
internal sealed class MinionHelperToken {
    public ulong AccountId { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Tokens in the player database, so a pairing survives server restarts and deploys.</summary>
internal sealed class RavenMinionHelperTokenStore : IMinionHelperTokenStore {
    private static string DocumentId(string tokenHash) => "MinionHelperTokens/" + tokenHash;

    public void Save(string tokenHash, ulong accountId) {
        using var session = PlayerDatabase.Instance.Store.OpenSession();
        session.Store(new MinionHelperToken { AccountId = accountId, CreatedUtc = DateTime.UtcNow }, DocumentId(tokenHash));
        session.SaveChanges();
    }

    public bool TryLoad(string tokenHash, out ulong accountId) {
        using var session = PlayerDatabase.Instance.Store.OpenSession();
        var record = session.Load<MinionHelperToken>(DocumentId(tokenHash));
        accountId = record?.AccountId ?? 0;
        return accountId != 0;
    }
}

/// <summary>An in-memory store for tests.</summary>
internal sealed class MemoryMinionHelperTokenStore : IMinionHelperTokenStore {
    private readonly ConcurrentDictionary<string, ulong> _tokens = new();
    public void Save(string tokenHash, ulong accountId) => _tokens[tokenHash] = accountId;
    public bool TryLoad(string tokenHash, out ulong accountId) => _tokens.TryGetValue(tokenHash, out accountId);
}

internal sealed class MinionHelperPairing {
    internal static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    private static readonly Lazy<MinionHelperPairing> s_shared = new(() => new MinionHelperPairing(new RavenMinionHelperTokenStore()));

    /// <summary>The server's pairing state, backed by the player database.</summary>
    internal static MinionHelperPairing Shared => s_shared.Value;

    private readonly IMinionHelperTokenStore _store;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, (ulong AccountId, DateTime Expires)> _codes = new(StringComparer.Ordinal);
    // Resolved tokens, so a reconnecting helper does not hit the database every time.
    private readonly ConcurrentDictionary<string, ulong> _resolved = new(StringComparer.Ordinal);

    internal MinionHelperPairing(IMinionHelperTokenStore store, Func<DateTime> clock = null) {
        _store = store;
        _clock = clock ?? (() => DateTime.UtcNow);
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
    internal bool TryRedeem(string code, out string token, out ulong accountId) {
        token = null;
        accountId = 0;
        var normalized = Normalize(code);
        if (normalized is null) return false;
        lock (_gate) {
            Prune();
            if (!_codes.Remove(normalized, out var entry)) return false;
            accountId = entry.AccountId;
        }

        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var hash = Hash(token);
        _store.Save(hash, accountId);
        _resolved[hash] = accountId;
        return true;
    }

    /// <summary>The account a token was paired with. False for an unknown token.</summary>
    internal bool TryResolve(string token, out ulong accountId) {
        accountId = 0;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return false;
        var hash = Hash(token);
        if (_resolved.TryGetValue(hash, out accountId)) return true;
        if (!_store.TryLoad(hash, out accountId)) return false;
        _resolved[hash] = accountId;
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
