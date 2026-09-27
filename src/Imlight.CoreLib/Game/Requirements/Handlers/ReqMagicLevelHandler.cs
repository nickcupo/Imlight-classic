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
 * CLASSIC: compares the wizard's level with a ReqMagicLevel's value, for
 * quests such as Enrollment (level 2) and Olde News (level 5).
 * 
 * USAGE EXAMPLE:
 * Picked by RequirementDispatcher for any ReqMagicLevel in a requirement list.
 * 
 * NOTE:
 * Off unless ClassicQuestEngine.IsActive: stock Imlight has no handler for
 * ReqMagicLevel and reads it as unmet. The client's zone triggers use it with
 * an empty m_magicSchool (Gamma's teleport tips, level 15 or lower).
 * 
 * TODO:
 * - Does a non-empty m_magicSchool ask for the level in that school only?
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;

namespace Imlight.CoreLib.Game.Requirements.Handlers;

internal sealed class ReqMagicLevelHandler : BaseRequirementHandler<ReqMagicLevel> {

    public override bool Evaluate(IRequirementContext context) {
        if (!ClassicQuestEngine.IsActive) {
            return false;
        }

        var level = context.GetWizard()?.MagicSchoolBehavior?.Level;
        if (level is null) {
            return false;
        }

        float actual = level.Value;
        var required = Requirement.m_numericValue;

        return Requirement.m_operatorType switch {
            OPERATOR_TYPE.OPERATOR_EQUALS => actual == required,
            OPERATOR_TYPE.OPERATOR_GREATER_THAN => actual > required,
            OPERATOR_TYPE.OPERATOR_LESS_THAN => actual < required,
            OPERATOR_TYPE.OPERATOR_GREATER_THAN_EQ => actual >= required,
            OPERATOR_TYPE.OPERATOR_LESS_THAN_EQ => actual <= required,
            _ => false
        };
    }

}
