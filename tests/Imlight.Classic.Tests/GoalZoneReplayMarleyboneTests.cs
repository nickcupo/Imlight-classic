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
 * GOAL ZONE REPLAY (MARLEYBONE)
 * ========================================================================
 *
 * PURPOSE:
 * Keeps two Marleybone goals that depend on a zone's own state working after a
 * wizard leaves and comes back (a new instance has not seen the earlier events).
 *
 * NOTE:
 * It's Alive! goal 5 talks to the Clockwork, which spawns on the zone event of
 * If You Build It... "Goal 2"; goal 5 re-posts that event. Keys to Success
 * goals 1-3 activate the Big Ben chest spawners. Both are replayed on entering
 * the goal's zone (GoalZoneEvents).
 *
 * Created by: Nick with Claude Code (claude-sonnet-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/08/2026
 */

using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Newtonsoft.Json;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class GoalZoneReplayMarleyboneTests {

    private static readonly JsonSerializerSettings s_json = new() {
        TypeNameHandling = TypeNameHandling.Auto,
        NullValueHandling = NullValueHandling.Ignore,
    };

    private static QuestTemplate LoadQuest(string name)
        => JsonConvert.DeserializeObject<QuestTemplate>(
            File.ReadAllText(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates", name + ".json")), s_json)!;

    [Fact]
    public void ItsAliveClockwordGoalReplaysTheSpawnEventInTheLab() {
        const string lab = "Marleybone/MB_ScotlandYard/MB_KatzLab";
        var quest = LoadQuest("MB-YARD3-C02-002");
        var goal5 = quest.m_goals.Single(g => g.m_goalName.ToString() == "Goal 5");
        var post = Assert.IsType<ResPostEvent>(Assert.Single(goal5.m_activateResults.m_results));
        Assert.Equal("GoalComplete_MB-YARD3-C02-001_Goal 2", post.m_eventName.ToString());

        Assert.Equal([goal5], GoalZoneEvents.ToReplay([quest], (_, goal) => goal == "Goal 5", lab).Select(x => x.Goal));
        Assert.Empty(GoalZoneEvents.ToReplay([quest], (_, goal) => goal == "Goal 4", lab));
    }

    [Fact]
    public void KeysToSuccessGoalsActivateTheirChestSpawnersAndReplayInBigBen() {
        const string bigBen = "Marleybone/MB_BigBen/MB_BigBen";
        var quest = LoadQuest("MB-MUSE3-C01-001");
        foreach (var goal in quest.m_goals) {
            var spawns = goal.m_activateResults.m_results.OfType<ResSpawn>().ToList();
            Assert.Equal(4, spawns.Count);
            Assert.All(spawns, s => Assert.True(s.m_activate));
            Assert.Equal([goal], GoalZoneEvents.ToReplay([quest], (_, name) => name == goal.m_goalName.ToString(), bigBen)
                .Select(x => x.Goal));
        }

        Assert.Empty(GoalZoneEvents.ToReplay([quest], (_, _) => true, "Marleybone/MB_BigBen/MB_Museum"));
    }

    [Fact]
    public void AGoalThatDeactivatesASpawnerIsNotReplayed() {
        var goal = new BountyGoalTemplate {
            m_goalName = "G",
            m_destinationZone = "Z/Z",
            m_activateResults = new ResultList { m_results = [new ResSpawn { m_spawnID = 1, m_activate = false }] },
        };
        var quest = new QuestTemplate { m_questName = "Q", m_goals = [goal] };
        Assert.Empty(GoalZoneEvents.ToReplay([quest], (_, _) => true, "Z/Z"));
    }

}
