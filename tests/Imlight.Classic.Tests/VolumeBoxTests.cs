/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * AGPL-3.0-or-later; see the LICENSE file.
 *
 * ========================================================================
 * CLASSIC BOX VOLUME TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Box volumes (MB_Prison "Meowiarty's Cell" and 283 more in the r806919 zones) only fired at their exact centre.
 *
 * NOTE:
 * Cases are the real MB_Prison and MB_Ironworks_T1 volumes: width along X, length along Y, height (unknown_int as a
 * float) along Z. Turned boxes (2026-10-08): the box's local frame is the world turned by its yaw (m_orientation.Z),
 * checked against the r806919 collision floors (door-volumes report). Cases are the real Catacombs Crypt01, MB_Z02
 * Prison and MS_CAT_TempleOfPurrfectHarmony boxes and the Unicorn Way arena door sphere.
 *
 * Created by: Nick with Claude Code (claude-sonnet-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Zone.Components;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class VolumeBoxTests {

    private static Volume Cell() => new() {
        m_primitiveType = "Box", m_radius = 0, m_length = 275.6091f, m_width = 2640.86f,
        unknown_int = BitConverter.SingleToInt32Bits(1861.218f),
    };

    [Fact]
    public void BoxVolumeContainsAPointInsideItsExtent() {
        // Malone's cell: the box is 2640 wide (X) and 275 long (Y) around (-2112, 29428, -589).
        Assert.True(VolumeBounds.Contains(Cell(), -2112, 29428, -589, -3203, 29524, -240));
    }

    [Fact]
    public void BoxVolumeExcludesPointsOutsideAnyAxis() {
        Assert.False(VolumeBounds.Contains(Cell(), -2112, 29428, -589, -2112, 29800, -240));
        Assert.False(VolumeBounds.Contains(Cell(), -2112, 29428, -589, -5000, 29428, -240));
        Assert.False(VolumeBounds.Contains(Cell(), -2112, 29428, -589, -2112, 29428, 500));
    }

    [Fact]
    public void SphereVolumeStillUsesItsRadius() {
        var sphere = new Volume { m_primitiveType = "", m_radius = 100 };

        Assert.True(VolumeBounds.Contains(sphere, 0, 0, 0, 60, 60, 0));
        Assert.False(VolumeBounds.Contains(sphere, 0, 0, 0, 80, 80, 0));
    }

    // Crypt01 AV_WC-CATA-ENC-002Goal7 at (-3878.1, 15618.2, 329.9): 426 wide, 966 long, 702 high, turned 89.6 degrees,
    // so its long side runs along world X. Read unturned it ran along Y, into the wall, and missed the room.
    private static Volume CryptGoal7() => new() {
        m_primitiveType = "Box", m_radius = 0, m_width = 426.1276f, m_length = 965.7089f,
        unknown_int = 1143962727, m_orientation = new Vector3(0, 0, 1.563962f),
    };

    [Fact]
    public void TurnedBoxUsesItsYaw() {
        const float cx = -3878.1f, cy = 15618.2f, cz = 329.9f;

        // 400 along world X: inside the turned box (its 966 length), outside the old unturned reading (426 wide).
        Assert.True(VolumeBounds.Contains(CryptGoal7(), cx, cy, cz, cx + 400, cy, cz));
        Assert.True(VolumeBounds.Contains(CryptGoal7(), cx, cy, cz, cx - 400, cy, cz));

        // 400 along world Y: the old reading fired here; the turned box is only 213 deep that way.
        Assert.False(VolumeBounds.Contains(CryptGoal7(), cx, cy, cz, cx, cy + 400, cz));
        Assert.False(VolumeBounds.Contains(CryptGoal7(), cx, cy, cz, cx, cy - 400, cz));

        // Height still applies (702 high).
        Assert.False(VolumeBounds.Contains(CryptGoal7(), cx, cy, cz, cx + 100, cy, cz + 400));
    }

    [Fact]
    public void TurnedBoxTurnsClockwiseLikeGamebryo() {
        // MB_Z02_Prison Volume-Halston: 793 wide, 369 long, yaw +45 degrees. Its width runs along (1, -1): the offset is
        // turned by +yaw into the box's frame. The other sign (or no turn) puts the same point 350 out along the 369 side.
        var halston = new Volume {
            m_primitiveType = "Box", m_radius = 0, m_width = 793.4666f, m_length = 369.3983f,
            unknown_int = 1145462237, m_orientation = new Vector3(2.264645e-08f, 6.327581e-09f, 0.7854096f),
        };
        const float cx = 2154.443f, cy = -5729.056f, cz = 580.5818f;
        var d = 350f / MathF.Sqrt(2);

        Assert.True(VolumeBounds.Contains(halston, cx, cy, cz, cx + d, cy - d, cz));
        Assert.True(VolumeBounds.Contains(halston, cx, cy, cz, cx - d, cy + d, cz));
        Assert.False(VolumeBounds.Contains(halston, cx, cy, cz, cx + d, cy + d, cz));
        Assert.False(VolumeBounds.Contains(halston, cx, cy, cz, cx - d, cy - d, cz));
    }

    [Fact]
    public void UnturnedBoxIsUnchanged() {
        var (x, y) = VolumeBounds.ToLocal(0f, 12f, -34f);

        Assert.Equal(12f, x);
        Assert.Equal(-34f, y);

        // A half turn is the same box.
        var cell = Cell() with { m_orientation = new Vector3(0, 0, MathF.PI) };
        Assert.True(VolumeBounds.Contains(cell, -2112, 29428, -589, -3203, 29524, -240));
        Assert.False(VolumeBounds.Contains(cell, -2112, 29428, -589, -2112, 29800, -240));
    }

    [Fact]
    public void SphereIgnoresItsOrientation() {
        var sphere = new Volume { m_primitiveType = "Sphere", m_radius = 100, m_orientation = new Vector3(0, 0, 1.2f) };

        Assert.True(VolumeBounds.Contains(sphere, 0, 0, 0, 99, 0, 0));
        Assert.True(VolumeBounds.Contains(sphere, 0, 0, 0, 0, 99, 0));
        Assert.False(VolumeBounds.Contains(sphere, 0, 0, 0, 0, 0, 101));
    }

    [Fact]
    public void ArrivalSpotInsideATurnedBoxCountsAsInside() {
        // MS_CAT_TempleOfPurrfectHarmony: the zone's Start lands inside Goal06 (1607 wide, 400 long, turned 93.3 degrees).
        // The component then records the wizard as arrived inside (no fresh enter until they leave and come back); the
        // unturned reading had the Start 261 outside the box.
        var goal06 = new Volume {
            m_primitiveType = "Box", m_radius = 0, m_width = 1607.032f, m_length = 400f,
            unknown_int = BitConverter.SingleToInt32Bits(400f), m_orientation = new Vector3(0, 0, 1.62755f),
        };

        Assert.True(VolumeBounds.Contains(goal06, -68.86356f, 1936.751f, 29.99993f, 14.00391f, 2197.908f, 30.00014f));
        Assert.False(VolumeBounds.Contains(goal06 with { m_orientation = Vector3.Zero },
            -68.86356f, 1936.751f, 29.99993f, 14.00391f, 2197.908f, 30.00014f));
    }

    [Fact]
    public void ArenaDoorSphereFiresWhereItsRadiusReaches() {
        // Unicorn Way 'TeleportVol_Teleport location (Street1 Arena Entrance)': a sphere (no yaw), radius 322.66 at
        // (2924.16, 1638.98, 22.03). The arena facade's collision ('Collision Box 8') starts at x = 2831.5, so the
        // sphere reaches 230 units out into the street. The yaw fix does not move it.
        var door = new Volume {
            m_primitiveType = "", m_radius = 322.6577f, m_orientation = new Vector3(0, 0, 0.7f),
        };
        const float cx = 2924.162f, cy = 1638.975f, cz = 22.028f;

        Assert.True(VolumeBounds.Contains(door, cx, cy, cz, 2602f, cy, cz));
        Assert.False(VolumeBounds.Contains(door, cx, cy, cz, 2600f, cy, cz));
        Assert.True(VolumeBounds.Contains(door, cx, cy, cz, 2831.5f - 40f, cy, cz));
    }

}
