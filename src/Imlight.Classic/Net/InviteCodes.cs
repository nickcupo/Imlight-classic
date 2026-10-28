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
 * INVITE CODES
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (outside-access): one sign-up per friend. The owner makes a code on
 * the admin page ("for Sam", valid 7 days), passes it to that friend, and the
 * friend page creates exactly one ordinary account with it
 * ([Classic] FriendSignupInviteRequired = true).
 *
 * A code is 12 Crockford base-32 characters (60 random bits), shown once as
 * XXXX-XXXX-XXXX. Only SHA-256 of the normalized code is stored; the record's
 * id is the first 12 hex digits of that hash (for listing and revoking).
 * Redeeming is serialized: the account is created while the book is locked,
 * and the code is spent only when the account was created.
 *
 * NOTE:
 * Never log a code. The id is safe to log.
 *
 * Created by: Nick with Claude Code
 * Version: KALI 1.0
 * Last Updated: 10/28/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Imlight.Classic.Net;

public sealed class InviteRecord {
    public string Hash { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public string? UsedBy { get; set; }
    public DateTime? UsedUtc { get; set; }
    public bool Revoked { get; set; }

    public string ShortId => InviteCodes.IdOf(Hash);

    public string State(DateTime now)
        => Revoked ? "revoked" : UsedBy is not null ? "used" : ExpiresUtc <= now ? "expired" : "open";
}

/// <summary>Where invite records live (RavenDB in the server, a dictionary in tests).</summary>
public interface IInviteStore {
    InviteRecord? Load(string hash);
    void Save(InviteRecord record);
    IReadOnlyList<InviteRecord> All();
}

public sealed class MemoryInviteStore : IInviteStore {
    private readonly Dictionary<string, InviteRecord> _records = new(StringComparer.Ordinal);

    public InviteRecord? Load(string hash) {
        lock (_records) return _records.TryGetValue(hash, out var record) ? Copy(record) : null;
    }

    public void Save(InviteRecord record) {
        lock (_records) _records[record.Hash] = Copy(record);
    }

    public IReadOnlyList<InviteRecord> All() {
        lock (_records) return _records.Values.Select(Copy).ToList();
    }

    private static InviteRecord Copy(InviteRecord r) => new() {
        Hash = r.Hash, Label = r.Label, CreatedUtc = r.CreatedUtc, ExpiresUtc = r.ExpiresUtc, UsedBy = r.UsedBy,
        UsedUtc = r.UsedUtc, Revoked = r.Revoked,
    };
}

public enum InviteRedeem { Created, Unknown, Expired, Used, Revoked, NotCreated }

public static class InviteCodes {
    /// <summary>Crockford base 32: no I, L, O or U.</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int Length = 12;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(30);
    public const int MaxLabelLength = 40;

    /// <summary>A new random code, grouped for reading aloud: "7K2M-QX9D-4HRT".</summary>
    public static string NewCode() {
        var chars = new char[Length];
        for (var i = 0; i < Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var raw = new string(chars);
        return $"{raw[..4]}-{raw[4..8]}-{raw[8..]}";
    }

    /// <summary>
    /// The code as typed, normalized: upper case, spaces and dashes dropped, O read as 0 and I/L as 1. Null when it
    /// is not 12 characters of the alphabet.
    /// </summary>
    public static string? Normalize(string? typed) {
        if (typed is null || typed.Length > 40) return null;
        var builder = new StringBuilder(Length);
        foreach (var c in typed.ToUpperInvariant()) {
            if (c is ' ' or '-') continue;
            var mapped = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            if (Alphabet.IndexOf(mapped) < 0) return null;
            builder.Append(mapped);
        }

        return builder.Length == Length ? builder.ToString() : null;
    }

    public static string HashOf(string normalized)
        => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(normalized))).ToLowerInvariant();

    public static string IdOf(string hash) => hash.Length >= 12 ? hash[..12] : hash;

    /// <summary>A label the admin page may store: printable, at most 40 characters, trimmed; null when invalid.</summary>
    public static string? CleanLabel(string? label) {
        var text = (label ?? "").Trim();
        if (text.Length is 0 or > MaxLabelLength || text.Any(char.IsControl)) return null;
        return text;
    }
}

/// <summary>Creates, lists, revokes and redeems invites over one store.</summary>
public sealed class InviteBook {
    private readonly IInviteStore _store;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();

    public InviteBook(IInviteStore store, Func<DateTime>? clock = null) {
        _store = store;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>A new invite; the code is returned once and never stored.</summary>
    public (string Code, InviteRecord Record) Create(string label, TimeSpan? lifetime = null) {
        var clean = InviteCodes.CleanLabel(label) ?? throw new ArgumentException("label");
        var life = lifetime ?? InviteCodes.DefaultLifetime;
        if (life <= TimeSpan.Zero || life > InviteCodes.MaxLifetime) throw new ArgumentOutOfRangeException(nameof(lifetime));
        lock (_gate) {
            while (true) {
                var code = InviteCodes.NewCode();
                var hash = InviteCodes.HashOf(InviteCodes.Normalize(code)!);
                if (_store.Load(hash) is not null) continue; // 60 bits: never in practice
                var now = _clock();
                var record = new InviteRecord { Hash = hash, Label = clean, CreatedUtc = now, ExpiresUtc = now + life };
                _store.Save(record);
                return (code, record);
            }
        }
    }

    public IReadOnlyList<InviteRecord> List() => _store.All().OrderByDescending(r => r.CreatedUtc).ToList();

    /// <summary>Revokes an unused invite by its id. False when there is no such open invite.</summary>
    public bool Revoke(string id) {
        if (string.IsNullOrEmpty(id) || id.Length != 12) return false;
        lock (_gate) {
            var record = _store.All().FirstOrDefault(r => r.ShortId == id);
            if (record is null || record.UsedBy is not null || record.Revoked) return false;
            record.Revoked = true;
            _store.Save(record);
            return true;
        }
    }

    /// <summary>
    /// Redeems <paramref name="typed"/> for <paramref name="username"/>: <paramref name="create"/> runs only for an open
    /// invite, under the book's lock, and the invite is spent only when it returns true.
    /// </summary>
    public InviteRedeem Redeem(string? typed, string username, Func<bool> create, out string? id) {
        id = null;
        var normalized = InviteCodes.Normalize(typed);
        if (normalized is null) return InviteRedeem.Unknown;
        var hash = InviteCodes.HashOf(normalized);
        lock (_gate) {
            var record = _store.Load(hash);
            if (record is null) return InviteRedeem.Unknown;
            id = record.ShortId;
            if (record.Revoked) return InviteRedeem.Revoked;
            if (record.UsedBy is not null) return InviteRedeem.Used;
            if (record.ExpiresUtc <= _clock()) return InviteRedeem.Expired;
            // Spent before the account exists, so a store that fails afterwards can never leave a reusable code.
            record.UsedBy = username;
            record.UsedUtc = _clock();
            _store.Save(record);
            bool created;
            try {
                created = create();
            } catch (Exception) {
                Unspend(record);
                throw;
            }

            if (!created) {
                Unspend(record);
                return InviteRedeem.NotCreated;
            }

            return InviteRedeem.Created;
        }
    }

    private void Unspend(InviteRecord record) {
        record.UsedBy = null;
        record.UsedUtc = null;
        _store.Save(record); // if this throws too, the invite stays spent: the owner makes a new one
    }
}
