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
 * QUEST NAME ALIASES TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Quests first served under made-up names carry their KingsIsle names now
 * (classic-data/quests/quest-name-aliases.yaml). Checks the alias rules, that
 * classic-data uses only the new names, and the start-up migration of saved
 * characters on a schema-3 snapshot (documents exported from a rig database
 * written by the schema-3 build: quests in progress, finished quests).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Quests;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class QuestNameAliasesTests {

    private static string AliasesPath => Path.Combine(ClassicDataFixture.Root, QuestNameAliasesLoader.RelativePath);
    private static string QuestDir => Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates");

    private static QuestNameAliases Real() => QuestNameAliasesLoader.Load(AliasesPath);

    private static JObject Quest(string name) => JObject.Parse(File.ReadAllText(Path.Combine(QuestDir, name + ".json")));

    private static QuestNameAliases Inline(params (string Old, string Name)[] pairs)
        => new([.. pairs.Select(p => new QuestNameAlias(p.Old, p.Name, "targets"))], "inline");

    // ---------------------------------------------------------------- the real file

    [Fact]
    public void TheRealFileRenamesTheKnownQuests() {
        var aliases = Real();
        Assert.Equal(165, aliases.Count);
        Assert.Equal("WC-ST07-C01-001", aliases.Canonical("WC-CLASSIC-SIDE-059")); // The Looking Glass (DialogCache)
        Assert.Equal("KT-CRY7-C01-001", aliases.Canonical("KT-CLASSIC-SIDE-060")); // Danger, Beware!
        Assert.Equal("KT-CRY7-C04-001", aliases.Canonical("KT-CLASSIC-SIDE-064")); // Prince Charming
        Assert.Equal("WC-MAIN-C02-003", aliases.Canonical("WC-CLASSIC-SIDE-060")); // A Potion For Bartleby (DialogCache)
        Assert.Equal("HO-Halloween05", aliases.Canonical("HO-Halloween_2009-C01-003"));
        Assert.Equal("WC-MAIN-C01-009", aliases.Canonical("WC-MAIN-C01-009")); // not an old name
        Assert.Null(aliases.NewNameOf("MB-CLASSIC-SIDE-052")); // kept: no confident KingsIsle name
    }

    [Fact]
    public void EveryNewNameIsServedAndNoOldNameIs() {
        var aliases = Real();
        foreach (var alias in aliases.Aliases) {
            Assert.False(File.Exists(Path.Combine(QuestDir, alias.Old + ".json")), $"{alias.Old} is still a quest file");
            var path = Path.Combine(QuestDir, alias.Name + ".json");
            Assert.True(File.Exists(path), $"{alias.Name} ({alias.Old}) has no quest file");
            Assert.Equal(alias.Name, (string?) Quest(alias.Name)["m_questName"]);
        }
    }

    [Fact]
    public void ClassicDataReferencesOnlyTheNewNames() {
        var aliases = Real();
        var olds = aliases.Aliases.Select(a => Regex.Escape(a.Old)).OrderByDescending(s => s.Length);
        // Goal names stay as they were ("WC-CLASSIC-MAIN-002c_Goal0"), so a saved goal keeps finding its template goal.
        var pattern = new Regex(@"(?<![A-Za-z0-9_-])(" + string.Join("|", olds) + @")(?![A-Za-z0-9-])(?!_Goal\d)");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ClassicDataFixture.Root, "*", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(ClassicDataFixture.Root, file).Replace('\\', '/');
            if (relative == QuestNameAliasesLoader.RelativePath
                    || relative.StartsWith("spiraldb-overlay/DropTables/", StringComparison.Ordinal) // table names stay
                    || relative.EndsWith(".provenance.yaml", StringComparison.Ordinal)) { // history: "served as ..."
                continue;
            }

            if (!(relative.EndsWith(".json") || relative.EndsWith(".yaml") || relative.EndsWith(".tsv"))) {
                continue;
            }

            foreach (var (line, number) in File.ReadLines(file).Select((l, i) => (l, i + 1))) {
                // A quest's drop table keeps its old name: "m_tableName": "WC-CLASSIC-SIDE-059".
                if (line.Contains("\"m_tableName\"", StringComparison.Ordinal)) {
                    continue;
                }

                if (pattern.Match(line) is { Success: true } match) {
                    offenders.Add($"{relative}:{number}: {match.Value}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "old quest names still referenced:\n" + string.Join("\n", offenders.Take(30)));
    }

    // ---------------------------------------------------------------- the rules

    [Theory]
    [InlineData("QT-WC-CLASSIC-SIDE-059", "QT-WC-ST07-C01-001")]
    [InlineData("WC-CLASSIC-SIDE-059_Complete", "WC-ST07-C01-001_Complete")]
    [InlineData("WC-CLASSIC-SIDE-059_QT-WC-CLASSIC-SIDE-059", "WC-ST07-C01-001_QT-WC-ST07-C01-001")]
    [InlineData("KT-HUB-C03-004_QT-WC-CLASSIC-SIDE-059", "KT-HUB-C03-004_QT-WC-ST07-C01-001")]
    [InlineData("HO-Halloween_2009-C01-003_Complete", "HO-Halloween05_Complete")]
    [InlineData("WC-CLASSIC-SIDE-0590_Complete", "WC-CLASSIC-SIDE-0590_Complete")]
    [InlineData("QT-WC-CLASSIC-SIDE-05", "QT-WC-CLASSIC-SIDE-05")]
    [InlineData("ClassicKills_Rat", "ClassicKills_Rat")]
    [InlineData("QT-KT-CRY7-INSTANCE", "QT-KT-CRY7-INSTANCE")]
    [InlineData("", "")]
    public void RegistryKeysWithAnOldNameAreRewritten(string key, string expected) {
        var aliases = Inline(("WC-CLASSIC-SIDE-059", "WC-ST07-C01-001"), ("HO-Halloween_2009-C01-003", "HO-Halloween05"));
        Assert.Equal(expected, aliases.CanonicalEntry(key));
    }

    [Fact]
    public void AKeyAlreadyUnderTheNewNameKeepsTheLargerValue() {
        var aliases = Inline(("A-CLASSIC-SIDE-001", "A-X-C01-001"), ("A-CLASSIC-SIDE-002", "A-X-C01-002"));
        var registry = new Dictionary<string, ulong> {
            ["A-CLASSIC-SIDE-001_Kills"] = 7, ["A-X-C01-001_Kills"] = 3,
            ["A-CLASSIC-SIDE-002_Kills"] = 1, ["A-X-C01-002_Kills"] = 5,
        };
        var renames = aliases.MigrateRegistry(registry);
        Assert.Equal(2, renames.Count);
        Assert.Equal(7UL, registry["A-X-C01-001_Kills"]);
        Assert.Equal(5UL, registry["A-X-C01-002_Kills"]);
        Assert.Equal(2, registry.Count);
    }

    [Theory]
    [InlineData("{old: A-CLASSIC-SIDE-001, name: A-X-C01-001, evidence: magic}", "evidence")]
    [InlineData("{old: A-CLASSIC-SIDE-001, name: A-CLASSIC-SIDE-001, evidence: targets}", "both")]
    [InlineData("{old: A-CLASSIC-SIDE-001, name: A-X-C01-001, evidence: targets}\n  - {old: A-CLASSIC-SIDE-001, name: A-X-C01-002, evidence: targets}", "twice")]
    [InlineData("{old: A-CLASSIC-SIDE-001, name: A-X-C01-001, evidence: targets}\n  - {old: A-CLASSIC-SIDE-002, name: A-X-C01-001, evidence: targets}", "two quests")]
    [InlineData("{old: A-CLASSIC-SIDE-001, name: A-CLASSIC-SIDE-002, evidence: targets}\n  - {old: A-CLASSIC-SIDE-002, name: A-X-C01-001, evidence: targets}", "itself an old name")]
    [InlineData("{old: A-CLASSIC-SIDE-001, name: A-X-C01-001}", "evidence")]
    public void TheLoaderRejectsABadFile(string entries, string error) {
        var path = Path.Combine(Path.GetTempPath(), "quest-name-aliases-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, $"kind: quest-name-aliases\nversion: 1\nid: quest-name-aliases\nlicense_tag: own\naliases:\n  - {entries}\n");
        try {
            var ex = Assert.Throws<ClassicDataException>(() => QuestNameAliasesLoader.Load(path));
            Assert.Contains(error, ex.Message);
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void NoFileMeansNoAliases() {
        var root = Path.Combine(Path.GetTempPath(), "no-classic-data-" + Guid.NewGuid().ToString("N"));
        Assert.Same(QuestNameAliases.Empty, QuestNameAliasesLoader.LoadFromRoot(root));
        Assert.Equal("WC-CLASSIC-SIDE-059", QuestNameAliases.Empty.Canonical("WC-CLASSIC-SIDE-059"));
        Assert.Equal("QT-X_Complete", QuestNameAliases.Empty.CanonicalEntry("QT-X_Complete"));
    }

    // ---------------------------------------------------------------- the migration on a schema-3 snapshot

    // Exported 2026-10-05 from a rig database (rig-qnames) written by the schema-3 build (classic 3b0c3d35) with the
    // pre-rename data: dmbot01 holds Seal the Deal (WC-CLASSIC-SIDE-037) at its third goal, Danger, Beware!
    // (KT-CLASSIC-SIDE-060) at its second and The Looking Glass (WC-CLASSIC-SIDE-059) at its second, and has finished
    // A Foul Decree (WC-CLASSIC-SIDE-036); dmbot03 finished the whole tomb chain (KT-CLASSIC-SIDE-060..064).
    private const string Snapshot = """
        {
          "wizards": [
            { "id": "wizards/65-A", "CharId": 5074231002536944060, "QuestBehavior": { "Registry": {
                "ClassicKills_Rat": 2, "ClassicKills_Gobbler": 4, "ClassicKills_Golem": 5, "CL_StarterKitGiven": 1,
                "GainedEnrollment": 1, "QT-KT-CRY7-INSTANCE": 1, "ClassicKills_Cat": 7, "KT-HUB-C03-004_Complete": 1,
                "MB-MUSE3-C03-001_Complete": 1, "WC-CLASSIC-SIDE-036_Complete": 1, "QT-WC-ST06-C01-006": 1,
                "WC-MAIN-C01-009_Complete": 1 },
              "CurrentQuestIDs": [ 5750609992495420278, 5680353004116380609, 5573856025721237564 ] } },
            { "id": "wizards/129-A", "CharId": 5322322454337049478, "QuestBehavior": { "Registry": {
                "ClassicKills_Krok": 30, "KT-CRY6-C01-003_QT-KT-CRY6-C01-003": 1, "KT-CLASSIC-SIDE-060_Complete": 1,
                "ClassicKills_Undead": 8, "KT-CLASSIC-SIDE-062_Complete": 1, "KT-HUB-C03-004_Complete": 1,
                "CL_StarterKitGiven": 1, "KT-CLASSIC-SIDE-061_Complete": 1, "KT-CLASSIC-SIDE-064_Complete": 1,
                "ClassicBadge_mander-savior": 1, "KT-CLASSIC-SIDE-063_Complete": 1, "ClassicKills_Spider": 1,
                "QT-KT-CRY7-INSTANCE": 1, "GainedEnrollment": 1 },
              "CurrentQuestIDs": [ 4818087096025986047 ] } }
          ],
          "questInstances": [
            { "ID": 5750609992495420278, "OwnerCharId": 5074231002536944060, "QuestName": "WC-CLASSIC-SIDE-037", "GoalProgress": [
                { "GoalName": "1_WizardQuestGoals_Kill", "GoalType": "GOAL_TYPE_BOUNTY", "CurrentProgress": 2147483647 },
                { "GoalName": "2_WizardQuestGoals_Kill", "GoalType": "GOAL_TYPE_BOUNTY", "CurrentProgress": 2147483647 },
                { "GoalName": "3_WizardQuestGoals_TalkNPC", "GoalType": "GOAL_TYPE_PERSONA", "CurrentProgress": 0 },
                { "GoalName": "4_WizardQuestGoals_TalkNPC", "GoalType": "GOAL_TYPE_PERSONA", "CurrentProgress": -1 } ] },
            { "ID": 4818087096025986047, "OwnerCharId": 5322322454337049478, "QuestName": "KT-CRY6-C01-003", "GoalProgress": [
                { "GoalName": "1_WizardQuestGoals_Kill", "GoalType": "GOAL_TYPE_BOUNTY", "CurrentProgress": 2147483647 },
                { "GoalName": "2_WizardQuestGoals_Explore", "GoalType": "GOAL_TYPE_WAYPOINT", "CurrentProgress": 0 },
                { "GoalName": "3_WizardQuestGoals_TalkNPC", "GoalType": "GOAL_TYPE_PERSONA", "CurrentProgress": -1 } ] },
            { "ID": 5680353004116380609, "OwnerCharId": 5074231002536944060, "QuestName": "KT-CLASSIC-SIDE-060", "GoalProgress": [
                { "GoalName": "1_WizardQuestGoals_Explore", "GoalType": "GOAL_TYPE_WAYPOINT", "CurrentProgress": 2147483647 },
                { "GoalName": "2_WizardQuestGoals_Kill", "GoalType": "GOAL_TYPE_BOUNTY", "CurrentProgress": 0 } ] },
            { "ID": 5573856025721237564, "OwnerCharId": 5074231002536944060, "QuestName": "WC-CLASSIC-SIDE-059", "GoalProgress": [
                { "GoalName": "1_WizardQuestGoals_TalkNPC", "GoalType": "GOAL_TYPE_PERSONA", "CurrentProgress": 2147483647 },
                { "GoalName": "2_WizardQuestGoals_KillCollect", "GoalType": "GOAL_TYPE_BOUNTYCOLLECT", "CurrentProgress": 0 },
                { "GoalName": "3_WizardQuestGoals_TalkNPC", "GoalType": "GOAL_TYPE_PERSONA", "CurrentProgress": -1 },
                { "GoalName": "4_WizardQuestGoals_TalkNPC", "GoalType": "GOAL_TYPE_PERSONA", "CurrentProgress": -1 } ] }
          ]
        }
        """;

    private sealed record SnapshotState(Dictionary<string, Dictionary<string, ulong>> Registries, List<JObject> Instances);

    private static SnapshotState LoadSnapshot() {
        var root = JObject.Parse(Snapshot);
        var registries = root["wizards"]!.ToDictionary(
            w => (string) w["id"]!,
            w => ((JObject) w["QuestBehavior"]!["Registry"]!).Properties().ToDictionary(p => p.Name, p => (ulong) p.Value));

        return new SnapshotState(registries, [.. root["questInstances"]!.Cast<JObject>()]);
    }

    // What QuestNameMigration does to each document.
    private static (int Instances, int Keys) Migrate(QuestNameAliases aliases, SnapshotState state) {
        var instances = 0;
        foreach (var instance in state.Instances) {
            if (aliases.NewNameOf((string?) instance["QuestName"]) is { } renamed) {
                instance["QuestName"] = renamed;
                instances++;
            }
        }

        var keys = state.Registries.Values.Sum(registry => aliases.MigrateRegistry(registry).Count);

        return (instances, keys);
    }

    private static bool Satisfied(JToken? requirements, IReadOnlyDictionary<string, ulong> registry) {
        // Every quest-registry ReqHasEntry in the list (the prerequisite checks).
        var checks = requirements?.SelectTokens("$..m_requirements[?(@.m_isQuestRegistry == true)]").ToList() ?? [];
        Assert.NotEmpty(checks);

        return checks.All(check => registry.ContainsKey($"{(string?) check["m_questName"]}_{(string?) check["m_entryName"]}"));
    }

    [Fact]
    public void TheSnapshotMigratesActiveAndFinishedQuestsOnce() {
        var aliases = Real();
        var state = LoadSnapshot();

        Assert.Equal((3, 6), Migrate(aliases, state)); // 037, 060, 059 instances; 036 + tomb chain 060..064 keys
        Assert.Equal((0, 0), Migrate(aliases, state)); // idempotent

        Assert.Equal(["WC-ST06-C01-006", "KT-CRY6-C01-003", "KT-CRY7-C01-001", "WC-ST07-C01-001"],
            state.Instances.Select(i => (string?) i["QuestName"]));
        var dmbot01 = state.Registries["wizards/65-A"];
        Assert.Contains("WC-ST06-C01-005_Complete", dmbot01.Keys); // A Foul Decree
        Assert.DoesNotContain(dmbot01.Keys, key => key.Contains("CLASSIC-SIDE", StringComparison.Ordinal));
        Assert.Equal(12, dmbot01.Count); // nothing else touched
        var dmbot03 = state.Registries["wizards/129-A"];
        foreach (var quest in new[] { "KT-CRY7-C01-001", "KT-CRY7-C02-001", "KT-CRY7-C02-002", "KT-CRY7-C03-001", "KT-CRY7-C04-001" }) {
            Assert.Equal(1UL, dmbot03[quest + "_Complete"]);
        }

        Assert.Equal(1UL, dmbot03["KT-CRY6-C01-003_QT-KT-CRY6-C01-003"]);
        Assert.Equal(14, dmbot03.Count);
    }

    [Fact]
    public void AQuestInProgressGoesOnFromItsGoal() {
        var state = LoadSnapshot();
        Migrate(Real(), state);
        foreach (var instance in state.Instances) {
            var template = Quest((string) instance["QuestName"]!);
            var goalNames = template["m_goals"]!.Select(g => (string?) g["m_goalName"]).ToList();
            // Goal names are unchanged, so every saved goal still finds its template goal...
            foreach (var goal in instance["GoalProgress"]!) {
                Assert.Contains((string?) goal["GoalName"], goalNames);
            }

            // ...and the one in progress is still the next one to do.
            var active = instance["GoalProgress"]!.First(g => (int) g["CurrentProgress"]! == 0);
            var activeIndex = goalNames.IndexOf((string?) active["GoalName"]);
            Assert.All(instance["GoalProgress"]!.Take(activeIndex), g => Assert.Equal(int.MaxValue, (int) g["CurrentProgress"]!));
        }

        // The Looking Glass's arrow goals: the goal in progress (the Cyclops monocle) has the quest-helper id "Goal 2".
        var lookingGlass = Quest("WC-ST07-C01-001");
        Assert.Equal(606539207U, (uint) lookingGlass["m_goals"]![1]!["m_goalNameID"]!); // StringHash("Goal 2")
    }

    [Fact]
    public void FinishedQuestsStillOpenTheirChains() {
        var state = LoadSnapshot();
        Migrate(Real(), state);
        var dmbot01 = state.Registries["wizards/65-A"];
        var dmbot03 = state.Registries["wizards/129-A"];

        // Seal the Deal (held by dmbot01) asks for A Foul Decree, finished before the rename.
        Assert.True(Satisfied(Quest("WC-ST06-C01-006")["m_requirements"], dmbot01));
        // Prince Charming (the last of the tomb chain) asks for Prison Break, finished before the rename.
        Assert.True(Satisfied(Quest("KT-CRY7-C04-001")["m_requirements"], dmbot03));

        // To Sunken City opens once The Looking Glass (in progress for dmbot01) is done under its new name.
        var toSunkenCity = Quest("WC-ST07-C01-002")["m_requirements"];
        Assert.False(Satisfied(toSunkenCity, dmbot01));
        dmbot01["WC-ST07-C01-001_Complete"] = 1; // what ServerQuestBehavior.CompleteQuest writes
        Assert.True(Satisfied(toSunkenCity, dmbot01));
    }

}
