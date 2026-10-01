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
 * PLAYER QUERY
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: asks a player's session for its active wizard with a timeout.
 * Zone actors (the trigger supervisor, triggers, NPC service mementos) used
 * to block on an Ask with no timeout; Akka's default ask timeout is
 * infinite, so a player whose session had just gone (a logout or a server
 * transfer between the event and the question) never answered and the
 * zone actor stopped for good: every trigger of the zone, or one object's
 * NEWOBJECTs, interactions and MSG_QUERYZONEENTITY answers.
 *
 * USAGE EXAMPLE:
 * var wizard = PlayerQuery.ActiveWizard(message.PlayerActor, "trigger " + name);
 * if (wizard is null) { return; }
 *
 * NOTE:
 * A missing answer is logged once per place and player actor, then at debug.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Concurrent;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// Asks a player's session for its wizard without blocking the asking actor forever.
/// </summary>
internal static class PlayerQuery {

    /// <summary>How long a zone actor waits for a player's session to answer.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly ConcurrentDictionary<(string Where, string Player), bool> s_reported = new();

    /// <summary>How long a session that did not answer is skipped without asking again.</summary>
    internal static readonly TimeSpan Backoff = TimeSpan.FromSeconds(30);

    // Sessions that timed out, until when they are skipped. A zone asks per trigger and per event, so one departed
    // or stuck session otherwise blocked the whole zone for 5 s on every music and ambient trigger (2026-10-01).
    private static readonly ConcurrentDictionary<IActorRef, DateTime> s_unresponsive = new();

    /// <summary>
    /// The player's active wizard, or null when the session does not answer within <see cref="Timeout"/>.
    /// </summary>
    /// <param name="player">The player's session actor.</param>
    /// <param name="where">Who asks, for the log.</param>
    /// <returns>The wizard, or null.</returns>
    internal static Wizard ActiveWizard(IActorRef player, string where) {
        if (TryActiveWizard(player, Timeout, out var wizard, out var error) || error is null) {
            return wizard;
        }

        var key = (where ?? "?", player.Path.ToString());
        if (s_reported.Count > 10_000) {
            s_reported.Clear();
        }

        if (s_reported.TryAdd(key, true)) {
            Logger.Warning("{Where}: {Player} did not answer the wizard query within {Seconds} s ({Error}); skipped.",
                Logger.Args(where, player.Path.Name, Timeout.TotalSeconds, error.GetBaseException().GetType().Name));
        }
        else {
            Logger.Debug("{Where}: {Player} did not answer the wizard query; skipped.", Logger.Args(where, player.Path.Name));
        }

        return null;
    }

    /// <summary>
    /// Asks <paramref name="player"/> for its wizard, waiting at most <paramref name="timeout"/>.
    /// </summary>
    /// <param name="player">The player's session actor.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="wizard">The wizard, or null.</param>
    /// <param name="error">Why there is no answer; null when there was no one to ask.</param>
    /// <returns>True when the session answered.</returns>
    internal static bool TryActiveWizard(IActorRef player, TimeSpan timeout, out Wizard wizard, out Exception error) {
        wizard = null;
        error = null;
        if (player is null || player.IsNobody() || player is IInternalActorRef { IsTerminated: true }) {
            return false; // A session that has shut down has no wizard; asking it only waits out the timeout.
        }

        if (s_unresponsive.TryGetValue(player, out var until)) {
            if (DateTime.UtcNow < until) {
                return false;
            }

            s_unresponsive.TryRemove(player, out _);
        }

        try {
            wizard = player.Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD(), timeout)
                .Result?.Wizard;

            return true;
        }
        catch (Exception ex) {
            error = ex;
            if (s_unresponsive.Count > 10_000) {
                s_unresponsive.Clear();
            }

            s_unresponsive[player] = DateTime.UtcNow + Backoff;

            return false;
        }
    }

}
