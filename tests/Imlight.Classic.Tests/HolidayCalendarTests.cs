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
 * HOLIDAY CALENDAR TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The 2009 holiday calendar: the real data file loads, each event runs on
 * the right dates (Christmas wrapping into January, Easter moving with
 * the computus), and the Hallowe'en Towers belong to Hallowe'en.
 *
 * NOTE:
 * The live path (date override, decorations, vendors, tower gate, login
 * fallback) was run on the rig; see playbot-reports/features.md.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.IO;
using System.Linq;
using Imlight.Classic.Holidays;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class HolidayCalendarTests {

    private static HolidayCalendar Real()
        => HolidayCalendarLoader.Load(Path.Combine(ClassicDataFixture.Root, "holidays", "holidays-2009.yaml"));

    private static string[] ActiveIds(HolidayCalendar calendar, int year, int month, int day)
        => [.. calendar.ActiveOn(new DateOnly(year, month, day)).Select(holiday => holiday.Id)];

    [Theory]
    [InlineData(2009, 4, 12)]
    [InlineData(2010, 4, 4)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    public void WesternEasterIsRight(int year, int month, int day)
        => Assert.Equal(new DateOnly(year, month, day), HolidayCalendar.WesternEaster(year));

    [Fact]
    public void RealCalendarLoadsEveryEvent() {
        var calendar = Real();
        Assert.Equal(["valentines", "st_patricks", "spring", "halloween", "christmas"], calendar.Events.Select(e => e.Id).ToArray());
        Assert.All(calendar.Events, holiday => Assert.NotEmpty(holiday.Vendors));
        var spooky = calendar.Events.Single(e => e.Id == "halloween").Vendors.Single();
        Assert.Equal(126413UL, spooky.Npc);
        Assert.Contains(spooky.Items, item => item.Name == "Cool Pumpkin Mask" && item.Crowns == 750);
    }

    [Fact]
    public void EventsRunOnTheirDates() {
        var calendar = Real();
        Assert.Equal(["halloween"], ActiveIds(calendar, 2009, 10, 15));
        Assert.Equal(["christmas"], ActiveIds(calendar, 2009, 12, 25));
        Assert.Equal(["christmas"], ActiveIds(calendar, 2010, 1, 3));   // wraps into January
        Assert.Empty(ActiveIds(calendar, 2010, 1, 10));
        Assert.Equal(["valentines"], ActiveIds(calendar, 2010, 2, 14));
        Assert.Equal(["st_patricks"], ActiveIds(calendar, 2010, 3, 19));
        Assert.Equal(["spring"], ActiveIds(calendar, 2010, 4, 4));      // Easter 2010
        Assert.Equal(["spring"], ActiveIds(calendar, 2009, 4, 13));     // Eggbert's day in 2009
        Assert.Empty(ActiveIds(calendar, 2009, 7, 4));
    }

    [Fact]
    public void TowersBelongToHalloween() {
        var calendar = Real();
        Assert.Equal("halloween", calendar.EventForZone("Holiday/Halloween/Gauntlet_Easy/Level1")?.Id);
        Assert.Null(calendar.EventForZone("Holiday/HalloweenX/Level1"));
        Assert.Null(calendar.EventForZone("WizardCity/WC_Ravenwood"));
    }

    [Fact]
    public void BossDropsAreHalloweensOnly() {
        var halloween = Real().Events.Single(e => e.Id == "halloween");
        var pumpkinHead = halloween.Drops.Single(drop => drop.Template == 126423);
        Assert.Contains(97582UL, pumpkinHead.Items);
        Assert.Equal([708781150UL], pumpkinHead.TreasureCards.ToArray());
    }

    [Fact]
    public void BadCalendarIsRefusedWithEveryError() {
        var dir = Path.Combine(Path.GetTempPath(), "holidays-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var path = Path.Combine(dir, "holidays-bad.yaml");
            File.WriteAllText(path, """
                id: holidays-bad
                profiles: [late-2009]
                provenance: [{source: x, source_date: '2009-10-01', retrieved: '2026-10-01', covers: [dates], confidence: inferred}]
                license_tag: own
                events:
                - {id: a, name: A, registry: Halloween, start: '02-30', end: '03-01', confidence: inferred, source: 0}
                - {id: b, name: B, registry: Nope, confidence: inferred, source: 0}
                """);
            var ex = Assert.Throws<ClassicDataException>(() => HolidayCalendarLoader.Load(path));
            Assert.Contains("02-30", ex.Message);
            Assert.Contains("start and end, or easter", ex.Message);
        }
        finally {
            Directory.Delete(dir, true);
        }
    }

}
