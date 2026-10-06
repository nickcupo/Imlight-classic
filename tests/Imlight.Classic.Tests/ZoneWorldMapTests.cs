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
 * Zone classification and zone decisions on an inline fixture map:
 * specificity, globbing, overrides, areas, feature and date conditions,
 * unmapped zones and hub fallback.
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
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using Imlight.Classic.Zones;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ZoneWorldMapTests : IDisposable {

    private static readonly DateOnly s_cutoff = new(2010, 5, 25);
    private static readonly string[] s_worlds = ["wizard_city", "krokotopia", "grizzleheim"];

    private readonly TempClassicData _data = new();
    private readonly ZoneWorldMap _map;

    public ZoneWorldMapTests() {
        _map = ZoneFixture.LoadMap(_data, ZoneFixture.WorldsYaml(
            worlds: """
                  wizard_city: { name: Wizard City, hub_key: WizardCity, prefixes: [WizardCity, ClassicMode] }
                  krokotopia: { name: Krokotopia, hub_key: Krokotopia, prefixes: [Krokotopia] }
                  grizzleheim: { name: Grizzleheim, hub_key: Grizzleheim, prefixes: [Grizzleheim] }
                  celestia: { name: Celestia, prefixes: [Celestia] }
                  wintertusk: { name: Wintertusk, prefixes: [] }
                """,
            areas: """
                  interiors: { name: Interiors, prefixes: [WizardCity/Interiors], access: allow, reason: r, confidence: high }
                  kt_generic: { name: KT, prefixes: ["Krokotopia/KT_*"], access: allow, reason: r, confidence: low }
                  kt_tomb: { name: Tomb, prefixes: ["Krokotopia/KT_Tomb*"], access: deny, reason: tomb, confidence: medium }
                  housing: { name: Housing, prefixes: [Housing], access: feature, feature: housing, reason: r, confidence: high }
                  test: { name: Test, prefixes: [Test], access: deny, reason: test zones, confidence: high, message: No tests. }
                  before: { name: Before, prefixes: [Before], access: allow, introduced: 2010-01-01, reason: r, confidence: high }
                  onday: { name: On day, prefixes: [OnDay], access: allow, introduced: 2010-05-25, reason: r, confidence: high }
                  later: { name: Later, prefixes: [Later], access: allow, introduced: 2010-10-27, reason: r, confidence: high }
                  someday: { name: Someday, prefixes: [Someday], access: allow, introduced_after: 2010-05-25, reason: r, confidence: low }
                """,
            overrides: """
                  - { zone: Grizzleheim/GH_HFjord, world: wintertusk, reason: wintertusk side world, confidence: high }
                  - { zone: "WizardCity/WC_Hatch*", feature: pets.hatching, reason: hatchery, confidence: medium }
                  - { zone: WizardCity/WC_Hatchery, access: deny, reason: closed, confidence: high }
                """));
    }

    public void Dispose() => _data.Dispose();

    private ClassicRules Rules(DateOnly? cutoff = null, string[]? worlds = null, Dictionary<string, bool>? features = null)
        => new(ZoneFixture.Profile(cutoff ?? s_cutoff, worlds ?? s_worlds, features), _map);

    [Fact]
    public void MoreSegmentsWin() {
        var classification = _map.Classify("WizardCity/Interiors/WC_Shop");

        Assert.Equal("interiors", classification.Home!.Area!.Id);
        Assert.Null(classification.EffectiveWorld);
    }

    [Fact]
    public void MoreLiteralCharactersWinOnEqualSegments() {
        Assert.Equal("kt_tomb", _map.Classify("Krokotopia/KT_Tomb2/Room").Home!.Area!.Id);
        Assert.Equal("kt_generic", _map.Classify("Krokotopia/KT_Hub").Home!.Area!.Id);
    }

    [Fact]
    public void MatchingIgnoresCase() {
        var decision = Rules().IsZoneAllowed("wizardcity/wc_hub");

        Assert.True(decision.Allowed);
        Assert.Equal("wizard_city", decision.WorldId);
    }

    [Fact]
    public void PrefixMatchesWholeSegmentsOnly() {
        var decision = Rules().IsZoneAllowed("WizardCityX/WC_Hub");

        Assert.False(decision.Allowed);
        Assert.Equal("unmapped", decision.RuleSource);
        Assert.Equal(ClassicMessages.Unmapped, decision.PlayerMessage);
        Assert.Equal("no zone-map prefix covers 'WizardCityX/WC_Hub'", decision.Reason);
    }

    [Fact]
    public void StarStaysInsideOneSegment() {
        Assert.True(ZonePattern.Parse("A/B*").Matches("A/Bx/C"));
        Assert.True(ZonePattern.Parse("A*").Matches("Abc/D"));
        Assert.False(ZonePattern.Parse("A*C").Matches("Ab/C"));
        Assert.True(ZonePattern.Parse("A*C").Matches("AbC/D"));
        Assert.False(ZonePattern.Parse("A/B").Matches("A"));
    }

    [Fact]
    public void ClassicModeCountsAsWizardCity() {
        var decision = Rules().IsZoneAllowed("ClassicMode/WC_Ravenwood");

        Assert.True(decision.Allowed);
        Assert.Equal("wizard_city", decision.WorldId);
    }

    [Fact]
    public void OverrideReassignsTheWorld() {
        var decision = Rules().IsZoneAllowed("Grizzleheim/GH_HFjord/GH_Nordrilund");

        Assert.False(decision.Allowed);
        Assert.Equal("wintertusk", decision.WorldId);
        Assert.Equal("grizzleheim", decision.HomeWorldId);
        Assert.Equal("Wintertusk isn't open yet.", decision.PlayerMessage);
        Assert.Equal("worlds.yaml overrides[0]", decision.RuleSource);
        Assert.Equal("high", decision.Confidence);
        Assert.True(Rules().IsZoneAllowed("Grizzleheim/GH_MainHub").Allowed);
    }

    [Fact]
    public void DenyOverrideBeatsTheOtherOverrides() {
        var hatchingOn = new Dictionary<string, bool> { [ClassicFeatures.PetsHatching] = true };
        var decision = Rules(features: hatchingOn).IsZoneAllowed("WizardCity/WC_Hatchery/Room");

        Assert.False(decision.Allowed);
        Assert.Equal("worlds.yaml overrides[2]", decision.RuleSource);

        var classification = _map.Classify("WizardCity/WC_Hatchery");
        Assert.Equal(2, classification.Overrides.Length);
        Assert.Equal("WizardCity/WC_Hatchery", classification.Overrides[0].Pattern.Text);
    }

    [Fact]
    public void FeatureOverrideClosesWhenTheFeatureIsOff() {
        var hatchingOff = new Dictionary<string, bool> { [ClassicFeatures.PetsHatching] = false };

        var decision = Rules(features: hatchingOff).IsZoneAllowed("WizardCity/WC_HatchPad");

        Assert.False(decision.Allowed);
        Assert.Equal("worlds.yaml overrides[1]", decision.RuleSource);
        Assert.Contains("feature pets.hatching is off", decision.Reason);
        Assert.True(Rules().IsZoneAllowed("WizardCity/WC_HatchPad").Allowed);
    }

    [Fact]
    public void DeniedArea() {
        var decision = Rules().IsZoneAllowed("Test/Anything");

        Assert.False(decision.Allowed);
        Assert.Equal("test", decision.AreaId);
        Assert.Equal("No tests.", decision.PlayerMessage);
        Assert.Equal("worlds.yaml areas.test.prefixes[0]", decision.RuleSource);
    }

    [Fact]
    public void FeatureGatedArea() {
        var off = new Dictionary<string, bool> { [ClassicFeatures.Housing] = false };

        Assert.False(Rules(features: off).IsZoneAllowed("Housing/WizardCity/Dorm").Allowed);
        Assert.True(Rules().IsZoneAllowed("Housing/WizardCity/Dorm").Allowed);
    }

    [Fact]
    public void IntroducedIsComparedWithTheCutoff() {
        var rules = Rules();

        Assert.True(rules.IsZoneAllowed("Before/X").Allowed);
        Assert.True(rules.IsZoneAllowed("OnDay/X").Allowed);
        var later = rules.IsZoneAllowed("Later/X");
        Assert.False(later.Allowed);
        Assert.StartsWith("introduced 2010-10-27, after the cutoff 2010-05-25", later.Reason);
    }

    [Fact]
    public void IndependentZoneContentCutoffKeepsExactLaterZonesClosedUnderOctoberRules() {
        var october = new DateOnly(2010, 10, 31);
        var rules = new ClassicRules(ZoneFixture.Profile(cutoff: october, worlds: s_worlds, zoneContentCutoff: s_cutoff), _map);

        Assert.True(Rules(cutoff: october).IsZoneAllowed("Later/X").Allowed);
        var decision = rules.IsZoneAllowed("Later/X");
        Assert.False(decision.Allowed);
        Assert.StartsWith("introduced 2010-10-27, after the cutoff 2010-05-25", decision.Reason);
        Assert.True(rules.IsZoneAllowed("OnDay/X").Allowed);
        Assert.Equal(october, rules.Profile.Cutoff);
        Assert.False(ZoneFixture.Profile(zoneContentCutoff: s_cutoff).IsUnrestricted);
    }

    [Fact]
    public void IntroducedAfterDeniesWheneverThereIsACutoff() {
        Assert.False(Rules().IsZoneAllowed("Someday/X").Allowed);
        Assert.False(Rules(cutoff: new DateOnly(2030, 1, 1)).IsZoneAllowed("Someday/X").Allowed);

        var noCutoff = new ClassicRules(ZoneFixture.Profile(cutoff: null, worlds: s_worlds), _map);
        Assert.True(noCutoff.IsZoneAllowed("Someday/X").Allowed);
        Assert.True(noCutoff.IsZoneAllowed("Later/X").Allowed);
    }

    [Fact]
    public void ClosedWorld() {
        var decision = Rules().IsZoneAllowed("Celestia/CL_Hub");

        Assert.False(decision.Allowed);
        Assert.Equal("celestia", decision.WorldId);
        Assert.Equal("Celestia isn't open yet.", decision.PlayerMessage);
        Assert.Equal("world celestia is not in profile test (worlds.yaml worlds.celestia.prefixes[0])", decision.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankZoneIsDenied(string? zone) {
        var decision = Rules().IsZoneAllowed(zone);

        Assert.False(decision.Allowed);
        Assert.Equal("empty zone name", decision.Reason);
    }

    [Fact]
    public void ReassignedClosedZoneGoesToItsHomeWorldHub() {
        Assert.Equal("Grizzleheim", Rules().HubKeyFor("Grizzleheim/GH_HFjord/GH_Nordrilund"));
    }

    [Fact]
    public void AreasAndClosedWorldsGoToTheFallbackWorld() {
        Assert.Equal("WizardCity", Rules().HubKeyFor("Housing/WizardCity/Dorm"));
        Assert.Equal("WizardCity", Rules().HubKeyFor("Celestia/CL_Hub"));
        Assert.Equal("WizardCity", Rules().HubKeyFor("Nonsense/X"));
        Assert.Equal("Krokotopia", Rules().HubKeyFor("Krokotopia/Other"));
    }

    [Fact]
    public void WithoutTheFallbackWorldTheFirstOpenWorldWithAHubWins() {
        var rules = Rules(worlds: ["celestia", "krokotopia", "grizzleheim"]);

        Assert.Equal("Krokotopia", rules.HubKeyFor("Celestia/CL_Hub"));
        Assert.Equal("Krokotopia", rules.HubKeyFor("WizardCity/WC_Hub"));
        Assert.Equal("Grizzleheim", rules.HubKeyFor("Grizzleheim/GH_HFjord/X"));
    }

    [Fact]
    public void HubKeysFollowTheProfile() {
        var rules = Rules(worlds: ["wizard_city", "krokotopia"]);

        Assert.True(rules.IsHubKeyAllowed("WizardCity"));
        Assert.True(rules.IsHubKeyAllowed("krokotopia"));
        Assert.False(rules.IsHubKeyAllowed("Grizzleheim"));
        Assert.False(rules.IsHubKeyAllowed("Nowhere"));
    }

    [Fact]
    public void SpiralDoorRequestNeedsAListedHubKeyAndAnOpenZone() {
        var rules = Rules(worlds: ["wizard_city", "krokotopia"]);

        Assert.True(rules.IsWorldTeleportAllowed("Krokotopia", "Krokotopia/KT_Hub").Allowed);

        var unlisted = rules.IsWorldTeleportAllowed("KT_Other", "Krokotopia/KT_Other/KT_Hub");
        Assert.True(rules.IsZoneAllowed("Krokotopia/KT_Other/KT_Hub").Allowed);
        Assert.False(unlisted.Allowed);
        Assert.Equal(ClassicRules.HubKeyRule, unlisted.RuleSource);
        Assert.Equal(ClassicMessages.Unmapped, unlisted.PlayerMessage);
        Assert.Contains("hub key 'KT_Other'", unlisted.Reason);

        var closedZone = rules.IsWorldTeleportAllowed("Grizzleheim", "Grizzleheim/GH_MainHub");
        Assert.False(closedZone.Allowed);
        Assert.Equal("Grizzleheim isn't open yet.", closedZone.PlayerMessage);
    }

    [Fact]
    public void ProfileWithNoHubWorldIsRejected() {
        var ex = Assert.Throws<ClassicDataException>(() => Rules(worlds: ["celestia"]));

        Assert.Contains(ex.Errors, error => error.KeyPath == "fallback_world");
    }

}
