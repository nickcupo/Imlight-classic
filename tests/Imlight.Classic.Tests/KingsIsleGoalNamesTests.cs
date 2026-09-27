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
 * KingsIsle's goal names from the r806919 zone data resolve onto the
 * captured goals of the SpiralDB quests they name, and leave quests that
 * already use KingsIsle's names alone.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 * The goal lists are the loaded quests' own (SpiralDB capture or overlay);
 * the requested names are what the client's triggers, spawns and sigils
 * check for them.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Generic;
using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class KingsIsleGoalNamesTests {

    private static readonly string?[] s_drakeQuest = [ // DS-ACAD-C01-005
        "1_WizardQuestGoals_UseItem", "2_WizardQuestGoals_ExploreZone", "3_WizardQuestGoals_Kill", "4_WizardQuestGoals_TalkNPC",
    ];

    private static readonly string?[] s_informantQuest = [ // MB-AIR2-C02-003
        "1_WizardQuestGoals_Explore", "2_WizardQuestGoals_Kill", "3_WizardQuestGoals_TalkNPC", "4_WizardQuestGoals_TalkNPC",
    ];

    private static readonly string?[] s_scarletQuest = [ // WC-TRITON-MAIN-005 (2019 rewrite)
        "1_WizardQuestGoals_00000242", "2_WizardQuestGoals_KillCollect", "3_WizardQuestGoals_TalkNPC",
        "4_WizardQuestGoals_Explore", "5_WizardQuestGoals_UseItem", "6_WizardQuestGoals_TalkNPC",
    ];

    [Theory]
    [InlineData("Goal", "1_WizardQuestGoals_UseItem")]          // DrakeSpeechTrigger in the Academy
    [InlineData("Goal 4", "4_WizardQuestGoals_TalkNPC")]         // Trigger-Drake in Ambrose's office
    [InlineData("Goal 2", "2_WizardQuestGoals_ExploreZone")]
    public void LegacyQuestsCountGoalFromOne(string requested, string expected)
        => Assert.Equal(expected, KingsIsleGoalNames.Resolve("DS-ACAD-C01-005", s_drakeQuest, requested));

    [Fact]
    public void TheInformantSpawnsWhileTheThirdGoalTalksToHim()
        => Assert.Equal("3_WizardQuestGoals_TalkNPC", KingsIsleGoalNames.Resolve("MB-AIR2-C02-003", s_informantQuest, "Goal 3"));

    [Theory]
    [InlineData("Goal", "1_WizardQuestGoals_00000242")]          // Trigg_Cogs_Theme: the cogs
    [InlineData("Goal 1", "2_WizardQuestGoals_KillCollect")]     // Trigg_ScarletCombat: the Scarlet Screamers
    [InlineData("Goal 3", "4_WizardQuestGoals_Explore")]
    public void RewriteQuestsCountGoalOneAsTheSecond(string requested, string expected)
        => Assert.Equal(expected, KingsIsleGoalNames.Resolve("WC-TRITON-MAIN-005", s_scarletQuest, requested));

    [Fact]
    public void AGoalOfThatNameAlwaysWins() {
        string?[] goals = ["GetWater", "2_WizardQuestGoals_TalkNPC", "DefeatColossus"]; // WC-ICE-C02-001 (overlay)

        Assert.Equal("DefeatColossus", KingsIsleGoalNames.Resolve("WC-ICE-C02-001", goals, "DefeatColossus"));
        Assert.Null(KingsIsleGoalNames.Resolve("WC-ICE-C02-001", goals, "KillColossus"));
    }

    [Fact]
    public void QuestsThatUseKingsIsleNamesGetNoAlias() {
        string?[] chapterOne = ["WC-MAIN-C01-003_Goal0", "WC-MAIN-C01-003_Goal1"];
        string?[] renamed = ["Goal", "Goal 2", "3_WizardQuestGoals_TalkNPC"];

        Assert.Equal("WC-MAIN-C01-003_Goal0", KingsIsleGoalNames.Resolve("WC-MAIN-C01-003", chapterOne, "WC-MAIN-C01-003_Goal0"));
        Assert.Null(KingsIsleGoalNames.Resolve("WC-MAIN-C01-003", chapterOne, "Goal"));
        Assert.Equal("Goal 2", KingsIsleGoalNames.Resolve("MB-AIR2-C02-003", renamed, "Goal 2"));
        Assert.Null(KingsIsleGoalNames.Resolve("MB-AIR2-C02-003", renamed, "Goal 3"));
    }

    [Fact]
    public void AGoalPastTheEndOrADesignersNameResolvesToNothing() {
        string?[] twoGoals = ["1_WizardQuestGoals_Kill", "2_WizardQuestGoals_TalkNPC"]; // KT-CRYHub-C01-004

        Assert.Null(KingsIsleGoalNames.Resolve("KT-CRYHub-C01-004", twoGoals, "Goal 3"));
        Assert.Null(KingsIsleGoalNames.Resolve("KT-CRYHub-C01-004", twoGoals, "Goal_Explore_Bartleby"));
        Assert.Null(KingsIsleGoalNames.Resolve("KT-CRYHub-C01-004", twoGoals, null));
    }

    [Fact]
    public void CapturedNumbersBeatListOrder() {
        string?[] shuffled = ["2_WizardQuestGoals_TalkNPC", "1_WizardQuestGoals_Kill"];

        Assert.Equal("1_WizardQuestGoals_Kill", KingsIsleGoalNames.Resolve("MS-DTH2-C01-005", shuffled, "Goal"));
    }

    [Fact]
    public void GoalsWithoutACapturedNumberCountByPlace() {
        string?[] mixed = ["GetWater", "2_WizardQuestGoals_TalkNPC", "DefeatColossus", "4_WizardQuestGoals_TalkNPC"];

        Assert.Equal("DefeatColossus", KingsIsleGoalNames.Resolve("WC-ICE-C02-001", mixed, "Goal 3"));
        Assert.Equal("GetWater", KingsIsleGoalNames.Resolve("WC-ICE-C02-001", mixed, "Goal"));
    }

    [Theory]
    [InlineData("MS-WAR3-C02-003", "Goal 3-Sunbird", 2)]
    [InlineData("WC-OLDE-MAIN-001", "Goal 6 - talked to Penny Downstairs", 6)]
    [InlineData("WC-MAIN-C01-003", "WC-MAIN-C01-003_Goal0", 0)]
    [InlineData("WC-MAIN-C01-009", "WC-MAIN-C01-009_Goal1", 1)]
    [InlineData("WC-PreLM-MAIN-001", "Goal 02", 2)]
    [InlineData("MB-SPELL-C01-001", "Goal 1", 0)]
    [InlineData("MS-CAT-MAIN-005", "Goal 0", 0)]
    public void NamesParseToPositions(string quest, string goal, int index)
        => Assert.Equal(index, KingsIsleGoalNames.IndexOf(quest, goal));

    [Theory]
    [InlineData("KillColossus")]
    [InlineData("GoalSummonSeraph")]
    [InlineData("Goal_Explore_Bartleby")]
    [InlineData("2_WizardQuestGoals_TalkNPC")]
    public void DesignerAndCapturedNamesAreNotKingsIsleNumbers(string goal) {
        Assert.False(KingsIsleGoalNames.IsKingsIsleName(goal));
        Assert.Null(KingsIsleGoalNames.IndexOf("MB-AIR2-C02-003", goal));
    }

    [Fact]
    public void LegacyNamesComeFromTheChainNumbering() {
        Assert.True(KingsIsleGoalNames.UsesLegacyNumbering("MB-AIR2-C02-003"));
        Assert.True(KingsIsleGoalNames.UsesLegacyNumbering("KT-CRYHub-C01-004"));
        Assert.True(KingsIsleGoalNames.UsesLegacyNumbering("WC-MAIN-C01-012"));
        Assert.False(KingsIsleGoalNames.UsesLegacyNumbering("WC-TRITON-MAIN-005"));
        Assert.False(KingsIsleGoalNames.UsesLegacyNumbering("WC-OLDE-MAIN-002A"));
        Assert.False(KingsIsleGoalNames.UsesLegacyNumbering(null));
    }

    [Fact]
    public void GoalCompleteEventsCarryTheKingsIsleNames() {
        Assert.Equal(["Goal", "Goal 1", "MB-AIR2-C02-003_Goal0"],
            KingsIsleGoalNames.AliasesOf("MB-AIR2-C02-003", s_informantQuest, "1_WizardQuestGoals_Explore"));
        Assert.Equal(["Goal 3", "MB-AIR2-C02-003_Goal2"],
            KingsIsleGoalNames.AliasesOf("MB-AIR2-C02-003", s_informantQuest, "3_WizardQuestGoals_TalkNPC"));
        Assert.Equal(["Goal 1", "WC-TRITON-MAIN-005_Goal1"],
            KingsIsleGoalNames.AliasesOf("WC-TRITON-MAIN-005", s_scarletQuest, "2_WizardQuestGoals_KillCollect"));
    }

    [Fact]
    public void KingsIsleNamedQuestsPostNoAliasEvents() {
        IReadOnlyList<string?> chapterOne = ["WC-MAIN-C01-003_Goal0"];

        Assert.Empty(KingsIsleGoalNames.AliasesOf("WC-MAIN-C01-003", chapterOne, "WC-MAIN-C01-003_Goal0"));
        Assert.Empty(KingsIsleGoalNames.AliasesOf("MB-AIR2-C02-003", s_informantQuest, "not a goal"));
    }

    [Fact]
    public void EveryAliasResolvesBackToItsGoal() {
        foreach (var (quest, goals) in new[] { ("DS-ACAD-C01-005", s_drakeQuest), ("WC-TRITON-MAIN-005", s_scarletQuest) }) {
            foreach (var goal in goals) {
                foreach (var alias in KingsIsleGoalNames.AliasesOf(quest, goals, goal)) {
                    Assert.Equal(goal, KingsIsleGoalNames.Resolve(quest, goals, alias));
                }
            }
        }
    }

}
