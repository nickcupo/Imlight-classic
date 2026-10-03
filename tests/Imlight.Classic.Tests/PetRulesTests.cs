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
 * PET RULES TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The 2010 pet loop rules: levels and experience marks, energy per game,
 * stat points from a score, snacks (liked and loved), talents, the Dance
 * Game's rounds, and hatching.
 *
 * NOTE:
 * Live: buying snacks, a Dance Game, feeding and a level up ran on rig-pets
 * with the headless bot (playbot-reports/pets.md).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Pets;
using Xunit;

namespace Imlight.Classic.Tests;

public class PetRulesTests {

    // The common pet template table (ObjectData/Pets, PetLevelInfo.m_requiredXP by level 0..7).
    private static readonly uint[] s_template = [0, 125, 250, 525, 1050, 2125, 2250, 0];

    [Fact]
    public void LevelMarksAreCumulativeAndStopAtEpic() {
        var t = PetRules.Thresholds(s_template);
        Assert.Equal([0, 0, 125, 375, 900, 1950], t);
        Assert.Equal(PetRules.Baby, PetRules.LevelFor(124, t));
        Assert.Equal(PetRules.Teen, PetRules.LevelFor(125, t));
        Assert.Equal(PetRules.Adult, PetRules.LevelFor(375, t));
        Assert.Equal(PetRules.Ancient, PetRules.LevelFor(1949, t));
        Assert.Equal(PetRules.Epic, PetRules.LevelFor(99_999, t));
        Assert.Equal(1950, PetRules.CapXp(5000, t));
        Assert.Equal(125, PetRules.NextLevelXp(PetRules.Baby, t));
        Assert.Equal(1950, PetRules.NextLevelXp(PetRules.Epic, t));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 6)]
    [InlineData(4, 8)]
    [InlineData(5, 8)]
    public void EnergyIsTwoALevelAndEpicStaysAtEight(int level, int cost) => Assert.Equal(cost, PetRules.EnergyCost(level));

    [Fact]
    public void FullScoreGivesFourPointsSplitOverTheTrack() {
        Assert.Equal(4, PetRules.GamePoints(5, 5));
        Assert.Equal(3, PetRules.GamePoints(4, 5));
        Assert.Equal(0, PetRules.GamePoints(0, 5));
        var track = new[] { new PetStatChange("Agility", 3), new PetStatChange("Strength", 1) };
        Assert.Equal(track, PetRules.DistributePoints(4, track));
        var two = PetRules.DistributePoints(2, track);
        Assert.Equal(2, two.Sum(c => c.Change));
        Assert.Equal("Agility", two.OrderByDescending(c => c.Change).First().Stat);
        Assert.Empty(PetRules.DistributePoints(0, track));
    }

    [Fact]
    public void BabiesEarnDoubleExperience() {
        Assert.Equal(8, PetRules.GameXp(4, PetRules.Baby));
        Assert.Equal(4, PetRules.GameXp(4, PetRules.Teen));
    }

    [Fact]
    public void SnacksGiveTheirStatSumAndMoreWhenLiked() {
        string[] cereal = ["PetSnack", "Cereal"];
        var snack = new[] { new PetStatChange("Strength", 2), new PetStatChange("Power", 2), new PetStatChange("Intellect", 2) };
        Assert.Equal(SnackTaste.Normal, PetRules.Taste(["Meal"], "Storm", cereal, "Fire"));
        Assert.Equal(SnackTaste.Liked, PetRules.Taste(["Cereal"], "Storm", cereal, "Fire"));
        Assert.Equal(SnackTaste.Liked, PetRules.Taste(["Meal"], "Fire", cereal, "Fire"));
        Assert.Equal(SnackTaste.Loved, PetRules.Taste(["Cereal"], "Fire", cereal, "Fire"));
        Assert.Equal(6, PetRules.Feed(snack, SnackTaste.Normal).Xp);
        var loved = PetRules.Feed(snack, SnackTaste.Loved);
        Assert.Equal(8, loved.Xp);
        Assert.Equal(4, loved.Changes.Single(c => c.Stat == "Power").Change);
        Assert.Equal(1, PetRules.Feed([new PetStatChange("Agility", 1)], SnackTaste.Liked).Changes.Single(c => c.Stat == "Power").Change);
    }

    [Fact]
    public void StatsNeverPassTheirMaximums() {
        var current = new Dictionary<string, int> { ["Will"] = 239, ["Agility"] = 10 };
        var max = new Dictionary<string, int> { ["Will"] = 240, ["Agility"] = 230 };
        var applied = PetRules.ApplyStats(current, max, [new PetStatChange("Will", 4), new PetStatChange("Agility", 4)]);
        Assert.Equal(240, current["Will"]);
        Assert.Equal(14, current["Agility"]);
        Assert.Equal(1, applied.Single(a => a.Stat == "Will").Change);
    }

    [Fact]
    public void TalentsComeFromThe2010PartOfThePool() {
        string[] pool = ["Talent-Damage-Storm02", "Talent-Fishing-MayCastRevealFishSchool", "Talent-Power-Adv-Chest-TCStorm-02-Locked", "Talent-Stat01-Str01"];
        Assert.False(PetRules.IsTalentIn2010(pool[1]));
        Assert.False(PetRules.IsTalentIn2010(pool[2]));
        var random = new Random(3);
        var first = PetRules.PickTalent(pool, [], random);
        Assert.Contains(first, new[] { pool[0], pool[3] });
        var second = PetRules.PickTalent(pool, [first], random);
        Assert.NotEqual(first, second);
        Assert.Null(PetRules.PickTalent(pool, [pool[0], pool[3]], random));
        Assert.Equal(4, PetRules.MaxTalents);
    }

    [Fact]
    public void DanceIsFiveRoundsOrThreeFailures() {
        var game = new DanceGame(new Random(1));
        var lengths = new List<int>();
        while (!game.IsOver) {
            var moves = game.NextRound();
            lengths.Add(moves.Length);
            Assert.All(moves, c => Assert.InRange(c, 'a', 'd'));
            Assert.True(game.Answer(moves));
        }

        Assert.Equal([3, 4, 5, 6, 7], lengths);
        Assert.Equal(5, game.Successes);
        Assert.Equal(4, game.Points);
        Assert.Null(game.NextRound());

        var bad = new DanceGame(new Random(2));
        for (var i = 0; i < 3; i++) {
            bad.NextRound();
            Assert.False(bad.Answer("x"));
        }

        Assert.True(bad.IsOver);
        Assert.Equal(0, bad.Points);
        Assert.False(bad.Answer(bad.Current));
    }

    [Fact]
    public void HatchingNeedsAdultsOnceADayAndCostsTwentyToFiftyThousand() {
        var stats = new Dictionary<string, int> { ["Will"] = 200 };
        var adult = new HatchParent(1, PetRules.Adult, stats, ["Talent-Stat01-Str01"], [], 20, 0);
        var teen = adult with { Level = PetRules.Teen };
        var now = 1_000_000L;
        Assert.NotNull(PetHatchRules.Check(adult, teen, now));
        Assert.Null(PetHatchRules.Check(adult, adult, now));
        Assert.NotNull(PetHatchRules.Check(adult with { LastHatchUnix = now - 3600 }, adult, now));
        Assert.Null(PetHatchRules.Check(adult with { LastHatchUnix = now - 25 * 3600 }, adult, now));
        Assert.Equal(20_000, PetHatchRules.GoldCost(0));
        Assert.Equal(50_000, PetHatchRules.GoldCost(1000));
        Assert.InRange(PetHatchRules.GoldCost(60), 20_000, 50_000);
    }

    [Fact]
    public void ABabyMixesItsParents() {
        var a = new HatchParent(10, PetRules.Adult, new Dictionary<string, int> { ["Will"] = 100, ["Power"] = 200 },
            ["Talent-Stat01-Str01", "Talent-Damage-Fire01"], ["Talent-Damage-Fire01"], 20, 0);
        var b = new HatchParent(20, PetRules.Epic, new Dictionary<string, int> { ["Will"] = 200, ["Power"] = 200 },
            ["Talent-Stat01-Agi01", "Talent-Fishing-Luck01"], ["Talent-Stat01-Agi01"], 20, 0);
        for (var seed = 0; seed < 20; seed++) {
            var baby = PetHatchRules.Breed(a, b, new Random(seed));
            Assert.Contains(baby.TemplateId, new ulong[] { 10, 20 });
            Assert.Contains(baby.MaxStats["Will"], new[] { 100, 150, 200 });
            Assert.Equal(200, baby.MaxStats["Power"]);
            Assert.InRange(baby.TalentPool.Count, 1, 10);
            Assert.DoesNotContain("Talent-Fishing-Luck01", baby.TalentPool);
            Assert.Equal(baby.TalentPool.Count, baby.TalentPool.Distinct().Count());
        }
    }

}
