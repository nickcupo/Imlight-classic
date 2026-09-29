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
 * CLASSIC QUEST OFFER ORDER
 * ========================================================================
 *
 * PURPOSE:
 * The order in which an NPC that gives several quests offers them: main-story
 * (m_mainline) quests first, then by title key as before.
 *
 * USAGE EXAMPLE:
 * quests.Sort(QuestOfferOrder.For<QuestTemplate>(q => q.m_mainline, q => q.m_questTitle));
 *
 * NOTE:
 * The offer service shows one quest at a time (the first available one). Sorted
 * by title key alone, a side quest with a lower key (the classic side quests use
 * QuestTitle_14xxx, the Marleybone story QuestTitle_9Bxx) came first, so a player
 * who declined it was offered the same side quest again and never saw the story
 * quest until the side quest was taken (Mayor Pimsbury, playbot-mb run r02).
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/29/2026
 */

using System;
using System.Collections.Generic;

namespace Imlight.Classic.Quests;

/// <summary>
/// The order in which an NPC offers its quests.
/// </summary>
public static class QuestOfferOrder {

    /// <summary>
    /// Main-story quests first, then ordinal by title key.
    /// </summary>
    /// <param name="mainline">Whether a quest is main story.</param>
    /// <param name="titleKey">A quest's title key.</param>
    /// <typeparam name="T">The quest type.</typeparam>
    /// <returns>A comparer for <see cref="List{T}.Sort(IComparer{T})"/>.</returns>
    public static IComparer<T> For<T>(Func<T, bool> mainline, Func<T, string?> titleKey) {
        ArgumentNullException.ThrowIfNull(mainline);
        ArgumentNullException.ThrowIfNull(titleKey);

        return Comparer<T>.Create((a, b) => {
            var byMain = mainline(b).CompareTo(mainline(a));
            return byMain != 0 ? byMain : string.CompareOrdinal(titleKey(a), titleKey(b));
        });
    }

}
