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
 * REQUIREMENT SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * CLASSIC: evaluates a requirement list the way KingsIsle's data is
 * written, left to right, including lists nested inside a list.
 * 
 * USAGE EXAMPLE:
 * Reached through RequirementDispatcher.EvaluateRequirements when
 * ClassicQuestEngine.IsActive.
 * 
 * NOTE:
 * The fold itself is Imlight.Classic.Quests.RequirementFold, tested there
 * with real client trigger shapes. A nested list is evaluated against its
 * own items, so the handlers for them attach, and the outer list applies
 * its m_applyNOT and m_operator like any other item.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Generic;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Requirements;

public static partial class RequirementDispatcher {

    private static bool EvaluateLeftToRight(RequirementList requirements, IRequirementContext context)
        => RequirementFold.Evaluate(
            requirements.m_requirements ?? [],
            requirement => requirement is RequirementList nested
                ? EvaluateLeftToRight(nested, new NestedListContext(context, nested))
                : EvaluateIndividualRequirement(requirement, context),
            requirement => requirement.m_operator == Operator.ROP_OR,
            requirement => requirement.m_applyNOT);

    private sealed class NestedListContext(IRequirementContext outer, RequirementList nested) : IRequirementContext {

        public RequirementList GetFullRequirementList() => nested;
        public List<Requirement> GetRequirements() => nested.m_requirements;
        public IActorRef GetPlayerRef() => outer.GetPlayerRef();
        public CoreObject GetPlayerObj() => outer.GetPlayerObj();
        public Wizard GetWizard() => outer.GetWizard();
        public IActorRef GetZoneRef() => outer.GetZoneRef();
        public string GetQuestName() => outer.GetQuestName();
        public string GetGoalName() => outer.GetGoalName();
        public string GetTriggerName() => outer.GetTriggerName();

    }

}
