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
 * CLASSIC ZONE TRANSFER MERGE
 * ========================================================================
 *
 * PURPOSE:
 * The merge rule of SpiralDB ZoneTransfer overlay records marked
 * "Merge": true. Such a record adds entries to the zone's loaded record by
 * trigger name instead of replacing the whole record, so generated travel
 * data can sit beside SpiralDB's own entries without copying them.
 *
 * USAGE EXAMPLE:
 * zone.Teleports = ZoneTransferMerge.Apply(loaded.Teleports, merge.Teleports, t => t.TriggerName, t => t.Teleport is null);
 *
 * NOTE:
 * An entry replaces the loaded entry of the same trigger name (names compare
 * exactly, as the trigger lookup does) or is appended; an entry marked as a
 * removal drops the loaded entry. Within one overlay root the server applies
 * the full records first and the merge records after them, in file-name
 * order, so the result does not depend on directory order.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;

namespace Imlight.Classic.Travel;

/// <summary>
/// Merges ZoneTransfer entries by trigger name.
/// </summary>
public static class ZoneTransferMerge {

    /// <summary>
    /// The loaded entries with <paramref name="additions"/> applied in order.
    /// </summary>
    /// <param name="loaded">The zone's entries so far; null means none. Not modified.</param>
    /// <param name="additions">The merge record's entries.</param>
    /// <param name="name">An entry's trigger name.</param>
    /// <param name="isRemoval">True for an entry that removes the loaded entry of its name.</param>
    /// <typeparam name="T">The entry type.</typeparam>
    /// <returns>A new list: loaded order kept, replacements in place, new names appended.</returns>
    public static List<T> Apply<T>(IEnumerable<T>? loaded, IEnumerable<T>? additions, Func<T, string?> name,
                                   Func<T, bool> isRemoval) {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(isRemoval);

        var result = new List<T>(loaded ?? []);
        foreach (var entry in additions ?? []) {
            if (entry is null || name(entry) is not { Length: > 0 } key) {
                continue;
            }

            var index = result.FindIndex(existing => existing is not null && string.Equals(name(existing), key, StringComparison.Ordinal));
            if (isRemoval(entry)) {
                if (index >= 0) {
                    result.RemoveAt(index);
                }
            }
            else if (index >= 0) {
                result[index] = entry;
            }
            else {
                result.Add(entry);
            }
        }

        return result;
    }

}
