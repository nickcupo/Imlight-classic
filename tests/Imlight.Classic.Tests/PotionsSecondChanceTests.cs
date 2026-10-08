using System;
using System.IO;
using System.Linq;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Game.Minigames;
using Imlight.CoreLib.Game.SecondChance;
using Xunit;

namespace Imlight.Classic.Tests;

/// <summary>
/// CLASSIC: 2009 potion flasks (minigame fill, Hilda Brewer) and the October 2009 Second Chance chests.
/// </summary>
[Collection(nameof(ClassicRuntimeCollection))]
public sealed class PotionsSecondChanceTests {

    public PotionsSecondChanceTests() {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}potions-second-chance-tests.log\n");
            ConfigurationManager.Initialize(path);
        }
        finally { File.Delete(path); }
    }

    private static PotionRules Potions() => PotionRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "potions-2009.yaml"));

    private static SecondChanceRules Chests() => SecondChanceRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "second-chance-2009.yaml"));

    [Fact]
    public void TheProfilesNameThePotionAndChestRules() {
        Assert.Equal("rules/potions-2009.yaml", ClassicDataFixture.LoadProfile("late-2009").Rules.Potions);
        Assert.Equal("rules/second-chance-2009.yaml", ClassicDataFixture.LoadProfile("late-2009").Rules.SecondChance);
        // The first half of 2009 had flasks but no Second Chance chests (October 2009).
        Assert.Equal("rules/potions-2009.yaml", ClassicDataFixture.LoadProfile("arc1-2009h1").Rules.Potions);
        Assert.Null(ClassicDataFixture.LoadProfile("arc1-2009h1").Rules.SecondChance);
        Assert.Null(ClassicDataFixture.LoadProfile("dev-unrestricted").Rules.Potions);
    }

    [Fact]
    public void HildaBrewerCharges2009Prices() {
        var rules = Potions();
        Assert.Equal(100, rules.ShopPrice(1));    // January 2009: Fill One 100
        Assert.Equal(100, rules.ShopPrice(10));
        Assert.Equal(130, rules.ShopPrice(13));   // May 2009: 130
        Assert.Equal(300, rules.ShopPrice(30));   // "1200 for 4" (July 2009)
        Assert.Equal(500, rules.ShopPrice(50));   // April 2010: "100-500"
    }

    [Fact]
    public void AMinigameRefillsManaFirstThenTheFlasks() {
        var rules = Potions();

        // Mana not full: the reward goes to the globe, the flasks get what is left over.
        var fill = rules.MinigameFill(currentMana: 40, maxMana: 100, flasks: 0, maxFlasks: 3);
        Assert.Equal(100, fill.Mana);
        Assert.Equal(60, fill.ManaGained);
        Assert.Equal(0.4f, fill.Flasks, 3);

        // Mana full: a whole flask a game.
        fill = rules.MinigameFill(currentMana: 100, maxMana: 100, flasks: 1, maxFlasks: 3);
        Assert.Equal(100, fill.Mana);
        Assert.Equal(0, fill.ManaGained);
        Assert.Equal(2f, fill.Flasks, 3);

        // Never past the flasks owned, and nothing for a wizard with no flask yet.
        Assert.Equal(3f, rules.MinigameFill(100, 100, 2.5f, 3).Flasks, 3);
        Assert.Equal(0f, rules.MinigameFill(100, 100, 0, 0).FlasksGained, 3);
        Assert.Equal(0f, rules.MinigameFill(100, 100, 3, 3).FlasksGained, 3);
    }

    [Fact]
    public void OnlyTheFirstScoreThresholdPaysTheManaSlot() {
        int[] thresholds = [200, 275, 300];
        Assert.Equal(0, MinigameRewards.ThresholdsReached(199, thresholds));
        Assert.Equal(1, MinigameRewards.ThresholdsReached(200, thresholds));
        Assert.Equal(3, MinigameRewards.ThresholdsReached(5000, thresholds));
        Assert.Equal(0, MinigameRewards.ThresholdsReached(5000, null!));
    }

    [Fact]
    public void TheChestsAreTheOctober2009Batches() {
        var rules = Chests();
        Assert.Equal(17, rules.Chests.Length);
        Assert.Equal(50, rules.CostOfUse(0));
        Assert.Equal(100, rules.CostOfUse(1));
        Assert.Equal(500, rules.CostOfUse(9));     // "500+" (The Friendly Necromancer, 2009-10-17)
        Assert.Equal("Plague Oni", rules.ChestByTemplate(191252)!.Boss);
        Assert.Equal(191253UL, rules.ChestByName("DS_MonsterChest_Malistaire")!.Template);
        Assert.Null(rules.ChestByName("WC_MonsterChest_PrinceGobblestone")); // a later chest
        Assert.All(rules.Chests, chest => Assert.True(chest.Template is >= 184103 and <= 191256));
    }

    [Fact]
    public void AChestNeedsItsBossBeatenInThatZoneVisit() {
        var rules = Chests();
        var state = new SecondChanceChests();
        var chest = rules.ChestByName("MS_MonsterChest_PlagueOni")!;
        const string zone = "MooShu/MS_Plague/Interiors/MS_Plague2_PalaceInterior";

        Assert.Equal(ChestRefusal.BossNotDefeated, state.OpenForFixture(1, 900, chest, zone, 7, rules));
        state.RecordWin(1, zone, 7, [77505]); // Ideyoshi, not the Plague Oni
        Assert.Equal(ChestRefusal.BossNotDefeated, state.OpenForFixture(1, 900, chest, zone, 7, rules));
        state.RecordWin(1, zone, 7, [77504]);
        Assert.Equal(ChestRefusal.BossNotDefeated, state.OpenForFixture(1, 900, chest, zone, 8, rules)); // another instance
        Assert.Equal(ChestRefusal.None, state.OpenForFixture(1, 900, chest, zone, 7, rules));
        Assert.Equal(77504UL, state.BossForFixture(1, chest));

        // A win elsewhere replaces the record.
        state.RecordWin(1, "MooShu/MS_Hub", 0, [1]);
        Assert.Equal(ChestRefusal.BossNotDefeated, state.OpenForFixture(1, 900, chest, zone, 7, rules));
    }

    [Fact]
    public void EachUseCostsFiftyMoreAndTheDayHasALimit() {
        var rules = Chests();
        var now = new DateTime(2009, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        var state = new SecondChanceChests(() => now);
        var chest = rules.ChestByName("DS_MonsterChest_Malistaire")!;
        const string zone = "DragonSpire/DS_A3_Kings/Interiors/DS_MalistaireLair";
        state.RecordWin(5, zone, 3, [126504]);
        Assert.Equal(ChestRefusal.None, state.OpenForFixture(5, 42, chest, zone, 3, rules));

        var crowns = 3000;
        var paid = new System.Collections.Generic.List<int>();
        bool Pay(int cost) {
            if (crowns < cost) {
                return false;
            }

            crowns -= cost;
            paid.Add(cost);

            return true;
        }

        for (var i = 0; i < rules.DailyUses; i++) {
            Assert.Equal(ChestRefusal.None, state.TryUseForFixture(5, 42, zone, 3, rules, Pay, out _, out _));
        }

        Assert.Equal(Enumerable.Range(0, rules.DailyUses).Select(i => 50 + 50 * i), paid);
        Assert.Equal(ChestRefusal.NoUsesLeft, state.TryUseForFixture(5, 42, zone, 3, rules, Pay, out _, out _));
        Assert.Equal(0, state.UsesLeftForFixture(5, chest, rules));

        // The next UTC day starts again at 50 Crowns; a wizard short of Crowns pays nothing and keeps the use.
        now = now.AddDays(1);
        Assert.Equal(ChestRefusal.BossNotDefeated, state.TryUseForFixture(5, 42, zone, 3, rules, Pay, out _, out _)); // the win is old
        state.RecordWin(5, zone, 3, [126504]);
        crowns = 49;
        Assert.Equal(ChestRefusal.NotEnoughCrowns, state.TryUseForFixture(5, 42, zone, 3, rules, Pay, out _, out int cost));
        Assert.Equal(50, cost);
        Assert.Equal(rules.DailyUses, state.UsesLeftForFixture(5, chest, rules));

        // A roll needs the chest's own window: another chest id, or a closed window, is refused.
        crowns = 1000;
        Assert.Equal(ChestRefusal.NoPrompt, state.TryUseForFixture(5, 43, zone, 3, rules, Pay, out _, out _));
        state.Close(5);
        Assert.Equal(ChestRefusal.NoPrompt, state.TryUseForFixture(5, 42, zone, 3, rules, Pay, out _, out _));
        Assert.Equal(1000, crowns); // nothing was taken
    }

    [Fact]
    public void ConcurrentRollsNeverPassTheDailyLimitOrDoubleCharge() {
        var rules = Chests();
        var state = new SecondChanceChests();
        var chest = rules.ChestByName("KT_MonsterChest_Krokopatra")!;
        state.RecordWin(9, chest.Zone, 0, [35433]);
        state.OpenForFixture(9, 1, chest, chest.Zone, 0, rules);
        var spent = 0;
        var results = Enumerable.Range(0, 64).AsParallel().Select(attempt =>
            state.TryUseForFixture(9, 1, chest.Zone, 0, rules, cost => { System.Threading.Interlocked.Add(ref spent, cost); return true; }, out _, out _)).ToArray();
        Assert.Equal(rules.DailyUses, results.Count(r => r == ChestRefusal.None));
        Assert.Equal(Enumerable.Range(0, rules.DailyUses).Sum(i => rules.CostOfUse(i)), spent);
    }

}
