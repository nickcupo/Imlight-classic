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
 * CLASSIC PLAYER HEALTH TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Pins the 2009 base health table (progression/health-2009.yaml) and its
 * loader's checks.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System.IO;
using Imlight.Classic.Rules;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class HealthTableTests {

    private static HealthTable Real() => HealthTableLoader.Load(Path.Combine(ClassicDataFixture.Root, "progression", "health-2009.yaml"));

    [Fact]
    public void TheCanonicalProfilesNameTheHealthTable() {
        Assert.Equal("progression/health-2009.yaml", ClassicDataFixture.LoadProfile("late-2009").Rules.PlayerHealth);
        Assert.Equal("progression/health-2009.yaml", ClassicDataFixture.LoadProfile("arc1-2009h1").Rules.PlayerHealth);
        Assert.Null(ClassicDataFixture.LoadProfile("dev-unrestricted").Rules.PlayerHealth);
    }

    [Fact]
    public void TheTableHasThe2009Values() {
        var table = Real();
        Assert.Equal(50, table.MaxLevel);
        Assert.Equal(793, table.HealthOf("Death", 15));   // the wiki's 2009-01-25 character page screenshot (r806919: 828)
        Assert.Equal(1800, table.HealthOf("Life", 50));   // r806919: 1980
        Assert.Equal(1200, table.HealthOf("Storm", 50));  // r806919: 1233
        Assert.Equal(415, table.HealthOf("fire", 1));
        Assert.Equal(2025, table.HealthOf("ICE", 50));
        Assert.Null(table.HealthOf("Shadow", 10));
        Assert.Null(table.HealthOf("Life", 51));
        Assert.Null(table.HealthOf("Life", 0));
    }

    [Fact]
    public void EverySchoolGrowsWithLevel() {
        var table = Real();
        foreach (var school in HealthTable.Schools) {
            for (var level = 2; level <= table.MaxLevel; level++) {
                Assert.True(table.HealthOf(school, level) > table.HealthOf(school, level - 1), $"{school} {level}");
            }
        }
    }

    [Fact]
    public void ALevelOutOfOrderOrAMissingSchoolIsRejected() {
        var dir = Directory.CreateTempSubdirectory("w101c-health-");
        try {
            var path = Path.Combine(dir.FullName, "health-test.yaml");
            File.WriteAllText(path, """
                id: health-test
                profiles: [late-2009]
                max_level: 2
                license_tag: own
                provenance:
                - {source: test, source_date: '2010-05-15', retrieved: '2026-10-04', covers: [levels], confidence: corroborated}
                levels:
                - {level: 2, fire: 1, ice: 1, storm: 1, myth: 1, life: 1, death: 1, balance: 1}
                - {level: 1, fire: 1, ice: 1, storm: 1, myth: 1, life: 1, death: 1}
                """);

            var error = Assert.Throws<ClassicDataException>(() => HealthTableLoader.Load(path));
            Assert.Contains("out of order", error.Message);
            Assert.Contains("balance", error.Message);
        }
        finally {
            dir.Delete(true);
        }
    }

}
