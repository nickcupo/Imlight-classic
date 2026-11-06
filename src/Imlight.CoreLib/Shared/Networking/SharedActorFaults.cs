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
 * SHARED ACTOR FAULTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (crash review 2026-10-28): what an actor that many players share (GameWorld, an InstanceContainer, a Zone,
 * a zone supervisor) does when one of its handlers throws. It used to reach the parent's supervisor:
 * - GameWorld's parent (Server.SupervisorStrategy) STOPS it: every zone of the server, and every later zone change
 *   went to dead letters until the process restarted.
 * - A Zone, a zone supervisor or an InstanceContainer RESTARTED: its children stopped (supervisors, entities, the
 *   container's zones with players inside), and nothing loads a restarted zone or supervisor again (it waits for the
 *   MSG_ZONELOADRESULTS it already had), so that zone stayed broken until the server restarted.
 * Now the failure is logged with the actor and the message and the actor keeps its state and children; one bad message
 * costs only itself. A zone transfer that failed is answered with an error, so the session asking for it is not left
 * waiting (SessionActor.AskServer has no timeout).
 *
 * Created by: Nick with Claude Code
 * Version: KALI 1.0
 * Last Updated: 10/28/2026
 */

#nullable enable

using System;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Shared.Networking;

internal static class SharedActorFaults {

    /// <summary>
    /// Logs a shared actor's handler failure and answers a failed zone transfer. Always true: the actor carries on
    /// (ReceiveProtocolDispatcher.OnHandlerFault).
    /// </summary>
    internal static bool Handle(string actor, object? message, Exception exception, IActorRef? sender) {
        Logger.Error("{Actor} failed handling {Message}; it keeps running. {Exception}",
            Logger.Args(actor, message?.GetType().Name ?? "(none)", exception));

        if (message is ZONE_102_PROTOCOL.MSG_ZONETRANSFER && sender is not null && !sender.IsNobody()) {
            sender.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP { ErrorCode = 1 });
        }

        return true;
    }

}
