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
 * AMBIENT WALK
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): how an ambient wizard's walk reaches the official
 * client. The client draws another mobile's MSG_SERVERMOVE by running it
 * to that spot at the mobile's own speed and standing it there (the same
 * way a creature walks from one MSG_SERVERMOVE per path node). A player's
 * speed comes from PlayerObject's PathMovementBehavior: 600 units a second
 * (m_movementSpeed 600, m_movementScale 1, in the r806919 Root.wad).
 *   - Before: a step of 230 x 0.3 = 69 units every 300 ms. The client ran
 *     each step in 115 ms and stood for 185 ms: a wizard that looks like
 *     someone tapping the forward key.
 *   - Now: one MSG_SERVERMOVE per straight leg of the route (to its far
 *     corner), the next one when the wizard gets there at 600 units a
 *     second; the client runs the whole leg without a stop.
 * A leg is a straight run from one point to the next; the server keeps
 * the wizard's spot along it (for aggro, help offers, late spawns).
 *
 * USAGE EXAMPLE:
 * var leg = WalkLeg.Begin(from, corner, now, AmbientPace.RunSpeed);
 * var here = leg.At(now + TimeSpan.FromSeconds(0.5));
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Numerics;

namespace Imlight.Classic.Ambient;

/// <summary>How fast an ambient wizard runs (see the file header).</summary>
public static class AmbientPace {

    /// <summary>PlayerObject's PathMovementBehavior in the r806919 data: m_movementSpeed 600 x m_movementScale 1.</summary>
    public const float ClientRunSpeed = 600f;

    /// <summary>The run speed from the player template's movement speed and scale, or <see cref="ClientRunSpeed"/>.</summary>
    public static float FromTemplate(float? movementSpeed, float? movementScale) {
        var scale = movementScale is { } s && s > 0f ? s : 1f;
        var speed = (movementSpeed ?? 0f) * scale;
        return float.IsFinite(speed) && speed is >= 50f and <= 5000f ? speed : ClientRunSpeed;
    }

}

/// <summary>One straight run of a walk: from <see cref="From"/> to <see cref="To"/>, starting at <see cref="Start"/>.</summary>
public readonly record struct WalkLeg(Vector3 From, Vector3 To, DateTime Start, double Seconds) {

    /// <summary>A leg from <paramref name="from"/> to <paramref name="to"/> at <paramref name="speed"/> units a second (ground distance).</summary>
    public static WalkLeg Begin(Vector3 from, Vector3 to, DateTime now, float speed)
        => new(from, to, now, GroundDistance(from, to) / Math.Max(1f, speed));

    /// <summary>When the wizard gets to <see cref="To"/>.</summary>
    public DateTime End => Start + TimeSpan.FromSeconds(Seconds);

    /// <summary>The heading of the run (radians, counter-clockwise from +X).</summary>
    public float Heading => MathF.Atan2(To.Y - From.Y, To.X - From.X);

    /// <summary>Where the wizard is at <paramref name="now"/>: on the line, at the start before it and at the end after it.</summary>
    public Vector3 At(DateTime now) {
        if (Seconds <= 0) {
            return To;
        }

        var t = (float) Math.Clamp((now - Start).TotalSeconds / Seconds, 0, 1);
        return Vector3.Lerp(From, To, t);
    }

    public static float GroundDistance(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

}
