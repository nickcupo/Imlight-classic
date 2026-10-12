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
 * ZONE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages a game world zone including loading, player transfers, entity tracking, 
 * and communication between zone supervisors.
 * 
 * USAGE EXAMPLE:
 * var zoneActor = Context.ActorOf(Zone.Props("MyWorld/Hub", 12345));
 * 
 * NOTE:
 * Uses Akka actor model for asynchronous communication and supervisor pattern.
 * Zone loading is handled asynchronously with a timeout mechanism.
 * Mobile ID allocation is thread-safe using locks.
 *
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Supervisors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Core;

/// <summary>
/// Represents a zone in the game world.
/// </summary>
public class Zone : ReceiveProtocolDispatcher, IWithTimers {

    private const ushort RESERVED_MOBILE_ID_MAX = ushort.MaxValue / 20; // 5% of the maximum mobile ID count is reserved for special objects.
    private static readonly Vector4 s_locationFailedGiveaway = new(float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue);

    /// <summary>
    /// The zone data as loaded from game client data.
    /// </summary>
    public WizZoneData ZoneData { get; private set; }

    /// <summary>
    /// The zone path, formatted as it would be in the access pass.
    /// </summary>
    public string ZonePath { get; init; }

    /// <summary>
    /// The pretty name of the zone, as displayed in the game client.
    /// If the zone data is not loaded, this will be the same as <see cref="ZonePath"/>.
    /// </summary>
    public string ZoneName {
        get {
            if (ZoneData is null) {
                return ZonePath;
            }

            return ZoneData.m_zoneDisplayName;
        }
    }

    public ITimerScheduler Timers { get; set; }

    private readonly uint _dynamicZoneId;
    private readonly Lock _mobileIdLock = new();
    private readonly List<IActorRef> _supervisors = [];
    private readonly IActorRef _sigilSupervisor;
    private readonly IActorRef _playerSupervisor;
    private readonly IActorRef _objectSupervisor;
    private readonly IActorRef _volumeSupervisor;
    private readonly IActorRef _triggerSupervisor;
    private readonly IActorRef _pathSupervisor;
    private readonly Stopwatch _zoneLoadTimer;
    private readonly Dictionary<IActorRef, IServerMessage> _pendingPlayerEvents = [];
    private readonly HashSet<ushort> _mobileIdMap = [];
    private readonly Dictionary<IActorRef, bool> _supervisorLoadResults = [];
    private readonly HashSet<GID> _criticalObjectIds = [];
    private bool _isLoading;
    // CLASSIC: the players in the zone, not a counter. A session's REMOVEPLAYER came twice after a zone transfer
    // (DoZoneTransfer, then the old session's OnPreDispose), which drove the count down twice and released the mobile
    // id a second time; and the REMOVEPLAYER of the player who emptied the zone never reached the player supervisor,
    // because the count was lowered before the broadcast that checks it.
    private readonly HashSet<IActorRef> _players = [];

    // CLASSIC: what a REMOVEPLAYER for each player needs, kept from its ADDPLAYER. The zone watches its players' session
    // actors: a session that stopped without its REMOVEPLAYER (its ZoneService had no game object to name, or a crash)
    // stayed in the zone, and the zone's triggers kept asking it for its wizard (live 2026-10-01).
    private readonly Dictionary<IActorRef, ZONE_102_PROTOCOL.MSG_ADDPLAYER> _playerAdds = [];
    private int _playerCount => _players.Count;
    private readonly List<ZONE_102_PROTOCOL.MSG_PLAYERMOVE> _pendingPlayerMoves = [];
    private readonly List<ZONE_102_PROTOCOL.MSG_CREATUREMOVE> _pendingCreatureMoves = [];

    /// <summary>
    /// Creates a new zone from the path of the zone, formatted as it would be in the access pass.
    /// </summary>
    /// <param name="zonePath">The path of the zone, formatted as it would be in the access pass.</param>
    /// <param name="dynamicZoneId">The dynamic zone ID of the zone.</param>
    public Zone(string zonePath, uint dynamicZoneId) : this(zonePath, dynamicZoneId, 0) { }

    /// <summary>
    /// CLASSIC: an instanced zone (a dungeon) knows whose instance container holds it.
    /// </summary>
    public Zone(string zonePath, uint dynamicZoneId, ulong instanceOwnerId, ulong housingDeedId = 0) {
        this.InstanceOwnerId = instanceOwnerId;
        this.HousingDeedId = housingDeedId; // CLASSIC: trusted physical house identity.
        this.ZonePath = zonePath;
        this._dynamicZoneId = dynamicZoneId;
        this._isLoading = true;
        this._zoneLoadTimer = new Stopwatch();

        _objectSupervisor = CreateSupervisor<ZoneObjectSupervisor>();
        _supervisors.Add(_objectSupervisor);
        _volumeSupervisor = CreateSupervisor<ZoneVolumeSupervisor>();
        _supervisors.Add(_volumeSupervisor);
        _triggerSupervisor = CreateSupervisor<ZoneTriggerSupervisor>();
        _supervisors.Add(_triggerSupervisor);
        _playerSupervisor = CreateSupervisor<ZonePlayerSupervisor>();
        _supervisors.Add(_playerSupervisor);
        _pathSupervisor = CreateSupervisor<ZonePathSupervisor>();
        _supervisors.Add(_pathSupervisor);
        _sigilSupervisor = CreateSupervisor<ZoneSigilSupervisor>();
        _supervisors.Add(_sigilSupervisor);

        Timers.StartPeriodicTimer("flush-moves", new ZONE_102_PROTOCOL.MSG_FLUSHMOVES(),
            TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

        _zoneLoadTimer.Restart();
        _isLoading = true;

        Logger.Debug("Zone {ZoneName} begins load.", Logger.Args(ZoneName));
    }

    // Props
    public static Props Props(string zonePath, uint dynamicZoneId)
        => Akka.Actor.Props.Create(() => new Zone(zonePath, dynamicZoneId))
            .WithMailbox("akka.actor.mailbox.zone-priority");

    // CLASSIC: an instanced zone, held by the instance container of instanceOwnerId.
    public static Props Props(string zonePath, uint dynamicZoneId, ulong instanceOwnerId, ulong housingDeedId = 0)
        => Akka.Actor.Props.Create(() => new Zone(zonePath, dynamicZoneId, instanceOwnerId, housingDeedId))
            .WithMailbox("akka.actor.mailbox.zone-priority");

    /// <summary>
    /// CLASSIC: the character whose instance container holds this zone; 0 for a public zone.
    /// </summary>
    public ulong InstanceOwnerId { get; }
    public ulong HousingDeedId { get; } // CLASSIC

    // CLASSIC: a party lost a fight here; once the zone is empty, the instance is dropped so the next entry is fresh.
    private bool _resetWhenEmpty;

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_INSTANCEPARTYLOST))]
    private void ReceiveInstancePartyLost(CLASSIC_FEATURES_PROTOCOL.MSG_INSTANCEPARTYLOST message) {
        if (InstanceOwnerId == 0) {
            return;
        }

        _resetWhenEmpty = true;
        Logger.Information("Zone {Zone} (instance of {Owner}): a party lost here; it resets once empty.",
            Logger.Args(ZonePath, InstanceOwnerId));
        DropIfEmptyAfterLoss();
    }

    // CLASSIC: an instanced zone tells its container whenever it becomes empty or occupied. The container keeps a
    // dungeon nobody is in for the empty lifetime, then resets all of its zones together (2009: "Leaving any other way
    // gives you 30 minutes to return before it resets"; the level resets once everyone has left; Classic.InstanceResets,
    // InstanceContainer). Was a per-zone timer for sigil runs only, which could drop a room behind a party still in the
    // next one, and kept a wizard's own dungeon copies until a restart.
    private bool? _reportedOccupied;

    private void UpdateEmptyRunTimer() {
        if (InstanceOwnerId == 0 || !Imlight.Classic.ClassicRuntime.IsActive) {
            return;
        }

        var occupied = _playerCount > 0 || _isLoading;
        if (_reportedOccupied == occupied) {
            return;
        }

        _reportedOccupied = occupied;
        Context.Parent.Tell(new Imlight.CoreLib.Game.World.InstanceContainer.ZoneOccupancy(ZonePath, occupied));
    }

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_EMPTYRUNEXPIRED))]
    private void ReceiveEmptyRunExpired(CLASSIC_FEATURES_PROTOCOL.MSG_EMPTYRUNEXPIRED message) {
        // CLASSIC: superseded by the container's dungeon timer (InstanceContainer.ZoneOccupancy).
    }

    private void DropIfEmptyAfterLoss() {
        UpdateEmptyRunTimer(); // CLASSIC
        if (!_resetWhenEmpty || _playerCount > 0 || _isLoading) {
            return;
        }

        _resetWhenEmpty = false;
        Context.Parent.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_DROPSELF { ZoneName = ZonePath });
    }

    protected override void PostStop() {
        Classic.ZoneDataDirectory.Remove(Self); // CLASSIC
        Classic.ZoneObjectStates.Remove(Self); // CLASSIC: the instance's state objects (Temple of Storms obelisks).
        base.PostStop();
    }

    protected override void PreRestart(Exception reason, object message) {
        Logger.Error("Zone {ZoneName} restarts for: {Exception}", Logger.Args(ZoneName, reason));
        base.PreRestart(reason, message);
    }

    /// <summary>
    /// Closes the zone and stops all child actors.
    /// </summary>
    protected void CloseZone() {
        var msg = new ZONE_102_PROTOCOL.MSG_ZONECLOSED() {
            DynamicZoneId = _dynamicZoneId
        };

        foreach (var supervisor in _supervisors) {
            supervisor.Tell(msg);
        }

        // Inform any pending player events that the zone is closing.
        foreach (var (player, _) in _pendingPlayerEvents) {
            var rsp = new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
                ZoneActorRef = Self,
                DynamicZoneId = _dynamicZoneId,
                ErrorCode = 1,
                MobileId = 0,
                ZoneDisplayName = ZoneName
            };

            player.Tell(rsp);
        }

        // Inform the supervisor (GameWorld) that the zone is closing.
        Context.Parent.Tell(msg);
        Context.Stop(Self);
    }

    // CLASSIC: the PERF log's mailbox probe ([Classic] PerfLogSeconds): a timer message stamped with when it is due.
    protected override void PreStart() {
        base.PreStart();
        if (Classic.PerfMonitor.Enabled) {
            SchedulePerfProbe();
        }
    }

    private void SchedulePerfProbe()
        => Timers.StartSingleTimer("perf-probe", new Classic.PerfMonitor.ZoneProbe(System.Diagnostics.Stopwatch.GetTimestamp()
            + (long) (Classic.PerfMonitor.ZoneProbeInterval.TotalSeconds * System.Diagnostics.Stopwatch.Frequency)), Classic.PerfMonitor.ZoneProbeInterval);

    [MessageHandler(typeof(Classic.PerfMonitor.ZoneProbe))]
    private void ReceivePerfProbe(Classic.PerfMonitor.ZoneProbe probe) {
        Classic.PerfMonitor.ZoneProbeHandled(probe, (Context as Akka.Actor.ActorCell)?.Mailbox.MessageQueue.Count ?? 0);
        SchedulePerfProbe();
    }

    #region Handlers

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONETRANSFER))]
    protected virtual void ReceiveZoneTransfer(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message) {
        if (_isLoading) {
            _pendingPlayerEvents[Sender] = message;

            return;
        }

        ProcessZoneTransfer(message, Sender);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ADDPLAYER))]
    protected virtual void ReceiveAddPlayer(ZONE_102_PROTOCOL.MSG_ADDPLAYER message) {
        if (_isLoading) {
            _pendingPlayerEvents[message.PlayerActor] = message;

            return;
        }

        _players.Add(message.PlayerActor); // CLASSIC: was _playerCount++.
        WatchPlayer(message.PlayerActor, message); // CLASSIC
        UpdateEmptyRunTimer(); // CLASSIC
        InformZoneSupervisors(message.PlayerActor, message);
        
        // Send response to confirm player was added
        var response = new ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP {
            WizardGameObject = message.PlayerObject, AttachGeneration = message.AttachGeneration, ZoneActorRef = Self
        };
        Sender.Tell(response);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_REMOVEPLAYER))]
    protected virtual void ReceiveRemovePlayer(ZONE_102_PROTOCOL.MSG_REMOVEPLAYER message) {
        if (_isLoading) {
            _pendingPlayerEvents.Remove(message.PlayerActor);
            Sender.Tell(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYERRSP());

            return;
        }

        // CLASSIC: a second REMOVEPLAYER for a player already gone is answered and otherwise ignored: its mobile id may
        // belong to someone else by now. The supervisors hear the removal while the player still counts, so the player
        // supervisor forgets the last player too.
        if (message.PlayerActor is null || !_players.Contains(message.PlayerActor)) {
            Logger.Debug("Zone {Zone} ignored a REMOVEPLAYER for {Player}, who is not in it.",
                Logger.Args(ZonePath, message.PlayerActor?.Path.Name));
            Sender.Tell(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYERRSP());

            return;
        }

        // CLASSIC: a move of this player still waiting for the flush would reach the zone's objects after this removal,
        // and an object that tracks players in range (InteractServiceMementoComponent) would take the player back with
        // an actor that is going away: no more quest markers or NPC options for that character in this zone.
        _pendingPlayerMoves.RemoveAll(move => Equals(move.PlayerActor, message.PlayerActor));
        InformZoneSupervisors(message.PlayerActor, message);
        _players.Remove(message.PlayerActor);
        _playerAdds.Remove(message.PlayerActor); // CLASSIC
        // CLASSIC: give back the id this player was added with. The message's id can be the next zone's already: a
        // session sets its object's mobile id for the new zone before the old zone hears the REMOVEPLAYER.
        ReleaseObjectIdentifier(_playerMobileIds.Remove(message.PlayerActor, out var addedWith) && addedWith != 0
            ? addedWith : message.MobileId);
        Sender.Tell(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYERRSP());
        DropIfEmptyAfterLoss(); // CLASSIC
    }

    // CLASSIC: see _playerAdds.
    private void WatchPlayer(IActorRef playerActor, ZONE_102_PROTOCOL.MSG_ADDPLAYER add) {
        var mobileId = add?.PlayerObject?.m_nMobileID ?? 0;
        ClaimTransferMobileId(mobileId); // CLASSIC: the id its zone transfer handed out is in use now.
        if (playerActor is null || playerActor.IsNobody()) {
            return;
        }

        // CLASSIC: a second ADDPLAYER of the same actor with another id gives the first one back.
        if (mobileId != 0) {
            if (_playerMobileIds.TryGetValue(playerActor, out var previous) && previous != 0 && previous != mobileId) {
                ReleaseObjectIdentifier(previous);
            }

            _playerMobileIds[playerActor] = mobileId;
        }

        _playerAdds[playerActor] = add;
        try {
            Context.Watch(playerActor);
        }
        catch (Exception ex) {
            Logger.Debug("Zone {Zone} could not watch {Player}: {Error}", Logger.Args(ZonePath, playerActor.Path.Name, ex.Message));
        }
    }

    /// <summary>
    /// CLASSIC: a player's session stopped while the player was still in this zone: remove the player as its own
    /// REMOVEPLAYER would have.
    /// </summary>
    [MessageHandler(typeof(Terminated))]
    protected void ReceivePlayerTerminated(Terminated message) {
        if (!_playerAdds.TryGetValue(message.ActorRef, out var add)) {
            return;
        }

        if (!_players.Contains(message.ActorRef)) {
            _playerAdds.Remove(message.ActorRef);

            return;
        }

        Logger.Information("Zone {Zone}: session {Player} stopped without leaving the zone; removing it.",
            Logger.Args(ZonePath, message.ActorRef.Path.Name));
        var remove = new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER {
            AttachGeneration = add.AttachGeneration,
            PlayerActor = message.ActorRef,
        };
        if (add.PlayerObject is { } playerObject) {
            remove.GlobalId = playerObject.m_globalID;
            remove.MobileId = playerObject.m_nMobileID;
        }

        ReceiveRemovePlayer(remove);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PLAYERMOVE))]
    protected virtual void ReceivePlayerMove(ZONE_102_PROTOCOL.MSG_PLAYERMOVE message) {
        if (_isLoading) {
            _pendingPlayerEvents[message.PlayerActor] = message;
            return;
        }

        if (!IsMobileIdInUse(message.PlayerObject.m_nMobileID)) {
            return;
        }

        // CLASSIC: the mobile id alone lets through a move of a player who has left, once another player has its id.
        if (message.PlayerActor is not null && !_players.Contains(message.PlayerActor)) {
            return;
        }

        _pendingPlayerMoves.Add(message);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_CREATUREMOVE))]
    protected virtual void ReceiveCreatureMove(ZONE_102_PROTOCOL.MSG_CREATUREMOVE message) {
        if (_playerCount <= 0) {
            return;
        }

        if (!IsMobileIdInUse(message.CreatureObject.m_nMobileID)) {
            return;
        }

        _pendingCreatureMoves.Add(message);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_FLUSHMOVES))]
    private void ReceiveFlushMoves(ZONE_102_PROTOCOL.MSG_FLUSHMOVES _) {
        if (_pendingPlayerMoves.Count > 0) {
            var moves = _pendingPlayerMoves.ToArray();
            _pendingPlayerMoves.Clear();
            DispatchBroadcast(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Messages = moves,
                Targets = ZoneBroadcastTarget.Objects
                        | ZoneBroadcastTarget.Volumes
                        | ZoneBroadcastTarget.Sigils
                        | ZoneBroadcastTarget.Paths,
            });
        }

        if (_pendingCreatureMoves.Count > 0) {
            var moves = _pendingCreatureMoves.ToArray();
            _pendingCreatureMoves.Clear();
            DispatchBroadcast(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Messages = moves,
                Targets = ZoneBroadcastTarget.Sigils,
            });
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS))]
    private void ReceiveZoneLoadResults(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) {
        Logger.Debug("Zone {ZoneName} client data gathered.", Logger.Args(ZoneName));

        if (message.Error) {
            Logger.Error("Zone {ZoneName} failed to load because {ErrorMessage}", 
                Logger.Args(ZoneName, message.ErrorMessage));

            CloseZone();

            return;
        }

        _zoneLoadTimer.Restart();
        ZoneData = message.ZoneData;
        Classic.ZoneDataDirectory.Set(Self, ZoneData, message.NodeData); // CLASSIC: sessions read it without an Ask.

        // Inform each supervisor of the loaded zone data. They are expected to give a reply
        // to inform the zone that they have loaded their data.
        _supervisorLoadResults.Clear();
        foreach (var supervisor in _supervisors) {
            _supervisorLoadResults[supervisor] = false;
            supervisor.Tell(message);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONESUPERVISORLOADRESULTS))]
    private void ReceiveSupervisorLoadComplete(ZONE_102_PROTOCOL.MSG_ZONESUPERVISORLOADRESULTS message) {
        _supervisorLoadResults[Sender] = true;

        Logger.Debug("Zone {ZoneName} supervisor {SupervisorName} loaded.", Logger.Args(ZoneName, message.SupervisorName));

        // If the supervisor load results are all true, then the zone is fully loaded.
        if (_supervisorLoadResults.All(x => x.Value)) {
            // Finally process any pending player events that couldn't occur because the zone was still loading.
            ProcessQueue();

            _zoneLoadTimer.Stop();
            Logger.Information("Zone {ZoneName} loaded in {Time}ms.", Logger.Args(ZoneName, _zoneLoadTimer.ElapsedMilliseconds));
            _isLoading = false;
            UpdateEmptyRunTimer(); // CLASSIC: a copy nobody ever entered still expires.

            var startMsg = new ZONE_102_PROTOCOL.MSG_ZONESTART();

            foreach (var supervisor in _supervisors) {
                supervisor.Tell(startMsg);
            }
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONELOADTIMER))]
    private void ReceiveZoneTimerEnd() {
        if (_isLoading) {
            Logger.Error("Zone {ZoneName} failed to load within the timeout.", Logger.Args(ZoneName));
            CloseZone();
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_REGISTERCRITICALOBJECT))]
    private void ReceiverRegisterCriticalObject(ZONE_102_PROTOCOL.MSG_REGISTERCRITICALOBJECT message) 
        => _criticalObjectIds.Add(message.ObjectID);

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_GETRESERVEDMOBILEID))]
    private void ReceiveGetMobileId() {
        var rsp = new ZONE_102_PROTOCOL.MSG_GETRESERVEDMOBILEIDRSP {
            MobileID = GenerateReservedObjectIdentifier()
        };
        Sender.Tell(rsp);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_QUERYZONEDATA))]
    private void ReceiveQueryZoneData() {
        Sender.Tell(new ZONE_102_PROTOCOL.MSG_QUERYZONEDATARSP {
            ZoneData = ZoneData,
            InstanceOwnerId = InstanceOwnerId, // CLASSIC
            HousingDeedId = HousingDeedId,
        });
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_POSTEVENT))]
    private void ReceiveTriggerPost(ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        Logger.Verbose("Zone {ZoneName} received post event {EventName}.", Logger.Args(ZoneName, message.EventName));

        foreach (var supervisor in _supervisors) {
            supervisor.Tell(message);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_QUERYZONEENTITY))]
    private void ReceiveQueryEntityObject(ZONE_102_PROTOCOL.MSG_QUERYZONEENTITY message) {
        foreach (var supervisor in _supervisors) {
            supervisor.Forward(message);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_QUERYNEARESTDUELTARGET))]
    private void ReceiveQueryNearestDuelTarget(ZONE_102_PROTOCOL.MSG_QUERYNEARESTDUELTARGET message) {
        foreach (var supervisor in _supervisors) {
            supervisor.Forward(message);
        }
    }

    // CLASSIC: a pet game's question about its scene objects goes to every object (path creatures included).
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_QUERYTEMPLATEOBJECTS))]
    private void ReceiveQueryTemplateObjects(ZONE_102_PROTOCOL.MSG_QUERYTEMPLATEOBJECTS message) {
        foreach (var supervisor in _supervisors) {
            supervisor.Forward(message);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ARMSCRIPTEDCOMBAT))]
    private void ReceiveArmScriptedCombat(ZONE_102_PROTOCOL.MSG_ARMSCRIPTEDCOMBAT message) { // CLASSIC: ScriptedAggro
        foreach (var supervisor in _supervisors) {
            supervisor.Forward(message);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST))]
    private void ReceiveZoneBroadcast(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST message) {
        DispatchBroadcast(message);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_REQUESTCOMBATSIGIL))]
    private void ReceiveRequestCombatSigil(ZONE_102_PROTOCOL.MSG_REQUESTCOMBATSIGIL message)
        => _supervisors.ForEach(supervisor => supervisor.Forward(message));

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_SPAWNENTITY))]
    private void ReceiveSpawnEntity(ZONE_102_PROTOCOL.MSG_SPAWNENTITY message)
        => _supervisors.ForEach(supervisor => supervisor.Forward(message));

    #endregion

    private void DispatchBroadcast(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST message) {
        if ((message.Targets & ZoneBroadcastTarget.Players) != 0) {
            if (_playerCount > 0) _playerSupervisor.Forward(message);
        }
        if ((message.Targets & ZoneBroadcastTarget.Objects)  != 0) _objectSupervisor.Forward(message);
        if ((message.Targets & ZoneBroadcastTarget.Volumes)  != 0) _volumeSupervisor.Forward(message);
        if ((message.Targets & ZoneBroadcastTarget.Triggers) != 0) _triggerSupervisor.Forward(message);
        if ((message.Targets & ZoneBroadcastTarget.Paths)    != 0) _pathSupervisor.Forward(message);
        if ((message.Targets & ZoneBroadcastTarget.Sigils)   != 0) _sigilSupervisor.Forward(message);
    }

    private IActorRef CreateSupervisor<T>() where T : ActorBase {
        var props = Akka.Actor.Props.Create(() => (T) Activator.CreateInstance(typeof(T), this));
        
        return Context.ActorOf(props, typeof(T).Name);
    }

    private void InformZoneSupervisors(IActorRef player, IServerMessage message) {
        DispatchBroadcast(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Sender = player,
            Messages = [message],
            Targets = ZoneBroadcastTarget.All,
        });
    }

    private Vector4 GetLocationFromString(ByteString location) {
        var actualLocation = Vector4.Zero;

        var parsedLoc = Util.GetVectorFromCompactString(location);
        if (parsedLoc.X != 0 || parsedLoc.Y != 0 || parsedLoc.Z != 0) {
            actualLocation = parsedLoc;
        }
        else {
            var searchedLoc = ZoneData.m_locationList.FirstOrDefault(x => x.m_locName == location);
            if (searchedLoc is null) {
                var start = ZoneData.m_locationList.FirstOrDefault(x => x.m_locName == "Start");
                if (start is not null) {
                    actualLocation = (Vector4) start.m_location;
                    actualLocation.W = start.m_direction;

                    return actualLocation;
                }

                return s_locationFailedGiveaway;
            }

            actualLocation = (Vector4) searchedLoc.m_location;
            actualLocation.W = searchedLoc.m_direction;
        }

        return actualLocation;
    }

    private void ProcessZoneTransfer(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message, IActorRef sender) {
        var hardLimit = ZoneData?.m_nHardLimit ?? 0;

        // CLASSIC: up to four wizards in an instance; a friend teleporting into a full one is refused (2009:
        // "Your friend is in a full instance"; Classic.GroupInstances).
        // CLASSIC (2026-10-04): ambient wizards do not count; one leaves when a real player's friend comes in.
        var realPlayers = Classic.Ambient.AmbientWizards.Count == 0 ? _playerCount
            : _players.Count(p => !Classic.Ambient.AmbientWizards.IsAmbient(p));
        if (message.RefuseWhenFull && InstanceOwnerId != 0 && Classic.GroupInstances.IsFull(realPlayers, hardLimit)) {
            Logger.Information("Zone {Zone} (instance of {Owner}) is full ({Players}); a teleport-in was refused.",
                Logger.Args(ZonePath, InstanceOwnerId, realPlayers));
            sender.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
                ErrorCode = Imcodec.Cryptography.StringHash.Compute(Classic.GroupInstances.FullInstanceError),
                ErrorMessage = Classic.GroupInstances.FullInstanceMessage,
                InstanceOwnerId = InstanceOwnerId,
                HousingDeedId = HousingDeedId, // CLASSIC
                ZoneHardLimit = hardLimit,
            });

            return;
        }

        var rsp = new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
            ZoneActorRef = Self,
            DynamicZoneId = _dynamicZoneId,
            ErrorCode = 0,
            MobileId = GenerateTransferMobileId(), // CLASSIC: given back if no ADDPLAYER claims it.
            ZoneDisplayName = ZoneName,
            CriticalObjects = [.. _criticalObjectIds],
            InstanceOwnerId = InstanceOwnerId, // CLASSIC
            HousingDeedId = HousingDeedId,
            ZoneHardLimit = hardLimit, // CLASSIC
        };

        var actualLocation = GetLocationFromString(message.DestinationLocation);

        rsp.Location = (Vector3) actualLocation;
        rsp.Orientation = actualLocation.W;
        sender.Tell(rsp);
    }

    private void ProcessQueue() {
        foreach (var (playerActor, pendingEvent) in _pendingPlayerEvents) {
            if (pendingEvent is ZONE_102_PROTOCOL.MSG_ZONETRANSFER transfer) {
                ProcessZoneTransfer(transfer, playerActor);
            }
            else if (pendingEvent is ZONE_102_PROTOCOL.MSG_ADDPLAYER addPlayer) {
                _players.Add(playerActor); // CLASSIC: was _playerCount++.
                WatchPlayer(playerActor, addPlayer); // CLASSIC
                UpdateEmptyRunTimer(); // CLASSIC
                InformZoneSupervisors(playerActor, addPlayer);
                
                // Send response to confirm player was added
                var response = new ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP {
                    WizardGameObject = addPlayer.PlayerObject, AttachGeneration = addPlayer.AttachGeneration, ZoneActorRef = Self
                };
                playerActor.Tell(response);
            }
            else {
                InformZoneSupervisors(playerActor, pendingEvent);
            }
        }
        
        _pendingPlayerEvents.Clear();
    }

    private ushort GenerateObjectIdentifier() {
        lock (_mobileIdLock) {
            // Find first available ID.
            for (ushort i = RESERVED_MOBILE_ID_MAX + 1; i <= ushort.MaxValue; i++) {
                if (!_mobileIdMap.Contains(i)) {
                    _mobileIdMap.Add(i);
                    return i;
                }
            }

            throw new InvalidOperationException("Failed to generate a mobile ID.");
        }
    }

    internal ushort ReserveMobileId() => GenerateReservedObjectIdentifier();

    // CLASSIC: a zone transfer hands out a player-range mobile id, and only the player's MSG_ADDPLAYER (then its
    // REMOVEPLAYER) accounted for it. A transfer nobody followed with an ADDPLAYER kept its id for the zone's life: a
    // session that closed mid-transfer, an ambient companion whose transfer timed out on its side (AmbientCompanionGroup
    // gives up after 30 s and ignores a late answer), a companion sent home between the answer and the add. Such an
    // id is now given back once unclaimed for TransferClaimWindow; an ADDPLAYER claims it.
    internal static TimeSpan TransferClaimWindow { get; set; } = TimeSpan.FromSeconds(90);
    private readonly Dictionary<ushort, long> _transferIdDue = [];
    private readonly Dictionary<IActorRef, ushort> _playerMobileIds = [];

    private ushort GenerateTransferMobileId() {
        lock (_mobileIdLock) {
            FreeUnclaimedTransferIds();
            var mobileId = GenerateObjectIdentifier();
            _transferIdDue[mobileId] = Stopwatch.GetTimestamp() + (long) (TransferClaimWindow.TotalSeconds * Stopwatch.Frequency);
            return mobileId;
        }
    }

    private void ClaimTransferMobileId(ushort mobileId) {
        if (mobileId == 0) {
            return;
        }

        lock (_mobileIdLock) {
            _transferIdDue.Remove(mobileId);
            _mobileIdMap.Add(mobileId); // a late ADDPLAYER after its id was given back takes it again
        }
    }

    // CLASSIC: caller holds _mobileIdLock.
    private void FreeUnclaimedTransferIds() {
        if (_transferIdDue.Count == 0) {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        List<ushort> due = null;
        foreach (var (mobileId, at) in _transferIdDue) {
            if (at <= now) {
                (due ??= []).Add(mobileId);
            }
        }

        if (due is null) {
            return;
        }

        foreach (var mobileId in due) {
            _transferIdDue.Remove(mobileId);
            _mobileIdMap.Remove(mobileId);
        }

        Logger.Debug("Zone {Zone}: {Count} mobile id(s) of zone transfers nobody added were given back.", Logger.Args(ZonePath, due.Count));
    }

    /// <summary>CLASSIC: player-range mobile ids held now (unclaimed transfers past their window are freed first).</summary>
    internal int PlayerMobileIdsInUse {
        get {
            lock (_mobileIdLock) {
                FreeUnclaimedTransferIds();
                return _mobileIdMap.Count(id => id > RESERVED_MOBILE_ID_MAX);
            }
        }
    }

    // CLASSIC: the reserved mobile ids (1..RESERVED_MOBILE_ID_MAX) of the zone's own objects were never given back.
    // Every mob that died (and every combat minion and summoned pet) kept its id after its actor stopped, so a zone
    // with steady combat used up all 3276 and stopped spawning ("all IDs in use"; Unicorn Way on live, 2026-10-09,
    // after ~12 h of ambient wizards fighting). An entity now reserves its id under its own actor and gives it back
    // when it stops; the id becomes free again after the same short cooldown a player's id has (ReleaseObjectIdentifier),
    // so a new object's MSG_NEWOBJECT never races the old one's MSG_DELETEOBJECT on the client. Entity actors call these
    // from their own threads (as ReserveMobileId always did), so all of it is under _mobileIdLock and needs no timer.
    private static readonly long s_reservedReleaseDelayTicks = 2 * Stopwatch.Frequency;
    private readonly Dictionary<IActorRef, ushort> _reservedByOwner = [];
    private readonly Dictionary<ushort, long> _reservedReleaseDue = [];
    private int _reservedInUse;
    private int _reservedWarnedPercent;
    private long _reservedHandedOut;

    internal static ushort ReservedMobileIdMax => RESERVED_MOBILE_ID_MAX;

    /// <summary>CLASSIC: reserves a mobile id for an entity actor; the same id again if that actor already has one.</summary>
    internal ushort ReserveMobileId(IActorRef owner) {
        lock (_mobileIdLock) {
            if (owner is not null && _reservedByOwner.TryGetValue(owner, out var existing)) {
                return existing;
            }

            var mobileId = GenerateReservedObjectIdentifier();
            if (owner is not null) {
                _reservedByOwner[owner] = mobileId;
            }

            return mobileId;
        }
    }

    /// <summary>CLASSIC: an entity actor stopped; its reserved mobile id is free again after the cooldown.</summary>
    internal void ReleaseReservedMobileId(IActorRef owner) {
        lock (_mobileIdLock) {
            if (owner is null || !_reservedByOwner.Remove(owner, out var mobileId)) {
                return;
            }

            _reservedReleaseDue[mobileId] = Stopwatch.GetTimestamp() + s_reservedReleaseDelayTicks;
        }
    }

    /// <summary>CLASSIC: reserved mobile ids held now, cooling down ones included (tests and the sanity log read it).</summary>
    internal int ReservedMobileIdsInUse {
        get {
            lock (_mobileIdLock) {
                FreeDueReservedIds();
                return _reservedInUse;
            }
        }
    }

    /// <summary>CLASSIC: entity actors holding a reserved mobile id now.</summary>
    internal int ReservedMobileIdOwners {
        get {
            lock (_mobileIdLock) {
                return _reservedByOwner.Count;
            }
        }
    }

    // CLASSIC: frees the reserved ids whose cooldown is over. Caller holds _mobileIdLock.
    private void FreeDueReservedIds(bool all = false) {
        if (_reservedReleaseDue.Count == 0) {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        List<ushort> due = null;
        foreach (var (mobileId, at) in _reservedReleaseDue) {
            if (all || at <= now) {
                (due ??= []).Add(mobileId);
            }
        }

        if (due is null) {
            return;
        }

        foreach (var mobileId in due) {
            _reservedReleaseDue.Remove(mobileId);
            if (_mobileIdMap.Remove(mobileId)) {
                _reservedInUse--;
            }
        }
    }

    // CLASSIC: a warning as the reserved pool fills (50, 75, 90 %), so a leak shows long before spawns stop.
    private void WarnReservedPoolFill() {
        // CLASSIC: a sanity line every 500 ids handed out: in use stays flat while handed out grows.
        if (++_reservedHandedOut % 500 == 0) {
            Logger.Information("Zone {Zone}: {HandedOut} reserved mobile ids handed out so far, {InUse} of {Max} in use ({Owners} objects hold one).",
                Logger.Args(ZonePath, _reservedHandedOut, _reservedInUse, RESERVED_MOBILE_ID_MAX, _reservedByOwner.Count));
        }

        var percent = _reservedInUse * 100 / RESERVED_MOBILE_ID_MAX;
        var step = percent >= 90 ? 90 : percent >= 75 ? 75 : percent >= 50 ? 50 : 0;
        if (step > _reservedWarnedPercent) {
            _reservedWarnedPercent = step;
            Logger.Warning("Zone {Zone}: {InUse} of {Max} reserved mobile ids in use ({Owners} objects hold one, {Cooling} cooling down).",
                Logger.Args(ZonePath, _reservedInUse, RESERVED_MOBILE_ID_MAX, _reservedByOwner.Count, _reservedReleaseDue.Count));
        }
        else if (step < _reservedWarnedPercent) {
            _reservedWarnedPercent = step;
        }
    }

    private bool IsMobileIdInUse(ushort mobileId) {
        lock (_mobileIdLock) {
            return _mobileIdMap.Contains(mobileId);
        }
    }

    private ushort GenerateReservedObjectIdentifier() {
        lock (_mobileIdLock) {
            FreeDueReservedIds(); // CLASSIC

            // Find first available ID in reserved range.
            for (ushort i = 1; i <= RESERVED_MOBILE_ID_MAX; i++) {
                if (!_mobileIdMap.Contains(i)) {
                    _mobileIdMap.Add(i);
                    _reservedInUse++; // CLASSIC
                    WarnReservedPoolFill(); // CLASSIC
                    return i;
                }
            }

            // CLASSIC: rather than fail, take the ids still cooling down (a burst of deaths and spawns).
            if (_reservedReleaseDue.Count > 0) {
                FreeDueReservedIds(all: true);
                for (ushort i = 1; i <= RESERVED_MOBILE_ID_MAX; i++) {
                    if (_mobileIdMap.Add(i)) {
                        _reservedInUse++;
                        return i;
                    }
                }
            }

            throw new InvalidOperationException($"Failed to generate a reserved mobile ID - all IDs in use ({_reservedByOwner.Count} objects hold one)."); // CLASSIC: with the holder count.
        }
    }

    private void ReleaseObjectIdentifier(ushort mobileId) {
        // Derfer the actual release on a short cooldown. This is a direct fix
        // for https://github.com/Revive101/Imlight/issues/131, I think.
        // Without this, a rapid leave+join cycle can cause the new player's
        // MSG_NEWOBJECT to race ahead of the old player's MSG_REMOVEOBJECT.
        var key = $"release-mobile-{mobileId}";
        Timers.StartSingleTimer(key, new ZONE_102_PROTOCOL.MSG_RELEASEMOBILEID {
            MobileId = mobileId
        }, TimeSpan.FromSeconds(2));
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_RELEASEMOBILEID))]
    private void ReceiveReleaseMobileId(ZONE_102_PROTOCOL.MSG_RELEASEMOBILEID message) {
        lock (_mobileIdLock) {
            // CLASSIC: keep the reserved count right (a test zone hands a player a reserved id).
            if (_mobileIdMap.Remove(message.MobileId) && message.MobileId is > 0 and <= RESERVED_MOBILE_ID_MAX) {
                _reservedInUse--;
            }
            _reservedReleaseDue.Remove(message.MobileId);
        }
    }

}
