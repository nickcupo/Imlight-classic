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
 * WORLD BOUNDS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: positions no zone can contain. The widest placed object in any r806919 zone is 84788 from the origin
 * (MS_Death_Zone2_HauntedVillage); the client's move wire format (int16 x 4) ends at 131068. A wizard saved beyond
 * MaxCoordinate could not walk back after the next login, so such a position is never accepted from a client and a
 * saved one is replaced by the zone's "Start" location.
 *
 * Created by: Nick with Claude Code (claude-sonnet-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/13/2026
 */

using System;

namespace Imlight.Classic.Security;

public static class WorldBounds {

    /// <summary>No zone reaches this far from the origin on x or y (z is bounded by the same figure).</summary>
    public const double MaxCoordinate = 100000;

    /// <summary>The zone's own named start location, used when a saved position is outside every possible zone.</summary>
    public const string StartLocation = "Start";

    public static bool Outside(double x, double y, double z)
        => !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)
            || Math.Abs(x) > MaxCoordinate || Math.Abs(y) > MaxCoordinate || Math.Abs(z) > MaxCoordinate;

}
