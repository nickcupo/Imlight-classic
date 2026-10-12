// CLASSIC: the Great Spyre lift and the Oasis boat rides (owner ruling 2026-10-08): the travel overlay sends wizards
// into the ride zones, and every ride zone has a start event, a final trigger and a fallback.
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ClassicRidesTests {

    [Theory]
    [InlineData("DragonSpire-DS_A3_Kings-DS_A3Z3_Volcano-DS_Volcano1", "DS_ElevatorActivator instance", "DragonSpire/DS_A3_Kings/DS_A3Z3_Volcano/DS_Volcano_Elevator_Up")]
    [InlineData("DragonSpire-DS_A3_Kings-DS_A3Z3_Volcano-DS_Volcano2", "DS_ElevatorActivator instance", "DragonSpire/DS_A3_Kings/DS_A3Z3_Volcano/DS_Volcano_Elevator_Down")]
    [InlineData("Krokotopia-KT_Hub", "KT-New-BoatRide instance", "Krokotopia/KT_HubFlyingShip_ToIsland")]
    [InlineData("Krokotopia-KT_Hub_Sphinx", "TeleporttoSphinxShip", "Krokotopia/KT_HubFlyingShip_ToMainland")]
    public void TheTravelOverlaySendsWizardsOntoEachRide(string file, string trigger, string rideZone) {
        var path = Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "ZoneTransfer", file + ".travel.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var entry = Assert.Single(doc.RootElement.GetProperty("Teleports").EnumerateArray(),
            t => t.GetProperty("TriggerName").GetString() == trigger);
        Assert.Equal(rideZone, entry.GetProperty("Teleport").GetProperty("m_destinationZone").GetString());
        var ride = ClassicRides.Find(rideZone);
        Assert.NotNull(ride);
        Assert.StartsWith("Enter_", ride!.StartEvent);
        Assert.True(ride.FallbackSeconds > ride.RideSeconds);
        Assert.True(ride.FallbackSeconds <= 60);
    }

    [Fact]
    public void TheBoatBackFinishesOnTheOverlaysOwnTriggerAndTheOthersOnSpiralDbs() {
        var path = Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "ZoneTransfer", "Krokotopia-KT_HubFlyingShip_ToMainland.travel.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var ride = ClassicRides.Find("Krokotopia/KT_HubFlyingShip_ToMainland")!;
        var final = Assert.Single(doc.RootElement.GetProperty("Teleports").EnumerateArray(),
            t => t.GetProperty("TriggerName").GetString() == ride.FinalTrigger);
        Assert.Equal("Krokotopia/KT_Hub", final.GetProperty("Teleport").GetProperty("m_destinationZone").GetString());
        Assert.Equal(4, ClassicRides.All.Count());
        Assert.Null(ClassicRides.Find("DragonSpire/DS_A3_Kings/DS_A3_FlightPath")); // its own start trigger already works
    }

    [Fact]
    public void TheFallbackMovesOnlyAWizardStillOnThatVisitOfTheRide() {
        var ride = ClassicRides.Find("dragonspire/ds_a3_kings/ds_a3z3_volcano/ds_volcano_elevator_up")!; // any case
        Assert.True(ClassicRides.FallbackApplies(ride, ride.Zone, 7, 7));
        Assert.False(ClassicRides.FallbackApplies(ride, "DragonSpire/DS_A3_Kings/DS_A3Z3_Volcano/DS_Volcano2", 7, 7)); // arrived
        Assert.False(ClassicRides.FallbackApplies(ride, ride.Zone, 7, 8));  // a later ride re-armed its own timer
        Assert.False(ClassicRides.FallbackApplies(ride, null, 7, 7));
    }

}
