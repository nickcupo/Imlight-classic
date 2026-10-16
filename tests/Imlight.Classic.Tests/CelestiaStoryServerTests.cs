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
 * THE CELESTIA 2010 STORY ON THE SERVER
 * ========================================================================
 *
 * PURPOSE:
 * Runs the October 2010 Celestia main story (work package D) through the
 * server's own types: every quest celestia-2010 restores loads as a
 * QuestTemplate whose goal logic, start goals, offer and reward table hang
 * together; the older profiles tombstone every one of them and celestia-2010
 * restores every one; the requirement chain opens one quest after the other
 * in story order (Door to the Stars at level 48 after The Final Countdown,
 * the instance quests only in their zone, the Professor quests only at level
 * 58 and for their school); and the Spiral Door lists Celestia only in
 * celestia-2010 and only once Door to the Stars is held.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter CelestiaStory
 *
 * NOTE:
 * A server integration test (CoreLib types); reads the monorepo's classic-data.
 * The story order is the order of celestia-2010's restored_quests block, which
 * tools/classic-quests/gen_celestia2010.py writes in chain order.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/16/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Travel;
using Imlight.Common;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class CelestiaStoryServerTests : IDisposable {

    private const string Celestia = "celestia-2010";
    private const string October = "october-2010-arc1";
    private const string DoorToTheStars = "WC-MAIN-C01-013";
    private const string FinalCountdown = "DS-ACAD-C01-005";

    private static readonly JsonSerializerSettings s_json = new() {
        TypeNameHandling = TypeNameHandling.Auto,
        NullValueHandling = NullValueHandling.Ignore,
    };

    public CelestiaStoryServerTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(Celestia));
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-celestia-story-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    private static IReadOnlyList<string> Story => ClassicDataFixture.LoadProfile(Celestia).RestoredQuests;

    private static string QuestPath(string name)
        => Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates", name + ".json");

    private static QuestTemplate LoadQuest(string name)
        => JsonConvert.DeserializeObject<QuestTemplate>(File.ReadAllText(QuestPath(name)), s_json)!;

    private static bool IsSchoolQuest(string name) => name.Contains("-C07-", StringComparison.Ordinal);

    private static Wizard WizardIn(string zone, int level = 48, string school = "Fire") => new() {
        CharId = 1,
        Zone = zone,
        MagicSchoolBehavior = new ServerMagicSchoolBehavior { Level = level, MagicSchool = Enum.Parse<MagicSchool>(school) },
        QuestBehavior = new ServerQuestBehavior(),
    };

    private static bool Meets(QuestTemplate quest, Wizard wizard)
        => RequirementDispatcher.EvaluateRequirements(quest.m_requirements,
            new QuestRequirementContext(quest.m_requirements, null, null, wizard, quest.m_questName));

    private static void Complete(Wizard wizard, string quest)
        => Assert.True(wizard.QuestBehavior.SetQuestRegistryValue(quest, "Complete", 1));

    private static string? GrantZone(QuestTemplate quest)
        => quest.m_requirements?.m_requirements?.OfType<ReqInZone>().Select(r => r.m_zoneName).FirstOrDefault();

    // ---------------------------------------------------------------- the data

    [Fact]
    public void EveryRestoredQuestLoadsWithConsistentGoalsOfferAndReward() {
        Assert.True(Story.Count >= 116, $"celestia-2010 restores {Story.Count} quests");
        Assert.Equal(DoorToTheStars, Story[0]);
        foreach (var name in Story) {
            var quest = LoadQuest(name);
            Assert.Equal(name, quest.m_questName);
            var goals = quest.m_goals.Select(g => g.m_goalName).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(goals.Count, quest.m_goals.Count); // no goal name twice
            Assert.NotEmpty(quest.m_startGoals);
            Assert.All(quest.m_startGoals, g => Assert.Contains(g, goals));
            Assert.All(quest.m_goalLogic, l => Assert.All(l.m_goalsAND.Concat(l.m_goalsToAdd), g => Assert.Contains(g, goals)));
            Assert.Single(quest.m_goalLogic, l => l.m_completeQuest);
            // every goal is reached: the start goals plus everything a logic entry adds
            var reached = quest.m_startGoals.Concat(quest.m_goalLogic.SelectMany(l => l.m_goalsToAdd)).ToHashSet(StringComparer.Ordinal);
            Assert.True(goals.SetEquals(reached), $"{name}: goals never added: {string.Join(", ", goals.Except(reached))}");
            // goal ids are the KingsIsle hash of the goal name (the client's quest arrow finds them)
            Assert.All(quest.m_goals, g => Assert.Equal(StringHash(g.m_goalName), g.m_goalNameID));
            // an offered quest has a Prep giver; an instance quest is granted in its zone
            var prep = (quest.m_dialogList as ActorDialogList)?.m_dialogs.FirstOrDefault(d => d.m_dialogTag == "Prep");
            Assert.True(prep?.m_dialogEntries.FirstOrDefault()?.m_actorTemplateID > 0 || GrantZone(quest) is not null,
                $"{name}: neither a giver nor a zone grant");
            var drop = Assert.IsType<ResDropTable>(quest.m_endResults.m_results[0]);
            Assert.Equal(name, drop.m_tableName);
            Assert.True(File.Exists(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "DropTables", name + ".json")),
                $"{name}: no reward table");
            Assert.StartsWith("QuestTitle_", quest.m_questTitle);
        }
    }

    [Fact]
    public void TheProfessorQuestsTeachTheRankEightSpells() {
        var taught = Story.Where(IsSchoolQuest).Select(LoadQuest)
            .SelectMany(q => q.m_endResults.m_results.OfType<ResLearnSpell>()).Select(r => r.m_templateID).ToList();
        // Snow Angel, Efreet, Leviathan, Medusa, Forest Lord, Ra, Skeletal Dragon
        Assert.Equal(new uint[] { 1037864232, 1401700123, 1405772650, 2028234611, 1408643162, 2066, 1111782343 }.Order(),
            taught.Select(t => (uint) t).Order());
    }

    [Fact]
    public void OlderProfilesTombstoneTheWholeStoryAndCelestiaRestoresIt() {
        var lines = File.ReadAllLines(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates", "_disabled.txt"));
        ConcurrentDictionary<string, QuestTemplate> Loaded()
            => new(Story.ToDictionary(n => n, n => new QuestTemplate { m_questName = n }), StringComparer.OrdinalIgnoreCase);

        foreach (var older in new[] { October, "late-2009", "arc1-2009h1" }) {
            var quests = Loaded();
            var result = SpiralDB.ApplyTombstoneLines(lines, quests, SpiralDB.RestoredQuests(ClassicDataFixture.LoadProfile(older)));
            Assert.Empty(quests);
            Assert.Empty(result.Restored);
        }

        var mine = Loaded();
        var restored = SpiralDB.ApplyTombstoneLines(lines, mine, SpiralDB.RestoredQuests(ClassicDataFixture.LoadProfile(Celestia)));
        Assert.Equal(Story.Count, mine.Count);
        Assert.Equal(Story.Order(StringComparer.Ordinal), restored.Restored.Order(StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- the chain

    [Fact]
    public void DoorToTheStarsNeedsLevelFortyEightAndTheFinalCountdown() {
        var door = LoadQuest(DoorToTheStars);
        var tooLow = WizardIn("WizardCity/WC_Hub", level: 47);
        Complete(tooLow, FinalCountdown);
        Assert.False(Meets(door, tooLow));

        var notYet = WizardIn("WizardCity/WC_Hub", level: 50);
        Assert.False(Meets(door, notYet));
        Complete(notYet, FinalCountdown);
        Assert.True(Meets(door, notYet));
    }

    [Fact]
    public void TheStoryOpensOneQuestAfterTheOtherInOrder() {
        var story = Story.Where(n => !IsSchoolQuest(n)).Select(LoadQuest).ToList();
        var done = WizardIn("WizardCity/WC_Hub", level: 50);
        Complete(done, FinalCountdown);
        for (var i = 0; i < story.Count; i++) {
            var quest = story[i];
            var zone = GrantZone(quest);
            var here = WizardIn(zone ?? "Celestia/CL_Hub", level: 50);
            foreach (var name in story.Take(i).Select(q => q.m_questName).Append(FinalCountdown)) {
                Complete(here, name);
            }

            Assert.True(Meets(quest, here), $"{quest.m_questName} is not open after the quests before it");
            if (zone is not null) {
                var outside = WizardIn("Celestia/CL_Hub", level: 50);
                foreach (var name in story.Take(i).Select(q => q.m_questName).Append(FinalCountdown)) {
                    Complete(outside, name);
                }

                Assert.False(Meets(quest, outside), $"{quest.m_questName} is granted outside {zone}");
            }

            // nothing later opens before this quest is done (an instance quest is a step of its parent quest, which
            // the next story quest waits on instead)
            foreach (var later in story.Skip(i + 1).Where(q => zone is null && GrantZone(q) is null).Take(1)) {
                Assert.False(Meets(later, here), $"{later.m_questName} opens before {quest.m_questName} is done");
            }
        }
    }

    [Theory]
    [InlineData("WC-ICE-C07-001", "Ice")]
    [InlineData("WC-FIRE-C07-001", "Fire")]
    [InlineData("WC-STRM-C07-001", "Storm")]
    [InlineData("WC-MYTH-C07-001", "Myth")]
    [InlineData("WC-LIFE-C07-001", "Life")]
    [InlineData("KT-BAL-C07-001", "Balance")]
    [InlineData("WC-DETH-C07-001", "Death")]
    public void AProfessorQuestNeedsLevelFiftyEightAndItsSchool(string first, string school) {
        var quest = LoadQuest(first);
        Assert.False(Meets(quest, WizardIn("WizardCity/WC_Ravenwood", level: 57, school: school)));
        Assert.True(Meets(quest, WizardIn("WizardCity/WC_Ravenwood", level: 58, school: school)));
        var other = school == "Fire" ? "Ice" : "Fire";
        Assert.False(Meets(quest, WizardIn("WizardCity/WC_Ravenwood", level: 60, school: other)));
    }

    // ---------------------------------------------------------------- the Spiral Door

    private sealed class Progress(params string[] active) : IPlayerProgress {
        public bool HasActiveQuest(string questName) => active.Contains(questName);
        public bool HasCompletedQuest(string questName) => false;
        public bool HasEntry(string entryName) => false;
        public int Level => 50;
    }

    [Fact]
    public void TheSpiralDoorListsCelestiaOnlyInCelestiaAndOnlyWithDoorToTheStars() {
        var celestia = ClassicDataFixture.RealRules(Celestia);
        Assert.Contains("Celestia", WorldTeleportDoorComponent.ListedWorlds(celestia, null));
        Assert.DoesNotContain("Celestia", WorldTeleportDoorComponent.ListedWorlds(celestia, new Progress()));
        Assert.Contains("Celestia", WorldTeleportDoorComponent.ListedWorlds(celestia, new Progress(DoorToTheStars)));

        var october = ClassicDataFixture.RealRules(October);
        Assert.DoesNotContain("Celestia", WorldTeleportDoorComponent.ListedWorlds(october, null));
        Assert.DoesNotContain("Celestia", WorldTeleportDoorComponent.ListedWorlds(october, new Progress(DoorToTheStars)));
        // the Arc 1 worlds are listed as before
        Assert.Equal(WorldTeleportDoorComponent.SpiralDoorHubKeys.Take(6), WorldTeleportDoorComponent.ListedWorlds(october, null));
    }

    [Fact]
    public void EveryCelestiaZoneTheStoryUsesIsOpenInCelestiaAndClosedInOctober() {
        var celestia = ClassicDataFixture.RealRules(Celestia);
        var october = ClassicDataFixture.RealRules(October);
        var zones = Story.Select(LoadQuest).SelectMany(q => q.m_goals).Select(g => g.m_destinationZone)
            .Where(z => z is not null && z.StartsWith("Celestia/", StringComparison.Ordinal)).Distinct().ToList();
        Assert.NotEmpty(zones);
        foreach (var zone in zones) {
            Assert.True(celestia.IsZoneAllowed(zone).Allowed, $"{zone} closed in celestia-2010: {celestia.IsZoneAllowed(zone).Reason}");
            Assert.False(october.IsZoneAllowed(zone).Allowed, $"{zone} open in october-2010-arc1");
        }
    }

    private static uint StringHash(string s) => Imcodec.Cryptography.StringHash.Compute(s);
}
