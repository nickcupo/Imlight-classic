// CLASSIC: the Temple of Storms (Temple Dweller, KT-CRYHub-C01-004). The Well of Spirits' sigil used to land in the
// inner sanctum ('Target location 0', a level the r806919 collision joins only to the boss doors), and SpiralDB sent the
// trial pads to Wizard City, the Oasis, Celestia, or back to their own wing's start (ios-parity, 2026-10-26). The travel
// overlay (tools/classic-quests/travel_hand.py) now lands each where the 2014 package's named locations say.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Wad;
using Imlight.Common;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.World;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class TempleOfStormsTravelTests {

    private const string Temple = "Krokotopia/KT_Tomb/KT_TempleOfStorms";
    private const string Well = "Krokotopia/KT_Tomb/KT_WellOfSpirits";

    private static void EnsureConfiguration() {
        try { ConfigurationManager.GetSetting("Logging.LogLevel"); }
        catch (InvalidOperationException) {
            var config = Path.GetTempFileName();
            try {
                File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-temple-tests.log")}\n");
                ConfigurationManager.Initialize(config);
            }
            finally { File.Delete(config); }
        }
    }

    private static Dictionary<string, (string Zone, string Loc)> Overlay(string zone) {
        EnsureConfiguration();
        var loaded = new ConcurrentDictionary<string, WizardZoneData>(StringComparer.Ordinal);
        var method = typeof(SpiralDB).GetMethod("LoadZoneData", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay"), loaded]);
        var record = loaded.Values.Single(z => z.ZoneName == zone);

        return record.Teleports.ToDictionary(t => t.TriggerName,
            t => ((string) t.Teleport.m_destinationZone, (string) t.Teleport.m_destinationLoc), StringComparer.Ordinal);
    }

    // The 2014 package of the Temple (the one tools/classic-zones.list serves) names its locations; r806919 leaves them blank.
    private static Dictionary<string, string> NamedLocations() {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "w101c-private", "2014wiz",
            "Wizard101", "Data", "GameData", "Krokotopia-KT_Tomb-KT_TempleOfStorms.wad");
        if (!File.Exists(path)) {
            Assert.Skip("the private 2014 client is not on this machine");
        }

        using var stream = File.OpenRead(path);
        var wad = ArchiveParser.Parse(stream)!;
        var serializer = new BindSerializer();
        Assert.True(serializer.Deserialize<WizZoneData>(wad.OpenFile("gamedata.bin")!.Value.ToArray(), 1, out var zone));

        return zone.m_locationList.Where(l => l is not null).ToDictionary(l => (string) l.m_locName,
            l => FormattableString.Invariant($"{l.m_location.X},{l.m_location.Y},{l.m_location.Z},{l.m_direction}"), StringComparer.Ordinal);
    }

    private static (double X, double Y, double Z) Point(string loc) {
        var p = loc.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return (p[0], p[1], p[2]);
    }

    private static void Near(string expected, string actual) {
        var (a, b) = (Point(expected), Point(actual));
        Assert.True(Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(a.Z - b.Z) < 0.5, $"{actual} is not {expected}");
    }

    [Fact]
    public void TheWellOfSpiritsSigilEntersAtTheTemplesEntranceNotTheSanctum() {
        var (zone, loc) = Overlay(Well)["ToCrypt5FromHub"];
        Assert.Equal(Temple, zone);
        Near("-4.767157,521.5967,0.0004882813", loc);
        Near(NamedLocations()["Target (KT_Crypt_Hub KT_Crypt_Level05 Entrance)"], loc);
    }

    [Theory]
    [InlineData("Trigger 5", "Target location (Test of Strength)")]          // hub pad, StartCourage
    [InlineData("Trigger 2 (1)", "Target location (Test of Puzzles)")]       // hub pad, StartMind
    [InlineData("Trigger CourageTeleport", "Teleport From Tests")]           // the Strength wing's far-end pad, after the fight
    [InlineData("Trigger MindTeleporter1", "Teleport From Tests")]           // the Puzzles wing's far-end pad, after the puzzle
    [InlineData("Teleport", "Target location 0")]                            // the hub's TeleportAcross into the sanctum
    [InlineData("Trigger QuestCompleteTeleport", "Target location 0")]       // Get Smart's TempleTeleport (unchanged)
    public void TheTemplePadsLandAtTheTemplesOwnNamedLocations(string trigger, string location) {
        var (zone, loc) = Overlay(Temple)[trigger];
        Assert.Equal(Temple, zone);
        Near(NamedLocations()[location], loc);
    }

    [Fact]
    public void TheTemplesWayOutStillLeadsToTheWell() {
        var overlay = Overlay(Temple);
        Assert.Equal(Well, overlay["exit porter"].Zone);
        Assert.Equal(Well, overlay["KT-TeleporterToWellOfSpirits"].Zone);
    }
}
