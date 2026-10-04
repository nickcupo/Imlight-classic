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
 * AMBIENT WALK TESTS
 * ========================================================================
 *
 * PURPOSE:
 * An ambient wizard's walk as the official client draws it: the client
 * runs a mobile to each MSG_SERVERMOVE at a player's speed (600) and
 * stands it there. One move per straight leg, sent when the wizard gets
 * to the leg's start, keeps it running; the old 69-unit step every
 * 300 ms (230 units a second) left it standing most of the time.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter AmbientWalkTests
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Imlight.Classic.Ambient;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class AmbientWalkTests {

    private static readonly DateTime s_t0 = new(2026, 10, 4, 20, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The official client's remote mobile, drawn from <paramref name="start"/>: it runs at <paramref name="speed"/> toward
    /// the last move it got and stands
    /// when it is there. Returns the seconds it stood between the first and the last move, and how many stops (over 30
    /// ms) that was.
    /// </summary>
    private static (double Standing, int Stops) ClientStands(Vector3 start, IReadOnlyList<(double At, Vector3 To)> moves, float speed) {
        var standing = 0.0;
        var stops = 0;
        var here = start;
        for (var i = 1; i < moves.Count; i++) {
            var gap = moves[i].At - moves[i - 1].At;
            var target = moves[i - 1].To;
            var need = WalkLeg.GroundDistance(here, target) / speed;
            if (need >= gap) {
                here = Vector3.Lerp(here, target, (float) (gap / need));
                continue;
            }

            here = target;
            standing += gap - need;
            stops += gap - need > 0.03 ? 1 : 0;
        }

        return (standing, stops);
    }

    private static readonly Vector3[] s_route = [
        new(0, 0, 0), new(900, 0, 0), new(900, 1400, 10), new(300, 1900, 10), new(300, 2600, 0),
    ];

    [Theory]
    [InlineData(600f, 1f, 600f)]
    [InlineData(300f, 2f, 600f)]
    [InlineData(600f, 0f, 600f)]
    [InlineData(null, null, 600f)]
    [InlineData(0f, 1f, 600f)]
    [InlineData(float.NaN, 1f, 600f)]
    [InlineData(450f, 1f, 450f)]
    public void RunSpeedComesFromThePlayerTemplate(float? speed, float? scale, float expected)
        => Assert.Equal(expected, AmbientPace.FromTemplate(speed, scale));

    [Fact]
    public void ALegTakesItsLengthOverTheSpeedAndStaysOnItsLine() {
        var leg = WalkLeg.Begin(new Vector3(0, 0, 0), new Vector3(600, 800, 40), s_t0, 500f);

        Assert.Equal(2.0, leg.Seconds, 6);
        Assert.Equal(s_t0.AddSeconds(2), leg.End);
        Assert.Equal(new Vector3(300, 400, 20), leg.At(s_t0.AddSeconds(1)));
        Assert.Equal(leg.From, leg.At(s_t0.AddSeconds(-1)));
        Assert.Equal(leg.To, leg.At(s_t0.AddSeconds(9)));
        Assert.Equal(MathF.Atan2(800, 600), leg.Heading, 5);
    }

    [Fact]
    public void OneMovePerLegKeepsTheClientRunningToTheEnd() {
        // What AmbientZone sends: each leg's far corner when the wizard starts it, at the client's own run speed.
        var moves = new List<(double, Vector3)>();
        var at = s_t0;
        for (var i = 1; i < s_route.Length; i++) {
            var leg = WalkLeg.Begin(s_route[i - 1], s_route[i], at, AmbientPace.ClientRunSpeed);
            moves.Add(((at - s_t0).TotalSeconds, leg.To));
            at = leg.End;
        }

        moves.Add(((at - s_t0).TotalSeconds, s_route[^1])); // the stop at the end
        var (standing, stops) = ClientStands(s_route[0], moves, AmbientPace.ClientRunSpeed);

        Assert.Equal(0, stops);
        Assert.InRange(standing, 0, 0.001);
        Assert.Equal(5, moves.Count); // 4 legs and the stop, not a move every 300 ms
    }

    [Fact]
    public void TheOldStepEvery300MsLeftTheClientStandingMostOfTheTime() {
        // Before 2026-10-04: a 230 x 0.3 = 69-unit step every 300 ms (the owner: "like someone is tapping the forward key").
        const float oldSpeed = 230f;
        const double tick = 0.3;
        var moves = new List<(double, Vector3)> { (0, s_route[0]) };
        var here = s_route[0];
        var next = 1;
        var t = 0.0;
        while (next < s_route.Length) {
            var budget = oldSpeed * (float) tick;
            while (budget > 0 && next < s_route.Length) {
                var d = WalkLeg.GroundDistance(here, s_route[next]);
                if (d > budget) {
                    here = Vector3.Lerp(here, s_route[next], budget / d);
                    budget = 0;
                }
                else {
                    here = s_route[next++];
                    budget -= d;
                }
            }

            t += tick;
            moves.Add((t, here));
        }

        var (standing, stops) = ClientStands(s_route[0], moves, AmbientPace.ClientRunSpeed);

        Assert.True(stops > moves.Count / 2, $"{stops} stops in {moves.Count} moves");
        Assert.InRange(standing / t, 0.55, 0.7); // about 1 - 230/600 of the walk spent standing
    }

}
