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
 * CLASSIC PET GAME SERVICE: CANNON, GOBBLER DROP AND MAZE
 * ========================================================================
 *
 * PURPOSE:
 * The three Pavilion games that are played in their own phantom zone:
 * admission at the kiosk, the trip into the game's private zone, the native
 * logic object before INIT, each game's loop and score, the acknowledged
 * reward, and the trip back to the kiosk.
 *
 * USAGE EXAMPLE:
 * Kiosk: MSG_PETGAMEJOIN(Game, Track) -> MSG_PETGAMEJOINRSP(1), transfer to
 * ThePhantomZoneWorld/PetGame...(PlayerStart). After the attach there:
 * MSG_NEWOBJECT(logic 174074/174076/210073) -> MSG_PETGAMEINIT; client READY ->
 * Cannon: DATA 12(origin), START, DATA 14(wind); client DATA 15(velocity) ->
 *   DATA 17(shot, result); client DATA 13 -> next DATA 14 ... -> END.
 * Drop: START, then falling food (NEWOBJECT + DROPOBJECT 21), catches (22),
 *   misses (23), gobbler size (DROPBONUS 18), speed (19), time up (20) -> END.
 * Maze: snacks and bonuses (NEWOBJECT), START, MAZE 0; pickups (6, 9, 10, 11),
 *   ghosts (7, 8) judged from the pet's MSG_MB_MOVE -> END.
 * Client MSG_PETGAMEENDING -> back to the kiosk.
 *
 * NOTE:
 * The native evidence (docs/playtest/2026-10-07-pet-game-native-contracts.md)
 * proves the client's message bodies; it does not prove the scene setup, which
 * of the Drop object commands 21/22/23 is spawn/catch/miss, the Cannon gravity,
 * or the historical scores. Those are marked UNVERIFIED here and in
 * classic-data/rules/pet-minigames-2010.yaml; the owner checks them in game.
 * Every game ends on the server's clock even if the client stops talking,
 * and the wizard is always sent back.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/08/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pets;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal sealed partial class PetGameService {

    private const byte CannonReady = 13, CannonFire = 15, CannonOrigin = 12, CannonWindData = 14, CannonResultData = 17;
    private const byte DropScale = 18, DropSpeed = 19, DropTimesUp = 20, DropSpawned = 21, DropCaught = 22, DropMissed = 23;
    private const byte MazeStart = 0, MazeSnack = 6, MazeFrozen = 7, MazeGhostEaten = 8, MazeSpeed = 9, MazeClock = 10, MazeStar = 11;
    private const string PhantomTick = "petGameTick", PhantomTarget = "petGameTarget", PhantomFinish = "petGameFinish",
        PhantomReturn = "petGameReturn", PhantomIdle = "petGameIdle";

    /// <summary>The server's game clock step.</summary>
    internal static readonly TimeSpan PhantomStep = TimeSpan.FromMilliseconds(100);
    /// <summary>How often the maze asks its zone where the ghosts are (every this many ticks).</summary>
    private const int GhostQueryTicks = 2;
    /// <summary>How long a placed piece stays after it is eaten or lands, so the client can play its effect.</summary>
    private const double PieceLingerSeconds = 1.5;
    /// <summary>How long the Cannon target query may take before the first path node is used instead.</summary>
    private static readonly TimeSpan TargetWait = TimeSpan.FromSeconds(2);
    /// <summary>After the result, the wizard goes back even if the client never closes the game window.</summary>
    private static readonly TimeSpan ReturnWait = TimeSpan.FromSeconds(90);
    /// <summary>A Cannon game nobody fires in this long ends with the shots taken.</summary>
    private static readonly TimeSpan CannonIdle = TimeSpan.FromMinutes(3);

    // CLASSIC: one tick belongs to one admitted game.
    private sealed record PhantomTickMessage(Session Session, long Sequence);
    private sealed record PhantomTargetTimeout(Session Session);
    private sealed record PhantomFinishMessage(Session Session);
    private sealed record PhantomReturnMessage(Session Session);
    private sealed record PhantomIdleMessage(Session Session, int Shots);

    /// <summary>A phantom-zone game's state inside its Session.</summary>
    private sealed class PhantomState {
        public PetGameScene Scene;
        public PendingPetGame Origin;
        public CannonGame Cannon;
        public DropGame Drop;
        public MazeGame Maze;
        public long Tick;
        public Vector3? Pet;
        public readonly Dictionary<int, ulong> Pieces = [];
        public readonly List<(double Due, ulong Gid)> Linger = [];
        public readonly Dictionary<ulong, Vector3> Ghosts = [];
        public Vector3? Target;
        public bool Returned;
        public bool Over; // The game's own end was reached (its result is being saved or was refused).
        public double Clock;
    }

    // ------------------------------------------------------------------ admission at the kiosk

    /// <summary>A JOIN of Cannon, Gobbler Drop or Maze at its kiosk: admit it here, then travel to the game's zone.</summary>
    private void JoinPhantom(PET_9_PROTOCOL.MSG_PETGAMEJOIN message, string game, Wizard wizard, PetGameInfo info) {
        void Refuse(string why, string tell = null) {
            Logger.Information("Pet game {0}: refused ({1}).", Logger.Args(game, why));
            if (tell is not null) InformGameClient(tell);
            SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 0 });
        }

        if (PetGameScenes.IsGameZone(wizard.Zone)) { Refuse("already in a pet game zone"); return; }
        _ = int.TryParse(message.Track.ToString(), out var track);
        track = Math.Clamp(track, 0, Math.Max(0, (info.m_trackChoices?.Count ?? 1) - 1));
        var zone = PetGameScenes.ZoneFor(game, track);
        var account = GetActiveAccount();
        if (account is null || zone is null || !PetGameScenes.TryLoad(game, track, out _)) { Refuse("its zone data is not available"); return; }
        if (!SessionActor.TryCapturePetGameAttach(wizard, out var attach)) { Refuse("no completed attachment"); return; }

        var selectedPet = EquippedPet(wizard);
        WizClientObjectItem pet;
        int energy;
        try {
            if (!ClassicPetProgressTransactions.TryInitializeForGame(wizard, selectedPet?.m_globalID.Full ?? 0, out pet, out energy,
                () => SessionActor.MatchesPetGameAttach(attach))) {
                if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
                Refuse("no fresh equipped pet", "Equip a pet to play the pet games.");
                return;
            }
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) CloseSession();
            throw;
        }

        var b = PetProgress.Behavior(pet);
        if (b is null || b.m_level == 0) { Refuse("no hatched pet", "Equip a pet to play the pet games."); return; }
        var cost = PetRules.EnergyCost(b.m_level);
        if (energy < cost) { Refuse($"energy {energy} < {cost}", "Your pet is too tired to play."); return; }

        var origin = Util.GetCompactStringFromVector(wizard.Location, wizard.Orientation);
        var pending = new PendingPetGame(account.AccountId, wizard.CharId, game, track, pet.m_globalID.Full, zone,
            wizard.Zone, origin, default);
        if (!PetGameTransfers.Queue(pending, DateTime.UtcNow)) { Refuse("the transfer could not be queued"); return; }

        Logger.Information("Pet game {0} track {1}: {2} joins with pet {3} (level {4}, energy {5}); going to {6}.",
            Logger.Args(game, track, wizard.CharId, pet.m_globalID.Full, b.m_level, energy, zone));
        LeaveMorph();
        RetireTraining();
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = game, Success = 1 });
        // A private copy of the game's zone; the wizard stands at its PlayerStart, out of the game's way.
        Teleport(zone, doTeleportEffects: false, makePrivate: true, destinationLocation: "PlayerStart");
    }

    // ------------------------------------------------------------------ arrival in the game's zone

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceivePhantomAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        if (_closing || !Enabled) return;
        var wizard = GetActiveWizard();
        if (wizard is null || !PetGameScenes.IsGameZone(wizard.Zone)) return;
        if (_session?.Phantom is not null || _pendingJoin is not null) return; // A repeated completion of the same scene.

        var account = GetActiveAccount();
        if (account is null || !PetGameTransfers.TryConsume(account.AccountId, wizard.CharId, wizard.Zone, DateTime.UtcNow, out var pending)) {
            // A wizard in a game zone with no admitted game (a relog or a stale transfer) is sent back out.
            Logger.Information("Pet game zone {0}: {1} has no admitted game here; sending them back.", Logger.Args(wizard.Zone, wizard.CharId));
            SendHome(null, wizard);
            return;
        }

        void Abandon(string why) {
            Logger.Warning("Pet game {0}: setup refused in {1} ({2}); sending {3} back.", Logger.Args(pending.Game, pending.Zone, why, wizard.CharId));
            InformGameClient("The pet game could not be set up. Please try again.");
            SendHome(pending, wizard);
        }

        if (!SessionActor.TryCapturePetGameAttach(wizard, out var attach)) { Abandon("no completed attachment"); return; }
        if (!PetGameConfigs.TryGet(pending.Game, out var info) || !PetGameInitializationCodec.TryPrepare(info, out var initData)) {
            Abandon("no game configuration"); return;
        }
        if (!PetGameScenes.TryLoad(pending.Game, pending.Track, out var scene)) { Abandon("no zone data"); return; }
        if (!PetGameObjectCodec.TryPrepareLogic(pending.Game, attach.World, PetGameScenes.W(scene.LogicLocation), out var logic)) {
            Abandon("the native logic object could not be prepared"); return;
        }

        WizClientObjectItem pet;
        int energy;
        try {
            // CLASSIC: re-read the fresh equipped pet after the transfer; the kiosk's admission alone is not enough.
            if (!ClassicPetProgressTransactions.TryInitializeForGame(wizard, pending.PetItem, out pet, out energy,
                () => SessionActor.MatchesPetGameAttach(attach)) || pet.m_globalID.Full != pending.PetItem) {
                if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
                Abandon("the admitted pet is no longer the fresh equipped pet");
                return;
            }
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) CloseSession();
            throw;
        }
        var b = PetProgress.Behavior(pet);
        if (b is null || b.m_level == 0 || energy < PetRules.EnergyCost(b.m_level)) { Abandon("pet level or energy changed"); return; }

        var candidate = new Session {
            Game = pending.Game, Track = pending.Track, PetId = pet.m_globalID, Context = attach,
            Phantom = new PhantomState { Scene = scene, Origin = pending },
        };
        var init = new PET_9_PROTOCOL.MSG_PETGAMEINIT { Game = pending.Game, Data = initData, MinLevel = 0, Track = (byte) pending.Track };
        var token = new object();
        _pendingJoin = new(token, candidate, attach);
        Logger.Information("Pet game {0} track {1}: {2} arrived in {3}; publishing logic {4} before INIT.",
            Logger.Args(pending.Game, pending.Track, wizard.CharId, pending.Zone, logic.GlobalId));
        SessionActor.ActorRef.Tell(new PetGamePublication(token, attach, logic, init, SendJoinResponse: false), Self);
    }

    /// <summary>Sends the wizard back where the game was joined (or out of a game zone to the zone before it).</summary>
    private void SendHome(PendingPetGame origin, Wizard wizard = null) {
        wizard ??= GetActiveWizard();
        var zone = origin?.OriginZone;
        var location = origin?.OriginLocation;
        if (string.IsNullOrWhiteSpace(zone) || PetGameScenes.IsGameZone(zone)) {
            zone = wizard?.PreviousZone;
            location = "Start";
        }
        if (string.IsNullOrWhiteSpace(zone) || PetGameScenes.IsGameZone(zone) || zone.Contains("Phantom", StringComparison.OrdinalIgnoreCase)) {
            zone = "WizardCity/WC_Hub";
            location = "Start";
        }
        Teleport(zone, doTeleportEffects: false, destinationLocation: location ?? "Start");
    }

    // ------------------------------------------------------------------ the games

    /// <summary>READY for a phantom game: each game's opening, in the native order.</summary>
    private void StartPhantom(Session session) {
        var state = session.Phantom;
        var random = Random.Shared;
        switch (session.Game) {
            case PetGameObjectCodec.Cannon:
                // The origin (command 12) must reach the client before START sets up the scene; the target's place
                // comes from the zone (its spawner put it on one of the range's nodes).
                SessionActor.GetZoneActor()?.Tell(new ZONE_102_PROTOCOL.MSG_QUERYTEMPLATEOBJECTS {
                    TemplateIds = [PetGameScenes.CannonTargetTemplate], Requester = Self }, Self);
                Timers.StartSingleTimer(PhantomTarget, new PhantomTargetTimeout(session), TargetWait);
                break;
            case PetGameObjectCodec.Drop:
                state.Drop = new DropGame(state.Scene.Drop, session.Track, state.Scene.Spots, random);
                if (!SendTrainingOutput(session, [new PET_9_PROTOCOL.MSG_PETGAMESTART { Game = session.Game, Data = "" }])) return;
                ScheduleTick(session);
                break;
            case PetGameObjectCodec.Maze:
                state.Maze = new MazeGame(state.Scene.Spots, random);
                var output = new List<IMessage>();
                foreach (var pickup in state.Maze.Pickups) {
                    var template = pickup.Kind switch {
                        MazePickupKind.Clock => PetGameScenes.MazeClockTemplate,
                        MazePickupKind.SpeedBoost => PetGameScenes.MazeSpeedTemplate,
                        MazePickupKind.Star => PetGameScenes.MazeStarTemplate,
                        _ => state.Scene.TrackTemplate,
                    };
                    if (PetGameObjectCodec.TryPreparePiece(template, PetGameScenes.W(pickup.Position), out var piece)) {
                        state.Pieces[pickup.Key] = piece.GlobalId;
                        output.Add(new GAME_5_PROTOCOL.MSG_NEWOBJECT { Data = piece.Data });
                    }
                }
                output.Add(new PET_9_PROTOCOL.MSG_PETGAMESTART { Game = session.Game, Data = "" });
                output.Add(new PET_9_PROTOCOL.MSG_PETGAMEMAZE { GameCommand = MazeStart, GameData = 0, ObjectID = 0 });
                Logger.Information("Pet maze: {0} pickups ({1} snacks) placed.", Logger.Args(state.Pieces.Count, state.Maze.SnackCount));
                if (!SendTrainingOutput(session, output)) return;
                ScheduleTick(session);
                break;
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_TEMPLATEOBJECTLOCATION))]
    private void ReceiveTemplateObjectLocation(ZONE_102_PROTOCOL.MSG_TEMPLATEOBJECTLOCATION message) {
        if (_session is not { Phantom: { } state, Ended: false } session || !EnsureTrainingContext(session)) return;
        var at = PetGameScenes.V(message.Location);
        if (message.TemplateId == PetGameScenes.CannonTargetTemplate && session.Game == PetGameObjectCodec.Cannon && state.Cannon is null) {
            state.Target = at;
            Timers.Cancel(PhantomTarget);
            OpenCannon(session);
        }
        else if (message.TemplateId == PetGameScenes.MazeGhostTemplate && state.Maze is not null) {
            state.Ghosts[message.GlobalId] = at;
        }
    }

    [MessageHandler(typeof(PhantomTargetTimeout))]
    private void ReceiveTargetTimeout(PhantomTargetTimeout message) {
        if (!ReferenceEquals(_session, message.Session) || message.Session.Phantom is not { Cannon: null } state
            || !EnsureTrainingContext(message.Session)) return;
        // CLASSIC: no answer from the zone: aim at the range's first target spot (the client's target may stand elsewhere).
        Logger.Warning("Pet cannon: the zone did not report its target; using the range's first target spot.");
        state.Target = state.Scene.Spots[0];
        OpenCannon(message.Session);
    }

    private void OpenCannon(Session session) {
        var state = session.Phantom;
        var origin = state.Scene.Location("Home");
        state.Cannon = new CannonGame(Random.Shared, origin, state.Target ?? state.Scene.Spots[0]);
        var wind = state.Cannon.BeginShot();
        Logger.Information("Pet cannon: origin {0}, target {1} ({2:0} away); shot 1 wind {3} at {4} degrees.",
            Logger.Args(origin, state.Cannon.TargetCentre, Vector3.Distance(origin, state.Cannon.TargetCentre), wind.Speed, wind.DirectionDegrees));
        if (!SendTrainingOutput(session, [
                CannonData(CannonOrigin, Floats(origin.X, origin.Y, origin.Z)),
                new PET_9_PROTOCOL.MSG_PETGAMESTART { Game = session.Game, Data = "" },
                CannonData(CannonWindData, WindBody(wind))])) return;
        Timers.StartSingleTimer(PhantomIdle, new PhantomIdleMessage(session, 0), CannonIdle);
    }

    /// <summary>Cannon commands 13 (ready for the next shot) and 15 (fired).</summary>
    private void ReceiveCannonCommand(Session session, byte[] data) {
        var cannon = session.Phantom?.Cannon;
        if (cannon is null || !session.Started || session.Ended) return;
        switch (data[0]) {
            case CannonFire when data.Length >= 13: {
                var velocity = new Vector3(BitConverter.ToSingle(data, 1), BitConverter.ToSingle(data, 5), BitConverter.ToSingle(data, 9));
                if (cannon.Fire(velocity) is not { } shot) {
                    Logger.Information("Pet cannon: a shot was refused (velocity {0}).", Logger.Args(velocity));
                    return;
                }
                Logger.Information("Pet cannon shot {0}{1}: velocity {2} -> {3} ({4}), result {5}.",
                    Logger.Args(shot.Ordinal, shot.Practice ? " (practice)" : "", velocity,
                        shot.CrossedTarget ? $"{shot.Distance:0} from the centre" : "missed the target", shot.End, shot.Result));
                SendTrainingOutput(session, [CannonData(CannonResultData, Ints(shot.Ordinal, shot.Result))]);
                Timers.StartSingleTimer(PhantomIdle, new PhantomIdleMessage(session, cannon.Shots.Count), CannonIdle);
                break;
            }
            case CannonReady:
                if (cannon.IsOver) {
                    FinishPhantom(session);
                    return;
                }
                if (cannon.BeginShot() is { } wind) {
                    Logger.Information("Pet cannon shot {0}: wind {1} at {2} degrees.", Logger.Args(cannon.Shots.Count + 1, wind.Speed, wind.DirectionDegrees));
                    SendTrainingOutput(session, [CannonData(CannonWindData, WindBody(wind))]);
                }
                break;
        }
    }

    [MessageHandler(typeof(PhantomIdleMessage))]
    private void ReceivePhantomIdle(PhantomIdleMessage message) {
        if (!ReferenceEquals(_session, message.Session) || message.Session is not { Started: true, Ended: false, Phantom.Cannon: { } cannon }
            || cannon.Shots.Count != message.Shots || !EnsureTrainingContext(message.Session)) return;
        Logger.Information("Pet cannon: nobody fired for {0}; ending with {1} shot(s).", Logger.Args(CannonIdle, cannon.Shots.Count));
        FinishPhantom(message.Session);
    }

    private void ScheduleTick(Session session)
        => Timers.StartSingleTimer(PhantomTick, new PhantomTickMessage(session, session.Phantom.Tick), PhantomStep);

    [MessageHandler(typeof(PhantomTickMessage))]
    private void ReceivePhantomTick(PhantomTickMessage message) {
        var session = message.Session;
        if (!ReferenceEquals(_session, session) || session.Phantom is not { } state || state.Tick != message.Sequence
            || !session.Started || session.Ended || !EnsureTrainingContext(session)) return;
        state.Tick++;
        var dt = PhantomStep.TotalSeconds;
        state.Clock += dt;
        var output = new List<IMessage>();
        foreach (var linger in state.Linger.Where(l => l.Due <= state.Clock).ToList()) {
            state.Linger.Remove(linger);
            output.Add(new GAME_5_PROTOCOL.MSG_DELETEOBJECT { GameObjectID = linger.Gid, Data = "" });
        }

        var over = false;
        if (state.Drop is { } drop && !drop.IsOver) {
            foreach (var e in drop.Advance(dt, state.Pet)) {
                over |= AddDropEvent(state, e, output);
            }
        }
        else if (state.Maze is { } maze && !maze.IsOver) {
            if (state.Tick % GhostQueryTicks == 0) {
                SessionActor.GetZoneActor()?.Tell(new ZONE_102_PROTOCOL.MSG_QUERYTEMPLATEOBJECTS {
                    TemplateIds = [PetGameScenes.MazeGhostTemplate], Requester = Self }, Self);
            }
            if (maze.Advance(dt) is { Kind: MazeEventKind.TimesUp }) {
                over = true;
                Logger.Information("Pet maze: time is up with {0} snack(s).", Logger.Args(maze.Score));
            }
        }

        if (output.Count != 0 && !SendTrainingOutput(session, output)) return;
        if (over) {
            Timers.StartSingleTimer(PhantomFinish, new PhantomFinishMessage(session), TimeSpan.FromSeconds(PetMinigameRules.TimesUpGraceSeconds));
            return;
        }
        ScheduleTick(session);
    }

    private bool AddDropEvent(PhantomState state, DropEvent e, List<IMessage> output) {
        switch (e.Kind) {
            case DropEventKind.Spawned:
                if (PetGameObjectCodec.TryPreparePiece(e.Item.Food.TemplateId, PetGameScenes.W(e.Item.Position), out var piece)) {
                    state.Pieces[e.Item.Key] = piece.GlobalId;
                    output.Add(new GAME_5_PROTOCOL.MSG_NEWOBJECT { Data = piece.Data });
                    output.Add(new PET_9_PROTOCOL.MSG_PETGAMEDROPOBJECT { GameCommand = DropSpawned, GID = piece.GlobalId });
                }
                return false;
            case DropEventKind.Caught or DropEventKind.Missed:
                if (state.Pieces.Remove(e.Item.Key, out var gid)) {
                    output.Add(new PET_9_PROTOCOL.MSG_PETGAMEDROPOBJECT { GameCommand = e.Kind == DropEventKind.Caught ? DropCaught : DropMissed, GID = gid });
                    state.Linger.Add((state.Clock + PieceLingerSeconds, gid));
                }
                if (e.Kind == DropEventKind.Caught) {
                    output.Add(new PET_9_PROTOCOL.MSG_PETGAMEDROPBONUS { GameCommand = DropScale, Bonus = (int) MathF.Round(e.Scale * 1000) });
                    if (e.SpeedBonusSeconds != 0) {
                        output.Add(new PET_9_PROTOCOL.MSG_PETGAMEDROPBONUS { GameCommand = DropSpeed, Bonus = e.SpeedBonusSeconds });
                    }
                }
                return false;
            case DropEventKind.TimesUp:
                output.Add(new PET_9_PROTOCOL.MSG_PETGAMEDROPBONUS { GameCommand = DropTimesUp, Bonus = 0 });
                foreach (var gid2 in state.Pieces.Values) output.Add(new GAME_5_PROTOCOL.MSG_DELETEOBJECT { GameObjectID = gid2, Data = "" });
                state.Pieces.Clear();
                Logger.Information("Pet drop: time is up, fullness {0} ({1} caught).", Logger.Args(e.Fullness, state.Drop.Caught));
                return true;
        }
        return false;
    }

    [MessageHandler(typeof(PhantomFinishMessage))]
    private void ReceivePhantomFinish(PhantomFinishMessage message) {
        if (!ReferenceEquals(_session, message.Session) || message.Session.Ended || !EnsureTrainingContext(message.Session)) return;
        FinishPhantom(message.Session);
    }

    private void FinishPhantom(Session session) {
        var state = session.Phantom;
        Timers.Cancel(PhantomTick);
        Timers.Cancel(PhantomIdle);
        var points = state.Cannon?.Points ?? state.Drop?.Points ?? state.Maze?.Points ?? 0;
        var score = state.Cannon?.Score ?? state.Drop?.Fullness ?? state.Maze?.Score ?? 0;
        Logger.Information("Pet game {0}: score {1} -> {2} point(s).", Logger.Args(session.Game, score, points));
        state.Over = true;
        Finish(points, points);
        if (ReferenceEquals(_session, session) && session.Ended) {
            Timers.StartSingleTimer(PhantomReturn, new PhantomReturnMessage(session), ReturnWait);
        }
    }

    [MessageHandler(typeof(PhantomReturnMessage))]
    private void ReceivePhantomReturn(PhantomReturnMessage message) {
        if (!ReferenceEquals(_session, message.Session)) return;
        Logger.Information("Pet game {0}: the game window was not closed; sending the wizard back.", Logger.Args(message.Session.Game));
        LeavePhantom(message.Session);
    }

    /// <summary>Retires the phantom game and sends the wizard back once.</summary>
    private void LeavePhantom(Session session) {
        var state = session.Phantom;
        if (state is null || state.Returned) return;
        state.Returned = true;
        var pieces = state.Pieces.Values.Concat(state.Linger.Select(l => l.Gid)).ToList();
        if (pieces.Count != 0) {
            SendTrainingOutput(session, [.. pieces.Select(gid => (IMessage) new GAME_5_PROTOCOL.MSG_DELETEOBJECT { GameObjectID = gid, Data = "" })]);
        }
        if (ReferenceEquals(_session, session)) RetireTraining();
        SendHome(state.Origin);
    }

    // ------------------------------------------------------------------ the pet's moves and jumps

    [MessageHandler(typeof(MOVEBEHAVIOR_15_PROTOCOL.MSG_MB_MOVE))]
    private void ReceivePetMove(MOVEBEHAVIOR_15_PROTOCOL.MSG_MB_MOVE message)
        => PetMoved(message.GlobalID, message.LocationX, message.LocationY, message.LocationZ);

    [MessageHandler(typeof(MOVEBEHAVIOR_15_PROTOCOL.MSG_MB_MOVE_T))]
    private void ReceivePetMoveTimed(MOVEBEHAVIOR_15_PROTOCOL.MSG_MB_MOVE_T message)
        => PetMoved(message.GlobalID, message.LocationX, message.LocationY, message.LocationZ);

    /// <summary>The wire's truncated quarter coordinates (low 16 bits, signed) back to world units.</summary>
    internal static Vector3 DecodeMove(ushort x, ushort y, ushort z) => new((short) x * 4f, (short) y * 4f, (short) z * 4f);

    private void PetMoved(ulong gid, ushort x, ushort y, ushort z) {
        if (_session is not { Phantom: { } state, Started: true, Ended: false } session || !EnsureTrainingContext(session)) return;
        var pet = SessionActor.SummonedPetGlobalId;
        if (gid == 0 || (pet != 0 && gid != pet)) {
            Logger.Debug("Pet game {0}: a move for {1} is not the summoned pet {2}.", Logger.Args(session.Game, gid, pet));
            return;
        }
        var at = DecodeMove(x, y, z);
        state.Pet = at;
        if (state.Maze is not { IsOver: false } maze) return;
        var output = new List<IMessage>();
        foreach (var e in maze.PetMoved(at, state.Ghosts.Select(g => (g.Key, g.Value)))) {
            ulong Piece() {
                if (e.Pickup is null || !state.Pieces.Remove(e.Pickup.Key, out var gid2)) return 0;
                state.Linger.Add((state.Clock + PieceLingerSeconds, gid2));
                return gid2;
            }
            var score = (byte) Math.Clamp(e.Score, 0, 255);
            output.Add(e.Kind switch {
                MazeEventKind.Snack => new PET_9_PROTOCOL.MSG_PETGAMEMAZE { GameCommand = MazeSnack, GameData = score, ObjectID = Piece() },
                MazeEventKind.Clock => new PET_9_PROTOCOL.MSG_PETGAMEMAZE { GameCommand = MazeClock, GameData = 0, ObjectID = Piece() },
                MazeEventKind.SpeedBoost => new PET_9_PROTOCOL.MSG_PETGAMEMAZE { GameCommand = MazeSpeed, GameData = 0, ObjectID = Piece() },
                MazeEventKind.Star => new PET_9_PROTOCOL.MSG_PETGAMEMAZE { GameCommand = MazeStar, GameData = 0, ObjectID = Piece() },
                MazeEventKind.Frozen => new PET_9_PROTOCOL.MSG_PETGAMEMAZE { GameCommand = MazeFrozen, GameData = 0, ObjectID = e.Ghost },
                MazeEventKind.GhostEaten => new PET_9_PROTOCOL.MSG_PETGAMEMAZE { GameCommand = MazeGhostEaten, GameData = score, ObjectID = e.Ghost },
                _ => null,
            });
        }
        output.RemoveAll(m => m is null);
        if (output.Count != 0) SendTrainingOutput(session, output);
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEJUMP))]
    private void ReceivePetJump(PET_9_PROTOCOL.MSG_PETGAMEJUMP message) {
        if (_session is { Phantom.Drop: { } drop, Started: true, Ended: false } session && EnsureTrainingContext(session)) drop.Jump();
    }

    // ------------------------------------------------------------------ wire helpers

    private static PET_9_PROTOCOL.MSG_PETGAMEDATA CannonData(byte command, byte[] body)
        => new() { Game = PetGameObjectCodec.Cannon, Data = new ByteString([command, .. body]) };

    private static byte[] WindBody(CannonWind wind)
        => [.. Ints(wind.Speed, wind.DirectionDegrees), .. Floats(wind.DistanceBound, wind.Acceleration.X, wind.Acceleration.Y, wind.Acceleration.Z)];

    private static byte[] Ints(params int[] values) => [.. values.SelectMany(BitConverter.GetBytes)];

    private static byte[] Floats(params float[] values) => [.. values.SelectMany(BitConverter.GetBytes)];

}
