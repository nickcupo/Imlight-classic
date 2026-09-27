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
 * CLASSIC SPELL VALUES TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The real classic-data/spells records and rules/accuracy-2009.yaml load,
 * and resolve to the numbers the records and profiles promise.
 *
 * USAGE EXAMPLE:
 * W101C_REQUIRE_CLASSIC_DATA=1 dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 * Skipped when no classic-data directory is found above the test binary,
 * unless W101C_REQUIRE_CLASSIC_DATA=1 (see ClassicDataFixture).
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.IO;
using System.Linq;
using Imlight.Classic.Rules;
using Imlight.Classic.Spells;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class RealSpellsTests {

    private static string SpellsPath => Path.Combine(ClassicDataFixture.Root, "spells");

    private static ClassicSpellBook LoadBook() => ClassicSpellLoader.Load(SpellsPath);

    [Fact]
    public void EveryRecordLoads() {
        var book = LoadBook();
        var files = Directory.GetFiles(SpellsPath, "*.yaml", SearchOption.AllDirectories);

        Assert.Equal(files.Length, book.Records.Length);
        Assert.Equal(ClassicSpellSchema.Schools.Order(), book.Records.Select(record => record.School).Distinct().Order());
    }

    [Fact]
    public void FireCat() {
        var record = LoadBook().FindByTemplate("Spells/Tiered Spells/Fire Cat.xml");

        Assert.NotNull(record);
        Assert.Equal("spell.fire.fire_cat", record.Id);
        Assert.Equal(SpellPips.Of(1), record.Values.Pips);
        Assert.Equal(75, record.Values.AccuracyPercent);
        Assert.Equal(new SpellEffectValues(SpellEffectKind.Damage, "fire", 80, 120, null, null, SpellTargets.Single, null),
            Assert.Single(record.Values.Effects));
    }

    [Fact]
    public void BalefrostHasItsPreUpdateNumbersInArc1() {
        var record = LoadBook().FindByName("Balefrost")!;
        var late = record.ValuesFor(ClassicDataFixture.LoadProfile("late-2009").Lineage);
        var arc1 = record.ValuesFor(ClassicDataFixture.LoadProfile("arc1-2009h1").Lineage);

        Assert.Equal(SpellPips.Of(2), late.Pips);
        Assert.Equal(35, Assert.Single(late.Effects).Percent);
        Assert.Equal(SpellPips.Of(4), arc1.Pips);
        Assert.Equal(25, Assert.Single(arc1.Effects).Percent);
    }

    [Fact]
    public void TauntIsAnXCardInArc1Only() {
        var record = LoadBook().FindByName("Taunt")!;

        Assert.Equal(SpellPips.Of(2), record.ValuesFor(ClassicDataFixture.LoadProfile("late-2009").Lineage).Pips);
        Assert.True(record.ValuesFor(ClassicDataFixture.LoadProfile("arc1-2009h1").Lineage).Pips.IsX);
    }

    [Fact]
    public void EveryNamedProfileExistsAndTheCanonicalOneIsTheSchemas() {
        var profiles = ClassicProfileLoader.LoadAll(ClassicDataFixture.ProfilesPath);
        var ids = profiles.Select(profile => profile.Id).ToHashSet();

        Assert.Equal(ClassicSpellSchema.CanonicalProfileId, Assert.Single(profiles, profile => profile.Status == ProfileStatus.Canonical).Id);
        foreach (var record in LoadBook().Records) {
            Assert.All(record.Profiles, id => Assert.Contains(id, ids));
            Assert.Contains(ClassicSpellSchema.CanonicalProfileId, record.Profiles);
        }
    }

    [Fact]
    public void TheCanonicalAccuracyTableLoads() {
        var profile = ClassicDataFixture.LoadProfile("late-2009");
        var table = AccuracyTableLoader.Load(Path.Combine(ClassicDataFixture.Root, profile.Rules.AccuracyTable!));

        Assert.Equal("accuracy-2009", table.Id);
        Assert.Contains("late-2009", table.Profiles);
        Assert.Equal(0.75, table.BaseAccuracy("fire", profile.Lineage));
        Assert.Equal(0.9, table.BaseAccuracy("life", profile.Lineage));
        Assert.All(ClassicSpellSchema.Schools, school => Assert.NotNull(table.BaseAccuracy(school, profile.Lineage)));
    }

    [Fact]
    public void ArcOneLineageListsItsParent() {
        Assert.Equal(new[] { "arc1-2009h1", "late-2009" }, ClassicDataFixture.LoadProfile("arc1-2009h1").Lineage);
        Assert.Equal(new[] { "late-2009" }, ClassicDataFixture.LoadProfile("late-2009").Lineage);
    }

}
