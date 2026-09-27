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
 * CLASSIC CINEMATICS
 * ========================================================================
 *
 * PURPOSE:
 * The zone timers KingsIsle's triggers start around a staged cinematic: a
 * timer ends when a client reports the cinematic's sync event, or when its
 * time runs out, and its end posts "End<timer name>" into the zone.
 *
 * USAGE EXAMPLE:
 * timers.Start(zoneKey, "MalistaireCinematic", "CLIENTEVENT.SawMalistaireFightIntro",
 *              TimeSpan.FromSeconds(300), () => PostEvent(zone, "EndMalistaireCinematic"));
 * timers.ClientEvent(zoneKey, "SawMalistaireFightIntro");   // MSG_POSTZONEEVENTFROMCLIENT
 *
 * NOTE:
 * r806919's Malistaire lair is the Arc 1 case: StartCinematicTrigger starts
 * timer "MalistaireCinematic" (300 s, condition
 * CLIENTEVENT.SawMalistaireFightIntro) and plays the staged cinematic
 * MalistaireFightIntro; the cinematic's last stage has a
 * ServerSyncCinematicAction that sends SawMalistaireFightIntro; the trigger
 * TriggerSetUpScene fires on EndMalistaireCinematic and spawns the fight.
 * Without the timer the fight never appears. The time limit is the data's
 * own, so a client that never plays the cinematic still gets the fight.
 * A timer's end runs once, whichever comes first; starting a timer that is
 * already running in the zone restarts it.
 *
 * TODO:
 * - KingsIsle's class name for this result is unknown (type hash 1038905797).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Threading;

namespace Imlight.Classic.Cinematics;

/// <summary>
/// Zone timers that end on a client event or on their time limit.
/// </summary>
/// <param name="schedule">Runs an action after a delay; the result cancels it when disposed.</param>
public sealed class CinematicTimers(Func<TimeSpan, Action, IDisposable> schedule) {

    /// <summary>
    /// The prefix KingsIsle's zone data puts on a client event condition.
    /// </summary>
    public const string ClientEventPrefix = "CLIENTEVENT.";

    /// <summary>
    /// The prefix of the zone event a timer's end posts.
    /// </summary>
    public const string EndEventPrefix = "End";

    private readonly Lock _lock = new();
    private readonly Dictionary<(object Zone, string Name), Running> _running = [];

    /// <summary>
    /// The zone event a timer's end posts.
    /// </summary>
    /// <param name="timerName">The timer's name.</param>
    public static string EndEventOf(string timerName) => EndEventPrefix + timerName;

    /// <summary>
    /// The client event a condition waits for, or null if it waits for none.
    /// </summary>
    /// <param name="condition">The timer's condition, such as <c>CLIENTEVENT.SawMalistaireFightIntro</c>.</param>
    public static string? ClientEventOf(string? condition)
        => condition is not null && condition.StartsWith(ClientEventPrefix, StringComparison.Ordinal)
           && condition.Length > ClientEventPrefix.Length
            ? condition[ClientEventPrefix.Length..]
            : null;

    /// <summary>
    /// The number of timers running now.
    /// </summary>
    public int Count {
        get {
            lock (_lock) {
                return _running.Count;
            }
        }
    }

    /// <summary>
    /// Starts (or restarts) a timer in a zone.
    /// </summary>
    /// <param name="zone">The zone, compared by equality (the zone actor).</param>
    /// <param name="timerName">The timer's name.</param>
    /// <param name="condition">The data's end condition, such as <c>CLIENTEVENT.SawX</c>; may be null.</param>
    /// <param name="limit">The time limit; zero or less ends at once.</param>
    /// <param name="onEnd">Runs once when the timer ends.</param>
    public void Start(object zone, string timerName, string? condition, TimeSpan limit, Action onEnd) {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentException.ThrowIfNullOrEmpty(timerName);
        ArgumentNullException.ThrowIfNull(onEnd);

        var running = new Running(ClientEventOf(condition), onEnd);
        Running? replaced;
        lock (_lock) {
            _running.Remove((zone, timerName), out replaced);
            _running[(zone, timerName)] = running;
        }

        replaced?.Cancel();
        if (limit <= TimeSpan.Zero) {
            End(zone, timerName, running);

            return;
        }

        running.Expiry = schedule(limit, () => End(zone, timerName, running));
    }

    /// <summary>
    /// A client reported an event in a zone: ends every timer there that waits for it.
    /// </summary>
    /// <param name="zone">The zone.</param>
    /// <param name="eventName">The event, without the CLIENTEVENT. prefix.</param>
    /// <returns>The number of timers it ended.</returns>
    public int ClientEvent(object zone, string? eventName) {
        if (zone is null || string.IsNullOrEmpty(eventName)) {
            return 0;
        }

        var waiting = new List<(string Name, Running Timer)>();
        lock (_lock) {
            foreach (var ((timerZone, name), timer) in _running) {
                if (timerZone.Equals(zone) && string.Equals(timer.ClientEvent, eventName, StringComparison.Ordinal)) {
                    waiting.Add((name, timer));
                }
            }
        }

        var ended = 0;
        foreach (var (name, timer) in waiting) {
            if (End(zone, name, timer)) {
                ended++;
            }
        }

        return ended;
    }

    /// <summary>
    /// Stops every timer of a zone without running their ends (the zone unloaded).
    /// </summary>
    /// <param name="zone">The zone.</param>
    public void StopZone(object zone) {
        var stopped = new List<Running>();
        lock (_lock) {
            foreach (var key in new List<(object Zone, string Name)>(_running.Keys)) {
                if (key.Zone.Equals(zone) && _running.Remove(key, out var timer)) {
                    stopped.Add(timer);
                }
            }
        }

        foreach (var timer in stopped) {
            timer.Cancel();
        }
    }

    private bool End(object zone, string timerName, Running timer) {
        lock (_lock) {
            if (_running.TryGetValue((zone, timerName), out var current) && ReferenceEquals(current, timer)) {
                _running.Remove((zone, timerName));
            }
        }

        if (!timer.TryFinish()) {
            return false;
        }

        timer.Expiry?.Dispose();
        timer.OnEnd();

        return true;
    }

    private sealed class Running(string? clientEvent, Action onEnd) {

        private int _finished;

        public string? ClientEvent { get; } = clientEvent;
        public Action OnEnd { get; } = onEnd;
        public IDisposable? Expiry { get; set; }

        public bool TryFinish() => Interlocked.Exchange(ref _finished, 1) == 0;

        public void Cancel() {
            if (TryFinish()) {
                Expiry?.Dispose();
            }
        }

    }

}
