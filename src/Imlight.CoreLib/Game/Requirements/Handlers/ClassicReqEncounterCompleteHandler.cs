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
 * CLASSIC: the ReqEncounterComplete trigger check: the wizard has completed
 * the quest the encounter names.
 *
 * USAGE EXAMPLE:
 * Picked by RequirementDispatcher for a ClassicReqEncounterComplete.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic;

namespace Imlight.CoreLib.Game.Requirements.Handlers;

internal sealed class ClassicReqEncounterCompleteHandler : BaseRequirementHandler<ClassicReqEncounterComplete> {

    public override bool Evaluate(IRequirementContext context) {
        var wizard = context?.GetWizard();

        return wizard is not null && EncounterComplete.Met(wizard.HasCompletedQuest, Requirement?.m_encounterName);
    }

}
