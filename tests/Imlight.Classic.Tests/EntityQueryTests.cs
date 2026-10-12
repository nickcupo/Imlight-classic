using Imlight.Classic.Zones;
using Xunit;

namespace Imlight.Classic.Tests;

public class EntityQueryTests {

    [Fact]
    public void GlobalIdQueryNamesOnlyThatEntity() {
        Assert.True(EntityQuery.Matches(100, 7, 100, 0));
        Assert.False(EntityQuery.Matches(200, 8, 100, 0));
    }

    [Fact]
    public void EntityWithoutMobileIdDoesNotAnswerAGlobalIdQuery() {
        // The rig-final 2026-10-12 bug: a mobile-0 entity answered a query for the Colossus sigil and NPC.
        Assert.False(EntityQuery.Matches(5467, 0, 5674, 0));
    }

    [Fact]
    public void NonZeroMobileIdStillFindsTheEntity() {
        Assert.True(EntityQuery.Matches(5467, 15, 0, 15));
        Assert.False(EntityQuery.Matches(5467, 0, 0, 0));
    }

}
