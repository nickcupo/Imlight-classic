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
 * SCHOOL PRIDE TESTS (experiments.schoolflavor)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-20, design-flavor): the switch is off in every real
 * profile and turns on from the dashboard; the greeting pools, the greeter
 * choice, the school titles and the Ravenwood tree spots.
 *
 * Created by: Nick with Claude Code (claude-fable-5-1)
 * Version: KALI 1.0
 * Last Updated: 10/20/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Ambient;
using Imlight.Classic.Settings;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.CoreLib.Shared.Behaviors;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class SchoolPrideTests {

    [Theory]
    [InlineData("late-2009")]
    [InlineData("october-2010-arc1")]
    [InlineData("celestia-2010")]
    [InlineData("wintertusk-2011")]
    [InlineData("dev-unrestricted")]
    public void OffInEveryRealProfile(string profile) {
        var rules = ClassicDataFixture.RealRules(profile);
        Assert.False(rules.IsExperimentEnabled(ClassicExperiments.SchoolFlavor));
        Assert.Equal(profile == "dev-unrestricted", !rules.IsRestricted);
    }

    [Fact]
    public void TheDashboardSwitchTurnsItOn() {
        var rules = ClassicDataFixture.RealRules("october-2010-arc1");
        rules.OwnerExtraExperiments = () => new HashSet<string> { ClassicExperiments.SchoolFlavor };
        Assert.True(rules.IsExperimentEnabled(ClassicExperiments.SchoolFlavor));
        Assert.Throws<ArgumentException>(() => rules.IsExperimentEnabled("jetpacks"));
    }

    [Fact]
    public void TheDashboardSwitchDefaultsToOff() {
        var store = new ClassicSettingsStore(_ => null, null);
        Assert.False(store.Bool(ClassicSettingKeys.SchoolFlavor));
    }

    [Fact]
    public void EverySchoolHasATitleAndLines() {
        foreach (var school in Enum.GetValues<AmbientSchool>()) {
            Assert.Contains(SchoolPride.Title(school), new[] { "pyromancer", "thaumaturge", "diviner", "conjurer", "theurgist", "necromancer", "sorcerer" });
            Assert.True(SchoolPride.SameSchool[school].Length >= 5, school.ToString());
            Assert.All(SchoolPride.SameSchool[school], line => Assert.DoesNotContain("{you}", line));
        }

        Assert.Equal(7, Enum.GetValues<AmbientSchool>().Select(SchoolPride.Title).Distinct().Count());
        Assert.Equal(7, Enum.GetValues<AmbientSchool>().Select(SchoolPride.TreeTemplate).Distinct().Count());
    }

    [Fact]
    public void SameSchoolGetsPrideOtherSchoolsGetATeaseOrNothing() {
        var rng = new Random(2009);
        Assert.Same(SchoolPride.SameSchool[AmbientSchool.Fire], SchoolPride.Greetings(AmbientSchool.Fire, AmbientSchool.Fire, rng));

        var teased = 0;
        for (var i = 0; i < 200; i++) {
            var pool = SchoolPride.Greetings(AmbientSchool.Storm, AmbientSchool.Ice, rng);
            if (pool is null) {
                continue;
            }

            teased++;
            Assert.All(pool, line => {
                Assert.DoesNotContain("{you}", line);
                Assert.DoesNotContain("{title}", line);
            });
            Assert.Contains(pool, line => line.Contains("ice", StringComparison.Ordinal) || line.Contains("thaumaturge", StringComparison.Ordinal));
        }

        Assert.InRange(teased, 60, 140);
    }

    [Fact]
    public void FillUsesThePlayersSchoolAndTitle() {
        Assert.Equal("myth? ew jk", SchoolPride.Fill("{you}? ew jk", AmbientSchool.Myth));
        Assert.Equal("hi conjurer", SchoolPride.Fill("hi {title}", AmbientSchool.Myth));
        Assert.Equal("{school} is better than death", SchoolPride.Fill("{school} is better than {you}", AmbientSchool.Death));
    }

    [Fact]
    public void ASameSchoolWizardGreetsBeforeANearerOne() {
        var near = new List<(string, AmbientSchool, float)> { ("ice-near", AmbientSchool.Ice, 100), ("fire-far", AmbientSchool.Fire, 400) };
        Assert.Equal("fire-far", SchoolPride.PickGreeter(near, AmbientSchool.Fire));
        Assert.Equal("ice-near", SchoolPride.PickGreeter(near, AmbientSchool.Life));
        Assert.Null(SchoolPride.PickGreeter(new List<(string, AmbientSchool, float)>(), AmbientSchool.Life));
    }

    [Fact]
    public void ThePlayersSchoolOfFocusMapsToAnAmbientSchool() {
        Assert.Equal(AmbientSchool.Fire, AmbientWizards.SchoolFrom(MagicSchool.Fire));
        Assert.Equal(AmbientSchool.Balance, AmbientWizards.SchoolFrom(MagicSchool.Balance));
        Assert.Null(AmbientWizards.SchoolFrom(MagicSchool.None));
        Assert.Null(AmbientWizards.SchoolFrom(MagicSchool.Moon));
        foreach (var school in Enum.GetValues<AmbientSchool>()) {
            Assert.Equal(school, AmbientWizards.SchoolFrom(AmbientWizards.SchoolOf(school)));
        }
    }

}
