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
 * PLAYER DATA SCHEMA AND GAME CLOCK TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The start-up schema check (an older build refuses newer data) and the
 * game's calendar day in a configured time zone.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using Imlight.Classic.Admin;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class PlayerDataSchemaTests {

    [Fact]
    public void AnOlderBuildRefusesNewerData() {
        Assert.Equal(SchemaVerdict.TooNew, PlayerDataSchema.Check(PlayerDataSchema.Current + 1));
        Assert.Equal(SchemaVerdict.TooNew, PlayerDataSchema.Check(3, build: 2));
        Assert.Null(PlayerDataSchema.ToWrite(SchemaVerdict.TooNew)); // never lowers the marker
        Assert.Contains("will not start", PlayerDataSchema.Describe(SchemaVerdict.TooNew, 3, build: 2));
    }

    [Fact]
    public void SameOrOlderDataStartsAndIsMarked() {
        Assert.Equal(SchemaVerdict.Same, PlayerDataSchema.Check(PlayerDataSchema.Current));
        Assert.Null(PlayerDataSchema.ToWrite(SchemaVerdict.Same));

        Assert.Equal(SchemaVerdict.Upgrade, PlayerDataSchema.Check(1, build: 2));
        Assert.Equal(2, PlayerDataSchema.ToWrite(SchemaVerdict.Upgrade, build: 2));

        // A database from before the marker (live today) or a new one: marked with this build's version.
        Assert.Equal(SchemaVerdict.Unmarked, PlayerDataSchema.Check(null));
        Assert.Equal(PlayerDataSchema.Current, PlayerDataSchema.ToWrite(SchemaVerdict.Unmarked));
    }

    [Fact]
    public void TheBankBuildIsVersionTwoAndTheRefusalIsNotRestarted() {
        Assert.True(PlayerDataSchema.Current >= 2);
        Assert.Equal(78, PlayerDataSchema.RefusedExitCode); // RestartPreventExitStatus=78 in w101c-imlight.service
        Assert.Equal("Meta/PlayerDataSchema", PlayerDataSchema.DocumentId);
    }

    [Fact]
    public void TheGameDayFollowsTheConfiguredZone() {
        var newYork = GameClock.Resolve("America/New_York", out var error);
        Assert.Null(error);

        // 8 pm EDT on Oct 30 is already Oct 31 in UTC: Halloween starts at midnight New York time, not then.
        var evening = new DateTime(2026, 10, 31, 0, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2026, 10, 30), GameClock.DayOf(evening, newYork));
        Assert.Equal(new DateOnly(2026, 10, 31), GameClock.DayOf(evening, TimeZoneInfo.Utc));
        Assert.Equal(new DateOnly(2026, 10, 31), GameClock.DayOf(new DateTime(2026, 10, 31, 4, 0, 0, DateTimeKind.Utc), newYork));

        // Winter (EST, UTC-5): the day turns at 05:00 UTC.
        Assert.Equal(new DateOnly(2026, 12, 24), GameClock.DayOf(new DateTime(2026, 12, 25, 4, 59, 0, DateTimeKind.Utc), newYork));
        Assert.Equal(new DateOnly(2026, 12, 25), GameClock.DayOf(new DateTime(2026, 12, 25, 5, 0, 0, DateTimeKind.Utc), newYork));
    }

    [Fact]
    public void AnEmptyOrUnknownZoneFallsBack() {
        Assert.Same(TimeZoneInfo.Utc, GameClock.Resolve("", out var none, TimeZoneInfo.Utc));
        Assert.Null(none);
        Assert.Same(TimeZoneInfo.Utc, GameClock.Resolve("Mars/Olympus_Mons", out var error, TimeZoneInfo.Utc));
        Assert.NotNull(error);
    }

}
