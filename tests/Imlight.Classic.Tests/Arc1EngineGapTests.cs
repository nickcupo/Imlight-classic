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
 * The small comparisons the Arc 1 engine gaps use: ReqIsGender, the
 * QT-<quest> entries, "Ddl_" client tags and the dormant-spawner choice.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 * Cases come from r806919: the Ravenwood dorm doors, the Windhammer tower's
 * crystal stand, the Dark Cave bubbles and the Grand Chasm (Past) boss
 * spawners.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class Arc1EngineGapTests {

    [Theory]
    [InlineData("MALE", "Male", true)]
    [InlineData("FEMALE", "Female", true)]
    [InlineData("MALE", "Female", false)]
    [InlineData("FEMALE", "Male", false)]
    [InlineData("female", "Female", true)]
    [InlineData("MALE", "Neutral", false)]
    [InlineData("", "Male", false)]
    [InlineData(null, "Male", false)]
    [InlineData("MALE", null, false)]
    public void DormDoorsReadTheWizardsGender(string? required, string? wizard, bool met)
        => Assert.Equal(met, PlayerGender.Matches(required, wizard));

    [Theory]
    [InlineData("QT-DS-NEC1-C01-004", "DS-NEC1-C01-004")]
    [InlineData("QT-DS-LIB3-C03-006", "DS-LIB3-C03-006")]
    [InlineData("QT-KT-PYM3-C02-003-OK", "KT-PYM3-C02-003-OK")]
    public void QuestTakenEntriesNameTheirQuest(string entry, string quest)
        => Assert.Equal(quest, QuestTakenEntry.QuestNameOf(entry));

    [Theory]
    [InlineData("QT-")]
    [InlineData("WC-MAIN-C01-003_Complete")]
    [InlineData("qt-DS-NEC1-C01-004")]
    [InlineData(null)]
    public void OtherEntriesNameNoQuest(string? entry)
        => Assert.Null(QuestTakenEntry.QuestNameOf(entry));

    [Theory]
    [InlineData("Ddl_WC_DarkCave_Bubble1", "WC_DarkCave_Bubble1")]
    [InlineData("DDL_DS_NA_Pedistal_1", "DS_NA_Pedistal_1")]
    [InlineData("WC_TA_Cog", "WC_TA_Cog")]
    [InlineData("Ddl_", "Ddl_")]
    public void DdlTagsNameTheObjectBehindThePrefix(string tag, string name)
        => Assert.Equal(name, ClientTagVariants.ObjectNameOf(tag));

    [Fact]
    public void BubbleGoalsMatchTheDarkCaveBubbles() {
        Assert.True(ClientTagVariants.NamesObject(["Ddl_WC_DarkCave_Bubble2"], "WC_DarkCave_Bubble2"));
        Assert.True(ClientTagVariants.NamesObject(["WC_TA_Cog", "Ddl_WC_TA_Cogs"], "WC_TA_Cog"));
        Assert.False(ClientTagVariants.NamesObject(["Ddl_WC_DarkCave_Bubble2"], "WC_DarkCave_Bubble1"));
        Assert.False(ClientTagVariants.NamesObject(["Ddl_wc_darkcave_bubble2"], "WC_DarkCave_Bubble2"));
        Assert.False(ClientTagVariants.NamesObject(null, "WC_DarkCave_Bubble2"));
        Assert.False(ClientTagVariants.NamesObject(["Ddl_"], ""));
    }

    private static readonly SpawnerInfo[] s_grandChasmPast = [
        new(815695, false, [126309]),
        new(805549, false, [126501]), // Helephant boss, "Spawn point z11"
        new(805551, false, [126501]), // the same boss, "Spawn point z12"
        new(815699, false, [126695]),
        new(900001, true, [126501]),
    ];

    [Fact]
    public void OnlyInactiveUnstartedSpawnersAreDormant() {
        var dormant = DormantSpawners.Find(s_grandChasmPast, [815695]);

        Assert.Equal([805549u, 805551u, 815699u], dormant.Select(spawner => spawner.Id));
    }

    [Fact]
    public void ABossOnTwoSpawnersIsStartedOnce() {
        var dormant = DormantSpawners.Find(s_grandChasmPast, []);

        Assert.Equal([805549u], DormantSpawners.Plan(dormant, templateId => templateId == 126501));
    }

    [Fact]
    public void NothingNeededStartsNothing() {
        var dormant = DormantSpawners.Find(s_grandChasmPast, []);

        Assert.Empty(DormantSpawners.Plan(dormant, _ => false));
    }

    [Fact]
    public void ATemplateTheZonePlacesAnywayIsLeftToItsSpawners() {
        var dormant = DormantSpawners.Find(s_grandChasmPast, []);
        var placed = DormantSpawners.Placed(s_grandChasmPast, [126695]); // the active 900001 and a static 126695

        Assert.Equal([126501u, 126695u], placed.Order());
        Assert.Empty(DormantSpawners.Plan(dormant, templateId => templateId is 126501 or 126695, placed));
        Assert.Equal([815695u], DormantSpawners.Plan(dormant, templateId => templateId == 126309, placed));
    }

    [Fact]
    public void EachNeededTemplateGetsItsOwnSpawner() {
        var dormant = DormantSpawners.Find(s_grandChasmPast, []);
        var asked = new List<uint>();

        var planned = DormantSpawners.Plan(dormant, templateId => {
            asked.Add(templateId);

            return templateId is 126501 or 126695;
        });

        Assert.Equal([805549u, 815699u], planned);
        Assert.Equal(asked.Distinct().Count(), asked.Count); // each template is asked about once
    }

}
