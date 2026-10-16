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
 * THE WINTERTUSK WORLD ON THE SERVER
 * ========================================================================
 *
 * PURPOSE:
 * Work package I (Wintertusk, May 2011): every quest wintertusk-2011
 * restores loads as a QuestTemplate whose goal logic, offer and reward hang
 * together; the older profiles (live october-2010-arc1 and celestia-2010)
 * tombstone every one and serve none of the Wintertusk zones; restored_quests
 * adds up along extends; the main story opens one quest after the other from
 * Cold News (after Blood Brother) to Winter News, the Nastrond instance
 * quests only while Bones of the Earth is held; Go Home in Hrundle Fjord uses
 * the Grizzleheim hub; and the Northguard dock keeps going to Hrundle Fjord
 * after A Bigger Boat.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter WintertuskWorld
 *
 * NOTE:
 * A server integration test (CoreLib types); reads the monorepo's classic-data.
 * The order is wintertusk-2011's restored_quests block, which
 * tools/classic-quests/gen_wintertusk2011.py writes in chain order (main story
 * first, then the side quests).
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
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class WintertuskWorldTests : IDisposable {

    private const string Wintertusk = "wintertusk-2011";
    private const string Celestia = "celestia-2010";
    private const string October = "october-2010-arc1";
    private const string ColdNews = "GH-CLASSIC-WT-001";
    private const string BloodBrother = "GH-FORT-C03-003";
    private const string BiggerBoat = "GH-NORTH-C02-002";
    private const string BonesOfTheEarth = "GH-FJORD-C05-005";
    private const string WinterNews = "GH-FJORD-C05-006";
    private const string HrundleFjord = "Grizzleheim/GH_HFjord/GH_HFjord";

    private static readonly JsonSerializerSettings s_json = new() {
        TypeNameHandling = TypeNameHandling.Auto,
        NullValueHandling = NullValueHandling.Ignore,
    };

    public WintertuskWorldTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(Wintertusk));
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-wintertusk-world-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    /// <summary>The quests wintertusk-2011 restores itself (its own block), in chain order.</summary>
    private static IReadOnlyList<string> Quests
        => ClassicDataFixture.LoadProfile(Wintertusk).RestoredQuests
            .Except(ClassicDataFixture.LoadProfile(Celestia).RestoredQuests, StringComparer.OrdinalIgnoreCase).ToList();

    private static string QuestPath(string name)
        => Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates", name + ".json");

    private static QuestTemplate LoadQuest(string name)
        => JsonConvert.DeserializeObject<QuestTemplate>(File.ReadAllText(QuestPath(name)), s_json)!;

    private static Wizard WizardIn(string zone, int level = 50) => new() {
        CharId = 1,
        Zone = zone,
        MagicSchoolBehavior = new ServerMagicSchoolBehavior { Level = level, MagicSchool = MagicSchool.Fire },
        QuestBehavior = new ServerQuestBehavior(),
    };

    private static bool Meets(QuestTemplate quest, Wizard wizard)
        => RequirementDispatcher.EvaluateRequirements(quest.m_requirements,
            new QuestRequirementContext(quest.m_requirements, null, null, wizard, quest.m_questName));

    private static void Complete(Wizard wizard, string quest)
        => Assert.True(wizard.QuestBehavior.SetQuestRegistryValue(quest, "Complete", 1));

    private static uint StringHash(string s) => Imcodec.Cryptography.StringHash.Compute(s);

    /// <summary>A Nastrond instance quest: it needs Bones of the Earth held (ReqHasQuest).</summary>
    private static bool IsInstance(QuestTemplate quest)
        => quest.m_requirements?.m_requirements?.OfType<ReqHasQuest>().Any() == true;

    // ---------------------------------------------------------------- the data

    [Fact]
    public void EveryWintertuskQuestLoadsWithConsistentGoalsOfferAndReward() {
        Assert.True(Quests.Count >= 82, $"wintertusk-2011 restores {Quests.Count} quests of its own");
        Assert.Equal(ColdNews, Quests[0]);
        foreach (var name in Quests) {
            var quest = LoadQuest(name);
            Assert.Equal(name, quest.m_questName);
            var goals = quest.m_goals.Select(g => g.m_goalName).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(goals.Count, quest.m_goals.Count);
            Assert.NotEmpty(quest.m_startGoals);
            Assert.All(quest.m_startGoals, g => Assert.Contains(g, goals));
            Assert.All(quest.m_goalLogic, l => Assert.All(l.m_goalsAND.Concat(l.m_goalsToAdd), g => Assert.Contains(g, goals)));
            Assert.Single(quest.m_goalLogic, l => l.m_completeQuest);
            var reached = quest.m_startGoals.Concat(quest.m_goalLogic.SelectMany(l => l.m_goalsToAdd)).ToHashSet(StringComparer.Ordinal);
            Assert.True(goals.SetEquals(reached), $"{name}: goals never added: {string.Join(", ", goals.Except(reached))}");
            Assert.All(quest.m_goals, g => Assert.Equal(StringHash(g.m_goalName), g.m_goalNameID));
            var prep = (quest.m_dialogList as ActorDialogList)?.m_dialogs.FirstOrDefault(d => d.m_dialogTag == "Prep");
            Assert.True(prep?.m_dialogEntries.FirstOrDefault()?.m_actorTemplateID > 0, $"{name}: no giver");
            var drop = Assert.IsType<ResDropTable>(quest.m_endResults.m_results[0]);
            Assert.Equal(name, drop.m_tableName);
            Assert.True(File.Exists(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "DropTables", name + ".json")),
                $"{name}: no reward table");
            Assert.StartsWith("QuestTitle_", quest.m_questTitle);
        }
    }

    [Fact]
    public void OlderProfilesTombstoneEveryWintertuskQuestAndWintertuskRestoresThem() {
        var lines = File.ReadAllLines(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates", "_disabled.txt"));
        ConcurrentDictionary<string, QuestTemplate> Loaded()
            => new(Quests.ToDictionary(n => n, n => new QuestTemplate { m_questName = n }), StringComparer.OrdinalIgnoreCase);

        foreach (var older in new[] { Celestia, October, "late-2009", "arc1-2009h1" }) {
            var quests = Loaded();
            var result = SpiralDB.ApplyTombstoneLines(lines, quests, SpiralDB.RestoredQuests(ClassicDataFixture.LoadProfile(older)));
            Assert.Empty(quests);
            Assert.Empty(result.Restored);
        }

        var mine = Loaded();
        var restored = SpiralDB.ApplyTombstoneLines(lines, mine, SpiralDB.RestoredQuests(ClassicDataFixture.LoadProfile(Wintertusk)));
        Assert.Equal(Quests.Count, mine.Count);
        Assert.Equal(Quests.Order(StringComparer.Ordinal), restored.Restored.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RestoredQuestsAddUpAlongExtends() {
        using var data = new TempClassicData();
        data.WriteProfile("base", """
            id: base
            title: Base
            status: canonical
            cutoff: 2010-05-25
            """);
        data.WriteProfile("child", """
            id: child
            title: Child
            status: optional
            extends: base
            cutoff: 2010-10-31
            restored_quests: [A-QUEST, B-QUEST]
            """);
        data.WriteProfile("grandchild", """
            id: grandchild
            title: Grandchild
            status: optional
            extends: child
            cutoff: 2011-05-31
            restored_quests: [C-QUEST, a-quest]
            """);
        Assert.Empty(ClassicProfileLoader.Load(data.ProfilesPath, "base").RestoredQuests);
        Assert.Equal(new[] { "A-QUEST", "B-QUEST" }, ClassicProfileLoader.Load(data.ProfilesPath, "child").RestoredQuests);
        Assert.Equal(new[] { "A-QUEST", "B-QUEST", "C-QUEST" }, ClassicProfileLoader.Load(data.ProfilesPath, "grandchild").RestoredQuests);

        // the real chain: wintertusk-2011 keeps whatever celestia-2010 restores
        var celestia = ClassicDataFixture.LoadProfile(Celestia).RestoredQuests;
        var wintertusk = ClassicDataFixture.LoadProfile(Wintertusk).RestoredQuests;
        Assert.All(celestia, quest => Assert.Contains(quest, wintertusk));
        Assert.Contains(ColdNews, wintertusk);
        Assert.DoesNotContain(ColdNews, celestia);
    }

    // ---------------------------------------------------------------- the chain

    [Fact]
    public void ColdNewsNeedsBloodBrother() {
        var cold = LoadQuest(ColdNews);
        var wizard = WizardIn("WizardCity/Interiors/WC_Headmistress_House", level: 45);
        Assert.False(Meets(cold, wizard));
        Complete(wizard, BloodBrother);
        Assert.True(Meets(cold, wizard));
        Assert.Equal(38168, (int) ((ActorDialogList) cold.m_dialogList).m_dialogs.First(d => d.m_dialogTag == "Prep")
            .m_dialogEntries[0].m_actorTemplateID);   // Merle Ambrose
    }

    [Fact]
    public void TheMainStoryOpensOneQuestAfterTheOtherFromColdNewsToWinterNews() {
        var story = Quests.Select(LoadQuest).Where(q => q.m_mainline).ToList();
        Assert.Equal(ColdNews, story[0].m_questName);
        Assert.Equal(WinterNews, story[^1].m_questName);
        for (var i = 0; i < story.Count; i++) {
            var quest = story[i];
            var here = WizardIn(HrundleFjord);
            foreach (var name in story.Take(i).Select(q => q.m_questName).Append(BloodBrother)) {
                Complete(here, name);
            }

            Assert.True(Meets(quest, here), $"{quest.m_questName} is not open after the quests before it");
            // nothing later opens before this quest is done (an instance quest is a step of Bones of the Earth, which
            // Winter News waits on instead)
            if (i + 1 < story.Count && !IsInstance(quest)) {
                Assert.False(Meets(story[i + 1], here), $"{story[i + 1].m_questName} opens before {quest.m_questName} is done");
            }
        }
    }

    [Fact]
    public void TheNastrondInstanceQuestsNeedBonesOfTheEarth() {
        var lowRoad = LoadQuest("GH-NAS-C01-001");
        var wizard = WizardIn("Grizzleheim/GH_HFjord/GH_Nastrond");
        foreach (var name in Quests.TakeWhile(n => n != BonesOfTheEarth)) {
            Complete(wizard, name);
        }

        Complete(wizard, BloodBrother);
        Assert.False(Meets(lowRoad, wizard));
        Assert.True(wizard.QuestBehavior.AddQuest(new QuestInstance(LoadQuest(BonesOfTheEarth), wizard.CharId)));
        Assert.True(Meets(lowRoad, wizard));
    }

    [Fact]
    public void EverySideQuestNeedsAWintertuskQuestFirst() {
        foreach (var quest in Quests.Select(LoadQuest).Where(q => !q.m_mainline)) {
            var fresh = WizardIn(HrundleFjord);
            Complete(fresh, BloodBrother);
            Assert.False(Meets(quest, fresh), $"{quest.m_questName} is open before any Wintertusk quest");
        }
    }

    // ---------------------------------------------------------------- the world

    [Fact]
    public void WintertuskZonesOpenOnlyInWintertuskAndGoHomeLeadsToNorthguard() {
        var wintertusk = ClassicDataFixture.RealRules(Wintertusk);
        var zones = Quests.Select(LoadQuest).SelectMany(q => q.m_goals).Select(g => g.m_destinationZone)
            .Where(z => z is not null && z.StartsWith("Grizzleheim/GH_HFjord", StringComparison.Ordinal)).Distinct().ToList();
        Assert.True(zones.Count >= 30, $"{zones.Count} Wintertusk zones");
        foreach (var older in new[] { October, Celestia }) {
            var rules = ClassicDataFixture.RealRules(older);
            Assert.All(zones, zone => Assert.False(rules.IsZoneAllowed(zone).Allowed, $"{zone} open in {older}"));
        }

        Assert.All(zones, zone => Assert.True(wintertusk.IsZoneAllowed(zone).Allowed,
            $"{zone} closed in wintertusk-2011: {wintertusk.IsZoneAllowed(zone).Reason}"));
        Assert.Equal("Grizzleheim", wintertusk.HubKeyFor(HrundleFjord));
        Assert.Equal("Grizzleheim", wintertusk.HubKeyFor("Grizzleheim/GH_HFjord/GH_Nastrond"));
        Assert.True(wintertusk.IsZoneAllowed("Grizzleheim/GH_MainHub").Allowed);
        Assert.False(wintertusk.IsHubKeyAllowed("Wintertusk"));
    }

    [Fact]
    public void TheNorthguardDockKeepsGoingToHrundleFjordAfterABiggerBoat() {
        Trigger Dock() => new() {
            m_triggerName = ClassicWintertuskTravel.DockTrigger,
            m_requirements = new RequirementList {
                m_operator = Operator.ROP_AND,
                m_requirements = [new ReqHasGoal { m_questName = BiggerBoat, m_goalName = "Goal 1", m_operator = Operator.ROP_AND }],
            },
        };

        var elsewhere = new List<Trigger> { Dock() };
        Assert.False(ClassicWintertuskTravel.Apply("Grizzleheim/GH_Raven", elsewhere));
        Assert.Single(elsewhere[0].m_requirements.m_requirements);

        var triggers = new List<Trigger> { Dock() };
        Assert.True(ClassicWintertuskTravel.Apply(ClassicWintertuskTravel.Northguard, triggers));
        Assert.False(ClassicWintertuskTravel.Apply(ClassicWintertuskTravel.Northguard, triggers));   // once only
        var requirements = triggers[0].m_requirements.m_requirements;
        Assert.Equal(2, requirements.Count);
        Assert.All(requirements, r => Assert.Equal(Operator.ROP_OR, r.m_operator));

        var done = WizardIn(ClassicWintertuskTravel.Northguard);
        Assert.False(RequirementDispatcher.EvaluateRequirements(triggers[0].m_requirements,
            new QuestRequirementContext(triggers[0].m_requirements, null, null, done, BiggerBoat)));
        Complete(done, BiggerBoat);
        Assert.True(RequirementDispatcher.EvaluateRequirements(triggers[0].m_requirements,
            new QuestRequirementContext(triggers[0].m_requirements, null, null, done, BiggerBoat)));
    }

}
