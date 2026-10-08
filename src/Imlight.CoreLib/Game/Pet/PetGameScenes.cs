// CLASSIC: the phantom zones Cannon, Gobbler Drop and Maze play in, read from the client's own zone files.
//
// Each game's logic object (174074 Cannon, 174076 Drop, 210073 Maze) is placed in its own phantom zone, together with
// the scene the game needs: the Drop settings (GameSettings.xml, PetDropGameSettings) and its drop-spot path, the
// maze's snack path (Path 0, 135 nodes) and ghost spawners, a cannon range's target path and target spawner. The zone
// data marks them Minigame/HomeToFallback zones with a PlayerStart. The server reads the same files the client does.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pets;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>What a phantom-zone pet game needs from its zone.</summary>
internal sealed record PetGameScene(string Game, int Track, string Zone, uint LogicTemplate, Vector3 LogicLocation,
    IReadOnlyDictionary<string, Vector3> Locations, IReadOnlyList<Vector3> Spots, DropSettings Drop, uint TrackTemplate) {

    /// <summary>The named location, or the logic object's place when the zone has none of that name.</summary>
    public Vector3 Location(string name) => Locations.TryGetValue(name, out var at) ? at : LogicLocation;
}

internal static class PetGameScenes {

    internal const string PhantomPrefix = "ThePhantomZoneWorld/PetGame";

    /// <summary>The Cannon target (PetCannon_Target), spawned by each range's spawnData on one of its path's nodes.</summary>
    internal const uint CannonTargetTemplate = 209089;

    /// <summary>The maze's ghost (MazeGhost01), five to six of them walking the maze's patrol paths.</summary>
    internal const uint MazeGhostTemplate = 210075;
    internal const uint MazeClockTemplate = 210084;
    internal const uint MazeSpeedTemplate = 210082;
    internal const uint MazeStarTemplate = 210083;

    /// <summary>The snack of each maze track (Apples, Pizzas, Cake, Dog biscuits, Fish: MazeSnack01..05).</summary>
    internal static readonly uint[] MazeSnackTemplates = [210074, 195082, 195079, 195080, 195081];

    // Tests author scenes without client files.
    internal static readonly AsyncLocal<Func<string, int, PetGameScene>> TestScope = new();
    private static readonly ConcurrentDictionary<(string, int), PetGameScene> s_cache = new();

    /// <summary>True for the three games that are played in their own phantom zone.</summary>
    internal static bool IsPhantomGame(string game)
        => game is PetGameObjectCodec.Cannon or PetGameObjectCodec.Drop or PetGameObjectCodec.Maze;

    /// <summary>The zone a game and track are played in (a cannon track is its range: Wizard City .. Dragonspyre).</summary>
    internal static string ZoneFor(string game, int track) => game switch {
        PetGameObjectCodec.Cannon => $"ThePhantomZoneWorld/PetGameCannon/Range{Math.Clamp(track, 0, 4) + 1:00}",
        PetGameObjectCodec.Drop => "ThePhantomZoneWorld/PetGameDrop",
        PetGameObjectCodec.Maze => "ThePhantomZoneWorld/PetGameMaze",
        _ => null,
    };

    /// <summary>True when <paramref name="zone"/> is one of the pet games' phantom zones.</summary>
    internal static bool IsGameZone(string zone)
        => zone is not null && zone.StartsWith(PhantomPrefix, StringComparison.OrdinalIgnoreCase)
            && (zone.StartsWith(PhantomPrefix + "Cannon/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(zone, ZoneFor(PetGameObjectCodec.Drop, 0), StringComparison.OrdinalIgnoreCase)
                || string.Equals(zone, ZoneFor(PetGameObjectCodec.Maze, 0), StringComparison.OrdinalIgnoreCase));

    internal static bool TryLoad(string game, int track, out PetGameScene scene) {
        scene = null;
        if (!IsPhantomGame(game)) return false;
        if (TestScope.Value is { } test) {
            scene = test(game, track);
            return scene is not null;
        }
        if (s_cache.TryGetValue((game, track), out scene)) return true;
        try {
            scene = Load(game, track);
        }
        catch (Exception ex) {
            Logger.Error("Pet game {0} track {1}: its zone data could not be read: {2}", Logger.Args(game, track, ex.Message));
            scene = null;
        }
        if (scene is null) return false;
        s_cache[(game, track)] = scene;
        return true;
    }

    private static PetGameScene Load(string game, int track) {
        var zone = ZoneFor(game, track);
        if (!ResourceManager.TryLoadArchive(zone, out var wad)) {
            Logger.Error("Pet game {0}: the zone archive {1} is missing.", Logger.Args(game, zone));
            return null;
        }

        T Read<T>(string file) where T : PropertyClass {
            var bytes = wad.OpenFile(file)?.ToArray();
            if (bytes is null) return null;
            var serializer = new BindSerializer();
            return serializer.Deserialize<T>(bytes, 1, out var result) ? result : null;
        }

        var zoneData = Read<WizZoneData>("gamedata.bin");
        var paths = Read<PathTemplateList>("pathData.xml");
        var nodes = Read<NodeTemplateList>("pathNodeData.bin");
        var spawns = Read<SpawnManager>("spawnData.xml");
        if (zoneData is null || paths is null || nodes is null) {
            Logger.Error("Pet game {0}: {1} lacks its zone, path or node data.", Logger.Args(game, zone));
            return null;
        }

        var logicTemplate = PetGameObjectCodec.LogicTemplates[game];
        var logic = zoneData.m_objectList?.FirstOrDefault(o => o is not null && o.m_templateID.Full == logicTemplate);
        if (logic is null) {
            Logger.Error("Pet game {0}: {1} does not place the game's logic object {2}.", Logger.Args(game, zone, logicTemplate));
            return null;
        }

        var locations = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        foreach (var location in zoneData.m_locationList ?? []) {
            if (location is not null) locations.TryAdd(location.m_locName.ToString(), V(location.m_location));
        }

        var nodesById = (nodes.m_nodeList ?? []).Where(n => n is not null).GroupBy(n => n.m_id.Full).ToDictionary(g => g.Key, g => g.First());
        IReadOnlyList<Vector3> PathNodes(ulong pathId) {
            var path = paths.m_pathList?.FirstOrDefault(p => p is not null && p.m_id.Full == pathId);
            return path?.m_nodeIDs?.Where(id => nodesById.ContainsKey(id.Full)).Select(id => V(nodesById[id.Full].m_location)).ToList() ?? [];
        }

        DropSettings drop = null;
        IReadOnlyList<Vector3> spots = [];
        uint trackTemplate = 0;
        switch (game) {
            case PetGameObjectCodec.Drop: {
                var settings = Read<PetDropGameSettings>("GameSettings.xml");
                if (settings is null) {
                    Logger.Error("Pet game {0}: {1}/GameSettings.xml could not be read.", Logger.Args(game, zone));
                    return null;
                }
                drop = ToSettings(settings);
                spots = PathNodes(settings.m_uPathID);
                break;
            }
            case PetGameObjectCodec.Maze:
                // The snack layout is the maze path no spawner walks (Path 0); the others are the ghosts' patrols.
                var walked = (spawns?.m_spawners ?? []).SelectMany(s => s?.m_spawnList ?? [])
                    .Select(i => i?.m_objectInfo is null ? 0 : i.m_objectInfo.m_pathID.Full).ToHashSet();
                var snackPath = paths.m_pathList?.Where(p => p is not null && !walked.Contains(p.m_id.Full))
                    .OrderByDescending(p => p.m_nodeIDs?.Count ?? 0).FirstOrDefault();
                spots = snackPath is null ? [] : PathNodes(snackPath.m_id.Full);
                trackTemplate = MazeSnackTemplates[Math.Clamp(track, 0, MazeSnackTemplates.Length - 1)];
                break;
            case PetGameObjectCodec.Cannon:
                var targetPath = (spawns?.m_spawners ?? []).SelectMany(s => s?.m_spawnList ?? [])
                    .FirstOrDefault(i => i?.m_objectInfo?.m_templateID.Full == CannonTargetTemplate)?.m_objectInfo is { } targetInfo ? targetInfo.m_pathID.Full : 0;
                spots = PathNodes(targetPath);
                trackTemplate = CannonTargetTemplate;
                break;
        }

        if (spots.Count == 0) {
            Logger.Error("Pet game {0}: {1} has no usable spots for the game.", Logger.Args(game, zone));
            return null;
        }

        return new PetGameScene(game, track, zone, logicTemplate, V(logic.m_location), locations, spots, drop, trackTemplate);
    }

    private static DropSettings ToSettings(PetDropGameSettings s) => new(
        s.m_nFullnessScoreTarget, s.m_nTimeLimitInSeconds, s.m_nWeightChangeInSeconds, s.m_fDropsPerSecondAtStart,
        s.m_fDropsPerSecondAtEnd, s.m_fCatchDistance, s.m_fFreezeSpeed, s.m_fInitialItemHeight, s.m_fGroundHeight,
        s.m_fPetHeight, s.m_fJumpHeight, s.m_fMaxScaleIncrease,
        (s.m_foodItems ?? []).Where(f => f is not null).Select(f => new DropFood((uint) f.m_uTemplateID, f.m_nFoodGroup,
            f.m_fFallSpeed, f.m_nFullnessPoints, f.m_nTimeBonusInSeconds, f.m_nSpeedBonusInSeconds, f.m_nFreezeTimerInSeconds,
            f.m_fDropWeight, f.m_fWeightChange)).ToList());

    internal static Vector3 V(Imcodec.Math.Vector3 v) => new(v.X, v.Y, v.Z);

    internal static Imcodec.Math.Vector3 W(Vector3 v) => new(v.X, v.Y, v.Z);
}
