// CLASSIC: an actor Tell is not an acknowledged human ticket save.
using System;
using System.Threading;
using System.Threading.Tasks;
using Imlight.CoreLib.Classic.Arena;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ArenaShutdownOutcomeTests {
    [Fact]
    public async Task HumanSaveMustCompleteBeforeDrainAndDuplicateReceiptCannotAwardAgain() {
        var receipts = new ArenaOutcomeReceipts();
        var receipt = receipts.Begin(123);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var awards = 0;
        var saving = Task.Run(() => receipt.ApplyAward(() => {
            entered.Set();
            release.Wait();
            Interlocked.Increment(ref awards);
            return true;
        }));
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var drain = receipts.DrainAsync();
            Assert.False(drain.IsCompleted);
            release.Set();
            Assert.True(await saving.WaitAsync(TimeSpan.FromSeconds(5)));
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(receipt.ApplyAward(() => { Interlocked.Increment(ref awards); return true; }));
            Assert.Equal(1, awards);
            await receipts.DrainAsync();
        }
        finally {
            release.Set();
            await saving.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RefusedSaveFaultsDrainAndAnUncertainAwardIsNeverRetried() {
        var receipts = new ArenaOutcomeReceipts();
        var receipt = receipts.Begin(123);
        var attempts = 0;
        Assert.Throws<InvalidOperationException>(() => receipt.ApplyAward(() => { attempts++; return false; }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => receipts.DrainAsync());
        Assert.False(receipt.ApplyAward(() => { attempts++; return true; }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task LostActorReceiptTimesOutRatherThanClaimingTheOutcomeWasSaved() {
        var receipts = new ArenaOutcomeReceipts();
        var receipt = receipts.Begin(123);
        await Assert.ThrowsAsync<TimeoutException>(() => receipts.DrainAsync().WaitAsync(TimeSpan.FromMilliseconds(10)));
        Assert.False(receipt.Completion.IsCompleted);
    }
}
