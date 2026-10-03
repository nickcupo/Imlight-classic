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
 * CLASSIC CHAT TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: notices are chat lines (no "!" alert) unless modal; the post-combat
 * grace uses the client's PostCombatEffect by name.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ClassicChatTests {
    [Fact]
    public void NoticesNeverCarryAnUnpackedSpeakerName() {
        // A chat line's speaker must be a packed name; plain text froze the client. Notices are server messages.
        var line = Assert.IsType<EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE>(ClassicChat.Notice("Added 5 gold.", modal: false));
        Assert.Equal("Added 5 gold.", line.Message.ToString());
        Assert.Equal(0, line.Modal);
        var popup = Assert.IsType<EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE>(ClassicChat.Notice("Help", modal: true));
        Assert.Equal(1, popup.Modal);
    }

    [Fact]
    public void ThePostCombatGraceIsTheClientsPostCombatEffect()
        => Assert.Equal(1618528611u, StringHash.Compute("PostCombatEffect"));
}
