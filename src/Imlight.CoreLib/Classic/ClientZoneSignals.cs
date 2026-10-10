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
 * CLIENT ZONED SIGNALS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the real client sends MSG_CLIENTZONED after it enters a zone. A
 * duel holding a seat for a wizard who logged back in waits for it before it
 * sends the wizard the fight (Imlight.Classic.Rules.RejoinTiming). The session
 * reports the message here; the duel asks here, keyed by character and
 * session, so an older session's report never counts for a newer one.
 *
 * USAGE EXAMPLE:
 * if (ClientZoneSignals.Await(charId, session, duelActor)) { ready now } else { a MSG_REJOINCLIENTREADY follows }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System.Collections.Concurrent;
using System.Collections.Generic;
using Akka.Actor;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Classic;

internal static class ClientZoneSignals {

    private sealed record Waiter(IActorRef Session, IActorRef Duel);

    // The last session of each character whose client reported entering its zone.
    private static readonly ConcurrentDictionary<ulong, IActorRef> s_zoned = new();
    private static readonly ConcurrentDictionary<ulong, Waiter> s_waiting = new();

    /// <summary>The session of <paramref name="charId"/> got MSG_CLIENTZONED; a duel waiting for it is told.</summary>
    internal static void Zoned(ulong charId, IActorRef session) {
        if (charId == 0 || session is null) {
            return;
        }

        s_zoned[charId] = session;
        if (s_waiting.TryGetValue(charId, out var waiter) && Equals(waiter.Session, session)
                && s_waiting.TryRemove(new KeyValuePair<ulong, Waiter>(charId, waiter))) {
            waiter.Duel.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_REJOINCLIENTREADY { CharacterId = charId, Zoned = true });
        }
    }

    /// <summary>
    /// True when the client of <paramref name="session"/> already reported entering the zone; otherwise
    /// <paramref name="duel"/> is told (MSG_REJOINCLIENTREADY, Zoned) when it does.
    /// </summary>
    internal static bool Await(ulong charId, IActorRef session, IActorRef duel) {
        if (charId == 0 || session is null || duel is null) {
            return false;
        }

        if (HasZoned(charId, session)) {
            return true;
        }

        var waiter = new Waiter(session, duel);
        s_waiting[charId] = waiter;

        // The report may have come in between the check and the registration.
        if (HasZoned(charId, session) && s_waiting.TryRemove(new KeyValuePair<ulong, Waiter>(charId, waiter))) {
            return true;
        }

        return false;
    }

    /// <summary>The duel no longer waits for <paramref name="session"/> (it left, or the duel went out).</summary>
    internal static void Forget(ulong charId, IActorRef session) {
        if (s_waiting.TryGetValue(charId, out var waiter) && Equals(waiter.Session, session)) {
            s_waiting.TryRemove(new KeyValuePair<ulong, Waiter>(charId, waiter));
        }
    }

    internal static bool HasZoned(ulong charId, IActorRef session)
        => s_zoned.TryGetValue(charId, out var zoned) && Equals(zoned, session);

    /// <summary>For tests: forget everything.</summary>
    internal static void ResetForTests() {
        s_zoned.Clear();
        s_waiting.Clear();
    }

}
