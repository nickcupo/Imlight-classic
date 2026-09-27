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
 * KINP SESSION
 * ========================================================================
 *
 * PURPOSE:
 * When the server sends its KeepAlive, and whether a session that has not
 * answered one is dead.
 *
 * USAGE EXAMPLE:
 * var policy = new HeartbeatPolicy(keepAliveInterval, keepAliveRspWaitTime, sessionAcceptWaitTime);
 * if (policy.Judge(sentAt, lastHeardAt, zoneLoadStartedAt, now) == HeartbeatVerdict.Dead) { close(); }
 *
 * NOTE:
 * Any frame from the client counts as an answer; the client sends its own
 * KeepAlive every 10 seconds. A client that is loading a zone may not read
 * its socket, so silence is tolerated for the zone-load wait after the load
 * starts.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;

namespace Imlight.Classic.Net;

/// <summary>
/// What the end of a heartbeat's response wait found.
/// </summary>
public enum HeartbeatVerdict {

    /// <summary>
    /// The client sent something after the heartbeat went out.
    /// </summary>
    Alive,

    /// <summary>
    /// Nothing yet, but the client is still inside its zone-load wait.
    /// </summary>
    Loading,

    /// <summary>
    /// Nothing, and no reason to wait longer.
    /// </summary>
    Dead,

}

/// <summary>
/// The server heartbeat's timing and its rule for a silent session.
/// </summary>
public sealed class HeartbeatPolicy {

    /// <summary>
    /// Creates a policy from the ini's seconds.
    /// </summary>
    /// <param name="intervalSeconds">Advanced.KeepAliveInterval: seconds between heartbeats; 0 turns them off.</param>
    /// <param name="responseWaitSeconds">Advanced.KeepAliveRspWaitTime: seconds to wait for an answer; 0 turns heartbeats off.</param>
    /// <param name="zoneLoadWaitSeconds">Seconds of silence tolerated after a zone load starts.</param>
    public HeartbeatPolicy(int intervalSeconds, int responseWaitSeconds, int zoneLoadWaitSeconds) {
        Interval = TimeSpan.FromSeconds(Math.Max(0, intervalSeconds));
        ResponseWait = TimeSpan.FromSeconds(Math.Max(0, responseWaitSeconds));
        ZoneLoadWait = TimeSpan.FromSeconds(Math.Max(0, zoneLoadWaitSeconds));
    }

    /// <summary>
    /// Time between heartbeats.
    /// </summary>
    public TimeSpan Interval { get; }

    /// <summary>
    /// Time a heartbeat waits for an answer.
    /// </summary>
    public TimeSpan ResponseWait { get; }

    /// <summary>
    /// Silence tolerated after a zone load starts.
    /// </summary>
    public TimeSpan ZoneLoadWait { get; }

    /// <summary>
    /// False when either setting is 0: no heartbeat is scheduled.
    /// </summary>
    public bool IsEnabled => Interval > TimeSpan.Zero && ResponseWait > TimeSpan.Zero;

    /// <summary>
    /// Judges a session when a heartbeat's response wait ends.
    /// </summary>
    /// <param name="sentAt">When the heartbeat went out.</param>
    /// <param name="lastHeardAt">When the last frame arrived from the client.</param>
    /// <param name="zoneLoadStartedAt">When the session's latest zone load started, if it has had one.</param>
    /// <param name="now">The time now.</param>
    /// <returns>Whether the session is alive, loading, or dead.</returns>
    public HeartbeatVerdict Judge(DateTimeOffset sentAt, DateTimeOffset lastHeardAt, DateTimeOffset? zoneLoadStartedAt,
                                  DateTimeOffset now) {
        if (lastHeardAt >= sentAt) {
            return HeartbeatVerdict.Alive;
        }

        if (zoneLoadStartedAt is { } loadStarted && now - loadStarted < ZoneLoadWait) {
            return HeartbeatVerdict.Loading;
        }

        return HeartbeatVerdict.Dead;
    }

}
