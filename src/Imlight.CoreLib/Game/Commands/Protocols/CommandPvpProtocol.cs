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
 * OPEN PVP COMMANDS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: for every wizard in an open PvP circle (no special rights):
 * .pvp ready (start without waiting for the countdown once everyone is
 * ready), .pvp leave (step out, or leave the fight), .pvp status (the
 * circles in this zone).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System.Linq;
using Akka.Actor;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandPvpProtocol : CommandProtocol {

    internal override string Group { get; set; } = "pvp";

    [Command("ready")]
    private void ReadyCommand()
        => Context.SessionActor.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_PVPCOMMAND { Actor = Context.SessionActor, Leave = false });

    [Command("leave")]
    private void LeaveCommand()
        => Context.SessionActor.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_PVPCOMMAND { Actor = Context.SessionActor, Leave = true });

    [Command("status")]
    private void StatusCommand() {
        if (!ClassicPvp.Enabled) {
            InformSenderClient("Open PvP is off on this server.");

            return;
        }

        var circles = ClassicPvp.CirclesIn(Context.Character.Zone);
        InformSenderClient(circles.Count == 0
            ? $"No duel circle is open here. Walk into one of the {ClassicPvp.Config!.Circles.Length} circles in the Arena to start one."
            : string.Join("; ", circles.Select(c => $"{c.Tag}: {c.Phase}, {c.Side0} v {c.Side1}, {c.Ready} ready")));
    }

}
