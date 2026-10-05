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
 * DATAMINE GATE FIXES
 * ========================================================================
 *
 * PURPOSE:
 * Pins the overlay records that open client doors and sigils the datamine gap report
 * (tools/datamine/gap_report.py) found locked: a gate entry no served quest set, or a
 * door with no destination.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter DatamineGateFixesTests
 *
 * NOTE:
 * The client's sigils check KingsIsle's "QT-<internal quest name>" entries. Where the
 * overlay serves the quest under another name, the served quest sets the entry itself
 * (a start result), as the arc1/final review did for Colossus Boulevard's castle sigil.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class DatamineGateFixesTests {

    private const string Colossus = "WizardCity-WC_Streets-WC_Colossus";

    private static JObject Load(params string[] path)
        => JObject.Parse(File.ReadAllText(Path.Combine([ClassicDataFixture.Root, "spiraldb-overlay", .. path])));

    private static JToken Teleport(string travelFile, string trigger)
        => Load("ZoneTransfer", travelFile + ".travel.json")["Teleports"]!
            .Single(t => (string?) t["TriggerName"] == trigger)["Teleport"]!;

    private static bool StartSetsEntry(string quest, string entry)
        => Load("QuestTemplates", quest + ".json")["m_startResults"]!["m_results"]!
            .Any(r => ((string?) r["$type"])!.Contains("ResModifyEntry")
                && (string?) r["m_entryName"] == entry
                && (bool?) r["m_isQuestRegistry"] == false
                && (int?) r["m_value"] == 1);

    [Fact]
    public void SealTheDealSetsTheEntryItsTowerSigilsCheck() {
        Assert.True(StartSetsEntry("WC-ST06-C01-006", "QT-WC-ST06-C01-006"));
    }

    [Theory]
    [InlineData("Street 6 Tower 1 Instance Sigil", "WizardCity/WC_Streets/Interiors/WC_Colossus_T2")] // Rotunda Chateau
    [InlineData("Street 6 Tower 2 Instance Sigil", "WizardCity/WC_Streets/Interiors/WC_Colossus_T3")] // Greebly's Garrison
    [InlineData("Street 6 CustTower 1 Instance Sigil", "WizardCity/WC_Streets/Interiors/WC_Colossus_T1")] // Troll Tower
    public void ColossusTowerSigilsLeadToTheirOwnTowers(string sigil, string destination) {
        Assert.Equal(destination, (string?) Teleport(Colossus, sigil)["m_destinationZone"]);
    }

    [Theory]
    [InlineData("WC-ST06-C01-006", "WizQst952A_00000020")] // Seal the Deal: "Have you defeated both Gobbler barons?"
    [InlineData("WC-ICE-C02-001", "WizQst13CCC_00000001")]
    [InlineData("GH-FORT-C01-002", "WizQst2A449_00000000")]
    [InlineData("MS-MAIN-C02-001", "WizQst9C21_00000002")]
    [InlineData("DS-NEC-C01-001", "WizQst1ED25_00000001")]
    public void GiversRemindWithTheirOwnUnderwayLine(string quest, string key) {
        var dialogs = Load("QuestTemplates", quest + ".json")["m_dialogList"]!["m_dialogs"]!;
        var prep = dialogs.Single(d => (string?) d["m_dialogTag"] == "Prep")["m_dialogEntries"]![0]!;
        var underway = dialogs.Single(d => (string?) d["m_dialogTag"] == "Underway")["m_dialogEntries"]!.Single();

        Assert.Equal(key, (string?) underway["m_dialog"]);
        Assert.Equal((string?) prep["m_personaName"], (string?) underway["m_personaName"]);
        Assert.Equal((int?) prep["m_actorTemplateID"], (int?) underway["m_actorTemplateID"]);
        Assert.Equal("", (string?) underway["m_soundFile"]);
    }

    [Fact]
    public void EveryOverlayUnderwayLineComesFromTheQuestsOwnTable() {
        var bad = Directory.GetFiles(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates"), "*.json")
            .Select(f => (File: Path.GetFileName(f), Quest: JObject.Parse(File.ReadAllText(f))))
            .Where(q => q.Quest["m_dialogList"] is JObject list && list["m_dialogs"] is JArray)
            .SelectMany(q => {
                var dialogs = (JArray) q.Quest["m_dialogList"]!["m_dialogs"]!;
                var tables = dialogs.Where(d => (string?) d["m_dialogTag"] == "Prep")
                    .SelectMany(d => d["m_dialogEntries"]!).Select(e => ((string?) e["m_dialog"])?.Split('_')[0]).ToHashSet();
                return dialogs.Where(d => (string?) d["m_dialogTag"] == "Underway")
                    .SelectMany(d => d["m_dialogEntries"]!)
                    .Where(e => tables.Count > 0 && !tables.Contains(((string?) e["m_dialog"])?.Split('_')[0]))
                    .Select(e => $"{q.File}: {(string?) e["m_dialog"]}");
            })
            .ToList();

        Assert.Empty(bad);
    }

    [Theory]
    [InlineData("KT-SPH3-C02-006", "QT-KT-SPH3-Gate1")] // Battle of the Sunbird: Bird gate, KT_Arena_T5
    [InlineData("KT-SPH3-C02-002", "QT-KT-SPH3-Gate2")] // Stonechin: Sun gate, KT_Arena_T6
    [InlineData("KT-SPH3-C02-003", "QT-KT-SPH3-Gate3")] // Malletmane: Snake gate, KT_Arena_T7
    [InlineData("KT-SPH3-C02-004", "QT-KT-SPH3-Gate4")] // Who's More Amazing?: Moon gate, KT_Arena_T8
    public void GrandArenaQuestsOpenTheirGate(string quest, string entry) {
        Assert.True(StartSetsEntry(quest, entry));
    }

    [Theory]
    [InlineData("KT-CRY6-C01-003")] // Tomb Town: KT_DjeseritTomb_T1 'Trigger-OpenDoorway'
    [InlineData("KT-CRY6-C01-004")] // Dem Bones: KT_AhnicTomb_T2 'Trigger-DoorOpener'
    public void TombQuestsSetTheirOwnDoorEntry(string quest) {
        Assert.Contains(Load("QuestTemplates", quest + ".json")["m_startResults"]!["m_results"]!,
            r => (string?) r["m_entryName"] == "QT-" + quest && (bool?) r["m_isQuestRegistry"] == true
                && (string?) r["m_questName"] == quest);
    }

    [Fact]
    public void TicketToKensingtonOpensTheKensingtonSigil() {
        Assert.True(StartSetsEntry("MB-CLASSIC-SIDE-052", "MB-AIRHub-C07"));
    }

    [Fact]
    public void EightLeggedQueenOpensTheCrystalGroveGauntletSigil() {
        Assert.True(StartSetsEntry("DS-ACAD1-C05-003", "QT-ACAD1-C05-003"));
        Assert.Equal("DragonSpire/DS_A3_Kings/Interiors/DS_CrystalGrove_Gauntlet_7Room",
            (string?) Teleport("DragonSpire-DS_A3_Kings-DS_A3Z1_CrystalGrove", "ToTower7FromDS_CrystalGrove")["m_destinationZone"]);
    }

}
