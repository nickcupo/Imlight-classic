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
 * CHAT SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages in-game chat functionality, including message processing, 
 * command handling, and logging of player communications.
 * 
 * USAGE EXAMPLE:
 * Internal service handling chat-related messages within the game server's 
 * session management system.
 * 
 * NOTE:
 * - Implements chat command processing for authorized users
 * 
 * TODO:
 * - Review and enhance chat command authorization
 * - Improve message sanitization logic
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Commands;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class ChatService(SessionActor sessionActor) : MessageService(sessionActor) {

    // Always make sure this command prefix is within the bounds of the Regex.
    private const string CommandPrefix = ".";
    private const string MessageRegex = "[^a-zA-Z0-9\\p{P} ]";

    private Wizard _selectedCharacter;
    private Account _selectedAccount;

    private readonly IActorRef _dispatcherRef = CommandDispatcher.Instance;

    // CLASSIC: one rate limit for all of a session's chat lines, typed and quick (security audit 2026-10-04).
    private readonly Imlight.Classic.Security.TokenBucket _chatRate =
        new(Imlight.Classic.Security.ChatGuard.RateBurst, Imlight.Classic.Security.ChatGuard.RatePerSecond);
    private DateTimeOffset _lastRateNotice;

    /// <summary>CLASSIC: false (and one notice every few seconds) when the session is chatting too fast.</summary>
    private bool TakeChatToken() {
        var now = DateTimeOffset.UtcNow;
        if (_chatRate.TryTake(now)) {
            return true;
        }

        if (now - _lastRateNotice > TimeSpan.FromSeconds(5)) {
            _lastRateNotice = now;
            InformGameClient("You are sending messages too quickly.");
        }

        return false;
    }

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new ChatService(parentActor));

    // CLASSIC: see the QA hook in ReceiveRequestRadialChat.
    internal const string QaFaultPhrase = "qa-fault-session";
    private static readonly bool s_qaFaultHook = ConfigurationManager.Settings["Classic.QaFaultHook"].AsBool();

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTRADIALCHAT))]
    private void ReceiveRequestRadialChat(GAME_5_PROTOCOL.MSG_REQUESTRADIALCHAT message) {
        var charObj = GetActiveGameObject();
        var wizard = GetActiveWizard();
        var account = GetActiveAccount();

        if (account.InfractionHistory.IsCurrentlyMuted) {
            InformGameClient("You are currently muted.");

            return;
        }

        // CLASSIC: an empty or unusable line is dropped (it closed the session), and what is passed on is filtered,
        // cut to ChatGuard.MaxLength and rate-limited (security audit 2026-10-04).
        byte[] rawMessage = message.Message;
        var sanitized = Imlight.Classic.Security.ChatGuard.SanitizeRadial(rawMessage);
        if (sanitized is null) {
            return;
        }

        // Craft the wizard name.
        var byteName = wizard.PlayerNameBehavior.GetWizardNameAsByteHexString();
        var sourceName = DataManipulation.SpacedHexStringToBytes(byteName);

        var cleanedMessage = CleanMessageTrash(rawMessage);
        if (string.IsNullOrEmpty(cleanedMessage)) {
            return;
        }

        // CLASSIC: a QA hook for rigs only ([Classic] QaFaultHook, off unless a rig's ini sets it; never in the shipped
        // ini): this Say makes the handler throw, to check that a failing service saves the wizard, takes it out of
        // its zone and closes the session (multiplayer audit item D, SessionFaults).
        if (cleanedMessage.Trim() == QaFaultPhrase && s_qaFaultHook) {
            throw new InvalidOperationException("QA fault hook ([Classic] QaFaultHook): injected session service failure");
        }

        // Parse in-game chat commands. Do not broadcast it to the zone.
        if (cleanedMessage.StartsWith(CommandPrefix) && account.AuthLevel > AuthLevel.None) {
            SendChatCommand(cleanedMessage, charObj, wizard);

            return;
        }

        if (!TakeChatToken()) {
            return;
        }

        LogChatMessage(wizard.PlayerNameBehavior.GetWizardName(), cleanedMessage, wizard.Zone);
        SaveChatLog(cleanedMessage, charObj, wizard);

        // Broadcast the message to the zone, skipping players who have ignored the sender.
        var msg = new GAME_5_PROTOCOL.MSG_RADIALCHAT {
            Message = new ByteString(sanitized),
            SourceID = charObj.m_globalID,
            SourceName = sourceName,
            Filter = 2,
        };
        SendFilteredZoneMessage(msg, wizard.CharId, wizard.Zone);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTRADIALQUICKCHAT))]
    private void ReceiveRequestRadialQuickChat(GAME_5_PROTOCOL.MSG_REQUESTRADIALQUICKCHAT message) {
        var account = GetActiveAccount();
        if (account.InfractionHistory.IsCurrentlyMuted) {
            InformGameClient("You are currently muted.");

            return;
        }

        if (!TakeChatToken()) { // CLASSIC
            return;
        }

        var globalId = GetActiveGameObject().m_globalID;
        var character = GetActiveWizard();
        var byteName = character.PlayerNameBehavior.GetWizardNameAsByteHexString();
        var src = DataManipulation.SpacedHexStringToBytes(byteName);

        var msg = new GAME_5_PROTOCOL.MSG_RADIALQUICKCHAT() {
            MessageID = message.MessageID,
            SourceID = globalId,
            SourceName = src,
            Filter = 0,
        };
        SendFilteredZoneMessage(msg, character.CharId, character.Zone);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDQUICKCHAT))]
    private void ReceiveWhisperRadialQuickChat(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDQUICKCHAT message) {
        // A player is whispering to another player using the radial quick chat.
        var targetID = message.TargetID;

        // Check if the sender is muted.
        var account = GetActiveAccount();
        if (account.InfractionHistory.IsCurrentlyMuted) {
            InformGameClient("You are currently muted.");

            return;
        }

        // Check if the target has ignored the sender.
        var myWizard = GetActiveWizard();
        if (BuddyRelationshipCollection.HasBlocked(targetID, myWizard.CharId) || !TakeChatToken()) { // CLASSIC: rate
            return;
        }
        
        if (!TryGetOnlinePlayer(targetID, out var targetPlayer)) {
            // Inform the user of error if the target is offline.
            SendToSocket(new GAME_5_PROTOCOL.MSG_DIRECTEDCHATFAIL());

            return;
        }

        // Send the quick chat to the target player.
        var hexName = myWizard.PlayerNameBehavior.GetWizardNameAsByteHexString();
        var sourceName = DataManipulation.SpacedHexStringToBytes(hexName);
        var msg = new GAME_5_PROTOCOL.MSG_DIRECTEDQUICKCHAT {
            SourceName = sourceName,
            SourceID = myWizard.CharId,
            MessageID = message.MessageID,
            Filter = 0
        };

        var targetActorPath = targetPlayer.ActorPath;
        Context.ActorSelection(targetActorPath).Tell(msg);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDQUICKCHATEXT))]
    private void ReceiveWhisperRadialChatExt(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDQUICKCHATEXT message) {
        // A player is whispering to another player, directly.
        // This message uses UTF-8 encoding.
        var targetID = message.TargetID;

        // Check if the sender is muted.
        var account = GetActiveAccount();
        if (account.InfractionHistory.IsCurrentlyMuted) {
            InformGameClient("You are currently muted.");

            return;
        }

        // Check if the target has ignored the sender.
        var myWizard = GetActiveWizard();
        if (BuddyRelationshipCollection.HasBlocked(targetID, myWizard.CharId) || !TakeChatToken()) { // CLASSIC: rate
            return;
        }

        if (!TryGetOnlinePlayer(targetID, out var targetPlayer)) {
            // Inform the user of error if the target is offline.
            SendToSocket(new GAME_5_PROTOCOL.MSG_DIRECTEDCHATFAIL());

            return;
        }

        // Send the directed chat to the target player.
        var hexName = myWizard.PlayerNameBehavior.GetWizardNameAsByteHexString();
        var sourceName = DataManipulation.SpacedHexStringToBytes(hexName);
        byte[] quickExt = message.Message;
        if (!Imlight.Classic.Security.ChatGuard.AcceptsQuickChatExt(quickExt)) { // CLASSIC: length only
            return;
        }

        var msg = new GAME_5_PROTOCOL.MSG_DIRECTEDQUICKCHATEXT {
            SourceName = sourceName,
            SourceID = myWizard.CharId,
            Message = message.Message,
            Filter = 0
        };

        var targetActorPath = targetPlayer.ActorPath;
        Context.ActorSelection(targetActorPath).Tell(msg);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDCHAT))]
    private void ReceiveWhisperDirectChat(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDCHAT message) {
        // A player is whispering to another player, directly.
        // This message uses a wide-character string (16 bits per character).
        var targetID = message.TargetID;

        // Check if the sender is muted.
        var account = GetActiveAccount();
        if (account.InfractionHistory.IsCurrentlyMuted) {
            InformGameClient("You are currently muted.");

            return;
        }

        // Check if the target has ignored the sender.
        var myWizard = GetActiveWizard();
        if (BuddyRelationshipCollection.HasBlocked(targetID, myWizard.CharId) || !TakeChatToken()) { // CLASSIC: rate
            return;
        }

        if (!TryGetOnlinePlayer(targetID, out var targetPlayer)) {
            // Inform the user of error if the target is offline.
            SendToSocket(new GAME_5_PROTOCOL.MSG_DIRECTEDCHATFAIL());

            return;
        }

        // Send the directed chat to the target player.
        var hexName = myWizard.PlayerNameBehavior.GetWizardNameAsByteHexString();
        var sourceName = DataManipulation.SpacedHexStringToBytes(hexName);
        // CLASSIC: filtered and cut like typed chat; an empty whisper is dropped (security audit 2026-10-04).
        string whisper = message.Message;
        var cleanWhisper = Imlight.Classic.Security.ChatGuard.SanitizeText(whisper);
        if (cleanWhisper is null) {
            return;
        }

        var msg = new GAME_5_PROTOCOL.MSG_DIRECTEDCHAT {
            SourceName = sourceName,
            SourceID = myWizard.CharId,
            Message = cleanWhisper,
            Filter = 0
        };

        var targetActorPath = targetPlayer.ActorPath;
        Context.ActorSelection(targetActorPath).Tell(msg);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_BUDDYSTATS))]
    private void ReceivePlayerSelect(GAME_5_PROTOCOL.MSG_BUDDYSTATS message) {
        // Regular players have no reason to be able to select other players
        // for command context.
        var localAccount = GetActiveAccount();
        if (localAccount.AuthLevel < AuthLevel.HallMonitor) {
            return;
        }

        if (message.BuddyID == 0) {
            _selectedCharacter = null;
            _selectedAccount = null;

            return;
        }
        
        if (!Wizard.TryGetCharacterId(message.BuddyID, out var characterId)) {
            return;
        }

        var persistentCharacter = WizardCollection.GetCharacter(characterId);
        if (persistentCharacter is null) {
            return;
        }

        var selectedAccount = AccountCollection.GetAccount(persistentCharacter.AccountId);
        if (selectedAccount is null) {
            return;
        }

        // Cache for our next command.
        _selectedCharacter = persistentCharacter;
        _selectedAccount = selectedAccount;
    }

    private static string CleanMessageTrash(byte[] message) {
        // CLASSIC: an empty line (or only the prefix byte) has nothing to clean; [1..] on it threw.
        if (message is null || message.Length == 0) {
            return null;
        }

        // Remove the first byte, unless it's the command prefix.
        if (message[0] != (byte) '.') {
            message = message[1..];
        }

        // Define a regular expression pattern to keep alphanumeric characters and punctuation
        string validCharactersPattern = MessageRegex; // \p{P} matches any punctuation character
        var cleanedMessage = Regex.Replace(new ByteString(message).ToString() ?? "", validCharactersPattern, "").Trim();

        return cleanedMessage;
    }

    private static void LogChatMessage(string name, string message, string zoneName) 
        => Logger.Information("[{0}] {1}: {2}", Logger.Args(zoneName, name, message));

    private static void SaveChatLog(string message, CoreObject charObj, Wizard character) 
        => ChatLogCollection.QueueChatLog(new ChatLog() { // CLASSIC: off the speaker's thread
            TimeStamp = DateTime.UtcNow,
            ZoneName = character.Zone,
            CharacterId = character.CharId,
            AccountId = character.AccountId,
            Message = message,
        });

    /// <summary>
    /// Sends a zone chat message to all online players in the zone, skipping those
    /// who have ignored the sender.
    /// </summary>
    private void SendFilteredZoneMessage(IMessage msg, ulong senderCharId, string zoneName) {
        var blockedBy = BuddyRelationshipCollection.GetCharactersWhoBlocked(senderCharId);
        var playersInZone = OnlinePlayerCollection.GetPlayersInZone(zoneName);
        var senderInstance = OnlinePlayerCollection.GetOnlinePlayer(senderCharId)?.InstanceOwnerId ?? 0; // CLASSIC

        foreach (var player in ChatAudience(playersInZone, senderCharId, senderInstance, blockedBy)) {
            Context.ActorSelection(player.ActorPath).Tell(msg);
        }
    }

    /// <summary>
    /// The players a zone chat line reaches: everyone in the sender's zone, except the sender (they see their own
    /// message client-side) and those who have blocked the sender. CLASSIC: only in the sender's instance, too; an
    /// instance (a dorm, a sigil run, a quest instance) shares its zone name with every other copy of that zone, so
    /// a Say in one dungeon run used to reach the wizards in every other run of it.
    /// </summary>
    internal static IEnumerable<OnlinePlayer> ChatAudience(IEnumerable<OnlinePlayer> playersInZone, ulong senderCharId,
                                                          ulong senderInstance, ICollection<ulong> blockedBy)
        => playersInZone.Where(player => player.CharacterId != senderCharId
                                         && player.InstanceOwnerId == senderInstance
                                         && !blockedBy.Contains(player.CharacterId));

    private void SendChatCommand(string input, CoreObject charObj, Wizard character) {
        var account = GetActiveAccount();

        _dispatcherRef.Tell(new SERVER_100_PROTOCOL.MSG_COMMAND() {
            CommandText = input[1..], // Remove the command prefix
            ActorRef = SessionActor.ActorRef,
            CoreObject = charObj,
            Wizard = character,
            Account = account,
            ZoneActor = SessionActor.GetZoneActor(),
            ServerActor = SessionActor.ServerRef,
            SelectedWizard = _selectedCharacter,
            SelectedAccount = _selectedAccount
        });
    }

}