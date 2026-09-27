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
 * Compares a player's number with a requirement's m_numericValue under the
 * requirement's OPERATOR_TYPE, as ReqMagicLevel does.
 *
 * USAGE EXAMPLE:
 * NumericRequirement.Meets(level, Requirement.m_operatorType.ToString(), Requirement.m_numericValue);
 *
 * NOTE:
 * The operator is KingsIsle's enum name (OPERATOR_GREATER_THAN_EQ and so on).
 * Enrollment and Olde News use OPERATOR_GREATER_THAN_EQ; Gamma's teleport
 * tips use OPERATOR_LESS_THAN_EQ.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

namespace Imlight.Classic.Quests;

/// <summary>
/// The comparison of a numeric requirement.
/// </summary>
public static class NumericRequirement {

    /// <summary>
    /// True when <paramref name="actual"/> stands in the named relation to <paramref name="required"/>;
    /// an unknown operator is never met.
    /// </summary>
    /// <param name="actual">The player's value, such as the wizard's level.</param>
    /// <param name="operatorType">The OPERATOR_TYPE name, such as OPERATOR_GREATER_THAN_EQ.</param>
    /// <param name="required">The requirement's m_numericValue.</param>
    /// <returns>Whether the requirement is met.</returns>
    public static bool Meets(float actual, string? operatorType, float required)
        => operatorType switch {
            "OPERATOR_EQUALS" => actual == required,
            "OPERATOR_GREATER_THAN" => actual > required,
            "OPERATOR_LESS_THAN" => actual < required,
            "OPERATOR_GREATER_THAN_EQ" => actual >= required,
            "OPERATOR_LESS_THAN_EQ" => actual <= required,
            _ => false,
        };

}
