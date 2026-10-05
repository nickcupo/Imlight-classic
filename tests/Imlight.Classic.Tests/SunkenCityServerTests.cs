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
 * SUNKEN CITY ON THE SERVER
 * ========================================================================
 *
 * PURPOSE:
 * Runs the Sunken City chain (WC-ST07-*) through the server's own types:
 * the quests load as QuestTemplates, To Sunken City needs The Looking Glass
 * and its start result opens the Nightside sigil, each instance quest is
 * granted only on the Sunken City street after the one before it, the book
 * pedestal's DecayBook event completes Finding A Way only in the book tower,
 * and every goal chain ends in completion.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter SunkenCity
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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class SunkenCityServerTests : IDisposable {

    private static readonly JsonSerializerSettings s_json = new() {
        TypeNameHandling = TypeNameHandling.Auto,
        NullValueHandling = NullValueHandling.Ignore,
    };

    public SunkenCityServerTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(new ClassicRules(ZoneFixture.Profile(levelCap: 50), ZoneFixture.MinimalMap()));
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-sunken-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    private static QuestTemplate LoadQuest(string name)
        => JsonConvert.DeserializeObject<QuestTemplate>(File.ReadAllText(Path.Combine(ClassicDataFixture.Root,
            "spiraldb-overlay", "QuestTemplates", name + ".json")), s_json)!;

    private static Wizard WizardIn(string zone) => new() {
        CharId = 1,
        Zone = zone,
        MagicSchoolBehavior = new ServerMagicSchoolBehavior { Level = 20 },
        QuestBehavior = new ServerQuestBehavior(),
    };

    // The Nightside sigil 'Entrance to Sunken City' (MinigameSigilInfo, template 107081) as the client zone data states it.
    private static RequirementList Sigil() => new() {
        m_operator = Operator.ROP_AND,
        m_requirements = [new ReqHasEntry {
            m_entryName = SunkenCityTests.SigilEntry, m_isQuestRegistry = false, m_operator = Operator.ROP_AND,
        }],
    };

    private static bool Meets(RequirementList requirements, Wizard wizard, string quest = SunkenCityTests.ToSunkenCity)
        => RequirementDispatcher.EvaluateRequirements(requirements,
            new QuestRequirementContext(requirements, null, null, wizard, quest));

    private static void Complete(Wizard wizard, string quest)
        => Assert.True(wizard.QuestBehavior.SetQuestRegistryValue(quest, "Complete", 1));

    [Fact]
    public void MarlaOffersItAfterTheLookingGlassAndTakingItOpensTheSigil() {
        var quest = LoadQuest(SunkenCityTests.ToSunkenCity);
        var wizard = WizardIn("WizardCity/WC_NightSide");
        Assert.False(Meets(quest.m_requirements, wizard));
        Assert.False(Meets(Sigil(), wizard)); // the sigil stays dark

        Complete(wizard, "WC-ST07-C01-001");
        Assert.True(Meets(quest.m_requirements, wizard));

        var start = Assert.IsType<ResModifyEntry>(Assert.Single(quest.m_startResults.m_results));
        Assert.True(wizard.QuestBehavior.SetRegistryValue(start.m_entryName, (ulong) start.m_value));
        Assert.True(Meets(Sigil(), wizard));
    }

    [Fact]
    public void EachInstanceQuestIsGrantedOnTheStreetAfterTheOneBefore() {
        var street = WizardIn(SunkenCityTests.Street);
        var tower = WizardIn(SunkenCityTests.NortonTower);
        var quests = SunkenCityTests.Instance.Select(LoadQuest).ToList();

        // Nothing before To Sunken City.
        Assert.All(quests, q => Assert.False(Meets(q.m_requirements, street, q.m_questName)));

        // To Sunken City done (ReqHasQuest also reads a completed quest): Finding A Way, on the street only.
        Complete(street, SunkenCityTests.ToSunkenCity);
        Complete(tower, SunkenCityTests.ToSunkenCity);
        for (var i = 0; i < quests.Count; i++) {
            Assert.True(Meets(quests[i].m_requirements, street, quests[i].m_questName));
            Assert.False(Meets(quests[i].m_requirements, tower, quests[i].m_questName)); // not inside a tower
            foreach (var later in quests.Skip(i + 1)) {
                Assert.False(Meets(later.m_requirements, street, later.m_questName));
            }

            Complete(street, quests[i].m_questName);
            Complete(tower, quests[i].m_questName);
        }
    }

    [Fact]
    public void TheBookPedestalCompletesFindingAWayInTheBookTowerOnly() {
        var goal = LoadQuest(SunkenCityTests.FindingAWay).m_goals.Single(g => g.m_goalName == "Goal");
        var pedestal = new GameObjectTemplate { m_objectName = "WC_BookPedestal_SunkenCity" };
        Assert.True(InteractableQuestEvents.CompletesGoal(pedestal, SunkenCityTests.BookTower, goal));
        Assert.False(InteractableQuestEvents.CompletesGoal(pedestal, SunkenCityTests.PaulsonTower, goal));
        Assert.True(InteractableQuestEvents.IsCompletedByEvent(goal));
        Assert.Equal(["DecayBook"], InteractableQuestEvents.ZoneEventsFiredIn(pedestal, SunkenCityTests.BookTower)); // the narration trigger
    }

    [Fact]
    public void EveryGoalChainEndsInCompletion() {
        foreach (var (name, _, expected) in SunkenCityTests.Chain) {
            var quest = LoadQuest(name);
            Assert.Equal(expected.Select(g => g.Id), quest.m_goals.Select(goal => goal.m_goalNameID));

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

            Assert.True(completed, name);
            Assert.Equal(quest.m_goals.Select(goal => goal.m_goalName.ToString()), visited);
        }
    }

    [Fact]
    public void TheGateGoalsWaitForTheirVolumes() {
        (string Quest, string Volume)[] gates = [(SunkenCityTests.OpenSezMe, "Gate01Activator Volume"),
            (SunkenCityTests.ProjectMayhem, "Gate02Activator Volume"), (SunkenCityTests.FightGrubb, "Gate03Activator Volume")];
        foreach (var (quest, volume) in gates) {
            var goal = Assert.Single(LoadQuest(quest).m_goals.OfType<WaypointGoalTemplate>());
            Assert.Equal(volume, goal.m_proximityTag.ToString());
            Assert.Equal(SunkenCityTests.Street, goal.m_zoneTag.ToString());
            Assert.False(goal.m_zoneEntry);
        }
    }

}
