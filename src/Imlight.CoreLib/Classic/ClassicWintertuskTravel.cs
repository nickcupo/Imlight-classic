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
 * CLASSIC WINTERTUSK TRAVEL
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the way from Northguard to Hrundle Fjord (Wintertusk, May 2011)
 * after the first trip. The Northguard dock volume Volume_GoToGH2 fires the
 * client trigger Trigger_GoToGH2, which teleports to Hrundle Fjord only while
 * goal "Goal 1" of A Bigger Boat (GH-NORTH-C02-002) is open. Baldur Goldpaws
 * then says his ship is at the wizard's service for later trips, a service no
 * client or server data we have carries. Here the same trigger also passes
 * once A Bigger Boat is complete, so the dock keeps taking the wizard back.
 * The way home is Go Home (both clients list Hrundle Fjord as a hub zone of
 * the Grizzleheim world, whose Go Home leads to Northguard).
 *
 * USAGE EXAMPLE:
 * ClassicWintertuskTravel.Apply(Zone.ZonePath, triggers);   // ZoneTriggerSupervisor, classic only
 *
 * NOTE:
 * A Bigger Boat exists only in the wintertusk-2011 profile (the quest is
 * tombstoned for every older profile), and the Wintertusk zones are closed
 * elsewhere, so the change is inert in the other profiles.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/16/2026
 */

using System;
using System.Collections.Generic;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// The Northguard dock keeps sending a wizard who finished A Bigger Boat to Hrundle Fjord.
/// </summary>
internal static class ClassicWintertuskTravel {

    internal const string Northguard = "Grizzleheim/GH_MainHub";
    internal const string DockTrigger = "Trigger_GoToGH2";
    internal const string BiggerBoat = "GH-NORTH-C02-002";

    /// <summary>
    /// In Northguard: Trigger_GoToGH2 passes while A Bigger Boat's Goal 1 is open (its own requirement) or once the quest
    /// is complete. Returns true when the trigger was changed.
    /// </summary>
    internal static bool Apply(string? zonePath, List<Trigger> triggers) {
        if (!string.Equals(zonePath, Northguard, StringComparison.Ordinal) || triggers is null) {
            return false;
        }

        foreach (var trigger in triggers) {
            if (trigger is null || (string) trigger.m_triggerName != DockTrigger) {
                continue;
            }

            var requirements = trigger.m_requirements?.m_requirements;
            if (requirements is not { Count: 1 } || requirements[0] is not ReqHasGoal goal
                || !string.Equals((string) goal.m_questName, BiggerBoat, StringComparison.OrdinalIgnoreCase)) {
                return false;   // not the r806919/2014 trigger we know: leave it alone
            }

            goal.m_operator = Operator.ROP_OR;
            requirements.Add(new ReqHasEntry {
                m_entryName = "Complete",
                m_isQuestRegistry = true,
                m_questName = BiggerBoat,
                m_applyNOT = false,
                m_operator = Operator.ROP_OR,
            });

            return true;
        }

        return false;
    }

}
