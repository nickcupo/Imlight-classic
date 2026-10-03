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
 * NAV GRID
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: where an ambient wizard can walk, from the zone's own collision
 * (collision.bcd), so it keeps to the ground a player could walk on and
 * goes around walls, trees and buildings instead of through them (owner,
 * 2026-10-03: "friendly wizards walk weird and phase through barriers").
 *
 * The floor is the zone's Walkable meshes, sampled on a square grid (up to
 * two floors a cell, for bridges). A cell is blocked where a wizard-sized
 * body standing on it would touch an obstacle (the Object boxes and
 * cylinders), and near the floor's edge. Routes are 8-connected A* over the
 * cells (no corner cutting, small steps only), then pulled straight where
 * the straight line stays on open cells.
 *
 * Collision shapes as the client stores them (checked against zone.nav's
 * walkable points): world = Rotation (row-major) x local + Location; a
 * box's three numbers are full sizes; a cylinder's are length (height,
 * along local Z) then radius.
 *
 * USAGE EXAMPLE:
 * var grid = NavGrid.Build(floorTriangles, obstacles);
 * if (grid.TryRoute(from, to, out var waypoints)) { ... }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/03/2026
 */

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Imlight.Classic.Ambient;

/// <summary>A walkable floor triangle in world space.</summary>
public readonly record struct NavTriangle(Vector3 A, Vector3 B, Vector3 C);

/// <summary>A solid collision shape: a box (Size = full sizes) or an upright cylinder (Radius, Height).</summary>
/// <param name="Center">World position of the shape's centre.</param>
/// <param name="AxisX">World direction of the local X axis.</param>
/// <param name="AxisY">World direction of the local Y axis.</param>
/// <param name="AxisZ">World direction of the local Z axis.</param>
/// <param name="Size">Box: full sizes along the local axes. Cylinder: (radius, radius, height).</param>
/// <param name="Round">A cylinder (round in local X/Y).</param>
public readonly record struct NavObstacle(Vector3 Center, Vector3 AxisX, Vector3 AxisY, Vector3 AxisZ, Vector3 Size, bool Round) {

    /// <summary>From a collision shape's row-major rotation (world = R x local + location).</summary>
    public static NavObstacle Box(Vector3 location, float[,] rotation, Vector3 size)
        => new(location, Column(rotation, 0), Column(rotation, 1), Column(rotation, 2), size, false);

    /// <summary>A cylinder; the stored params are (length, radius) with the length along local Z.</summary>
    public static NavObstacle Cylinder(Vector3 location, float[,] rotation, float radius, float height)
        => new(location, Column(rotation, 0), Column(rotation, 1), Column(rotation, 2), new Vector3(radius, radius, height), true);

    private static Vector3 Column(float[,] r, int i) => new(r[0, i], r[1, i], r[2, i]);

    /// <summary>True when <paramref name="p"/> is inside the shape grown sideways by <paramref name="inflate"/>.</summary>
    public bool Contains(Vector3 p, float inflate) {
        var d = p - Center;
        var x = Vector3.Dot(d, AxisX);
        var y = Vector3.Dot(d, AxisY);
        var z = Vector3.Dot(d, AxisZ);
        if (MathF.Abs(z) > Size.Z / 2) {
            return false;
        }

        return Round
            ? x * x + y * y <= (Size.X + inflate) * (Size.X + inflate)
            : MathF.Abs(x) <= Size.X / 2 + inflate && MathF.Abs(y) <= Size.Y / 2 + inflate;
    }

    /// <summary>The world-space box around the shape, grown sideways by <paramref name="inflate"/>.</summary>
    public (Vector2 Min, Vector2 Max, float Bottom, float Top) Bounds(float inflate) {
        var hx = Round ? Size.X + inflate : Size.X / 2 + inflate;
        var hy = Round ? Size.Y + inflate : Size.Y / 2 + inflate;
        var hz = Size.Z / 2;
        var ex = MathF.Abs(AxisX.X) * hx + MathF.Abs(AxisY.X) * hy + MathF.Abs(AxisZ.X) * hz;
        var ey = MathF.Abs(AxisX.Y) * hx + MathF.Abs(AxisY.Y) * hy + MathF.Abs(AxisZ.Y) * hz;
        var ez = MathF.Abs(AxisX.Z) * hx + MathF.Abs(AxisY.Z) * hy + MathF.Abs(AxisZ.Z) * hz;
        return (new Vector2(Center.X - ex, Center.Y - ey), new Vector2(Center.X + ex, Center.Y + ey), Center.Z - ez, Center.Z + ez);
    }

}

/// <summary>A zone's walkable cells and routes over them (see the file header).</summary>
public sealed class NavGrid {

    /// <summary>Cell size in world units.</summary>
    public const float CellSize = 40f;

    /// <summary>How far a wizard's body reaches sideways (a player's capsule is about this wide).</summary>
    public const float BodyRadius = 30f;

    /// <summary>The largest height change between neighbouring cells (a step or a ramp, not a ledge).</summary>
    public const float MaxStep = 34f;

    private const short Empty = short.MinValue;
    private const byte Floor = 1;
    private const byte Blocked = 2;
    private const byte Open = 4;
    private const float SameSurface = 90f;
    private static readonly float[] s_bodyHeights = [28f, 90f, 150f];

    private readonly short[] _height0;
    private short[]? _height1;
    private readonly byte[] _flags0;
    private byte[]? _flags1;
    private readonly int[] _component;

    /// <summary>World X of the grid's first column edge.</summary>
    public float OriginX { get; }

    /// <summary>World Y of the grid's first row edge.</summary>
    public float OriginY { get; }

    /// <summary>Columns.</summary>
    public int Width { get; }

    /// <summary>Rows.</summary>
    public int Height { get; }

    private NavGrid(float originX, float originY, int width, int height) {
        (OriginX, OriginY, Width, Height) = (originX, originY, width, height);
        _height0 = new short[width * height];
        Array.Fill(_height0, Empty);
        _flags0 = new byte[width * height];
        _component = new int[width * height * 2];
    }

    /// <summary>Builds the grid from the zone's floor and obstacles.</summary>
    public static NavGrid Build(IReadOnlyList<NavTriangle> floor, IReadOnlyList<NavObstacle> obstacles) {
        ArgumentNullException.ThrowIfNull(floor);
        ArgumentNullException.ThrowIfNull(obstacles);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var t in floor) {
            minX = MathF.Min(minX, MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)));
            minY = MathF.Min(minY, MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)));
            maxX = MathF.Max(maxX, MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X)));
            maxY = MathF.Max(maxY, MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y)));
        }

        if (floor.Count == 0) {
            return new NavGrid(0, 0, 1, 1);
        }

        var grid = new NavGrid(minX - CellSize, minY - CellSize, (int) ((maxX - minX) / CellSize) + 3, (int) ((maxY - minY) / CellSize) + 3);
        foreach (var t in floor) {
            grid.Rasterize(t);
        }

        foreach (var o in obstacles) {
            grid.Block(o);
        }

        grid.MarkOpen();
        grid.Label();
        return grid;
    }

    // ---- queries --------------------------------------------------------------------------------

    /// <summary>The walkable spot nearest <paramref name="p"/> (within <paramref name="reach"/>), on the floor closest in height.</summary>
    public Vector3? Snap(Vector3 p, float reach = 400f) {
        var best = -1;
        var bestCost = float.MaxValue;
        var (cx, cy) = CellOf(p.X, p.Y);
        var r = (int) MathF.Ceiling(reach / CellSize);
        for (var dy = -r; dy <= r; dy++) {
            for (var dx = -r; dx <= r; dx++) {
                var (x, y) = (cx + dx, cy + dy);
                if (!InGrid(x, y)) {
                    continue;
                }

                for (var layer = 0; layer < 2; layer++) {
                    var node = Node(x, y, layer);
                    if (!IsOpen(node)) {
                        continue;
                    }

                    var horizontal = MathF.Sqrt(dx * dx + dy * dy) * CellSize;
                    var cost = horizontal + MathF.Abs(HeightOf(node) - p.Z) * 3;
                    if (horizontal <= reach && cost < bestCost) {
                        (best, bestCost) = (node, cost);
                    }
                }
            }
        }

        return best < 0 ? null : Point(best);
    }

    /// <summary>True when <paramref name="p"/> stands on an open cell (at about its height).</summary>
    public bool IsWalkable(Vector3 p) => NodeAt(p) >= 0;

    /// <summary>The floor height under <paramref name="p"/>, on the floor nearest its height, or null.</summary>
    public float? FloorAt(Vector3 p) {
        var (x, y) = CellOf(p.X, p.Y);
        if (!InGrid(x, y)) {
            return null;
        }

        float? best = null;
        for (var layer = 0; layer < 2; layer++) {
            var h = RawHeight(x, y, layer);
            if (h != Empty && (best is null || MathF.Abs(h - p.Z) < MathF.Abs(best.Value - p.Z))) {
                best = h;
            }
        }

        return best;
    }

    /// <summary>True when both points are walkable and one walk joins them.</summary>
    public bool Connected(Vector3 a, Vector3 b) {
        var (na, nb) = (NodeAt(a), NodeAt(b));
        return na >= 0 && nb >= 0 && _component[na] == _component[nb];
    }

    /// <summary>
    /// A walk from <paramref name="from"/> to <paramref name="to"/> as straight legs (the first waypoint is the first
    /// turn, the last is the snapped goal). False when either end has no open ground near it or no walk joins them.
    /// </summary>
    public bool TryRoute(Vector3 from, Vector3 to, out List<Vector3> waypoints, int maxExpansions = 250_000) {
        waypoints = [];
        if (Snap(from, 200f) is not { } start || Snap(to) is not { } goal) {
            return false;
        }

        var (s, g) = (NodeAt(start), NodeAt(goal));
        if (s < 0 || g < 0 || _component[s] != _component[g]) {
            return false;
        }

        if (s == g) {
            waypoints.Add(goal);
            return true;
        }

        var cells = AStar(s, g, maxExpansions);
        if (cells is null) {
            return false;
        }

        // Pull the string: keep a turn only where the straight line from the last kept turn would leave open ground.
        var anchor = 0;
        for (var i = 2; i < cells.Count; i++) {
            if (!Clear(cells[anchor], cells[i])) {
                waypoints.Add(Point(cells[i - 1]));
                anchor = i - 1;
            }
        }

        waypoints.Add(goal);
        return true;
    }

    /// <summary>True when the straight walk from <paramref name="a"/> to <paramref name="b"/> stays on open ground.</summary>
    public bool SegmentClear(Vector3 a, Vector3 b) {
        var (na, nb) = (NodeAt(a), NodeAt(b));
        return na >= 0 && nb >= 0 && Clear(na, nb);
    }

    /// <summary>Open (walkable) cells, layer by layer: (column, row, height). For plots.</summary>
    public IEnumerable<(int X, int Y, float Z)> OpenCells() {
        for (var layer = 0; layer < 2; layer++) {
            for (var y = 0; y < Height; y++) {
                for (var x = 0; x < Width; x++) {
                    var node = Node(x, y, layer);
                    if (IsOpen(node)) {
                        yield return (x, y, HeightOf(node));
                    }
                }
            }
        }
    }

    /// <summary>Cells with floor that a body cannot stand on (an obstacle). For plots.</summary>
    public IEnumerable<(int X, int Y)> BlockedCells() {
        for (var y = 0; y < Height; y++) {
            for (var x = 0; x < Width; x++) {
                if ((_flags0[y * Width + x] & Blocked) != 0 || (_flags1 is not null && (_flags1[y * Width + x] & Blocked) != 0)) {
                    yield return (x, y);
                }
            }
        }
    }

    // ---- building -------------------------------------------------------------------------------

    private void Rasterize(NavTriangle t) {
        var normal = Vector3.Cross(t.B - t.A, t.C - t.A);
        var length = normal.Length();
        if (length < 1e-3f || MathF.Abs(normal.Z) / length < 0.6f) {
            return; // a wall or a steep bank, not floor
        }

        var (x0, y0) = CellOf(MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)), MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)));
        var (x1, y1) = CellOf(MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X)), MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y)));
        var denominator = (t.B.Y - t.C.Y) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Y - t.C.Y);
        if (MathF.Abs(denominator) < 1e-6f) {
            return;
        }

        for (var y = Math.Max(0, y0); y <= Math.Min(Height - 1, y1); y++) {
            for (var x = Math.Max(0, x0); x <= Math.Min(Width - 1, x1); x++) {
                var (px, py) = (OriginX + (x + 0.5f) * CellSize, OriginY + (y + 0.5f) * CellSize);
                var a = ((t.B.Y - t.C.Y) * (px - t.C.X) + (t.C.X - t.B.X) * (py - t.C.Y)) / denominator;
                var b = ((t.C.Y - t.A.Y) * (px - t.C.X) + (t.A.X - t.C.X) * (py - t.C.Y)) / denominator;
                var c = 1 - a - b;
                if (a < -1e-4f || b < -1e-4f || c < -1e-4f) {
                    continue;
                }

                AddFloor(x, y, a * t.A.Z + b * t.B.Z + c * t.C.Z);
            }
        }
    }

    private void AddFloor(int x, int y, float z) {
        var i = y * Width + x;
        var h = (short) Math.Clamp(MathF.Round(z), short.MinValue + 1, short.MaxValue);
        if (_height0[i] == Empty || MathF.Abs(_height0[i] - h) < SameSurface) {
            _height0[i] = _height0[i] == Empty ? h : Math.Max(_height0[i], h); // overlapping meshes: the top one
            _flags0[i] |= Floor;
            return;
        }

        if (_height1 is null) {
            _height1 = new short[_height0.Length];
            Array.Fill(_height1, Empty);
            _flags1 = new byte[_flags0.Length];
        }

        if (_height1[i] == Empty || MathF.Abs(_height1[i] - h) < SameSurface) {
            _height1[i] = _height1[i] == Empty ? h : Math.Max(_height1[i], h);
            _flags1![i] |= Floor;
        }
    }

    private void Block(NavObstacle o) {
        var (min, max, bottom, top) = o.Bounds(BodyRadius);
        var (x0, y0) = CellOf(min.X, min.Y);
        var (x1, y1) = CellOf(max.X, max.Y);
        for (var y = Math.Max(0, y0); y <= Math.Min(Height - 1, y1); y++) {
            for (var x = Math.Max(0, x0); x <= Math.Min(Width - 1, x1); x++) {
                for (var layer = 0; layer < 2; layer++) {
                    var h = RawHeight(x, y, layer);
                    if (h == Empty || h + s_bodyHeights[^1] < bottom || h + s_bodyHeights[0] > top) {
                        continue;
                    }

                    var (px, py) = (OriginX + (x + 0.5f) * CellSize, OriginY + (y + 0.5f) * CellSize);
                    foreach (var lift in s_bodyHeights) {
                        if (o.Contains(new Vector3(px, py, h + lift), BodyRadius)) {
                            FlagsOf(layer)![y * Width + x] |= Blocked;
                            break;
                        }
                    }
                }
            }
        }
    }

    private void MarkOpen() {
        // Open: floor, no obstacle, and floor all round at a step's height (keeps a body's width from ledges and holes).
        for (var y = 0; y < Height; y++) {
            for (var x = 0; x < Width; x++) {
                for (var layer = 0; layer < 2; layer++) {
                    var flags = FlagsOf(layer);
                    var i = y * Width + x;
                    if (flags is null || (flags[i] & (Floor | Blocked)) != Floor) {
                        continue;
                    }

                    var h = RawHeight(x, y, layer);
                    var edge = false;
                    foreach (var (dx, dy) in s_four) {
                        if (StepTo(x + dx, y + dy, h) < 0) {
                            edge = true;
                            break;
                        }
                    }

                    if (!edge) {
                        flags[i] |= Open;
                    }
                }
            }
        }
    }

    private void Label() {
        Array.Fill(_component, -1);
        var next = 0;
        var queue = new Queue<int>();
        for (var start = 0; start < _component.Length; start++) {
            if (!IsOpen(start) || _component[start] >= 0) {
                continue;
            }

            _component[start] = next;
            queue.Enqueue(start);
            while (queue.Count > 0) {
                var node = queue.Dequeue();
                foreach (var (neighbour, _) in Neighbours(node)) {
                    if (_component[neighbour] < 0) {
                        _component[neighbour] = next;
                        queue.Enqueue(neighbour);
                    }
                }
            }

            next++;
        }
    }

    // ---- search ---------------------------------------------------------------------------------

    private static readonly (int Dx, int Dy)[] s_four = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private static readonly (int Dx, int Dy)[] s_eight = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    private List<int>? AStar(int start, int goal, int maxExpansions) {
        var cost = new Dictionary<int, float> { [start] = 0 };
        var parent = new Dictionary<int, int>();
        var open = new PriorityQueue<int, float>();
        var (gx, gy) = XY(goal);
        open.Enqueue(start, 0);
        var expansions = 0;
        while (open.TryDequeue(out var node, out _)) {
            if (node == goal) {
                var path = new List<int> { goal };
                while (parent.TryGetValue(path[^1], out var back)) {
                    path.Add(back);
                }

                path.Reverse();
                return path;
            }

            if (++expansions > maxExpansions) {
                return null;
            }

            var here = cost[node];
            foreach (var (neighbour, step) in Neighbours(node)) {
                var through = here + step;
                if (cost.TryGetValue(neighbour, out var known) && known <= through) {
                    continue;
                }

                cost[neighbour] = through;
                parent[neighbour] = node;
                var (nx, ny) = XY(neighbour);
                var (dx, dy) = (Math.Abs(nx - gx), Math.Abs(ny - gy));
                var h = (Math.Max(dx, dy) + 0.4142f * Math.Min(dx, dy)) * CellSize;
                open.Enqueue(neighbour, through + h);
            }
        }

        return null;
    }

    private IEnumerable<(int Node, float Step)> Neighbours(int node) {
        var (x, y) = XY(node);
        var h = HeightOf(node);
        foreach (var (dx, dy) in s_eight) {
            var next = StepTo(x + dx, y + dy, h);
            if (next < 0 || !IsOpen(next)) {
                continue;
            }

            if (dx != 0 && dy != 0 && (OpenStep(x + dx, y, h) < 0 || OpenStep(x, y + dy, h) < 0)) {
                continue; // no cutting a corner past a wall
            }

            yield return (next, dx != 0 && dy != 0 ? CellSize * 1.4142f : CellSize);
        }
    }

    /// <summary>True when every cell on the line between the two nodes is open and joined at a step's height.</summary>
    private bool Clear(int from, int to) {
        var (x0, y0) = XY(from);
        var (x1, y1) = XY(to);
        var h = HeightOf(from);
        var steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) * 3;
        var (lastX, lastY) = (x0, y0);
        for (var i = 1; i <= steps; i++) {
            var t = (float) i / steps;
            var x = (int) MathF.Floor(x0 + 0.5f + (x1 - x0) * t);
            var y = (int) MathF.Floor(y0 + 0.5f + (y1 - y0) * t);
            if (x == lastX && y == lastY) {
                continue;
            }

            if (x != lastX && y != lastY && (OpenStep(x, lastY, h) < 0 || OpenStep(lastX, y, h) < 0)) {
                return false; // a diagonal squeeze
            }

            var next = OpenStep(x, y, h);
            if (next < 0) {
                return false;
            }

            (lastX, lastY, h) = (x, y, HeightOf(next));
        }

        return true;
    }

    // ---- cells ----------------------------------------------------------------------------------

    private int StepTo(int x, int y, float fromHeight) {
        if (!InGrid(x, y)) {
            return -1;
        }

        for (var layer = 0; layer < 2; layer++) {
            var h = RawHeight(x, y, layer);
            if (h != Empty && MathF.Abs(h - fromHeight) <= MaxStep) {
                return Node(x, y, layer);
            }
        }

        return -1;
    }

    private int OpenStep(int x, int y, float fromHeight) {
        var node = StepTo(x, y, fromHeight);
        return node >= 0 && IsOpen(node) ? node : -1;
    }

    private int NodeAt(Vector3 p) {
        var (x, y) = CellOf(p.X, p.Y);
        if (!InGrid(x, y)) {
            return -1;
        }

        var best = -1;
        for (var layer = 0; layer < 2; layer++) {
            var node = Node(x, y, layer);
            if (IsOpen(node) && MathF.Abs(HeightOf(node) - p.Z) <= 120 && (best < 0 || MathF.Abs(HeightOf(node) - p.Z) < MathF.Abs(HeightOf(best) - p.Z))) {
                best = node;
            }
        }

        return best;
    }

    private Vector3 Point(int node) {
        var (x, y) = XY(node);
        return new Vector3(OriginX + (x + 0.5f) * CellSize, OriginY + (y + 0.5f) * CellSize, HeightOf(node));
    }

    private (int X, int Y) CellOf(float x, float y) => ((int) MathF.Floor((x - OriginX) / CellSize), (int) MathF.Floor((y - OriginY) / CellSize));

    private bool InGrid(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    private int Node(int x, int y, int layer) => (y * Width + x) * 2 + layer;

    private (int X, int Y) XY(int node) => (node / 2 % Width, node / 2 / Width);

    private short RawHeight(int x, int y, int layer)
        => layer == 0 ? _height0[y * Width + x] : _height1 is null ? Empty : _height1[y * Width + x];

    private float HeightOf(int node) => node % 2 == 0 ? _height0[node / 2] : _height1![node / 2];

    private byte[]? FlagsOf(int layer) => layer == 0 ? _flags0 : _flags1;

    private bool IsOpen(int node) => FlagsOf(node % 2) is { } flags && (flags[node / 2] & Open) != 0;

}
