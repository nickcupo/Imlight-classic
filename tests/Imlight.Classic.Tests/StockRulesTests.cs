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
 * The stock guarantee: ClassicRules.Stock and dev-unrestricted answer like
 * stock Imlight from every member.
 * 
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using Xunit;

namespace Imlight.Classic.Tests;

public sealed class StockRulesTests {

    private static ClassicRules Resolve(string which)
        => which == "stock" ? ClassicRules.Stock : ClassicDataFixture.RealRules("dev-unrestricted");

    [Theory]
    [InlineData("stock")]
    [InlineData("dev-unrestricted")]
    public void NothingIsRestricted(string which) {
        var rules = Resolve(which);

        Assert.False(rules.IsRestricted);
        Assert.True(rules.CriticalAndBlockEnabled);
        Assert.Null(rules.HubKeyFor("Celestia/X"));
        Assert.True(rules.IsHubKeyAllowed("Anything"));
        Assert.True(rules.IsWorldTeleportAllowed("Anything", "Celestia/X").Allowed);
    }

    [Theory]
    [InlineData("stock")]
    [InlineData("dev-unrestricted")]
    public void EveryZoneIsAllowed(string which) {
        var rules = Resolve(which);

        foreach (var zone in new[] { "", "Celestia/X", "Test/X", "Nonsense/X", "WizardCity/QA_SpawnRate", "PetDerby/X" }) {
            Assert.True(rules.IsZoneAllowed(zone).Allowed, zone);
        }
    }

    [Theory]
    [InlineData("stock")]
    [InlineData("dev-unrestricted")]
    public void LevelAndXpPassThrough(string which) {
        var rules = Resolve(which);

        Assert.Equal(200, rules.EffectiveMaxLevel(200));
        Assert.Equal(0, rules.ClampLevel(0, 200));
        Assert.Equal(250, rules.ClampLevel(250, 200));
        Assert.Null(rules.XpCeiling(1_000_000, 900_000));
        Assert.Equal(500, rules.XpToApply(int.MaxValue - 1000, 500, null));
        Assert.Equal(123, rules.ClampXp(123, null));
    }

    [Theory]
    [InlineData("stock")]
    [InlineData("dev-unrestricted")]
    public void EveryFeatureIsEnabled(string which) {
        var rules = Resolve(which);

        foreach (var feature in ClassicFeatures.All) {
            Assert.True(rules.IsFeatureEnabled(feature), feature);
        }
    }

}
