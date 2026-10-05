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
 * LOST LIEUTENANT (BRISKBREEZE TOWER ENTRY)
 * ========================================================================
 *
 * PURPOSE:
 * Pins the level-50 side quest that opens Briskbreeze Tower (WC-GNT-C01-001,
 * client QuestTitle_2EBE5): Sergeant Muldoon offers it at level 50, taking it
 * sets the sigil's QT- entry, the four talk goals run O'Doyle, Mindy
 * Pixiecrown, Kirby Longspear and Culpepper, and arc1-2009h1 removes it
 * through the profile's disabled_quests.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter LostLieutenant
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
using Newtonsoft.Json.Linq;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class LostLieutenantTests {

    internal const string Quest = "WC-GNT-C01-001";
    internal const string SigilEntry = "QT-WC-GNT-C01-001"; // the ToGauntlet01 sigil's only requirement (client zone data)
    private const int Muldoon = 38218;

    internal static JObject Load(params string[] path)
        => JObject.Parse(File.ReadAllText(Path.Combine([ClassicDataFixture.Root, "spiraldb-overlay", .. path])));

    internal static JObject QuestJson() => Load("QuestTemplates", Quest + ".json");

    [Fact]
    public void TheSigilEntryNamesTheQuest()
        => Assert.Equal(Quest, QuestTakenEntry.QuestNameOf(SigilEntry));

    [Fact]
    public void ItIsASideQuestOfTheClientTable() {
        var quest = QuestJson();
        Assert.Equal(Quest, (string?) quest["m_questName"]);
        Assert.Equal("QuestTitle_2EBE5", (string?) quest["m_questTitle"]);
        Assert.False((bool) quest["m_mainline"]!);
        Assert.Equal(0, (int) quest["m_questRepeat"]!);
        Assert.Equal(50, (int) quest["m_questLevel"]!);

        var keys = quest.Descendants().OfType<JProperty>()
            .Where(p => p.Name is "m_dialog" or "m_locationName" && p.Value.Type == JTokenType.String)
            .Select(p => (string) p.Value!)
            .Where(k => k.StartsWith("WizQst", StringComparison.Ordinal))
            .ToList();
        Assert.All(keys, key => Assert.StartsWith("WizQst2EBE5_", key));
        Assert.Equal(19, keys.Distinct().Count()); // keys 0-1 and 3-19; the client table has no key 2
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(49, false)]
    [InlineData(50, true)]
    public void SergeantMuldoonOffersItAtLevelFifty(int level, bool offered) {
        var quest = QuestJson();
        var requirement = Assert.Single(quest["m_requirements"]!["m_requirements"]!);
        Assert.EndsWith("ReqMagicLevel, Imcodec.ObjectProperty", (string?) requirement["$type"]);
        Assert.False((bool) requirement["m_applyNOT"]!);
        Assert.Equal(offered, NumericRequirement.Meets(level,
            (string?) requirement["m_operatorType"], (float) requirement["m_numericValue"]!));

        var prep = DialogOf(quest, "Prep");
        Assert.All(prep["m_dialogEntries"]!, entry => Assert.Equal(Muldoon, (int) entry["m_actorTemplateID"]!));
        Assert.Equal(["WizQst2EBE5_00000004", "WizQst2EBE5_00000005", "WizQst2EBE5_00000006", "WizQst2EBE5_00000007"],
            prep["m_dialogEntries"]!.Select(entry => (string) entry["m_dialog"]!));
        var underway = Assert.Single(DialogOf(quest, "Underway")["m_dialogEntries"]!);
        Assert.Equal(Muldoon, (int) underway["m_actorTemplateID"]!);
        Assert.Equal("WizQst2EBE5_00000003", (string?) underway["m_dialog"]);
    }

    [Fact]
    public void TakingItSetsTheSigilEntryForGood() {
        var result = Assert.Single(QuestJson()["m_startResults"]!["m_results"]!);
        Assert.EndsWith("ResModifyEntry, Imcodec.ObjectProperty", (string?) result["$type"]);
        Assert.Equal(SigilEntry, (string?) result["m_entryName"]);
        Assert.False((bool) result["m_isQuestRegistry"]!); // the sigil reads the wizard registry, not the quest's own
        Assert.Equal(1, (int) result["m_value"]!);

        // Nothing at the end clears it, so the wizard can enter again after the rescue.
        Assert.DoesNotContain(QuestJson().Descendants().OfType<JProperty>(),
            p => p.Name == "m_entryName" && (string?) p.Value == SigilEntry && p.Parent?["m_value"]?.Value<int>() == 0);
    }

    [Fact]
    public void EachGoalLeadsToTheNextAndCulpepperFinishesIt() {
        var quest = QuestJson();
        var goals = quest["m_goals"]!.ToList();
        (string Persona, int Template, string Zone, string[] Keys)[] chain = [
            ("WC-SHP-NPC01", 38220, "WizardCity/WC_Shop_Area", ["08", "09", "10"]),             // Private O'Doyle
            ("WC-ST06-NPC01", 38170, "WizardCity/WC_Streets/WC_Colossus", ["11", "12"]),         // Mindy Pixiecrown
            ("WC-ST06-NPC04", 38178, "WizardCity/WC_Streets/WC_Colossus", ["13", "14", "15", "16"]), // Kirby Longspear
            ("WC-ST06-NPC06_Culpepper", 191462, "WizardCity/Gauntlets/WC_Gauntlet_01/Room11", ["17", "18", "19"]),
        ];
        Assert.Equal(chain.Length, goals.Count);
        Assert.Equal([(string) goals[0]["m_goalName"]!], quest["m_startGoals"]!.Select(g => (string) g!));

        var logic = quest["m_goalLogic"]!.ToList();
        for (var i = 0; i < chain.Length; i++) {
            var goal = goals[i];
            Assert.Equal("GOAL_TYPE_PERSONA", (string?) goal["m_goalType"]);
            Assert.Equal(chain[i].Persona, (string?) goal["m_personaName"]);
            Assert.Equal(chain[i].Zone, (string?) goal["m_destinationZone"]);
            var completion = Assert.Single(goal["m_dialogList"]!["m_dialogs"]!);
            Assert.Equal("Completion", (string?) completion["m_dialogTag"]);
            Assert.All(completion["m_dialogEntries"]!, entry => {
                Assert.Equal(chain[i].Template, (int) entry["m_actorTemplateID"]!);
                Assert.Equal(chain[i].Persona + "_Persona", (string?) entry["m_personaName"]);
            });
            Assert.Equal(chain[i].Keys.Select(k => "WizQst2EBE5_000000" + k),
                completion["m_dialogEntries"]!.Select(entry => (string) entry["m_dialog"]!));

            var step = logic.Single(l => l["m_goalsAND"]!.Select(g => (string) g!).SequenceEqual([(string) goal["m_goalName"]!]));
            var last = i == chain.Length - 1;
            Assert.Equal(last, (bool) step["m_completeQuest"]!);
            Assert.Equal(last ? [] : [(string) goals[i + 1]["m_goalName"]!], step["m_goalsToAdd"]!.Select(g => (string) g!));
        }
    }

    [Fact]
    public void TheRewardIsThePlaceholderTable() {
        var end = Assert.Single(QuestJson()["m_endResults"]!["m_results"]!);
        Assert.Equal(Quest, (string?) end["m_tableName"]);
        var table = Load("DropTables", Quest + ".json");
        Assert.Equal(128, (int) table["MinGold"]!);
        Assert.Equal(128, (int) table["MaxGold"]!);
        Assert.Equal(285, (int) table["ExperienceAmount"]!);
        Assert.Empty(table["Items"]!);

        var provenance = File.ReadAllText(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates",
            Quest + ".provenance.yaml"));
        Assert.Contains("confidence: unverified", provenance);
    }

    [Fact]
    public void OnlyTheLateProfileHasIt() {
        Assert.Empty(ClassicDataFixture.LoadProfile("late-2009").DisabledQuests);
        Assert.Empty(ClassicDataFixture.LoadProfile("dev-unrestricted").DisabledQuests);
        Assert.Equal([Quest], ClassicDataFixture.LoadProfile("arc1-2009h1").DisabledQuests);
    }

    [Fact]
    public void DisabledQuestsAreInheritedAndAChildListReplacesIt() {
        using var data = new TempClassicData();
        data.WriteProfile("base", """
            id: base
            title: Base
            status: canonical
            cutoff: 2010-05-25
            disabled_quests: [A-QUEST-001, B-QUEST-002]
            """);
        data.WriteProfile("child", """
            id: child
            title: Child
            status: optional
            extends: base
            cutoff: 2009-06-30
            """);
        data.WriteProfile("grandchild", """
            id: grandchild
            title: Grandchild
            status: optional
            extends: child
            cutoff: 2009-06-30
            disabled_quests: [C-QUEST-003]
            """);

        Assert.Equal(["A-QUEST-001", "B-QUEST-002"], ClassicProfileLoader.Load(data.ProfilesPath, "child").DisabledQuests);
        Assert.Equal(["C-QUEST-003"], ClassicProfileLoader.Load(data.ProfilesPath, "grandchild").DisabledQuests);
    }

    [Theory]
    [InlineData("disabled_quests: [A-QUEST-001, a-quest-001]", "listed twice")]
    [InlineData("disabled_quests: ['has space']", "not a quest name")]
    [InlineData("disabled_quests: A-QUEST-001", "expected a list")]
    public void BadDisabledQuestsAreRejected(string yaml, string message) {
        using var data = new TempClassicData();
        data.WriteProfile("p", "id: p\ntitle: P\nstatus: canonical\ncutoff: 2010-05-25\n" + yaml);
        var ex = Assert.Throws<ClassicDataException>(() => ClassicProfileLoader.Load(data.ProfilesPath, "p"));
        Assert.Contains(message, ex.Message);
    }

    private static JToken DialogOf(JObject quest, string tag)
        => quest["m_dialogList"]!["m_dialogs"]!.Single(d => (string?) d["m_dialogTag"] == tag);

}
