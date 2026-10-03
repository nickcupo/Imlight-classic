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

}
