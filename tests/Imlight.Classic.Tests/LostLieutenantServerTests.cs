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
 * LOST LIEUTENANT ON THE SERVER
 * ========================================================================
 *
 * PURPOSE:
 * Runs the Lost Lieutenant overlay (WC-GNT-C01-001) through the server's own
 * types: it loads as a QuestTemplate, its offer needs level 50 under the
 * classic engine, the entry its start result writes passes the tower sigil's
 * requirement, Muldoon's story quest is offered first, and a profile's
 * disabled_quests removes it.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter LostLieutenant
 *
 * NOTE:
 * A server integration test (CoreLib types); reads the monorepo's classic-data.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
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
public sealed class LostLieutenantServerTests : IDisposable {

    private static readonly JsonSerializerSettings s_json = new() {
        TypeNameHandling = TypeNameHandling.Auto,
        NullValueHandling = NullValueHandling.Ignore,
    };

    public LostLieutenantServerTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(new ClassicRules(ZoneFixture.Profile(levelCap: 50), ZoneFixture.MinimalMap()));
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-lostlt-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    private static QuestTemplate LoadQuest(string name)
        => JsonConvert.DeserializeObject<QuestTemplate>(File.ReadAllText(Path.Combine(ClassicDataFixture.Root,
            "spiraldb-overlay", "QuestTemplates", name + ".json")), s_json)!;

    private static Wizard WizardAt(int level) => new() {
        CharId = 1,
        MagicSchoolBehavior = new ServerMagicSchoolBehavior { Level = level },
        QuestBehavior = new ServerQuestBehavior(),
    };

    // The ToGauntlet01 sigil's requirement as the client zone data states it (gauntlet-cheats report).
    private static RequirementList Sigil() => new() {
        m_operator = Operator.ROP_AND,
        m_requirements = [new ReqHasEntry {
            m_entryName = LostLieutenantTests.SigilEntry, m_isQuestRegistry = false, m_operator = Operator.ROP_AND,
        }],
    };

    private static bool Meets(RequirementList requirements, Wizard wizard)
        => RequirementDispatcher.EvaluateRequirements(requirements,
            new QuestRequirementContext(requirements, null, null, wizard, LostLieutenantTests.Quest));

    [Theory]
    [InlineData(10, false)]
    [InlineData(49, false)]
    [InlineData(50, true)]
    public void TheOfferNeedsLevelFifty(int level, bool offered) {
        var quest = LoadQuest(LostLieutenantTests.Quest);
        Assert.Equal(offered, Meets(quest.m_requirements, WizardAt(level)));
    }

    [Fact]
    public void TheStartResultLightsTheSigilAndCompletionKeepsItLit() {
        var quest = LoadQuest(LostLieutenantTests.Quest);
        var wizard = WizardAt(50);
        Assert.False(Meets(Sigil(), wizard)); // dark before the quest
        Assert.False(Imlight.CoreLib.Classic.BriskbreezeTower.IsUnlocked(wizard));

        // What ResModifyEntryHandler does with the quest's start result (Wizard.SetRegistryValue, without its save).
        var start = Assert.IsType<ResModifyEntry>(Assert.Single(quest.m_startResults.m_results));
        Assert.False(start.m_isQuestRegistry);
        Assert.True(wizard.QuestBehavior.SetRegistryValue(start.m_entryName, (ulong) start.m_value));
        Assert.True(Meets(Sigil(), wizard));
        Assert.True(Imlight.CoreLib.Classic.BriskbreezeTower.IsUnlocked(wizard)); // the spellbook guide follows the same entry

        // Completion only rolls the reward table; nothing clears the entry.
        Assert.All(quest.m_endResults.m_results, result => Assert.IsNotType<ResModifyEntry>(result));
        Assert.All(quest.m_goals.SelectMany(goal => goal.m_completeResults?.m_results ?? []),
            result => Assert.IsNotType<ResModifyEntry>(result));
        Assert.True(Meets(Sigil(), wizard));
    }

    [Fact]
    public void TheGoalChainIsFourTalksEndingWithCulpepper() {
        var quest = LoadQuest(LostLieutenantTests.Quest);
        Assert.Equal(["WC-SHP-NPC01", "WC-ST06-NPC01", "WC-ST06-NPC04", "WC-ST06-NPC06_Culpepper"],
            quest.m_goals.Select(goal => Assert.IsType<PersonaGoalTemplate>(goal).m_personaName.ToString()));

        // The client's compiled quest-helper (POI) table keys this quest's arrows by these ids: KingsIsle's goal names
        // "Goal", "Goal 1", "Goal 2" and "Goal 3" (r806919 POI records under StringHash("WC-GNT-C01-001")).
        Assert.Equal(new[] { "Goal", "Goal 1", "Goal 2", "Goal 3" }.Select(Imcodec.Cryptography.StringHash.Compute),
            quest.m_goals.Select(goal => goal.m_goalNameID));

        // Walk the goal logic from the start goals: each step adds the next goal, the last completes the quest.
        var active = quest.m_startGoals.Select(goal => goal.ToString()).ToList();
        var visited = new List<string>();
        var completed = false;
        while (active.Count > 0 && !completed) {
            var goal = Assert.Single(active);
            visited.Add(goal);
            var step = quest.m_goalLogic.Single(logic => logic.m_goalsAND.Select(g => g.ToString()).SequenceEqual([goal]));
            completed = step.m_completeQuest;
            active = [.. step.m_goalsToAdd.Select(g => g.ToString())];
        }

        Assert.True(completed);
        Assert.Equal(quest.m_goals.Select(goal => goal.m_goalName.ToString()), visited);
    }

    [Fact]
    public void MuldoonOffersHisStoryQuestBeforeIt() {
        var side = LoadQuest(LostLieutenantTests.Quest);
        var story = LoadQuest("WC-CLASSIC-MAIN-002b"); // Muldoon's Arc 1 street quest
        Assert.True(story.m_mainline);
        var offers = new List<QuestTemplate> { side, story };
        offers.Sort(InteractQuestOfferComponent.OfferOrder);
        Assert.Same(story, offers[0]);
    }

    [Fact]
    public void DisabledQuestsAreRemovedAndUnknownNamesReported() {
        var quests = new ConcurrentDictionary<string, QuestTemplate>(StringComparer.OrdinalIgnoreCase) {
            [LostLieutenantTests.Quest] = LoadQuest(LostLieutenantTests.Quest),
            ["WC-CLASSIC-MAIN-002b"] = LoadQuest("WC-CLASSIC-MAIN-002b"),
        };

        var removed = SpiralDB.RemoveDisabledQuests(quests, ["wc-gnt-c01-001", "NO-SUCH-QUEST"], out var unmatched);

        Assert.Equal(["wc-gnt-c01-001"], removed);
        Assert.Equal(["NO-SUCH-QUEST"], unmatched);
        Assert.Equal(["WC-CLASSIC-MAIN-002b"], quests.Keys);
    }

}
