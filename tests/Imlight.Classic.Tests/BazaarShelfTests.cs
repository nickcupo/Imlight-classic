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
 * BAZAAR SHELF TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-08): the October 2010 Bazaar shelf
 * (classic-data/rules/bazaar-october-2010.yaml, BazaarStockPlanner.PlanShelf):
 * a full shelf of every kind, gear over the level bands, boss drops in few
 * copies, partial rotation, and the 2009 file left as it was.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/08/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imlight.Classic.Bazaar;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class BazaarShelfTests {

    private static BazaarRules October()
        => BazaarRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "bazaar-october-2010.yaml"));

    private static BazaarRules Classic2009()
        => BazaarRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "bazaar-2009.yaml"));

    // A pool shaped like the live one: plenty of gear over levels 1-50 (every fifth a boss drop), cards, housing, and the
    // October file's reagents.
    private static List<BazaarCandidate> Pool(BazaarRules rules) {
        var pool = new List<BazaarCandidate>();
        for (var i = 0; i < 1200; i++) {
            pool.Add(new BazaarCandidate((ulong) (10_000 + i), BazaarKind.Gear, 100 + i, 1 + i % 50, Rare: i % 5 == 0));
        }

        for (var i = 0; i < 130; i++) {
            pool.Add(new BazaarCandidate((ulong) (20_000 + i), BazaarKind.TreasureCard, 50, 0, Rare: i % 4 == 0));
        }

        for (var i = 0; i < 150; i++) {
            pool.Add(new BazaarCandidate((ulong) (30_000 + i), BazaarKind.Housing, 80, 0, Rare: i % 3 == 0));
        }

        pool.AddRange(rules.Shelf!.Reagents.Select(r => new BazaarCandidate(r.Template, BazaarKind.Reagent, 10)));
        return pool;
    }

    private static Dictionary<ulong, int> Held(IEnumerable<BazaarLot> plan)
        => plan.ToDictionary(lot => lot.Template, lot => lot.Copies);

    [Fact]
    public void OctoberProfileHasItsOwnShelfAnd2009IsUnchanged() {
        var october = October();
        Assert.Equal(["october-2010-arc1"], october.Profiles);
        var shelf = Assert.IsType<BazaarShelf>(october.Shelf);
        Assert.True(shelf.Lots >= 300);
        Assert.InRange(shelf.RotateShare, 0.2, 0.5);
        Assert.Equal(40, shelf.Reagents.Length);
        Assert.Equal(40, shelf.Reagents.Select(r => r.Template).Distinct().Count());
        Assert.All(shelf.Reagents, r => Assert.InRange(r.Template, 106931UL, 106970UL));
        Assert.Equal(1.0, shelf.GearBands.Sum(b => b.Share), 3);
        Assert.Equal(50, shelf.GearBands[^1].MaxLevel);
        Assert.Equal([38214UL, 81096UL], shelf.TreasureCardVendors);

        var classic = Classic2009();
        Assert.Null(classic.Shelf);
        Assert.DoesNotContain("october-2010-arc1", classic.Profiles);
        Assert.Contains("late-2009", classic.Profiles);

        // The prices are the 2009 file's.
        Assert.Equal(classic.ItemTiers, october.ItemTiers);
        Assert.Equal(classic.ReagentTiers, october.ReagentTiers);
    }

    [Fact]
    public void FreshShelfFillsEveryKindWithManyLots() {
        var rules = October();
        var plan = BazaarStockPlanner.PlanShelf(rules, Pool(rules), rules.Shelf!.Lots, new Dictionary<ulong, int>(), new Random(7));
        var lots = rules.Shelf.Lots;
        Assert.Equal((int) Math.Round(lots * 0.6), plan.Count(l => l.Kind == BazaarKind.Gear));
        Assert.Equal((int) Math.Round(lots * 0.2), plan.Count(l => l.Kind == BazaarKind.TreasureCard));
        Assert.Equal((int) Math.Round(lots * 0.1), plan.Count(l => l.Kind == BazaarKind.Housing));
        // Most reagents are in stock (rare harvests may be missing), every plain harvest always is.
        Assert.InRange(plan.Count(l => l.Kind == BazaarKind.Reagent), 30, 40);
        foreach (var harvest in rules.Shelf.Reagents.Where(r => r.Presence >= 1.0)) {
            var lot = Assert.Single(plan, l => l.Template == harvest.Template);
            Assert.InRange(lot.Copies, harvest.Min, harvest.Max);
        }

        Assert.Equal(plan.Count, plan.Select(l => l.Template).Distinct().Count());
        Assert.All(plan, l => Assert.False(l.Kept));
        Assert.All(plan, l => Assert.InRange(l.PriceFactor, 1 - rules.PriceJitter, 1 + rules.PriceJitter));
    }

    [Fact]
    public void GearIsSpreadOverTheLevelBands() {
        var rules = October();
        var pool = Pool(rules);
        var levels = pool.ToDictionary(c => c.Template, c => c.Level);
        var plan = BazaarStockPlanner.PlanShelf(rules, pool, rules.Shelf!.Lots, new Dictionary<ulong, int>(), new Random(11));
        var gear = plan.Where(l => l.Kind == BazaarKind.Gear).ToList();
        var lower = 0;
        foreach (var band in rules.Shelf.GearBands) {
            var count = gear.Count(l => levels[l.Template] > lower && levels[l.Template] <= band.MaxLevel);
            Assert.InRange(count, (int) (gear.Count * band.Share) - 2, (int) (gear.Count * band.Share) + 2);
            lower = band.MaxLevel;
        }
    }

    [Fact]
    public void BossDropsAreFewCopiesAndCommonDropsMany() {
        var rules = October();
        var pool = Pool(rules);
        var rare = pool.Where(c => c.Rare).Select(c => c.Template).ToHashSet();
        var plan = BazaarStockPlanner.PlanShelf(rules, pool, rules.Shelf!.Lots, new Dictionary<ulong, int>(), new Random(5));
        foreach (var lot in plan.Where(l => l.Kind != BazaarKind.Reagent)) {
            var (min, max) = rare.Contains(lot.Template) ? rules.StockRanges[lot.Kind] : rules.Shelf.CommonRanges[lot.Kind];
            Assert.InRange(lot.Copies, min, max);
        }

        Assert.Contains(plan, l => l.Kind == BazaarKind.Gear && rare.Contains(l.Template));
        Assert.Contains(plan, l => l.Kind == BazaarKind.Gear && !rare.Contains(l.Template));
    }

    [Fact]
    public void ARestockKeepsMostOfTheShelfAndRotatesTheRest() {
        var rules = October();
        var pool = Pool(rules);
        var first = BazaarStockPlanner.PlanShelf(rules, pool, rules.Shelf!.Lots, new Dictionary<ulong, int>(), new Random(1));
        var held = Held(first);
        // Players bought out a few lots since: those copies are gone and are not kept.
        var soldOut = first.Where(l => l.Kind == BazaarKind.Gear).Take(10).Select(l => l.Template).ToList();
        foreach (var template in soldOut) {
            held[template] = 0;
        }

        var second = BazaarStockPlanner.PlanShelf(rules, pool, rules.Shelf.Lots, held, new Random(2));
        var items = first.Count(l => l.Kind != BazaarKind.Reagent) - soldOut.Count;
        var kept = second.Where(l => l.Kept).ToList();
        Assert.InRange(kept.Count, (int) (items * (1 - rules.Shelf.RotateShare)) - 2, (int) (items * (1 - rules.Shelf.RotateShare)) + 2);
        Assert.All(kept, l => Assert.Equal(held[l.Template], l.Copies));
        Assert.All(kept, l => Assert.NotEqual(BazaarKind.Reagent, l.Kind));
        Assert.DoesNotContain(second, l => l.Kept && soldOut.Contains(l.Template));

        // The shelf stays full: each kind is back to its share.
        Assert.Equal(first.Count(l => l.Kind == BazaarKind.Gear), second.Count(l => l.Kind == BazaarKind.Gear));
        Assert.Equal(first.Count(l => l.Kind == BazaarKind.TreasureCard), second.Count(l => l.Kind == BazaarKind.TreasureCard));
        Assert.Equal(first.Count(l => l.Kind == BazaarKind.Housing), second.Count(l => l.Kind == BazaarKind.Housing));

        // And it changes: a good share of the items are new.
        var newItems = second.Count(l => l.Kind != BazaarKind.Reagent && !held.ContainsKey(l.Template));
        Assert.True(newItems >= items * rules.Shelf.RotateShare * 0.8, $"only {newItems} new items");
    }

    [Fact]
    public void ReconcileOfAKeptLotChangesNothing() {
        // A kept lot asks for exactly the server copies held now: no copies added or removed, players' copies untouched.
        var rules = October();
        Assert.Equal((0, 4), BazaarStockPlanner.Reconcile(held: 7, serverBefore: 4, serverWanted: 4, rules.CapFor(BazaarKind.Gear)));
    }

    [Fact]
    public void NoLotsMeansAnEmptyServerShelf() {
        var rules = October();
        Assert.Empty(BazaarStockPlanner.PlanShelf(rules, Pool(rules), 0, new Dictionary<ulong, int> { [10_001] = 3 }, new Random(1)));
    }

    [Fact]
    public void ShelfErrorsAreReported() {
        var dir = Path.Combine(Path.GetTempPath(), "bazaar-shelf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var source = File.ReadAllText(Path.Combine(ClassicDataFixture.Root, "rules", "bazaar-october-2010.yaml"));
            var path = Path.Combine(dir, "bazaar-october-2010.yaml");
            File.WriteAllText(path, source.Replace("- {max_level: 50, share: 0.2}", "- {max_level: 50, share: 0.5}")
                .Replace("rotate_share: 0.35", "rotate_share: 1.5"));
            var error = Assert.Throws<ClassicDataException>(() => BazaarRulesLoader.Load(path));
            Assert.Contains("band shares must add up to 1", error.Message);
            Assert.Contains("rotate_share", error.Message);
        }
        finally {
            Directory.Delete(dir, recursive: true);
        }
    }

}
