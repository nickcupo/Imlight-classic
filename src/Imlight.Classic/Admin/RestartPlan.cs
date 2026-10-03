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
 * SAFE RESTART PLAN
 * ========================================================================
 *
 * PURPOSE:
 * The timeline of a safe restart (or a backup, which also stops the
 * server): in-game warnings counting down ("Server restarting in 5
 * minutes"), then a wait until no one is in a fight, capped so a restart
 * always happens.
 *
 * USAGE EXAMPLE:
 * var plan = new RestartPlan(now, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), RestartKind.Restart, "update");
 * foreach (var text in plan.DueWarnings(now)) Broadcast(text);
 * if (plan.Decide(now, wizardsInFights) == RestartDecision.Go) Restart();
 *
 * NOTE:
 * Pure: the clock and the fight count come from the caller.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Admin;

/// <summary>What the server does at the end of the countdown.</summary>
public enum RestartKind {

    Restart,
    Backup

}

/// <summary>What to do now.</summary>
public enum RestartDecision {

    /// <summary>Still counting down.</summary>
    Countdown,

    /// <summary>The countdown is over, but someone is in a fight.</summary>
    WaitForFights,

    /// <summary>Restart now.</summary>
    Go

}

/// <summary>
/// One scheduled safe restart.
/// </summary>
public sealed class RestartPlan {

    // Seconds before the deadline at which a warning goes out (besides the one at the start).
    private static readonly int[] s_warningMarks = [3600, 1800, 900, 600, 300, 240, 180, 120, 60, 30, 10];

    private readonly SortedSet<int> _pendingMarks;
    private bool _announcedWait;

    public RestartPlan(DateTime nowUtc, TimeSpan warning, TimeSpan maxWait, RestartKind kind, string? reason) {
        if (warning < TimeSpan.Zero || maxWait < TimeSpan.Zero) {
            throw new ArgumentOutOfRangeException(nameof(warning), "times cannot be negative");
        }

        StartedUtc = nowUtc;
        DeadlineUtc = nowUtc + warning;
        HardCapUtc = DeadlineUtc + maxWait;
        Kind = kind;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        _pendingMarks = [.. s_warningMarks.Where(mark => mark < warning.TotalSeconds)];
    }

    public DateTime StartedUtc { get; }

    /// <summary>When the countdown ends.</summary>
    public DateTime DeadlineUtc { get; }

    /// <summary>The latest the restart happens, fights or not.</summary>
    public DateTime HardCapUtc { get; }

    public RestartKind Kind { get; }

    public string? Reason { get; }

    private bool _startAnnounced;

    /// <summary>
    /// The warnings due since the last call: the first one at once, then at the marks (an hour, 30, 15, 10, 5..1
    /// minutes, 30 and 10 seconds). Marks already passed together give only the latest.
    /// </summary>
    public IReadOnlyList<string> DueWarnings(DateTime nowUtc) {
        var due = new List<string>();
        var left = DeadlineUtc - nowUtc;
        if (!_startAnnounced) {
            _startAnnounced = true;
            if (left > TimeSpan.Zero) {
                due.Add(Message(left));
            }

            // Marks at or above what is left now were covered by this first warning.
            _pendingMarks.RemoveWhere(mark => mark >= Math.Ceiling(left.TotalSeconds));

            return due;
        }

        var passed = _pendingMarks.Where(mark => left.TotalSeconds <= mark).ToList();
        if (passed.Count > 0) {
            foreach (var mark in passed) {
                _pendingMarks.Remove(mark);
            }

            if (left > TimeSpan.Zero) {
                due.Add(Message(TimeSpan.FromSeconds(passed.Min())));
            }
        }

        return due;
    }

    /// <summary>Decides whether to restart now.</summary>
    /// <param name="nowUtc">The time now.</param>
    /// <param name="wizardsInFights">How many wizards are in a duel now.</param>
    public RestartDecision Decide(DateTime nowUtc, int wizardsInFights) {
        if (nowUtc < DeadlineUtc) {
            return RestartDecision.Countdown;
        }

        if (wizardsInFights > 0 && nowUtc < HardCapUtc) {
            return RestartDecision.WaitForFights;
        }

        return RestartDecision.Go;
    }

    /// <summary>The one message telling players the server waits for their fight, or null once said.</summary>
    public string? WaitMessage() {
        if (_announcedWait) {
            return null;
        }

        _announcedWait = true;

        return Kind == RestartKind.Backup
            ? "The server goes down for a backup as soon as the fights in progress end."
            : "The server restarts as soon as the fights in progress end.";
    }

    /// <summary>The broadcast for <paramref name="left"/> time to go.</summary>
    public string Message(TimeSpan left) {
        var when = Describe(left);
        var what = Kind == RestartKind.Backup ? "Server going down for a backup" : "Server restarting";
        var tail = Kind == RestartKind.Backup ? " It will be back in a minute or two." : "";

        return $"{what} in {when}.{(Reason is null ? "" : $" ({Reason})")}{tail}";
    }

    /// <summary>"5 minutes", "1 minute", "30 seconds".</summary>
    public static string Describe(TimeSpan left) {
        var seconds = (int) Math.Max(1, Math.Round(left.TotalSeconds));
        if (seconds >= 60) {
            var minutes = (int) Math.Round(seconds / 60.0);

            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        return seconds == 1 ? "1 second" : $"{seconds} seconds";
    }

}
