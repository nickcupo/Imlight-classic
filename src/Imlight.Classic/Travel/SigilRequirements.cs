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
 * SIGIL REQUIREMENTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: which requirement list gates a dungeon sigil. A classic travel
 * entry for the sigil that carries requirements (a hand decision with 2009
 * evidence) replaces the sigil's own list from the zone's game data.
 *
 * USAGE EXAMPLE:
 * var requirements = SigilRequirements.Choose(sigil.m_requirements, travel, travelHasAny);
 *
 * NOTE:
 * The case: the 2014 MS_Plague_Zone2_RiverVillage package that live loads
 * puts two sigils on one pad (7510,9795): ToTower2FromKV asks for
 * QT-MS-PLAG2-C01-002 and ToTower2Part2 for QT-MS-PLAG2-C03-001 (Did You
 * Miss Me?), a quest Woo Ping gives inside MS_Plague2_T2_Part2 itself, so
 * Monknapping's last goal (talk to Woo Ping there) could not be reached
 * (playbot ms, finbot03, run g2-b03b). r806919's door into T2_Part2 asks
 * for Monknapping instead; the travel entry carries that.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

namespace Imlight.Classic.Travel;

/// <summary>Which requirement list gates a dungeon sigil.</summary>
public static class SigilRequirements {

    /// <summary>
    /// The travel entry's requirements when it carries any (<paramref name="travelHasAny"/>), else the sigil's own.
    /// </summary>
    /// <typeparam name="T">The requirement list type.</typeparam>
    /// <param name="own">The sigil's requirements from the zone's game data.</param>
    /// <param name="travel">The classic travel entry's requirements, or null.</param>
    /// <param name="travelHasAny">Whether <paramref name="travel"/> holds at least one requirement.</param>
    /// <returns>The list that gates the sigil.</returns>
    public static T Choose<T>(T own, T travel, bool travelHasAny) where T : class
        => travel is not null && travelHasAny ? travel : own;

}
