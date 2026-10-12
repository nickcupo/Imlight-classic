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
 * AMBIENT GROUP SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-09): the client's own group requests, where they
 * concern ambient wizards (Classic/Ambient: AmbientGroups):
 *   - MSG_PARTYREQUESTINVITE (the Group button on a wizard's card or the
 *     friends list): to an ambient wizard, it is asked like a call by
 *     name and answers in its own time; a wizard already with another
 *     player, in a dungeon run or away is refused at once
 *     (MSG_PARTYREQUESTRESPONSE: 2 already grouped, 7 unavailable).
 *   - MSG_PARTYLEAVE (Leave Group): the player's companions all go.
 *   - MSG_REMOVEPARTYMEMBER (removing a member in the group window): that
 *     companion goes.
 *   - MSG_REQUESTDIRECTEDCHAT to the group's channel (the client's group
 *     chat): to the companions (ChatService leaves these alone).
 *   - MSG_BUDDYREQUESTLIST (the client asks for its lists after login and
 *     each zone change): the group window is sent again.
 * The client leaves the source fields of these requests empty; the
 * session's own wizard is the one asking. Invites to real players are not
 * handled here (there is no player-to-player grouping on this server yet).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal sealed class AmbientGroupService(SessionActor sessionActor) : MessageService(sessionActor) {

    /// <summary>The client's invite error codes (MSG_PARTYREQUESTRESPONSE), as upstream Imlight's group directory sends them.</summary>
    private const int AlreadyGrouped = 2;
    private const int Unavailable = 7;

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_PARTYREQUESTINVITE))]
    private void ReceivePartyRequestInvite(GAME_5_PROTOCOL.MSG_PARTYREQUESTINVITE message) {
        if (GetActiveWizard() is not { } me || !AmbientGroups.Settings.Enabled) {
            return;
        }

        var target = Candidates(message.TargetCharacterID, message.TargetGlobalID).FirstOrDefault(AmbientWizards.IsAmbientChar);
        if (target == 0) {
            return; // a real player: not an ambient wizard's business
        }

        if (AmbientGroups.Invite(me.CharId, target, out var alreadyGrouped)) {
            Logger.Information("{0} invited ambient wizard {1} to a group.", Logger.Args(me.CharId, target));
            return;
        }

        Logger.Information("{0} invited ambient wizard {1} to a group: {2}.", Logger.Args(me.CharId, target,
            alreadyGrouped ? "already with someone" : "not free"));
        if (AmbientGroups.Settings.Window && AmbientWizards.TryGet(target, out var wizard)) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_PARTYREQUESTRESPONSE {
                DestinationCharacterID = me.CharId, TargetCharacterID = target, TargetGlobalID = wizard.Wizard.GameObjectID,
                ErrorCode = alreadyGrouped ? AlreadyGrouped : Unavailable, PlayerNameBlob = AmbientChat.NameBytes(wizard.Wizard),
            });
        }
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_PARTYLEAVE))]
    private void ReceivePartyLeave(GAME_5_PROTOCOL.MSG_PARTYLEAVE message) {
        if (GetActiveWizard() is { } me && AmbientGroups.TryGet(me.CharId, out var group)) {
            Logger.Information("{0} left the group: the ambient companions go.", Logger.Args(me.CharId));
            group.Tell(new AmbientCompanionGroup.LeaveGroup());
        }
    }

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_REMOVEPARTYMEMBER))]
    private void ReceiveRemovePartyMember(WIZARD3_56_PROTOCOL.MSG_REMOVEPARTYMEMBER message) {
        if (GetActiveWizard() is not { } me || !AmbientGroups.TryGet(me.CharId, out var group)) {
            return;
        }

        var member = Candidates(message.PlayerGID, message.PlayerGID).FirstOrDefault(id => AmbientGroups.LeaderOf(id) == me.CharId);
        if (member != 0) {
            group.Tell(new AmbientCompanionGroup.RemoveMember(member));
        }
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDCHAT))]
    private void ReceiveDirectedChat(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDCHAT message) {
        if (GetActiveWizard() is not { } me || AmbientGroups.LeaderOfChannel(message.TargetID) != me.CharId
            || !AmbientGroups.TryGet(me.CharId, out var group) || GetActiveAccount()?.InfractionHistory?.IsCurrentlyMuted == true) {
            return;
        }

        string text = message.Message;
        if (Imlight.Classic.Security.ChatGuard.SanitizeText(text) is { } clean) {
            Logger.Information("[Group {0}] {1}: {2}", Logger.Args(message.TargetID, me.PlayerNameBehavior.GetWizardName(), clean));
            group.Tell(new AmbientCompanionGroup.GroupChat(clean));
        }
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_BUDDYREQUESTLIST))]
    private void ReceiveBuddyRequestList(GAME_5_PROTOCOL.MSG_BUDDYREQUESTLIST message) {
        if (GetActiveWizard() is { } me && AmbientGroups.TryGet(me.CharId, out var group)) {
            group.Tell(new AmbientCompanionGroup.Refresh());
        }
    }

    /// <summary>The character ids a request may mean: the character field, or either field read as an object id.</summary>
    private static IEnumerable<ulong> Candidates(ulong characterField, ulong objectField) {
        if (characterField != 0) {
            yield return characterField;
        }

        if (Wizard.TryGetCharacterId(objectField, out var fromObject) && fromObject != 0) {
            yield return fromObject;
        }

        if (Wizard.TryGetCharacterId(characterField, out var fromCharacter) && fromCharacter != 0) {
            yield return fromCharacter;
        }
    }

}
