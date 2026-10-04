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
 * CLASSIC SWITCH STORE TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The [Classic] switches: 2009 defaults, ini values, dashboard overrides,
 * validation and the override file.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using Imlight.Classic.Settings;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ClassicSettingsStoreTests : IDisposable {

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"classic-settings-{Guid.NewGuid():N}.json");

    public void Dispose() {
        if (File.Exists(_path)) {
            File.Delete(_path);
        }
    }

    [Fact]
    public void DefaultsAreThe2009Values() {
        var store = new ClassicSettingsStore(_ => null, null);
        Assert.Equal(1.0, store.Double(ClassicSettingKeys.XpMultiplier));
        Assert.Equal(1.0, store.Double(ClassicSettingKeys.GoldMultiplier));
        Assert.Equal(1.0, store.Double(ClassicSettingKeys.DropRateMultiplier));
        Assert.Equal(1.0, store.Double(ClassicSettingKeys.SpellAnimationSpeed));
        Assert.Equal(100, store.Int(ClassicSettingKeys.BankSize));
        Assert.True(store.Bool(ClassicSettingKeys.TeleportToFriendAnywhere));
        Assert.True(store.Bool(ClassicSettingKeys.BazaarStocked));
        Assert.True(store.Bool(ClassicSettingKeys.OpenPvp));
        Assert.Equal("default", store.Resolve(ClassicSettingKeys.XpMultiplier).Source);
    }

    [Fact]
    public void ZoneTransferDelayDefaultsTo250AndAcceptsTheUpstreamSecond() {
        var store = new ClassicSettingsStore(_ => null, null);
        Assert.Equal(250, store.Int(ClassicSettingKeys.ZoneTransferDelayMs));

        var ini = new Dictionary<string, string> { [ClassicSettingKeys.ZoneTransferDelayMs] = "1000" };
        Assert.Equal(1000, new ClassicSettingsStore(key => ini.GetValueOrDefault(key), null).Int(ClassicSettingKeys.ZoneTransferDelayMs));
    }

    [Fact]
    public void IniValueBeatsDefaultAndOverrideBeatsIni() {
        var ini = new Dictionary<string, string> { [ClassicSettingKeys.XpMultiplier] = "2" };
        var store = new ClassicSettingsStore(key => ini.GetValueOrDefault(key), _path);
        Assert.Equal((2.0, "ini"), (store.Double(ClassicSettingKeys.XpMultiplier), store.Resolve(ClassicSettingKeys.XpMultiplier).Source));

        Assert.True(store.TrySet(ClassicSettingKeys.XpMultiplier, "3.5", out var error), error);
        Assert.Equal(3.5, store.Double(ClassicSettingKeys.XpMultiplier));
        Assert.Equal("dashboard", store.Resolve(ClassicSettingKeys.XpMultiplier).Source);

        // The override survives a restart, and clearing it falls back to the ini.
        var reloaded = new ClassicSettingsStore(key => ini.GetValueOrDefault(key), _path);
        Assert.Equal(3.5, reloaded.Double(ClassicSettingKeys.XpMultiplier));
        Assert.True(reloaded.TrySet(ClassicSettingKeys.XpMultiplier, "", out _));
        Assert.Equal(2.0, reloaded.Double(ClassicSettingKeys.XpMultiplier));
    }

    [Theory]
    [InlineData(ClassicSettingKeys.XpMultiplier, "-1")]
    [InlineData(ClassicSettingKeys.XpMultiplier, "lots")]
    [InlineData(ClassicSettingKeys.OpenPvp, "yes")]
    [InlineData(ClassicSettingKeys.BackpackSize, "2.5")]
    [InlineData(ClassicSettingKeys.SpellAnimationSpeed, "10")]
    [InlineData("NoSuchSwitch", "1")]
    public void BadValuesAreRefused(string key, string value) {
        var store = new ClassicSettingsStore(_ => null, null);
        Assert.False(store.TrySet(key, value, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void BadIniValueFallsBackToDefault() {
        var store = new ClassicSettingsStore(key => key == ClassicSettingKeys.GoldMultiplier ? "x" : null, null);
        Assert.Equal((1.0, "default"), (store.Double(ClassicSettingKeys.GoldMultiplier), store.Resolve(ClassicSettingKeys.GoldMultiplier).Source));
    }

    [Fact]
    public void BrokenOverrideFileIsReportedNotFatal() {
        File.WriteAllText(_path, "{ not json");
        var store = new ClassicSettingsStore(_ => null, _path);
        Assert.NotEmpty(store.Warnings);
        Assert.Equal(1.0, store.Double(ClassicSettingKeys.XpMultiplier));
    }

    [Fact]
    public void EverySwitchDefaultIsValid() {
        foreach (var definition in ClassicSettingsStore.Definitions) {
            Assert.True(ClassicSettingsStore.Validate(definition, definition.Default, out var error), error);
        }
    }

}

public sealed class OwnerExtraFeatureTests {

    [Fact]
    public void OwnerExtrasTurnOnFeaturesTheProfileLeavesOff() {
        var rules = ClassicDataFixture.RealRules("late-2009");
        Assert.False(rules.IsFeatureEnabled(ClassicFeatures.PetsLeveling));
        Assert.False(rules.IsZoneAllowed("WizardCity/WC_Streets/Interiors/WC_PET_Park").Allowed);

        var extras = new System.Collections.Generic.HashSet<string> { ClassicFeatures.PetsLeveling, ClassicFeatures.PetsHatching };
        rules.OwnerExtraFeatures = () => extras;
        Assert.True(rules.IsFeatureEnabled(ClassicFeatures.PetsLeveling));
        Assert.True(rules.IsZoneAllowed("WizardCity/WC_Streets/Interiors/WC_PET_Park").Allowed);
        Assert.False(rules.IsFeatureEnabled(ClassicFeatures.Jewels));
    }

}
