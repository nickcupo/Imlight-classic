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
 * CLASSIC CINEMATICS
 * ========================================================================
 *
 * PURPOSE:
 * Receives the event a client posts from a cinematic's
 * ServerSyncCinematicAction (MSG_POSTZONEEVENTFROMCLIENT) and ends the
 * zone timer that waits for it.
 *
 * USAGE EXAMPLE:
 * Registered in GameServiceFactory; the client sends the message itself.
 *
 * NOTE:
 * The event only ends timers whose condition is CLIENTEVENT.<event> in the
 * wizard's own zone; it is never posted to the zone's triggers, so a client
 * cannot fire a trigger by naming its event.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Cinematics;
using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Game.Services;

internal sealed class ClassicCinematicService(SessionActor sessionActor) : MessageService(sessionActor) { // CLASSIC

    private static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new ClassicCinematicService(parentActor));

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_POSTZONEEVENTFROMCLIENT))]
    private void ReceivePostZoneEventFromClient(GAME_5_PROTOCOL.MSG_POSTZONEEVENTFROMCLIENT message) {
        if (!ClassicQuestEngine.IsActive) {
            return;
        }

        var zoneActor = SessionActor.GetZoneActor();
        if (zoneActor is null) {
            return;
        }

        var ended = ClassicCinematics.Timers.ClientEvent(ClassicCinematics.ZoneKey(zoneActor), message.EventName);
        Logger.Debug("Client event {0} ended {1} cinematic timer(s).", Logger.Args((string) message.EventName, ended));
    }

}
