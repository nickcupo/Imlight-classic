using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

// CLASSIC: Prince Gobblestone (WC_Colossus_ThroneRoom, aggro radius 1) fights only when the room's script sends him.
public sealed class ScriptedAggroTests {

    [Fact]
    public void ThePrinceWithRadiusOneIsScriptedOnlyAndAnOrdinaryGobblerIsNot() {
        Assert.True(ScriptedAggro.IsScriptedOnly(isMonster: true, proximity: 1f));
        Assert.False(ScriptedAggro.IsScriptedOnly(isMonster: true, proximity: 350f)); // a Gobbler Gorger
        Assert.False(ScriptedAggro.IsScriptedOnly(isMonster: false, proximity: 1f)); // a bystander with no deck
    }

    [Fact]
    public void AnArmedWizardIsAttackedOnlyOnceHeIsNearTheBoss() {
        Assert.False(ScriptedAggro.Engages(true, 1f, armed: true, inReach: false)); // the MakeWar try at the door: not from across the room
        Assert.True(ScriptedAggro.Engages(true, 1f, armed: true, inReach: true));
    }

    [Fact]
    public void AWizardTheScriptNeverSentTheBossAfterIsLeftAlone() {
        Assert.False(ScriptedAggro.Engages(true, 1f, armed: false, inReach: true));
    }

    [Fact]
    public void AnOrdinaryCreatureKeepsItsOwnProximityAggroOnly() {
        Assert.False(ScriptedAggro.Engages(true, 350f, armed: true, inReach: true));
    }

    [Fact]
    public void TheReachIsLongerThanAGobblersRadiusSoAWizardWalkingInIsCaught() {
        Assert.True(ScriptedAggro.Reach > 350f);
        Assert.True(ScriptedAggro.Reach < 1000f); // never from the entrance, ~3000 away
    }
}
