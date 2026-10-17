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
 * STREET MANNERS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): where an ambient wizard stands and how long it
 * stays, so a street of them does not look like a script:
 *   - Doorways: a zone's named locations are mostly where wizards arrive
 *     through a door or a gate. A wizard that wanders "to" one stands a
 *     few steps off it (StandOffDistance), never on it, so it is not in
 *     the doorway when a player comes through.
 *   - Personal space: no two wizards pick spots within PersonalSpace of
 *     each other (at the same NPC they stand side by side, not inside one
 *     another).
 *   - After a duel: a real player lingers a few seconds (looks around,
 *     maybe a "gg"), then walks on; the owner saw ambient wizards "just
 *     stand in the street after combat". AfterDuelPause is that moment;
 *     the next choice after it is always a walk.
 *   - Idle: standing at a shop or a spot, a wizard turns a little now and
 *     then (IdleTurn), at uneven times, instead of freezing.
 *
 * USAGE EXAMPLE:
 * var spot = StreetManners.StandOff(location, angle, StreetManners.StandOffDistance);
 * if (!StreetManners.Crowded(spot, others)) { ... }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Imlight.Classic.Ambient;

/// <summary>Spot and timing rules for ambient wizards in the street (see the file header).</summary>
public static class StreetManners {

    /// <summary>No two wizards stand closer than this (a player's body is about 60 across).</summary>
    public const float PersonalSpace = 130f;

    /// <summary>How far off a doorway (a named arrival location) a wizard stands.</summary>
    public const float StandOffDistance = 260f;

    /// <summary>Closer than this to a doorway is in the way.</summary>
    public const float DoorwayClearance = 180f;

    /// <summary>How long a wizard lingers where its duel was, 3 to 8 seconds, by <paramref name="seed"/>.</summary>
    public static TimeSpan AfterDuelPause(int seed) => TimeSpan.FromSeconds(3 + (uint) seed % 6);

    /// <summary>The longest a duel may keep a wizard before it is taken for over (a lost message, a closed duel).</summary>
    public static readonly TimeSpan LongestDuel = TimeSpan.FromMinutes(20);

    /// <summary>
    /// A spot <paramref name="distance"/> from <paramref name="location"/> at <paramref name="angle"/> (radians): where
    /// a wizard stands instead of on a doorway.
    /// </summary>
    public static Vector2 StandOff(Vector2 location, double angle, float distance)
        => location + new Vector2((float) Math.Cos(angle), (float) Math.Sin(angle)) * distance;

    /// <summary>True when <paramref name="spot"/> is within <see cref="PersonalSpace"/> of any of <paramref name="others"/>.</summary>
    public static bool Crowded(Vector2 spot, IEnumerable<Vector2> others) {
        foreach (var other in others) {
            if (Vector2.Distance(spot, other) < PersonalSpace) {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="spot"/> is in a doorway (within <see cref="DoorwayClearance"/> of one).</summary>
    public static bool InDoorway(Vector2 spot, IEnumerable<Vector2> doorways) {
        foreach (var door in doorways) {
            if (Vector2.Distance(spot, door) < DoorwayClearance) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Where the <paramref name="index"/>-th wizard at an NPC stands: a step to the side of the spot in front of it,
    /// alternating left and right, so two shoppers stand side by side.
    /// </summary>
    public static Vector2 BesideNpc(Vector2 front, float facing, int index) {
        if (index <= 0) {
            return front;
        }

        var side = (index % 2 == 1 ? 1 : -1) * ((index + 1) / 2) * PersonalSpace;
        var across = new Vector2(-MathF.Sin(facing), MathF.Cos(facing));
        return front + across * side;
    }

    /// <summary>
    /// CLASSIC (2026-10-17): a duel circle's radius when its sigil names none. Unverified estimate; the sigil's own
    /// m_radius is used when the duel reports it.
    /// </summary>
    public const float DefaultDuelCircleRadius = 300f;

    /// <summary>Closer than this to a creature path node is on the creatures' path (where street duels open).</summary>
    public const float PathClearance = 160f;

    /// <summary>
    /// CLASSIC (2026-10-17): true when <paramref name="spot"/> is inside a duel circle or within <see cref="PersonalSpace"/>
    /// of its edge (owner: wizards "just stand in the middle of duel circles"). A radius of 0 is the default radius.
    /// </summary>
    public static bool InDuelCircle(Vector2 spot, IEnumerable<(Vector2 Center, float Radius)> circles) {
        foreach (var (center, radius) in circles) {
            if (Vector2.Distance(spot, center) < CircleReach(radius)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The nearest spot just outside the circle, straight out from its middle through <paramref name="from"/>.</summary>
    public static Vector2 OutOfCircle(Vector2 from, Vector2 center, float radius) {
        var away = from - center;
        if (away.LengthSquared() < 1f) {
            away = Vector2.UnitX;
        }

        return center + Vector2.Normalize(away) * (CircleReach(radius) + PersonalSpace);
    }

    private static float CircleReach(float radius) => (radius > 0 ? radius : DefaultDuelCircleRadius) + PersonalSpace;

    /// <summary>True when <paramref name="spot"/> is on the creatures' path (within <see cref="PathClearance"/> of a node).</summary>
    public static bool OnPath(Vector2 spot, IEnumerable<Vector2> pathNodes) {
        foreach (var node in pathNodes) {
            if (Vector2.Distance(spot, node) < PathClearance) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// CLASSIC (2026-10-17): a stroll's stop on the creatures' path is a moment, 1 to 3 seconds by <paramref name="roll"/>
    /// in [0, 1): a player walks along a street; it does not stand where the mobs walk and the duels open.
    /// </summary>
    public static TimeSpan PathPause(double roll) => TimeSpan.FromSeconds(1 + Math.Clamp(roll, 0, 1) * 2);

    /// <summary>A hunter's wait on the path before it walks on to another node: 3 to 8 seconds by <paramref name="roll"/>.</summary>
    public static TimeSpan HuntPause(double roll) => TimeSpan.FromSeconds(3 + Math.Clamp(roll, 0, 1) * 5);

    /// <summary>
    /// A small turn while standing: -0.6 to 0.6 radians, or none, from a roll in [0, 1). About one stand in three
    /// stays still.
    /// </summary>
    public static float IdleTurn(double roll) => roll < 0.33 ? 0f : (float) ((roll - 0.665) / 0.335 * 0.6);

}
