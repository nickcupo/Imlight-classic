using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class DoorLightRulesTests {
    [Fact] public void UnboundLightPreservesPlacedState() => Assert.Null(DoorLightRules.State(null, ["Test/Quest"]));
    [Fact] public void ClosedQuestDoorRemainsOff() => Assert.Equal("Off", DoorLightRules.State([new("Test/Quest", true, false)], ["Test/Quest"]));
    [Fact] public void DisarmedDoorIsClosed() => Assert.Equal("Off", DoorLightRules.State([new("Test/Quest", false, true)], ["Test/Quest"]));
    [Fact] public void EligibleNonquestDoorIsYellow() => Assert.Equal("On", DoorLightRules.State([new("Test/Open", true, true)], ["Test/Other"]));
    [Fact] public void EligibleActiveGoalDoorIsBlue() => Assert.Equal("Quest", DoorLightRules.State([new("Test/Quest", true, true)], ["test/quest"]));
    [Fact] public void UnresolvedTeleportDoesNotStealDoorFromLaterResolvedRoute() => Assert.Equal("Quest", DoorLightRules.State([new("", true, true), new("Test/Quest", true, true)], ["Test/Quest"]));
    [Fact] public void LaterQuestAlternativeDoesNotColorWinningPlainDoorBlue() => Assert.Equal("On", DoorLightRules.State([new("Test/Plain", true, true), new("Test/Quest", true, true)], ["Test/Quest"]));
    [Fact] public void StateChangesWithPerPlayerEligibilityAndGoalProgress() {
        DoorLightRules.Route[] routes = [new("Test/Door", true, true)];
        Assert.Equal("Quest", DoorLightRules.State(routes, ["Test/Door"]));
        Assert.Equal("On", DoorLightRules.State(routes, []));
        Assert.Equal("Off", DoorLightRules.State([new("Test/Door", true, false)], ["Test/Door"]));
    }
}
