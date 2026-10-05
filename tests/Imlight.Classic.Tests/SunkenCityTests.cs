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
 * SUNKEN CITY (MARLA STINGER'S CHAIN)
 * ========================================================================
 *
 * PURPOSE:
 * Pins the Sunken City chain after The Looking Glass: To Sunken City
 * (WC-ST07-C01-002), the five instance quests Finding A Way, Open Sez Me,
 * Project Mayhem, Under Lock And Key and Fight Grubb! (WC-ST07-C02-001 ..
 * C04-002) and Delivering The Proof (WC-ST07-C01-003): their client text
 * tables, the goal ids and targets of the client's quest-helper (POI)
 * records, the Nightside sigil entry, the zone events, the 2010 rewards and
 * the tower doors.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter SunkenCity
 *
 * NOTE:
 * Reads the monorepo's classic-data (skipped outside it, as the other data
 * tests are). The POI records are restated here (the client file is not in
 * git): quest name, goal id and target per record.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class SunkenCityTests {

    internal const string Street = "WizardCity/WC_Streets/WC_Sunken_City";
    internal const string BookTower = "WizardCity/WC_Streets/Interiors/WC_Sunken_City_T2";
    internal const string PaulsonTower = "WizardCity/WC_Streets/Interiors/WC_Sunken_City_T3";
    internal const string NortonTower = "WizardCity/WC_Streets/Interiors/WC_Sunken_City_T1";
    internal const string SigilEntry = "QT-WC-ST07-002"; // Nightside's 'Entrance to Sunken City' sigil (template 107081)

    internal const string ToSunkenCity = "WC-ST07-C01-002";
    internal const string FindingAWay = "WC-ST07-C02-001";
    internal const string OpenSezMe = "WC-ST07-C02-002";
    internal const string ProjectMayhem = "WC-ST07-C03-001";
    internal const string UnderLockAndKey = "WC-ST07-C04-001";
    internal const string FightGrubb = "WC-ST07-C04-002";
    internal const string DeliveringTheProof = "WC-ST07-C01-003";

    internal static readonly string[] Instance = [FindingAWay, OpenSezMe, ProjectMayhem, UnderLockAndKey, FightGrubb];

    // (quest, client text table, goals in play order: goal id, kind, target). Goal ids and targets are the r806919 POI
    // records under StringHash(quest); the order is the dated wiki's.
    internal static readonly (string Quest, string Table, (uint Id, string Kind, string Target)[] Goals)[] Chain = [
        (ToSunkenCity, "140B8", [(606539207, "bounty", "Grubb"), (640093639, "persona", "WC-ST07-NPC01")]),
        (FindingAWay, "140BB", [(640093639, "persona", "WC_ST07_NPC02"), (2559431, "event", "DecayBook")]),
        (OpenSezMe, "140BC", [(640093639, "persona", "WC_ST07_NPC02"), (2559431, "volume", "Gate01Activator Volume")]),
        (ProjectMayhem, "140BE", [(606539207, "persona", "WC_ST07_NPC03"), (2559431, "bounty", "Paulson"),
            (640093639, "persona", "WC_ST07_NPC03"), (673648071, "volume", "Gate02Activator Volume")]),
        (UnderLockAndKey, "140C0", [(606539207, "persona", "WC_ST07_NPC04"), (2559431, "bounty", "Norton")]),
        (FightGrubb, "140C1", [(2559431, "persona", "WC_ST07_NPC04"), (0x1E370BE6, "volume", "Gate03Activator Volume"),
            (0x010F1939, "bounty", "Grubb")]),
        (DeliveringTheProof, "140BA", [(2559431, "persona", "WC-RAV-NPC02"), (606539207, "persona", "WC-ST07-NPC01")]),
    ];

    internal static JObject Load(params string[] path)
        => JObject.Parse(File.ReadAllText(Path.Combine([ClassicDataFixture.Root, "spiraldb-overlay", .. path])));

    internal static JObject QuestJson(string quest) => Load("QuestTemplates", quest + ".json");

    public static TheoryData<string> AllQuests() {
        var data = new TheoryData<string>();
        foreach (var (quest, _, _) in Chain) {
            data.Add(quest);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllQuests))]
    public void EachQuestUsesOnlyItsOwnClientTable(string questName) {
        var (_, table, _) = Chain.Single(c => c.Quest == questName);
        var quest = QuestJson(questName);
        Assert.Equal(questName, (string?) quest["m_questName"]);
        Assert.Equal("QuestTitle_" + table, (string?) quest["m_questTitle"]);
        Assert.False((bool) quest["m_mainline"]!);
        Assert.Equal(0, (int) quest["m_questRepeat"]!);

        var keys = quest.Descendants().OfType<JProperty>()
            .Where(p => p.Name is "m_dialog" or "m_locationName" or "m_descriptor" or "m_descriptor2" && p.Value.Type == JTokenType.String)
            .Select(p => (string) p.Value!)
            .Where(k => k.StartsWith("WizQst", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.StartsWith("WizQst" + table + "_", key));
        Assert.DoesNotContain(quest.Descendants().OfType<JProperty>(), p => p.Name == "m_soundFile" && (string?) p.Value != "");
    }

    [Theory]
    [MemberData(nameof(AllQuests))]
    public void GoalsCarryTheQuestHelperIdsAndTargetsInWikiOrder(string questName) {
        var (_, _, expected) = Chain.Single(c => c.Quest == questName);
        var quest = QuestJson(questName);
        var goals = quest["m_goals"]!.ToList();
        Assert.Equal(expected.Length, goals.Count);
        Assert.Equal([(string) goals[0]["m_goalName"]!], quest["m_startGoals"]!.Select(g => (string) g!));

        var logic = quest["m_goalLogic"]!.ToList();
        for (var i = 0; i < goals.Count; i++) {
            var goal = goals[i];
            var (id, kind, target) = expected[i];
            Assert.Equal(id, (uint) goal["m_goalNameID"]!);
            switch (kind) {
                case "persona":
                    Assert.Equal("GOAL_TYPE_PERSONA", (string?) goal["m_goalType"]);
                    Assert.Equal(target, (string?) goal["m_personaName"]);
                    break;
                case "bounty":
                    Assert.Equal("GOAL_TYPE_BOUNTYCOLLECT", (string?) goal["m_goalType"]);
                    Assert.Equal([target], goal["m_npcAdjectives"]!.Select(a => (string) a!));
                    break;
                case "volume":
                    Assert.Equal("GOAL_TYPE_WAYPOINT", (string?) goal["m_goalType"]);
                    Assert.Equal(target, (string?) goal["m_proximityTag"]);
                    Assert.Equal(Street, (string?) goal["m_zoneTag"]);
                    break;
                case "event":
                    Assert.Equal("GOAL_TYPE_WAYPOINT", (string?) goal["m_goalType"]);
                    Assert.Equal([target], goal["m_clientTags"]!.Select(t => (string) t!));
                    Assert.Equal("", (string?) goal["m_proximityTag"]);
                    Assert.Equal("", (string?) goal["m_zoneTag"]);
                    break;
            }

            var step = logic.Single(l => l["m_goalsAND"]!.Select(g => (string) g!).SequenceEqual([(string) goal["m_goalName"]!]));
            var last = i == goals.Count - 1;
            Assert.Equal(last, (bool) step["m_completeQuest"]!);
            Assert.Equal(last ? [] : [(string) goals[i + 1]["m_goalName"]!], step["m_goalsToAdd"]!.Select(g => (string) g!));
        }
    }

    [Fact]
    public void TheGoalsWhoseNamesAreKnownAreNamedLikeKingsIsle() {
        // "Goal", "Goal 2", ... hash to the POI ids; Fight Grubb!'s other two ids match no such name, so those goals
        // carry the ids under descriptive names.
        var named = 0;
        foreach (var (quest, _, _) in Chain) {
            foreach (var goal in QuestJson(quest)["m_goals"]!) {
                var name = (string) goal["m_goalName"]!;
                if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^Goal( [0-9]+)?$")) {
                    Assert.Equal(Imcodec.Cryptography.StringHash.Compute(name), (uint) goal["m_goalNameID"]!);
                    named++;
                } else {
                    Assert.Equal(FightGrubb, quest);
                }
            }
        }

        Assert.Equal(15, named);
    }

    [Fact]
    public void MarlaOffersToSunkenCityAfterTheLookingGlassAndItLightsTheSigil() {
        var quest = QuestJson(ToSunkenCity);
        var requirement = Assert.Single(quest["m_requirements"]!["m_requirements"]!);
        Assert.EndsWith("ReqHasEntry, Imcodec.ObjectProperty", (string?) requirement["$type"]);
        Assert.Equal("WC-ST07-C01-001", (string?) requirement["m_questName"]); // The Looking Glass as served
        Assert.Equal("Complete", (string?) requirement["m_entryName"]);

        Assert.Equal(["WizQst140B8_00000002"], Dialog(quest, "Prep").Select(e => (string) e["m_dialog"]!));
        Assert.Equal(["WizQst140B8_00000001"], Dialog(quest, "Underway").Select(e => (string) e["m_dialog"]!));
        Assert.All(Dialog(quest, "Prep"), e => Assert.Equal(82097, (int) e["m_actorTemplateID"]!));

        var start = Assert.Single(quest["m_startResults"]!["m_results"]!);
        Assert.EndsWith("ResModifyEntry, Imcodec.ObjectProperty", (string?) start["$type"]);
        Assert.Equal(SigilEntry, (string?) start["m_entryName"]);
        Assert.False((bool) start["m_isQuestRegistry"]!);
        Assert.Equal(1, (int) start["m_value"]!);
        foreach (var name in Chain.Select(c => c.Quest)) {
            Assert.DoesNotContain(QuestJson(name).Descendants().OfType<JProperty>(),
                p => p.Name == "m_entryName" && (string?) p.Value == SigilEntry && p.Parent?["m_value"]?.Value<int>() == 0);
        }
    }

    [Fact]
    public void TheInstanceQuestsAreGrantedOnTheStreetOneAfterAnother() {
        string previous = null!;
        foreach (var name in Instance) {
            var quest = QuestJson(name);
            var requirements = quest["m_requirements"]!["m_requirements"]!.ToList();
            Assert.Equal(2, requirements.Count);
            Assert.EndsWith("ReqInZone, Imcodec.ObjectProperty", (string?) requirements[0]["$type"]);
            Assert.Equal(Street, (string?) requirements[0]["m_zoneName"]);
            if (previous is null) {
                Assert.EndsWith("ReqHasQuest, Imcodec.ObjectProperty", (string?) requirements[1]["$type"]);
                Assert.Equal(ToSunkenCity, (string?) requirements[1]["m_questName"]);
            } else {
                Assert.EndsWith("ReqHasEntry, Imcodec.ObjectProperty", (string?) requirements[1]["$type"]);
                Assert.Equal(previous, (string?) requirements[1]["m_questName"]);
                Assert.Equal("Complete", (string?) requirements[1]["m_entryName"]);
            }

            Assert.Equal(JTokenType.Null, quest["m_dialogList"]!.Type); // no Prep: no NPC offers them
            previous = name;
        }
    }

    [Fact]
    public void TheStreetGoalsPostTheEventsThatOpenTowersAndGates() {
        // The client's WC_Sunken_City activators: TowerOne/SecondTower/ThirdTower enable the tower doors,
        // FirstGate/SecondGate/ThirdGate arm the Gate01/02/03Activator Volume triggers.
        string[] events = [.. Instance.SelectMany(q => QuestJson(q)["m_goals"]!)
            .SelectMany(g => g["m_completeResults"]!["m_results"]!)
            .Where(r => ((string?) r["$type"])!.Contains("ResPostEvent"))
            .Select(r => (string) r["m_eventName"]!)];
        Assert.Equal(["TowerOne", "FirstGate", "SecondTower", "SecondGate", "ThirdTower", "ThirdGate"], events);
    }

    [Theory]
    [InlineData(ToSunkenCity, 18, 30)]
    [InlineData(FindingAWay, 25, 75)]
    [InlineData(OpenSezMe, 0, 115)]
    [InlineData(ProjectMayhem, 23, 97)]
    [InlineData(UnderLockAndKey, 25, 125)]
    [InlineData(FightGrubb, 28, 125)]
    [InlineData(DeliveringTheProof, 46, 230)]
    public void RewardsAreTheCutoffWikiValues(string quest, int gold, int xp) {
        var end = Assert.Single(QuestJson(quest)["m_endResults"]!["m_results"]!);
        Assert.Equal(quest, (string?) end["m_tableName"]);
        var table = Load("DropTables", quest + ".json");
        Assert.Equal(gold, (int) table["MinGold"]!);
        Assert.Equal(gold, (int) table["MaxGold"]!);
        Assert.Equal(xp, (int) table["ExperienceAmount"]!);
        var items = table["Items"]!.Select(i => (string) i["ItemId"]!).ToList();
        Assert.Equal(quest == DeliveringTheProof ? ["4940"] : [], items); // Dagger of Talent (Athame-T2-023)
    }

    [Fact]
    public void DeliveringTheProofGoesToDrakeThenMarla() {
        var quest = QuestJson(DeliveringTheProof);
        var requirement = Assert.Single(quest["m_requirements"]!["m_requirements"]!);
        Assert.Equal(ToSunkenCity, (string?) requirement["m_questName"]);
        Assert.Equal("Complete", (string?) requirement["m_entryName"]);
        var goals = quest["m_goals"]!.ToList();
        Assert.Equal(["WizQst140BA_00000003"], Completion(goals[0]).Select(e => (string) e["m_dialog"]!));
        Assert.All(Completion(goals[0]), e => Assert.Equal(38206, (int) e["m_actorTemplateID"]!));
        Assert.Equal(["WizQst140BA_00000005"], Completion(goals[1]).Select(e => (string) e["m_dialog"]!));
        Assert.All(Completion(goals[1]), e => Assert.Equal(82097, (int) e["m_actorTemplateID"]!));
    }

    [Fact]
    public void BothProfilesHaveTheChain() {
        foreach (var profile in new[] { "late-2009", "arc1-2009h1" }) {
            var disabled = ClassicDataFixture.LoadProfile(profile).DisabledQuests;
            Assert.DoesNotContain(disabled, q => q.StartsWith("WC-ST07-", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void EachProvenanceNamesItsDatedWikiRevision() {
        (string Quest, string Oldid)[] pages = [(ToSunkenCity, "56980"), (FindingAWay, "65640"), (OpenSezMe, "56975"),
            (ProjectMayhem, "63035"), (UnderLockAndKey, "56977"), (FightGrubb, "56979"), (DeliveringTheProof, "56981")];
        foreach (var (quest, oldid) in pages) {
            var text = File.ReadAllText(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates",
                quest + ".provenance.yaml"));
            Assert.Contains("oldid=" + oldid, text);
            Assert.Contains("confidence: unverified", text); // the quest level and display images
        }
    }

    [Fact]
    public void TheTowerDoorsLeadToTheTowerTheirExitsReturnTo() {
        var record = Load("ZoneTransfer", "WizardCity-WC_Streets-WC_Sunken_City.json");
        Assert.Equal(Street, (string?) record["ZoneName"]);
        Assert.True((bool) record["Merge"]!);
        var doors = record["Teleports"]!.ToDictionary(t => (string) t["TriggerName"]!, t => (string) t["Teleport"]!["m_destinationZone"]!);
        Assert.Equal(BookTower, doors["TriggerTeleportTower1"]);   // the door by the Gate of Durden: the Tome of Decay
        Assert.Equal(PaulsonTower, doors["TriggerTeleportTower2"]);
        Assert.Equal(NortonTower, doors["TriggerTeleportTower3"]);  // the five-level tower
        Assert.Equal(3, doors.Count);
    }

    private static IEnumerable<JToken> Dialog(JObject quest, string tag)
        => quest["m_dialogList"]!["m_dialogs"]!.Single(d => (string?) d["m_dialogTag"] == tag)["m_dialogEntries"]!;

    private static IEnumerable<JToken> Completion(JToken goal)
        => Assert.Single(goal["m_dialogList"]!["m_dialogs"]!)["m_dialogEntries"]!;

}
