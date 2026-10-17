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
 * QUEST ARROW MATCH TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The stock client draws a quest's arrow from its compiled quest-helper
 * table (poi.oryan.dat), keyed by StringHash(quest name) and the goal's
 * m_goalNameID. Every quest the late-2009 server serves (SpiralDB cache +
 * classic-data overlay - tombstones) must have its name and goal ids in that
 * table, or be listed in classic-data/quests/quest-arrow-gaps-2009.tsv; a
 * listed entry that now matches must be dropped from the list.
 *
 * NOTE:
 * The table and the SpiralDB cache are private (never in git): the test reads
 * W101C_POI (default ~/w101c-private/overlay/poi/poi.classic.dat, the shipped quest-arrow build) and W101C_SPIRALDB
 * (default ~/w101c-private/run/imlight/spiraldb-cache) and is skipped without them.
 * Table layout: u32 zone count, zone strings (u32 length + UTF-8), u32 record count,
 * 33-byte records <u64 key = goalId << 32 | questHash, u8, u16 zone, u32 template,
 * u32, 3 x f32, u8, u8> (other tables follow; only this one is read).
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

public sealed class QuestArrowMatchTests {

    private const int RecordSize = 33;

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string PoiPath => Environment.GetEnvironmentVariable("W101C_POI")
        ?? Path.Combine(Home, "w101c-private", "overlay", "poi", "poi.classic.dat"); // the table we ship (tools/classic-quests/poi_arrows.py)

    private static string SpiralDbPath => Environment.GetEnvironmentVariable("W101C_SPIRALDB")
        ?? Path.Combine(Home, "w101c-private", "run", "imlight", "spiraldb-cache");

    /// <summary>KingsIsle's string hash (Imcodec StringHash.Compute), the client's quest and goal ids.</summary>
    internal static uint StringHash(string input) {
        if (string.IsNullOrEmpty(input)) {
            return 0;
        }

        var result = 0;
        var shift1 = 0;
        var shift2 = 32;
        foreach (var c in input) {
            var cb = (byte) c;
            result ^= (cb - 32) << shift1;
            if (shift1 > 24) {
                result ^= (cb - 32) >> shift2;
                if (shift1 >= 27) {
                    shift1 -= 32;
                    shift2 += 32;
                }
            }

            shift1 += 5;
            shift2 -= 5;
        }

        if (result < 0) {
            result = -result;
        }

        return (uint) result;
    }

    /// <summary>The goal ids the table keeps under each quest hash.</summary>
    internal static Dictionary<uint, HashSet<uint>> ReadPoi(string path) {
        var data = File.ReadAllBytes(path);
        var offset = 0;
        uint U32() {
            var value = BitConverter.ToUInt32(data, offset);
            offset += 4;
            return value;
        }

        var zones = U32();
        for (var i = 0; i < zones; i++) {
            var length = (int) U32();
            offset += length;
        }

        var records = U32();
        var byQuest = new Dictionary<uint, HashSet<uint>>();
        for (var i = 0; i < records; i++) {
            var key = BitConverter.ToUInt64(data, offset + i * RecordSize);
            var quest = (uint) (key & 0xFFFFFFFF);
            if (!byQuest.TryGetValue(quest, out var goals)) {
                byQuest[quest] = goals = [];
            }

            goals.Add((uint) (key >> 32));
        }

        Assert.True(offset + (long) records * RecordSize <= data.Length, "truncated quest-helper table"); // more tables follow

        return byQuest;
    }

    /// <summary>The late-2009 served quests: SpiralDB, then the overlay (a later file wins), less the tombstones.</summary>
    internal static Dictionary<string, JObject> ServedQuests(string spiralDb, string overlay) {
        var quests = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { Path.Combine(spiralDb, "QuestTemplates"), Path.Combine(overlay, "QuestTemplates") }) {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json")) {
                var quest = JObject.Parse(File.ReadAllText(file)); // Newtonsoft accepts SpiralDB's trailing commas
                quests[(string) quest["m_questName"]!] = quest;
            }
        }

        foreach (var line in File.ReadLines(Path.Combine(overlay, "QuestTemplates", "_disabled.txt"))) {
            var name = line.Split('#')[0].Trim();
            if (name.Length > 0) {
                quests.Remove(name);
            }
        }

        return quests;
    }

    /// <summary>The template ids the table's usable records (flag byte 0) point at, by (quest hash, goal id).</summary>
    internal static Dictionary<(uint Quest, uint Goal), List<(uint Template, string Zone)>> ReadTargets(string path) {
        var data = File.ReadAllBytes(path);
        var offset = 0;
        var zoneCount = BitConverter.ToUInt32(data, offset);
        offset += 4;
        var zones = new List<string>();
        for (var i = 0; i < zoneCount; i++) {
            var length = (int) BitConverter.ToUInt32(data, offset);
            zones.Add(System.Text.Encoding.Latin1.GetString(data, offset + 4, length));
            offset += 4 + length;
        }
        var records = BitConverter.ToUInt32(data, offset);
        offset += 4;
        var targets = new Dictionary<(uint, uint), List<(uint, string)>>();
        for (var i = 0; i < records; i++) {
            var at = offset + i * RecordSize;
            if (data[at + 8] != 0) {
                continue; // the client skips a record whose flag byte is set (QuestHelperThread, FUN_141662bd0)
            }
            var key = ((uint) BitConverter.ToUInt64(data, at), (uint) (BitConverter.ToUInt64(data, at) >> 32));
            if (!targets.TryGetValue(key, out var list)) {
                targets[key] = list = [];
            }
            list.Add((BitConverter.ToUInt32(data, at + 11), zones[BitConverter.ToUInt16(data, at + 9)]));
        }
        return targets;
    }

    // REA audit 2026-10-17: these goals' ids used to find a record of another goal (Dorothy's "return to Dorothy"
    // pointed at Private Connelly in the street: the later client's quest had an extra goal there).
    // Then poi_arrows.py: the Smiths' volumes, the Registrar's classic zone, Triton NPCs instead of a hidden later one.
    [Theory]
    [InlineData("WC-ST01-C05-001", "2_WizardQuestGoals_TalkNPC", 81280u, "WizardCity/WC_Streets/Interiors/WC_Unicorn_H2")]
    [InlineData("WC-ST01-C05-001", "1_WizardQuestGoals_TalkNPC", 1700u, "WizardCity/WC_Streets/Interiors/WC_Unicorn_H3")]
    [InlineData("WC-CLASSIC-SIDE-054", "1_WizardQuestGoals_Explore", 1700u, "WizardCity/WC_Hub")]        // The Commons Smith
    [InlineData("WC-CLASSIC-SIDE-054", "10_WizardQuestGoals_Explore", 1700u, "WizardCity/WC_Golem_Tower")] // Golem Court Smith
    [InlineData("WC-CLASSIC-SIDE-054", "11_WizardQuestGoals_TalkNPC", 44124u, "WizardCity/WC_Hub")]     // back to Zeke
    [InlineData("WC-BFF-C01-001", "1_WizardQuestGoals_TalkNPC", 39088u, "WizardCity/WC_Golem_Tower")]   // the Registrar
    [InlineData("WC-STRM-C06-002", "1_WizardQuestGoals_TalkNPC", 38147u, "WizardCity/WC_Streets/WC_Triton")]
    public void TheArrowPointsAtTheGoalsOwnTarget(string questName, string goalName, uint template, string zone) {
        if (!File.Exists(PoiPath)) {
            Assert.Skip($"no quest-helper table at {PoiPath} (private)");
        }
        var quest = JObject.Parse(File.ReadAllText(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates",
            questName + ".json")));
        var goal = quest["m_goals"]!.Single(g => (string) g["m_goalName"]! == goalName);
        var targets = ReadTargets(PoiPath);

        Assert.True(targets.TryGetValue((StringHash(questName), (uint) goal["m_goalNameID"]!), out var found), "no record");
        Assert.Equal([(template, zone)], found.Distinct());
    }

    [Fact]
    public void TheHashIsKingsIsles() {
        Assert.Equal(2559431U, StringHash("Goal"));
        Assert.Equal(606539207U, StringHash("Goal 2"));
        Assert.Equal(442525861U, StringHash("WC-ST07-C01-001")); // The Looking Glass's records
    }

    [Fact]
    public void EveryServedQuestHasItsArrowRecordsOrIsAKnownGap() {
        if (!File.Exists(PoiPath)) {
            Assert.Skip($"no quest-helper table at {PoiPath} (private)");
        }

        if (!Directory.Exists(Path.Combine(SpiralDbPath, "QuestTemplates"))) {
            Assert.Skip($"no SpiralDB cache at {SpiralDbPath} (private)");
        }

        var poi = ReadPoi(PoiPath);
        var served = ServedQuests(SpiralDbPath, Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay"));
        var gaps = File.ReadLines(Path.Combine(ClassicDataFixture.Root, "quests", "quest-arrow-gaps-2009.tsv"))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('\t'))
            .Select(cells => (Quest: cells[0], Goal: cells[1]))
            .ToHashSet();

        var unexpected = new List<string>();
        var found = new HashSet<(string, string)>();
        foreach (var (name, quest) in served.OrderBy(q => q.Key, StringComparer.Ordinal)) {
            if (!poi.TryGetValue(StringHash(name), out var goals)) {
                if (!found.Add((name, "*")) || !gaps.Contains((name, "*"))) {
                    unexpected.Add($"{name}: no quest-helper record under this name (a made-up name? see quest-name-aliases.yaml)");
                }

                continue;
            }

            foreach (var goal in quest["m_goals"]!) {
                var goalName = (string) goal["m_goalName"]!;
                if (goals.Contains((uint) goal["m_goalNameID"]!)) {
                    continue;
                }

                found.Add((name, goalName));
                if (!gaps.Contains((name, goalName))) {
                    unexpected.Add($"{name} / {goalName}: goal id {(uint) goal["m_goalNameID"]!} is not among the quest's records");
                }
            }
        }

        var stale = gaps.Where(gap => !found.Contains(gap)).Select(gap => $"{gap.Quest}\t{gap.Goal}").ToList();
        Assert.True(unexpected.Count == 0, $"{unexpected.Count} served quests/goals without an arrow record:\n" + string.Join("\n", unexpected.Take(40)));
        Assert.True(stale.Count == 0, $"{stale.Count} listed gaps now match or are no longer served; drop them from quest-arrow-gaps-2009.tsv:\n"
            + string.Join("\n", stale.Take(40)));
    }

}
