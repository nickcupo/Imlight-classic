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
 * float) along Z.
 *
 * Created by: Nick with Claude Code (claude-sonnet-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System;
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

}
