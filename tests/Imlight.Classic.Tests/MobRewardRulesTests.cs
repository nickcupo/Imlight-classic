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
 * CLASSIC MOB REWARD TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The mob reward loader's checks, the pip counting of the combat XP rule,
 * gold and drop rolls, and the real mob-rewards-2009 file.
 *
 * USAGE EXAMPLE:
 * dotnet test tests/Imlight.Classic.Tests --filter MobRewardRulesTests
 *
 * NOTE:
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Imlight.Classic.Rules;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class MobRewardRulesTests : IDisposable {

    private readonly TempClassicData _data = new();

    public void Dispose() => _data.Dispose();

    private string Write(string yaml, string name = "mob-rewards-test.yaml") {
        var folder = Path.Combine(_data.Root, "progression");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, yaml.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));

        return path;
    }

    private const string Source = "{source: https://example.invalid, source_date: '2010-05-15', retrieved: '2026-09-27', covers: [combat_xp], confidence: verified}";

    private static string RulesYaml(string xPip = "1", string fizzle = "card", string mobs = "", string extraRank = "",
                                    string normalChance = "0.0", string extra = "", int maxPerMob = 3) {
        var text = new StringBuilder();
        text.Append("id: mob-rewards-test\nprofiles: [late-2009]\nlicense_tag: own\n");
        text.Append("combat_xp:\n  xp_per_pip: 3\n  zero_pip_counts_as: 1\n  x_pip_counts_as: ").Append(xPip)
            .Append("\n  fizzle_counts: ").Append(fizzle).Append("\n  provenance:\n  - ").Append(Source).Append('\n');
        text.Append("gold:\n  provenance:\n  - ").Append(Source).Append("\n  by_rank:\n");
        text.Append("  - {rank: 1, normal: [1, 3], elite: [4, 6], boss: [10, 20], confidence: verified}\n");
        text.Append("  - {rank: 5, normal: [30, 40], elite: [50, 60], boss: [100, 200], confidence: inferred}\n");
        text.Append(extraRank);
        text.Append("drops:\n  confidence: inferred\n  expected: {normal: ").Append(normalChance)
            .Append(", elite: 0.5, boss: 1.0}\n  max_per_mob: ").Append(maxPerMob).Append("\n  unseen_chance: 0\n");
        text.Append(extra.Length == 0 || extra.EndsWith('\n') ? extra : extra + "\n");
        text.Append(mobs.Length == 0 ? "mobs: []\n" : "mobs:\n" + mobs);

        return text.ToString();
    }

    private const string TwoMobs = """
        - name: Skeletal Pirate
          templates: [35785]
          rank: 1
          kind: normal
          gold: [1, 2]
          drops: [{name: Canvas Cover, template: 77552}, {name: Soot-Stained Hat}]
          source: {page: Skeletal Pirate (Creature), oldid: 42880, date: '2009-09-20'}
        - name: Kraken
          templates: [35309, 81106]
          rank: 3
          kind: boss
          drops: [{name: Hat, template: 1111}, {name: Robe, template: 2222}]
          source: {page: Kraken (Boss), oldid: 1, date: '2010-01-01'}
        """;

    [Fact]
    public void CountsPipsTheClassicWay() {
        var rules = MobRewardRulesLoader.Load(Write(RulesYaml()));
        var xp = rules.CombatXp;

        Assert.Equal(1, xp.PipsForCast(0, isXPip: false, xPipsSpent: 0));    // a blade or wand spell
        Assert.Equal(4, xp.PipsForCast(4, isXPip: false, xPipsSpent: 0));
        Assert.Equal(1, xp.PipsForCast(0, isXPip: true, xPipsSpent: 7));     // Tempest with 7 pips counts as 1
        Assert.Equal(4, xp.PipsForFizzle(4, isXPip: false));                 // a fizzled card counts its pips
        Assert.Equal(1, xp.PipsForFizzle(0, isXPip: true));
        Assert.Equal(27, xp.Xp(9));
        Assert.Equal(0, xp.Xp(-2));
    }

    [Fact]
    public void StockCountingIsImlights() {
        var stock = CombatXpRule.Stock;

        Assert.Equal(7, stock.PipsForCast(0, isXPip: true, xPipsSpent: 7));
        Assert.Equal(1, stock.PipsForFizzle(5, isXPip: false));
        Assert.Equal(1, stock.PipsForCast(0, isXPip: false, xPipsSpent: 0));

        var rules = MobRewardRulesLoader.Load(Write(RulesYaml(xPip: "null", fizzle: "one")));
        Assert.Equal(6, rules.CombatXp.PipsForCast(0, isXPip: true, xPipsSpent: 6));
        Assert.Equal(1, rules.CombatXp.PipsForFizzle(5, isXPip: false));
    }

    [Fact]
    public void GoldComesFromTheMobOrItsRank() {
        var rules = MobRewardRulesLoader.Load(Write(RulesYaml(mobs: TwoMobs)));

        Assert.Equal(new GoldRange(1, 2), rules.GoldFor(new MobInfo(35785, 1, MobKind.Normal)));        // documented
        Assert.Equal(new GoldRange(10, 20), rules.GoldFor(new MobInfo(35309, 3, MobKind.Boss)));        // Kraken: no range, rank 1-4 row
        Assert.Equal(new GoldRange(4, 6), rules.GoldFor(new MobInfo(999, 2, MobKind.Elite)));
        Assert.Equal(new GoldRange(30, 40), rules.GoldFor(new MobInfo(999, 5, MobKind.Normal)));
        Assert.Equal(new GoldRange(100, 200), rules.GoldFor(new MobInfo(999, 12, MobKind.Boss)));     // past the last row
        Assert.Equal(new GoldRange(1, 3), rules.GoldFor(new MobInfo(999, 0, MobKind.Normal)));        // rank 0 counts as 1
        Assert.Equal(3, rules.MobCount);
        Assert.Equal("Kraken", rules.Find(81106)!.Name);
    }

    [Fact]
    public void DropsOnlyDocumentedItemsWithTemplates() {
        // normal expected 2.0 over the pirate's two listed items: each drops every time, but only one has a template.
        var rules = MobRewardRulesLoader.Load(Write(RulesYaml(mobs: TwoMobs, normalChance: "2.0")));
        var random = new Random(7);

        for (var i = 0; i < 50; i++) {
            var pirate = rules.Roll(new MobInfo(35785, 1, MobKind.Normal), random);
            Assert.InRange(pirate.Gold, 1, 2);
            Assert.Equal([77552UL], pirate.Items);              // the only item with a template

            var kraken = rules.Roll(new MobInfo(35309, 3, MobKind.Boss), random);
            Assert.All(kraken.Items, item => Assert.Contains(item, new ulong[] { 1111, 2222 }));

            var stranger = rules.Roll(new MobInfo(424242, 3, MobKind.Boss), random);   // undocumented: gold only
            Assert.Empty(stranger.Items);
            Assert.Empty(stranger.TreasureCards);
            Assert.Empty(stranger.Reagents);
        }

        var never = MobRewardRulesLoader.Load(Write(RulesYaml(mobs: TwoMobs, normalChance: "0.0")));
        Assert.All(Enumerable.Range(0, 50), _ => Assert.Null(never.Roll(new MobInfo(35785, 1, MobKind.Normal), random).Item));
    }

    [Fact]
    public void UntalliedEntriesShareTheKindsExpectedDrops() {
        // Boss expected 1.0 over Kraken's two items: each at 0.5, so about one item per fight, sometimes two, sometimes none.
        var rules = MobRewardRulesLoader.Load(Write(RulesYaml(mobs: TwoMobs)));
        var random = new Random(11);
        var counts = new int[3];
        for (var i = 0; i < 4000; i++) {
            counts[rules.Roll(new MobInfo(35309, 3, MobKind.Boss), random).Items.Length]++;
        }

        Assert.InRange(counts[0] / 4000.0, 0.21, 0.29);
        Assert.InRange(counts[1] / 4000.0, 0.45, 0.55);
        Assert.InRange(counts[2] / 4000.0, 0.21, 0.29);
        Assert.Equal(0.5, rules.ItemDrops.ChanceOf(new DropEntry(1111, null), MobKind.Boss, listed: 2, tallied: false));
    }

    private const string TalliedBoss = """
        - name: Lord Nightshade
          templates: [35099]
          rank: 3
          kind: boss
          tally: 50
          drops: [{name: Always, template: 11, chance: 1}, {name: Seen once, template: 12, chance: 0.02}, {name: Never seen, template: 13, chance: 0}, {name: Unlisted, template: 14}]
          treasure_cards: [{name: Ghost Touch, template: 501, chance: 1}]
          reagents: [{name: Ectoplasm, template: 601}]
          source: {page: Lord Nightshade, oldid: 68538, date: '2010-05-09'}
        """;

    private const string CardAndReagentRules = """
        treasure_cards:
          confidence: inferred
          expected: {normal: 0, elite: 0, boss: 20}
          max_per_mob: 1
          provenance:
          - {source: https://example.invalid, source_date: '2010-05-15', retrieved: '2026-09-27', covers: [treasure_cards], confidence: inferred}
          client_tables:
          - {name: Fire Elf, template: 502, mobs: [424242, 35099]}
        reagents:
          confidence: inferred
          expected: {normal: 0, elite: 0, boss: 20}
          max_per_mob: 1
          quantity: [2, 3]
          provenance:
          - {source: https://example.invalid, source_date: '2010-05-15', retrieved: '2026-09-27', covers: [reagents], confidence: inferred}
          client_tables:
          - {name: Bone, template: 602, mobs: [424242]}
        """;

    [Fact]
    public void TalliedChancesWinAndUnseenEntriesRarelyDrop() {
        var rules = MobRewardRulesLoader.Load(Write(RulesYaml(mobs: TalliedBoss, extra: CardAndReagentRules)));
        var random = new Random(3);
        var seen = new Dictionary<ulong, int>();
        for (var i = 0; i < 2000; i++) {
            foreach (var item in rules.Roll(new MobInfo(35099, 3, MobKind.Boss), random).Items) {
                seen[item] = seen.GetValueOrDefault(item) + 1;
            }
        }

        Assert.Equal(2000, seen[11]);                            // 100% on the tally
        Assert.InRange(seen.GetValueOrDefault(12UL), 15, 70);      // 2%
        Assert.False(seen.ContainsKey(13));                      // never seen, unseen_chance 0
        Assert.False(seen.ContainsKey(14));                      // not on the tally: unseen too
        Assert.Equal(0.02, rules.ItemDrops.ChanceOf(new DropEntry(12, 0.02), MobKind.Boss, 4, tallied: true));
    }

    [Fact]
    public void TreasureCardsAndReagentsComeFromTheListThenTheClientTables() {
        var rules = MobRewardRulesLoader.Load(Write(RulesYaml(mobs: TalliedBoss, extra: CardAndReagentRules)));
        var random = new Random(5);
        for (var i = 0; i < 100; i++) {
            var nightshade = rules.Roll(new MobInfo(35099, 3, MobKind.Boss), random);
            Assert.Equal([501UL], nightshade.TreasureCards);                 // its own list, not the Fire Elf fallback
            var reagent = Assert.Single(nightshade.Reagents);                  // boss expected 20 over one entry: always
            Assert.Equal(601UL, reagent.Template);
            Assert.InRange(reagent.Quantity, 2, 3);

            var stranger = rules.Roll(new MobInfo(424242, 3, MobKind.Boss), random);
            Assert.Equal([502UL], stranger.TreasureCards);                   // client loot-table fallback
            Assert.Equal(602UL, Assert.Single(stranger.Reagents).Template);
            Assert.Empty(stranger.Items);

            Assert.Empty(rules.Roll(new MobInfo(424242, 3, MobKind.Normal), random).TreasureCards);   // normal expected 0
        }
    }

    [Fact]
    public void CapsDropsPerMob() {
        const string greedy = """
            - name: Greedy
              templates: [77]
              rank: 1
              kind: boss
              tally: 50
              drops: [{name: A, template: 1, chance: 1}, {name: B, template: 2, chance: 1}, {name: C, template: 3, chance: 1}, {name: D, template: 4, chance: 1}]
              source: {page: Greedy, oldid: 1, date: '2010-01-01'}
            """;
        var rules = MobRewardRulesLoader.Load(Write(RulesYaml(mobs: greedy, maxPerMob: 2)));
        var random = new Random(9);
        var picked = new HashSet<ulong>();
        for (var i = 0; i < 200; i++) {
            var items = rules.Roll(new MobInfo(77, 1, MobKind.Boss), random).Items;
            Assert.Equal(2, items.Length);
            picked.UnionWith(items);
        }

        Assert.Equal(4, picked.Count);    // the cap keeps a random two, not always the first two
    }

    [Fact]
    public void RejectsChancesWithoutATallyAndBadQuantities() {
        var untallied = RulesYaml(mobs: TalliedBoss.Replace("  tally: 50\n", ""));
        var ex = Assert.Throws<ClassicDataException>(() => MobRewardRulesLoader.Load(Write(untallied)));
        Assert.Contains(ex.Errors, error => error.Message.Contains("needs the mob's tally"));

        var zero = RulesYaml(extra: CardAndReagentRules.Replace("quantity: [2, 3]", "quantity: [0, 3]"));
        ex = Assert.Throws<ClassicDataException>(() => MobRewardRulesLoader.Load(Write(zero)));
        Assert.Contains(ex.Errors, error => error.Message.Contains("at least one"));

        var tooLikely = RulesYaml(mobs: TalliedBoss.Replace("chance: 0.02", "chance: 1.5"));
        Assert.Throws<ClassicDataException>(() => MobRewardRulesLoader.Load(Write(tooLikely)));
    }

    [Fact]
    public void RejectsBadRangesRepeatsAndSharedTemplates() {
        var badRange = RulesYaml(extraRank: "  - {rank: 5, normal: [1, 3], elite: [1, 1], boss: [1, 1], confidence: inferred}\n"
                                          + "  - {rank: 6, normal: [9, 3], elite: [1, 1], boss: [1, 1], confidence: inferred}\n");
        var ex = Assert.Throws<ClassicDataException>(() => MobRewardRulesLoader.Load(Write(badRange)));
        Assert.Contains(ex.Errors, error => error.Message.Contains("above max"));
        Assert.Contains(ex.Errors, error => error.Message.Contains("repeats"));

        var shared = RulesYaml(mobs: TwoMobs.Replace("templates: [35309, 81106]", "templates: [35309, 35785]"));
        ex = Assert.Throws<ClassicDataException>(() => MobRewardRulesLoader.Load(Write(shared)));
        Assert.Contains(ex.Errors, error => error.Message.Contains("belongs to two mobs"));

        var badFizzle = RulesYaml(fizzle: "sometimes");
        Assert.Throws<ClassicDataException>(() => MobRewardRulesLoader.Load(Write(badFizzle)));
    }

    [Fact]
    public void RealMobRewards2009() {
        var profile = ClassicDataFixture.LoadProfile("late-2009");
        var rules = MobRewardRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, profile.Rules.MobRewards!));

        Assert.Equal("mob-rewards-2009", rules.Id);
        Assert.Equal(3, rules.CombatXp.XpPerPip);
        Assert.Equal(1, rules.CombatXp.ZeroPipCountsAs);
        Assert.Equal(1, rules.CombatXp.XPipCountsAs);
        Assert.Equal(1, rules.GoldByRank.Keys.First());
        Assert.NotEmpty(rules.Mobs);
        Assert.All(rules.GoldByRank.Values, row => Assert.True(row.Normal.Max <= row.Boss.Max));

        // Drops: a boss gives about one item a fight, an elite fewer, a street mob well under one; cards and
        // reagents are rarer; the cap lets a boss drop two.
        Assert.InRange(rules.ItemDrops.Expected[MobKind.Boss], 0.8, 1.3);
        Assert.True(rules.ItemDrops.Expected[MobKind.Elite] < rules.ItemDrops.Expected[MobKind.Boss]);
        Assert.Equal(2, rules.ItemDrops.MaxPerMob);
        Assert.True(rules.ItemDrops.Expected[MobKind.Normal] < rules.ItemDrops.Expected[MobKind.Elite]);
        Assert.True(rules.TreasureCardDrops.Expected[MobKind.Boss] < rules.ItemDrops.Expected[MobKind.Boss]);
        Assert.Equal(1, rules.TreasureCardDrops.MaxPerMob);
        Assert.NotEmpty(rules.TreasureCardFallback);
        Assert.NotEmpty(rules.ReagentFallback);

        // Rattlebones (Unicorn Way, rank 1 elite): the cutoff page's 50-fight tally.
        var rattlebones = rules.Find(35311)!;
        Assert.Equal("Rattlebones", rattlebones.Name);
        Assert.Equal(50, rattlebones.Tally);
        Assert.Contains(rattlebones.Items, entry => entry.Chance is > 0);
        Assert.Contains(rules.Mobs, mob => !mob.TreasureCards.IsEmpty);
        Assert.Contains(rules.Mobs, mob => !mob.Reagents.IsEmpty);
    }

}
