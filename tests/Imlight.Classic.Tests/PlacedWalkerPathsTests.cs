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
 * PLACED WALKER PATH TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: a walker placed in a zone (the Marleybone cops) finds its path by the id its
 * PathBehavior template names; a missing, empty or broken path resolves to no nodes.
 *
 * Created by: Nick with Claude Code (claude-sonnet-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/03/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Ambient;
using Imlight.CoreLib.Game.Zone.Core;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class PlacedWalkerPathsTests {

    private static NodeObject Node(ulong id, float x, float y)
        => new() { m_id = id, m_location = new Vector3(x, y, 0) };

    private static PlacedWalkerPaths Zone() {
        var nodes = new NodeTemplateList { m_nodeList = [Node(1, 0, 0), Node(2, 100, 0), Node(3, 100, 100)] };
        var paths = new PathTemplateList {
            m_pathList = [
                new PathObjectTemplate { m_id = 10, m_nodeIDs = [1, 2, 3] },
                new PathObjectTemplate { m_id = 11, m_nodeIDs = [] },
                new PathObjectTemplate { m_id = 12, m_nodeIDs = [1, 99] },
            ]
        };

        return new PlacedWalkerPaths(paths, nodes);
    }

    [Fact]
    public void ANamedPathResolvesToItsNodesInOrder() {
        var nodes = Zone().NodesOf(10);

        Assert.Equal([1UL, 2UL, 3UL], nodes.Select(node => node.m_id.Full));
    }

    [Theory]
    [InlineData(0UL)]   // the template names no path
    [InlineData(999UL)] // a path the zone's data does not have (the Museum cops)
    [InlineData(11UL)]  // a path with no nodes
    [InlineData(12UL)]  // a path naming a node the node data lacks
    public void AnUnusablePathResolvesToNoNodes(ulong pathId)
        => Assert.Empty(Zone().NodesOf(pathId));

    [Fact]
    public void ADataLessZoneResolvesToNoNodes()
        => Assert.Empty(new PlacedWalkerPaths(null, null).NodesOf(10));

    [Fact]
    public void ANodeOffThePathIsApproachedAtTheNearestNode() {
        var nodes = Zone().NodesOf(10);

        Assert.Equal(2UL, PlacedWalkerPaths.Nearest(nodes, new Vector3(90, 30, 0)).m_id.Full);
        Assert.Equal(1UL, PlacedWalkerPaths.Nearest(nodes, new Vector3(-400, 5, 0)).m_id.Full);
    }


    // ---- the first walk to the path (2026-10-08) ----------------------------------------------------------------

    private static readonly float[,] s_identity = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

    private static NavGrid Room(params NavObstacle[] walls) {
        var (a, b, c, d) = (new System.Numerics.Vector3(0, 0, 0), new System.Numerics.Vector3(2000, 0, 0),
            new System.Numerics.Vector3(2000, 2000, 0), new System.Numerics.Vector3(0, 2000, 0));
        return NavGrid.Build([new NavTriangle(a, b, c), new NavTriangle(a, c, d)], walls);
    }

    private static NavObstacle Wall(float cx, float cy, float sx, float sy)
        => NavObstacle.Box(new System.Numerics.Vector3(cx, cy, 150), s_identity, new System.Numerics.Vector3(sx, sy, 300));

    [Fact]
    public void AClearFirstWalkIsOneStraightLeg() {
        var node = Node(7, 1700, 1000);
        var legs = PlacedWalkerPaths.ApproachLegs(Room(), new Vector3(300, 1000, 0), node, out var how);

        Assert.Equal(PlacedWalkerPaths.Approach.Straight, how);
        Assert.Same(node, Assert.Single(legs));
    }

    [Fact]
    public void AFirstWalkAcrossAWallGoesRoundItAndEndsAtTheNode() {
        var wall = Wall(1000, 900, 100, 1600);
        var grid = Room(wall);
        var node = Node(7, 1700, 1000);
        var legs = PlacedWalkerPaths.ApproachLegs(grid, new Vector3(300, 1000, 0), node, out var how);

        Assert.Equal(PlacedWalkerPaths.Approach.Routed, how);
        Assert.True(legs.Count >= 2);
        Assert.Same(node, legs[^1]);
        Assert.True(legs.Max(leg => leg.m_location.Y) > 1700 - 1); // round the open end of the wall
        var at = new System.Numerics.Vector3(300, 1000, 0);
        foreach (var leg in legs.Take(legs.Count - 1)) {
            var next = new System.Numerics.Vector3(leg.m_location.X, leg.m_location.Y, leg.m_location.Z);
            Assert.True(grid.SegmentClear(at, next), $"leg {at} -> {next} crosses the wall");
            Assert.InRange(leg.m_direction, 0f, 2 * System.MathF.PI);
            at = next;
        }
    }

    [Fact]
    public void WithoutAGridOrARouteTheWalkStaysTheOldStraightLine() {
        var node = Node(7, 1700, 1000);
        Assert.Same(node, Assert.Single(PlacedWalkerPaths.ApproachLegs(null, new Vector3(300, 1000, 0), node, out var none)));
        Assert.Equal(PlacedWalkerPaths.Approach.NoGrid, none);

        var closed = Room(Wall(1000, 1000, 80, 2400)); // the room cut in two
        Assert.Same(node, Assert.Single(PlacedWalkerPaths.ApproachLegs(closed, new Vector3(300, 1000, 0), node, out var cut)));
        Assert.Equal(PlacedWalkerPaths.Approach.NoRoute, cut);
    }

}
