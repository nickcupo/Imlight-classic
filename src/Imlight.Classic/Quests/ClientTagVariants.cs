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
 * Says whether a goal's client tag names a world object: its m_objectName
 * as it is, or behind the "Ddl_" prefix SpiralDB's captured usage goals
 * put in front of it.
 *
 * USAGE EXAMPLE:
 * ClientTagVariants.NamesObject(goal.m_clientTags, template.m_objectName); // InteractQuestSelectComponent
 *
 * NOTE:
 * WC-CYCLOPS-MAIN-003's three bubble goals are tagged
 * Ddl_WC_DarkCave_Bubble1..3 while the Dark Cave places WC_DarkCave_Bubble1..3
 * (r806919 ObjectData and WC_DarkCave gamedata). The prefix is matched in
 * any case (Ddl_, DDL_); the object name itself is matched exactly, as
 * upstream matches it.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;

namespace Imlight.Classic.Quests;

/// <summary>
/// The spellings of an object name a goal's client tag may carry.
/// </summary>
public static class ClientTagVariants {

    private const string DdlPrefix = "Ddl_";

    /// <summary>
    /// The object name a client tag names: the tag without a "Ddl_" prefix, or the tag itself.
    /// </summary>
    /// <param name="tag">A goal client tag.</param>
    public static string? ObjectNameOf(string? tag) {
        if (tag is null) {
            return null;
        }

        return tag.Length > DdlPrefix.Length && tag.StartsWith(DdlPrefix, StringComparison.OrdinalIgnoreCase)
            ? tag[DdlPrefix.Length..]
            : tag;
    }

    /// <summary>
    /// Whether one of the tags names the object, as it is or behind a "Ddl_" prefix.
    /// </summary>
    /// <param name="tags">The goal's client tags.</param>
    /// <param name="objectName">The object's m_objectName.</param>
    public static bool NamesObject(IEnumerable<string?>? tags, string? objectName) {
        if (tags is null || string.IsNullOrEmpty(objectName)) {
            return false;
        }

        foreach (var tag in tags) {
            if (string.Equals(tag, objectName, StringComparison.Ordinal)
                || string.Equals(ObjectNameOf(tag), objectName, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

}
