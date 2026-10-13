/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
 * GOAL ZONE REPLAY (FINISHED GOALS)
 * ========================================================================
 *
 * PURPOSE:
 * A boss that a finished goal's event spawns (the Dean, Malistaire) is posted again to a new zone instance while the
 * quest's kill goal is open, so a lost duel, a flight or a logout cannot make the main story unfinishable.
 *
 * Created by: Nick with Claude Code (claude-sonnet-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/13/2026
 */

using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Newtonsoft.Json;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class GoalZoneReplayFinishedGoalTests {

    private static readonly JsonSerializerSettings s_json = new() {
        TypeNameHandling = TypeNameHandling.Auto,
        NullValueHandling = NullValueHandling.Ignore,
    };

    private static QuestTemplate LoadQuest(string name)
        => JsonConvert.DeserializeObject<QuestTemplate>(
            File.ReadAllText(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates", name + ".json")), s_json)!;

    [Fact]
    public void DeansFightEventIsPostedAgainInDetentionT4WhileTheKillGoalIsOpen() {
        const string t4 = "DragonSpire/DS_A2_Battle/Interiors/DS_Detention_T4";
        var quest = LoadQuest("DS-NEC3-C02-003");
        var open = quest.m_goals.Single(g => g is BountyGoalTemplate).m_goalName.ToString();
        var talk = quest.m_goals.Single(g => g.m_goalName.ToString() == "Goal 3");

        var replay = GoalZoneEvents.ToReplayCompleted([quest], (_, goal) => goal == open, (_, goal) => goal == "Goal 3", t4).ToList();
        var (_, goal, posts) = Assert.Single(replay);
        Assert.Equal(talk, goal);
        Assert.Equal("DS-NEC3-C02-003_Fight", Assert.Single(posts).m_eventName.ToString());

        // Not elsewhere, not once the kill is done, not before the talk is finished.
        Assert.Empty(GoalZoneEvents.ToReplayCompleted([quest], (_, g) => g == open, (_, g) => g == "Goal 3", "DragonSpire/DS_A2_Battle/DS_A2Z3_Detention"));
        Assert.Empty(GoalZoneEvents.ToReplayCompleted([quest], (_, _) => false, (_, _) => true, t4));
        Assert.Empty(GoalZoneEvents.ToReplayCompleted([quest], (_, g) => g == open, (_, _) => false, t4));
    }

    [Fact]
    public void MalistairesSceneEventIsPostedAgainInTheLairWhileHisKillGoalIsOpen() {
        const string lair = "DragonSpire/DS_A3_Kings/Interiors/DS_MalistaireLair";
        var quest = LoadQuest("DS-ACAD-C01-005");
        var kill = quest.m_goals.Single(g => g is BountyGoalTemplate).m_goalName.ToString();

        var replay = GoalZoneEvents.ToReplayCompleted([quest], (_, g) => g == kill, (_, g) => g is "Goal" or "Goal 2", lair).ToList();
        var (_, goal, posts) = Assert.Single(replay);
        Assert.Equal("Goal 2", goal.m_goalName.ToString());
        Assert.Contains(posts, p => p.m_eventName.ToString() == "EndMalistaireCinematic");
    }

}
