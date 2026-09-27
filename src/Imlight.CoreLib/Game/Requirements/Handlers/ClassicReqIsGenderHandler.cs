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
 * CLASSIC: compares the wizard's gender with a ReqIsGender's
 * m_playerGender, for the Ravenwood dorm doors (Tower 1 boys, Tower 2
 * girls) and the dorm's way out.
 *
 * USAGE EXAMPLE:
 * Picked by RequirementDispatcher for any ReqIsGender in a requirement list.
 *
 * NOTE:
 * Off unless ClassicQuestEngine.IsActive: stock Imlight has no handler for
 * ReqIsGender and reads it as unmet. The comparison is
 * Imlight.Classic.Quests.PlayerGender; the wizard's gender is the avatar's
 * m_eGender chosen at creation.
 *
 * TODO:
 * - Once upstream ships its own ReqIsGender handler, is this one still needed? Both would register for the type.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic;

namespace Imlight.CoreLib.Game.Requirements.Handlers;

internal sealed class ClassicReqIsGenderHandler : BaseRequirementHandler<ReqIsGender> {

    public override bool Evaluate(IRequirementContext context) {
        if (!ClassicQuestEngine.IsActive) {
            return false;
        }

        var gender = context.GetWizard()?.WizardAvatar?.m_eGender;
        if (gender is null) {
            return false;
        }

        return PlayerGender.Matches(Requirement.m_playerGender, gender.Value.ToString());
    }

}
