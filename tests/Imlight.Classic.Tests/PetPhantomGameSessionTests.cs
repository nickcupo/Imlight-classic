// CLASSIC: Cannon, Gobbler Drop and Maze through the actual pet service and its guarded parent: kiosk admission,
// the trip to the phantom zone, the logic object before INIT, each game loop, the acknowledged reward and the way back.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pets;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed partial class PetGameSessionAdmissionTests {
    private const string Cannon = "PetGameCannon", Drop = "PetGameDrop", Maze = "PetGameMaze";
    private const ulong WorldPet = (1UL << 40) + 793020;

    private static readonly (string Game, uint Id)[] PhantomLogicTemplates = [
        (Cannon, PetGameObjectCodec.CannonTemplate), (Drop, PetGameObjectCodec.DropTemplate), (Maze, PetGameObjectCodec.MazeTemplate)];
    private static readonly uint[] PieceTemplates = [193079, 193089, ..PetGameScenes.MazeSnackTemplates,
        PetGameScenes.MazeClockTemplate, PetGameScenes.MazeSpeedTemplate, PetGameScenes.MazeStarTemplate];

    private static readonly Vector3 CannonHome = new(13394, -84, -72), CannonTargetFoot = new(7183, -639, -7);
    private static readonly Vector3 DropSpot = new(40, 20, 52);

    // Authored scenes with the shapes of the client's zone files (no client data is read by these tests).
    private static PetGameScene AuthoredScene(string game, int track) => game switch {
        Cannon => new(game, track, PetGameScenes.ZoneFor(game, track), PetGameObjectCodec.CannonTemplate, CannonHome,
            new Dictionary<string, Vector3> { ["Home"] = CannonHome, ["PlayerStart"] = CannonHome + new Vector3(800, 0, 0) },
            [CannonTargetFoot], null!, PetGameScenes.CannonTargetTemplate),
        Drop => new(game, track, PetGameScenes.ZoneFor(game, track), PetGameObjectCodec.DropTemplate, new Vector3(-906, -13, 193),
            new Dictionary<string, Vector3>(), [DropSpot],
            new DropSettings(160, 40, 10, 1f, 2f, 60f, 0.5f, 900f, -100f, 150f, 200f, 1f, [new DropFood(193079, 0, 140, 4, 0, 0, 0, 100, 0)]), 0),
        Maze => new(game, track, PetGameScenes.ZoneFor(game, track), PetGameObjectCodec.MazeTemplate, new Vector3(-39, -3070, 153),
            new Dictionary<string, Vector3>(), [..Enumerable.Range(0, 12).Select(i => new Vector3(-2500 + i * 500, -4000, 0))], null!,
            PetGameScenes.MazeSnackTemplates[track]),
        _ => null!,
    };

    private static ushort Wire(float value) => unchecked((ushort) (short) (int) (value * 0.25f));

    private static MOVEBEHAVIOR_15_PROTOCOL.MSG_MB_MOVE Move(Vector3 at, ulong pet = WorldPet)
        => new() { LocationX = Wire(at.X), LocationY = Wire(at.Y), LocationZ = Wire(at.Z), GlobalID = pet };

    /// <summary>Joins at the kiosk, then completes the transfer's attach in the game's zone.</summary>
    private static async Task<IMessage[]> Arrive(Fixture f, string game, int track = 0) {
        var origin = f.Store.Live.Zone;
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEJOIN { Game = game, Track = track.ToString() });
        var joined = await f.Drain();
        Assert.Equal(1, Assert.Single(joined.OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        Assert.DoesNotContain(joined, p => p is GAME_5_PROTOCOL.MSG_NEWOBJECT or PET_9_PROTOCOL.MSG_PETGAMEINIT);
        await f.FanoutBarrier();
        Assert.True(f.Transfers.TryDequeue(out var transfer));
        Assert.Equal(PetGameScenes.ZoneFor(game, track), transfer.DestinationZone);
        Assert.Equal("PlayerStart", transfer.DestinationLocation);
        Assert.True(transfer.IsPrivate);
        Assert.Null((await f.State()).Session);

        // The new session's normal attach completes in the game's zone.
        f.Store.Live.PreviousZone = origin;
        f.Store.Live.Zone = transfer.DestinationZone;
        f.Instance.SummonedPetGlobalId = WorldPet;
        await f.CompleteAttach(f.Attach with { Zone = transfer.DestinationZone, Generation = f.Attach.Generation + 1 });
        await f.State(); await f.ParentBarrier(); await f.State();
        return await f.Drain();
    }

    private static async Task<object> Timer(Fixture f, string name) {
        var timers = await f.Timers();
        Assert.True(timers.TryGetValue(name, out var message), $"no {name} timer");
        return message;
    }

    private static byte[] Body(PET_9_PROTOCOL.MSG_PETGAMEDATA data) => (byte[]) data.Data;

    [Theory]
    [InlineData(Cannon, 2)] [InlineData(Drop, 0)] [InlineData(Maze, 3)]
    public async Task PhantomGamesTravelBeforeSetupAndPublishTheirLogicBeforeInitOnArrival(string game, int track) {
        using var f = await Fixture.Create();
        var origin = f.Store.Live.Zone;
        var setup = await Arrive(f, game, track);
        Assert.Equal(2, setup.Length);
        var logic = Assert.IsType<GAME_5_PROTOCOL.MSG_NEWOBJECT>(setup[0]);
        var raw = (byte[]) logic.Data;
        Assert.Equal((uint) PetGameObjectCodec.LogicTemplates[game], BitConverter.ToUInt32(raw, 2));
        var init = Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEINIT>(setup[1]);
        Assert.Equal(game, init.Game.ToString()); Assert.Equal((byte) track, init.Track);
        var state = await f.State();
        Assert.Equal(game, state.Game); Assert.False(state.Started);
        // The admitted game is consumed once: a repeated completion of the same scene does not set up again.
        await f.CompleteAttach(f.Attach);
        Assert.Empty(await f.Drain());
        Assert.Same(state.Session, (await f.State()).Session);
        // Closing before the end costs nothing and goes back to the kiosk spot.
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEENDING { Game = game });
        await f.FanoutBarrier();
        Assert.True(f.Transfers.TryDequeue(out var back));
        Assert.Equal(origin, back.DestinationZone);
        Assert.Equal(4, back.DestinationLocation.Split(',').Length);
        Assert.Null((await f.State()).Session);
        Assert.DoesNotContain(await f.Drain(), p => p is PET_9_PROTOCOL.MSG_PETGAMEEND);
        Assert.Equal(0, f.Store.Saves);
    }

    [Fact]
    public async Task AWizardInAGameZoneWithoutAnAdmittedGameIsSentBack() {
        using var f = await Fixture.Create();
        f.Store.Live.PreviousZone = "WizardCity/WC_Streets/Interiors/WC_PET_Park";
        f.Store.Live.Zone = PetGameScenes.ZoneFor(Maze, 0);
        await f.CompleteAttach(f.Attach with { Zone = f.Store.Live.Zone, Generation = f.Attach.Generation + 1 });
        await f.State(); await f.FanoutBarrier();
        Assert.True(f.Transfers.TryDequeue(out var back));
        Assert.Equal("WizardCity/WC_Streets/Interiors/WC_PET_Park", back.DestinationZone);
        Assert.Equal("Start", back.DestinationLocation);
        Assert.Null((await f.State()).Session);
        Assert.DoesNotContain(await f.Drain(), p => p is GAME_5_PROTOCOL.MSG_NEWOBJECT);
    }

    [Fact]
    public async Task APhantomJoinIsRefusedWithoutEnergyOrZoneDataAndKeepsTheCurrentGame() {
        using var f = await Fixture.Create();
        f.SceneLoader = (_, _) => null!;
        await f.Join(Maze);
        Assert.Equal(0, Assert.Single((await f.Drain()).OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        f.SceneLoader = AuthoredScene;
        f.Store.Saved.PetOwnerBehavior.SetEnergy(0);
        await f.Join(Drop);
        Assert.Equal(0, Assert.Single((await f.Drain()).OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        await f.FanoutBarrier();
        Assert.Empty(f.Transfers);
        Assert.False(PetGameTransfers.TryConsume(793010UL, f.Store.Live.CharId, PetGameScenes.ZoneFor(Drop, 0), DateTime.UtcNow, out _));
    }

    [Fact]
    public async Task CannonOpensWithOriginBeforeStartScoresEachFlownShotAndRewardsOnce() {
        using var f = await Fixture.Create();
        f.Store.Live.Account.AuthLevel = AuthLevel.None;
        await Arrive(f, Cannon);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY());
        Assert.Empty(await f.Drain()); // Waiting for the zone to say where its target stands.
        await f.Fire(await Timer(f, "petGameTarget"));
        var opening = await f.Drain();
        Assert.Equal(3, opening.Length);
        var origin = Body(Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEDATA>(opening[0]));
        Assert.Equal(13, origin.Length); Assert.Equal(12, origin[0]);
        Assert.Equal(CannonHome, new Vector3(BitConverter.ToSingle(origin, 1), BitConverter.ToSingle(origin, 5), BitConverter.ToSingle(origin, 9)));
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMESTART>(opening[1]);
        var wind = Body(Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEDATA>(opening[2]));

        var centre = CannonTargetFoot + new Vector3(0, 0, PetMinigameRules.CannonTargetCentreHeight);
        for (var shot = 1; shot <= PetMinigameRules.CannonShots; shot++) {
            Assert.Equal(25, wind.Length); Assert.Equal(14, wind[0]);
            var accel = new Vector3(BitConverter.ToSingle(wind, 13), BitConverter.ToSingle(wind, 17), BitConverter.ToSingle(wind, 21));
            var a = accel - new Vector3(0, 0, PetMinigameRules.CannonGravity);
            const float t = 3f;
            var v = (centre - CannonHome - 0.5f * a * t * t) / t;
            byte[] fire = [15, ..BitConverter.GetBytes(v.X), ..BitConverter.GetBytes(v.Y), ..BitConverter.GetBytes(v.Z)];
            await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Cannon, Data = new ByteString(fire) });
            var result = Body(Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEDATA>(Assert.Single(await f.Drain())));
            Assert.Equal(9, result.Length); Assert.Equal(17, result[0]);
            Assert.Equal(shot, BitConverter.ToInt32(result, 1)); Assert.Equal(3, BitConverter.ToInt32(result, 5));
            // A second fire for the same shot is refused.
            await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Cannon, Data = new ByteString(fire) });
            Assert.Empty(await f.Drain());
            Assert.Equal(0, f.Store.Saves);
            await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Cannon, Data = new ByteString([13]) });
            if (shot < PetMinigameRules.CannonShots) wind = Body(Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEDATA>(Assert.Single(await f.Drain())));
        }

        var end = await f.Drain();
        Assert.Single(end.OfType<PET_9_PROTOCOL.MSG_PETGAMEEND>());
        Assert.Equal(1, f.Store.Saves);
        Assert.Equal(4u, f.PreparedEnd!.m_wins); // Three Perfect scored shots: the full four points.
        Assert.True((await f.State()).Ended);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = Cannon, Data = new ByteString([13]) });
        Assert.Empty(await f.Drain());
        Assert.Equal(1, f.Store.Saves);
        await Timer(f, "petGameReturn");
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEENDING { Game = Cannon });
        await f.FanoutBarrier();
        Assert.True(f.Transfers.TryDequeue(out _));
    }

    [Fact]
    public async Task CannonUsesTheTargetTheZoneReports() {
        using var f = await Fixture.Create();
        await Arrive(f, Cannon, 4);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY());
        var reported = new Vector3(9000, 100, 20);
        await f.Send(new ZONE_102_PROTOCOL.MSG_TEMPLATEOBJECTLOCATION { GlobalId = 5, TemplateId = PetGameScenes.CannonTargetTemplate,
            Location = PetGameScenes.W(reported) });
        var opening = await f.Drain();
        Assert.Equal(3, opening.Length);
        Assert.DoesNotContain(await f.Timers(), t => Equals(t.Key, "petGameTarget"));
        var wind = Body((PET_9_PROTOCOL.MSG_PETGAMEDATA) opening[2]);
        var bound = BitConverter.ToSingle(wind, 9);
        Assert.Equal(Vector2.Distance(new(CannonHome.X, CannonHome.Y), new(reported.X, reported.Y)) + PetMinigameRules.CannonDistanceMargin, bound, 1f);
    }

    [Fact]
    public async Task DropSpawnsFoodCatchesItUnderThePetAndEndsOnTheServerClock() {
        using var f = await Fixture.Create();
        f.Store.Live.Account.AuthLevel = AuthLevel.None;
        await Arrive(f, Drop);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY());
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMESTART>(Assert.Single(await f.Drain()));
        await f.Send(Move(DropSpot));
        await f.Send(Move(new Vector3(9000, 0, 0), pet: WorldPet + 1)); // Not the summoned pet: ignored.

        var all = new List<IMessage>();
        for (var i = 0; i < 600 && (await f.State()) is { Ended: false }; i++) {
            var timers = await f.Timers();
            // A fired single timer stays in the recording proxy; the finish timer is armed only at the end.
            if (timers.TryGetValue("petGameFinish", out var finish)) await f.Fire(finish);
            else if (timers.TryGetValue("petGameTick", out var tick)) await f.Fire(tick);
            else break;
            all.AddRange(await f.Drain());
        }

        var drops = all.OfType<PET_9_PROTOCOL.MSG_PETGAMEDROPOBJECT>().ToList();
        var spawned = drops.Where(d => d.GameCommand == 21).Select(d => (ulong) d.GID).ToList();
        var caught = drops.Where(d => d.GameCommand == 22).Select(d => (ulong) d.GID).ToList();
        Assert.True(spawned.Count >= 40, $"{spawned.Count} spawned");
        Assert.NotEmpty(caught);
        Assert.All(caught, gid => Assert.Contains(gid, spawned));
        Assert.Equal(spawned.Count, all.OfType<GAME_5_PROTOCOL.MSG_NEWOBJECT>().Count());
        var scales = all.OfType<PET_9_PROTOCOL.MSG_PETGAMEDROPBONUS>().Where(b => b.GameCommand == 18).Select(b => b.Bonus).ToList();
        Assert.Equal(caught.Count, scales.Count);
        Assert.Equal(scales.OrderBy(x => x), scales);
        Assert.Single(all.OfType<PET_9_PROTOCOL.MSG_PETGAMEDROPBONUS>(), b => b.GameCommand == 20);
        Assert.Single(all.OfType<PET_9_PROTOCOL.MSG_PETGAMEEND>());
        Assert.Equal(1, f.Store.Saves);
        var fullness = Math.Min(160, caught.Count * 4);
        Assert.Equal(PetRules.GamePoints(fullness, 160), (int) f.PreparedEnd!.m_wins);
    }

    [Fact]
    public async Task MazePlacesItsPiecesJudgesPickupsAndGhostsFromThePetsMovesAndEndsOnItsClock() {
        using var f = await Fixture.Create();
        f.Store.Live.Account.AuthLevel = AuthLevel.None;
        await Arrive(f, Maze);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY());
        var opening = await f.Drain();
        Assert.Equal(12, opening.Count(p => p is GAME_5_PROTOCOL.MSG_NEWOBJECT));
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMESTART>(opening[^2]);
        var start = Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEMAZE>(opening[^1]);
        Assert.Equal(0, start.GameCommand);

        var events = new List<PET_9_PROTOCOL.MSG_PETGAMEMAZE>();
        foreach (var spot in AuthoredScene(Maze, 0).Spots) {
            await f.Send(Move(spot));
            events.AddRange((await f.Drain()).OfType<PET_9_PROTOCOL.MSG_PETGAMEMAZE>());
        }
        var snacks = events.Where(e => e.GameCommand == 6).ToList();
        Assert.Equal(12 - PetMinigameRules.MazeClocks - PetMinigameRules.MazeSpeedBoosts - PetMinigameRules.MazeStars, snacks.Count);
        Assert.Equal(Enumerable.Range(1, snacks.Count).Select(i => (byte) i), snacks.Select(e => e.GameData));
        Assert.Equal(PetMinigameRules.MazeClocks, events.Count(e => e.GameCommand == 10));
        Assert.Equal(PetMinigameRules.MazeStars, events.Count(e => e.GameCommand == 11));
        Assert.All(events, e => Assert.NotEqual(0UL, (ulong) e.ObjectID));

        // A ghost reported by the zone at the pet's spot: the last star still protects the pet, so it is eaten.
        var last = AuthoredScene(Maze, 0).Spots[^1];
        await f.Send(new ZONE_102_PROTOCOL.MSG_TEMPLATEOBJECTLOCATION { GlobalId = 99, TemplateId = PetGameScenes.MazeGhostTemplate,
            Location = PetGameScenes.W(last) });
        await f.Send(Move(last));
        var ghost = Assert.Single((await f.Drain()).OfType<PET_9_PROTOCOL.MSG_PETGAMEMAZE>());
        Assert.Equal(8, ghost.GameCommand); Assert.Equal(99UL, (ulong) ghost.ObjectID);

        var all = new List<IMessage>();
        for (var i = 0; i < 1200 && (await f.State()) is { Ended: false }; i++) {
            var timers = await f.Timers();
            // A fired single timer stays in the recording proxy; the finish timer is armed only at the end.
            if (timers.TryGetValue("petGameFinish", out var finish)) await f.Fire(finish);
            else if (timers.TryGetValue("petGameTick", out var tick)) await f.Fire(tick);
            else break;
            all.AddRange(await f.Drain());
        }
        Assert.Equal(12, all.Count(p => p is GAME_5_PROTOCOL.MSG_DELETEOBJECT)); // Every eaten piece is taken away.
        Assert.Single(all.OfType<PET_9_PROTOCOL.MSG_PETGAMEEND>());
        Assert.Equal(1, f.Store.Saves);
        Assert.Equal(PetRules.GamePoints(snacks.Count, PetMinigameRules.MazeFullScore), (int) f.PreparedEnd!.m_wins);
    }

    [Fact]
    public async Task APhantomGameWhoseSceneIsLostStopsWithoutOutputOrSaving() {
        using var f = await Fixture.Create();
        await Arrive(f, Drop);
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEREADY()); await f.Drain();
        var tick = await Timer(f, "petGameTick");
        f.LoseContext("transfer");
        await f.Fire(tick);
        Assert.Null((await f.State()).Session);
        Assert.Empty(await f.Drain());
        Assert.Equal(0, f.Store.Saves);
    }

    [Fact]
    public void ThePendingGameIsBoundToItsAccountCharacterAndZoneAndConsumedOnce() {
        var registry = new PetGameTransferRegistry(lifetime: TimeSpan.FromMinutes(1));
        var now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        var entry = new PendingPetGame(1, 2, Maze, 0, 3, "ThePhantomZoneWorld/PetGameMaze", "WizardCity/WC_Hub", "1,2,3,0", default);
        Assert.False(registry.Queue(entry with { PetItem = 0 }, now));
        Assert.True(registry.Queue(entry, now));
        Assert.False(registry.TryConsume(9, 2, entry.Zone, now, out _));
        Assert.False(registry.TryConsume(1, 2, "ThePhantomZoneWorld/PetGameDrop", now, out _));
        Assert.True(registry.TryConsume(1, 2, "thephantomzoneworld/petgamemaze", now, out var found));
        Assert.Equal(entry.OriginLocation, found.OriginLocation);
        Assert.False(registry.TryConsume(1, 2, entry.Zone, now, out _));
        Assert.True(registry.Queue(entry, now));
        Assert.False(registry.TryConsume(1, 2, entry.Zone, now + TimeSpan.FromMinutes(2), out _));
    }

    [Fact]
    public void PetMovesDecodeTheNativeQuarterCoordinates() {
        Assert.Equal(new Vector3(-2500, 4000, 52), Imlight.CoreLib.Game.Services.PetGameService.DecodeMove(Wire(-2500), Wire(4000), Wire(52)));
        Assert.True(PetGameScenes.IsGameZone("ThePhantomZoneWorld/PetGameCannon/Range03"));
        Assert.True(PetGameScenes.IsGameZone("thephantomzoneworld/petgamemaze"));
        Assert.False(PetGameScenes.IsGameZone("ThePhantomZoneWorld/PetGameDance"));
        Assert.False(PetGameScenes.IsGameZone("WizardCity/WC_Hub"));
        Assert.Equal("ThePhantomZoneWorld/PetGameCannon/Range05", PetGameScenes.ZoneFor(Cannon, 4));
    }
}

public sealed partial class PetGameSessionAdmissionTests {

    private static async Task<bool> Eventually(Func<bool> condition) {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(30, TestContext.Current.CancellationToken);
        return condition();
    }

    // Live a2b88dd9 trusted ATTACHCOMPLETE only if WizardService had already recorded the attach's world object; the zone
    // answers both in parallel, so a fresh session's first attach was never trusted (rig: every Dance join refused).
    [Fact]
    public async Task ACompletionBeforeTheWorldObjectIsRecordedIsTrustedOnceItIsAndFansOutOnce() {
        using var f = await Fixture.Create();
        await f.FanoutBarrier();
        var fanouts = f.AttachFanout.Count;
        ActiveWizardDirectory.SetGameObject(f.Endpoint, null!);
        var next = f.Attach with { Generation = f.Attach.Generation + 1 };
        await f.CompleteAttach(next);
        Assert.False(f.Instance.TryCapturePetGameAttach(f.Store.Live, out _));
        ActiveWizardDirectory.SetGameObject(f.Endpoint, f.Store.Live.GameObject);
        Assert.True(await Eventually(() => f.Instance.TryCapturePetGameAttach(f.Store.Live, out var c) && c.Attach.Generation == next.Generation));
        await f.FanoutBarrier();
        Assert.Equal(fanouts + 1, f.AttachFanout.Count); // The retry re-evaluates trust only.
        await f.Join(Dance);
        var setup = await f.Drain();
        Assert.Equal(1, Assert.Single(setup.OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        Assert.Single(setup.OfType<PET_9_PROTOCOL.MSG_PETGAMEINIT>());
    }

    [Fact]
    public async Task APhantomArrivalWaitsForTheTrustedSceneOrGoesBackAfterItsWait() {
        using var f = await Fixture.Create();
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEJOIN { Game = Maze, Track = "0" });
        await f.Drain(); await f.FanoutBarrier(); Assert.True(f.Transfers.TryDequeue(out var transfer));
        var origin = f.Store.Live.Zone;
        f.Store.Live.Zone = transfer.DestinationZone;
        ActiveWizardDirectory.SetGameObject(f.Endpoint, null!);
        await f.CompleteAttach(f.Attach with { Zone = transfer.DestinationZone, Generation = f.Attach.Generation + 1 });
        await f.State();
        Assert.DoesNotContain(await f.Drain(), p => p is PET_9_PROTOCOL.MSG_PETGAMEINIT);
        var wait = await Timer(f, "petGameArrival");
        ActiveWizardDirectory.SetGameObject(f.Endpoint, f.Store.Live.GameObject);
        Assert.True(await Eventually(() => f.Packets.Any(p => p is PET_9_PROTOCOL.MSG_PETGAMEINIT)),
            string.Join(",", f.Ingress.Select(i => i.Type.Name)) + " | " + string.Join(",", f.Packets.Select(p => p.GetType().Name))
            + " | trusted=" + f.Instance.TryCapturePetGameAttach(f.Store.Live, out _));
        Assert.Equal(Maze, (await f.State()).Game);
        await f.Fire(wait); // A late wait changes nothing once the game is set up.
        Assert.Equal(Maze, (await f.State()).Game);
        await f.FanoutBarrier();
        Assert.Empty(f.Transfers);

        // A second trip whose scene is never trusted gives up and goes back.
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEENDING { Game = Maze }); await f.FanoutBarrier(); f.Transfers.Clear();
        f.Store.Live.Zone = origin;
        await f.CompleteAttach(f.Attach with { Zone = origin, Generation = f.Attach.Generation + 1 }); // Back at the kiosk.
        await f.Send(new PET_9_PROTOCOL.MSG_PETGAMEJOIN { Game = Maze, Track = "0" });
        await f.FanoutBarrier(); Assert.True(f.Transfers.TryDequeue(out _)); await f.Drain();
        f.Store.Live.Zone = transfer.DestinationZone;
        ActiveWizardDirectory.SetGameObject(f.Endpoint, null!);
        await f.CompleteAttach(f.Attach with { Zone = transfer.DestinationZone, Generation = f.Attach.Generation + 1 });
        await f.Fire(await Timer(f, "petGameArrival"));
        await f.FanoutBarrier();
        Assert.True(f.Transfers.TryDequeue(out var back));
        Assert.Equal(origin, back.DestinationZone);
        Assert.Null((await f.State()).Session);
    }
}
