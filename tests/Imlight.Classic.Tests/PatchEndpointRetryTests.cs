/*
 * CLASSIC: the background search for the patch endpoint (Aurorium) when it was not there at start
 * (Imlight.CoreLib/Patch/PatchEndpointRetry.cs, PatchServer.StartEndpointRetry).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Last Updated: 10/17/2026
 */
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Imlight.CoreLib.Patch;
using Xunit;

namespace Imlight.Classic.Tests;

public class PatchEndpointRetryTests {

    [Fact]
    public void ItChecksEvery15SecondsForTenMinutesThenEveryThreeMinutes() {
        Assert.Equal(40, PatchEndpointRetry.FastAttempts);
        Assert.Equal(TimeSpan.FromSeconds(15), PatchEndpointRetry.DelayBefore(1));
        Assert.Equal(TimeSpan.FromSeconds(15), PatchEndpointRetry.DelayBefore(40));
        Assert.Equal(TimeSpan.FromMinutes(3), PatchEndpointRetry.DelayBefore(41));
        Assert.Equal(TimeSpan.FromMinutes(3), PatchEndpointRetry.DelayBefore(1000));

        var fastTotal = Enumerable.Range(1, PatchEndpointRetry.FastAttempts)
            .Aggregate(TimeSpan.Zero, (sum, attempt) => sum + PatchEndpointRetry.DelayBefore(attempt));
        Assert.Equal(TimeSpan.FromMinutes(10), fastTotal);
    }

    [Fact]
    public async Task ItStopsAtTheFirstAnswerAndReportsWhichCheckAnswered() {
        var waits = new List<TimeSpan>();
        var misses = new List<int>();
        var probes = 0;

        var attempt = await PatchEndpointRetry.RunAsync(
            _ => Task.FromResult(++probes == 3),
            (wait, _) => { waits.Add(wait); return Task.CompletedTask; },
            CancellationToken.None,
            (miss, _) => misses.Add(miss));

        Assert.Equal(3, attempt);
        Assert.Equal(3, probes);
        Assert.Equal(new[] { 1, 2 }, misses);
        // It always waits before probing: the start-up check has just failed.
        Assert.All(waits, wait => Assert.Equal(TimeSpan.FromSeconds(15), wait));
        Assert.Equal(3, waits.Count);
    }

    [Fact]
    public async Task ItKeepsGoingPastTheFastPhaseAtTheSlowInterval() {
        var waits = new List<TimeSpan>();
        var attempt = await PatchEndpointRetry.RunAsync(
            _ => Task.FromResult(waits.Count == 45),
            (wait, _) => { waits.Add(wait); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal(45, attempt);
        Assert.Equal(40, waits.Count(w => w == TimeSpan.FromSeconds(15)));
        Assert.Equal(5, waits.Count(w => w == TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public async Task AProbeThatThrowsCountsAsAMiss() {
        var probes = 0;
        var attempt = await PatchEndpointRetry.RunAsync(
            _ => ++probes < 2 ? throw new InvalidOperationException("connection refused") : Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task StoppingTheServerEndsTheSearchWithoutAnAnswer() {
        using var stop = new CancellationTokenSource();
        var probes = 0;
        var attempt = await PatchEndpointRetry.RunAsync(
            _ => {
                if (++probes == 2) {
                    stop.Cancel();
                }
                return Task.FromResult(false);
            },
            (_, token) => token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask,
            stop.Token);

        Assert.Equal(0, attempt);
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task ARealDelayIsCancelledPromptly() {
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var run = PatchEndpointRetry.RunAsync(
            _ => Task.FromResult(false),
            (wait, token) => Task.Delay(wait, token),
            stop.Token);

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Same(run, finished);
        Assert.Equal(0, await run);
    }

    [Fact]
    public void MissesAreLoggedSparsely() {
        var logged = Enumerable.Range(1, 40 + 60).Where(PatchEndpointRetry.ShouldLogMiss).ToArray();
        // First miss, end of the fast phase, then once per hour of 3-minute checks.
        Assert.Equal(new[] { 1, 40, 60, 80, 100 }, logged);
    }

}
