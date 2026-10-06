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
 * ZONE SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages zone interactions, player transfers, and zone-specific 
 * mechanics within the game server session.
 * 
 * USAGE EXAMPLE:
 * Internal service handling complex zone transition, spawning, 
 * and player management processes.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty, Jeff
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Quests;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Sigils;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class ZoneService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const int ZONE_REMOVAL_WAIT_TIME_IN_SECONDS = 8;
    private const int ZONE_TRANSFER_CLEANUP_WAIT_TIME_IN_SECONDS = 1;
    private const int ZONE_HEAL_TICK_INTERVAL_IN_SECONDS = 5;
    private const float TELEPORT_EFFECTS_TIME = 2.0f;
    private const string ENTER_ZONE_EVENT_NAME = "EnterZone";

    public IActorRef ZoneActor;
    private long _attachGeneration;

    private readonly TimeSpan _zoneRemovalWaitTime = TimeSpan.FromSeconds(ZONE_REMOVAL_WAIT_TIME_IN_SECONDS);
    private readonly bool _randomBackflips
        = ConfigurationManager.Settings["April Fools.RandomBackFlips"].AsBool();
    private bool _isTransferQueued;
    private bool _removedForTransfer; // CLASSIC: DoZoneTransfer removed the player from ZoneActor
    private uint _currentDynamicZoneId;
    private ulong _currentInstanceOwner; // CLASSIC: the instance (owner or sigil run) this session's zone belongs to

    private const string SIGIL_ENTER_TIMER_KEY = "sigilenter";
    private const float SIGIL_COUNTDOWN_SECONDS = 10.0f;
    private const float SIGIL_YAW_ERROR_COMPENSATION = 1.58f; // Gamebryo yaw compensation
    private ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY _activeSigilEntry;
    private ulong _activeSigilCharId; // CLASSIC: who stood on it, to free the group slot without an Ask (dispose)

    private readonly CoreObjectSerializer _effectSerializer = new(
        behaviors: SerializerFlags.None
    );
    private readonly CoreObjectSerializer _zoneObjectSerializer = new(
        versionable: false,
        behaviors: SerializerFlags.None
    );

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new ZoneService(parentActor));

    protected override void OnPreDispose() {
        // CLASSIC (multiplayer audit B): a wizard who disconnects or changes zone during a sigil countdown frees its
        // slot in the group (a zone change also closes this session).
        ReleaseSigilSlot();
        _activeSigilEntry = null;

        SessionActor.PublishDoorAttach(null);
        var gameObj = GetActiveGameObject();
        if (gameObj is null) {
            base.OnPreDispose();
            return;
        }

        var globalId = gameObj.m_globalID;

        // CLASSIC: DoZoneTransfer already took the player out of this zone before the server transfer; a second
        // REMOVEPLAYER from the disposed session is not sent (Zone also ignores one for a player it no longer has).
        if (_removedForTransfer) {
            ZoneActor = null;
            base.OnPreDispose();

            return;
        }

        // If the zone reference is not null, we'll tell the zone to remove the player.
        SessionActor.PublishDoorAttach(null);
        ZoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER() {
            AttachGeneration = _attachGeneration,
            PlayerActor = SessionActor.ActorRef,
            GlobalId = globalId,
            MobileId = gameObj.m_nMobileID,
        });
        ZoneActor = null;

        // Remove the player from the online player collection.
        OnlinePlayerCollection.RemoveOnlinePlayer(SessionActor.SessionID);

        base.OnPreDispose();
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceivePostAttach(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        // Send immediate effects.
        var wizard = GetActiveWizard();
        var timeHomeLastClicked = DateTimeOffset.FromUnixTimeSeconds(wizard.TimeHomeLastClicked);
        var timeDifference = DateTimeOffset.UtcNow.Subtract(timeHomeLastClicked);

        if (timeDifference.TotalSeconds < 30) {
            SendCantGoHomeEffect(timeHomeLastClicked);
        }

        var postEventMsg = new ZONE_102_PROTOCOL.MSG_POSTEVENT {
            EventName = ENTER_ZONE_EVENT_NAME,
            PlayerActor = SessionActor.ActorRef,
            PlayerGameObject = GetActiveGameObject()
        };
        ZoneActor.Tell(postEventMsg);

        // CLASSIC: every account gets its starting Crowns once. The client shows the balance only after its interface
        // has loaded (Revive101 found about five seconds), so send it now and again shortly after.
        ClassicCrowns.EnsureStartingCrowns(wizard.Account);
        SendToSocket(ClassicCrowns.BalanceMessage(wizard.Account, wizard.CharId));
        Timers.StartSingleTimer("sync-crowns", ClassicCrowns.BalanceMessage(wizard.Account, wizard.CharId), TimeSpan.FromSeconds(5));

        return;
    }

    // CLASSIC: the delayed Crowns sync; the balance is read again when it fires.
    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_CROWNBALANCE))]
    private void ReceiveCrownBalanceSync(WIZARD_12_PROTOCOL.MSG_CROWNBALANCE message) {
        if (GetActiveWizard() is { } wizard) {
            SendToSocket(ClassicCrowns.BalanceMessage(wizard.Account, wizard.CharId));
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONETRANSFER))]
    private void ReceiveZoneTransferRequest(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message) {
        if (_isTransferQueued) {
            return;
        }

        // CLASSIC: world gate. Every transfer path reaches this point before the zone is allocated.
        var classicDecision = ClassicGate.Decide(message.DestinationZone);
        if (!classicDecision.Allowed) {
            Sender.Tell(ClassicGate.RefuseTransfer(classicDecision, GetActiveWizard()?.CharId,
                message.SendToClient ? InformGameClient : null));

            return;
        }

        // CLASSIC (multiplayer audit B): any other transfer during a sigil countdown (a teleport to a friend, a door)
        // steps the wizard off the sigil. The sigil's own transfer clears the entry before it gets here.
        if (_activeSigilEntry is not null) {
            CancelSigilCountdown();
        }

        // CLASSIC: a door or trigger inside an instance leads to the same instance's zones (Classic.GroupInstances).
        if (ClassicRuntime.IsActive) {
            message.OwnerCharId = GroupInstances.OwnerForTransfer(message.OwnerCharId, message.KeepInstance,
                _currentInstanceOwner);

            // CLASSIC: a door between two zones of one dungeon keeps the wizard in the same private copy, even when the
            // next room's hard limit is a public zone's (Briskbreeze Tower's floors 2-10 have 100).
            var fromZone = GetActiveWizard()?.Zone;
            if (message.KeepInstance && _currentInstanceOwner != 0
                    && Imlight.Classic.Travel.InstanceGroups.SameGroup(fromZone, message.DestinationZone)) {
                message.IsPrivate = true;
            }

            // CLASSIC: the 2009 reset rule (Classic.InstanceResets): entering a dungeon from outside into the wizard's
            // own copy starts it fresh, every zone of it, unless someone is inside or the wizard left it "another way"
            // within the return window. A gauntlet (the Golem Tower) always starts fresh.
            if (message.SendToClient && GetActiveWizard() is { } entering
                    && InstanceResets.ResetOnEntry(entering.CharId, fromZone, message.DestinationZone, message.OwnerCharId,
                        DateTime.UtcNow)) {
                message.ResetInstance = true;
            }
        }

        // Sending the server transfer request to the server will allocate and load the zone.
        var zoneDetails = AskServer<ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP>(message);
        if (message.SendToClient && zoneDetails.ErrorCode == 0) {
            // Check if the destination zone is the same as the current zone. If so, just teleport the player.
            // CLASSIC: only when it is the same copy of the zone; another instance of it is a real zone change.
            if (message.DestinationZone == GetActiveWizard().Zone
                    && (!ClassicRuntime.IsActive || ZoneActor is null || Equals(zoneDetails.ZoneActorRef, ZoneActor))) {
                // CLASSIC: the zone resolves named locations such as a hub's "Start"; was message.DestinationLocation.
                DoTeleport(new Imcodec.Math.Vector4(zoneDetails.Location, zoneDetails.Orientation));

                // CLASSIC: save where the server put the wizard (a teleport stone's far end); a relog before the next
                // move used to return the wizard to the spot before the jump.
                GetActiveWizard().SetPersistentLocation(zoneDetails.Location);

                return;
            }

            // CLASSIC: the attach after the client's zone change joins the instance that answered (a sigil group's
            // run, a friend's dungeon), not the wizard's own.
            if (ClassicRuntime.IsActive && GetActiveWizard() is { } traveller) {
                GroupInstances.QueueEntry(traveller.CharId, message.DestinationZone, zoneDetails.InstanceOwnerId,
                    DateTime.UtcNow);

                // CLASSIC: how the wizard left a dungeon (its exit, or another way), for the 2009 reset rule.
                InstanceResets.NoteTransfer(traveller.CharId, traveller.Zone, _currentInstanceOwner, message.DestinationZone,
                    zoneDetails.InstanceOwnerId, message.KeepInstance, DateTime.UtcNow);
            }

            ReadyClientForZoneTransfer(message);
        }
        else if (zoneDetails.ErrorCode == StringHash.Compute(GroupInstances.FullInstanceError)) {
            // CLASSIC: 2009's refusal of a teleport into a full instance.
            InformGameClient(GroupInstances.FullInstanceMessage, true);
        }
        else if (zoneDetails.ErrorCode != 0) {
            // The server has returned an error code. This means the zone transfer failed.
            InformGameClient("Failed to transfer to zone: " + zoneDetails.ErrorMessage, true);
        }
        else {
            // If we're not sending this message to the client, it means the zone is being loaded
            // for MSG_ATTACH. In which case, the client is already prepared for the zone transfer.
            SetZone(zoneDetails.ZoneActorRef);
            SessionActor.PublishDoorAttach(null);
            _currentDynamicZoneId = zoneDetails.DynamicZoneId;
            _currentInstanceOwner = zoneDetails.InstanceOwnerId; // CLASSIC
        }

        Sender.Tell(zoneDetails);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_ZONETRANSFERACK))]
    private void ReceiveZoneTransferAck(GAME_5_PROTOCOL.MSG_ZONETRANSFERACK message) {
        // CLASSIC: only a transfer the server queued (security audit 2026-10-04); an unasked ACK sent the wizard to
        // whatever zone was queued last.
        if (!_isTransferQueued) {
            Logger.Warning("Zone transfer ACK with no transfer queued; ignored.");

            return;
        }

        // The client has accepted the zone transfer. We can now send the server transfer message.
        DoZoneTransfer();
    }

    /// <summary>CLASSIC: a voluntary teleport (Go Home, Go to Dorm, a world door, a zone hop) is refused in a duel; the
    /// 2009 client hides those buttons while the duel UI is up (security audit 2026-10-04). Flee keeps its own rules.</summary>
    private bool RefusedInDuel() {
        var refusal = Imlight.Classic.Security.VoluntaryTeleport.Check(GetActiveWizard()?.IsInDuel == true);
        if (refusal == Imlight.Classic.Security.TeleportRefusal.None) {
            return false;
        }

        InformGameClient(Imlight.Classic.Security.VoluntaryTeleport.Message(refusal));

        return true;
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_ZONETRANSFERNACK))]
    private void ReceiveZoneTransferNack(GAME_5_PROTOCOL.MSG_ZONETRANSFERNACK message) {
        // The client has denied the zone transfer.
        Logger.Debug("Client was not OK with zone transfer!");
        _isTransferQueued = false;
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY))]
    private void ReceiveStartSigilEntry(ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY message) {
        if (_isTransferQueued || _activeSigilEntry is not null) {
            return;
        }

        if (string.IsNullOrEmpty(message.DestinationZone)) {
            return;
        }

        // CLASSIC: refuse a closed instance before the dismount, snap and countdown.
        if (!ClassicGate.AllowsZone(message.DestinationZone, GetActiveWizard()?.CharId, InformGameClient)) {
            return;
        }

        // Force a dismount.
        SessionActor.ActorRef.Tell(
            new ZONE_102_PROTOCOL.MSG_ENFORCEINTERIORMOUNT { Force = true }
        );

        _activeSigilEntry = message;
        _activeSigilCharId = GetActiveWizard()?.CharId ?? 0;

        SnapPlayerToSigilFace(message);

        // CLASSIC: a wizard joining a sigil group gets the time left on the group's countdown.
        var countdown = message.CountdownSeconds > 0 ? message.CountdownSeconds : SIGIL_COUNTDOWN_SECONDS;
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_MINIGAMETIMERSTART {
            Time = (float) countdown,
            SigilGID = message.SigilGID,
            Teleport = 0,
        });

        Timers.StartSingleTimer(
            SIGIL_ENTER_TIMER_KEY,
            new ZONE_102_PROTOCOL.MSG_SIGILENTER(),
            TimeSpan.FromSeconds(countdown));

        Logger.Information("Dungeon sigil countdown started -> '{0}'",
            Logger.Args(message.DestinationZone));
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_SIGILENTER))]
    private void ReceiveSigilEnter(ZONE_102_PROTOCOL.MSG_SIGILENTER message) {
        if (_activeSigilEntry is null) {
            return;
        }

        var wizard = GetActiveWizard();
        var gameObj = GetActiveGameObject();
        if (wizard is null || gameObj is null) {
            return;
        }

        // If the player walked off the pad during the countdown and the client's walk-off cancel never
        // arrived, drop the transfer instead of yanking them from across the street.
        // CLASSIC: only the wizards still standing on the pad at zero go (Help_Instances03).
        if (!IsOnSigilPad(_activeSigilEntry, wizard.Location)) {
            CancelSigilCountdown();

            return;
        }

        var entry = _activeSigilEntry;
        _activeSigilEntry = null;
        _activeSigilCharId = 0;

        // CLASSIC: a sigil run is a new instance shared by the wizards who used the sigil together.
        var owner = entry.RunId != 0 ? entry.RunId : wizard.CharId;
        Logger.Information("Entering dungeon instance '{0}' (owner {1})",
            Logger.Args(entry.DestinationZone, owner));

        var tpMsg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = entry.DestinationZone,
            DestinationLocation = entry.DestinationLoc,
            SendToClient = true,
            IsPrivate = true,
            OwnerCharId = owner,
            ResetInstance = entry.RunId == 0, // CLASSIC: a run's instance is new already
        };

        SendTeleportEffects();
        ReceiveZoneTransferRequest(tpMsg);
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_LEAVESIGILTIMERWAITING))]
    private void ReceiveLeaveSigilTimerWaiting(WIZARD_12_PROTOCOL.MSG_LEAVESIGILTIMERWAITING message) {
        if (_activeSigilEntry is null) {
            return;
        }

        Logger.Information("Client left the dungeon sigil pad — cancelling countdown.");
        CancelSigilCountdown();
    }

    // CLASSIC (multiplayer audit B): a wizard who walks off the pad during the countdown steps off the sigil at once,
    // freeing its slot, even when the client sends no MSG_LEAVESIGILTIMERWAITING. A little slack over the pad radius
    // keeps a move sent just before the snap onto the face from cancelling it.
    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENTMOVE))]
    private void ReceiveClientMoveDuringSigil(GAME_5_PROTOCOL.MSG_CLIENTMOVE message) {
        if (_activeSigilEntry is null) {
            return;
        }

        var position = new Imcodec.Math.Vector3(
            unchecked((short) message.LocationX * 4),
            unchecked((short) message.LocationY * 4),
            unchecked((short) message.LocationZ * 4));
        if (IsOnSigilPad(_activeSigilEntry, position, slack: SigilWalkOffSlack)) {
            return;
        }

        Logger.Information("Wizard walked off the dungeon sigil pad — cancelling countdown.");
        CancelSigilCountdown();
    }

    /// <summary>CLASSIC: how far past the pad radius a move may be before it counts as walking off.</summary>
    internal const float SigilWalkOffSlack = 1.15f;

    internal static bool IsOnSigilPad(ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY entry, Imcodec.Math.Vector3 position, float slack = 1f) {
        var pad = Util.GetVectorFromCompactString(entry.SigilLoc);

        return GroupInstances.IsOnPad(pad.X - position.X, pad.Y - position.Y, pad.Z - position.Z, entry.Radius * slack);
    }

    /// <summary>CLASSIC: frees this wizard's slot in its sigil group, if it is in one.</summary>
    private void ReleaseSigilSlot() {
        var entry = _activeSigilEntry;
        if (entry is null || entry.RunId == 0) {
            return;
        }

        var charId = _activeSigilCharId != 0 ? _activeSigilCharId : GetActiveWizard()?.CharId ?? 0;
        if (GroupInstances.LeaveSigilRun(entry.RunId, charId)) {
            Logger.Information("Wizard {0} stepped off sigil run {1}; the slot is free.", Logger.Args(charId, entry.RunId));
        }
    }


    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PATCHINGBLOCKED))]
    private void ReceivePatchingBlocked(WIZARD_12_PROTOCOL.MSG_PATCHINGBLOCKED message) {
        _isTransferQueued = false;
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_RETRYTELEPORT))]
    private void ReceiveRetryTeleport(GAME_5_PROTOCOL.MSG_RETRYTELEPORT message) {
        if (!_isTransferQueued) { // CLASSIC: as the ACK
            return;
        }

        DoZoneTransfer();
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_ZONEHOP))]
    private void ReceiveZoneHop(WIZARD2_53_PROTOCOL.MSG_ZONEHOP message) {
        // This message is sent when the client has enabled classic mode and wants to reload their current zone.
        if (_isTransferQueued || RefusedInDuel()) { // CLASSIC
            return;
        }

        var character = GetActiveWizard();

        _isTransferQueued = true;
        var zoneTransferRequestMessage = new GAME_5_PROTOCOL.MSG_ZONETRANSFERREQUEST {
            ZoneName = character.Zone,
            SendAck = 0
        };
        SendToSocket(zoneTransferRequestMessage);

        character.QueuedZoneName = character.Zone;
        character.QueuedZoneLocation = Util.GetCompactStringFromVector(character.Location, character.Orientation);
    }

    // This button and the GotoDorm button are locked client-side until level 2.
    // jooty, again? cmon man
    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_GOHOME))]
    private void ReceiveGoHome(WIZARD_12_PROTOCOL.MSG_GOHOME message) {
        // this teleports the wizard to the world hub, NOT their home/dorm. for that you want MSG_GOTODORM. goofy ahh naming scheme
        if (RefusedInDuel()) { // CLASSIC
            return;
        }

        var wizard = GetActiveWizard();
        // CLASSIC: go home through the classic zone map (ClassicMode and Housing belong to Wizard City), to the hub of
        // the world the wizard is in (unlocking a world only adds it to the world list; owner 2026-10-01), and refuse a
        // closed hub before the effects play.
        var hub = ClassicGate.HubFor(wizard.Zone);
        if (hub is null || !ClassicGate.AllowsZone(hub.Value.Zone, wizard.CharId, InformGameClient)) {
            return;
        }

        SendTeleportEffects();

        var tpmsg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = hub.Value.Zone,
            DestinationLocation = hub.Value.Location,
            SendToClient = true,
            OwnerCharId = wizard.CharId,
        };

        wizard.SetTimeHomeLastClicked(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var delay = TimeSpan.FromSeconds(TELEPORT_EFFECTS_TIME);
        Timers.StartSingleTimer("zonetransfer", tpmsg, delay);
    }

    // This button and the GoHome button are locked client-side until level 2.
    // jooty, again? cmon man
    private const string DormZone = "WizardCity/Interiors/WC_Housing_Dorm_Interior"; // CLASSIC

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_GOTODORM))]
    private void ReceiveGotoDorm(WIZARD_12_PROTOCOL.MSG_GOTODORM message) {
        // CLASSIC: every 2009 wizard had a Ravenwood dorm room (housing, December 2008). The dorm is the wizard's own
        // private copy of the dorm zone; its door trigger leads back to the Ravenwood dormitory.
        if (RefusedInDuel()) { // CLASSIC
            return;
        }

        if (ClassicRuntime.IsActive) {
            if (!ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.Housing)) {
                ClassicGate.RefuseFeature(ClassicFeatures.Housing, GetActiveWizard().CharId, InformGameClient);

                return;
            }

            var owner = GetActiveWizard();
            if (!ClassicGate.AllowsZone(DormZone, owner.CharId, InformGameClient)) {
                return;
            }

            SendTeleportEffects();
            owner.SetTimeHomeLastClicked(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Timers.StartSingleTimer("zonetransfer", new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                DestinationZone = DormZone,
                DestinationLocation = "Start",
                SendToClient = true,
                IsPrivate = true,
                OwnerCharId = owner.CharId,
            }, TimeSpan.FromSeconds(TELEPORT_EFFECTS_TIME));

            return;
        }

        var wizard = GetActiveWizard();
        SendTeleportEffects();

        var tpmsg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = "WizardCity/QA_SpawnRate", // just teleporting to gm for now
            DestinationLocation = "Start",
            SendToClient = true,
            OwnerCharId = wizard.CharId,
        };

        wizard.SetTimeHomeLastClicked(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var delay = TimeSpan.FromSeconds(TELEPORT_EFFECTS_TIME);
        Timers.StartSingleTimer("zonetransfer", tpmsg, delay);
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_WORLDTELEPORTREQUEST))]
    private void ReceiveWorldTeleportRequest(WIZARD_12_PROTOCOL.MSG_WORLDTELEPORTREQUEST message) {
        if (message.World.Length == 0) { // user clicked "exit", remove the wizbang
            var wizBangMsg = new GAME_5_PROTOCOL.MSG_WIZBANG() {
                GameObjectID = GetActiveWizard().GameObjectID,
                WizBangID = (uint) WizBangs.None
            };

            ZoneBroadcast(wizBangMsg, false);

            return;
        }

        if (RefusedInDuel()) { // CLASSIC
            return;
        }

        var zoneMap = WorldHubZones.GetHubForZone(message.World);
        if (zoneMap is null) {
            Logger.Error("{0} tried to teleport to an invalid world: {1}",
                Logger.Args(GetActiveWizard().CharId, message.World));

            return;
        }

        // CLASSIC: the client may ask for any key; refuse one the door would not list, a closed zone or a world the
        // wizard has not unlocked yet, and clear the wizbang.
        if (!ClassicGate.AllowsWorldTeleport(zoneMap.m_world, zoneMap.m_universeTPZone, GetActiveWizard().CharId, InformGameClient)
            || !ClassicGate.AllowsWorldUnlock(zoneMap.m_world, new WizardProgress(GetActiveWizard()), GetActiveWizard().CharId, InformGameClient)) {
            ZoneBroadcast(new GAME_5_PROTOCOL.MSG_WIZBANG {
                GameObjectID = GetActiveWizard().GameObjectID,
                WizBangID = (uint) WizBangs.None
            }, false);

            return;
        }

        var zoneName = zoneMap.m_universeTPZone;
        var zoneLocation = zoneMap.m_universeTPLocation;

        var msg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER() {
            DestinationZone = zoneName,
            DestinationLocation = zoneLocation,
            SendToClient = true
        };
        ReceiveZoneTransferRequest(msg);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ADDPLAYER))]
    private void ReceiveAddPlayer(ZONE_102_PROTOCOL.MSG_ADDPLAYER message) {
        // This is an internal message from MSG_ATTACH to add the player to the zone.
        if (ZoneActor is null) {
            throw new NullReferenceException(nameof(ZoneActor));
        }

        message.AttachGeneration = ++_attachGeneration;
        SessionActor.PublishDoorAttach(new(GetActiveWizard().Zone, ZoneActor, _attachGeneration, message.PlayerObject.m_globalID));
        ZoneActor.Forward(message);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP))]
    private void ReceiveAddPlayerRsp(ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP message) {
        // I've just been added to a zone. I need to spawn myself for all the other players.
        SpawnMyself(message.ZoneActorRef, message.AttachGeneration);

        // Dismount in no-mount zones, re-equip on leaving (EquipmentService owns the reconcile).
        SessionActor.ActorRef.Tell(new ZONE_102_PROTOCOL.MSG_ENFORCEINTERIORMOUNT());

        if (_randomBackflips) {
            var wizard = GetActiveWizard();
            Timers.StartPeriodicTimer("backflip", new ZONE_102_PROTOCOL.MSG_RANDOMFLIPS {
                ZoneName = wizard.Zone,
                SenderCharID = wizard.CharId
            },
            TimeSpan.FromSeconds(25));
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PLAYERADDEDTOZONE))]
    private void ReceiveNewPlayerAddedToZone(ZONE_102_PROTOCOL.MSG_PLAYERADDEDTOZONE message) {
        // A new player has been added to the zone. We need to spawn them.
        // Skip if this is myself.
        if (message.PlayerActor == SessionActor.ActorRef) {
            Logger.Error("{0} {1} received {2} for self.",
                Logger.Args(SessionActor.ActorRef, SessionActor.SessionID, message.GetType()));

            return;
        }

        // Spawn myself for the new player.
        SpawnMyselfFor(message.PlayerActor);
    }

    // Every 25 seconds, a random player in your zone will do a backflip
    // They will only be backflipping for you, nobody else, not even for themselves
    // If you look at the first letter of each variable in this function, it actually spells out the word "gaslit"
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_RANDOMFLIPS))]
    private void ReceiveRandomFlips(ZONE_102_PROTOCOL.MSG_RANDOMFLIPS message) {
        var rand = new Random();
        var players = OnlinePlayerCollection.GetPlayersInZone(message.ZoneName);
        players = players.Where(p => p.CharacterId != message.SenderCharID).ToArray();
        if (players.Length < 1) {
            return;
        }

        players = players.Where(p => p.CharacterId != message.SenderCharID).ToArray();

        var randomPlayerIndex = rand.Next(0, players.Length - 1);
        var randomPlayer = players[randomPlayerIndex];
        var castEffect = new CANTRIPSMESSAGES_57_PROTOCOL.MSG_CASTEFFECT {
            GameObjectID = Wizard.GetGameObjectId(randomPlayer.CharacterId),
            SpellTemplateID = 1521398842,
            AnimationName = "P_B_Cantrip_Emote_Backflip"
        };

        SendToSocket(castEffect);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PLAYERREMOVEDFROMZONE))]
    private void ReceivePlayerRemovedFromZone(ZONE_102_PROTOCOL.MSG_PLAYERREMOVEDFROMZONE message) {
        // A player has been removed from the zone. We need to remove them.
        // Skip if this is myself.
        if (message.PlayerActor == SessionActor.ActorRef) {
            Logger.Error("{0} {1} received {2} for self.",
                Logger.Args(SessionActor.ActorRef, SessionActor.SessionID, message.GetType()));

            return;
        }

        // Remove the player from the zone.
        var removeMsg = new GAME_5_PROTOCOL.MSG_REMOVEOBJECT {
            GameObjectID = message.GlobalId
        };

        SendToSocket(removeMsg);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST))]
    private void ReceiveZoneBroadcast(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST message) {
        if (ZoneActor is null) {
            throw new Exception("Zone Reference was null.");
        }

        ZoneActor.Tell(message);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PLAYERMOVE))]
    private void ReceiveZoneInteraction(ZONE_102_PROTOCOL.MSG_PLAYERMOVE message) {
        // This is an exception. Sometimes the MoveService interval happens as we zone transfer.
        if (ZoneActor is null) {
            return;
        }

        ZoneActor.Forward(message);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_QUERYZONEENTITY))]
    private void ReceiveQueryZoneObject(ZONE_102_PROTOCOL.MSG_QUERYZONEENTITY message) {
        if (ZoneActor is null) {
            throw new Exception("Zone Reference was null.");
        }

        ZoneActor.Forward(message);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_SENDTOHUB))]
    private void ReceiveBootToHub(ZONE_102_PROTOCOL.MSG_SENDTOHUB message) {
        var wizard = GetActiveWizard();
        var zoneName = wizard.Zone;

        // CLASSIC: the hub of the world the wizard is in, from the classic zone map, whether or not the wizard has
        // unlocked that world (owner 2026-10-01: a defeat sends you to that world's commons); without a profile this
        // is the stock lookup.
        var worldHubMap = ClassicGate.HubFor(zoneName);
        if (worldHubMap is null) {
            Logger.Error("Could not find world hub mapping for zone {0}",
                Logger.Args(zoneName));

            return;
        }

        var destinationZoneName = worldHubMap.Value.Zone;
        var destinationZoneLocation = worldHubMap.Value.Location;

        var msg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER() {
            DestinationZone = destinationZoneName,
            DestinationLocation = destinationZoneLocation,
            SendToClient = true
        };

        ReceiveZoneTransferRequest(msg);
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS))]
    private void ReceiveTeleportEffects(CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS message) {
        SendTeleportEffects();
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEHEALTICK))]
    private void ReceiveZoneHealTick(ZONE_102_PROTOCOL.MSG_ZONEHEALTICK message) {
        var wizard = GetActiveWizard();
        var currentWizardHealth = wizard.GameStats.m_currentHitpoints;
        var maxWizardHealth = wizard.GameStats.m_baseHitpoints;

        // If this wizard is max health, skip.
        if (currentWizardHealth >= maxWizardHealth) {
            return;
        }

        // Update our Wizard server side.
        var healPercent = message.MaxHealthPercent;
        float healAmount = healPercent / 100 * maxWizardHealth;
        var newHealth = Math.Min(currentWizardHealth + (int) healAmount, maxWizardHealth);

        wizard.UpdateHealth(newHealth);

        // Inform the client about the new health changes.
        // The client has a max health increase effect applied, so sending it here would double the health client side.
        var magicSchool = wizard.MagicSchoolBehavior.MagicSchool;
        var level = wizard.MagicSchoolBehavior.Level;
        var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(magicSchool, level);
        var normMaxHealth = baseStats.m_hitpoints;

        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH() {
            CharacterID = wizard.GameObject.m_globalID,
            NewHealth = newHealth,
            NewHealthMax = normMaxHealth,
            DisplayDiff = 1,
        };
        SendToSocket(networkMessage);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REALM_INFO_QUERY))]
    private void ReceiveRealmInfoQuery(GAME_5_PROTOCOL.MSG_REALM_INFO_QUERY message) {
        // Query the LoginServer's GameServerPool for the realm list.
        var realmListMsg = new SERVER_100_PROTOCOL.MSG_REALMLIST();
        var realmList = AskServer<SERVER_100_PROTOCOL.MSG_REALMLIST>(realmListMsg);

        var currentRealm = "Imlight";
        var currentZone = GetActiveWizard()?.Zone ?? "";

        // Query our own game server for the current realm name.
        try {
            var serverInfo = AskServer<SERVER_100_PROTOCOL.MSG_SERVERINFO>(
                new SERVER_100_PROTOCOL.MSG_QUERYSERVER());
            currentRealm = serverInfo.RealmName ?? currentRealm;
        }
        catch { }

        // Serialize the realm list as a RealmInfoList PropertyClass blob.
        // The client expects this exact type; we cannot fabricate the format.
        var realmInfoList = new RealmInfoList {
            m_infoList = []
        };
        for (int i = 0; i < realmList.RealmNames.Length; i++) {
            realmInfoList.m_infoList.Add(new RealmInfo {
                m_realmName = realmList.RealmNames[i],
                m_displayName = realmList.RealmNames[i],
                m_realmPopulation = realmList.PlayerCounts[i]
            });
        }

        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.None
        );
        if (!serializer.Serialize(realmInfoList, (PropertyFlags) 31, out var realmInfoBlob)) {
            Logger.Error("Failed to serialize RealmInfoList for MSG_REALM_INFO_QUERY.");

            return;
        }

        // Serialize an empty instance list; the client requires a valid
        // InstanceInfoList PropertyClass blob, not an empty string.
        var instanceInfoList = new InstanceInfoList {
            m_instanceList = new List<InstanceInfo>()
        };
        if (!serializer.Serialize(instanceInfoList, (PropertyFlags) 31, out var instanceInfoBlob)) {
            Logger.Error("Failed to serialize InstanceInfoList for MSG_REALM_INFO_QUERY.");

            return;
        }

        var rsp = new GAME_5_PROTOCOL.MSG_REALM_INFO_QUERY {
            RealmInfoList = realmInfoBlob,
            CurrentRealm = currentRealm,
            InstanceInfoList = instanceInfoBlob,
            CurrentZone = currentZone
        };
        SendToSocket(rsp);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_TRANSFER_REALMS))]
    private void ReceiveTransferRealms(GAME_5_PROTOCOL.MSG_TRANSFER_REALMS message) {
        var wizard = GetActiveWizard();
        var account = GetActiveAccount();

        if (wizard is null || account is null) {
            return;
        }

        // Ask the LoginServer to create a session key on the target realm's game server.
        var createKeyMsg = new SERVER_100_PROTOCOL.MSG_CREATEPLAYERKEY {
            Account = account,
            TargetRealmName = message.RealmName
        };

        SERVER_100_PROTOCOL.MSG_CREATEPLAYERKEYRSP keyRsp;
        try {
            keyRsp = AskServer<SERVER_100_PROTOCOL.MSG_CREATEPLAYERKEYRSP>(createKeyMsg);
        }
        catch {
            Logger.Error("Failed to create player key for realm transfer to {Realm}.",
                Logger.Args(message.RealmName));

            return;
        }

        if (!keyRsp.Success) {
            Logger.Warning("Realm transfer to {Realm} failed; realm not found.",
                Logger.Args(message.RealmName));

            return;
        }

        // Send MSG_SERVERTRANSFER to redirect the client to the new game server.
        // CLASSIC: a fresh single-use proof per transfer (SessionID, Key/FallbackKey; Imlight.Classic.Net.GameSessionKeys).
        var transferKey = Auth.SecuritySettings.GameKeys.Value.IssueTransfer(account.AccountId, SessionActor.RemoteIp);
        var serverTransfer = new GAME_5_PROTOCOL.MSG_SERVERTRANSFER {
            IP = keyRsp.IP,
            TCPPort = keyRsp.Port,
            UDPPort = keyRsp.Port,
            Key = transferKey.Key,
            FallbackKey = transferKey.Key,
            UserID = account.AccountId,
            CharID = wizard.CharId,
            ZoneName = wizard.Zone,
            ZoneID = new Imcodec.Types.GID((ulong) keyRsp.Port),
            Location = Util.GetCompactStringFromVector(wizard.Location, wizard.Orientation),
            Slot = 0,
            SessionSlot = 0,
            SessionID = transferKey.SessionId, // CLASSIC: the client echoes it in MSG_ATTACH (the proof)
            TargetPlayerID = wizard.CharId,
            TransitionID = 1,
            FallbackIP = keyRsp.IP,
            FallbackTCPPort = keyRsp.Port,
            FallbackUDPPort = keyRsp.Port,
            FallbackZone = wizard.Zone,
            FallbackZoneID = new Imcodec.Types.GID((ulong) keyRsp.Port)
        };
        SessionActor.MarkTransferringOut(); // CLASSIC: Game/AccountSessions.cs
        LogTransfer(serverTransfer); // CLASSIC
        SendToSocket(serverTransfer);
    }

    [MessageHandler(typeof(GAME2_55_PROTOCOL.MSG_CURRENTREALM))]
    private void ReceiveCurrentRealm(GAME2_55_PROTOCOL.MSG_CURRENTREALM message) {
        var wizard = GetActiveWizard();
        var currentZone = wizard?.Zone ?? "";

        var currentRealm = "Imlight";
        try {
            var serverInfo = AskServer<SERVER_100_PROTOCOL.MSG_SERVERINFO>(
                new SERVER_100_PROTOCOL.MSG_QUERYSERVER());
            currentRealm = serverInfo.RealmName ?? currentRealm;
        }
        catch { }

        var rsp = new GAME2_55_PROTOCOL.MSG_CURRENTREALM {
            CurrentRealm = currentRealm,
            CurrentZone = currentZone
        };
        SendToSocket(rsp);
    }

    /// <summary>CLASSIC: what a MSG_SERVERTRANSFER carries (Debug), without its proofs.</summary>
    internal static void LogTransfer(GAME_5_PROTOCOL.MSG_SERVERTRANSFER t) {
        Logger.Debug("MSG_SERVERTRANSFER to {Ip}:{Tcp}/{Udp} user {User} char {Char} zone {Zone} ({ZoneId}) at {Location}; " +
                     "slot {Slot} session slot {SessionSlot} target {Target} transition {Transition}; fallback {FallbackIp}:" +
                     "{FallbackTcp} {FallbackZone} ({FallbackZoneId}); key {Key}, fallback key {FallbackKey}, session id {Sid}",
            Logger.Args(t.IP, t.TCPPort, t.UDPPort, (ulong) t.UserID, (ulong) t.CharID, t.ZoneName, (ulong) t.ZoneID, t.Location,
                t.Slot, t.SessionSlot, (ulong) t.TargetPlayerID, t.TransitionID, t.FallbackIP, t.FallbackTCPPort, t.FallbackZone,
                (ulong) t.FallbackZoneID, t.Key == 0 ? "0" : "set", t.FallbackKey == 0 ? "0" : "set",
                (ulong) t.SessionID == 0 ? "0" : "set"));
    }

    private void SetZone(IActorRef actorRef) {
        ZoneActor = actorRef;
        _removedForTransfer = false; // CLASSIC
    }

    private void ReadyClientForZoneTransfer(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message) {
        var character = GetActiveWizard();
        _isTransferQueued = true;

        // Ask the client if it's okay with being transferred.
        var msg = new GAME_5_PROTOCOL.MSG_ZONETRANSFERREQUEST {
            ZoneName = message.DestinationZone,
            SendAck = 1
        };
        SendToSocket(msg);

        character.QueuedZoneName = message.DestinationZone;
        character.QueuedZoneLocation = message.DestinationLocation;
    }

    private void DoZoneTransfer() {
        // Remove the player from their current zone. We're awaiting a reply so the zone can properly clean up
        // before we continue. CLASSIC: the reply comes back as a message (ZoneRemovedForTransfer) instead of blocking
        // this actor and a pool thread on .Result for up to 8 s during every zone change.
        try {
            SessionActor.PublishDoorAttach(null);
            var removePlayerMsg = new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER() {
                AttachGeneration = _attachGeneration,
                PlayerActor = SessionActor.ActorRef,
                GlobalId = GetActiveGameObject().m_globalID,
                IsPlayerStillConnected = true,
                MobileId = GetActiveGameObject().m_nMobileID
            };
            ZoneActor.Ask<ZONE_102_PROTOCOL.MSG_REMOVEPLAYERRSP>(removePlayerMsg, _zoneRemovalWaitTime)
                .PipeTo(Self, success: _ => new ZoneRemovedForTransfer(true), failure: _ => new ZoneRemovedForTransfer(false));
        }
        catch {
            Self.Tell(new ZoneRemovedForTransfer(false));
        }
    }

    /// <summary>CLASSIC: the zone's answer to DoZoneTransfer's MSG_REMOVEPLAYER, or its timeout.</summary>
    internal sealed record ZoneRemovedForTransfer(bool Removed);

    [MessageHandler(typeof(ZoneRemovedForTransfer))]
    private void ReceiveZoneRemovedForTransfer(ZoneRemovedForTransfer message) {
        if (message.Removed) {
            _removedForTransfer = true; // CLASSIC

            // Remove the player from the online player collection.
            OnlinePlayerCollection.RemoveOnlinePlayer(SessionActor.SessionID);
        }
        else {
            Logger.Warning("Zone removal timeout of {0} seconds exceeded.", Logger.Args(ZONE_REMOVAL_WAIT_TIME_IN_SECONDS));
        }

        // Defer the server transfer by the cleanup wait time so the client can
        // finish tearing down zone objects.
        // CLASSIC: [Classic] ZoneTransferDelayMs (default 250; upstream ZONE_TRANSFER_CLEANUP_WAIT_TIME_IN_SECONDS = 1 s).
        var cleanupMs = ClassicRuntime.IsActive
            ? Classic.ClassicSettings.ZoneTransferDelayMs
            : ZONE_TRANSFER_CLEANUP_WAIT_TIME_IN_SECONDS * 1000;
        Timers.StartSingleTimer("zone-transfer-delay", new SERVICE_101_PROTOCOL.MSG_ZONETRANSFER_DELAY(),
                                TimeSpan.FromMilliseconds(Math.Max(0, cleanupMs)));
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ZONETRANSFER_DELAY))]
    private void OnZoneTransferDelay(SERVICE_101_PROTOCOL.MSG_ZONETRANSFER_DELAY _) {
        var account = GetSocketAccount();
        var character = GetActiveWizard();

        // Persist the current zone and location as explicit fallback data so the
        // Wizard record reflects where the player should return if the new attach fails.
        WizardCollection.UpdateCharacterZone(character, character.Zone, character.ZoneDisplayName);
        WizardCollection.UpdateCharacterLocation(character, character.Location, character.Orientation.Z);

        // CLASSIC: a fresh single-use proof per transfer: the client echoes SessionID in its MSG_ATTACH (its LoginKey
        // comes out empty), and Key/FallbackKey serve a client that sends it (Imlight.Classic.Net.GameSessionKeys).
        var transferKey = Auth.SecuritySettings.GameKeys.Value.IssueTransfer(account.AccountId, SessionActor.RemoteIp);
        var serverTransfer = new GAME_5_PROTOCOL.MSG_SERVERTRANSFER() {
            IP = character.GameServerIp,
            TCPPort = character.GameServerPort,
            UDPPort = character.GameServerPort,
            Key = transferKey.Key,
            FallbackKey = transferKey.Key,
            UserID = account.AccountId,
            CharID = character.CharId,
            ZoneName = character.QueuedZoneName,
            Location = character.QueuedZoneLocation,
            Slot = 0,
            SessionSlot = 0,
            SessionID = transferKey.SessionId, // CLASSIC: the client echoes it in MSG_ATTACH (the proof)
            TargetPlayerID = character.CharId,
            TransitionID = 1,
            FallbackIP = character.GameServerIp,
            FallbackTCPPort = character.GameServerPort,
            FallbackUDPPort = character.GameServerPort,
            FallbackZone = character.Zone,
            FallbackZoneID = _currentDynamicZoneId
        };
        SessionActor.MarkTransferringOut(); // CLASSIC: Game/AccountSessions.cs
        LogTransfer(serverTransfer); // CLASSIC
        SendToSocket(serverTransfer);

        // Register fallback data on the GameServer so the new session can
        // proactively recover if MSG_ATTACH never arrives.
        SessionActor.ServerRef.Tell(new SERVICE_101_PROTOCOL.MSG_REGISTER_FALLBACK {
            RemoteIp = SessionActor.RemoteIp,
            UserId = account.AccountId,
            CharId = character.CharId,
            FallbackZone = character.Zone,
            FallbackZoneId = _currentDynamicZoneId,
            FallbackLocation = Util.GetCompactStringFromVector(character.Location, character.Orientation),
            GameServerIp = character.GameServerIp,
            GameServerPort = character.GameServerPort
        });
    }

    private void DoTeleport(string location) => DoTeleport(Util.GetVectorFromCompactString(location)); // CLASSIC

    private void DoTeleport(Imcodec.Math.Vector4 coords) { // CLASSIC: was a location string.
        var compressedCoords = coords / 4;

        var directionYaw = coords.W % (2 * Math.PI);
        if (directionYaw < 0) {
            directionYaw += 2 * Math.PI;
        }

        var serverTele = new GAME_5_PROTOCOL.MSG_SERVERTELEPORT() {
            LocationX = (ushort) compressedCoords.X,
            LocationY = (ushort) compressedCoords.Y,
            LocationZ = (ushort) compressedCoords.Z,
            Direction = (byte) Math.Round(directionYaw / (2 * Math.PI) * 250),
            MobileID = GetActiveGameObject().m_nMobileID,
        };
        var broadcastMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = serverTele,
            Selfless = false,
        };
        ReceiveZoneBroadcast(broadcastMsg);
    }

    internal static bool UsesLegacyDoorOrdering(string zone)
        => ClassicQuestEngine.IsActive && LegacyDoorBindings.ForZone(zone).Count != 0;

    internal static ZONE_102_PROTOCOL.MSG_ZONEBROADCAST PlayerSpawnBroadcast(GAME_5_PROTOCOL.MSG_NEWOBJECT player, bool ordered, IActorRef owner)
        => new() { Message = player, Selfless = true, Sender = ordered ? owner : null };

    private void SpawnMyself(IActorRef actor, long generation) {
        var wizard = GetActiveWizard();
        var ordered = UsesLegacyDoorOrdering(wizard.Zone);
        var attach = SessionActor.DoorAttach;
        if (ordered && (attach is null || attach.Actor != actor || attach.Actor != ZoneActor
            || attach.Generation != generation || attach.Owner != wizard.GameObjectID
            || !string.Equals(attach.Zone, wizard.Zone, StringComparison.OrdinalIgnoreCase))) return;
        var properGameObj = WizardObjectLoader.GetPlayerGameObject(wizard);

        var flags = PropertyFlags.Prop_Public | PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!_zoneObjectSerializer.Serialize(properGameObj, flags, out var gameObjData)) {
            Logger.Error("Failed to serialize game object for {0}",
                Logger.Args(wizard.CharId));

            return;
        }

        var addMsg = new GAME_5_PROTOCOL.MSG_NEWOBJECT {
            Data = gameObjData,
        };
        var broadcastMsg = PlayerSpawnBroadcast(addMsg, ordered, SessionActor.ActorRef);
        if (ordered) {
            SessionActor.ActorRef.Tell(new LegacyDoorOwnerObject(attach, properGameObj.m_globalID, addMsg), Self);
            // Peers still receive the player; the owner must receive only the
            // marked object above, otherwise another spawn can reset its lamps.
        }

        ZoneActor.Tell(broadcastMsg);
    }

    private void SpawnMyselfFor(IActorRef actorRef) {
        var wizard = GetActiveWizard();
        var properGameObj = WizardObjectLoader.GetPlayerGameObject(wizard);

        var flags = PropertyFlags.Prop_Public | PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!_zoneObjectSerializer.Serialize(properGameObj, flags, out var gameObjData)) {
            Logger.Error("Failed to serialize game object for {0}",
                Logger.Args(wizard.CharId));

            return;
        }

        var addMsg = new GAME_5_PROTOCOL.MSG_NEWOBJECT {
            Data = gameObjData,
        };

        actorRef.Tell(addMsg);
    }

    private void SendTeleportEffects() {
        var wizard = GetActiveWizard();
        var now = DateTimeOffset.UtcNow;

        SendCantGoHomeEffect(now);

        // what does this do? who knows! its probably important.
        var enterState = new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = wizard.GameObject.m_globalID,
            State = StringHash.Compute("Teleport"),
        };
        var broadcastWrapper = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = enterState,
            Selfless = false,
        };
        ReceiveZoneBroadcast(broadcastWrapper);

        SendRecallHomeEffect(now);
    }

    private void SendCantGoHomeEffect(DateTimeOffset unixTimeStart) {
        var wizard = GetActiveWizard();
        NamedEffect effect = new NamedEffect {
            m_effectNameID = StringHash.Compute("CantGoHome"),
            m_endTime = (uint) unixTimeStart.AddSeconds(30).ToUnixTimeSeconds(),
            m_internalID = wizard.GameEffects.Count,
        };

        wizard.GameEffects.Add(effect);

        var flags = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!_zoneObjectSerializer.Serialize(effect, flags, out var serializedEffect)) {
            Logger.Error("Failed to serialize game object for {0}",
                Logger.Args(wizard.CharId));

            return;
        }

        var addEffect = new GAME_5_PROTOCOL.MSG_ADDEFFECT {
            GameObjectID = wizard.GameObject.m_globalID,
            EffectData = serializedEffect
        };

        SendToSocket(addEffect);
    }

    private void SendRecallHomeEffect(DateTimeOffset time) {
        var wizard = GetActiveWizard();

        // on live servers, the end time is 200 seconds from the time gohome is sent. i still have no clue why.
        // also on live servers, when teleporting in zone, it will send the effects like 3 times. i also have no clue on this either.
        var effect = new NamedEffect {
            m_effectNameID = StringHash.Compute("RecallHome"),
            m_endTime = (uint) time.AddSeconds(2).ToUnixTimeSeconds(),
            m_internalID = wizard.GameEffects.Count,
        };

        wizard.GameEffects.Add(effect);

        var flags = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!_effectSerializer.Serialize(effect, flags, out var serializedEffect)) {
            Logger.Error("Failed to serialize game object for {0}",
                Logger.Args(wizard.CharId));

            return;
        }

        var addEffect = new GAME_5_PROTOCOL.MSG_ADDEFFECT {
            GameObjectID = wizard.GameObject.m_globalID,
            EffectData = serializedEffect
        };
        var broadcastWrapper = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = addEffect,
            Selfless = false,
        };
        ReceiveZoneBroadcast(broadcastWrapper);
    }

    private void CancelSigilCountdown() {
        ReleaseSigilSlot(); // CLASSIC (multiplayer audit B)
        var entry = _activeSigilEntry;
        _activeSigilEntry = null;
        _activeSigilCharId = 0;
        Timers.Cancel(SIGIL_ENTER_TIMER_KEY);

        if (entry is null) {
            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_MINIGAMETIMEREND { SigilGID = entry.SigilGID });

        // Give the player their mount back.
        SessionActor.ActorRef.Tell(new ZONE_102_PROTOCOL.MSG_ENFORCEINTERIORMOUNT());

        Logger.Information("Dungeon sigil countdown cancelled (stepped off pad)");
    }

    private void SnapPlayerToSigilFace(ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY entry) {
        var gameObj = GetActiveGameObject();
        var wizard = GetActiveWizard();
        if (gameObj is null || wizard is null) {
            return;
        }

        var pad = Util.GetVectorFromCompactString(entry.SigilLoc);
        float faceX, faceY, faceZ = pad.Z, faceYaw;
        if (TryGetSigilFaceSlot(entry, out var slotPos, out var slotYaw)) {
            faceX = slotPos.X;
            faceY = slotPos.Y;
            faceZ = slotPos.Z;
            faceYaw = slotYaw;
        }
        else {
            // Fallback: offset off the pad centre toward where the player approached from, facing centre.
            var here = wizard.Location;
            double dx = here.X - pad.X, dy = here.Y - pad.Y;
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            const double SIGIL_FACE_OFFSET = 220.0;
            double ox, oy;
            if (len > 1.0) {
                ox = dx / len * SIGIL_FACE_OFFSET;
                oy = dy / len * SIGIL_FACE_OFFSET;
            }
            else {
                ox = -Math.Cos(pad.W) * SIGIL_FACE_OFFSET;
                oy = -Math.Sin(pad.W) * SIGIL_FACE_OFFSET;
            }

            faceX = (float) (pad.X + ox);
            faceY = (float) (pad.Y + oy);
            double yaw = Math.Atan2(-oy, -ox);
            yaw = (2 * Math.PI) - yaw - SIGIL_YAW_ERROR_COMPENSATION;
            if (yaw < 0) {
                yaw += 2 * Math.PI;
            }

            faceYaw = (float) yaw;
        }

        // Broadcast the slide (Selfless=false so the player sees their own drag-in) + set authoritative pos.
        ReceiveZoneBroadcast(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = new WIZARD_12_PROTOCOL.MSG_AGGRO {
                GlobalID = gameObj.m_globalID,
                LocX = faceX,
                LocY = faceY,
                LocZ = faceZ,
                Yaw = faceYaw,
            },
            Selfless = false,
        });
        gameObj.m_location = new Imcodec.Math.Vector3(faceX, faceY, faceZ);
        gameObj.m_orientation = new Imcodec.Math.Vector3(0, 0, faceYaw);
    }

    internal static bool TryGetSigilFaceSlot(ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY entry,
                                            out Imcodec.Math.Vector3 pos, out float yaw) {
        pos = default;
        yaw = 0f;
        if (string.IsNullOrEmpty(entry.SigilType) || string.IsNullOrEmpty(entry.SigilLoc)) {
            return false;
        }

        var template = SigilFactory.GetSigilTemplate(entry.SigilType);
        var subCircles = template?.m_subCircles;
        if (subCircles is null || subCircles.Count == 0) {
            return false;
        }

        // Player faces only (dungeon-entry sigils shouldn't carry monster circles, but filter defensively).
        var playerSlots = new List<SigilSubCircle>();
        foreach (var sc in subCircles) {
            if (sc is null || sc.m_locationType == "MonsterCircle") {
                continue;
            }

            playerSlots.Add(sc);
        }
        if (playerSlots.Count == 0) {
            return false;
        }

        var slot = playerSlots[Math.Max(0, entry.Slot) % playerSlots.Count]; // CLASSIC: the n-th wizard's face
        var pad = Util.GetVectorFromCompactString(entry.SigilLoc);

        double sigilRotation = pad.W;
        if (sigilRotation < 0) {
            sigilRotation += 2 * Math.PI;
        }

        double rotationRadians = slot.m_rotation * (Math.PI / 180.0);
        double sx = pad.X + (slot.m_radius * Math.Cos(rotationRadians - sigilRotation));
        double sy = pad.Y + (slot.m_radius * Math.Sin(rotationRadians - sigilRotation));

        double faceYaw = Math.Atan2(pad.Y - sy, pad.X - sx);
        faceYaw = (2 * Math.PI) - faceYaw - SIGIL_YAW_ERROR_COMPENSATION;
        if (faceYaw < 0) {
            faceYaw += 2 * Math.PI;
        }

        pos = new Imcodec.Math.Vector3((float) sx, (float) sy, pad.Z);
        yaw = (float) faceYaw;

        return true;
    }

}
