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
 * MINION HELPER HUB
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: joins an account's game session with its Minion Helper
 * connections. Sessions bind when a wizard attaches to a zone; helpers attach
 * after showing a paired token. Orders from a helper go to the session's
 * combat service as internal messages, so the duel checks them exactly like
 * any other owner order (owner, Myth school, duel, round, card, pips,
 * target). State goes back to every helper of the account.
 *
 * USAGE EXAMPLE:
 * MinionHelperHub.Shared.BindSession(account.AccountId, SessionActor.ActorRef);
 * MinionHelperHub.Shared.Push(account.AccountId, json);
 *
 * NOTE:
 * Thread-safe: sessions call it from actor threads, helpers from socket tasks.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Akka.Actor;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Classic.MinionHelper;

/// <summary>One helper connection; Send writes one JSON line and never throws.</summary>
internal interface IMinionHelperConnection {
    void Send(string line);
}

internal sealed class MinionHelperHub {
    internal static MinionHelperHub Shared { get; } = new();

    private readonly ConcurrentDictionary<ulong, IActorRef> _sessions = new();
    private readonly ConcurrentDictionary<ulong, ImmutableArray<IMinionHelperConnection>> _helpers = new();
    private readonly object _gate = new();

    /// <summary>A session of this account is now in the world; any helper of the account links to it.</summary>
    internal void BindSession(ulong accountId, IActorRef session) {
        if (accountId == 0 || session is null) return;
        _sessions[accountId] = session;
        if (HelperCount(accountId) > 0) {
            session.Tell(new COMBAT_106_PROTOCOL.MSG_MINIONHELPERLINK { Connected = true }, ActorRefs.NoSender);
        }
    }

    /// <summary>The session is going away; its helpers show the wizard as offline.</summary>
    internal void UnbindSession(ulong accountId, IActorRef session) {
        if (accountId == 0 || session is null) return;
        if (((ICollection<KeyValuePair<ulong, IActorRef>>) _sessions).Remove(new(accountId, session))) {
            Push(accountId, Offline());
        }
    }

    internal bool HasSession(ulong accountId) => _sessions.ContainsKey(accountId);

    internal int HelperCount(ulong accountId)
        => _helpers.TryGetValue(accountId, out var list) ? list.Length : 0;

    /// <summary>A helper showed a valid token for this account.</summary>
    internal void AttachHelper(ulong accountId, IMinionHelperConnection helper) {
        lock (_gate) {
            _helpers.AddOrUpdate(accountId, [helper], (_, list) => list.Add(helper));
        }

        if (_sessions.TryGetValue(accountId, out var session)) {
            session.Tell(new COMBAT_106_PROTOCOL.MSG_MINIONHELPERLINK { Connected = true }, ActorRefs.NoSender);
        } else {
            helper.Send(Offline());
        }
    }

    /// <summary>A helper left. When the last one leaves, the minions go back to their AI.</summary>
    internal void DetachHelper(ulong accountId, IMinionHelperConnection helper) {
        bool last;
        lock (_gate) {
            if (!_helpers.TryGetValue(accountId, out var list)) return;
            var rest = list.Remove(helper);
            if (rest.Length == list.Length) return;
            if (rest.IsEmpty) _helpers.TryRemove(accountId, out _);
            else _helpers[accountId] = rest;
            last = rest.IsEmpty;
        }

        if (last && _sessions.TryGetValue(accountId, out var session)) {
            session.Tell(new COMBAT_106_PROTOCOL.MSG_MINIONHELPERLINK { Connected = false }, ActorRefs.NoSender);
        }
    }

    /// <summary>Hands a helper message to the account's session. False when the wizard is not in the world.</summary>
    internal bool Forward(ulong accountId, IServerMessage message) {
        if (!_sessions.TryGetValue(accountId, out var session)) return false;
        session.Tell(message, ActorRefs.NoSender);
        return true;
    }

    /// <summary>Sends one JSON line to every helper of the account.</summary>
    internal void Push(ulong accountId, string line) {
        if (!_helpers.TryGetValue(accountId, out var list)) return;
        foreach (var helper in list) helper.Send(line);
    }

    internal static string Offline() => JsonSerializer.Serialize(new { op = "state", phase = "offline" });

    /// <summary>Tests: forget everything.</summary>
    internal void ResetForTests() {
        lock (_gate) {
            _sessions.Clear();
            _helpers.Clear();
        }
    }

    internal IReadOnlyList<IMinionHelperConnection> HelpersOf(ulong accountId)
        => _helpers.TryGetValue(accountId, out var list) ? list : [];

    internal IEnumerable<ulong> Accounts => _sessions.Keys.ToArray();
}
