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
 * CLASSIC RULES TESTS
 * ========================================================================
 * 
 * PURPOSE:
 * The level cap and XP ceiling arithmetic, directly and through
 * ClassicRules with capped and uncapped profiles.
 * 
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 * 
 * NOTE:
 * The XP numbers are made up; the real table is in the client's Root.wad.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using Imlight.Classic.Rules;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class LevelCapRulesTests {

    private const int XpToReach50 = 900_000;
    private const int XpToLeave50 = 1_000_000;

    private static ClassicRules Capped()
        => new(ZoneFixture.Profile(levelCap: 50), ZoneFixture.MinimalMap());

    private static ClassicRules Uncapped()
        => new(ZoneFixture.Profile(), ZoneFixture.MinimalMap());

    [Fact]
    public void EffectiveMaxLevel() {
        Assert.Equal(50, Capped().EffectiveMaxLevel(200));
        Assert.Equal(40, Capped().EffectiveMaxLevel(40));
        Assert.Equal(200, Uncapped().EffectiveMaxLevel(200));
    }

    [Fact]
    public void ClampLevel() {
        var rules = Capped();

        Assert.Equal(50, rules.ClampLevel(60, 50));
        Assert.Equal(1, rules.ClampLevel(0, 50));
        Assert.Equal(50, rules.ClampLevel(50, 50));
        Assert.Equal(60, rules.ClampLevel(60, 0));
        Assert.Equal(0, Uncapped().ClampLevel(0, 200));
        Assert.Equal(60, Uncapped().ClampLevel(60, 50));
    }

    [Fact]
    public void XpCeiling() {
        Assert.Equal(XpToLeave50 - 1, LevelCapRules.XpCeiling(XpToLeave50, XpToReach50));
        Assert.Equal(XpToReach50, LevelCapRules.XpCeiling(0, XpToReach50));
        Assert.Null(LevelCapRules.XpCeiling(0, 0));
        Assert.Equal(XpToReach50, LevelCapRules.XpCeiling(XpToLeave50, XpToReach50, XpCapPolicy.StopOnArrival));
        Assert.Equal(XpToReach50, LevelCapRules.XpCeiling(XpToReach50, XpToReach50));

        Assert.Equal(XpToLeave50 - 1, Capped().XpCeiling(XpToLeave50, XpToReach50));
        Assert.Null(Uncapped().XpCeiling(XpToLeave50, XpToReach50));
        var stopOnArrival = new ClassicRules(ZoneFixture.Profile(levelCap: 50), ZoneFixture.MinimalMap(), XpCapPolicy.StopOnArrival);
        Assert.Equal(XpToReach50, stopOnArrival.XpCeiling(XpToLeave50, XpToReach50));
    }

    [Fact]
    public void XpToApply() {
        const int ceiling = XpToLeave50 - 1;
        var rules = Capped();

        Assert.Equal(500, rules.XpToApply(100, 500, ceiling));
        Assert.Equal(ceiling - (XpToReach50 - 10), rules.XpToApply(XpToReach50 - 10, 500_000, ceiling));
        Assert.Equal(0, rules.XpToApply(ceiling, 500, ceiling));
        Assert.Equal(0, rules.XpToApply(ceiling + 100, 500, ceiling));
        Assert.Equal(-300, rules.XpToApply(ceiling, -300, ceiling));
        Assert.Equal(500, rules.XpToApply(ceiling, 500, null));
        Assert.Equal(0, rules.XpToApply(100, 0, ceiling));
    }

    [Fact]
    public void ClampXpAndCanGainXp() {
        const int ceiling = XpToLeave50 - 1;

        Assert.Equal(ceiling, LevelCapRules.ClampXp(XpToLeave50 + 5, ceiling));
        Assert.Equal(10, LevelCapRules.ClampXp(10, ceiling));
        Assert.Equal(XpToLeave50 + 5, LevelCapRules.ClampXp(XpToLeave50 + 5, null));
        Assert.True(LevelCapRules.CanGainXp(ceiling - 1, ceiling));
        Assert.False(LevelCapRules.CanGainXp(ceiling, ceiling));
        Assert.True(LevelCapRules.CanGainXp(int.MaxValue, null));
        Assert.True(Capped().CanGainXp(XpToReach50, ceiling));
    }

    [Fact]
    public void Arc1InheritsTheSameCap() {
        var late = ClassicDataFixture.RealRules("late-2009");
        var arc1 = ClassicDataFixture.RealRules("arc1-2009h1");

        Assert.Equal(late.EffectiveMaxLevel(200), arc1.EffectiveMaxLevel(200));
        Assert.Equal(50, arc1.EffectiveMaxLevel(200));
        Assert.Equal(late.XpCeiling(XpToLeave50, XpToReach50), arc1.XpCeiling(XpToLeave50, XpToReach50));
        Assert.Equal(50, arc1.ClampLevel(51, arc1.EffectiveMaxLevel(200)));
    }

}
