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
 * MONSTROLOGY REWARD TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the extraction reward rules follow the r806919 tome's text (one
 * Animus per hit, one more for the defeat; small chance above your level;
 * bonus chance below it; no XP more than 5 levels above the rank).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using Imlight.CoreLib.Game.Monstrology;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class MonstrologyRewardsTests {
    private static Func<int> Rolls(params int[] values) {
        var queue = new Queue<int>(values);
        return () => queue.Count > 0 ? queue.Dequeue() : 100;
    }

    [Fact]
    public void AnExtractionAtYourLevelAlwaysWorksAndPaysTenXpPerRankPerAnimus() {
        var outcome = MonstrologyRewards.Decide(level: 1, rank: 1, observed: 2, boss: false, Rolls(100));
        Assert.Equal(new ExtractionOutcome(true, 2, 20, false), outcome);
        Assert.Equal(40, MonstrologyRewards.Decide(3, 2, 2, false, Rolls(100, 100)).Experience);
        Assert.Equal(80, MonstrologyRewards.Decide(3, 2, 2, true, Rolls(100, 100)).Experience);
    }

    [Fact]
    public void MonstersRankedAboveYouRarelyGiveAnimus() {
        Assert.Equal(50, MonstrologyRewards.SuccessChance(1, 2));
        Assert.Equal(35, MonstrologyRewards.SuccessChance(1, 3));
        Assert.Equal(10, MonstrologyRewards.SuccessChance(1, 10));
        Assert.False(MonstrologyRewards.Decide(1, 2, 2, false, Rolls(51)).Success);
        Assert.True(MonstrologyRewards.Decide(1, 2, 2, false, Rolls(50)).Success);
    }

    [Fact]
    public void AboveTheRankThereIsABonusAndNoXpPastFiveLevels() {
        Assert.Equal(0, MonstrologyRewards.BonusChance(2, 2));
        Assert.Equal(30, MonstrologyRewards.BonusChance(5, 2));
        Assert.Equal(50, MonstrologyRewards.BonusChance(20, 1));
        var bonus = MonstrologyRewards.Decide(5, 2, 1, false, Rolls(1, 30));
        Assert.True(bonus.Bonus); Assert.Equal(2, bonus.Animus);
        Assert.Equal(0, MonstrologyRewards.Decide(7, 1, 2, false, Rolls(1, 100)).Experience);
        Assert.Equal(20, MonstrologyRewards.Decide(6, 1, 2, false, Rolls(1, 100)).Experience); // exactly 5 above still earns
        Assert.Equal(new ExtractionOutcome(false, 0, 0, false), MonstrologyRewards.Decide(1, 1, 0, false, Rolls(1)));
    }
}
