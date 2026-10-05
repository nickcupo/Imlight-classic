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
 * ONE GAME SESSION PER ACCOUNT
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: an account is in the game once, with one wizard, as in 2009
 * ("only one character from an account can be logged in at a time", Fandom
 * Account, oldid 67863, 2010-04-29; a second login put the first out).
 * Every game session claims its account when it attaches. A newer login
 * closes the older session and waits (bounded) until it has stopped, so its
 * last saves are in before the new session loads the account; if the old
 * session does not stop in time, the new login is refused instead. The
 * login server puts an account's game session out the same way before it
 * lists the characters.
 *
 * Two sessions of one account each had their own live Account and Wizard:
 * the older one wrote stale Crowns, backpack and quest state over the newer
 * one's, and both could sell the same items (prod-exploits C1).
 *
 * USAGE EXAMPLE:
 * var claim = AccountSessions.Claim(accountId, session, loadedAtUtc);
 * if (claim == AccountClaim.Refused) { ... }
 *
 * NOTE:
 * A zone change is a reconnect: the old session is usually stopping already,
 * and is closed without the "logged in elsewhere" notice.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Imcodec.MessageLayer.Generated;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Game;

/// <summary>The outcome of <see cref="AccountSessions.Claim"/>.</summary>
internal enum AccountClaim {
    /// <summary>The account had no other session; what was loaded is current.</summary>
    Admitted,
    /// <summary>Another session of the account stopped after the account was loaded: load it again.</summary>
    AdmittedReload,
    /// <summary>The other session did not stop in time; this login is refused.</summary>
    Refused,
}

/// <summary>
/// CLASSIC: which game session holds each account, process-wide (every realm's game server runs in this process).
/// </summary>
internal static class AccountSessions {

    /// <summary>How long a new login waits for the account's old session to stop (and save).</summary>
    internal static TimeSpan StopWait { get; set; } = TimeSpan.FromSeconds(20);

    internal const string LoggedInElsewhere = "Your account was logged in from another location.";

    private sealed class Holder(ulong accountId, object session) {
        internal ulong AccountId { get; } = accountId;
        internal object Session { get; } = session;
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly object s_gate = new();
    private static readonly Dictionary<ulong, Holder> s_byAccount = [];
    private static readonly Dictionary<object, Holder> s_bySession = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<ulong, DateTime> s_lastStopUtc = [];

    /// <summary>Test seam: how a session is told to close. Defaults to <see cref="CloseSession"/>.</summary>
    internal static Action<object> Closer { get; set; } = CloseSession;

    /// <summary>
    /// Claims <paramref name="accountId"/> for <paramref name="session"/>. Another session holding it is closed, and
    /// this call blocks (at most <see cref="StopWait"/>) until that session has stopped.
    /// </summary>
    /// <param name="loadedAtUtc">When this session's copy of the account was loaded (just before the key check).</param>
    internal static AccountClaim Claim(ulong accountId, object session, DateTime loadedAtUtc) {
        if (session is null) {
            return AccountClaim.Refused;
        }

        var deadline = DateTime.UtcNow + StopWait;
        while (true) {
            Holder other;
            lock (s_gate) {
                if (!s_byAccount.TryGetValue(accountId, out other) || ReferenceEquals(other.Session, session)) {
                    if (other is null) {
                        var mine = new Holder(accountId, session);
                        s_byAccount[accountId] = mine;
                        s_bySession[session] = mine;
                    }

                    var stoppedSince = s_lastStopUtc.TryGetValue(accountId, out var stopped) && stopped >= loadedAtUtc;

                    return stoppedSince ? AccountClaim.AdmittedReload : AccountClaim.Admitted;
                }
            }

            Logger.Information("[SESSION] Account {0} logged in again; closing its older session.", Logger.Args(accountId));
            try {
                Closer(other.Session);
            }
            catch (Exception ex) {
                Logger.Warning("[SESSION] Could not close the older session of account {0}: {1}", Logger.Args(accountId, ex.Message));
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !other.Stopped.Task.Wait(left)) {
                Logger.Warning("[SESSION] Account {0}: the older session did not stop within {1} s; login refused.",
                    Logger.Args(accountId, StopWait.TotalSeconds));

                return AccountClaim.Refused;
            }

            // Stopped: loop to claim (another login may have claimed it meanwhile; that one is closed in turn).
        }
    }

    /// <summary>
    /// Closes the account's game session, if it has one, and waits (bounded) until it has stopped. For the login
    /// server: a new login puts the old game session out before the character list is read.
    /// </summary>
    /// <returns>False if a session is still running after the wait.</returns>
    internal static bool CloseAndWait(ulong accountId) {
        Holder other;
        lock (s_gate) {
            if (!s_byAccount.TryGetValue(accountId, out other)) {
                return true;
            }
        }

        Logger.Information("[SESSION] Account {0} logging in at the login server; closing its game session.", Logger.Args(accountId));
        try {
            Closer(other.Session);
        }
        catch (Exception ex) {
            Logger.Warning("[SESSION] Could not close the game session of account {0}: {1}", Logger.Args(accountId, ex.Message));
        }

        return other.Stopped.Task.Wait(StopWait);
    }

    /// <summary>A session has stopped (SessionActor.PostStop): its claim, if any, is released.</summary>
    internal static void Release(object session) {
        if (session is null) {
            return;
        }

        Holder mine;
        lock (s_gate) {
            if (!s_bySession.Remove(session, out mine)) {
                return;
            }

            if (s_byAccount.TryGetValue(mine.AccountId, out var holder) && ReferenceEquals(holder, mine)) {
                s_byAccount.Remove(mine.AccountId);
            }

            s_lastStopUtc[mine.AccountId] = DateTime.UtcNow;
        }

        mine.Stopped.TrySetResult();
    }

    /// <summary>The session holding the account, or null.</summary>
    internal static object HolderOf(ulong accountId) {
        lock (s_gate) {
            return s_byAccount.TryGetValue(accountId, out var holder) ? holder.Session : null;
        }
    }

    private static void CloseSession(object session) {
        if (session is not SessionActor actor) {
            return;
        }

        // A session on its way to another zone is closing anyway; it is not told it was logged in elsewhere.
        if (actor.TransferringOut) {
            actor.ActorRef.Tell("Close");

            return;
        }

        actor.ActorRef.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = LoggedInElsewhere, Modal = 1 });
        actor.ActorRef.Tell(SessionActor.AccountSessionsCloseSoon);
    }

}
