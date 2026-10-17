/*
 * CLASSIC: a wizard who joins a zone standing inside an object's interaction range is "entered" at once (the join check), and the
 * first move afterwards changes nothing, so the options go out once at join and are not doubled by the move.
 */

using Akka.Actor;
using Imlight.CoreLib.Game.Zone.Components;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class JoinRangeTests {

    private sealed class FakeRef : MinimalActorRef {
        public override ActorPath Path { get; } = new RootActorPath(Address.AllSystems, "fake");
        public override IActorRefProvider Provider => null!;
    }

    [Fact]
    public void JoiningInsideRangeEntersOnceAndALaterMoveInsideChangesNothing() {
        var range = new PlayersInRange();
        var actor = new FakeRef();
        Assert.Equal(RangeChange.Entered, range.Update(7, actor, true));   // OnPlayerJoin
        Assert.Equal(RangeChange.Unchanged, range.Update(7, actor, true)); // OnPlayerMove, standing still or nudging
        Assert.Equal(RangeChange.Left, range.Update(7, actor, false));
    }

}
