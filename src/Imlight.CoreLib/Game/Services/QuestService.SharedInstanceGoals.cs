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
 * QUEST SERVICE: TALK GOALS SHARED INSIDE AN INSTANCE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: when this wizard completes a talk goal (or a one-use "use" goal)
 * inside a shared instance, every other wizard in the same copy of that zone
 * who holds the quest with that goal active gets it too (owner ruling
 * 2026-10-09; rules and evidence in Imlight.Classic.Quests.InstanceGoalSharing).
 * Each wizard's own session completes its own goal (SharedInstanceGoal), so the
 * quest saves, dialogs and arrows go through the usual path.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

using System;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// CLASSIC: to a wizard's session: <paramref name="FromCharId"/> completed goal <paramref name="GoalName"/> of quest
/// <paramref name="QuestName"/> in zone <paramref name="Zone"/> of instance <paramref name="InstanceOwnerId"/>.
/// </summary>
internal sealed record SharedInstanceGoal(string QuestName, string GoalName, ulong FromCharId, string Zone,
    ulong InstanceOwnerId) : IServerMessage {

    public byte MessageOrder => 201;
    public byte ServiceID => 110;

}

internal partial class QuestService {

    // CLASSIC: after this wizard completed `goal` of `questName`: the others in the same instance copy share it.
    private void ShareInstanceGoal(Wizard wizard, string questName, GoalTemplate goal) {
        if (!ClassicQuestEngine.IsActive || wizard is null || goal is null || string.IsNullOrEmpty(questName)) {
            return;
        }

        if (!InstanceGoalSharing.SharesGoal(goal.m_goalType == GOAL_TYPE.GOAL_TYPE_PERSONA,
                goal.m_goalType == GOAL_TYPE.GOAL_TYPE_USAGE, goal.m_tallyCounter?.m_count ?? 1)) {
            return;
        }

        var me = OnlinePlayerCollection.GetOnlinePlayer(wizard.CharId);
        if (me is null || !InstanceGoalSharing.IsInstance(me.InstanceOwnerId, me.HousingDeedId)) {
            return;
        }

        foreach (var other in OnlinePlayerCollection.GetPlayersInZone(me.CurrentZone)) {
            if (other is null || other.CharacterId == wizard.CharId || string.IsNullOrEmpty(other.ActorPath)
                || AmbientWizards.IsAmbientChar(other.CharacterId)
                || !InstanceGoalSharing.SamePlace(me.CurrentZone, me.InstanceOwnerId, me.HousingDeedId,
                    other.CurrentZone, other.InstanceOwnerId, other.HousingDeedId)) {
                continue;
            }

            Context.ActorSelection(other.ActorPath).Tell(new SharedInstanceGoal(questName, goal.m_goalName, wizard.CharId,
                me.CurrentZone, me.InstanceOwnerId));
        }
    }

    [MessageHandler(typeof(SharedInstanceGoal))]
    private void ReceiveSharedInstanceGoal(SharedInstanceGoal shared) {
        var wizard = GetActiveWizard();
        if (wizard is null || !ClassicQuestEngine.IsActive || StopUncertainQuestSession(wizard)) {
            return;
        }

        // Still in that copy of that zone (a wizard who just left does not get it).
        var me = OnlinePlayerCollection.GetOnlinePlayer(wizard.CharId);
        if (me is null || !InstanceGoalSharing.SamePlace(shared.Zone, shared.InstanceOwnerId, 0, me.CurrentZone,
                me.InstanceOwnerId, me.HousingDeedId)) {
            return;
        }

        var quest = wizard.QuestBehavior.CurrentQuestInstances.FirstOrDefault(q => q.QuestName == shared.QuestName);
        var goal = quest?.GoalProgress.FirstOrDefault(g => g.GoalName == shared.GoalName);
        if (quest is null || goal is null || !quest.IsGoalActive(goal.GoalName)) {
            return;
        }

        var template = _cachedQuestTemplates.FirstOrDefault(q => q.m_questName == quest.QuestName)
                       ?? QuestTemplateCollection.GetQuestByName(quest.QuestName);
        var goalTemplate = template?.m_goals?.FirstOrDefault(g => g?.m_goalName == goal.GoalName);
        if (goalTemplate is null || !InstanceGoalSharing.SharesGoal(goalTemplate.m_goalType == GOAL_TYPE.GOAL_TYPE_PERSONA,
                goalTemplate.m_goalType == GOAL_TYPE.GOAL_TYPE_USAGE, goalTemplate.m_tallyCounter?.m_count ?? 1)) {
            return;
        }

        if (!_cachedQuestTemplates.Contains(template)) {
            _cachedQuestTemplates.Add(template);
        }

        Logger.Information("{0} shares goal '{1}' of '{2}' completed by {3} in instance {4} of {5}.",
            Logger.Args(wizard.CharId, goal.GoalName, quest.QuestName, shared.FromCharId, shared.InstanceOwnerId, shared.Zone));
        if (goalTemplate.m_goalType == GOAL_TYPE.GOAL_TYPE_USAGE) {
            if (!IncrementCommittedGoal(wizard, quest, goalTemplate, sendProgress: true)) return;
            if (goal.CurrentProgress < (goalTemplate.m_tallyCounter?.m_count ?? 1)) return;
        }

        CompleteGoal(quest, goalTemplate);
    }

}
