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
 * .minions CHAT COMMAND
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: any player types ".minions" in chat to get a one-time code for
 * the Minion Helper app (Classic/MinionHelper). Not a QA command.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.MinionHelper;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandMinionHelperProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("minions")]
    [Alias("minionhelper")]
    private void MinionsCommand() {
        if (!EnhancedGameplaySettings.Enabled || MinionHelperListener.ConfiguredPort == 0) {
            InformSenderClient("Minion control is off on this server.");
            return;
        }

        var account = Context.Account;
        if (account is null || account.AccountId == 0) return;
        var code = MinionHelperPairing.Shared.IssueCode(account.AccountId);
        InformSenderClient($"Minion Helper code: {code[..3]} {code[3..]} (good for 5 minutes). " +
                           "Type it into the Minion Helper window on your Mac.");
    }
}
