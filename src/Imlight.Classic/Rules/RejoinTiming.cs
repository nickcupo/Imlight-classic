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
 * WHEN A WIZARD WHO LOGGED BACK IN GETS THEIR DUEL
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: a wizard who dropped mid-fight and logs back in takes the seat
 * held for them. The duel (MSG_DUEL, every participant, the slot, the hand)
 * used to go out the moment the zone added the wizard: right after
 * MSG_LOGINCOMPLETE, while the client was still loading the zone and before
 * the zone had sent it the participants' objects. A headless client dropped
 * it (no participants; it never played and the seat was defeated), and on
 * the live server a friend's real r806919 client lost its connection 17 s after such a
 * rejoin (live log, 2026-10-09 01:19:09 to 01:19:26).
 *
 * Now the seat stays held (it passes, and a duel whose every wizard is away
 * waits) until the client is in the zone:
 *   - the real client sends MSG_CLIENTZONED ("sent from the client after it
 *     enters a new zone", its own message table); after it, and at least
 *     ClientLoadDelay after the wizard arrived (the arena seats a wizard the
 *     same 2 s after arrival, which the owner's client handles), the duel goes
 *     out the way it does for a wizard walking into a running fight;
 *   - a client that never says so (the headless playbot) gets it after
 *     ZonedWait.
 *
 * USAGE EXAMPLE:
 * if (RejoinTiming.Due(arrivedUtc, zoned, DateTime.UtcNow)) { ... }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System;

namespace Imlight.Classic.Rules;

public static class RejoinTiming {

    /// <summary>The least time after arrival before the duel goes out (the arena's seat delay).</summary>
    public static readonly TimeSpan ClientLoadDelay = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait for MSG_CLIENTZONED before sending the duel anyway.</summary>
    public static readonly TimeSpan ZonedWait = TimeSpan.FromSeconds(10);

    /// <summary>Timers can fire a little early by the wall clock.</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(100);

    /// <summary>True when a wizard who arrived at <paramref name="arrivedUtc"/> gets their held duel now.</summary>
    public static bool Due(DateTime arrivedUtc, bool zoned, DateTime nowUtc)
        => nowUtc + Tolerance - arrivedUtc >= (zoned ? ClientLoadDelay : ZonedWait);

}
