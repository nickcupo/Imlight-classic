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
 * AMBIENT NAV
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: a zone's NavGrid, built from the collision.bcd in its WAD (the
 * same file the client walks on): the Walkable meshes are the floor
 * (water left out), the Object boxes and cylinders are what a wizard must
 * go around. Built once per zone, off the actor thread; null when the zone
 * has no collision file.
 *
 * USAGE EXAMPLE:
 * var grid = await AmbientNav.ForZone("WizardCity/WC_Hub");
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Imcodec.BCD;
using Bcg = Imcodec.BCD.GeomParams;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using NumVector3 = System.Numerics.Vector3;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>Zone walk grids from collision.bcd (see the file header).</summary>
internal static class AmbientNav {

    private const string CollisionFile = "collision.bcd";
    private static readonly ConcurrentDictionary<string, Lazy<Task<NavGrid>>> s_grids = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The zone's grid (built on first use, then shared), or null without collision data.</summary>
    internal static Task<NavGrid> ForZone(string zone)
        => s_grids.GetOrAdd(zone, z => new Lazy<Task<NavGrid>>(() => Task.Run(() => Load(z)))).Value;

    /// <summary>
    /// How fast an ambient wizard runs, in units a second: the speed the official client runs any player's mobile at,
    /// from the player template's (template 1, PlayerObject) PathMovementBehavior, 600 in the r806919 data. CLASSIC
    /// (2026-10-04): was 230, a guess; the client ran each 300 ms step in 115 ms and stood still the rest (the owner:
    /// "like someone is tapping the forward key"). See AmbientWalk.
    /// </summary>
    internal static float RunSpeed => s_runSpeed.Value;

    private static readonly Lazy<float> s_runSpeed = new(() => {
        try {
            if (CoreObjectFactory.GetCoreTemplate(1) is Imcodec.ObjectProperty.TypeCache.GameObjectTemplate template
                && template.m_behaviors?.OfType<Imcodec.ObjectProperty.TypeCache.PathMovementBehaviorTemplate>().FirstOrDefault()
                    is { } path) {
                return AmbientPace.FromTemplate(path.m_movementSpeed, path.m_movementScale);
            }
        }
        catch (Exception) {
            // No template data (tests, a bare install): the r806919 value.
        }

        return AmbientPace.ClientRunSpeed;
    });

    private static NavGrid Load(string zone) {
        try {
            if (!ResourceManager.TryLoadArchive(zone, out var wad) || wad.OpenFile(CollisionFile) is not { } bytes) {
                Logger.Warning("Ambient wizards in {Zone}: no {File}; they will stay put.", Logger.Args(zone, CollisionFile));
                return null;
            }

            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            var bcd = Bcd.Parse(stream);
            var started = DateTime.UtcNow;
            var grid = Build(bcd);
            Logger.Information("Ambient wizards in {Zone}: walk grid {W}x{H} built in {Ms} ms.",
                Logger.Args(zone, grid.Width, grid.Height, (int) (DateTime.UtcNow - started).TotalMilliseconds));
            return grid;
        }
        catch (Exception e) {
            Logger.Warning("Ambient wizards in {Zone}: could not read {File} ({Error}); they will stay put.",
                Logger.Args(zone, CollisionFile, e.Message));
            return null;
        }
    }

    /// <summary>The grid for a parsed collision file.</summary>
    internal static NavGrid Build(Bcd bcd) {
        var (floor, obstacles) = Shapes(bcd);
        return NavGrid.Build(floor, obstacles);
    }

    /// <summary>
    /// CLASSIC (2026-10-04): the zone's lines of sight (SightGrid) from the same collision file, built once per zone off
    /// the actor thread; null without collision data. Help offers and dungeon recruiting ask it.
    /// </summary>
    internal static Task<SightGrid> SightFor(string zone)
        => s_sight.GetOrAdd(zone, z => new Lazy<Task<SightGrid>>(() => Task.Run(() => LoadSight(z)))).Value;

    private static readonly ConcurrentDictionary<string, Lazy<Task<SightGrid>>> s_sight = new(StringComparer.OrdinalIgnoreCase);

    private static SightGrid LoadSight(string zone) {
        try {
            if (!ResourceManager.TryLoadArchive(zone, out var wad) || wad.OpenFile(CollisionFile) is not { } bytes) {
                return null;
            }

            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            var (floor, obstacles) = Shapes(Bcd.Parse(stream));
            return SightGrid.Build(floor, obstacles);
        }
        catch (Exception e) {
            Logger.Warning("Ambient wizards in {Zone}: no sight lines ({Error}).", Logger.Args(zone, e.Message));
            return null;
        }
    }

    /// <summary>The floor triangles (Walkable, not water) and solid shapes (Object primitives) of a collision file.</summary>
    internal static (List<NavTriangle> Floor, List<NavObstacle> Obstacles) Shapes(Bcd bcd) {
        var floor = new List<NavTriangle>();
        var obstacles = new List<NavObstacle>();
        foreach (var c in bcd.Collisions) {
            var g = c.Geometry;
            var location = new NumVector3(g.Location[0], g.Location[1], g.Location[2]);
            var scale = g.Scale is > 0 ? g.Scale : 1f;
            if (c.Mesh is { } mesh && c.CategoryFlags.HasFlag(CollisionFlags.Walkable) && !c.CategoryFlags.HasFlag(CollisionFlags.Water)
                && !g.Name.Contains("Water", StringComparison.OrdinalIgnoreCase)) {
                var world = mesh.Vertices.Select(v => Transform(g.Rotation, new NumVector3(v[0], v[1], v[2]) * scale) + location).ToArray();
                foreach (var face in mesh.Faces) {
                    var (a, b, d) = (face.FaceVector[0], face.FaceVector[1], face.FaceVector[2]);
                    if (a < world.Length && b < world.Length && d < world.Length) {
                        floor.Add(new NavTriangle(world[a], world[b], world[d]));
                    }
                }
            }
            else if (c.CategoryFlags.HasFlag(CollisionFlags.Object)) {
                switch (g.Params) {
                    case Bcg.BoxGeomParams box:
                        obstacles.Add(NavObstacle.Box(location, g.Rotation, new NumVector3(box.Length, box.Width, box.Depth) * scale));
                        break;
                    case Bcg.CylinderGeomParams cylinder:
                        // Stored as (Radius, Length) but the numbers are (length, radius): checked against zone.nav.
                        obstacles.Add(NavObstacle.Cylinder(location, g.Rotation, cylinder.Length * scale, cylinder.Radius * scale));
                        break;
                    case Bcg.TubeGeomParams tube:
                        obstacles.Add(NavObstacle.Cylinder(location, g.Rotation, tube.Length * scale, tube.Radius * scale));
                        break;
                    case Bcg.SphereGeomParams sphere:
                        obstacles.Add(NavObstacle.Cylinder(location, g.Rotation, sphere.Radius * scale, sphere.Radius * 2 * scale));
                        break;
                }
            }
        }

        return (floor, obstacles);
    }

    private static NumVector3 Transform(float[,] r, NumVector3 v)
        => new(r[0, 0] * v.X + r[0, 1] * v.Y + r[0, 2] * v.Z,
               r[1, 0] * v.X + r[1, 1] * v.Y + r[1, 2] * v.Z,
               r[2, 0] * v.X + r[2, 1] * v.Y + r[2, 2] * v.Z);

}
