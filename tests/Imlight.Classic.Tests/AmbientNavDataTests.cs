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
 * AMBIENT NAV DATA TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: on a real zone's collision.bcd (a local copy named by the
 * W101C_NAV_BCD environment variable; the test does nothing without it),
 * routes between many open spots never leave open ground. With
 * W101C_NAV_OUT set, the grid and routes are written there as JSON for a
 * plot against the raw collision.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/03/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Imcodec.BCD;
using Imlight.Classic.Ambient;
using Imlight.CoreLib.Classic.Ambient;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class AmbientNavDataTests {

    [Fact]
    public void RealZoneRoutesStayOnOpenGround() {
        var path = Environment.GetEnvironmentVariable("W101C_NAV_BCD");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
            return;
        }

        var grid = AmbientNav.Build(Bcd.ParseFromFile(path));
        var open = grid.OpenCells().ToList();
        Assert.NotEmpty(open);

        var rng = new Random(7);
        var spots = Enumerable.Range(0, 400).Select(_ => open[rng.Next(open.Count)])
            .Select(c => new Vector3(grid.OriginX + (c.X + 0.5f) * NavGrid.CellSize, grid.OriginY + (c.Y + 0.5f) * NavGrid.CellSize, c.Z))
            .ToList();
        var routes = new List<object>();
        var made = 0;
        for (var i = 0; i + 1 < spots.Count && made < 60; i += 2) {
            if (!grid.Connected(spots[i], spots[i + 1]) || !grid.TryRoute(spots[i], spots[i + 1], out var route)) {
                continue;
            }

            var at = spots[i];
            foreach (var next in route) {
                Assert.True(grid.SegmentClear(at, next), $"leg {at} -> {next} leaves open ground");
                at = next;
            }

            made++;
            routes.Add(new[] { spots[i] }.Concat(route).Select(p => new[] { p.X, p.Y, p.Z }).ToArray());
        }

        Assert.True(made >= 20, $"only {made} routes");
        if (Environment.GetEnvironmentVariable("W101C_NAV_OUT") is { Length: > 0 } output) {
            File.WriteAllText(output, JsonSerializer.Serialize(new {
                originX = grid.OriginX, originY = grid.OriginY, cell = NavGrid.CellSize, width = grid.Width, height = grid.Height,
                open = open.Select(c => new[] { c.X, c.Y }).ToArray(),
                blocked = grid.BlockedCells().Select(c => new[] { c.X, c.Y }).ToArray(),
                routes,
            }));
        }
    }

}
