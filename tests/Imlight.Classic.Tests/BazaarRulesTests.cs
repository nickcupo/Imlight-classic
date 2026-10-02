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
 * BAZAAR RULES TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The Bazaar's tiered prices, the server stock plan and the reconcile rule
 * that never touches players' copies.
 *
 * NOTE:
 * Live: restocks and rotation, and buying a treasure card and a hat from
 * the stocked Bazaar, ran on the rig (playbot-reports/features.md).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.IO;
using System.Linq;
using Imlight.Classic.Bazaar;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class BazaarRulesTests {

    private static BazaarRules Real()
        => BazaarRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "bazaar-2009.yaml"));

    [Fact]
    public void PricesFallAsCopiesRise() {
        var rules = Real();
        Assert.Equal(150, rules.BuyPrice(100, 1, BazaarKind.Gear));
        Assert.Equal(135, rules.BuyPrice(100, 7, BazaarKind.Gear));
        Assert.Equal(100, rules.BuyPrice(100, 30, BazaarKind.Gear));
        Assert.Equal(50, rules.SellPrice(100, 0, BazaarKind.Gear));
        Assert.Equal(40, rules.SellPrice(100, 5, BazaarKind.Gear));
        Assert.Equal(500, rules.BuyPrice(100, 1, BazaarKind.Reagent));
        Assert.Equal(1, rules.BuyPrice(0, 1, BazaarKind.Gear));
        Assert.Equal(100, rules.CapFor(BazaarKind.Gear));
        Assert.Equal(500, rules.CapFor(BazaarKind.Reagent));
    }

    [Fact]
    public void PlanSplitsByShareWithinRanges() {
        var rules = Real();
        var pool = Enumerable.Range(1, 400).Select(i => new BazaarCandidate((ulong) i,
            (BazaarKind) (i % 4), 100)).ToList();
        var plan = BazaarStockPlanner.Plan(rules, pool, 100, new Random(3));
        Assert.Equal(50, plan.Count(lot => lot.Kind == BazaarKind.Gear));
        Assert.Equal(20, plan.Count(lot => lot.Kind == BazaarKind.Reagent));
        Assert.All(plan, lot => {
            var (min, max) = rules.StockRanges[lot.Kind];
            Assert.InRange(lot.Copies, min, max);
            Assert.InRange(lot.PriceFactor, 1 - rules.PriceJitter, 1 + rules.PriceJitter);
        });
        Assert.Equal(plan.Count, plan.Select(lot => lot.Template).Distinct().Count());
    }

    [Theory]
    [InlineData(5, 3, 0, -3, 0)]    // server's 3 rotate out, players' 2 stay
    [InlineData(5, 3, 6, 3, 6)]     // top the server's share up to 6
    [InlineData(1, 3, 3, 2, 3)]     // 2 server copies were bought: they count as the server's first
    [InlineData(98, 0, 5, 2, 2)]    // the cap leaves room for 2
    [InlineData(0, 0, 4, 4, 4)]
    public void ReconcileNeverTouchesPlayersCopies(int held, int before, int wanted, int change, int server)
        => Assert.Equal((change, server), BazaarStockPlanner.Reconcile(held, before, wanted, 100));

}
