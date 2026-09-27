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
 * The real profiles in classic-data/profiles load and mean what the plan
 * says: late-2009, arc1-2009h1 (inheriting from late-2009) and
 * dev-unrestricted.
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
using System.IO;
using System.Linq;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class RealProfilesTests {

    [Fact]
    public void Late2009() {
        var profile = ClassicDataFixture.LoadProfile("late-2009");

        Assert.Equal(ProfileStatus.Canonical, profile.Status);
        Assert.Equal(new DateOnly(2010, 5, 25), profile.Cutoff);
        Assert.Equal(50, profile.LevelCap);
        Assert.Equal(new[] { "wizard_city", "krokotopia", "marleybone", "mooshu", "dragonspyre", "grizzleheim" },
            profile.Worlds!.Value);
        Assert.False(profile.Features.IsEnabled(ClassicFeatures.CriticalAndBlock));
        Assert.False(profile.Features.IsEnabled(ClassicFeatures.PetsEnergy));
        Assert.True(profile.Features.IsEnabled(ClassicFeatures.Henchmen));
        Assert.Equal("progression/xp-2009.yaml", profile.Rules.XpTable);
        Assert.Equal("rules/accuracy-2009.yaml", profile.Rules.AccuracyTable);
        Assert.Equal("magus", profile.Rules.PowerPipsFromRank);
        Assert.False(profile.IsUnrestricted);
        Assert.NotNull(profile.Description);
        Assert.NotEmpty(profile.Notes);
    }

    [Fact]
    public void Arc1InheritsFromLate2009() {
        var profile = ClassicDataFixture.LoadProfile("arc1-2009h1");

        Assert.Equal(ProfileStatus.Optional, profile.Status);
        Assert.Equal("late-2009", profile.Extends);
        Assert.Equal(new DateOnly(2009, 6, 30), profile.Cutoff);
        Assert.Equal(new[] { "wizard_city", "krokotopia", "marleybone", "mooshu", "dragonspyre" }, profile.Worlds!.Value);
        Assert.Equal(50, profile.LevelCap);
        foreach (var feature in new[] {
                     ClassicFeatures.Crafting, ClassicFeatures.Bazaar, ClassicFeatures.Mounts, ClassicFeatures.Henchmen,
                     ClassicFeatures.Elixirs, ClassicFeatures.Seamstress, ClassicFeatures.HubTeleporters,
                 }) {
            Assert.False(profile.Features.IsEnabled(feature), feature);
        }

        Assert.True(profile.Features.IsEnabled(ClassicFeatures.Housing));
        Assert.False(profile.Features.IsEnabled(ClassicFeatures.PetsEnergy));
        Assert.False(profile.Features.IsEnabled(ClassicFeatures.CriticalAndBlock));
        Assert.Equal(new[] { "arc1-2009h1", "late-2009" },
            profile.SourceFiles.Select(Path.GetFileNameWithoutExtension).ToArray());
        Assert.Null(profile.Description);
        Assert.Empty(profile.Notes);
    }

    [Fact]
    public void DevUnrestrictedRestrictsNothing() {
        var profile = ClassicDataFixture.LoadProfile("dev-unrestricted");

        Assert.Equal(ProfileStatus.Debug, profile.Status);
        Assert.Null(profile.Cutoff);
        Assert.Null(profile.LevelCap);
        Assert.Null(profile.Worlds);
        Assert.True(profile.Features.AllEnabled);
        Assert.True(profile.IsUnrestricted);
    }

    [Fact]
    public void LoadAllLoadsEveryFileAndExactlyOneIsCanonical() {
        var profiles = ClassicProfileLoader.LoadAll(ClassicDataFixture.ProfilesPath);
        var files = Directory.GetFiles(ClassicDataFixture.ProfilesPath, "*.yaml");

        Assert.Equal(files.Length, profiles.Length);
        Assert.Equal("late-2009", Assert.Single(profiles, profile => profile.Status == ProfileStatus.Canonical).Id);
    }

    [Fact]
    public void DescribeSummarisesTheProfile() {
        var summary = ClassicDataFixture.LoadProfile("late-2009").Describe();

        Assert.StartsWith("late-2009 (canonical): cutoff 2010-05-25, level cap 50, worlds wizard_city,", summary);
        Assert.Contains("critical_and_block", summary);
    }

}
