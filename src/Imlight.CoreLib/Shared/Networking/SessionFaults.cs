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
 * SESSION FAULTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (multiplayer audit item D): what a session does when one of its services fails. The first failure is logged
 * once, in full, with the session, address, wizard, service and message; the session then closes the normal way
 * (SessionActor.Dispose: every service's OnPreDispose saves the wizard and takes it out of its zone, then the socket
 * closes and the client sees a disconnect). Failures after that, while it closes, are one Warning line each.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

#nullable enable

using System;
using Akka.Actor;
using Imlight.Common;

namespace Imlight.CoreLib.Shared.Networking;

/// <summary>CLASSIC: tells a session that one of its services failed; the session closes (SessionActor).</summary>
internal sealed record ServiceFaulted(string Service, string Message);

internal static class SessionFaults {

    /// <summary>
    /// Logs a service's failure (in full the first time for this session) and asks the session to close. True when
    /// this was the session's first failure.
    /// </summary>
    internal static bool Report(SessionActor session, string serviceName, object? message, Exception exception) {
        var messageName = message?.GetType().Name ?? "(none)";
        var first = session.MarkFaulted();
        if (first) {
            var wizard = Classic.ActiveWizardDirectory.TryGet(session.ActorRef, out var active, out _) && active is not null
                ? $"{active.PlayerNameBehavior?.GetWizardName()} (char {active.CharId})"
                : "(no wizard)";
            Logger.Error("SessionActor {Sid} ({Ip}, {Wizard}): service {Service} failed handling {Message}; saving and "
                         + "closing the session. {Exception}",
                Logger.Args(session.SessionID, session.RemoteIp, wizard, serviceName, messageName, exception));
        }
        else {
            Logger.Warning("SessionActor {Sid}: service {Service} failed again handling {Message} while the session closes: {Error}",
                Logger.Args(session.SessionID, serviceName, messageName, exception.GetBaseException().Message));
        }

        session.ActorRef?.Tell(new ServiceFaulted(serviceName, messageName));

        return first;
    }

}
