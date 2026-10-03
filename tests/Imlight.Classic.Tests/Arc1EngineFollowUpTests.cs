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
 * CLASSIC QUEST ENGINE TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The follow-up Arc 1 engine needs: the Monster_Killed trigger check and
 * per-player collection objects.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 * Cases come from r806919 zone triggers: Hyde Park Tower 3's boss death
 * (Willie Marks, "Cat-Thug-Blue-MBBoss-L35.AdjRef"), Unicorn Way house 4's
 * death trigger (two templates plus two empty ".AdjRef" entries) and the
 * collection goals the world builders reported (MB_KatzLab crates,
 * MS_MushroomPoison).
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class Arc1EngineFollowUpTests {

    private static readonly string[] s_willieMarksAdjectives = ["Cat", "Willie_Marks", "MONST_Cats"];

    [Fact]
    public void BossDeathTriggerNamesItsBossByTemplate() {
        string[] entries = ["Cat-Thug-Blue-MBBoss-L35.AdjRef"];

        Assert.True(KilledMonster.Matches(entries, "Cat-Thug-Blue-MBBoss-L35", s_willieMarksAdjectives));
        Assert.True(KilledMonster.Matches(entries, "cat-thug-blue-mbboss-l35", s_willieMarksAdjectives));
        Assert.False(KilledMonster.Matches(entries, "Cat-Thug-Blue-MBBoss-L38", ["Cat", "Nails_OLeary", "MONST_Cats"]));
    }

    [Fact]
    public void TemplateReferenceDoesNotMatchAnAdjective() {
        // "Cat.AdjRef" names a template called "Cat", not every monster with the Cat adjective.
        Assert.False(KilledMonster.Matches(["Cat.AdjRef"], "Cat-Thug-Blue-MBBoss-L35", s_willieMarksAdjectives));
    }

    [Fact]
    public void PlainEntryMatchesAnAdjective() {
        Assert.True(KilledMonster.Matches(["Willie_Marks"], "Cat-Thug-Blue-MBBoss-L35", s_willieMarksAdjectives));
        Assert.False(KilledMonster.Matches(["Nails_OLeary"], "Cat-Thug-Blue-MBBoss-L35", s_willieMarksAdjectives));
    }

    [Fact]
    public void EmptyReferencesAndListsNameNobody() {
        string[] house4 = ["WC_Scarecrow_A_01.AdjRef", "CC_Horse_Apoc_A_01.AdjRef", ".AdjRef", ".AdjRef"];

        Assert.True(KilledMonster.Matches(house4, "WC_Scarecrow_A_01", []));
        Assert.False(KilledMonster.Matches(house4, "", []));
        Assert.False(KilledMonster.Matches(house4, null, null));
        Assert.False(KilledMonster.Matches([], "WC_Scarecrow_A_01", []));
        Assert.False(KilledMonster.Matches(null, "WC_Scarecrow_A_01", []));
    }

    [Fact]
    public void BountyTemplateReferenceCountsEachDefeatedMonster() {
        // CLASSIC: Face Your Fate's goal names "GH-Bear-Scout-1-R3.AdjRef"; the Troubled Warrior template's own
        // adjectives are only Bear and Undead, so the adjective match alone never credited it.
        string[] goal = ["GH-Bear-Scout-1-R3.AdjRef"];

        Assert.Equal(2, KilledMonster.CountTemplateReferences(goal, ["GH-Bear-Scout-1-R3", "gh-bear-scout-1-r3"]));
        Assert.Equal(1, KilledMonster.CountTemplateReferences(goal, ["GH-Bear-Scout-1-R3", "GH-Bear-Warrior-4-GHBoss-R4"]));
        Assert.Equal(0, KilledMonster.CountTemplateReferences(["Bear"], ["GH-Bear-Scout-1-R3"]));
        Assert.Equal(0, KilledMonster.CountTemplateReferences([".AdjRef"], ["", null]));
        Assert.Equal(0, KilledMonster.CountTemplateReferences(null, ["GH-Bear-Scout-1-R3"]));
        Assert.Equal(1, KilledMonster.CountTemplateReferences(["A.AdjRef", "A.AdjRef"], ["A"]));
    }

    [Fact]
    public void CollectedObjectIsGoneOnlyForItsCollector() {
        var crate = new CollectedObject<ulong>(TimeSpan.FromSeconds(30));
        var now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        var back = crate.Take(1, now);

        Assert.Equal(now.AddSeconds(30), back);
        Assert.True(crate.IsTakenBy(1, now.AddSeconds(29)));
        Assert.False(crate.IsTakenBy(2, now), "the second wizard in a grouped KatzLab still has the crate");
        Assert.False(crate.IsTakenBy(1, now.AddSeconds(30)));
    }

    [Fact]
    public void CollectedObjectComesBackForMoreUsesThanPlaced() {
        // MS_MushroomPoison: 8 counted, 5 placed. A mushroom comes back, so one wizard can reach 8.
        var mushroom = new CollectedObject<ulong>(CollectedObject<ulong>.DefaultRespawnDelay);
        var now = DateTime.UtcNow;

        mushroom.Take(7, now);
        Assert.Empty(mushroom.ReleaseDue(now.AddSeconds(1)));
        Assert.Equal([7UL], mushroom.ReleaseDue(now + mushroom.RespawnDelay));
        Assert.False(mushroom.IsTakenBy(7, now.AddSeconds(1)));
    }

    [Fact]
    public void ReleaseGivesTheObjectBack() {
        var barrel = new CollectedObject<string>(TimeSpan.FromMinutes(1));
        var now = DateTime.UtcNow;

        barrel.Take("a", now);

        Assert.True(barrel.Release("a"));
        Assert.False(barrel.IsTakenBy("a", now));
        Assert.False(barrel.Release("a"));
    }

}
