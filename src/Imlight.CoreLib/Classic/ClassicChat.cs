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
 * CLASSIC CHAT
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: a plain chat line from the server to one player. The r806919
 * client turns every non-modal MSG_SERVERMESSAGE into a "!" alert icon on the
 * right of the screen (WizardGUIManager::HandleServerMessage, 0x140dd30d0),
 * and they stack. Short notices (command replies, "your side won", PvP seat
 * notices) go to the chat log instead, as an MSG_RADIALCHAT sent only to that
 * player. Modal messages (help pages, kicks) stay server messages: a popup.
 *
 * USAGE EXAMPLE:
 * session.Tell(ClassicChat.Line("Added 200000 gold."));
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using Imcodec.MessageLayer.Generated;

namespace Imlight.CoreLib.Classic;

internal static class ClassicChat {
    internal const string Speaker = "Wizard101 Classic";

    /// <summary>A chat line to one player (send it to that player's session only). No speech bubble: no source object.</summary>
    internal static GAME_5_PROTOCOL.MSG_RADIALCHAT Line(string text, string speaker = Speaker)
        => new() { SourceName = speaker, SourceID = 0, Message = text ?? "", Filter = 2 };

    /// <summary>A server notice: a popup when modal, otherwise a chat line (never a stacking "!" alert).</summary>
    internal static object Notice(string text, bool modal)
        => modal ? new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 1 } : Line(text);
}
