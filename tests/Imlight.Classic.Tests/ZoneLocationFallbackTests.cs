// CLASSIC: a transfer to a location a zone lacks lands on "Start", else on the zone's first location, never at
// float.MaxValue. The 2014 Krokotopia Arena package the server loads (WizardCity/KT_Duel_Arena, tools/classic-zones.list)
// has no "Start" (only eight Home and four Spectator points), so a GM `teleport WizardCity/KT_Duel_Arena` put the wizard
// at x = 3.4e38 (playbot-ios 2026-10-25). Matches and spectators use "Home" and were not affected.

using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ZoneLocationFallbackTests {

    private static LocationTemplate At(string name, float x, float y, float z, float direction = 0)
        => new() { m_locName = name, m_location = new Vector3(x, y, z), m_direction = direction };

    // The 2014 KT_Duel_Arena gamedata m_locationList (first entries).
    private static readonly LocationTemplate[] Kt2014 = [
        At("Home", 629.6f, 5760.0f, -1117.6f), At("Home", 276.6f, 5480.8f, -1117.6f),
        At("Spectator", -1158.8f, 1584.8f, 0f),
    ];

    [Fact]
    public void AZoneWithoutStartUsesItsFirstLocation() {
        var found = Imlight.CoreLib.Game.Zone.Core.Zone.FindLocation(Kt2014, "Start");
        Assert.NotNull(found);
        Assert.Equal("Home", found.m_locName);
        Assert.Equal(629.6f, found.m_location.X);
    }

    [Fact]
    public void TheNamedLocationThenStartComeFirst() {
        Assert.Equal("Spectator", Imlight.CoreLib.Game.Zone.Core.Zone.FindLocation(Kt2014, "Spectator")!.m_locName);
        LocationTemplate[] r806919 = [.. Kt2014, At("Start", -98.6f, -2259.7f, 0f)];
        Assert.Equal(-98.6f, Imlight.CoreLib.Game.Zone.Core.Zone.FindLocation(r806919, "Nowhere")!.m_location.X);
        Assert.Equal("Home", Imlight.CoreLib.Game.Zone.Core.Zone.FindLocation(r806919, "Home")!.m_locName);
    }

    [Fact]
    public void AZoneWithoutLocationsHasNone() {
        Assert.Null(Imlight.CoreLib.Game.Zone.Core.Zone.FindLocation([], "Start"));
        Assert.Null(Imlight.CoreLib.Game.Zone.Core.Zone.FindLocation(null!, "Start"));
    }
}
