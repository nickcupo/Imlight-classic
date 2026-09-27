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
 * The real zone map (worlds.yaml plus overrides-spiraldb.yaml) under the
 * real profiles: which zones are open and closed, and where closed zones
 * send players.
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

using System.IO;
using System.Linq;
using Imlight.Classic.Zones;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class RealZoneMapTests {

    [Fact]
    public void LoadsWithItsInclude() {
        var map = ZoneWorldMapLoader.Load(ClassicDataFixture.WorldsPath);

        Assert.Equal(new[] { "worlds.yaml", "overrides-spiraldb.yaml" }, map.SourceFiles.Select(Path.GetFileName).ToArray());
        Assert.Contains(map.Overrides, rule => rule.SourceFile == "overrides-spiraldb.yaml");
        Assert.Equal("wizard_city", map.FallbackWorldId);
    }

    [Fact]
    public void EverySchemaWorldIsPresent() {
        var map = ZoneWorldMapLoader.Load(ClassicDataFixture.WorldsPath);

        Assert.Equal(ClassicSchema.WorldIds.Order().ToArray(), map.Worlds.Select(world => world.Id).Order().ToArray());
    }

    [Theory]
    [InlineData("WizardCity/WC_Hub", "wizard_city")]
    [InlineData("ClassicMode/WC_Ravenwood", "wizard_city")]
    [InlineData("Krokotopia/KT_Hub", "krokotopia")]
    [InlineData("DragonSpire/DS_Hub_Cathedral", "dragonspyre")]
    [InlineData("Grizzleheim/GH_MainHub", "grizzleheim")]
    [InlineData("Housing/WizardCity/WC_Tier1_Interior_Preview", null)]
    [InlineData("ThePhantomZoneWorld/Minigame", null)]
    [InlineData("WizardCity/Interiors/WC_Library", "wizard_city")]
    [InlineData("WizardCity/Tutorial_Duel_Arena", "wizard_city")]
    [InlineData("MooShu/Interiors/MS_ShopJewelry", "mooshu")]
    public void OpenOnLate2009(string zone, string? world) {
        var decision = ClassicDataFixture.RealRules("late-2009").IsZoneAllowed(zone);

        Assert.True(decision.Allowed, decision.Reason);
        Assert.Equal(world, decision.WorldId);
    }

    [Theory]
    [InlineData("Celestia/Anything", "celestia")]
    [InlineData("Grizzleheim/GH_HFjord/GH_Nordrilund", "wintertusk")]
    [InlineData("WizardCity/Interiors/WC_Hatchery", "wizard_city")]
    [InlineData("PetDerby/Anything", null)]
    [InlineData("WizardCity/QA_SpawnRate", "wizard_city")]
    [InlineData("Test/Anything", null)]
    [InlineData("Nonsense/Anything", null)]
    [InlineData("WizardCity/Interiors/WC_Park_PetShop", "wizard_city")]
    [InlineData("Krokotopia/Interiors/KT_SkeletonKeyWood01", "krokotopia")]
    [InlineData("Krokotopia/KT_Selenopolis/KT_Z04_Selenopolis", "selenopolis")]
    [InlineData("Krokotopia/KT_Selenopolis/Interiors/KT_Z04_Selenopolis_T1", "selenopolis")]
    [InlineData("WizardCity/Interiors/WC_Shop_Jeweler", "wizard_city")]
    [InlineData("WizardCity/Interiors/WC_Shop_Makeup", "wizard_city")]
    [InlineData("WizardCity/Interiors/WC_LibraryBookStacks", "wizard_city")]
    [InlineData("WizardCity/TreasureTower/WC_TT01_Death_L01", "wizard_city")]
    [InlineData("WizardCity/Tutorial_Archmastery_Arena_1", "wizard_city")]
    [InlineData("WizardCity/Tutorial_Fusion_Arena_1", "wizard_city")]
    [InlineData("WizardCity/KR_IsleOfArachnis", "wizard_city")]
    [InlineData("WizardCity/WC_Streets/WC_Drains/Z00_Drains_HUB", "wizard_city")]
    [InlineData("WizardCity/WC_Streets/WC_Catacombs_B", "wizard_city")]
    [InlineData("WizardCity/WC_Streets/Interiors/WC_Catacombs_Avalon", "wizard_city")]
    [InlineData("MooShu/MS_Catmandu", "mooshu")]
    [InlineData("MooShu/Interiors/MS_CAT_SoyHut", "mooshu")]
    [InlineData("Marleybone/G14_Gauntlet/MB_Z01_BarkinghamCourtyard", "marleybone")]
    public void ClosedOnLate2009(string zone, string? world) {
        var decision = ClassicDataFixture.RealRules("late-2009").IsZoneAllowed(zone);

        Assert.False(decision.Allowed, decision.Reason);
        Assert.Equal(world, decision.WorldId);
        Assert.NotEmpty(decision.PlayerMessage);
    }

    [Fact]
    public void HatcheryIsClosedByThePetsHatchingSwitch() {
        var decision = ClassicDataFixture.RealRules("late-2009").IsZoneAllowed("WizardCity/Interiors/WC_Hatchery");

        Assert.Contains("feature pets.hatching is off", decision.Reason);
        Assert.Equal("overrides-spiraldb.yaml overrides[1]", decision.RuleSource);
        Assert.Equal("medium", decision.Confidence);
    }

    [Fact]
    public void JewelerIsClosedByTheJewelsSwitch() {
        var decision = ClassicDataFixture.RealRules("late-2009").IsZoneAllowed("WizardCity/Interiors/WC_Shop_Jeweler");

        Assert.Contains("feature jewels is off", decision.Reason);
        Assert.Equal("high", decision.Confidence);
    }

    [Fact]
    public void SelenopolisIsItsOwnClosedWorldOnTheSpiralDoor() {
        var rules = ClassicDataFixture.RealRules("late-2009");
        var decision = rules.IsWorldTeleportAllowed("KT_Selenopolis", "Krokotopia/KT_Selenopolis/KT_Z04_Selenopolis");

        Assert.False(rules.IsHubKeyAllowed("KT_Selenopolis"));
        Assert.False(decision.Allowed);
        Assert.Equal("Selenopolis isn't open yet.", decision.PlayerMessage);
        Assert.True(rules.IsWorldTeleportAllowed("Krokotopia", "Krokotopia/KT_WorldTeleporter").Allowed);
    }

    [Fact]
    public void Arc1ClosesGrizzleheimButKeepsHousing() {
        var rules = ClassicDataFixture.RealRules("arc1-2009h1");

        Assert.False(rules.IsZoneAllowed("Grizzleheim/GH_MainHub").Allowed);
        Assert.Equal("Grizzleheim isn't open yet.", rules.IsZoneAllowed("Grizzleheim/GH_MainHub").PlayerMessage);
        Assert.False(rules.IsHubKeyAllowed("Grizzleheim"));
        Assert.True(rules.IsHubKeyAllowed("DragonSpire"));
        Assert.True(rules.IsZoneAllowed("Housing/WizardCity/WC_Tier1_Interior_Preview").Allowed);
        Assert.Equal("WizardCity", rules.HubKeyFor("Grizzleheim/GH_MainHub"));
    }

    [Theory]
    [InlineData("late-2009")]
    [InlineData("arc1-2009h1")]
    public void EveryAllowedWorldHasAHubKey(string profileId) {
        var rules = ClassicDataFixture.RealRules(profileId);

        foreach (var worldId in rules.Profile.Worlds!.Value) {
            Assert.NotNull(rules.Zones.FindWorld(worldId)?.HubKey);
        }
    }

    [Fact]
    public void SpiralDoorListOnLate2009() {
        var rules = ClassicDataFixture.RealRules("late-2009");
        string[] door = ["WizardCity", "Krokotopia", "Marleybone", "MooShu", "Grizzleheim", "DragonSpire"];

        Assert.Equal(6, door.Count(rules.IsHubKeyAllowed));
        Assert.Equal(5, door.Count(ClassicDataFixture.RealRules("arc1-2009h1").IsHubKeyAllowed));
    }

    [Theory]
    [InlineData("ClassicMode/WC_Ravenwood", "WizardCity")]
    [InlineData("Housing/WizardCity/WC_Tier1_Interior_Preview", "WizardCity")]
    [InlineData("Celestia/CL_Hub", "WizardCity")]
    [InlineData("Grizzleheim/GH_HFjord/GH_Nordrilund", "Grizzleheim")]
    [InlineData("DragonSpire/DS_Hub_Cathedral", "DragonSpire")]
    [InlineData("MooShu/MS_Hub", "MooShu")]
    [InlineData("Krokotopia/KT_Selenopolis/KT_Z04_Selenopolis", "WizardCity")]
    public void HubKeyForLate2009(string zone, string hubKey) {
        Assert.Equal(hubKey, ClassicDataFixture.RealRules("late-2009").HubKeyFor(zone));
    }

}
