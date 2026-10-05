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
 * CHAT AUDIENCE TESTS
 * ========================================================================
 *
 * PURPOSE:
 * A Say reaches the sender's own copy of a zone only (multiplayer audit:
 * two sigil runs of one dungeon heard each other).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System.Linq;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.WizardData.Models.Misc;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ChatAudienceTests {

    private static OnlinePlayer P(ulong id, ulong instance) => new() { CharacterId = id, InstanceOwnerId = instance };

    [Fact]
    public void SayStaysInTheSendersInstance() {
        var zone = new[] { P(1, 0), P(2, 0), P(3, 77), P(4, 77), P(5, 88) };

        Assert.Equal([2UL], ChatService.ChatAudience(zone, 1, 0, []).Select(p => p.CharacterId));
        Assert.Equal([4UL], ChatService.ChatAudience(zone, 3, 77, []).Select(p => p.CharacterId));
        Assert.Empty(ChatService.ChatAudience(zone, 5, 88, []));
    }

    [Fact]
    public void SayStillSkipsThoseWhoBlockedTheSender() {
        var zone = new[] { P(1, 0), P(2, 0), P(3, 0) };

        Assert.Equal([3UL], ChatService.ChatAudience(zone, 1, 0, [2UL]).Select(p => p.CharacterId));
    }

}
