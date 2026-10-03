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
 * NAV GRID TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: ambient wizards' walks keep to the floor and go around walls,
 * through doorways, and never over ledges or across gaps.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/03/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Imlight.Classic.Ambient;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class NavGridTests {

    private static readonly float[,] s_identity = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

    private static IEnumerable<NavTriangle> Square(float x0, float y0, float x1, float y1, float z = 0) {
        yield return new NavTriangle(new(x0, y0, z), new(x1, y0, z), new(x1, y1, z));
        yield return new NavTriangle(new(x0, y0, z), new(x1, y1, z), new(x0, y1, z));
    }

    private static NavObstacle Wall(float cx, float cy, float sx, float sy) => NavObstacle.Box(new(cx, cy, 150), s_identity, new(sx, sy, 300));

    /// <summary>Every point along the legs (every 5 units) is outside every obstacle, sideways by the body radius.</summary>
    private static void AssertWalkClear(Vector3 from, List<Vector3> route, IReadOnlyList<NavObstacle> obstacles) {
        var at = from;
        foreach (var next in route) {
            var steps = (int) (Vector3.Distance(at, next) / 5) + 1;
            for (var i = 0; i <= steps; i++) {
                var p = Vector3.Lerp(at, next, (float) i / steps);
                foreach (var o in obstacles) {
                    Assert.False(o.Contains(p with { Z = 90 }, NavGrid.BodyRadius - 1), $"walks into an obstacle at {p}");
                }
            }

            at = next;
        }
    }

    [Fact]
    public void AWalkGoesAroundAWallNotThroughIt() {
        NavObstacle[] walls = [Wall(1000, 900, 100, 1600)];
        var grid = NavGrid.Build([.. Square(0, 0, 2000, 2000)], walls);
        var (from, to) = (new Vector3(300, 1000, 0), new Vector3(1700, 1000, 0));

        Assert.False(grid.SegmentClear(from, to));
        Assert.True(grid.TryRoute(from, to, out var route));
        Assert.True(route.Count >= 2);
        Assert.True(route.Max(p => p.Y) > 1700 - 1); // round the open end of the wall
        AssertWalkClear(from, route, walls);
        Assert.Equal(to.X, route[^1].X, 30f);
    }

    [Fact]
    public void AWalkUsesTheDoorway() {
        // A wall across the room with a 240-wide doorway in the middle.
        NavObstacle[] walls = [Wall(1000, 445, 80, 890), Wall(1000, 1555, 80, 890)];
        var grid = NavGrid.Build([.. Square(0, 0, 2000, 2000)], walls);
        var from = new Vector3(200, 300, 0);

        Assert.True(grid.TryRoute(from, new Vector3(1800, 1700, 0), out var route));
        AssertWalkClear(from, route, walls);
    }

    [Fact]
    public void NoWalkThroughAClosedWallOrAcrossAGap() {
        var closed = NavGrid.Build([.. Square(0, 0, 2000, 2000)], [Wall(1000, 1000, 80, 2400)]);
        Assert.False(closed.TryRoute(new(300, 1000, 0), new(1700, 1000, 0), out _));

        var islands = NavGrid.Build([.. Square(0, 0, 900, 2000), .. Square(1100, 0, 2000, 2000)], []);
        Assert.False(islands.TryRoute(new(300, 1000, 0), new(1700, 1000, 0), out _));
        Assert.False(islands.Connected(new(300, 1000, 0), new(1700, 1000, 0)));
    }

    [Fact]
    public void LedgesAreNotStepsButRampsAre() {
        var floor = Square(0, 0, 1000, 1000).Concat(Square(1000, 0, 2000, 1000, 200)).ToList();
        var ledge = NavGrid.Build(floor, []);
        Assert.False(ledge.TryRoute(new(300, 500, 0), new(1700, 500, 200), out _));

        // A ramp from 0 to 200 over 600 units joins them.
        floor = [.. Square(0, 0, 1000, 1000), .. Square(1600, 0, 2600, 1000, 200),
            new NavTriangle(new(1000, 0, 0), new(1600, 0, 200), new(1600, 1000, 200)),
            new NavTriangle(new(1000, 0, 0), new(1600, 1000, 200), new(1000, 1000, 0))];
        var ramp = NavGrid.Build(floor, []);
        Assert.True(ramp.TryRoute(new(300, 500, 0), new(2300, 500, 200), out var route));
        Assert.Equal(200, route[^1].Z, 10f);
    }

    [Fact]
    public void TreesAreWalkedAroundAndTurnedShapesStayTurned() {
        // A tree (cylinder, radius 150) on the straight line.
        NavObstacle[] tree = [NavObstacle.Cylinder(new(1000, 1000, 250), s_identity, 150, 500)];
        var grid = NavGrid.Build([.. Square(0, 0, 2000, 2000)], tree);
        var from = new Vector3(300, 1000, 0);
        Assert.True(grid.TryRoute(from, new(1700, 1000, 0), out var route));
        AssertWalkClear(from, route, tree);

        // A long thin box turned 90 degrees (row-major rotation, world = R x local): it lies along Y.
        float[,] quarter = { { 0, -1, 0 }, { 1, 0, 0 }, { 0, 0, 1 } };
        var turned = NavObstacle.Box(new(1000, 1000, 150), quarter, new(1000, 40, 300));
        Assert.True(turned.Contains(new(1000, 1400, 90), 0));
        Assert.False(turned.Contains(new(1400, 1000, 90), 0));
    }

    [Fact]
    public void LowCurbsAndHighBranchesDoNotBlock() {
        NavObstacle[] things = [
            NavObstacle.Box(new(1000, 1000, 5), s_identity, new(300, 300, 10)),     // a curb under the feet
            NavObstacle.Box(new(1000, 1400, 400), s_identity, new(300, 300, 100)),  // an awning overhead
        ];
        var grid = NavGrid.Build([.. Square(0, 0, 2000, 2000)], things);

        Assert.True(grid.IsWalkable(new(1000, 1000, 0)));
        Assert.True(grid.IsWalkable(new(1000, 1400, 0)));
    }

}
