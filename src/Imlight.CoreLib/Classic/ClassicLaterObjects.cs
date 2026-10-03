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
 * Zone objects the r806919 zone data places that belong to later versions of
 * the game. The classic server does not spawn them. The list is data, loaded
 * from the file the profile rule rules.later_objects names (classic-data/zones/
 * later-objects.yaml); a profile without the rule hides nothing.
 *
 * USAGE EXAMPLE:
 * if (ClassicLaterObjects.Skips(objectInfo, zone.ZoneName)) continue;   // ZoneObjectSupervisor
 *
 * NOTE:
 * The first entry was the 2019 Private O'Ryan (1451483, WC-ST01-NPC05-B)
 * beside Private Connelly: the client's quest helper pointed Saving Private
 * O'Ryan's arrow at it instead of at O'Ryan's house door (playtest 2026-09-28
 * #12). The classic O'Ryan (WC-ST01-NPC05) is inside the house, WC_Unicorn_H1.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/30/2026
 */

using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// Zone objects from later versions of the game, which the classic server does not spawn.
/// </summary>
internal static class ClassicLaterObjects {

    /// <summary>
    /// True when the classic quest engine is active and <paramref name="objectInfo"/> places a later object in <paramref name="zoneName"/>.
    /// </summary>
    internal static bool Skips(CoreObjectInfo objectInfo, string? zoneName)
        => ClassicQuestEngine.IsActive && IsLater(objectInfo.m_templateID, zoneName);

    /// <summary>
    /// True when <paramref name="templateId"/> is a later object in <paramref name="zoneName"/>.
    /// </summary>
    internal static bool IsLater(ulong templateId, string? zoneName)
        => ClassicProgression.LaterObjects.Hides(templateId, zoneName);

}
