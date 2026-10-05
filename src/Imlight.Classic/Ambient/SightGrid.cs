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
 * SIGHT GRID
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): can an ambient wizard see a spot? Built from the
 * same collision.bcd as NavGrid. In the Wizard City zones the solid
 * shapes are Object boxes and cylinders (buildings, walls, rocks, trees)
 * and the floor is Walkable meshes; there are no wall meshes. So a line
 * from one wizard's eyes to another spot is blocked where it passes
 * through a solid shape that is wider than a post (a lamp post or a thin
 * tree trunk does not hide a duel), or under the lowest floor of a cell
 * (a hill or a raised terrace in between). Water is no floor, so it does
 * not block; neither does a bridge (its cell's lowest floor is the ground
 * under it).
 *
 * The owner (2026-10-04): wizards offered help with duels they could not
 * see, "behind walls/buildings, in another part of the zone".
 *
 * USAGE EXAMPLE:
 * var sight = SightGrid.Build(floorTriangles, obstacles);
 * if (sight.CanSee(wizardFeet, duelCentre)) { ... }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Imlight.Classic.Ambient;

/// <summary>Lines of sight over a zone's collision (see the file header).</summary>
public sealed class SightGrid {

    /// <summary>Cell size in world units.</summary>
    public const float CellSize = 50f;

    /// <summary>How high a wizard's eyes are above its feet (a player's camera looks from about here).</summary>
    public const float EyeHeight = 70f;

    /// <summary>A shape narrower than this both ways (a post, a lamp, a sapling) does not hide anything.</summary>
    public const float ThinShape = 60f;

    // Near the ends a line starts inside the looker's own cell; a wall the wizard leans on is not in the way.
    private const float EndSlack = 45f;

    private readonly float[] _lowestFloor; // NaN: no floor
    private readonly Dictionary<int, List<(float Bottom, float Top)>> _solid = [];

    /// <summary>World X of the first column's edge.</summary>
    public float OriginX { get; }

    /// <summary>World Y of the first row's edge.</summary>
    public float OriginY { get; }

    /// <summary>Columns.</summary>
    public int Width { get; }

    /// <summary>Rows.</summary>
    public int Height { get; }

    private SightGrid(float originX, float originY, int width, int height) {
        (OriginX, OriginY, Width, Height) = (originX, originY, width, height);
        _lowestFloor = new float[width * height];
        Array.Fill(_lowestFloor, float.NaN);
    }

    /// <summary>Builds the grid from a zone's floor triangles and solid shapes.</summary>
    public static SightGrid Build(IReadOnlyList<NavTriangle> floor, IReadOnlyList<NavObstacle> obstacles) {
        ArgumentNullException.ThrowIfNull(floor);
        ArgumentNullException.ThrowIfNull(obstacles);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        void Grow(float x, float y) {
            minX = MathF.Min(minX, x);
            minY = MathF.Min(minY, y);
            maxX = MathF.Max(maxX, x);
            maxY = MathF.Max(maxY, y);
        }

        foreach (var t in floor) {
            Grow(t.A.X, t.A.Y);
            Grow(t.B.X, t.B.Y);
            Grow(t.C.X, t.C.Y);
        }

        foreach (var o in obstacles) {
            var (lo, hi, _, _) = o.Bounds(0);
            Grow(lo.X, lo.Y);
            Grow(hi.X, hi.Y);
        }

        if (minX > maxX) {
            return new SightGrid(0, 0, 1, 1);
        }

        var grid = new SightGrid(minX - CellSize, minY - CellSize, (int) ((maxX - minX) / CellSize) + 3,
            (int) ((maxY - minY) / CellSize) + 3);
        foreach (var t in floor) {
            grid.Rasterize(t);
        }

        foreach (var o in obstacles) {
            grid.AddSolid(o);
        }

        return grid;
    }

    /// <summary>
    /// True when a wizard standing at <paramref name="looker"/> can see a wizard (or a duel) at <paramref name="target"/>: the
    /// line between their eyes passes through no wide solid shape and stays above the ground. Distance is the caller's
    /// test.
    /// </summary>
    public bool CanSee(Vector3 looker, Vector3 target, float eye = EyeHeight) {
        var a = looker with { Z = looker.Z + eye };
        var b = target with { Z = target.Z + eye };
        var flat = MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        if (flat <= 2 * EndSlack) {
            return true;
        }

        var steps = (int) MathF.Ceiling(flat / (CellSize / 2));
        for (var i = 0; i <= steps; i++) {
            var along = flat * i / steps;
            if (along < EndSlack || along > flat - EndSlack) {
                continue;
            }

            var p = Vector3.Lerp(a, b, along / flat);
            var (x, y) = CellOf(p.X, p.Y);
            if (x < 0 || y < 0 || x >= Width || y >= Height) {
                continue; // past the zone's edge: nothing to hide behind
            }

            var cell = y * Width + x;
            var ground = _lowestFloor[cell];
            if (!float.IsNaN(ground) && ground > p.Z) {
                return false; // the ground rises above the line (a hill, a terrace)
            }

            if (_solid.TryGetValue(cell, out var spans)) {
                foreach (var (bottom, top) in spans) {
                    if (p.Z >= bottom && p.Z <= top) {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>True when cell (x, y) holds part of a wide solid shape. For plots and tests.</summary>
    public bool IsSolid(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && _solid.ContainsKey(y * Width + x);

    private (int X, int Y) CellOf(float x, float y) => ((int) MathF.Floor((x - OriginX) / CellSize), (int) MathF.Floor((y - OriginY) / CellSize));

    private void Rasterize(NavTriangle t) {
        var normal = Vector3.Cross(t.B - t.A, t.C - t.A);
        var length = normal.Length();
        if (length < 1e-3f || MathF.Abs(normal.Z) / length < 0.3f) {
            return; // a wall-like face: the shapes do the hiding
        }

        var denominator = (t.B.Y - t.C.Y) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Y - t.C.Y);
        if (MathF.Abs(denominator) < 1e-6f) {
            return;
        }

        var (x0, y0) = CellOf(MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)), MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)));
        var (x1, y1) = CellOf(MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X)), MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y)));
        for (var y = Math.Max(0, y0); y <= Math.Min(Height - 1, y1); y++) {
            for (var x = Math.Max(0, x0); x <= Math.Min(Width - 1, x1); x++) {
                var (px, py) = (OriginX + (x + 0.5f) * CellSize, OriginY + (y + 0.5f) * CellSize);
                var a = ((t.B.Y - t.C.Y) * (px - t.C.X) + (t.C.X - t.B.X) * (py - t.C.Y)) / denominator;
                var b = ((t.C.Y - t.A.Y) * (px - t.C.X) + (t.A.X - t.C.X) * (py - t.C.Y)) / denominator;
                var c = 1 - a - b;
                if (a < -1e-4f || b < -1e-4f || c < -1e-4f) {
                    continue;
                }

                var z = a * t.A.Z + b * t.B.Z + c * t.C.Z;
                var cell = y * Width + x;
                if (float.IsNaN(_lowestFloor[cell]) || z < _lowestFloor[cell]) {
                    _lowestFloor[cell] = z;
                }
            }
        }
    }

    private void AddSolid(NavObstacle o) {
        var wide = o.Round ? o.Size.X * 2 : MathF.Max(o.Size.X, o.Size.Y);
        if (wide < ThinShape) {
            return;
        }

        var (lo, hi, bottom, top) = o.Bounds(0);
        var (x0, y0) = CellOf(lo.X, lo.Y);
        var (x1, y1) = CellOf(hi.X, hi.Y);
        for (var y = Math.Max(0, y0); y <= Math.Min(Height - 1, y1); y++) {
            for (var x = Math.Max(0, x0); x <= Math.Min(Width - 1, x1); x++) {
                var centre = new Vector3(OriginX + (x + 0.5f) * CellSize, OriginY + (y + 0.5f) * CellSize, o.Center.Z);
                if (!o.Contains(centre, 0)) {
                    continue;
                }

                var cell = y * Width + x;
                if (!_solid.TryGetValue(cell, out var spans)) {
                    _solid[cell] = spans = [];
                }

                spans.Add((bottom, top));
            }
        }
    }

}
