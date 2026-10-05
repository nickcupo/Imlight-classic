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
 * FRIENDS SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages player friend requests, buddy list, and relationship status.
 * 
 * USAGE EXAMPLE:
 * Internal service handling various social-related messages and actions 
 * within the game server's session management system.
 * 
 * NOTE:
 * Flow to adding a friend:
    1. Player A clicks on player B's character and selects "Add Friend".
        Player A sends GAME_5_PROTOCOL.MSG_BUDDYREQUESTADD to the server. Player A keeps track of player B's ID.
    2. Server forwards the request to player B with CHARACTER_103_PROTOCOL.MSG_BUDDYREQUESTADDFWD.
    3. Player B receives the request, and adds it to their pending friend requests. They will see a notification in the client.
    4. Player B can now accept or deny the request.
        a. If they accept, they send GAME_5_PROTOCOL.MSG_BUDDYREQUESTACCEPT to the server.
        b. If they deny, they send GAME_5_PROTOCOL.MSG_BUDDYREQUESTDENY to the server.
    5. Server forwards the response to player A with CHARACTER_103_PROTOCOL.MSG_BUDDYREQUESTREPLYFWD. If the sender
         is offline, we will have to go to the database directly to add the friend.
    6. Player A receives the response. If the response is an acceptance, they will add the friend to their list.
        Player A's game client will see a notification that the friend request has been accepted.

    - The protocol uses two distinct ID domains for characters:
        - Character ID — the persistent character/account-level identifier (m_characterId)
        - GameObject ID — the runtime object identifier (WizClientObject.m_globalID.m_full)
    - Field names are not sufficient to determine the ID domain. In particular, fields named GID, GlobalID, or
        similar may contain either a Character ID or a GameObject ID depending on the message and context.

    - Instances where the GameObject ID is used:
        - GAME_5_PROTOCOL.MSG_BUDDYSTATS.TargetCharacterGID
        - GAME_5_PROTOCOL.MSG_BUDDYSTATS.BuddyID
        - GAME_5_PROTOCOL.MSG_BUDDYENTRY.ListOwnerGID
        - GAME_5_PROTOCOL.MSG_BUDDYLISTCOMPLETE.ListOwnerGID
        - GAME_5_PROTOCOL.MSG_BUDDYSTATUSUPDATE.ListOwnerGID
        - GAME_5_PROTOCOL.MSG_BUDDYREQUESTACCEPT.SourceObjectID
        - GAME_5_PROTOCOL.MSG_BUDDYREQUESTACCEPT.DestObjectID
        - GAME_5_PROTOCOL.MSG_BUDDYREQUESTADD.EntryGID
            - For the initial client → server buddy request, this is the target's GameObject ID.
    - Instances where the Char ID is used:
        - GAME_5_PROTOCOL.MSG_BUDDYENTRY.EntryGID
        - GAME_5_PROTOCOL.MSG_IGNOREADD.CharacterGID
        - GAME_5_PROTOCOL.MSG_IGNOREDROP.CharacterGID
        - GAME_5_PROTOCOL.MSG_GOTOPLAYER.TargetCharacterID
        - GAME_5_PROTOCOL.MSG_BUDDYREQUESTACCEPT.ListOwnerGID
        - GAME_5_PROTOCOL.MSG_BUDDYREQUESTACCEPT.EntryGID
        - GAME_5_PROTOCOL.MSG_BUDDYSTATUSUPDATE.EntryGID

    - Consequently, buddy messages can contain both ID domains in the same message. For example, MSG_BUDDYREQUESTACCEPT uses:
        - GameObject IDs:
            - SourceObjectID — the recipient's GameObject ID
            - DestObjectID — the requester's GameObject ID
        - Character IDs:
            - ListOwnerGID — the requester's Character ID
            - EntryGID — the recipient's Character ID
    
    - GlobalID should therefore be treated as a context-dependent protocol field, not as a synonym for GameObjectID.
        For example, CharacterInfo.GlobalID contains the Character ID despite its name.

    - The game client will not request buddy stats (`MSG_BUDDYSTATS`) if the friend is offline

    ============ BUDDY STATS ============ 

    !!! IN PROGRESS !!!

    StatBlock    : 
        - ObjectSerializer (StatefulFlags)
        - Type: WizGameStats
    EquipBlock   : 
        - ObjectSerializer (StatefulFlags)
        - Type: ClientWizEquipmentBehavior

    =====================================
 * 
 * TODO:
 * - For `MSG_BUDDYSTATS`, we need to implement the CRC32 hash check. Currently, we just send the stats regardless.
 * - Implement the `PreviousName` field in `MSG_BUDDYENTRY`.
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 04/27/2025
 */

using System;
using System.Collections.Concurrent;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.Math;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class FriendsService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const byte OFFLINE_STATUS_CODE = 1;
    private const byte ONLINE_STATUS_CODE = 4;
    private const float QUERY_TELEPORT_WIZARD_TIMEOUT_IN_SECONDS = 2;

    // CLASSIC: each stat lookup reads the database; a burst of 10, then 2 a second (security audit 2026-10-04).
    private readonly Imlight.Classic.Security.TokenBucket _buddyStatsRate = new(capacity: 10, refillPerSecond: 2);
    private readonly uint _englishLocaleHash = StringHash.Compute("English");

    // CLASSIC: friend requests waiting for an answer, (recipient, requester). The wizard's pending list holds both the
    // requests it sent and the ones it received, so a wizard could "accept" its own request (a friendship the other
    // never agreed to) and two wizards asking each other at the same moment each dropped the other's request as
    // "already pending". Kept server-wide so a zone change (a new session) does not lose it.
    private static readonly ConcurrentDictionary<(ulong Recipient, ulong Requester), DateTime> s_incomingRequests = new();

    internal static void NoteIncomingRequest(ulong recipient, ulong requester) {
        var now = DateTime.UtcNow;
        foreach (var stale in s_incomingRequests.Where(r => now - r.Value > TimeSpan.FromHours(1)).Select(r => r.Key).ToList()) {
            s_incomingRequests.TryRemove(stale, out _); // never answered
        }

        s_incomingRequests[(recipient, requester)] = now;
    }

    internal static bool HasIncomingRequest(ulong recipient, ulong requester)
        => s_incomingRequests.ContainsKey((recipient, requester));

    internal static bool TakeIncomingRequest(ulong recipient, ulong requester) {
        if (!s_incomingRequests.TryRemove((recipient, requester), out _)) {
            return false;
        }

        // Two wizards who asked each other: one answer settles both requests.
        s_incomingRequests.TryRemove((requester, recipient), out _);

        return true;
    }

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new FriendsService(parentActor));

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_BUDDYREQUESTLIST))]
    private void ReceiveBuddyRequestList(GAME_5_PROTOCOL.MSG_BUDDYREQUESTLIST message) {
        // When the player logs into the game, their client will request a list of their buddies.
        // We will iterate through the player's friends and send them an entry for each.
        var wizard = GetActiveWizard();
        var charId = wizard.CharId;

        // Iterate through the player's friends and send them an entry.
        var buddies = BuddyRelationshipCollection.GetBuddiesForWizard(charId);
        foreach (var buddy in buddies.Where(buddy => buddy != null)) {
            // When players add each other, they create a "Relationship." This is a record of their
            // interactions. When a player unfriends/blocks/reports another, that relationship is still exist
            // but is marked as "broken up." 
            if (!wizard.FriendsBehavior.TryGetRelationship(buddy.CharId, out var relationship)
                || relationship.IsBrokenUp) {
                // Ignore the relationship if it is broken up.
                Logger.Debug("{0} has a friend named {1} (ID: {2}), but the relationship is broken up.",
                    Logger.Args(wizard.PlayerNameBehavior.GetWizardName(),
                    buddy.PlayerNameBehavior.GetWizardName(),
                    buddy.CharId)
                );

                continue;
            }
            else if (relationship.Blocked) {
                // Ignore the relationship if it is blocked.
                Logger.Debug("{0} has a friend named {1} (ID: {2}), but the relationship is blocked.",
                    Logger.Args(wizard.PlayerNameBehavior.GetWizardName(),
                    buddy.PlayerNameBehavior.GetWizardName(),
                    buddy.CharId)
                );

                continue;
            }
            else if (!relationship.IsBrokenUp && !relationship.Blocked) {
                // This relationship is valid. We can send it to the client.
                SendBuddyEntry(buddy, relationship, wizard);
            }
            else {
                Logger.Warning("{0} has a friend with character ID {1}, but the relationship was not found.",
                    Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), buddy.CharId));
            }
        }

        SendBuddyListEnd(wizard);
        InformBuddiesOfStatusChange(true);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_BUDDYSTATS))]
    private void ReceiveBuddyStats(GAME_5_PROTOCOL.MSG_BUDDYSTATS message) {
        // When the player clicks on a friend in their buddy list, they will request the friend's stats (`MSG_BUDDYSTATS`).
        // The client caches each friends stats and sends the server the CRC32 hash of their cached stats.
        // We will hash the respective fields on the server side and compare them with the client's hash.
        // If the hashes do not match, we will send a new byte blob containing the serialized data to update the client's cache.
        if (!Wizard.TryGetCharacterId(message.BuddyID, out var buddyCharID)) {
            return;
        }

        // CLASSIC: oneself, a friend or a wizard in the same zone, and rate-limited (security audit 2026-10-04).
        var self = GetActiveWizard();
        if (!_buddyStatsRate.TryTake(DateTimeOffset.UtcNow)) {
            return;
        }

        self.FriendsBehavior.TryGetRelationship(buddyCharID, out var statsRelationship);
        var sameZone = TryGetOnlinePlayer(buddyCharID, out var statsTarget) && statsTarget.CurrentZone == self.Zone;
        if (!FriendRules.MayViewStats(self.CharId, buddyCharID, statsRelationship, sameZone)) {
            Logger.Debug("{0} asked for the stats of character {1}, who is neither a friend nor nearby.",
                Logger.Args(self.CharId, buddyCharID));

            return;
        }

        // TODO: Imlight currently doesn't care about the CRC. It will send the stats regardless.
        var buddyFromDatabase = WizardCollection.GetCharacter(buddyCharID);
        if (buddyFromDatabase is null) {
            Logger.Error("Player {0} requested stats for character ID {1}, but the character was not found.",
                Logger.Args(GetActiveWizard().PlayerNameBehavior.GetWizardName(), buddyCharID));

            return;
        }

        var returnMessage = new GAME_5_PROTOCOL.MSG_BUDDYSTATS {
            BuddyID = message.BuddyID,
            Level = (uint) buddyFromDatabase.MagicSchoolBehavior.Level,
            School = (uint) buddyFromDatabase.MagicSchoolBehavior.MagicSchool,
            Gender = 0, // TODO: ?? Is 0 female? Male?

            // Set each CRC as '1' to inform the client we're giving it new data.
            StatBlockCRC = 1,
        };

        var statBlock = GetBuddyGameStats(buddyFromDatabase);

        // Serialize each of the blocks.
        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.SerializeFlags
        );

        // Check if the serialization was successful.
        if (!serializer.Serialize(statBlock, 1, out var statBlockBytes)) {
            Logger.Error("Player {0} requested stats for character ID {1}, but the serialization failed.",
                Logger.Args(GetActiveWizard().PlayerNameBehavior.GetWizardName(), buddyCharID));

            return;
        }
        else {
            // Set the serialized data in the return message.
            returnMessage.StatBlock = statBlockBytes;

            // Log.
            var wizardName = GetActiveWizard().PlayerNameBehavior.GetWizardName();
            Logger.Debug("{0} has requested stats for character ID {1}.",
                Logger.Args(wizardName, buddyCharID));
        }


        SendToSocket(returnMessage);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_BUDDYREQUESTADD))]
    private void ReceiveBuddyRequestAdd(GAME_5_PROTOCOL.MSG_BUDDYREQUESTADD message) {
        // (SEE TOP OF FILE FOR FLOW) Step 1:
        // The player client has clicked on another player and selected "Add Friend".
        // We need to forward this request to the recipient. We will find the online actor for the recipient
        // and send the internal message "MSG_BUDDYREQUESTADDFWD" to them.
        var wizard = GetActiveWizard();

        if (!Wizard.TryGetCharacterId(message.EntryGID, out var buddyCharID) || buddyCharID == wizard.CharId) {
            return;
        }

        // CLASSIC: no request between two wizards when either has ignored the other (security audit 2026-10-04).
        if (BuddyRelationshipCollection.HasBlocked(buddyCharID, wizard.CharId)
                || BuddyRelationshipCollection.HasBlocked(wizard.CharId, buddyCharID)) {
            Logger.Debug("{0} friend request to {1} dropped: ignored.", Logger.Args(wizard.CharId, buddyCharID));

            return;
        }

        // Check to see if the recipient is online. If not, there is naught we can do.
        if (!TryGetOnlinePlayer(buddyCharID, out var onlinePlayer)) {
            var wizardName = wizard.PlayerNameBehavior.GetWizardName();
            Logger.Warning("{0} tried to add character ID {1} as a friend, but the character is not online.",
                Logger.Args(wizardName, buddyCharID));

            var errorMsg = new GAME_5_PROTOCOL.MSG_BUDDYREQUESTERROR {
                ListOwnerGID = wizard.GameObjectID,
                EntryGID = message.EntryGID,
                Error = 1
            };
            SendToSocket(errorMsg);

            return;
        }

        // Add the pending request to the sender. When we get a response from the recipient, we'll know it's valid.
        if (!wizard.AddPendingFriendRequest(buddyCharID)) {
            return;
        }

        // Forward the request to the recipient.
        // (SEE TOP OF FILE FOR FLOW) Step 2
        var fwdMsg = new CHARACTER_103_PROTOCOL.MSG_BUDDYREQUESTADDFWD {
            RequesterCharId = wizard.CharId,
            RecipientCharId = buddyCharID,
            OwnerName = wizard.PlayerNameBehavior.GetWizardName(),
            OwnerLevel = (byte) wizard.MagicSchoolBehavior.Level,
            OwnerSchool = wizard.MagicSchoolBehavior.MagicSchool.ToString()
        };
        Context.ActorSelection(onlinePlayer.ActorPath).Tell(fwdMsg);

        Logger.Debug("{0} has sent a friend request to character ID {1}.",
            Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), buddyCharID));
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_BUDDYREQUESTADDFWD))]
    private void ReceiveBuddyRequestAddFwd(CHARACTER_103_PROTOCOL.MSG_BUDDYREQUESTADDFWD message) {
        // (SEE TOP OF FILE FOR FLOW) Step 3:
        // This actor has received a friend request from another player. We need to add it to the pending requests,
        // and inform the game client.
        var wizard = GetActiveWizard();

        // Is the owner ID.. ourselves?
        if (message.RequesterCharId == wizard.CharId) {
            Logger.Warning("{0} tried to add themselves as a friend.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName()));

            return;
        }

        var buddyCharID = message.RequesterCharId;

        // Check to see if we already have this friend request pending. CLASSIC: a request received, not one we sent.
        if (HasIncomingRequest(wizard.CharId, buddyCharID)) {
            Logger.Warning("{0} tried to add character ID {1} as a friend, but the request is already pending.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), buddyCharID));

            return;
        }

        // Add the pending request to the recipient. When we get a response from the sender, we'll know it's valid.
        wizard.AddPendingFriendRequest(buddyCharID);
        NoteIncomingRequest(wizard.CharId, buddyCharID); // CLASSIC

        // Inform the game client. We will now await the player's response.
        var clientMsg = new GAME_5_PROTOCOL.MSG_BUDDYREQUESTADD {
            ListOwnerGID = message.RequesterCharId,
            EntryGID = message.RecipientCharId,
            OwnerName = message.OwnerName,
            OwnerLevel = message.OwnerLevel,
            OwnerSchool = message.OwnerSchool
        };
        SendToSocket(clientMsg);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_BUDDYREQUESTACCEPT))]
    private void ReceiveBuddyRequestAccept(GAME_5_PROTOCOL.MSG_BUDDYREQUESTACCEPT message) {
        // (SEE TOP OF FILE FOR FLOW) Step 4a: The recipient's game client has accepted the friend request.
        var wizard = GetActiveWizard();

        // Ensure that this wizard was even pending. If not, log an error and return.
        // Replies identify the saved requester, unlike the initial object-targeted add request.
        var buddyCharID = message.ListOwnerGID;
        // CLASSIC: only a request this wizard received can be accepted.
        if (!TakeIncomingRequest(wizard.CharId, buddyCharID) | !wizard.RemovePendingFriendRequest(buddyCharID)) {
            Logger.Error("{0} tried to add character ID {1} as a friend, but the request was not pending.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), buddyCharID));

            return;
        }

        // Add the wizard as a friend. If they are already friends, log an error and return.
        if (!wizard.AddOrRepairRelationship(buddyCharID)) {
            Logger.Error("{0} could not add or update the relationship with character ID {1}.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), buddyCharID));

            return;
        }

        // Forward the acceptance to the sender.
        // (SEE TOP OF FILE FOR FLOW) Step 5a
        if (!TryGetOnlinePlayer(buddyCharID, out var onlinePlayer)) {
            // The sender is not online. We'll have to go to the database directly to add the friend.
            var offlineWizard = WizardCollection.GetCharacter(buddyCharID);

            // Ensure that this wizard was even pending. If not, log an error and return.
            if (offlineWizard is null || !offlineWizard.RemovePendingFriendRequest(wizard.CharId)) {
                Logger.Error("{0} tried to add character ID {1} as a friend, but the request was not pending.",
                    Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), buddyCharID));

                return;
            }

            WizardCollection.UpdateCharacterFriendBehavior(offlineWizard);
        }
        else {
            if (!wizard.FriendsBehavior.TryGetRelationship(buddyCharID, out var relationship)) {
                Logger.Error("{0} tried to add character ID {1} as a friend, but the relationship was not found.",
                    Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), buddyCharID));

                return;
            }

            // The player is still online, so we can forward the acceptance.
            var fwdMsg = new CHARACTER_103_PROTOCOL.MSG_BUDDYREQUESTREPLYFWD {
                RequesterCharId = buddyCharID,
                RecipientCharId = wizard.CharId,
                Accept = true,
                NewRelationship = relationship
            };
            Context.ActorSelection(onlinePlayer.ActorPath).Tell(fwdMsg);
        }

        // Echo the packet back to the client.
        SendToSocket(message);

        // Send a buddy entry so the accepter sees the new friend immediately.
        if (wizard.FriendsBehavior.TryGetRelationship(buddyCharID, out var newRelationship)) {
            var buddyWizard = WizardCollection.GetCharacter(buddyCharID);
            if (buddyWizard != null) {
                SendBuddyEntry(buddyWizard, newRelationship, wizard);
                SendBuddyListEnd(wizard);
            }
        }
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_BUDDYREQUESTDENY))]
    private void ReceiveBuddyRequestDeny(GAME_5_PROTOCOL.MSG_BUDDYREQUESTDENY message) {
        // (SEE TOP OF FILE FOR FLOW) Step 4b: The recipient has denied the friend request.
        var wizard = GetActiveWizard();
        // Replies identify the saved requester, unlike the initial object-targeted add request.
        var buddyCharID = message.ListOwnerGID;
        // Ensure that this wizard was even pending. If not, log an error and return. CLASSIC: a request it received.
        if (!TakeIncomingRequest(wizard.CharId, buddyCharID) | !wizard.RemovePendingFriendRequest(buddyCharID)) {
            Logger.Error("{0} tried to deny a friend request from character ID {1}, but the request was not pending.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), buddyCharID));

            return;
        }

        // Forward the acceptance to the sender.
        // (SEE TOP OF FILE FOR FLOW) Step 5b
        if (!TryGetOnlinePlayer(buddyCharID, out var onlinePlayer)) {
            // The sender is not online. We'll have to go to the database directly to remove the pending request.
            var offlineWizard = WizardCollection.GetCharacter(buddyCharID);

            // Ensure that this wizard was even pending. If not, log an error and return.
            if (offlineWizard is null || !offlineWizard.RemovePendingFriendRequest(wizard.CharId)) {
                Logger.Error("{0} tried to deny a friend request from character ID {1}, but the request was not pending.",
                    Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), buddyCharID));

                return;
            }

            WizardCollection.UpdateCharacterFriendBehavior(offlineWizard);
        }
        else {
            // The player is still online, so we can forward the denial.
            var fwdMsg = new CHARACTER_103_PROTOCOL.MSG_BUDDYREQUESTREPLYFWD {
                RequesterCharId = buddyCharID,
                RecipientCharId = wizard.CharId,
                Accept = false
            };
            Context.ActorSelection(onlinePlayer.ActorPath).Tell(fwdMsg);
        }
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_BUDDYREQUESTREPLYFWD))]
    private void ReceiveBuddyRequestReplyFwd(CHARACTER_103_PROTOCOL.MSG_BUDDYREQUESTREPLYFWD message) {
        // (SEE TOP OF FILE FOR FLOW) Step 6:
        // We are informing player A that player B has accepted or denied their friend request.
        var myWizard = GetActiveWizard();
        IMessage clientMsg;

        var buddyCharID = message.RecipientCharId;
        myWizard.RemovePendingFriendRequest(buddyCharID);

        if (message.Accept) {
            var entryWizard = WizardCollection.GetCharacter(buddyCharID);
            if (entryWizard is null) {
                return;
            }
            var epochInSeconds = (uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Log
            var myName = myWizard.PlayerNameBehavior.GetWizardName();
            var entryName = entryWizard.PlayerNameBehavior.GetWizardName();
            Logger.Debug("{0} (ID {1}) has received a friend request reply from {2} (ID {3}). They have accepted.",
                Logger.Args(myName, myWizard.CharId, entryName, entryWizard.CharId));

            // Add the wizard as a friend. If they are already friends, log an error and return.
            if (!myWizard.AddOrRepairRelationship(message.NewRelationship)) {
                Logger.Error("{0} could not add or update the relationship with character ID {1}.",
                    Logger.Args(myWizard.PlayerNameBehavior.GetWizardName(), buddyCharID));

                return;
            }

            var myWizardNameHex = myWizard.PlayerNameBehavior.GetWizardNameAsByteHexString();
            var myWizardNameBytes = DataManipulation.SpacedHexStringToBytes(myWizardNameHex);
            var entryWizardNameHex = entryWizard.PlayerNameBehavior.GetWizardNameAsByteHexString();
            var entryWizardNameBytes = DataManipulation.SpacedHexStringToBytes(entryWizardNameHex);

            // Inform the game client that the friend request has been accepted.
            clientMsg = new GAME_5_PROTOCOL.MSG_BUDDYREQUESTACCEPT {
                ListOwnerGID = message.RequesterCharId,
                EntryGID = message.RecipientCharId,
                OwnerName = myWizardNameBytes,
                EntryName = entryWizardNameBytes,
                SourceObjectID = myWizard.GameObjectID,
                DestObjectID = entryWizard.GameObjectID,
                Error = 0,
                Permissions = (uint) entryWizard.Account.GetAccountFlags(),
                EntryLocale = _englishLocaleHash,
                FriendInfo = 197120,           // TODO: What is this?
                FriendDate = epochInSeconds,
                FriendStatusDate = epochInSeconds,
            };

            // Send a buddy entry so the requester sees the new friend immediately.
            if (myWizard.FriendsBehavior.TryGetRelationship(buddyCharID, out var newRel)) {
                SendBuddyEntry(entryWizard, newRel, myWizard);
                SendBuddyListEnd(myWizard);
            }
        }
        else {
            // Log
            var myName = myWizard.PlayerNameBehavior.GetWizardName();
            Logger.Debug("{0} (ID {1}) has received a friend request reply from character ID {2}. They have denied.",
                Logger.Args(myName, myWizard.CharId, buddyCharID));

            // Inform the game client that the friend request has been denied.
            clientMsg = new GAME_5_PROTOCOL.MSG_BUDDYREQUESTDENY {
                ListOwnerGID = message.RequesterCharId,
                EntryGID = message.RecipientCharId
            };
        }

        SendToSocket(clientMsg);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_BUDDYREQUESTDROP))]
    private void ReceiveBuddyDrop(GAME_5_PROTOCOL.MSG_BUDDYREQUESTDROP message) {
        // A player wants to remove a friend from their list. Start by removing the friend from ourselves first.
        var wizard = GetActiveWizard();
        // Buddy-list entries are keyed by saved character ID.
        var buddyCharID = message.EntryGID;

        if (!wizard.RemoveFriend(buddyCharID)) {
            Logger.Error("{0} tried to remove character ID {1} as a friend, but they are not friends.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), buddyCharID));

            return;
        }

        // Check if the friend is online. If they are, we need to forward the drop to them. Otherwise,
        // we can just remove them from the database.
        if (TryGetOnlinePlayer(buddyCharID, out var onlinePlayer)) {
            var fwdMsg = new CHARACTER_103_PROTOCOL.MSG_BUDDYDROPFWD {
                RequesterCharId = wizard.CharId,
                RecipientCharId = buddyCharID
            };
            Context.ActorSelection(onlinePlayer.ActorPath).Tell(fwdMsg);
        }
        else {
            var offlineWizard = WizardCollection.GetCharacter(buddyCharID);
            offlineWizard?.RemoveFriend(wizard.CharId);
        }
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_BUDDYDROPFWD))]
    private void ReceiveBuddyDropFwd(CHARACTER_103_PROTOCOL.MSG_BUDDYDROPFWD message) {
        // A player has removed us as a friend. We need to remove them from our friends list.
        var wizard = GetActiveWizard();
        // Remove the requester, not the recipient of this forwarded message.
        var buddyCharID = message.RequesterCharId;

        if (!wizard.RemoveFriend(buddyCharID)) {
            Logger.Error("{0} tried to remove character ID {1} as a friend, but they are not friends.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), buddyCharID));

            return;
        }

        // Inform the client that the friend has been removed.
        var clientMsg = new GAME_5_PROTOCOL.MSG_BUDDYDROP {
            ListOwnerGID = wizard.GameObjectID,
            EntryGID = buddyCharID
        };
        SendToSocket(clientMsg);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_GOTOPLAYER))]
    private void ReceiveGoToPlayer(GAME_5_PROTOCOL.MSG_GOTOPLAYER message) {
        var targetID = message.TargetCharacterID;

        // CLASSIC: only to a friend, not out of a duel, not into a minigame (security audit 2026-10-04).
        var me = GetActiveWizard();
        me.FriendsBehavior.TryGetRelationship(targetID, out var goToRelationship);
        var targetInMinigame = TryGetOnlinePlayer(targetID, out var goToTarget)
            && Minigames.MinigameConfig.IsMinigameZone(goToTarget.CurrentZone ?? "");
        var goToRefusal = FriendRules.CheckGoTo(me.CharId, targetID, goToRelationship, me.IsInDuel, targetInMinigame);
        if (goToRefusal != GoToPlayerRefusal.None) {
            Logger.Information("{0} teleport to character {1} refused: {2}.",
                Logger.Args(me.CharId, targetID, goToRefusal.ToString()));
            if (goToRefusal == GoToPlayerRefusal.InDuel) {
                InformGameClient(Imlight.Classic.Security.VoluntaryTeleport.Message(Imlight.Classic.Security.TeleportRefusal.InDuel));
            }
            SendToSocket(new GAME_5_PROTOCOL.MSG_GOTOPLAYERRESP {
                Error = 1
            });

            return;
        }

        // Check if the target is online.
        if (!TryGetOnlinePlayer(targetID, out var onlinePlayer)) {
            Logger.Debug("Player {0} tried to teleport to character ID {1}, but the character is not online.",
                Logger.Args(GetActiveWizard().PlayerNameBehavior.GetWizardName(), targetID));

            SendToSocket(new GAME_5_PROTOCOL.MSG_GOTOPLAYERRESP {
                Error = 1
            });

            return;
        }

        // Query for the target's Wizard so that we may get their X/Y/Z coordinates. CLASSIC: the answer comes back as a
        // message (GoToPlayerAnswer) instead of blocking this actor and a pool thread on .Result.
        var zone = onlinePlayer.CurrentZone;

        // CLASSIC: a friend in a dungeon is reached in their instance (a sigil group's run or the dungeon's owner), and
        // not when it already holds four wizards (2009: "Your friend is in a full instance"; Classic.GroupInstances).
        var instanceOwner = ClassicRuntime.IsActive ? onlinePlayer.InstanceOwnerId : 0;
        if (instanceOwner != 0 && GroupInstances.IsFull(GroupInstances.CountIn(OnlinePlayerCollection.GetOnlinePlayers(),
                p => p.CurrentZone, p => p.InstanceOwnerId, zone, instanceOwner), onlinePlayer.ZoneHardLimit)) {
            InformGameClient(GroupInstances.FullInstanceMessage, true);

            return;
        }

        Context.ActorSelection(onlinePlayer.ActorPath)
            .Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(
                message: new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD(),
                timeout: TimeSpan.FromSeconds(QUERY_TELEPORT_WIZARD_TIMEOUT_IN_SECONDS)
            )
            .PipeTo(Self, success: rsp => new GoToPlayerAnswer(targetID, zone, rsp, InstanceOwner: instanceOwner),
                failure: ex => new GoToPlayerAnswer(targetID, zone, null, ex, instanceOwner));
    }

    /// <summary>CLASSIC: told to a wizard whose teleport-to-friend got no answer from the friend's session.</summary>
    internal const string FriendNotAvailableMessage = "Your friend is not available.";

    /// <summary>CLASSIC: the target's answer to a teleport-to-friend question.</summary>
    internal sealed record GoToPlayerAnswer(ulong TargetId, string Zone, CHARACTER_103_PROTOCOL.MSG_CHARACTER Answer,
                                            Exception Error = null, ulong InstanceOwner = 0);

    [MessageHandler(typeof(GoToPlayerAnswer))]
    private void ReceiveGoToPlayerAnswer(GoToPlayerAnswer answer) {
        if (answer.Error is not null) {
            // The blocking version let the timeout escape the handler; log it instead. The friend's session is gone or
            // stuck (rig-pg-ms 2026-10-04: 13 silent timeouts against a partner disposed 6 s earlier): say so.
            Logger.Warning("Failed to query the target wizard for teleportation: {0}", Logger.Args(answer.Error.Message));
            InformGameClient(FriendNotAvailableMessage);

            return;
        }

        var queryResult = answer.Answer;
        if (queryResult is not null) {
            // CLASSIC: [Classic] TeleportToFriendAnywhere (owner ruling 2026-10-01, on): unlocked worlds only decide the
            // world list. Off, a friend in a world this wizard has not unlocked cannot be reached.
            if (ClassicRuntime.IsActive && !Classic.ClassicSettings.TeleportToFriendAnywhere
                    && ClassicRuntime.Rules.HubKeyFor(answer.Zone) is { } hubKey
                    && !ClassicGate.AllowsWorldUnlock(hubKey, new WizardProgress(GetActiveWizard()), GetActiveWizard().CharId,
                        InformGameClient)) {
                return;
            }

            var coordinates = Util.GetCompactStringFromVector((Vector4) queryResult.Wizard.Location);

            // CLASSIC: into the friend's instance, never a public copy, and refused if it filled up meanwhile.
            var inInstance = answer.InstanceOwner != 0;
            Teleport(
                destinationZone: answer.Zone,
                destinationLocation: coordinates,
                doTeleportEffects: true,
                makePrivate: inInstance,
                ownerCharId: inInstance ? answer.InstanceOwner : answer.TargetId,
                refuseWhenFull: inInstance
            );
        } else {
            Logger.Warning("Failed to query the target wizard for teleportation.");
            InformGameClient(FriendNotAvailableMessage);
        }
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_IGNOREADD))]
    private void ReceiveIgnoreAdd(GAME_5_PROTOCOL.MSG_IGNOREADD message) {
        // A player wants to ignore another player.
        var wizard = GetActiveWizard();
        var targetCharID = message.CharacterGID;

        if (targetCharID == 0 || targetCharID == wizard.CharId) {
            return;
        }

        // CLASSIC: the ignore list is capped (every entry is a database row; security audit 2026-10-04).
        if (!FriendRules.MayIgnoreAnother(wizard.FriendsBehavior.GetIgnoredCharacterIds(wizard.CharId).Count)) { // CLASSIC: this wizard's ignores
            InformGameClient($"Your ignore list is full ({FriendRules.MaxIgnored}).");

            return;
        }

        if (!wizard.IgnorePlayer(targetCharID)) {
            Logger.Error("{0} tried to ignore character ID {1}, but they are already ignored.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), targetCharID));

            return;
        }

        // Send the updated ignore list as a single-entry confirmation.
        // The client expects MSG_IGNORELIST with Add=1 to confirm individual additions.
        SendIgnoreListConfirmation(wizard, add: true);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_IGNOREDROP))]
    private void ReceiveIgnoreDrop(GAME_5_PROTOCOL.MSG_IGNOREDROP message) {
        // A player wants to unignore another player.
        var wizard = GetActiveWizard();
        var targetCharID = message.CharacterGID;

        if (!wizard.UnignorePlayer(targetCharID)) {
            Logger.Error("{0} tried to unignore character ID {1}, but they are not ignored.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), targetCharID));

            return;
        }

        // Send the full updated ignore list so the client refreshes.
        SendIgnoreListConfirmation(wizard, add: false);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_IGNORELIST))]
    private void ReceiveIgnoreList(GAME_5_PROTOCOL.MSG_IGNORELIST message) {
        // A player is requesting a list of all the players they have ignored.
        var wizard = GetActiveWizard();
        SendIgnoreListConfirmation(wizard, add: false);
    }

    /// Send the serialized ignore list to the client.
    /// <param name="add">When true, sends as a single-entry confirmation (Add=1);
    /// when false, sends the full list (Add=0).</param>
    private void SendIgnoreListConfirmation(Wizard wizard, bool add) {
        var ignoredList = wizard.FriendsBehavior.GetIgnoredPlayers(wizard.CharId);

        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.None
        );

        if (!serializer.Serialize(ignoredList, 1, out var listBytes)) {
            Logger.Error("Player {0} requested their ignore list, but the serialization failed.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName()));

            return;
        }

        var msg = new GAME_5_PROTOCOL.MSG_IGNORELIST {
            ListOwnerGID = wizard.GameObjectID,
            ListData = listBytes,
            Add = add ? (byte)1 : (byte)0
        };
        SendToSocket(msg);
    }

    // CLASSIC: True Friend codes (TrueFriendCodes has the rules and their evidence). The client asks for a code with
    // MSG_REQUESTCHATCODE and shows MSG_SENDCHATCODE's Code; the friend enters it with MSG_USECHATCODE, and a
    // MSG_SENDCHATCODE naming the creator in UseSuccess tells the client "You are now True Friends with ...".
    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTCHATCODE))]
    private void ReceiveRequestChatCode(GAME_5_PROTOCOL.MSG_REQUESTCHATCODE message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        var name = wizard.PlayerNameBehavior.GetWizardName();
        var (code, error) = TrueFriendCodes.Create(TrueFriendCodeCollection.Instance, wizard.CharId, name, DateTimeOffset.UtcNow);
        if (code is null) {
            Logger.Warning("{0} could not get a True Friend code: {1}.", Logger.Args(name, error));
            SendChatCode(wizard, "", error, 0, "");

            return;
        }

        Logger.Information("{0} (character {1}) made a True Friend code.", Logger.Args(name, wizard.CharId));
        SendChatCode(wizard, code.Code, TrueFriendCodeError.None, 0, name);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_USECHATCODE))]
    private void ReceiveUseChatCode(GAME_5_PROTOCOL.MSG_USECHATCODE message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        var name = wizard.PlayerNameBehavior.GetWizardName();
        string typed = message.Code;
        var (code, error) = TrueFriendCodes.Use(TrueFriendCodeCollection.Instance, wizard.CharId, typed, DateTimeOffset.UtcNow,
            creatorId => WizardCollection.GetCharacter(creatorId) is not null,
            creatorId => wizard.FriendsBehavior.TryGetRelationship(creatorId, out var relationship)
                         && relationship is { IsBrokenUp: false, Blocked: false });
        if (code is null) {
            Logger.Information("{0} entered a True Friend code that was not accepted: {1}.", Logger.Args(name, error));
            SendChatCode(wizard, TrueFriendCodes.Normalize(typed), error, 0, "");

            return;
        }

        MarkTrueFriends(wizard, code.CreatorCharId);
        BuddyRelationshipCollection.SetTrueFriends(wizard.CharId, code.CreatorCharId);
        var creator = WizardCollection.GetCharacter(code.CreatorCharId);
        var creatorName = creator?.PlayerNameBehavior.GetWizardName() ?? code.CreatorName;
        Logger.Information("{0} and {1} are now True Friends.", Logger.Args(name, creatorName));

        SendChatCode(wizard, code.Code, TrueFriendCodeError.None, code.CreatorCharId, creatorName);
        if (creator is not null) {
            ResendBuddyEntry(wizard, creator);
        }

        if (TryGetOnlinePlayer(code.CreatorCharId, out var onlineCreator)) {
            Context.ActorSelection(onlineCreator.ActorPath).Tell(new CHARACTER_103_PROTOCOL.MSG_TRUEFRIENDFWD {
                CreatorCharId = code.CreatorCharId,
                UserCharId = wizard.CharId,
                Code = code.Code,
            });
        }
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_TRUEFRIENDFWD))]
    private void ReceiveTrueFriendFwd(CHARACTER_103_PROTOCOL.MSG_TRUEFRIENDFWD message) {
        // A friend used this wizard's code: the creator becomes their True Friend too and is told so.
        var wizard = GetActiveWizard();
        if (wizard is null || wizard.CharId != message.CreatorCharId) {
            return;
        }

        MarkTrueFriends(wizard, message.UserCharId);
        var user = WizardCollection.GetCharacter(message.UserCharId);
        if (user is null) {
            return;
        }

        SendChatCode(wizard, message.Code, TrueFriendCodeError.None, message.UserCharId, user.PlayerNameBehavior.GetWizardName());
        ResendBuddyEntry(wizard, user);
    }

    private static void MarkTrueFriends(Wizard wizard, ulong friendCharId) {
        if (wizard.FriendsBehavior.TryGetRelationship(friendCharId, out var relationship) && relationship is not null) {
            relationship.AddedViaTrueFriend = true;
        }
    }

    private void ResendBuddyEntry(Wizard owner, Wizard friend) {
        if (owner.FriendsBehavior.TryGetRelationship(friend.CharId, out var relationship) && relationship is not null) {
            SendBuddyEntry(friend, relationship, owner);
            SendBuddyListEnd(owner);
        }
    }

    private void SendChatCode(Wizard wizard, string code, TrueFriendCodeError error, ulong trueFriendCharId, string name)
        => SendToSocket(new GAME_5_PROTOCOL.MSG_SENDCHATCODE {
            ListOwnerGID = wizard.GameObjectID,
            Code = code ?? "",
            Error = TrueFriendCodes.ErrorCode(error),
            UseSuccess = trueFriendCharId,
            CreatorName = name ?? "",
        });

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT))]
    private void ReceiveClientDisconnect()
        => InformBuddiesOfStatusChange(false);

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT))]
    private void ReceiveQueryLogout(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT message)
        => InformBuddiesOfStatusChange(false);

    private void SendBuddyEntry(Wizard buddy, Relationship relationship, Wizard owner) {
        // Check if this buddy is online.
        var isOnline = TryGetOnlinePlayer(buddy.CharId, out var onlinePlayer);
        var buddyHexName = buddy.PlayerNameBehavior.GetWizardNameAsByteHexString();
        var buddyByteName = DataManipulation.SpacedHexStringToBytes(buddyHexName);

        // Log
        var ownerName = GetActiveWizard().PlayerNameBehavior.GetWizardName();
        var statusMessage = isOnline ? "online" : "offline";
        var buddyWizardName = buddy.PlayerNameBehavior.GetWizardName();
        Logger.Debug("{0} has a friend named {1} (Hex: {2}) (ID: {3}) who is {4}.",
            Logger.Args(ownerName, buddyWizardName, buddyHexName, buddy.CharId, statusMessage));

        var buddyMsg = new GAME_5_PROTOCOL.MSG_BUDDYENTRY {
            ListOwnerGID = owner.GameObjectID,
            EntryGID = buddy.CharId,
            GameObjectID = buddy.GameObjectID,
            Name = buddyByteName,
            Status = isOnline ? ONLINE_STATUS_CODE : OFFLINE_STATUS_CODE,
            FriendInfo = 5702144,                                     // TODO: What is this?
            PasswordChat = (byte) (relationship.AddedViaTrueFriend ? 1 : 0), // CLASSIC: secure (True Friend) chat, as MSG_REQUESTCHATCODE calls it.
            Permissions = (uint) buddy.Account.GetAccountFlags(),
            ZoneName = onlinePlayer?.CurrentZoneDisplayName ?? string.Empty,
            RealmName = onlinePlayer?.CurrentRealm ?? string.Empty,
            Locale = 0,
            FriendDate = relationship.RelationshipEpochInSeconds,
            FriendStatusDate = relationship.RelationshipEpochInSeconds,
            PreviousName = string.Empty                               // TODO: Implement this
        };
        SendToSocket(buddyMsg);
    }

    private void SendBuddyListEnd(Wizard wizard) {
        var completeMsg = new GAME_5_PROTOCOL.MSG_BUDDYLISTCOMPLETE {
            ListOwnerGID = wizard.GameObjectID
        };
        SendToSocket(completeMsg);
    }

    /// <summary>How long after a session goes away a wizard still not back online counts as offline to friends.</summary>
    internal static readonly TimeSpan OfflineGrace = TimeSpan.FromSeconds(10);

    // CLASSIC: friends heard "offline" only from the client's own logout or disconnect packet, so a wizard whose game
    // crashed, whose network dropped, or whose session the server closed (a restart) stayed "online" on every friend's
    // list until the friend logged out (rig-mp fo1). A zone change also closes the session, so the check waits
    // OfflineGrace and tells friends only if the wizard has not come back online by then.
    protected override void OnPreDispose() {
        try {
            // Never a blocking ask while the session is going away: the wizard this session played, if any.
            var charId = Classic.ActiveWizardDirectory.TryGet(SessionActor?.ActorRef, out var wizard, out _) ? wizard?.CharId ?? 0 : 0;
            var system = Context.System;
            if (charId != 0) {
                _ = System.Threading.Tasks.Task.Delay(OfflineGrace).ContinueWith(_ => TellFriendsIfStillOffline(system, charId),
                    System.Threading.Tasks.TaskScheduler.Default);
            }
        }
        catch (Exception ex) {
            Logger.Warning("Could not schedule the offline notice for friends: {0}", Logger.Args(ex.Message));
        }

        base.OnPreDispose();
    }

    private static void TellFriendsIfStillOffline(ActorSystem system, ulong charId) {
        try {
            if (OnlinePlayerCollection.GetOnlinePlayer(charId) is not null) {
                return; // back (a zone change) or never gone
            }

            foreach (var buddy in BuddyRelationshipCollection.GetBuddiesForWizard(charId).Where(buddy => buddy != null)) {
                if (OnlinePlayerCollection.GetOnlinePlayer(buddy.CharId) is { ActorPath: { Length: > 0 } path } online) {
                    system.ActorSelection(path).Tell(new GAME_5_PROTOCOL.MSG_BUDDYSTATUSUPDATE {
                        ListOwnerGID = buddy.GameObjectID,
                        EntryGID = charId,
                        Status = OFFLINE_STATUS_CODE,
                        ZoneName = online.CurrentZoneDisplayName,
                        RealmName = online.CurrentRealm,
                    });
                }
            }
        }
        catch (Exception ex) {
            Logger.Warning("Could not tell friends that {0} went offline: {1}", Logger.Args(charId, ex.Message));
        }
    }

    private void InformBuddiesOfStatusChange(bool isOnline) {
        var ownerWizard = GetActiveWizard();
        var charID = ownerWizard.CharId;

        // Inform all buddies of the status change.
        var buddies = BuddyRelationshipCollection.GetBuddiesForWizard(charID);
        foreach (var buddy in buddies.Where(buddy => buddy != null)) {
            if (TryGetOnlinePlayer(buddy.CharId, out var onlinePlayer)) {
                var buddyStatusMsg = new GAME_5_PROTOCOL.MSG_BUDDYSTATUSUPDATE {
                    ListOwnerGID = buddy.GameObjectID,
                    EntryGID = charID,
                    Status = isOnline ? ONLINE_STATUS_CODE : OFFLINE_STATUS_CODE,
                    ZoneName = onlinePlayer.CurrentZoneDisplayName,
                    RealmName = onlinePlayer.CurrentRealm
                };
                var buddyActorPath = onlinePlayer.ActorPath;
                Context.ActorSelection(buddyActorPath).Tell(buddyStatusMsg);
            }
        }
    }

    private WizGameStats GetBuddyGameStats(Wizard databaseBuddy) {
        // Dragon database doesn't store any game stats because they follow a consistent
        // pattern that allows us to recreate them when they log in. Since "database buddy"
        // doesn't have any stats applied, this function must recreate them as if they were
        // logging in themselves.
        CharacterHelper.RecalculateGameStats(databaseBuddy);

        // The client type alternative 'GetClientTypeAlternative' doesn't return applied
        // stats, only base stats. Use 'GetCombatGameStats' to get the base stats + applied.
        return databaseBuddy.GameStats.GetCombatGameStats();
    }

}
