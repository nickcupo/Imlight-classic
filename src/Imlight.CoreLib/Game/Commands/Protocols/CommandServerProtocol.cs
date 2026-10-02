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
 * SERVER COMMANDS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: in-game server administration for the owner: broadcasts, safe
 * restarts and backups, the same actions as the admin dashboard.
 *
 * USAGE EXAMPLE:
 * .server restart 5 new holiday data
 * .server backup 2
 * .server cancel
 * .server broadcast Arena PvP night at 8!
 * .server status
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Globalization;
using Imlight.Classic.Admin;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Admin;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandServerProtocol : CommandProtocol {

    internal override string Group { get; set; } = "server";

    [Command("broadcast")]
    [Alias("say", "announce")]
    [AuthRequired(AuthLevel.Administrator)]
    private void BroadcastCommand([Remainder] string text) {
        var count = ServerAdmin.Broadcast(text);
        InformSenderClient($"Sent to {count} wizard(s).");
    }

    [Command("restart")]
    [AuthRequired(AuthLevel.Administrator)]
    private void RestartCommand([Remainder] string arguments) => Schedule(RestartKind.Restart, arguments);

    [Command("backup")]
    [AuthRequired(AuthLevel.Administrator)]
    private void BackupCommand([Remainder] string arguments) => Schedule(RestartKind.Backup, arguments);

    [Command("cancel")]
    [AuthRequired(AuthLevel.Administrator)]
    private void CancelCommand() {
        InformSenderClient(ServerAdmin.Cancel(Context.Account.Username)
            ? "The scheduled restart is cancelled."
            : "Nothing to cancel.");
    }

    [Command("status")]
    [AuthRequired(AuthLevel.Administrator)]
    private void StatusCommand() {
        var uptime = DateTime.UtcNow - ServerAdmin.StartedUtc;
        var restart = ServerAdmin.Status() is { } status
            ? $"{status.Kind} by {status.RequestedBy}: {status.State}, due {status.DeadlineUtc:HH:mm:ss} UTC"
            : "no restart scheduled";
        InformSenderClient($"Up {(int) uptime.TotalHours}h {uptime.Minutes}m. {ServerAdmin.OnlineWizards().Count} online, "
            + $"{ActiveDuels.Snapshot().Count} fight(s) in progress. {restart}.");
    }

    // "<minutes> [reason]"; minutes may be 0.
    private void Schedule(RestartKind kind, string arguments) {
        var parts = (arguments ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
                || minutes < 0 || minutes > 240) {
            InformSenderClient($"Usage: server {kind.ToString().ToLowerInvariant()} <minutes 0-240> [reason]");

            return;
        }

        var status = ServerAdmin.Schedule(kind, TimeSpan.FromMinutes(minutes), parts.Length > 1 ? parts[1] : null,
            Context.Account.Username);
        InformSenderClient($"{kind} scheduled for {status.DeadlineUtc:HH:mm:ss} UTC; it waits for fights until "
            + $"{status.HardCapUtc:HH:mm:ss} UTC at the latest. '.server cancel' stops it.");
    }

}
