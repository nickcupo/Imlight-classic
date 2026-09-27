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
 * KILLED MONSTER EVENTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: decides whether a Monster_Killed zone trigger's monster check
 * names one of the monsters a duel just defeated.
 *
 * USAGE EXAMPLE:
 * KilledMonster.Matches(["Cat-Thug-Blue-MBBoss-L35.AdjRef"], "Cat-Thug-Blue-MBBoss-L35", ["Cat", "Willie_Marks"]);
 *
 * NOTE:
 * r806919 zone triggers on "Monster_Killed" (974 of them in Arc 1) guard
 * their results with a server-only requirement class the type dump does not
 * list (class hash 1826357494: m_applyNOT, m_operator, m_checkTarget,
 * m_adjectiveList). Its entries are "<template object name>.AdjRef" (the
 * monster itself, e.g. Hyde Park Tower 3's boss) or a plain adjective.
 *
 * TODO:
 * - What m_checkTarget changes is unknown; every Arc 1 use seen sets it and the entries still name the defeated monster.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Quests;

/// <summary>
/// The Monster_Killed zone event and the check its triggers make on the defeated monster.
/// </summary>
public static class KilledMonster {

    /// <summary>The zone event KingsIsle's triggers listen for after a monster dies.</summary>
    public const string EventName = "Monster_Killed";

    /// <summary>The suffix that makes an entry name a monster template (by object name) rather than an adjective.</summary>
    public const string TemplateReferenceSuffix = ".AdjRef";

    /// <summary>
    /// True when one entry names the monster: "&lt;object name&gt;.AdjRef" against its template's object name, any other
    /// entry against its adjectives. Case is ignored; an empty list names nobody.
    /// </summary>
    /// <param name="entries">The requirement's m_adjectiveList.</param>
    /// <param name="objectName">The defeated monster's template m_objectName.</param>
    /// <param name="adjectives">The defeated monster's template adjectives.</param>
    /// <returns>Whether the entries name this monster.</returns>
    public static bool Matches(IEnumerable<string?>? entries, string? objectName, IEnumerable<string?>? adjectives) {
        if (entries is null) {
            return false;
        }

        var adjectiveSet = new HashSet<string>(
            (adjectives ?? []).Where(adjective => !string.IsNullOrEmpty(adjective))!,
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries) {
            if (string.IsNullOrWhiteSpace(entry)) {
                continue;
            }

            if (entry.EndsWith(TemplateReferenceSuffix, StringComparison.OrdinalIgnoreCase)) {
                var name = entry[..^TemplateReferenceSuffix.Length];
                if (!string.IsNullOrEmpty(objectName) && name.Equals(objectName, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }

                continue;
            }

            if (adjectiveSet.Contains(entry)) {
                return true;
            }
        }

        return false;
    }

}
