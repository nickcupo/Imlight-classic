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
 * The switch the quest engine hooks read: on a restricted profile, quest
 * and zone data is read the way KingsIsle wrote it.
 * 
 * USAGE EXAMPLE:
 * if (ClassicQuestEngine.IsActive) { return EvaluateLeftToRight(requirements, context); }
 * 
 * NOTE:
 * Off before ClassicRuntime is initialized and on dev-unrestricted, so those
 * run stock Imlight. See ClassicRules.UsesKingsIsleQuestRules for the list.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

#nullable enable

using Imlight.Classic;

namespace Imlight.CoreLib.Classic;

internal static class ClassicQuestEngine {

    /// <summary>
    /// True when the active profile reads quest and zone data the KingsIsle way.
    /// </summary>
    internal static bool IsActive
        => ClassicRuntime.IsInitialized && ClassicRuntime.Rules.UsesKingsIsleQuestRules;

}
