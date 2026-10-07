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
 * PLAYER ATTACHMENT SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages the process of authenticating and attaching a player to a game zone, 
 * handling session validation, character initialization, and zone transfer.
 * 
 * USAGE EXAMPLE:
 * Internal service used within the game server's session management system.
 * Triggered automatically during player login process.
 * 
 * NOTE:
 * - Relies on multiple microservices for authentication and zone management
 * - Performs critical security checks during player attachment
 * 
 * TODO:
 * - Implement proper realm name resolution (currently hardcoded)
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using Imlight.Classic;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class AttachService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const float PRELOGIN_DELAY_MS = 500.0f;
    private const float ATTACH_TIMEOUT_SECONDS = 15.0f;

    private Account _account;
    private Wizard _wizard;
    private GAME_5_PROTOCOL.MSG_LOGINCOMPLETE _loginCompleteMessage;
    private bool _attachReceived;
    private bool _loginCompleteSent; // CLASSIC
    private ulong _instanceOwnerId; // CLASSIC: the instance the attach joined (Classic.GroupInstances)
    private ulong _housingDeedId; // CLASSIC: ephemeral, validated lot identity.
    private int _zoneHardLimit; // CLASSIC

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new AttachService(parentActor));

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_ATTACH))]
    private void ReceiveAttach(GAME_5_PROTOCOL.MSG_ATTACH message) {
        _attachReceived = true;
        Timers.Cancel("attach-timeout");

        // Use the session key given in the message to ensure that the user didn't bypass our login server.
        // The key will be associated with the account they're trying to log into.
        ValidateAttach(message);

        // Tell the game server that the user has attached, and now we need to find a zone process for their
        // zone, or create a new one. This is an internal zone transfer that does not involve the client.
        var zoneDetails = InternalZoneTransfer(message.ZoneName, message.Location);
        if (zoneDetails is null || zoneDetails.ErrorCode != 0) {
            // CLASSIC: the client falls back and attaches again with the same key.
            Auth.SecuritySettings.GameKeys.Value.Arm(_account.AccountId);
            SendToSocket(new GAME_5_PROTOCOL.MSG_ATTACHFAILED {
                Error = zoneDetails?.ErrorCode ?? 1
            });

            return;
        }

        // CLASSIC: persist only an already authorized identity. A save failure cannot silently lose the lot on relog.
        if (ClassicRuntime.IsActive && HouseCatalog.Approved.Any()
                && !HouseCollection.RecordLocation(_wizard.CharId, zoneDetails.InstanceOwnerId,
                    zoneDetails.HousingDeedId, message.ZoneName)) {
            if (zoneDetails.HousingDeedId != 0) {
                HouseTransferEntries.Queue(_wizard.CharId, zoneDetails.InstanceOwnerId, zoneDetails.HousingDeedId,
                    message.ZoneName, DateTime.UtcNow);
                Classic.GroupInstances.QueueEntry(_wizard.CharId, message.ZoneName, zoneDetails.InstanceOwnerId, DateTime.UtcNow);
            }
            Auth.SecuritySettings.GameKeys.Value.Arm(_account.AccountId);
            SendToSocket(new GAME_5_PROTOCOL.MSG_ATTACHFAILED { Error = 1 });
            return;
        }

        _instanceOwnerId = zoneDetails.InstanceOwnerId; // CLASSIC
        _housingDeedId = zoneDetails.HousingDeedId; // CLASSIC
        _zoneHardLimit = zoneDetails.ZoneHardLimit; // CLASSIC
        // CLASSIC: housing services consume only this validated, in-process ownership context.
        // Login/attach packet bytes remain unchanged (the native blob cache uses the existing ZoneID).
        SessionActor.PublishHousingAttach(new HousingAttachContext(_wizard.CharId, zoneDetails.InstanceOwnerId,
            message.ZoneName, zoneDetails.DynamicZoneId, message.ZoneID, zoneDetails.HousingDeedId));

        // CLASSIC: logging back in to an arena whose match is over (or a copy of one nobody fights in): back to the arena
        // hall once the attach is done.
        if (Classic.Arena.ClassicArena.IsArenaZone(message.ZoneName)
                && Classic.Arena.ArenaMatchmaker.Instance?.RunFor(_wizard.CharId) is null
                && Classic.Arena.ClassicArena.Config is { } arena) {
            TellOtherServices(new CLASSIC_FEATURES_PROTOCOL.MSG_ARENARETURN {
                Zone = arena.HallZone, Location = arena.HallLocation, DelaySeconds = 5,
            });
        }

        // Set the character's location and zone to the ones given in the message.
        _wizard.SetZone(message.ZoneName, zoneDetails.ZoneDisplayName);
        _wizard.SetPersistentLocation(zoneDetails.Location);
        _wizard.SetPersistentOrientation(zoneDetails.Orientation);

        // Tiny anti-cheat measure. When the character object is created, we recalculate the game stats.
        CharacterHelper.RecalculateGameStats(_wizard);
        // CLASSIC: the native arena shop reads rank from GameStats, including before the first ranked match.
        Classic.Arena.ArenaShopSnapshot.RefreshLadder(_wizard);

        // Get the best game server for this user.
        var gameServer = GetGameServer();
        _wizard.GameServerIp = gameServer.IP;
        _wizard.GameServerPort = (ushort) gameServer.Port;

        // Craft the GameObject for this Wizard.
        var charGameObject = WizardObjectLoader.GetPlayerGameObject(_wizard);

        // Set the mobile id to the one given by the zone.
        charGameObject.m_nMobileID = zoneDetails.MobileId;

        // Set the Wizard's GameObject reference to what we just created.
        _wizard.GameObject = charGameObject;

        // Serialize the GameObject and send it to the client.
        var coSerializer = new CoreObjectSerializer(
            versionable: false,
            behaviors: SerializerFlags.Compress
        );
        var flags = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!coSerializer.Serialize(charGameObject, flags, out var localGameObjectData)) {
            Logger.Error($"User {message.UserID} failed to serialize their player object.");

            throw new SessionFatalException($"User {message.UserID} failed to serialize their player object.");
        }

        // Serialize the critical object list.
        var serializer = new ObjectSerializer(
            Versionable: false
        );
        var criticalObjects = GetCriticalObjects(zoneDetails.CriticalObjects);
        if (!serializer.Serialize(criticalObjects, flags, out var criticalObjectData)) {
            Logger.Error($"User {message.UserID} failed to serialize their critical object list.");

            throw new SessionFatalException($"User {message.UserID} failed to serialize their critical object list.");
        }

        var account = GetActiveAccount();
        var realmName = gameServer.RealmName ?? "Imlight";

        _loginCompleteMessage = new GAME_5_PROTOCOL.MSG_LOGINCOMPLETE() {
            RealmName = realmName,
            ServerTime = (uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds(),

            // Set character data.
            Data = localGameObjectData,
            IsCSR = _account.AuthLevel > AuthLevel.None ? 1 : 0, // todo: Change this back before prod!

            Permissions = 0b1100_1111,

            // Set zone data.
            ZoneName = message.ZoneName,
            ZoneID = message.ZoneID,
            DynamicZoneID = zoneDetails.DynamicZoneId,
            DynamicServerProcID = zoneDetails.DynamicZoneId,
            CriticalObjects = criticalObjectData,

            // Misc
            ShowSubscriberIcon = 0,
            // CLASSIC: members pay the full Crowns price. Left at 0, the client offered every non-member a
            // "Members Pay 0" price in the Crown Shop; at 100 it shows no members' price at all.
            SubscriberCrownsPricePercent = 100,
            TestServer = 1
        };

        // Send MSG_PRELOGIN so other services may do their work before we send the final login complete message.
        var preLoginMsg = new ZONE_102_PROTOCOL.MSG_PRELOGIN();
        TellOtherServices(preLoginMsg);

        // Send the same message to ourselves after a short delay to allow other services to prepare.
        Timers.StartSingleTimer(
            "PreLoginDelay",
            preLoginMsg,
            TimeSpan.FromMilliseconds(PRELOGIN_DELAY_MS)
        );
    }

    // CLASSIC: QuestService reports its MSG_PRELOGIN work queued (the held quests go out before MSG_LOGINCOMPLETE),
    // so login and every zone change continue at once instead of after the fixed 500 ms; the timer stays as a fallback.
    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_PRELOGINREADY))]
    private void ReceivePreLoginReady(CLASSIC_FEATURES_PROTOCOL.MSG_PRELOGINREADY message) {
        if (_loginCompleteMessage is null || _loginCompleteSent) {
            return;
        }

        Timers.Cancel("PreLoginDelay");
        ReceivePreLogin(null);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PRELOGIN))]
    private void ReceivePreLogin(ZONE_102_PROTOCOL.MSG_PRELOGIN message) {
        if (_loginCompleteSent) { // CLASSIC: once per attach (the ready report or the fallback timer, whichever is first)
            return;
        }

        _loginCompleteSent = true;
        var charGameObject = _wizard.GameObject as WizClientObject;

        // The client only counts critical objects whose MSG_NEWOBJECT arrives after MSG_LOGINCOMPLETE; one
        // that arrives earlier holds its loading screen until a 30 second timeout. Joining the zone sends them.
        SendToSocket(_loginCompleteMessage);

        // Wait for the zone to confirm the player was added
        var addPlayerResponse = AddPlayerToZone(charGameObject, _wizard);
        if (addPlayerResponse.WizardGameObject == null) {
            Logger.Error($"Failed to add player {_wizard.CharId} to zone.");
            Auth.SecuritySettings.GameKeys.Value.Arm(_account.AccountId); // CLASSIC: the client falls back with its key
            SendToSocket(new GAME_5_PROTOCOL.MSG_ATTACHFAILED {
                Error = 1
            });

            return;
        }

        // Add the player to the online player collection.
        // I don't know why this is normally blocking. Put it on a background thread.
        Task.Run(() => AddPlayerToOnlineCollection(_wizard,
                                                   _wizard.Zone,
                                                   _wizard.ZoneDisplayName,
                                                   "Centaur",
                                                   SessionActor.ActorRef));

        TellOtherServices(new SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE { AttachGeneration = addPlayerResponse.AttachGeneration, ZoneActorRef = addPlayerResponse.ZoneActorRef });

        // Attach succeeded — remove the fallback registration so stale entries
        // don't accumulate on the GameServer.
        SessionActor.ServerRef.Tell(new SERVICE_101_PROTOCOL.MSG_REMOVE_FALLBACK {
            RemoteIp = SessionActor.RemoteIp
        });

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEMANA {
            Mana = _wizard.GameStats.m_currentMana,
            MaxMana = _wizard.GameStats.GetClientTypeAlternative().m_baseMana,
        });
    }

    private void ValidateAttach(GAME_5_PROTOCOL.MSG_ATTACH message) {
        var loadedAtUtc = DateTime.UtcNow; // CLASSIC: the key check below loads the account (Game/AccountSessions.cs)
        if (!ValidateLoginKey(message.LoginKey, message.UserID, message.SessionID, out var account)) {
            // CLASSIC: what the client sent in place of a valid proof (never the key itself).
            Logger.Debug("Refused MSG_ATTACH: user {User} char {Char} zone {Zone} ({ZoneId}) slot {Slot} session slot {SessionSlot} " +
                         "target {Target} reattach {Reattach} retry {Retry}; login key {KeyLength} chars, pass key {PassLength} chars, " +
                         "session id {Sid}",
                Logger.Args((ulong) message.UserID, (ulong) message.CharID, message.ZoneName, (ulong) message.ZoneID, message.Slot,
                    message.SessionSlot, (ulong) message.TargetPlayerID, message.Reattach, message.Retry,
                    message.LoginKey.ToString()?.Length ?? 0, message.PassKey.ToString()?.Length ?? 0,
                    (ulong) message.SessionID == 0 ? "0" : "set"));
            SendToSocket(new GAME_5_PROTOCOL.MSG_ATTACHFAILED() {
                Error = 1,
                Rejected = 1,
            });

            throw new SessionFatalException(
                $"User [{message.UserID}] failed to validate their login key."); // CLASSIC: the key is not logged
        }
        // CLASSIC: one game session (so one wizard) per account. An older session of the account is closed and has
        // stopped, with its last saves in, before this one goes on; the account is loaded again if that session stopped
        // after this copy was read. Refused when the older session will not stop.
        switch (AccountSessions.Claim(account.AccountId, SessionActor, loadedAtUtc)) {
            case AccountClaim.Refused:
                SendToSocket(new GAME_5_PROTOCOL.MSG_ATTACHFAILED() {
                    Error = 1,
                    Rejected = 1,
                });

                throw new SessionFatalException($"User [{message.UserID}] is still logged in elsewhere.");
            case AccountClaim.AdmittedReload:
                account = AccountCollection.GetAccount(account.AccountId) ?? account;
                break;
        }

        if (!GetWizardFromAccount(account, message.CharID, out var wizard)) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_ATTACHFAILED() {
                Error = 1,
                NoDisconnect = 1, // @todo: find out what these error codes mean.
                Rejected = 1,
            });

            throw new SessionFatalException($"User [{message.UserID}] tried to attach with a character " +
                                            $"they did not have.");
        }

        // CLASSIC: playing keeps the login key alive (back to character select validates with it again).
        var playingAccountId = account.AccountId;
        System.Threading.Tasks.Task.Run(() => ClientKeyCollection.Touch(playingAccountId));

        // This is the first authentication action the user will send on the game server. Send messages to the
        // other services denoting both the account and character this SessionActor just logged into.
        SetAccountInternally(account);
        SetCharacterInternally(wizard);
    }

    private bool ValidateLoginKey(ByteString key, ulong userId, ulong sessionId, out Account account) {
        account = null;

        var msg = new SERVER_100_PROTOCOL.MSG_VALIDATESESSIONKEY() {
            Key = key,
            UserID = userId,
            SessionID = sessionId, // CLASSIC: a transfer's proof (GameSessionKeys)
            SessionActor = SessionActor
        };
        var rsp = AskServer<SERVER_100_PROTOCOL.MSG_VALIDATESESSIONKEYRSP>(msg);

        account = rsp.Account;

        return rsp.ErrorCode == 0;
    }

    private bool GetWizardFromAccount(Account account, ulong charId, out Wizard character) {
        var result = account.GetCharacter(charId);
        character = result;

        return result is not null;
    }

    private void SetAccountInternally(Account account) {
        TellOtherServices(new ACCOUNT_104_PROTOCOL.MSG_ACCOUNT() {
            Account = account
        });

        this._account = account;
    }

    private void SetCharacterInternally(Wizard character) {
        TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_SETACTIVEWIZARD {
            Wizard = character
        });

        this._wizard = character;
    }

    private ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP InternalZoneTransfer(string zoneName, string location) {
        var zoneMsg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = zoneName,
            DestinationLocation = location,
            SendToClient = false,
            // CLASSIC: a wizard who dropped mid-fight logs back in to the instance that holds their seat, and a wizard
            // on the way into someone else's instance (a sigil group, a friend's dungeon) attaches to that instance.
            OwnerCharId = Classic.GroupInstances.OwnerForAttach(_wizard.CharId, zoneName, DateTime.UtcNow),
        };

        // CLASSIC: the native attach has no trusted deed field. Only a completed internal transfer, or the
        // owner's durable saved lot on relog, can select a house. Visitors still require its owner present.
        if (ClassicRuntime.IsActive) {
            var now = DateTime.UtcNow;
            if (HouseTransferEntries.TryConsume(_wizard.CharId, zoneMsg.OwnerCharId, zoneName, now, out var deed)) {
                if (!HouseTransferEntries.MayEnter(_wizard.CharId, zoneMsg.OwnerCharId, deed, zoneName))
                    return new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP { ErrorCode = 1 };
                zoneMsg.HousingDeedId = deed;
                zoneMsg.IsPrivate = true;
            } else if (HouseCatalog.IsApprovedRoom(zoneName)) {
                if (zoneMsg.OwnerCharId != _wizard.CharId)
                    return new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP { ErrorCode = 1 };
                if (HouseCollection.HasSavedLocation(_wizard.CharId, _wizard.CharId, zoneName)) {
                    if (!HouseCollection.TryGetSavedLocation(_wizard.CharId, _wizard.CharId, zoneName, out var savedDeed))
                        return new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP { ErrorCode = 1 };
                    zoneMsg.HousingDeedId = savedDeed;
                } else {
                    if (!HouseCollection.TryGetEquipped(_wizard, out var home)
                            || !HouseCatalog.TryRoom(home.TemplateId, zoneName, out _))
                        return new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP { ErrorCode = 1 };
                    zoneMsg.HousingDeedId = home.DeedId;
                }
                zoneMsg.IsPrivate = true;
            }
        }

        // CLASSIC: logging out resets the dungeon the wizard was in (2009 rule, Classic.InstanceResets), unless a fight
        // there holds the wizard's seat or someone else is inside. The attach that ends a zone transfer is not a login.
        if (Classic.InstanceResets.IsActive && Classic.InstanceResets.ResetOnLogin(_wizard.CharId, zoneName, zoneMsg.OwnerCharId,
                DateTime.UtcNow)) {
            zoneMsg.ResetInstance = true;
        }

        var reply = AskOtherService<ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP>(zoneMsg);
        // CLASSIC: native retry after a failed allocation retains the same proof, without changing key validation.
        if (reply?.ErrorCode != 0 && zoneMsg.HousingDeedId != 0) {
            HouseTransferEntries.Queue(_wizard.CharId, zoneMsg.OwnerCharId, zoneMsg.HousingDeedId, zoneName, DateTime.UtcNow);
            Classic.GroupInstances.QueueEntry(_wizard.CharId, zoneName, zoneMsg.OwnerCharId, DateTime.UtcNow);
        }
        return reply;
    }

    private ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP AddPlayerToZone(WizClientObject charObj, Wizard wizard) {
        var msg = new ZONE_102_PROTOCOL.MSG_ADDPLAYER {
            PlayerActor = SessionActor.ActorRef,
            PlayerObject = charObj,
            Wizard = wizard,
            ActualWizardName = wizard.PlayerNameBehavior.GetWizardName(),
        };

        return AskOtherService<ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP>(msg);
    }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
    private async void AddPlayerToOnlineCollection(Wizard wizard,
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
                                                   string zoneName,
                                                   string zoneDisplayName,
                                                   string realmName,
                                                   IActorRef playerActor) {
        var onlinePlayerRef = new OnlinePlayer {
            SessionId = SessionActor.SessionID,
            AccountId = wizard.AccountId,
            CharacterId = wizard.CharId,
            CurrentZone = zoneName,
            CurrentRealm = realmName,
            ActorPath = playerActor.Path.ToString(),
            InstanceOwnerId = _instanceOwnerId, // CLASSIC
            HousingDeedId = _housingDeedId, // CLASSIC
            ZoneHardLimit = _zoneHardLimit, // CLASSIC
        };

        OnlinePlayerCollection.AddOnlinePlayer(onlinePlayerRef);

        // CLASSIC: this runs on a pool thread; a session that disposed meanwhile already cleared its entries, so take
        // this late one back off (else the list keeps a dead session until restart).
        if (SessionActor.IsDisposed) {
            OnlinePlayerCollection.RemoveSessionByActorPath(onlinePlayerRef.ActorPath);
        }
    }

    private SERVER_100_PROTOCOL.MSG_SERVERINFO GetGameServer() {
        var msg = new SERVER_100_PROTOCOL.MSG_QUERYSERVER();

        return AskServer<SERVER_100_PROTOCOL.MSG_SERVERINFO>(msg);
    }

    protected override void PreStart() {
        Timers.StartSingleTimer(
            "attach-timeout",
            new SERVICE_101_PROTOCOL.MSG_ATTACH_TIMEOUT(),
            TimeSpan.FromSeconds(ATTACH_TIMEOUT_SECONDS)
        );

        base.PreStart();
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACH_TIMEOUT))]
    private void ReceiveAttachTimeout(SERVICE_101_PROTOCOL.MSG_ATTACH_TIMEOUT _) {
        if (_attachReceived) {
            return;
        }

        Logger.Warning("Attach timeout for session {SessionId} — attempting fallback zone transfer.",
            Logger.Args(SessionActor.SessionID));

        // Query the GameServer for fallback data registered by the old session.
        var query = new SERVICE_101_PROTOCOL.MSG_QUERY_FALLBACK {
            RemoteIp = SessionActor.RemoteIp
        };
        var rsp = AskServer<SERVICE_101_PROTOCOL.MSG_QUERY_FALLBACK_RSP>(query);

        if (rsp is not null && rsp.Found) {
            Logger.Information("Fallback found for {RemoteIp} — redirecting to zone {Zone}.",
                Logger.Args(SessionActor.RemoteIp, rsp.FallbackZone));

            // CLASSIC: this connection never attached, so it proves nothing: it gets a key only when the account was
            // just transferred to this address (the fallback entry is keyed by address), and a fresh one.
            var keys = Auth.SecuritySettings.GameKeys.Value;
            if (!keys.HasTransferKeyFor(rsp.UserId, SessionActor.RemoteIp)) {
                Logger.Warning("Fallback for {RemoteIp} refused: account {Account} has no transfer to this address.",
                    Logger.Args(SessionActor.RemoteIp, rsp.UserId));
                SessionActor.ServerRef.Tell(new SERVICE_101_PROTOCOL.MSG_REMOVE_FALLBACK { RemoteIp = SessionActor.RemoteIp });
                CloseSession();
                return;
            }

            var transferKey = keys.IssueTransfer(rsp.UserId, SessionActor.RemoteIp);
            var serverTransfer = new GAME_5_PROTOCOL.MSG_SERVERTRANSFER {
                IP = rsp.GameServerIp,
                TCPPort = rsp.GameServerPort,
                UDPPort = rsp.GameServerPort,
                Key = transferKey.Key,
                FallbackKey = transferKey.Key,
                UserID = rsp.UserId,
                CharID = rsp.CharId,
                ZoneName = rsp.FallbackZone,
                ZoneID = rsp.FallbackZoneId,
                Location = rsp.FallbackLocation,
                Slot = 0,
                SessionSlot = 0,
                SessionID = transferKey.SessionId, // CLASSIC: the client echoes it in MSG_ATTACH (the proof)
                TargetPlayerID = rsp.CharId,
                TransitionID = 1,
                FallbackIP = rsp.GameServerIp,
                FallbackTCPPort = rsp.GameServerPort,
                FallbackUDPPort = rsp.GameServerPort,
                FallbackZone = rsp.FallbackZone,
                FallbackZoneID = rsp.FallbackZoneId
            };
            ZoneService.LogTransfer(serverTransfer); // CLASSIC
            SendToSocket(serverTransfer);

            // Remove the fallback entry now that we've consumed it.
            SessionActor.ServerRef.Tell(new SERVICE_101_PROTOCOL.MSG_REMOVE_FALLBACK {
                RemoteIp = SessionActor.RemoteIp
            });

            return;
        }

        Logger.Warning("No fallback found for {RemoteIp} — closing session.",
            Logger.Args(SessionActor.RemoteIp));
        CloseSession();
    }

    // CLASSIC: leaving the game (logout to character select, a zone change) keeps the login key alive for another idle
    // window, so a long stay in one zone does not expire the key the client validates with next.
    protected override void OnDispose() {
        if (_account is { } account) {
            var accountId = account.AccountId;
            System.Threading.Tasks.Task.Run(() => ClientKeyCollection.Touch(accountId));
        }

        base.OnDispose();
    }

    private static CriticalObjectList GetCriticalObjects(List<GID> objectIDs) => new() { m_objList = objectIDs };

}
