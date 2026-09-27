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
 * Compares a ReqIsGender's m_playerGender with the wizard's gender.
 *
 * USAGE EXAMPLE:
 * PlayerGender.Matches(Requirement.m_playerGender, wizard.WizardAvatar.m_eGender.ToString());
 *
 * NOTE:
 * The r806919 zone data writes "MALE" and "FEMALE" (the Ravenwood dorm
 * doors and the dorm's way out); the wizard's eGender is Male, Female or
 * Neutral. Case is ignored; an empty or unknown value never matches.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;

namespace Imlight.Classic.Quests;

/// <summary>
/// The comparison of a ReqIsGender requirement.
/// </summary>
public static class PlayerGender {

    /// <summary>
    /// Whether the wizard's gender is the one the requirement names.
    /// </summary>
    /// <param name="required">The requirement's m_playerGender ("MALE", "FEMALE").</param>
    /// <param name="wizardGender">The wizard's eGender name ("Male", "Female", "Neutral").</param>
    public static bool Matches(string? required, string? wizardGender) {
        var want = Normalize(required);

        return want is not null && want == Normalize(wizardGender);
    }

    private static string? Normalize(string? gender) {
        var value = gender?.Trim();
        if (string.Equals(value, "MALE", StringComparison.OrdinalIgnoreCase)) {
            return "MALE";
        }

        if (string.Equals(value, "FEMALE", StringComparison.OrdinalIgnoreCase)) {
            return "FEMALE";
        }

        return null;
    }

}
