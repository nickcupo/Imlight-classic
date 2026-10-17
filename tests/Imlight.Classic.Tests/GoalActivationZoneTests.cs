using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Zone.Supervisors;
using Xunit;

namespace Imlight.Classic.Tests;

/// <summary>
/// CLASSIC (cel playthrough): a goal that becomes active while the wizard is already in its zone re-posts EnterZone to the
/// triggers that wait for that goal (Mimir Winterbane's "Trigger During Quest" checks GH-AUS-C02-001 Goal 2).
/// </summary>
public sealed class GoalActivationZoneTests {

    private static RequirementList During(string quest, string goal) => new() {
        m_operator = Operator.ROP_AND,
        m_requirements = [new ReqHasGoal { m_questName = quest, m_goalName = goal, m_requiredStatus = GoalStatusRequirement.Incomplete }],
    };

    [Fact]
    public void ATriggerThatChecksTheGoalIsWoken() {
        Assert.True(ZoneTriggerSupervisor.NamesGoal(During("GH-AUS-C02-001", "Goal 2"), "GH-AUS-C02-001", "Goal 2"));
    }

    [Fact]
    public void AnotherGoalOrQuestIsNot() {
        Assert.False(ZoneTriggerSupervisor.NamesGoal(During("GH-AUS-C02-001", "Goal 2"), "GH-AUS-C02-001", "Goal 3"));
        Assert.False(ZoneTriggerSupervisor.NamesGoal(During("GH-AUS-C02-001", "Goal 2"), "GH-AUS-C02-002", "Goal 2"));
        Assert.False(ZoneTriggerSupervisor.NamesGoal(null, "GH-AUS-C02-001", "Goal 2"));
    }

    [Fact]
    public void ANestedListCounts() {
        var outer = new RequirementList { m_operator = Operator.ROP_AND, m_requirements = [During("Q", "Goal 1")] };
        Assert.True(ZoneTriggerSupervisor.NamesGoal(outer, "Q", "Goal 1"));
    }

    [Fact]
    public void ObjectStateTagsCountAsStateResults() {
        // Nastrond "Reveal Seals": four AddTriggerObject results and a RemoveDynaMod; it must replay after a zone reload.
        Assert.True(ZoneTriggerPlans.IsStateResult(new ResRemoveDynaMod()));
        Assert.True(ZoneTriggerPlans.IsStateResult(new ResAddDynaMod()));
        Assert.False(ZoneTriggerPlans.IsStateResult(new ResSpawn()));
    }

}
