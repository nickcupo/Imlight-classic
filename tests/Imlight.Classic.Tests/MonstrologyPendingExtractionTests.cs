using System.Collections.Generic;
using Imlight.CoreLib.Game.Monstrology;
using Xunit;
namespace Imlight.Classic.Tests;
public sealed class MonstrologyPendingExtractionTests {
    [Fact] public void LaterDotTicksDoNotMultiplyCastHitAnimusAndDefeatAddsOne() {
        var pending = new MonstrologyPendingExtraction();
        pending.Observe(42,9,35085,true,100,75);
        pending.Observe(42,9,35085,true,75,50,countHit:false);
        pending.Observe(42,9,35085,true,50,0,countHit:false);
        Assert.Equal(2,Assert.Single(pending.Finish(true,new HashSet<ulong>{42})).Animus);
    }

    [Fact] public void DamagingHitAndFinishingHitAccumulateOnlyForVictoriousPresentOwner() {
        var pending = new MonstrologyPendingExtraction();
        pending.Observe(42,9,35085,true,100,50);
        pending.Observe(42,9,35085,true,50,0);
        var earned = Assert.Single(pending.Finish(true,new HashSet<ulong>{42}));
        Assert.Equal(3,earned.Animus);
        Assert.Empty(pending.Finish(true,new HashSet<ulong>{42}));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void LossOrDepartedOwnerCannotEarn(bool victory) {
        var pending = new MonstrologyPendingExtraction();
        pending.Observe(42,9,35085,true,10,0);
        Assert.Empty(pending.Finish(victory,new HashSet<ulong>()));
        Assert.Empty(pending.Finish(true,new HashSet<ulong>{42}));
    }
    [Fact] public void ImmuneHealingDeadIneligibleAndUnknownTargetsDoNotEarn() {
        var pending = new MonstrologyPendingExtraction();
        pending.Observe(42,9,35085,true,10,10);
        pending.Observe(42,9,35085,true,10,20);
        pending.Observe(42,9,35085,true,0,0);
        pending.Observe(42,9,35085,false,10,0);
        pending.Observe(42,9,0,true,10,0);
        Assert.Empty(pending.Finish(true,new HashSet<ulong>{42}));
    }
}
