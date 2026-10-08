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
 * CLASSIC: resolves the path of a walker that is placed in a zone (the Marleybone cops), as opposed to
 * one a spawner creates. A placed walker's PathBehavior template names a path of the zone's path data.
 * CLASSIC (2026-10-08): a walker placed off its path walks to the nearest node over the zone's walk grid (the
 * ambient wizards' NavGrid, from collision.bcd) when the straight line would cross a wall; before, that first
 * leg was always a straight line of up to 862 units (the Marleybone cops).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Ambient;

namespace Imlight.CoreLib.Game.Zone.Core;

internal sealed class PlacedWalkerPaths {

    private readonly Dictionary<ulong, PathObjectTemplate> _pathsById = [];
    private readonly Dictionary<ulong, NodeObject> _nodesById = [];

    public PlacedWalkerPaths(PathTemplateList pathData, NodeTemplateList nodeData) {
        foreach (var path in pathData?.m_pathList ?? []) {
            if (path is not null) {
                _pathsById.TryAdd(path.m_id.Full, path);
            }
        }

        foreach (var node in nodeData?.m_nodeList ?? []) {
            if (node is not null) {
                _nodesById.TryAdd(node.m_id.Full, node);
            }
        }
    }

    /// <summary>The nodes of path <paramref name="pathId"/>, in path order; empty when the zone has no usable path by that id.</summary>
    public List<NodeObject> NodesOf(ulong pathId) {
        if (pathId == 0 || !_pathsById.TryGetValue(pathId, out var path) || path.m_nodeIDs is null) {
            return [];
        }

        var nodes = new List<NodeObject>(path.m_nodeIDs.Count);
        foreach (var id in path.m_nodeIDs) {
            if (!_nodesById.TryGetValue(id.Full, out var node)) {
                return []; // a path with a node the node data lacks is not walkable.
            }

            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>The node closest to <paramref name="location"/>, or null with no nodes.</summary>
    public static NodeObject Nearest(IEnumerable<NodeObject> nodes, Vector3 location)
        => nodes.MinBy(node => Vector3.Distance(node.m_location, location));

    /// <summary>How a placed walker's first walk to its path goes (see <see cref="ApproachLegs"/>).</summary>
    internal enum Approach { Straight, NoGrid, Routed, NoRoute }

    /// <summary>
    /// The legs of a placed walker's first walk, from <paramref name="from"/> to <paramref name="node"/> (the nearest
    /// node of its path), ending with <paramref name="node"/> itself. One leg (the node) when the straight line stays on
    /// the zone's open ground, when there is no grid (no collision data: the old straight walk) or when no walk joins
    /// the two (logged by the caller); otherwise the grid's turns first, each a node that faces along its leg.
    /// </summary>
    internal static List<NodeObject> ApproachLegs(NavGrid grid, Vector3 from, NodeObject node, out Approach how) {
        if (grid is null) {
            how = Approach.NoGrid;
            return [node];
        }

        var (a, b) = (Num(from), Num(node.m_location));
        if (grid.SegmentClear(a, b)) {
            how = Approach.Straight;
            return [node];
        }

        if (!grid.TryRoute(a, b, out var turns) || turns.Count == 0) {
            how = Approach.NoRoute;
            return [node];
        }

        how = Approach.Routed;
        var legs = new List<NodeObject>(turns.Count);
        var at = a;
        foreach (var turn in turns.Take(turns.Count - 1)) {          // the last turn is the snapped node: walk to the node
            legs.Add(new NodeObject {
                m_location = new Vector3(turn.X, turn.Y, turn.Z),
                m_direction = ClientDirection(at, turn),
            });
            at = turn;
        }

        legs.Add(node);
        return legs;
    }

    private static System.Numerics.Vector3 Num(Vector3 v) => new(v.X, v.Y, v.Z);

    // The node yaw the server sends for a move (radians, the client's clockwise yaw), facing from a to b.
    private static float ClientDirection(System.Numerics.Vector3 a, System.Numerics.Vector3 b)
        => Imlight.CoreLib.Classic.Ambient.AmbientWizards.ClientYaw(MathF.Atan2(b.Y - a.Y, b.X - a.X));

}
