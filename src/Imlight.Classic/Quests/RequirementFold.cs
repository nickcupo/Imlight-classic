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
 * Reads a requirement list the way KingsIsle's data is written: left to
 * right, each item's operator joining it to the next item.
 * 
 * USAGE EXAMPLE:
 * RequirementFold.Evaluate(list.m_requirements, Evaluate, r => r.m_operator == Operator.ROP_OR, r => r.m_applyNOT);
 * 
 * NOTE:
 * Client triggers write "any of these" as OR on every item but the last, whose
 * operator is AND (the Commons gate to Unicorn Way, the Commons exits, the
 * seven MB-SPELL school quests). Stock Imlight ANDs that last item with the OR
 * group instead. Lists that are all AND or all OR read the same both ways.
 * 
 * TODO:
 * - Does KingsIsle's reader give AND precedence over OR? Every r806919 trigger and spawn list reads the same either way.
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;

namespace Imlight.Classic.Quests;

/// <summary>
/// The left-to-right requirement list reading.
/// </summary>
public static class RequirementFold {

    /// <summary>
    /// Folds <paramref name="items"/> left to right: item i's operator joins it to item i + 1, so the last
    /// item's operator is unused, and <paramref name="negated"/> inverts one item. Null items are skipped
    /// and an empty list passes. An item is not evaluated when the result so far already decides its join.
    /// </summary>
    /// <param name="items">The list's items in data order.</param>
    /// <param name="evaluate">Evaluates one item, before negation.</param>
    /// <param name="joinsNextWithOr">True when the item's operator is OR.</param>
    /// <param name="negated">True when the item's result is inverted.</param>
    /// <typeparam name="T">The requirement type.</typeparam>
    /// <returns>The list's result.</returns>
    public static bool Evaluate<T>(IEnumerable<T?> items,
                                   Func<T, bool> evaluate,
                                   Func<T, bool> joinsNextWithOr,
                                   Func<T, bool> negated) where T : class {
        ArgumentNullException.ThrowIfNull(items);

        bool? result = null;
        var joinWithOr = false;
        foreach (var item in items) {
            if (item is null) {
                continue;
            }

            result = result switch {
                null => Apply(item),
                true when joinWithOr => true,
                false when !joinWithOr => false,
                _ => Apply(item),
            };
            joinWithOr = joinsNextWithOr(item);
        }

        return result ?? true;

        bool Apply(T item)
            => evaluate(item) != negated(item);
    }

}
