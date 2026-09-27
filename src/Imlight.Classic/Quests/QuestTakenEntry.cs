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
 * Reads KingsIsle's "QT-<quest>" registry entries, which the client's
 * doors, sigils and interactables check to mean "has taken the quest".
 *
 * USAGE EXAMPLE:
 * if (QuestTakenEntry.QuestNameOf(entryName) is { } quest) { ... has the quest active or done ... }
 *
 * NOTE:
 * Nothing in SpiralDB writes these entries; KingsIsle's server set them
 * when a quest was taken. Dragonspyre's Windhammer crystal stand summons
 * the drake while QT-DS-NEC1-C01-004 is set and DS-NEC1-C01-004's own
 * goal to use the stand is still open, so the entry stands for an active
 * quest too, not only a finished one. Only the bare "QT-" plus a quest
 * name counts: suffixed flags such as QT-KT-PYM3-C02-003-OK are the
 * quest's own entries and are left to the registry.
 *
 * TODO:
 * - Does KingsIsle clear a QT- entry when the quest is abandoned?
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;

namespace Imlight.Classic.Quests;

/// <summary>
/// KingsIsle's "QT-&lt;quest&gt;" registry entries.
/// </summary>
public static class QuestTakenEntry {

    /// <summary>
    /// The prefix KingsIsle's quest-taken entries carry.
    /// </summary>
    public const string Prefix = "QT-";

    /// <summary>
    /// The quest name a "QT-&lt;quest&gt;" entry names, or null for any other entry.
    /// </summary>
    /// <param name="entryName">A registry entry name.</param>
    public static string? QuestNameOf(string? entryName) {
        if (entryName is null || !entryName.StartsWith(Prefix, StringComparison.Ordinal)
            || entryName.Length == Prefix.Length) {
            return null;
        }

        return entryName[Prefix.Length..];
    }

}
