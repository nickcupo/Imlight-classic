using System.Collections.Generic;
using System.Linq;
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
*/

using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.WizardData.Collections;

public static class CreatureSpellbookCollection {

    private static readonly uint[] s_defaultSpellIds = [
        84361,      // Imp
        2062265892, // Thundersnake
        1496157882, // Frostbeetle
        2143810477, // Scarab
        1731857280, // Dark sprite
        1067010286, // Bloodbat
    ];

    /// <summary>
    /// Retrieves a creature spellbook by deck name.
    /// </summary>
    /// <param name="deckName">The name of the deck.</param>
    /// <returns>The creature spellbook with the specified deck name, or null if not found.</returns>
    public static CreatureSpellbook GetCreatureSpellbook(string deckName) 
        => SpiralDB.GetCreatureSpellbook(deckName);

    /// <summary>
    /// Retrieves the default creature spellbook.
    /// </summary>
    public static CreatureSpellbook GetDefaultCreatureSpellbook()
        => new("Default", s_defaultSpellIds);

    /// <summary>
    /// CLASSIC: the client's generic creature deck for a school and rank ("Mdeck-D-R4", or its "-1" twin), for a
    /// creature whose template names no deck of its own (summoned minions, Monstrology creatures). The highest rank
    /// at or below <paramref name="rank"/>, else the lowest the school has; null for an unknown school.
    /// </summary>
    public static CreatureSpellbook GetGenericCreatureSpellbook(string school, int rank)
        => GenericFor(SpiralDB.CreatureSpellbooks, school, rank);

    internal static CreatureSpellbook GenericFor(IReadOnlyDictionary<string, CreatureSpellbook> books, string school, int rank) {
        var letter = school?.ToLowerInvariant() switch {
            "fire" => "F", "ice" => "I", "storm" => "S", "myth" => "M", "life" => "L", "death" => "D", "balance" => "B",
            _ => null,
        };
        if (letter is null) {
            return null;
        }

        var pattern = new System.Text.RegularExpressions.Regex($"^Mdeck-{letter}-R(\\d+)(-1)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var candidates = books.Values
            .Select(book => (book, match: pattern.Match(book.DeckName ?? "")))
            .Where(x => x.match.Success && x.book.SpellTemplateIds.Any())
            .Select(x => (x.book, rank: int.Parse(x.match.Groups[1].Value), twin: x.match.Groups[2].Success))
            .OrderBy(x => x.rank).ThenBy(x => x.twin)
            .ToList();
        if (candidates.Count == 0) {
            return null;
        }

        var atOrBelow = candidates.Where(x => x.rank <= rank).ToList();
        return atOrBelow.Count > 0
            ? atOrBelow.Where(x => x.rank == atOrBelow.Max(y => y.rank)).First().book
            : candidates[0].book;
    }

    /// <summary>
    /// Preloads all creature spellbooks.
    /// SpiralDB loads all data at boot; this is a no-op kept for API compatibility.
    /// </summary>
    public static void PreloadSpellbooks() { }

}
