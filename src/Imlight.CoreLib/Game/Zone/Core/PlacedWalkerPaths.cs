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
 */

using System.Collections.Generic;
using System.Linq;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;

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

}
