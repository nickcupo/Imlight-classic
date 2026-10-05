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
 * TOKEN BUCKET
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: a per-session rate limit (chat lines, friend stat lookups). A
 * burst of Capacity requests passes at once; after that one request per
 * 1/RefillPerSecond seconds. Not thread-safe: each session actor owns its own.
 *
 * USAGE EXAMPLE:
 * var chat = new TokenBucket(capacity: 6, refillPerSecond: 1);
 * if (!chat.TryTake(DateTimeOffset.UtcNow)) return; // dropped
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;

namespace Imlight.Classic.Security;

public sealed class TokenBucket {

    private readonly double _capacity;
    private readonly double _refillPerSecond;
    private double _tokens;
    private DateTimeOffset? _last;

    public TokenBucket(double capacity, double refillPerSecond) {
        _capacity = Math.Max(1, capacity);
        _refillPerSecond = Math.Max(0, refillPerSecond);
        _tokens = _capacity;
    }

    /// <summary>Takes one token when one is left at <paramref name="now"/>; false (nothing taken) otherwise.</summary>
    public bool TryTake(DateTimeOffset now) {
        if (_last is { } last && now > last) {
            _tokens = Math.Min(_capacity, _tokens + (now - last).TotalSeconds * _refillPerSecond);
        }

        if (_last is null || now > _last) {
            _last = now;
        }

        if (_tokens < 1) {
            return false;
        }

        _tokens -= 1;
        return true;
    }

}
