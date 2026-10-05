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
 * PERFORMANCE MONITOR
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: optional server health numbers for load tests and live checks.
 * With [Classic] PerfLogSeconds > 0 the server logs one "PERF" line per
 * interval:
 *   - handler: time spent in each actor message handler (all actors built
 *     on ReceiveProtocolDispatcher), with the slowest kinds named;
 *   - zone: how late a zone actor handles a timer message (its mailbox wait
 *     plus dispatcher delay) and its largest mailbox backlog;
 *   - pool / akka: how long a work item waits for a thread-pool thread, and
 *     a message for an Akka default-dispatcher actor (starvation);
 *   - gc: collections per generation, pause time, heap and allocation rate;
 *   - combat: from the last combat move of a round to the round's actions
 *     being sent, less the fixed 1 s early-finish delay.
 * Off (0) costs one volatile read per handler.
 *
 * USAGE EXAMPLE:
 * PerfMonitor.Start(system, seconds);   // at boot
 * var t = PerfMonitor.Begin(); ... PerfMonitor.EndHandler(actorType, messageType, t);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Akka.Actor;
using Imlight.Common;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// A lock-free latency histogram with fixed millisecond buckets (percentiles are bucket upper bounds; max is exact).
/// </summary>
internal sealed class LatencyHistogram {

    internal static readonly double[] BucketUpperMs =
        [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, double.PositiveInfinity];

    private readonly long[] _counts = new long[BucketUpperMs.Length];
    private long _total;
    private long _sumMicros;
    private long _maxMicros;

    internal long Count => Interlocked.Read(ref _total);

    internal void Record(double ms) {
        if (ms < 0) {
            ms = 0;
        }

        var i = 0;
        while (ms > BucketUpperMs[i]) {
            i++;
        }

        Interlocked.Increment(ref _counts[i]);
        Interlocked.Increment(ref _total);
        var micros = (long) (ms * 1000);
        Interlocked.Add(ref _sumMicros, micros);
        long seen;
        while (micros > (seen = Interlocked.Read(ref _maxMicros))
               && Interlocked.CompareExchange(ref _maxMicros, micros, seen) != seen) { }
    }

    /// <summary>The bucket bound below which <paramref name="fraction"/> of the samples fall.</summary>
    internal double Percentile(double fraction) {
        var total = Count;
        if (total == 0) {
            return 0;
        }

        var want = (long) Math.Ceiling(total * fraction);
        long seen = 0;
        for (var i = 0; i < _counts.Length; i++) {
            seen += Interlocked.Read(ref _counts[i]);
            if (seen >= want) {
                return double.IsPositiveInfinity(BucketUpperMs[i]) ? Max : BucketUpperMs[i];
            }
        }

        return Max;
    }

    internal double Max => Interlocked.Read(ref _maxMicros) / 1000.0;

    internal double Mean => Count == 0 ? 0 : Interlocked.Read(ref _sumMicros) / 1000.0 / Count;

    internal string Describe()
        => Count == 0 ? "n=0" : $"n={Count} p50={Percentile(0.5):0.##} p95={Percentile(0.95):0.##} p99={Percentile(0.99):0.##} max={Max:0.#}";

}

/// <summary>
/// CLASSIC: periodic PERF log lines (see the file header). Disabled unless <see cref="Start"/> runs with a positive interval.
/// </summary>
internal static class PerfMonitor {

    /// <summary>A zone's timer probe; <see cref="DueTicks"/> is when it was due (Stopwatch ticks).</summary>
    internal sealed record ZoneProbe(long DueTicks);

    private sealed class ProbeActor : ReceiveActor {
        public ProbeActor() => Receive<long>(sent => Akka_.Record(Ms(sent, Stopwatch.GetTimestamp())));
    }

    private sealed class HandlerStat {
        public long Count;
        public long TotalTicks;
        public long MaxTicks;
    }

    private static volatile bool s_enabled;
    private static LatencyHistogram s_handlers = new();
    private static LatencyHistogram s_zone = new();
    private static LatencyHistogram s_pool = new();
    private static LatencyHistogram s_akka = new();
    private static LatencyHistogram s_combat = new();
    private static ConcurrentDictionary<(Type Actor, Type Message), HandlerStat> s_byKind = new();
    private static int s_zoneBacklogMax;
    private static Thread s_thread;

    internal static bool Enabled => s_enabled;

    /// <summary>How often each zone probes itself.</summary>
    internal static readonly TimeSpan ZoneProbeInterval = TimeSpan.FromMilliseconds(250);

    private static LatencyHistogram Akka_ => s_akka;

    internal static double Ms(long fromTicks, long toTicks) => (toTicks - fromTicks) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Starts the monitor when <paramref name="intervalSeconds"/> is positive.</summary>
    internal static void Start(ActorSystem system, int intervalSeconds) {
        if (intervalSeconds <= 0 || s_thread is not null) {
            return;
        }

        var probe = system.ActorOf(Props.Create(() => new ProbeActor()), "perf-probe");
        s_enabled = true;
        s_thread = new Thread(() => Run(probe, TimeSpan.FromSeconds(intervalSeconds))) {
            IsBackground = true, Name = "perf-monitor", Priority = ThreadPriority.AboveNormal,
        };
        s_thread.Start();
        Logger.Information("PERF monitor on: one line every {Seconds} s.", Logger.Args(intervalSeconds));
    }

    internal static long Begin() => s_enabled ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Records one handler run that started at <paramref name="started"/> (from <see cref="Begin"/>).</summary>
    internal static void EndHandler(Type actor, Type message, long started) {
        if (started == 0) {
            return;
        }

        var ticks = Stopwatch.GetTimestamp() - started;
        s_handlers.Record(ticks * 1000.0 / Stopwatch.Frequency);
        var stat = s_byKind.GetOrAdd((actor, message), _ => new HandlerStat());
        Interlocked.Increment(ref stat.Count);
        Interlocked.Add(ref stat.TotalTicks, ticks);
        long seen;
        while (ticks > (seen = Interlocked.Read(ref stat.MaxTicks))
               && Interlocked.CompareExchange(ref stat.MaxTicks, ticks, seen) != seen) { }
    }

    /// <summary>A zone handled its probe: how late it was and how many messages were still queued.</summary>
    internal static void ZoneProbeHandled(ZoneProbe probe, int backlog) {
        s_zone.Record(Ms(probe.DueTicks, Stopwatch.GetTimestamp()));
        int seen;
        while (backlog > (seen = Volatile.Read(ref s_zoneBacklogMax))
               && Interlocked.CompareExchange(ref s_zoneBacklogMax, backlog, seen) != seen) { }
    }

    /// <summary>A round's actions went out <paramref name="ms"/> after its last move (early-finish delay removed).</summary>
    internal static void CombatTurnaround(double ms) {
        if (s_enabled) {
            s_combat.Record(ms);
        }
    }

    private static readonly GCKind[] s_gcKinds = [GCKind.Ephemeral, GCKind.FullBlocking, GCKind.Background];
    private static readonly long[] s_lastGcIndex = new long[3];
    private static double s_maxGcPauseMs;

    /// <summary>The longest single GC pause: the latest GC of each kind, sampled five times a second (a GC that is
    /// followed by another of its kind within 200 ms is missed).</summary>
    private static void SampleGcPause() {
        for (var i = 0; i < s_gcKinds.Length; i++) {
            var info = GC.GetGCMemoryInfo(s_gcKinds[i]);
            if (info.Index == 0 || info.Index == s_lastGcIndex[i]) {
                continue;
            }

            s_lastGcIndex[i] = info.Index;
            foreach (var pause in info.PauseDurations) {
                s_maxGcPauseMs = Math.Max(s_maxGcPauseMs, pause.TotalMilliseconds);
            }
        }
    }

    private static void Run(IActorRef probe, TimeSpan interval) {
        var next = DateTime.UtcNow + interval;
        var gen = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var pause = GC.GetTotalPauseDuration();
        var allocated = GC.GetTotalAllocatedBytes();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.GetTimestamp();
        while (true) {
            // Starvation probes, five a second: a pool work item and an Akka message, each stamped when queued.
            var queued = Stopwatch.GetTimestamp();
            ThreadPool.UnsafeQueueUserWorkItem(_ => s_pool.Record(Ms(queued, Stopwatch.GetTimestamp())), null);
            probe.Tell(Stopwatch.GetTimestamp());
            SampleGcPause();
            Thread.Sleep(200);
            if (DateTime.UtcNow < next) {
                continue;
            }

            next += interval;
            var now = Stopwatch.GetTimestamp();
            var seconds = Ms(wall, now) / 1000.0;
            var gen2 = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
            var pause2 = GC.GetTotalPauseDuration();
            var allocated2 = GC.GetTotalAllocatedBytes();
            var cpu2 = Process.GetCurrentProcess().TotalProcessorTime;
            var (handlers, zone, pool, akka, combat, byKind) = (s_handlers, s_zone, s_pool, s_akka, s_combat, s_byKind);
            s_handlers = new LatencyHistogram();
            s_zone = new LatencyHistogram();
            s_pool = new LatencyHistogram();
            s_akka = new LatencyHistogram();
            s_combat = new LatencyHistogram();
            s_byKind = new ConcurrentDictionary<(Type, Type), HandlerStat>();
            var backlog = Interlocked.Exchange(ref s_zoneBacklogMax, 0);
            var maxGcPause = s_maxGcPauseMs;
            s_maxGcPauseMs = 0;

            var slow = byKind.OrderByDescending(kv => kv.Value.MaxTicks).Take(4)
                .Select(kv => $"{kv.Key.Actor.Name}/{kv.Key.Message.Name} max={kv.Value.MaxTicks * 1000.0 / Stopwatch.Frequency:0.#}");
            var busy = byKind.OrderByDescending(kv => kv.Value.TotalTicks).Take(4)
                .Select(kv => $"{kv.Key.Actor.Name}/{kv.Key.Message.Name} n={kv.Value.Count} sum={kv.Value.TotalTicks * 1000.0 / Stopwatch.Frequency:0}");
            ThreadPool.GetAvailableThreads(out var freeWorkers, out _);
            ThreadPool.GetMaxThreads(out var maxWorkers, out _);
            var info = GC.GetGCMemoryInfo();
            var line = new StringBuilder()
                .Append($"PERF cpu={(cpu2 - cpu).TotalMilliseconds / 10 / seconds:0}% ")
                .Append($"handler[{handlers.Describe()}] zone[{zone.Describe()} backlog={backlog}] ")
                .Append($"pool[{pool.Describe()} threads={ThreadPool.ThreadCount} busy={maxWorkers - freeWorkers} queued={ThreadPool.PendingWorkItemCount}] ")
                .Append($"akka[{akka.Describe()}] combat[{combat.Describe()}] ")
                .Append($"gc[g0={gen2[0] - gen[0]} g1={gen2[1] - gen[1]} g2={gen2[2] - gen[2]} pause={(pause2 - pause).TotalMilliseconds:0}ms ")
                .Append($"maxpause={maxGcPause:0.#}ms ")
                .Append($"heap={info.HeapSizeBytes / 1048576}MB alloc={(allocated2 - allocated) / 1048576.0 / seconds:0.0}MB/s] ")
                .Append($"slowest[{string.Join("; ", slow)}] busiest[{string.Join("; ", busy)}]");
            Logger.Information(line.ToString().Replace("{", "{{").Replace("}", "}}"));
            (gen, pause, allocated, cpu, wall) = (gen2, pause2, allocated2, cpu2, now);
        }
    }

}
