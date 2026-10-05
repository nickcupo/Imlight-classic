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
 * GOLEM TOWER (THE 2009 WAY IN AFTER THE TUTORIAL)
 * ========================================================================
 *
 * PURPOSE:
 * Pins how a 2009 wizard came to the Golem Tower after the tutorial: Regina
 * Flametalon's chain in Golem Court, open at level 5 once Unicorn Way ends
 * (Rattlebones Report), one trip per quest: Science Fair (a Wooden
 * Construct, floor 1), Second Gear (a Clockwork Golem, floor 3) and The
 * Final Piece (the Iron Golem, floor 5; Ghoul and Blood Bat treasure cards),
 * plus the Golem Tower Champion badge for the Iron Golem. Also pins that the
 * client's pre-launch Storm chain ("Introduction to Storms", "Apply Storm
 * Knowledge" ... "Learn Lightning Bats", Headmistress Greyrose's orientation)
 * stays unserved: no source from 2008 to 2010 shows it live.
 *
 * EVIDENCE:
 * Fandom Science Fair oldid 56965, Second Gear 56967, The Final Piece 56968
 * (all 2010-01-13); Golem Tower oldid 61479 (2010-02-06); Golem Court oldid
 * 38617 (2009-08-06); the playbot run in playbot-reports/golem-tower.md.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter GolemTower
 *
 * NOTE:
 * Reads the monorepo's classic-data (skipped outside it, as the other data
 * tests are).
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.IO;
using System.Linq;
using Imlight.Classic.Quests;
using Imlight.Classic.Rules;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class GolemTowerTests {

    private const int Regina = 38266; // WC-HUB-NPC06, Regina Flametalon, Golem Court
    private const string Floors = "WizardCity/WC_Streets/WC_Golem_Tower/WC_Golem_Tower_";

    private static string QuestDir => Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates");

    private static JObject Quest(string name) => JObject.Parse(File.ReadAllText(Path.Combine(QuestDir, name + ".json")));

    private static JToken[] Requirements(JObject quest) => [.. quest["m_requirements"]!["m_requirements"]!];

    [Theory]
    [InlineData("WC-CLASSIC-SIDE-056", "QuestTitle_9589", "WC-ST01-C01-006", "Wooden_Construct", 1)]
    [InlineData("WC-CLASSIC-SIDE-057", "QuestTitle_958A", "WC-CLASSIC-SIDE-056", "Clockwork_Golem", 3)]
    [InlineData("WC-CLASSIC-SIDE-058", "QuestTitle_13D92", "WC-CLASSIC-SIDE-057", "Iron_Golem", 5)]
    public void ReginaSendsTheWizardUpTheTowerOneTripAtATime(string name, string title, string after, string mob, int floor) {
        var quest = Quest(name);
        Assert.Equal(title, (string?) quest["m_questTitle"]);
        Assert.False((bool) quest["m_mainline"]!);

        var previous = Assert.Single(Requirements(quest), r => ((string?) r["$type"])!.Contains("ReqHasEntry"));
        Assert.Equal(after, (string?) previous["m_questName"]);
        Assert.Equal("Complete", (string?) previous["m_entryName"]);

        var goals = quest["m_goals"]!.ToArray();
        Assert.Equal(2, goals.Length);
        Assert.Equal("GOAL_TYPE_BOUNTYCOLLECT", (string?) goals[0]["m_goalType"]);
        Assert.Equal([mob], goals[0]["m_npcAdjectives"]!.Select(a => (string) a!));
        Assert.Equal(Floors + floor, (string?) goals[0]["m_destinationZone"]);
        Assert.Equal("GOAL_TYPE_PERSONA", (string?) goals[1]["m_goalType"]);
        Assert.Equal("WizardCity/WC_Golem_Tower", (string?) goals[1]["m_destinationZone"]);

        var actors = quest.Descendants().OfType<JProperty>().Where(p => p.Name == "m_actorTemplateID").Select(p => (int) p.Value).Distinct();
        Assert.Equal([Regina], actors);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void ScienceFairOpensAtLevelFiveAfterUnicornWay(int level, bool offered) {
        var requirement = Assert.Single(Requirements(Quest("WC-CLASSIC-SIDE-056")), r => ((string?) r["$type"])!.Contains("ReqMagicLevel"));
        Assert.Equal(offered, NumericRequirement.Meets(level,
            (string?) requirement["m_operatorType"], (float) requirement["m_numericValue"]!));
    }

    [Fact]
    public void TheFinalPieceGivesItsTwoTreasureCards() {
        var cards = QuestCardRewardsLoader.Load(Path.Combine(ClassicDataFixture.Root, "quests", "cards-2009.yaml"));
        Assert.Equal(["Ghoul", "Blood Bat"], cards.CardsFor("WC-CLASSIC-SIDE-058", "Storm").Select(c => c.Name));
    }

    [Fact]
    public void TheIronGolemMakesAGolemTowerChampion() {
        var badges = BadgeRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "badges", "badges-2009.yaml"));
        var champion = badges.Find("golem-tower-champion");
        Assert.NotNull(champion);
        Assert.Equal(new KillBadgeAward("Iron_Golem", 1), champion!.Award);
    }

    [Theory]
    [InlineData("WizQst9908")] // Introduction to Storms
    [InlineData("WizQst990B")] // Apply Storm Knowledge (Golem Tower, Enchanted Wood)
    [InlineData("WizQst990C")] // Fetch Schematics
    [InlineData("WizQst990D")] // Collect Components
    [InlineData("WizQst990E")] // History of the Tritons
    [InlineData("WizQst990F")] // Learn Lightning Bats
    [InlineData("WizQst9917")] // The Headmistress' Quest (Headmistress Greyrose)
    [InlineData("WizQst98DA")] // Achieve Rank
    [InlineData("WizQst98DB")]
    public void ThePreLaunchStormChainStaysUnserved(string table) {
        var prefix = table + "_";
        var users = Directory.EnumerateFiles(QuestDir, "*.json")
            .Where(f => File.ReadAllText(f).Contains(prefix, StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension);
        Assert.Empty(users);
    }

}
