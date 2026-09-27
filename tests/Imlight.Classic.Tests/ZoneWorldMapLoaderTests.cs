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
 * The zone map loader's validation errors: duplicates, unknown ids and
 * features, overrides without an effect or with access: allow, both date
 * fields, includes, the fallback hub and bad patterns.
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
using System.Linq;
using Imlight.Classic.Zones;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ZoneWorldMapLoaderTests : IDisposable {

    private const string Worlds = """
          wizard_city: { name: Wizard City, hub_key: WizardCity, prefixes: [WizardCity] }
          krokotopia: { name: Krokotopia, hub_key: Krokotopia, prefixes: [Krokotopia] }
        """;

    private const string OverridesFile = """
        kind: zone-overrides
        version: 1
        license_tag: cc-by-nc-sa-spiraldb
        source: test
        overrides:
          - { zone: wizardcity/wc_hub, access: deny, reason: r, confidence: high }
        """;

    private readonly TempClassicData _data = new();

    public void Dispose() => _data.Dispose();

    private ZoneWorldMap Load(string yaml)
        => ZoneFixture.LoadMap(_data, yaml);

    [Fact]
    public void ValidFixtureLoads() {
        _data.WriteZoneFile("extra.yaml", OverridesFile);

        var map = Load(ZoneFixture.WorldsYaml(Worlds, includes: "  - extra.yaml"));

        Assert.Equal(ClassicSchema.WorldIds.Length, map.Worlds.Length);
        Assert.Equal("extra.yaml overrides[0]", Assert.Single(map.Overrides).RuleSource);
        Assert.Equal(2, map.SourceFiles.Length);
        Assert.Equal("wizard_city", map.FallbackWorldId);
    }

    [Fact]
    public void DuplicatePrefixIgnoringCase() {
        var yaml = ZoneFixture.WorldsYaml(Worlds, areas: """
              shops: { name: Shops, prefixes: [wizardcity], access: allow, reason: r, confidence: high }
            """);

        var error = ZoneFixture.SingleError(() => Load(yaml), "areas.shops.prefixes[0]");

        Assert.Equal("prefix 'wizardcity' repeats worlds.wizard_city.prefixes[0]", error.Message);
    }

    [Fact]
    public void DuplicateOverridePatternAcrossFiles() {
        _data.WriteZoneFile("extra.yaml", OverridesFile);
        var yaml = ZoneFixture.WorldsYaml(Worlds, includes: "  - extra.yaml", overrides: """
              - { zone: WizardCity/WC_Hub, feature: housing, reason: r, confidence: high }
            """);

        var error = ZoneFixture.SingleError(() => Load(yaml), "overrides[0].zone");

        Assert.Equal("classic-data/zones/extra.yaml", error.File);
        Assert.Equal("zone 'wizardcity/wc_hub' repeats worlds.yaml overrides[0]", error.Message);
    }

    [Fact]
    public void UnknownWorldId() {
        var yaml = ZoneFixture.WorldsYaml(Worlds + "\n  narnia: { name: Narnia, prefixes: [Narnia] }\n");

        var error = ZoneFixture.SingleError(() => Load(yaml), "worlds.narnia");

        Assert.Equal("unknown world id 'narnia'", error.Message);
    }

    [Fact]
    public void MissingWorldEntry() {
        var yaml = ZoneFixture.WorldsYaml(Worlds).Replace("  wysteria: { name: wysteria, prefixes: [] }\n", "");

        var error = ZoneFixture.SingleError(() => Load(yaml), "worlds.wysteria");

        Assert.Equal("world 'wysteria' has no entry; every schema world needs one", error.Message);
    }

    [Fact]
    public void UnknownFeature() {
        var yaml = ZoneFixture.WorldsYaml(Worlds, overrides: """
              - { zone: WizardCity/X, feature: pets.hatchery, reason: r, confidence: high }
            """);

        var error = ZoneFixture.SingleError(() => Load(yaml), "overrides[0].feature");

        Assert.Equal("unknown feature 'pets.hatchery'", error.Message);
    }

    [Fact]
    public void OverrideWithNoEffect() {
        var yaml = ZoneFixture.WorldsYaml(Worlds, overrides: """
              - { zone: WizardCity/X, reason: r, confidence: high }
            """);

        var error = ZoneFixture.SingleError(() => Load(yaml), "overrides[0]");

        Assert.StartsWith("an override needs an effect", error.Message);
    }

    [Fact]
    public void OverrideCannotAllow() {
        var yaml = ZoneFixture.WorldsYaml(Worlds, overrides: """
              - { zone: WizardCity/X, access: allow, reason: r, confidence: high }
            """);

        var error = ZoneFixture.SingleError(() => Load(yaml), "overrides[0].access");

        Assert.StartsWith("access must be 'deny', got 'allow'", error.Message);
    }

    [Fact]
    public void BothDateFields() {
        var yaml = ZoneFixture.WorldsYaml(Worlds, overrides: """
              - { zone: WizardCity/X, introduced: 2011-01-01, introduced_after: 2010-05-25, reason: r, confidence: high }
            """);

        var error = ZoneFixture.SingleError(() => Load(yaml), "overrides[0].introduced_after");

        Assert.Equal("set introduced or introduced_after, not both", error.Message);
    }

    [Fact]
    public void MissingInclude() {
        var yaml = ZoneFixture.WorldsYaml(Worlds, includes: "  - missing.yaml");

        var error = ZoneFixture.SingleError(() => Load(yaml), "includes[0]");

        Assert.Equal("includes 'missing.yaml', which does not exist", error.Message);
    }

    [Fact]
    public void IncludeOfTheWrongKind() {
        _data.WriteZoneFile("other.yaml", ZoneFixture.WorldsYaml(Worlds));
        var yaml = ZoneFixture.WorldsYaml(Worlds, includes: "  - other.yaml");

        var error = ZoneFixture.SingleError(() => Load(yaml), "includes[0]");

        Assert.Equal("includes 'other.yaml', which is a 'zone-worlds' file, not zone-overrides", error.Message);
    }

    [Fact]
    public void OverridesFileCannotInclude() {
        _data.WriteZoneFile("extra.yaml", OverridesFile + "\nincludes: [more.yaml]\n");
        var yaml = ZoneFixture.WorldsYaml(Worlds, includes: "  - extra.yaml");

        var error = ZoneFixture.SingleError(() => Load(yaml), "includes");

        Assert.Equal("classic-data/zones/extra.yaml", error.File);
        Assert.Equal("unknown key 'includes'", error.Message);
    }

    [Fact]
    public void FallbackWorldNeedsAHubKey() {
        var yaml = ZoneFixture.WorldsYaml(Worlds, fallback: "celestia");

        var error = ZoneFixture.SingleError(() => Load(yaml), "fallback_world");

        Assert.Equal("fallback_world 'celestia' has no hub_key", error.Message);
    }

    [Theory]
    [InlineData("Wizard City")]
    [InlineData("A//B")]
    [InlineData("/A")]
    [InlineData("A/")]
    public void BadPattern(string pattern) {
        var yaml = ZoneFixture.WorldsYaml(Worlds, areas: $$"""
              bad: { name: Bad, prefixes: ["{{pattern}}"], access: allow, reason: r, confidence: high }
            """);

        var error = ZoneFixture.SingleError(() => Load(yaml), "areas.bad.prefixes[0]");

        Assert.StartsWith($"'{pattern}' is not a valid zone pattern", error.Message);
    }

    [Fact]
    public void FeatureAreaNeedsAFeature() {
        var yaml = ZoneFixture.WorldsYaml(Worlds, areas: """
              gated: { name: Gated, prefixes: [Gated], access: feature, reason: r, confidence: high }
              open: { name: Open, prefixes: [Open], access: allow, feature: housing, reason: r, confidence: high }
            """);

        var ex = Assert.Throws<ClassicDataException>(() => Load(yaml));

        Assert.Contains(ex.Errors, error => error.KeyPath == "areas.gated.feature");
        Assert.Contains(ex.Errors, error => error.KeyPath == "areas.open.feature");
    }

    [Fact]
    public void ErrorsCarryFileAndLine() {
        var yaml = ZoneFixture.WorldsYaml(Worlds, overrides: """
              - { zone: WizardCity/X, feature: nope, reason: r, confidence: high }
            """);
        var line = yaml.Split('\n').ToList().FindIndex(text => text.Contains("feature: nope", StringComparison.Ordinal)) + 1;

        var error = ZoneFixture.SingleError(() => Load(yaml), "overrides[0].feature");

        Assert.Equal("classic-data/zones/worlds.yaml", error.File);
        Assert.Equal(line, error.Line);
    }

}
