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
 * FRIEND REQUEST DIRECTION TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Only the wizard who received a friend request can answer it (multiplayer
 * audit: a wizard could accept its own request; two wizards asking each
 * other at once dropped both requests).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using Imlight.CoreLib.Game.Services;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class FriendRequestDirectionTests {

    [Fact]
    public void TheRequesterCannotAnswerItsOwnRequest() {
        const ulong a = 9_100_001, b = 9_100_002;
        FriendsService.NoteIncomingRequest(recipient: b, requester: a);

        Assert.False(FriendsService.TakeIncomingRequest(recipient: a, requester: b));
        Assert.True(FriendsService.HasIncomingRequest(b, a));
        Assert.True(FriendsService.TakeIncomingRequest(recipient: b, requester: a));
        Assert.False(FriendsService.TakeIncomingRequest(recipient: b, requester: a));
    }

    [Fact]
    public void RequestsBothWaysAreBothShownAndOneAnswerSettlesThem() {
        const ulong a = 9_200_001, b = 9_200_002;
        FriendsService.NoteIncomingRequest(recipient: b, requester: a);
        Assert.False(FriendsService.HasIncomingRequest(a, b)); // a's own request does not hide b's
        FriendsService.NoteIncomingRequest(recipient: a, requester: b);

        Assert.True(FriendsService.TakeIncomingRequest(recipient: a, requester: b));
        Assert.False(FriendsService.HasIncomingRequest(b, a));
    }

}
