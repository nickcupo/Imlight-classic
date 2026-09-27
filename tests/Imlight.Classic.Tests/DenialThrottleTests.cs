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
 * CLASSIC RULES TESTS
 * ========================================================================
 * 
 * PURPOSE:
 * DenialThrottle collapses repeats of one (character, subject) inside its
 * window and nothing else.
 * 
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class DenialThrottleTests {

    private readonly ManualClock _clock = new();

    [Fact]
    public void RepeatInsideTheWindowIsSuppressed() {
        var throttle = new DenialThrottle(_clock);

        Assert.True(throttle.ShouldReport(7, "Celestia/CL_Hub"));
        _clock.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(throttle.ShouldReport(7, "Celestia/CL_Hub"));
    }

    [Fact]
    public void RepeatAfterTheWindowIsReported() {
        var throttle = new DenialThrottle(_clock);

        Assert.True(throttle.ShouldReport(7, "Celestia/CL_Hub"));
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(throttle.ShouldReport(7, "Celestia/CL_Hub"));
        Assert.False(throttle.ShouldReport(7, "Celestia/CL_Hub"));
    }

    [Fact]
    public void OtherZoneOrCharacterIsReported() {
        var throttle = new DenialThrottle(_clock);

        Assert.True(throttle.ShouldReport(7, "Celestia/CL_Hub"));
        Assert.True(throttle.ShouldReport(7, "Test/X"));
        Assert.True(throttle.ShouldReport(8, "Celestia/CL_Hub"));
    }

    [Fact]
    public void ZoneComparisonIgnoresCase() {
        var throttle = new DenialThrottle(_clock);

        Assert.True(throttle.ShouldReport(7, "Celestia/CL_Hub"));
        Assert.False(throttle.ShouldReport(7, "celestia/cl_hub"));
    }

    [Fact]
    public void CustomWindow() {
        var throttle = new DenialThrottle(_clock, TimeSpan.FromSeconds(10));

        Assert.True(throttle.ShouldReport(1, "Z"));
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(throttle.ShouldReport(1, "Z"));
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(throttle.ShouldReport(1, "Z"));
    }

    private sealed class ManualClock : TimeProvider {

        private DateTimeOffset _now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;

    }

}
