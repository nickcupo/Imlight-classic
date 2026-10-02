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
 * AMBIENT ENDPOINT
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the actor a zone, a duel, the friends code and chat treat as an
 * ambient wizard's "player actor" (where a real player has a session). It
 * answers MSG_QUERYACTIVEWIZARD itself, passes the few messages the
 * wizard acts on to its AmbientZone actor (zone transfer and add-player
 * answers, other players arriving, chat, friend requests, duel events,
 * duel-target answers) and drops everything else: the client messages
 * zones send to players (spawns, moves, wizbangs, NPC options) cost one
 * type test here.
 *
 * USAGE EXAMPLE:
 * var endpoint = Context.ActorOf(AmbientEndpoint.Props(wizard, Self), $"ambient-{wizard.CharId}");
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>A message for an ambient wizard, passed from its endpoint to its zone actor.</summary>
internal sealed record AmbientInbox(AmbientWizard Wizard, object Message, IActorRef Sender);

/// <summary>An ambient wizard's player actor (see the file header).</summary>
internal sealed class AmbientEndpoint : UntypedActor {

    private readonly AmbientWizard _wizard;
    private readonly IActorRef _group;

    public AmbientEndpoint(AmbientWizard wizard, IActorRef group) {
        _wizard = wizard;
        _group = group;
    }

    public static Props Props(AmbientWizard wizard, IActorRef group)
        => Akka.Actor.Props.Create(() => new AmbientEndpoint(wizard, group));

    protected override void OnReceive(object message) {
        switch (message) {
            case CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD:
                Sender.Tell(new CHARACTER_103_PROTOCOL.MSG_CHARACTER {
                    Wizard = _wizard.Wizard, WizardGameObject = _wizard.Wizard.GameObject,
                });
                break;

            case GAME_5_PROTOCOL.MSG_RADIALCHAT chat:
                AmbientChat.LearnPrefix((byte[]) chat.Message);
                _group.Tell(new AmbientInbox(_wizard, message, Sender));
                break;

            case ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP:
            case ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP:
            case ZONE_102_PROTOCOL.MSG_PLAYERADDEDTOZONE:
            case ZONE_102_PROTOCOL.MSG_QUERYNEARESTDUELTARGETRSP:
            case GAME_5_PROTOCOL.MSG_DIRECTEDCHAT:
            case CHARACTER_103_PROTOCOL.MSG_BUDDYREQUESTADDFWD:
            case CHARACTER_103_PROTOCOL.MSG_BUDDYREQUESTREPLYFWD:
            case CHARACTER_103_PROTOCOL.MSG_BUDDYDROPFWD:
            case GAME_5_PROTOCOL.MSG_BUDDYSTATUSUPDATE:
            case COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL:
            case COMBAT_106_PROTOCOL.MSG_COMBATWIN:
            case COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT:
            case COMBAT_106_PROTOCOL.MSG_COMBATDEATH:
            case AmbientSparringSeat:
                _group.Tell(new AmbientInbox(_wizard, message, Sender));
                break;

            default:
                // A client message meant for a real player's socket, or a request no ambient wizard answers.
                break;
        }
    }

}
