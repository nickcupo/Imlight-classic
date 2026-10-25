// CLASSIC: the house door (House_Teleport_01, template 160429) between a lot and its inside. r806919 has no distance
// check of its own: the server notices the wizard stepping into the TeleportProximityBehavior's radius (108), sends
// Housing MSG_REQUESTHOUSINGZONE {GlobalID = the door, TemplateGID}, the client loads the template's DeedBehavior
// m_lotWorldZone and answers Status 0, and the server transfers with the lot kept to the other side's
// House_Teleport_01 location (HouseDoorComponent, ZoneService.Housing).
// Checked against the client data (2026-10-25, every enabled house of houses-october-2010.yaml): each exterior and
// interior places one House_Teleport_01 and a "House_Teleport_01" location; each structure's lot world zone is its
// interior and each deed's its exterior.

using System.IO;
using System.Linq;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Zone.Components;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class HouseDoorTests {

    // The Wooded Cottage's exterior door (r806919 Housing/WizardCity/WC_Tier1_Exterior gamedata).
    private static readonly Vector3 Door = new(1919.0f, 1901.8f, 200f);

    private static TeleportProximityBehaviorTemplate Proximity(float radius = 108f)
        => new() { m_radius = radius, m_locationName = "House_Teleport_01" };

    private static GameObjectTemplate HouseTeleport()
        => new() { m_templateID = 160429, m_objectName = "House_Teleport_01", m_behaviors = [Proximity()] };

    [Fact]
    public void TheInvisibleDoorIsSentToTheClientAndGetsTheDoorComponent() {
        // No RenderBehavior: without this the client never has the object its TeleportProximityBehavior answers for.
        Assert.True(RenderComponent.ShouldAttachToEntity(HouseTeleport()));
        Assert.True(HouseDoorComponent.ShouldAttachToEntity(HouseTeleport()));
        var plain = new GameObjectTemplate { m_templateID = 1, m_objectName = "Plain", m_behaviors = [] };
        Assert.False(RenderComponent.ShouldAttachToEntity(plain));
        Assert.False(HouseDoorComponent.ShouldAttachToEntity(plain));
    }

    [Fact]
    public void TheDoorOpensWithinItsRadiusOnly() {
        Assert.True(HouseDoorComponent.Within(Door, Door, Proximity()));
        Assert.True(HouseDoorComponent.Within(new Vector3(Door.X + 100, Door.Y, Door.Z), Door, Proximity()));
        Assert.False(HouseDoorComponent.Within(new Vector3(Door.X + 109, Door.Y, Door.Z), Door, Proximity()));
        // The arrival point outside ("House_Teleport_01" at 1645.3, 1646.0, 200) is out of the radius: no bounce back.
        Assert.False(HouseDoorComponent.Within(new Vector3(1645.3f, 1646.0f, 200f), Door, Proximity()));
        // The inside door (-31.84, -147.74, 0) and its arrival point (-31.70, 105.80, 0).
        Assert.False(HouseDoorComponent.Within(new Vector3(-31.70f, 105.80f, 0f), new Vector3(-31.84f, -147.74f, 0f), Proximity()));
    }

    [Fact]
    public void TheDoorHonoursTheBehaviorsOffset() {
        var shifted = new TeleportProximityBehaviorTemplate { m_radius = 10f, m_offsetX = 50f };
        Assert.False(HouseDoorComponent.Within(Door, Door, shifted));
        Assert.True(HouseDoorComponent.Within(new Vector3(Door.X + 50, Door.Y, Door.Z), Door, shifted));
    }

    [Fact]
    public void EveryShippedHouseDoorLeadsToTheOtherSideWithTheTemplateTheClientLoadsItFrom() {
        var catalog = HouseCatalog.Load(Path.Combine(ClassicDataFixture.Root, "housing", "houses-october-2010.yaml"),
            "october-2010-arc1");
        var houses = catalog.Values.Where(h => h.Complete).ToList();
        Assert.Equal(17, houses.Count);
        foreach (var house in houses) {
            Assert.Equal((house.InteriorZone, house.StructureTemplateId), ZoneService.DoorDestination(house, house.ExteriorZone));
            Assert.Equal((house.ExteriorZone, house.TemplateId), ZoneService.DoorDestination(house, house.InteriorZone));
            Assert.Null(ZoneService.DoorDestination(house, "WizardCity/WC_Hub"));
        }

        var cottage = catalog[160431];                                    // Wooded Cottage: deed 160431, structure 160432
        Assert.Equal(("Housing/WizardCity/WC_Tier1_Interior", 160432u),
            ZoneService.DoorDestination(cottage, "Housing/WizardCity/WC_Tier1_Exterior"));
        Assert.Equal("House_Teleport_01", ZoneService.DOOR_LOCATION);
    }
}
