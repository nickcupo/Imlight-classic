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
 * LOGIN THROTTLE
 * ========================================================================
 *
 * PURPOSE:
 * Brute-force protection shared by the game login (MSG_USER_AUTHEN_V3,
 * MSG_USER_VALIDATE), the launcher's /launcher/login and the admin page.
 *
 * NOTE:
 * Failures are counted per account and per client address inside a window.
 * Reaching the limit locks that account (or address) out; each lockout in a
 * row doubles, up to a cap. A success clears the account's count and lockout
 * level, not the address's, so one known password does not reset an address
 * that is guessing others. Memory is bounded: old entries are pruned.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Threading;

namespace Imlight.Classic.Net;

public sealed class LoginThrottleOptions {
    public int AccountFailures { get; init; } = 5;
    public int AddressFailures { get; init; } = 20;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan FirstLockout { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan MaxLockout { get; init; } = TimeSpan.FromMinutes(30);
}

public sealed class LoginThrottle {

    private sealed class Counter {
        public readonly Queue<DateTimeOffset> Failures = new();
        public DateTimeOffset LockedUntil;
        public int Lockouts;
        public DateTimeOffset LastSeen;
    }

    private const int PruneAbove = 4096;

    private readonly LoginThrottleOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Counter> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Counter> _addresses = new(StringComparer.Ordinal);

    public LoginThrottle(LoginThrottleOptions? options = null, TimeProvider? time = null) {
        var given = options ?? new LoginThrottleOptions();
        _options = new LoginThrottleOptions {
            AccountFailures = Math.Max(1, given.AccountFailures),
            AddressFailures = Math.Max(1, given.AddressFailures),
            Window = given.Window > TimeSpan.Zero ? given.Window : TimeSpan.FromMinutes(15),
            FirstLockout = given.FirstLockout > TimeSpan.Zero ? given.FirstLockout : TimeSpan.FromMinutes(1),
            MaxLockout = given.MaxLockout >= given.FirstLockout && given.MaxLockout > TimeSpan.Zero
                ? given.MaxLockout : TimeSpan.FromMinutes(30),
        };
        _time = time ?? TimeProvider.System;
    }

    public LoginThrottleOptions Options => _options;

    /// <summary>
    /// How long this account or address is still locked out, or null when a login may be tried.
    /// </summary>
    /// <param name="account">The username (case-insensitive), or null when not known yet.</param>
    /// <param name="address">The client address, or null.</param>
    public TimeSpan? LockedFor(string? account, string? address) {
        var now = _time.GetUtcNow();
        lock (_lock) {
            var wait = TimeSpan.Zero;
            if (account is { Length: > 0 } && _accounts.TryGetValue(account, out var a) && a.LockedUntil > now) {
                wait = a.LockedUntil - now;
            }

            if (address is { Length: > 0 } && _addresses.TryGetValue(address, out var b) && b.LockedUntil > now
                    && b.LockedUntil - now > wait) {
                wait = b.LockedUntil - now;
            }

            return wait > TimeSpan.Zero ? wait : null;
        }
    }

    /// <summary>Counts a failed login. Returns true when this failure started a lockout.</summary>
    public bool Failure(string? account, string? address) {
        var now = _time.GetUtcNow();
        var locked = false;
        lock (_lock) {
            Prune(now);
            if (account is { Length: > 0 }) {
                locked |= Count(_accounts, account, _options.AccountFailures, now);
            }

            if (address is { Length: > 0 }) {
                locked |= Count(_addresses, address, _options.AddressFailures, now);
            }
        }

        return locked;
    }

    /// <summary>A successful login clears the account's failures and lockout level.</summary>
    public void Success(string? account) {
        if (account is not { Length: > 0 }) {
            return;
        }

        lock (_lock) {
            _accounts.Remove(account);
        }
    }

    /// <summary>Entries currently tracked (accounts, addresses); for tests and monitoring.</summary>
    public (int Accounts, int Addresses) Tracked {
        get {
            lock (_lock) {
                return (_accounts.Count, _addresses.Count);
            }
        }
    }

    private bool Count(Dictionary<string, Counter> table, string key, int limit, DateTimeOffset now) {
        if (!table.TryGetValue(key, out var counter)) {
            counter = new Counter();
            table[key] = counter;
        }

        counter.LastSeen = now;
        var cutoff = now - _options.Window;
        while (counter.Failures.Count > 0 && counter.Failures.Peek() < cutoff) {
            counter.Failures.Dequeue();
        }

        // A lockout that ended long ago no longer doubles the next one.
        if (counter.Lockouts > 0 && counter.LockedUntil < now - _options.MaxLockout - _options.Window) {
            counter.Lockouts = 0;
        }

        counter.Failures.Enqueue(now);
        if (counter.Failures.Count < limit) {
            return false;
        }

        counter.Failures.Clear();
        var ticks = _options.FirstLockout.Ticks * Math.Pow(2, Math.Min(counter.Lockouts, 20));
        var lockout = ticks >= _options.MaxLockout.Ticks ? _options.MaxLockout : TimeSpan.FromTicks((long) ticks);
        counter.LockedUntil = now + lockout;
        counter.Lockouts++;
        return true;
    }

    private void Prune(DateTimeOffset now) {
        if (_accounts.Count + _addresses.Count < PruneAbove) {
            return;
        }

        var keep = _options.Window + _options.MaxLockout;
        foreach (var table in new[] { _accounts, _addresses }) {
            var stale = new List<string>();
            foreach (var (key, counter) in table) {
                if (counter.LockedUntil < now && now - counter.LastSeen > keep) {
                    stale.Add(key);
                }
            }

            foreach (var key in stale) {
                table.Remove(key);
            }
        }
    }
}
