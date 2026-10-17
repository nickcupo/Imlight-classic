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
*/

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Imlight.CoreLib.Patch;

/// <summary>
/// CLASSIC: keeps looking for the patch endpoint (Aurorium) when it was not there at start.
/// </summary>
/// <remarks>
/// At container boot the game server and Aurorium start together, and Aurorium can need a restart before it listens
/// (on 2026-10-17 it panicked once with "Cannot assign requested address" because the container's address was not up
/// yet, and listened 15 s later). The server checked only once and then ran without the patch server until someone
/// restarted it. This probes in the background, every <see cref="FastInterval"/> for the first
/// <see cref="FastPhase"/>, then every <see cref="SlowInterval"/>, until the endpoint answers or the server stops.
/// The probe and the wait are injected so the schedule can be tested without a network or a clock.
/// </remarks>
internal static class PatchEndpointRetry {

    internal static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan FastPhase = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan SlowInterval = TimeSpan.FromMinutes(3);

    /// <summary>Probes during the fast phase (10 min / 15 s = 40), after which the slow interval applies.</summary>
    internal static readonly int FastAttempts = (int) (FastPhase.Ticks / FastInterval.Ticks);

    /// <summary>The wait before retry number <paramref name="attempt"/> (1-based).</summary>
    internal static TimeSpan DelayBefore(int attempt) => attempt <= FastAttempts ? FastInterval : SlowInterval;

    /// <summary>
    /// Waits, probes, and repeats until <paramref name="probe"/> answers true (returns the 1-based attempt that
    /// answered) or <paramref name="cancellation"/> is cancelled (returns 0). A probe that throws counts as a miss.
    /// <paramref name="onMiss"/> is told about every failed attempt (for sparse logging).
    /// </summary>
    internal static async Task<int> RunAsync(
        Func<CancellationToken, Task<bool>> probe,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellation,
        Action<int, TimeSpan> onMiss = null) {
        for (var attempt = 1; ; attempt++) {
            var wait = DelayBefore(attempt);
            try {
                await delay(wait, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                return 0;
            }

            if (cancellation.IsCancellationRequested) {
                return 0;
            }

            bool answered;
            try {
                answered = await probe(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
                return 0;
            }
            catch (Exception) {
                answered = false;
            }

            if (answered) {
                return attempt;
            }

            onMiss?.Invoke(attempt, DelayBefore(attempt + 1));
        }
    }

    /// <summary>
    /// Log a miss only on the first, at the end of the fast phase, and then about hourly, so a missing patch server
    /// does not fill the journal.
    /// </summary>
    internal static bool ShouldLogMiss(int attempt) {
        if (attempt == 1 || attempt == FastAttempts) {
            return true;
        }

        var slowPerHour = (int) (TimeSpan.FromHours(1).Ticks / SlowInterval.Ticks);
        return attempt > FastAttempts && (attempt - FastAttempts) % slowPerHour == 0;
    }

}
