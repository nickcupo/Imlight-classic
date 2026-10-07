// CLASSIC: database shutdown must wait for the real timer's in-flight callback, not just cancel future ticks.
using System;
using System.Threading;
using System.Threading.Tasks;
using Imlight.CoreLib.Classic.Arena;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ArenaShutdownClockTests {
    [Fact]
    public async Task StopWaitsForInFlightCallbackAndNoTickSurvivesItsCompletion() {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var ticks = 0;
        var clock = new ArenaClock(() => {
            Interlocked.Increment(ref ticks);
            entered.Set();
            release.Wait();
        }, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        Task? stopped = null;
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "The timer callback did not start.");
            stopped = clock.StopAsync();
            Assert.Same(stopped, clock.StopAsync());
            Assert.False(stopped.IsCompleted);
            release.Set();
            await stopped.WaitAsync(TimeSpan.FromSeconds(5));
            var afterStop = Volatile.Read(ref ticks);
            await Task.Delay(TimeSpan.FromMilliseconds(40));
            Assert.Equal(afterStop, Volatile.Read(ref ticks));
        }
        finally {
            release.Set();
            await (stopped ?? clock.StopAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
