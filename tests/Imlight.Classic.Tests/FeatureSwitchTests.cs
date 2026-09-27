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
 * Feature switches of the real profiles, the "absent means enabled" rule
 * and the refusal texts.
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
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Zones;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class FeatureSwitchTests {

    [Fact]
    public void Late2009Switches() {
        var rules = ClassicDataFixture.RealRules("late-2009");

        Assert.False(rules.IsFeatureEnabled(ClassicFeatures.CriticalAndBlock));
        Assert.True(rules.IsFeatureEnabled(ClassicFeatures.Bazaar));
        Assert.False(rules.IsFeatureEnabled(ClassicFeatures.PetsEnergy));
        Assert.False(rules.IsFeatureEnabled(ClassicFeatures.Jewels));
        Assert.True(rules.IsFeatureEnabled(ClassicFeatures.Seamstress));
        Assert.False(rules.CriticalAndBlockEnabled);
    }

    [Fact]
    public void Arc1Switches() {
        var rules = ClassicDataFixture.RealRules("arc1-2009h1");

        Assert.False(rules.IsFeatureEnabled(ClassicFeatures.Bazaar));
        Assert.False(rules.IsFeatureEnabled(ClassicFeatures.Seamstress));
        Assert.True(rules.IsFeatureEnabled(ClassicFeatures.Housing));
        Assert.False(rules.CriticalAndBlockEnabled);
    }

    [Fact]
    public void AbsentSwitchIsEnabled() {
        var switches = new FeatureSwitches(new Dictionary<string, bool> { [ClassicFeatures.Bazaar] = false });

        Assert.True(switches.IsEnabled(ClassicFeatures.Fishing));
        Assert.False(switches.IsEnabled(ClassicFeatures.Bazaar));
        Assert.False(switches.AllEnabled);
        Assert.Equal(new[] { ClassicFeatures.Bazaar }, switches.Disabled.ToArray());
        Assert.True(FeatureSwitches.None.AllEnabled);
    }

    [Fact]
    public void UnknownPathThrows() {
        Assert.Throws<ArgumentException>(() => FeatureSwitches.None.IsEnabled("teleportation"));
        Assert.Throws<ArgumentException>(() => ClassicRules.Stock.IsFeatureEnabled("pets"));
        Assert.Throws<ArgumentException>(() => new FeatureSwitches(new Dictionary<string, bool> { ["nope"] = true }));
    }

    [Fact]
    public void CriticalAndBlockOnDevUnrestricted() {
        Assert.True(ClassicDataFixture.RealRules("dev-unrestricted").CriticalAndBlockEnabled);
        Assert.True(new ClassicRules(ZoneFixture.Profile(), ZoneWorldMap.Empty).CriticalAndBlockEnabled);
    }

    [Fact]
    public void RefusalTexts() {
        Assert.Equal("Your dorm room isn't available yet.", ClassicMessages.FeatureUnavailable(ClassicFeatures.Housing));
        Assert.Equal("The Bazaar isn't open yet.", ClassicMessages.FeatureUnavailable(ClassicFeatures.Bazaar));
        Assert.Equal("The Seamstress isn't open yet.", ClassicMessages.FeatureUnavailable(ClassicFeatures.Seamstress));
        Assert.Equal("Jewels can't be socketed yet.", ClassicMessages.FeatureUnavailable(ClassicFeatures.Jewels));
        Assert.Equal("That isn't available yet.", ClassicMessages.FeatureUnavailable(ClassicFeatures.Mounts));
        Assert.Equal("Celestia isn't open yet.", ClassicMessages.WorldClosed("Celestia"));
    }

}
