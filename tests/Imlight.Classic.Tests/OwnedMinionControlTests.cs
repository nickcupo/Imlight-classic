using System;
using Imlight.CoreLib.Game.Combat;
using Xunit;
using static Imlight.CoreLib.Shared.Packets.COMBAT_106_PROTOCOL;

namespace Imlight.Classic.Tests;

public sealed class OwnedMinionControlTests {
    private static OwnedMinionAccess Allowed => new(true, true, true, true, true, true, true, true, true, true);

    [Fact]
    public void AccessRequiresCurrentDuelRoundPlanningLivingMythAndOwnership() {
        Assert.Equal(OwnedMinionStatus.Accepted, OwnedMinionControl.ValidateAccess(Allowed, false));
        foreach (var (access, expected) in new[] {
            (Allowed with { SameDuel = false }, OwnedMinionStatus.InvalidDuel),
            (Allowed with { SameRound = false }, OwnedMinionStatus.InvalidRound),
            (Allowed with { Planning = false }, OwnedMinionStatus.NotPlanning),
            (Allowed with { OwnerPresent = false }, OwnedMinionStatus.InvalidOwner),
            (Allowed with { OwnerAlive = false }, OwnedMinionStatus.InvalidOwner),
            (Allowed with { OwnerIsMyth = false }, OwnedMinionStatus.NotMyth),
            (Allowed with { MinionPresent = false }, OwnedMinionStatus.NotOwnedMinion),
            (Allowed with { MinionAlive = false }, OwnedMinionStatus.NotOwnedMinion),
            (Allowed with { Owned = false }, OwnedMinionStatus.NotOwnedMinion),
            (Allowed with { SupportedSummon = false }, OwnedMinionStatus.UnsupportedSummon),
        }) Assert.Equal(expected, OwnedMinionControl.ValidateAccess(access, false));
        Assert.Equal(OwnedMinionStatus.Accepted, OwnedMinionControl.ValidateAccess(
            Allowed with { MinionPresent = false, MinionAlive = false, Owned = false, SupportedSummon = false }, true));
        Assert.Equal(OwnedMinionStatus.NotMyth, OwnedMinionControl.ValidateAccess(Allowed with { OwnerIsMyth = false }, true));
    }

    [Fact]
    public void EnemySelectionRejectsSentinelFriendDeadAndEmptyRatherThanRetargeting() {
        Assert.True(OwnedMinionControl.ValidTarget(OwnedMinionTarget.Enemy, true, true, false, false, false, false));
        Assert.False(OwnedMinionControl.ValidTarget(OwnedMinionTarget.Enemy, false, true, false, false, false, false));
        Assert.False(OwnedMinionControl.ValidTarget(OwnedMinionTarget.Enemy, true, true, true, false, false, false));
        Assert.False(OwnedMinionControl.ValidTarget(OwnedMinionTarget.Enemy, true, false, false, false, false, false));
        Assert.True(OwnedMinionControl.ValidTarget(OwnedMinionTarget.Self, false, false, false, false, false, false));
        Assert.False(OwnedMinionControl.ValidTarget(OwnedMinionTarget.Self, true, true, true, false, false, false));
        Assert.False(OwnedMinionControl.ValidTarget(OwnedMinionTarget.FriendNotSelf, true, true, true, true, false, false));
        Assert.False(OwnedMinionControl.ValidTarget(OwnedMinionTarget.OwnMinion, true, true, true, false, true, false));
        Assert.True(OwnedMinionControl.ValidTarget(OwnedMinionTarget.OwnMinion, true, true, true, false, true, true));
    }

    [Fact]
    public void OptInRequiresEveryLiveMinionOrderAndCannotLeakAcrossIdentityOrRound() {
        var state = new OwnedMinionControl(); var owner = new object(); var other = new object();
        var first = new object(); var second = new object();
        var live = new[] { (Owner: owner, Minion: first), (Owner: owner, Minion: second), (Owner: other, Minion: new object()) };
        Assert.True(state.AllOrdered(live));
        state.OptIn(owner); Assert.False(state.AllOrdered(live));
        state.SetOrder(first, new OwnedMinionOrder(3, 0, uint.MaxValue)); Assert.False(state.AllOrdered(live));
        state.SetOrder(second, new OwnedMinionOrder(0, 1, 2)); Assert.True(state.AllOrdered(live));
        state.Withdraw(second); Assert.False(state.AllOrdered(live));
        Assert.True(state.AllOrdered([live[0]]));
        Assert.False(state.HasOrder(new object()));
        state.NewRound(); Assert.True(state.IsOptedIn(owner)); Assert.False(state.HasOrder(first));
        state.Disable(owner); Assert.True(state.AllOrdered(live));
        state.OptIn(owner); state.Clear(); Assert.False(state.IsOptedIn(owner));
    }

    [Fact]
    public void RequestsCannotReplayOrReplaceWithOlderIdButResetWithRound() {
        var state = new OwnedMinionControl(); var owner = new object();
        Assert.True(state.IsNewRequest(owner, 0)); state.RecordRequest(owner, 7);
        Assert.False(state.IsNewRequest(owner, 7)); Assert.False(state.IsNewRequest(owner, 6));
        Assert.True(state.IsNewRequest(owner, 8)); Assert.True(state.IsNewRequest(new object(), 0));
        state.Disable(owner); Assert.False(state.IsNewRequest(owner, 6));
        state.NewRound(); Assert.True(state.IsNewRequest(owner, 0));
    }
}
