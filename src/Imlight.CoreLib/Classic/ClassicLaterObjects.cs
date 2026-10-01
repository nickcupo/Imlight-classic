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
 * CLASSIC LATER OBJECTS
 * ========================================================================
 *
 * PURPOSE:
 * Zone objects the r806919 zone data places that belong to later versions
 * of classic quests. The classic server does not spawn them.
 *
 * USAGE EXAMPLE:
 * if (ClassicLaterObjects.Skips(objectInfo)) continue;   // ZoneObjectSupervisor
 *
 * NOTE:
 * WC-ST01-NPC05-B (1451483) is the 2019 Unicorn Way Private O'Ryan, an
 * undetectable NPC standing next to Private Connelly. The client's quest
 * helper pointed Saving Private O'Ryan's arrow at it instead of at
 * O'Ryan's house door (playtest 2026-09-28 #12). The classic O'Ryan
 * (WC-ST01-NPC05) is inside the house, WC_Unicorn_H1.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/30/2026
 */

using System.Collections.Frozen;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// Zone objects from later versions of classic quests, which the classic server does not spawn.
/// </summary>
internal static class ClassicLaterObjects {

    /// <summary>
    /// Template ids of the later objects.
    /// </summary>
    internal static readonly FrozenSet<ulong> s_templates = FrozenSet.Create<ulong>(
        1451483 // WC-ST01-NPC05-B: the 2019 Private O'Ryan on Unicorn Way.
    );

    /// <summary>
    /// True when the classic quest engine is active and <paramref name="objectInfo"/> places a later object.
    /// </summary>
    internal static bool Skips(CoreObjectInfo objectInfo)
        => ClassicQuestEngine.IsActive && IsLater(objectInfo.m_templateID);

    /// <summary>
    /// True when <paramref name="templateId"/> is a later object.
    /// </summary>
    internal static bool IsLater(ulong templateId) => s_templates.Contains(templateId);

}
