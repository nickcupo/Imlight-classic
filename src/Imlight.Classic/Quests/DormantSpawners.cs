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
 * Picks the zone spawners to start for a wizard's open quest goals: the
 * inactive spawners that nothing in the client's zone data ever starts,
 * whose NPC or mob one of the wizard's active goals needs.
 *
 * USAGE EXAMPLE:
 * var dormant = DormantSpawners.Find(spawners, startedIds);      // zone load
 * var start = DormantSpawners.Plan(dormant, isNeededTemplate);   // player enters the zone
 *
 * NOTE:
 * KingsIsle's server started these itself (a quest's server-side results);
 * the client data only marks them inactive. Dragonspyre's Grand Chasm
 * (Past) keeps its Helephant boss, which DS-LIB3-C02-005 asks for, on two
 * such spawners (805549, 805551); Windhammer's tower (DS_Hatchery_T1) keeps
 * the drake DS-NEC1-C01-004/006 talk to on one (812068). A spawner a
 * trigger's ResSpawn or a known object use starts is left alone, so
 * scripted spawns keep their timing, and so is a template the zone already
 * places (statically or on an active spawner): dormant copies of street
 * mobs in towers stay reinforcements. One spawner per needed template is
 * picked, the first in wad order, so a boss kept on two alternative
 * spawners appears once; the spawner's own m_maxNumberOfSpawns still caps
 * repeats.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Quests;

/// <summary>
/// A zone spawner as the dormant-spawner decision sees it.
/// </summary>
/// <param name="Id">The spawner's m_id.</param>
/// <param name="Active">The spawner's m_active.</param>
/// <param name="TemplateIds">The templates of its spawn list.</param>
public sealed record SpawnerInfo(uint Id, bool Active, IReadOnlyList<uint> TemplateIds);

/// <summary>
/// The inactive spawners that only a quest starts.
/// </summary>
public static class DormantSpawners {

    /// <summary>
    /// The spawners that are inactive and that no trigger or known object use starts, in wad order.
    /// </summary>
    /// <param name="spawners">The zone's spawners in wad order.</param>
    /// <param name="startedIds">The spawner ids trigger results and object uses start.</param>
    public static IReadOnlyList<SpawnerInfo> Find(IEnumerable<SpawnerInfo> spawners, IEnumerable<uint> startedIds) {
        var started = startedIds.ToHashSet();

        return spawners.Where(spawner => !spawner.Active && !started.Contains(spawner.Id)).ToList();
    }

    /// <summary>
    /// The templates the zone places without a quest: its static objects and its active spawners.
    /// </summary>
    /// <param name="spawners">The zone's spawners.</param>
    /// <param name="staticTemplateIds">The templates of the zone's static objects.</param>
    public static IReadOnlySet<uint> Placed(IEnumerable<SpawnerInfo> spawners, IEnumerable<uint> staticTemplateIds)
        => spawners.Where(spawner => spawner.Active)
            .SelectMany(spawner => spawner.TemplateIds)
            .Concat(staticTemplateIds)
            .ToHashSet();

    /// <summary>
    /// The dormant spawners to start: for each template a goal needs that the zone does not already
    /// place, the first dormant spawner that spawns it.
    /// </summary>
    /// <param name="dormant">The dormant spawners in wad order.</param>
    /// <param name="isNeeded">Whether one of the wizard's active goals needs the template.</param>
    /// <param name="placed">Templates the zone places anyway (<see cref="Placed"/>); never started here.</param>
    public static IReadOnlyList<uint> Plan(IEnumerable<SpawnerInfo> dormant, Func<uint, bool> isNeeded,
                                           IReadOnlySet<uint>? placed = null) {
        var planned = new List<uint>();
        var covered = new HashSet<uint>(placed ?? new HashSet<uint>());
        var answers = new Dictionary<uint, bool>();
        foreach (var spawner in dormant) {
            foreach (var templateId in spawner.TemplateIds.Distinct()) {
                if (covered.Contains(templateId)) {
                    continue;
                }

                if (!answers.TryGetValue(templateId, out var needed)) {
                    needed = isNeeded(templateId);
                    answers[templateId] = needed;
                }

                if (!needed) {
                    continue;
                }

                covered.Add(templateId);
                if (!planned.Contains(spawner.Id)) {
                    planned.Add(spawner.Id);
                }
            }
        }

        return planned;
    }

}
