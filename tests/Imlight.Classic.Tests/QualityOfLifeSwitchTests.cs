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
 * QUALITY-OF-LIFE SWITCH TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The drop-rate multiplier on the 2009 drop rules: 1 changes nothing,
 * more raises each chance up to 100%, and the per-mob cap still holds.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Immutable;
using System.Linq;
using Imlight.Classic.Rules;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class QualityOfLifeSwitchTests {

    private static readonly DropRule s_rule = new(
        ImmutableDictionary<MobKind, double>.Empty.Add(MobKind.Normal, 0.2), MaxPerMob: 2, UnseenChance: 0.01,
        Quantity: new GoldRange(1, 1));

    private static readonly ImmutableArray<DropEntry> s_entries =
        [new DropEntry(1, 0.10), new DropEntry(2, 0.10), new DropEntry(3, 0.10), new DropEntry(4, 0.10)];

    private static double Rate(double multiplier) {
        var random = new Random(1234);
        var hits = Enumerable.Range(0, 20_000).Sum(_ => s_rule.Roll(s_entries, MobKind.Normal, 4, true, random, multiplier).Length);

        return hits / 20_000.0;
    }

    [Fact]
    public void MultiplierOfOneIsThe2009Roll() {
        var a = new Random(7);
        var b = new Random(7);
        for (var i = 0; i < 500; i++) {
            Assert.Equal(s_rule.Roll(s_entries, MobKind.Normal, 4, true, a),
                s_rule.Roll(s_entries, MobKind.Normal, 4, true, b, 1.0));
        }
    }

    [Fact]
    public void DoubleRateRoughlyDoublesDropsAndCapStillHolds() {
        var normal = Rate(1);
        var doubled = Rate(2);
        Assert.InRange(normal, 0.36, 0.44);   // four 10% entries
        Assert.InRange(doubled, 0.72, 0.86);
        Assert.Equal(2.0, Rate(100));          // every chance 100%, but at most two per mob
        Assert.Equal(0.0, Rate(0));
    }

}
