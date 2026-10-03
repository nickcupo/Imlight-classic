using System.Collections.Generic;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.WizardData.Collections;
using Xunit;

namespace Imlight.Classic.Tests;

// CLASSIC: Hallowe'en 2009's pumpkins and apple tubs (Jack Hallow's quests).
public sealed class HolidayObjectUseTests {

    private static WaypointGoalTemplate PumpkinGoal(string tag) => new() { m_clientTags = [tag], m_goalType = GOAL_TYPE.GOAL_TYPE_USAGE };

    [Fact]
    public void APumpkinCompletesOnlyTheGoalOfItsOwnStreet() {
        var pumpkin = new GameObjectTemplate { m_objectName = "HO_Pumpkin02" };

        Assert.True(InteractableQuestEvents.CompletesGoal(pumpkin, "WizardCity/WC_Streets/WC_Unicorn", PumpkinGoal("UnicornPumpkin")));
        Assert.False(InteractableQuestEvents.CompletesGoal(pumpkin, "WizardCity/WC_Streets/WC_Cyclops", PumpkinGoal("UnicornPumpkin")));
        Assert.True(InteractableQuestEvents.CompletesGoal(pumpkin, "WizardCity/WC_Shop_Area", PumpkinGoal("GetTreat")));
    }

    [Fact]
    public void PumpkinsAndApplesRollTheirInteractLootTables() {
        Assert.Equal("HO-Gold-Pumpkins", InteractableQuestEvents.UseLootTable(new GameObjectTemplate { m_objectName = "HO_Pumpkin03" }));
        Assert.Equal("HO-AppleTub-03", InteractableQuestEvents.UseLootTable(new GameObjectTemplate { m_objectName = "HO_AppleTub03" }));
        Assert.Null(InteractableQuestEvents.UseLootTable(new GameObjectTemplate { m_objectName = "DS_Desk" }));
    }

    [Fact]
    public void ARegistryGatedSpawnerRunsWithoutAQuestOnlyOnTheClassicEngine() {
        var ghosts = new SpawnObject { m_active = false, m_globalDynamic = true, m_globalDynamicReqs = new RequirementList() };
        var dormant = new SpawnObject { m_active = false };

        Assert.False(Imlight.CoreLib.Classic.ClassicQuestSpawns.RunsOnItsOwn(dormant));
        Assert.True(Imlight.CoreLib.Classic.ClassicQuestSpawns.RunsOnItsOwn(new SpawnObject { m_active = true }));
        Assert.Equal(Imlight.CoreLib.Classic.ClassicQuestEngine.IsActive, Imlight.CoreLib.Classic.ClassicQuestSpawns.RunsOnItsOwn(ghosts));
    }

}
