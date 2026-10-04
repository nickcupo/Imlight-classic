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
 * CLASSIC COMMONS EXITS
 * ========================================================================
 *
 * PURPOSE:
 * The Commons exits to Golem Court and the Shopping District as the pre-2019
 * game gated them. The r806919 Commons triggers TeleportToGolemCourt and
 * TeleportToShoppingDistrict ask for (WC-ST01-C01-006_Complete OR
 * WC-UNICORN-MAIN-008_Complete): the July 2019 new-player chain
 * (WC-UNICORN-MAIN-008), with Rattlebones Report as the pass for wizards
 * who had played before. The 2014 client's Commons (Wizard_1_240,
 * WizardCity-WC_Hub.wad triggers.xml) asks for magic level 2 or more on
 * both teleports, and its GolemCourtGateTrigger opens the Golem Court gate
 * on EnterZone for a level 2 wizard. No pre-2010 source mentions any gate:
 * Fandom's Golem Court (rev 38617, 2009-08-06) and Commons (rev 68272,
 * 2010-05-05) list both as free-to-play areas connected to the Commons.
 *
 * USAGE EXAMPLE:
 * ClassicCommonsExits.Apply(Zone.ZonePath, triggers);   // ZoneTriggerSupervisor, classic only
 *
 * NOTE:
 * The gates are the r806919 per-player dynamods WC_GateCommons_ToGolemCourt
 * and WC_GateCommons_ToShoppingDist (visual, no collision); Rattlebones
 * Report still opens them too. The level check is ClassicReqMagicLevelHandler.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// The Commons exits to Golem Court and the Shopping District, gated at magic level 2 as in the 2014 client.
/// </summary>
internal static class ClassicCommonsExits {

    internal const string Commons = "WizardCity/WC_Hub";
    internal const int RequiredLevel = 2;

    /// <summary>The exit teleport triggers and the gate dynamod each opens.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Exits = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["TeleportToGolemCourt"] = "WC_GateCommons_ToGolemCourt",
        ["TeleportToShoppingDistrict"] = "WC_GateCommons_ToShoppingDist",
    };

    internal const string GateTriggerPrefix = "Classic Open ";

    /// <summary>
    /// In the Commons: the exit teleports ask for magic level 2 instead of the 2019 quest flags, and one EnterZone
    /// trigger per exit opens its gate for a wizard of level 2 or more. Other zones are left as they are. Returns the
    /// number of exit triggers changed.
    /// </summary>
    internal static int Apply(string? zonePath, List<Trigger> triggers) {
        if (!string.Equals(zonePath, Commons, StringComparison.Ordinal) || triggers is null) {
            return 0;
        }

        var changed = 0;
        foreach (var trigger in triggers) {
            if (trigger is null || !Exits.ContainsKey((string) trigger.m_triggerName ?? "")) {
                continue;
            }

            trigger.m_requirements = LevelRequirement();
            changed++;
        }

        foreach (var gate in Exits.Values) {
            var name = GateTriggerPrefix + gate;
            if (triggers.Any(t => t is not null && (string) t.m_triggerName == name)) {
                continue;
            }

            triggers.Add(new Trigger {
                m_triggerName = name,
                m_triggerMax = uint.MaxValue,
                m_activateEvents = ["StartZone"],
                m_fireEvents = ["EnterZone"],
                m_deactivateEvents = [],
                m_requirements = LevelRequirement(),
                m_results = new ResultList {
                    m_results = [new ResAddDynaMod {
                        m_dynaModClientTag = gate,
                        m_dynaModRemove = false,
                        m_useQuestAsOriginator = false,
                        m_dynaModState = "IdleOpen",
                        m_zoneName = Commons,
                    }],
                },
            });
        }

        return changed;
    }

    internal static RequirementList LevelRequirement() => new() {
        m_applyNOT = false,
        m_operator = Operator.ROP_AND,
        m_requirements = [new ReqMagicLevel {
            m_applyNOT = false,
            m_operator = Operator.ROP_AND,
            m_numericValue = RequiredLevel,
            m_operatorType = OPERATOR_TYPE.OPERATOR_GREATER_THAN_EQ,
        }],
    };

}
