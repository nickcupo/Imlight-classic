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
 * CLASSIC MOVED OBJECTS
 * ========================================================================
 *
 * NPCs the r806919 zone data places somewhere other than where they stood
 * in 2009. The classic server does not spawn them at the later spot and
 * places them in their 2009 zone instead (ZoneObjectSupervisor):
 *
 * if (ClassicMovedObjects.MovedAway(objectInfo, zoneName)) continue;
 * foreach (var info in ClassicMovedObjects.MovedInto(zoneName)) { ... }
 *
 * Mr. Lincoln (WC-GTW-Registrar, 39088) stood in Golem Court in 2009
 * (https://wizard101.fandom.com/wiki/Mr._Lincoln?oldid=55150, 2009-12-28,
 * "Location: Golem Court"); the July 2019 new-player update moved him to
 * Ravenwood. r806919's Golem Court has no spot of his, so he takes the one of
 * Annie Shutterbug (WC-HUB-NPC14, 1452022), a later NPC the classic server
 * hides (zones/later-objects.yaml), so the spot is walkable and free.
 */

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// NPCs the classic server places in their 2009 zone instead of their later one.
/// </summary>
internal static class ClassicMovedObjects {

    /// <summary>
    /// One moved NPC: its template, the zone the later data places it in, and its 2009 zone and spot.
    /// </summary>
    internal sealed record Move(ulong TemplateId, string ZoneTag, string FromZone, string ToZone, Vector3 Location, float Yaw);

    internal static readonly ImmutableArray<Move> Moves = [
        new(39088, "WC-GTW-Registrar instance", "WizardCity/WC_Ravenwood", "WizardCity/WC_Golem_Tower",
            new Vector3(312.0146f, 424.4895f, 30.21804f), 0.8896183f),
    ];

    /// <summary>
    /// True when the classic quest engine is active and <paramref name="objectInfo"/> places a moved NPC in its later
    /// zone <paramref name="zoneName"/>.
    /// </summary>
    internal static bool MovedAway(CoreObjectInfo objectInfo, string? zoneName)
        => ClassicQuestEngine.IsActive && IsMovedAway(objectInfo.m_templateID, zoneName);

    internal static bool IsMovedAway(ulong templateId, string? zoneName)
        => Moves.Any(move => move.TemplateId == templateId && string.Equals(move.FromZone, zoneName, StringComparison.Ordinal));

    /// <summary>
    /// The placements of the NPCs moved into <paramref name="zoneName"/>; none when the classic quest engine is off.
    /// </summary>
    internal static IEnumerable<CoreObjectInfo> MovedInto(string? zoneName)
        => ClassicQuestEngine.IsActive ? PlacementsFor(zoneName) : [];

    internal static IEnumerable<CoreObjectInfo> PlacementsFor(string? zoneName)
        => Moves.Where(move => string.Equals(move.ToZone, zoneName, StringComparison.Ordinal))
            .Select(move => new CoreObjectInfo {
                m_templateID = move.TemplateId,
                m_location = move.Location,
                m_orientation = new Vector3(0, 0, move.Yaw),
                m_fScale = 1.0f,
                m_zoneTag = move.ZoneTag,
                m_startState = "",
                m_overrideName = "",
            });

}
