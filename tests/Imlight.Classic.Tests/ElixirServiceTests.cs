using System;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imlight.CoreLib.Classic.Elixirs;
using Imlight.CoreLib.Game.Services;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ElixirServiceTests {
    [Fact]
    public void AFailedCheckpointRetainsDebtAndACommittedCheckpointRetainsFractions() {
        var clock = new ElixirOnlineClock(1000);
        clock.Begin([1], 0);
        Assert.Empty(clock.Pending([1], 750));
        var failed = clock.Pending([1], 1750);
        Assert.Equal(1u, failed[1]);
        var retry = clock.Pending([1], 2750);
        Assert.Equal(2u, retry[1]);
        clock.Commit(retry);
        Assert.Empty(clock.Pending([1], 2999));
        Assert.Equal(1u, clock.Pending([1], 3000)[1]);
    }

    [Fact]
    public void ANewItemDoesNotInheritElapsedTimeFromAnExistingItem() {
        var clock = new ElixirOnlineClock(1000);
        clock.Begin([1], 1000);
        var elapsed = clock.Pending([1, 2], 5500);
        Assert.Equal(4u, elapsed[1]);
        Assert.False(elapsed.ContainsKey(2));
        clock.Commit(elapsed);
        elapsed = clock.Pending([1, 2], 6500);
        Assert.Equal(1u, elapsed[1]);
        Assert.Equal(1u, elapsed[2]);
    }

    [Fact]
    public void ANewOnlineSessionNeverChargesTheOfflineGap() {
        var clock = new ElixirOnlineClock(1000);
        clock.Begin([1], 0);
        clock.Commit(clock.Pending([1], 2000));
        clock.Begin([1], 1_000_000_000);
        Assert.Empty(clock.Pending([1], 1_000_000_999));
        Assert.Equal(1u, clock.Pending([1], 1_000_001_000)[1]);
    }

    [Fact]
    public void RemovingAnItemDropsItsAnchorAndReappearingStartsANewOne() {
        var clock = new ElixirOnlineClock(1000);
        clock.Begin([1, 2], 0);
        Assert.Single(clock.Pending([2], 5000));
        var elapsed = clock.Pending([1, 2], 6000);
        Assert.False(elapsed.ContainsKey(1));
        Assert.Equal(6u, elapsed[2]);
    }

    [Fact]
    public void DuplicateOrZeroIdsCannotDoubleChargeAndBackwardTicksCannotCharge() {
        var clock = new ElixirOnlineClock(1000);
        clock.Begin([0, 1, 1], 1000);
        Assert.Empty(clock.Pending([0, 1, 1], 500));
        var elapsed = clock.Pending([0, 1, 1], 2000);
        Assert.Equal(1u, Assert.Single(elapsed).Value);
        Assert.Throws<InvalidOperationException>(() => clock.Commit(new System.Collections.Generic.Dictionary<ulong, uint> { [2] = 1 }));
    }

    [Fact]
    public async Task TheProductionElixirActorConstructsWithoutStartingAnUnauthenticatedClock() {
        EquipmentAttachConcurrencyTests.Configure();
        using var system = ActorSystem.Create("elixir-service-transport", "akka.actor.provider = local");
        var actor = system.ActorOf(Props.Create(() => new ElixirService(null!)), "elixirs");
        try {
            var identity = await actor.Ask<ActorIdentity>(new Identify("ready"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(actor, identity.Subject);
        }
        finally { await system.Terminate(); }
    }
}
