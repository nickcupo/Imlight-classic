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
 * CLASSIC QUEST ENGINE
 * ========================================================================
 *
 * PURPOSE:
 * The active goals whose start posts zone events and nothing else, to post
 * again when the wizard enters the goal's zone: a new instance of the zone
 * has not seen them.
 *
 * USAGE EXAMPLE:
 * foreach (var (quest, goal) in GoalZoneEvents.ToReplay(held, isActive, zone)) { ... }
 *
 * NOTE:
 * Big Ben's level-5 Travis Pawman exists only after MovePawman, which
 * Stealthy Stuff's second goal posts when it starts. A wizard who leaves
 * Big Ben and comes back gets a new instance where Travis is still on
 * level 1, so the goal could never be finished. Only goals whose
 * activate results are all ResPostEvent qualify (GH-FORT-C03-003's mixes
 * in a ResModifyEntry and is left alone), and only in the goal's own
 * destination zone.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic;

/// <summary>Active goals whose zone events are posted again on entering their zone.</summary>
internal static class GoalZoneEvents {

    /// <summary>
    /// The (quest, goal) pairs to replay in <paramref name="zone"/>: active goals of held quests whose destination is
    /// that zone and whose activate results are all ResPostEvent.
    /// </summary>
    internal static IEnumerable<(QuestTemplate Quest, GoalTemplate Goal)> ToReplay(IEnumerable<QuestTemplate> held,
        Func<QuestTemplate, string, bool> isActive, string zone) {
        if (string.IsNullOrEmpty(zone)) {
            yield break;
        }

        foreach (var quest in held) {
            foreach (var goal in quest?.m_goals ?? []) {
                var results = goal?.m_activateResults?.m_results;
                if (results is not { Count: > 0 } || !results.All(r => r is ResPostEvent)
                    || !string.Equals(goal.m_destinationZone, zone, StringComparison.OrdinalIgnoreCase)
                    || !isActive(quest, goal.m_goalName)) {
                    continue;
                }

                yield return (quest, goal);
            }
        }
    }

}
