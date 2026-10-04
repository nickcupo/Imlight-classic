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
 * OPEN PVP TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The open PvP rules (side choice, start decision) and the real circle
 * file. The live 1v1 and 2v1 fights ran on the rig with headless clients
 * (playbot-reports/features.md).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System.IO;
using Imlight.Classic.Pvp;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class OpenPvpTests {

    [Theory]
    [InlineData(100, 900, 0, 0, 0)]
    [InlineData(900, 100, 0, 0, 1)]
    [InlineData(100, 900, 4, 0, 1)]   // nearer side full: the other side
    [InlineData(100, 900, 4, 4, -1)]  // both full
    [InlineData(500, 500, 2, 1, 0)]   // a tie goes to the first side
    public void SideIsTheNearerHalfUnlessFull(double d0, double d1, int s0, int s1, int expected)
        => Assert.Equal(expected, OpenPvpRules.ChooseSide(d0, d1, s0, s1));

    [Theory]
    [InlineData(1, 0, 0, false, OpenPvpStart.Wait)]
    [InlineData(0, 3, 3, true, OpenPvpStart.Wait)]
    [InlineData(1, 1, 0, false, OpenPvpStart.CountDown)]
    [InlineData(1, 1, 0, true, OpenPvpStart.Start)]
    [InlineData(2, 1, 3, false, OpenPvpStart.Start)]    // everyone ready
    [InlineData(2, 1, 2, false, OpenPvpStart.CountDown)]
    [InlineData(4, 4, 0, false, OpenPvpStart.Start)]    // a full circle starts at once
    public void FightStartsWhenBothSidesAreThere(int s0, int s1, int ready, bool over, OpenPvpStart expected)
        => Assert.Equal(expected, OpenPvpRules.Decide(s0, s1, ready, over));

    [Theory]
    [InlineData(true, true, false)]   // classic arena duel: no mana (wiki Health_and_Mana oldid 41879)
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]   // stock Imlight keeps its behaviour
    [InlineData(false, false, true)]
    public void OnlyAClassicPvpDuelIsFreeOfMana(bool pvp, bool classic, bool costs)
        => Assert.Equal(costs, OpenPvpRules.CastingCostsMana(pvp, classic));

    [Fact]
    public void RealCirclesLoad() {
        var config = OpenPvpLoader.Load(Path.Combine(ClassicDataFixture.Root, "pvp", "open-pvp-2009.yaml"));
        Assert.Equal("WizardCity/WC_Duel_Arena", config.Zone);
        Assert.Equal(560u, config.Template);
        Assert.Equal(3, config.Circles.Length);
        Assert.All(config.Circles, circle => Assert.StartsWith("PvP Circle", circle.Tag));
    }

}
