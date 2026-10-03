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
 * CLASSIC: a short server notice to one player. The r806919 client turns every
 * non-modal MSG_SERVERMESSAGE into a "!" alert icon (WizardGUIManager::
 * HandleServerMessage, 0x140dd30d0). A chat line (MSG_RADIALCHAT) would avoid
 * that, but its SourceName must be a packed name (name keys, or a serialized
 * MadlibBlock): plain text made the client log "Failed to unpack name" and freeze
 * at 100% CPU (2026-10-02 17:59, a minion's chat cue). Until a packed speaker
 * name is proven in the client, Notice and Line send MSG_SERVERMESSAGE again.
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

    /// <summary>A short notice to one player (send it to that player's session only).</summary>
    internal static EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE Line(string text)
        => new() { Message = text ?? "", Modal = 0 };

    /// <summary>A server notice: a popup when modal, otherwise a short notice.</summary>
    internal static object Notice(string text, bool modal)
        => modal ? new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 1 } : Line(text);
}
