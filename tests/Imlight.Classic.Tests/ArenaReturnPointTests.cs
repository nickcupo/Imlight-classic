// CLASSIC: the arenas' exits land on the 2014 Wizard City Arena floor (classic-data/spiraldb-overlay/ZoneTransfer/
// *.arena-return.json, tools/classic-quests/gen_arena_returns.py). tools/classic-zones.list installs the 2014 package of
// WizardCity/WC_Duel_Arena, whose only landing points are 'Start' and the hub entrance; SpiralDB's hall coordinates are
// the r806919 remodel's and land wizards outside the hall.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using Imlight.Classic.Pvp;
using Imlight.Common;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.World;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ArenaReturnPointTests {

    private const string Hall = "WizardCity/WC_Duel_Arena";
    private const string Start = "-10.99457,-4377.493,579.1199,-3.117046";   // 2014 WC_Duel_Arena gamedata 'Start'

    private static string OverlayDir => Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "ZoneTransfer");

    private static void EnsureConfiguration() {
        try { ConfigurationManager.GetSetting("Logging.LogLevel"); }
        catch (InvalidOperationException) {
            var config = Path.GetTempFileName();
            try {
                File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-arena-return-tests.log")}\n");
                ConfigurationManager.Initialize(config);
            }
            finally { File.Delete(config); }
        }
    }

    private static void LoadZoneData(string root, ConcurrentDictionary<string, WizardZoneData> target) {
        var method = typeof(SpiralDB).GetMethod("LoadZoneData", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [root, target]);
    }

    [Fact]
    public void EveryArenaReturnRecordLandsOnTheHallStart() {
        EnsureConfiguration();
        var files = Directory.GetFiles(OverlayDir, "*.arena-return.json");
        Assert.NotEmpty(files);
        var loaded = new ConcurrentDictionary<string, WizardZoneData>(StringComparer.Ordinal);
        LoadZoneData(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay"), loaded);
        foreach (var file in files) {
            Assert.Contains("\"Merge\": true", File.ReadAllText(file));      // other exits of the zone stay SpiralDB's
            Assert.True(File.Exists(file[..^".json".Length] + ".provenance.yaml"), $"{Path.GetFileName(file)} has no provenance");
            var zone = Path.GetFileName(file)[..^".arena-return.json".Length];
            var record = loaded.Values.Single(z => z.ZoneName.Replace('/', '-') == zone);
            var exits = record.Teleports.Where(t => File.ReadAllText(file).Contains($"\"{t.TriggerName}\"")).ToList();
            Assert.NotEmpty(exits);
            Assert.All(exits, t => Assert.Equal((Hall, Start), ((string) t.Teleport.m_destinationZone, t.Teleport.m_destinationLoc)));
        }
    }

    [Fact]
    public void EveryArenaInEveryRotationHasItsExitMapped() {
        foreach (var path in Directory.GetFiles(Path.Combine(ClassicDataFixture.Root, "pvp"), "arena-*.yaml")) {
            var config = ArenaLoader.Load(path);
            Assert.Equal(Hall, config.HallZone);
            Assert.Equal("Start", config.HallLocation);
            foreach (var arena in config.Arenas) {
                Assert.True(File.Exists(Path.Combine(OverlayDir, arena.Zone.Replace('/', '-') + ".arena-return.json")),
                    $"{arena.Zone} ({Path.GetFileName(path)}) has no arena-return overlay");
            }
        }
    }

    [Fact]
    public void MergeMovesOnlyTheExitAndKeepsTheZonesOtherTeleports() {
        EnsureConfiguration();
        var spiral = Directory.CreateTempSubdirectory("w101c-arena-return-");
        try {
            Directory.CreateDirectory(Path.Combine(spiral.FullName, "ZoneTransfer"));
            // SpiralDB's MB_Arena exit (WizardZoneDatas_6273-A.json) plus another entry the overlay must keep.
            File.WriteAllText(Path.Combine(spiral.FullName, "ZoneTransfer", "WizardZoneDatas_6273-A.json"), """
                {"ZoneName": "WizardCity/MB_Arena", "Events": [], "Teleports": [
                  {"TriggerName": "TeleportToCommons", "Teleport": {"m_exitTeleporter": 0, "m_teleporterTag": 0,
                    "m_teleportType": "TELEPORT_STATIC", "m_transitionID": 0,
                    "m_destinationLoc": "5.222679,-9556.717,408.5226,-3.103143", "m_destinationZone": "WizardCity/WC_Duel_Arena"}},
                  {"TriggerName": "Some other door", "Teleport": {"m_exitTeleporter": 0, "m_teleporterTag": 0,
                    "m_teleportType": "TELEPORT_STATIC", "m_transitionID": 0,
                    "m_destinationLoc": "1,2,3,0", "m_destinationZone": "WizardCity/WC_Hub"}}]}
                """);
            var loaded = new ConcurrentDictionary<string, WizardZoneData>(StringComparer.Ordinal);
            LoadZoneData(spiral.FullName, loaded);
            LoadZoneData(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay"), loaded);

            var zone = loaded["WizardCity/MB_Arena"];
            Assert.Equal(["TeleportToCommons", "Some other door"], zone.Teleports.Select(t => t.TriggerName));
            var exit = zone.Teleports[0].Teleport;
            Assert.Equal((Hall, Start), ((string) exit.m_destinationZone, exit.m_destinationLoc));
            var other = zone.Teleports[1].Teleport;
            Assert.Equal(("WizardCity/WC_Hub", "1,2,3,0"), ((string) other.m_destinationZone, other.m_destinationLoc));

            // The Grizzleheim arena's exit led to the r806919 remodel (WC_Duel_Arena_New); it now returns to the hall.
            var gh = loaded["WizardCity/GH_Arena"].Teleports.Single(t => t.TriggerName == "ToLobby").Teleport;
            Assert.Equal((Hall, Start), ((string) gh.m_destinationZone, gh.m_destinationLoc));
        }
        finally {
            spiral.Delete(true);
        }
    }
}
