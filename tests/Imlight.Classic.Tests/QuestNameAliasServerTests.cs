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
 * QUEST NAME ALIAS SERVER TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The server resolves an old (made-up) quest name to the quest's KingsIsle
 * name wherever a quest name or a quest-named registry key is read or written,
 * and maps a client check for "Goal N" onto the goal whose id is StringHash("Goal N").
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Behaviors;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class QuestNameAliasServerTests : IDisposable {

    private readonly QuestNameAliases _previous = QuestNameAliases.Current;

    public QuestNameAliasServerTests()
        => QuestNameAliases.Current = new([new QuestNameAlias("WC-CLASSIC-SIDE-059", "WC-ST07-C01-001", "dialogcache")], "test");

    public void Dispose() => QuestNameAliases.Current = _previous;

    [Fact]
    public void OldNamesReadAndWriteTheRenamedQuestsEntries() {
        var behavior = new ServerQuestBehavior();
        behavior.AddToQuestRegistry("WC-ST07-C01-001", "Complete", 1);

        Assert.True(behavior.HasCompletedQuest("WC-CLASSIC-SIDE-059"));
        Assert.True(behavior.HasQuestRegistryValue("WC-CLASSIC-SIDE-059", "Complete"));
        Assert.Equal(1UL, behavior.GetRegistryValue("WC-CLASSIC-SIDE-059_Complete"));

        Assert.True(behavior.SetRegistryValue("QT-WC-CLASSIC-SIDE-059", 1));
        Assert.True(behavior.Registry.ContainsKey("QT-WC-ST07-C01-001"));
        Assert.False(behavior.Registry.ContainsKey("QT-WC-CLASSIC-SIDE-059"));
        Assert.True(behavior.HasRegistryValue("QT-WC-ST07-C01-001"));

        Assert.True(behavior.SetQuestRegistryValue("WC-CLASSIC-SIDE-059", "Counter", 3));
        Assert.Equal(3UL, behavior.Registry["WC-ST07-C01-001_Counter"]);
        Assert.True(behavior.RemoveFromQuestRegistry("WC-CLASSIC-SIDE-059", "Counter"));
        Assert.False(behavior.Registry.ContainsKey("WC-ST07-C01-001_Counter"));
    }

    [Fact]
    public void OldNamesFindTheActiveRenamedQuest() {
        var behavior = new ServerQuestBehavior();
        var instance = new Imlight.CoreLib.WizardData.Models.Player.QuestInstance {
            ID = 7, QuestName = "WC-ST07-C01-001",
            GoalProgress = [new Imlight.CoreLib.WizardData.Models.Player.GoalInstance { GoalName = "2_WizardQuestGoals_KillCollect" }],
        };
        Assert.True(behavior.AddQuest(instance));
        Assert.True(behavior.HasQuest("WC-CLASSIC-SIDE-059"));
        Assert.True(behavior.StartQuestGoal("WC-CLASSIC-SIDE-059", "2_WizardQuestGoals_KillCollect"));
        Assert.True(instance.IsGoalActive("2_WizardQuestGoals_KillCollect"));
        Assert.False(behavior.HasQuest("WC-CLASSIC-SIDE-058"));
    }

    [Fact]
    public void AKingsIsleGoalNameFindsTheGoalWithItsId() {
        Assert.Equal("Goal 2", KingsIsleGoalIds.NumberedNameOf(606539207));
        Assert.Equal("Goal", KingsIsleGoalIds.NumberedNameOf(2559431));
        Assert.Null(KingsIsleGoalIds.NumberedNameOf(12345));

        var template = new QuestTemplate {
            m_questName = "KT-CRY7-C02-002",
            m_goals = [
                new PersonaGoalTemplate { m_goalName = "1_WizardQuestGoals_Kill", m_goalNameID = QuestArrowMatchTests.StringHash("Goal") },
                new PersonaGoalTemplate { m_goalName = "4_WizardQuestGoals_TalkNPC", m_goalNameID = QuestArrowMatchTests.StringHash("Goal 6") },
            ],
        };
        Assert.Equal("4_WizardQuestGoals_TalkNPC", KingsIsleGoalIds.GoalById(template, "Goal 6")?.m_goalName);
        Assert.Null(KingsIsleGoalIds.GoalById(template, "Goal 3"));
    }

}
