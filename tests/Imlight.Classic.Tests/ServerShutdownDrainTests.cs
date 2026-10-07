// CLASSIC: a blocked or refused human save must leave its session and Raven alive during shutdown.
using System;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Imlight.CoreLib.Classic.Admin;
using Imlight.CoreLib.Classic.Arena;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ServerShutdownDrainTests {
    [Fact]
    public async Task ShutdownLeavesSessionAliveUntilHumanSaveAcknowledgesAndThenWaitsForActorTermination() {
        using var system = ActorSystem.Create("shutdown-save-barrier", "akka.actor.provider=local");
        var receipts = new ArenaOutcomeReceipts();
        var receipt = receipts.Begin(123);
        var sessionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = system.ActorOf(Props.Create(() => new ClosingSession(sessionStarted, sessionStopped)));
        using var savingEntered = new ManualResetEventSlim();
        using var releaseSave = new ManualResetEventSlim();
        var databaseStopped = 0;
        var awards = 0;
        var saving = Task.Run(() => receipt.ApplyAward(() => {
            savingEntered.Set();
            releaseSave.Wait();
            Interlocked.Increment(ref awards);
            return true;
        }));
        try {
            await sessionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(savingEntered.Wait(TimeSpan.FromSeconds(5)));
            var drainingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped = ServerShutdownDrain.CompleteAsync(TimeSpan.FromSeconds(5),
                _ => Task.CompletedTask,
                _ => { drainingEntered.TrySetResult(); return receipts.DrainAsync(); },
                async remaining => Assert.True(await session.GracefulStop(remaining, "Close")),
                _ => system.Terminate(),
                _ => {
                    Assert.True(sessionStopped.Task.IsCompletedSuccessfully);
                    Assert.True(system.WhenTerminated.IsCompletedSuccessfully);
                    Interlocked.Increment(ref databaseStopped);
                    return Task.CompletedTask;
                });
            await drainingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopped.IsCompleted);
            Assert.Equal(0, databaseStopped);
            Assert.False(sessionStopped.Task.IsCompleted);
            Assert.Equal("alive", await session.Ask<string>("Read", TimeSpan.FromSeconds(5)));
            releaseSave.Set();
            Assert.True(await saving.WaitAsync(TimeSpan.FromSeconds(5)));
            await stopped.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, awards);
            Assert.Equal(1, databaseStopped);
        }
        finally {
            releaseSave.Set();
            await saving.WaitAsync(TimeSpan.FromSeconds(5));
            await system.Terminate();
        }
    }

    [Fact]
    public async Task LostHumanAcknowledgementHitsDeadlineWithoutClosingSessionOrDisposingDatabase() {
        var receipts = new ArenaOutcomeReceipts();
        var receipt = receipts.Begin(123);
        var laterStages = 0;
        Task Later(TimeSpan _) { Interlocked.Increment(ref laterStages); return Task.CompletedTask; }
        var error = await Assert.ThrowsAsync<TimeoutException>(() => ServerShutdownDrain.CompleteAsync(
            TimeSpan.FromMilliseconds(50), _ => Task.CompletedTask, _ => receipts.DrainAsync(), Later, Later, Later));
        Assert.Contains("saved ticket awards", error.Message);
        Assert.Equal(0, laterStages);
        Assert.False(receipt.Completion.IsCompleted);
    }

    [Fact]
    public async Task RefusedHumanSaveFailsShutdownWithoutClosingSessionOrDisposingDatabase() {
        var receipts = new ArenaOutcomeReceipts();
        var receipt = receipts.Begin(123);
        Assert.Throws<InvalidOperationException>(() => receipt.ApplyAward(() => false));
        var laterStages = 0;
        Task Later(TimeSpan _) { Interlocked.Increment(ref laterStages); return Task.CompletedTask; }
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServerShutdownDrain.CompleteAsync(
            TimeSpan.FromSeconds(5), _ => Task.CompletedTask, _ => receipts.DrainAsync(), Later, Later, Later));
        Assert.Equal(0, laterStages);
    }

    private sealed class ClosingSession : ReceiveActor {
        private readonly TaskCompletionSource _started;
        private readonly TaskCompletionSource _stopped;
        public ClosingSession(TaskCompletionSource started, TaskCompletionSource stopped) {
            _started = started;
            _stopped = stopped;
            Receive<string>(message => message == "Read", _ => Sender.Tell("alive"));
            Receive<string>(message => message == "Close", _ => Context.Stop(Self));
        }
        protected override void PreStart() { base.PreStart(); _started.TrySetResult(); }
        protected override void PostStop() { _stopped.TrySetResult(); base.PostStop(); }
    }
}
