// CLASSIC: a fresh Crimson Fields instance must restore the invasion for a held, unfinished kill goal.
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Newtonsoft.Json;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class EvermoreWaveReplayTests {
    private const string Battlefield = "MooShu/MS_War/MS_War_BattlefieldA";

    private static QuestTemplate Load() => JsonConvert.DeserializeObject<QuestTemplate>(
        File.ReadAllText(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates",
            "MS-WAR3-C02-005.json")),
        new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto, NullValueHandling = NullValueHandling.Ignore })!;

    [Fact]
    public void ReenteringAFreshInstanceRestoresTheHeldInvasionGoal() {
        var quest = Load();
        var kill = Assert.IsType<BountyGoalTemplate>(quest.m_goals.Single(g => g.m_goalName.ToString() == "Goal"));
        var replay = Assert.Single(GoalZoneEvents.ToReplay([quest], (_, goal) => goal == "Goal", Battlefield));
        Assert.Same(kill, replay.Goal);
        var post = Assert.IsType<ResPostEvent>(Assert.Single(replay.Goal.m_activateResults.m_results));
        Assert.Equal("SpawnMobs", post.m_eventName.ToString());
        Assert.Empty(quest.m_startResults.m_results);
        Assert.Equal(10, kill.m_bountyTotal);
        Assert.Equal(10, kill.m_tallyCounter.m_count);
        Assert.Equal(5, kill.m_npcAdjectives.Count);
    }

    [Fact]
    public void HandInAndCompletedGoalsCannotRestartTheWaveOrRewards() {
        var quest = Load();
        Assert.Empty(GoalZoneEvents.ToReplay([quest], (_, goal) => goal == "Goal 2", Battlefield));
        Assert.Empty(GoalZoneEvents.ToReplay([quest], (_, _) => false, Battlefield));
        Assert.IsType<ResDropTable>(Assert.Single(quest.m_endResults.m_results));
        Assert.All(quest.m_goals, goal => Assert.Empty(goal.m_completeResults.m_results));
    }

    [Fact]
    public void TheWaveIsNotPostedIntoAnotherZone() {
        Assert.Empty(GoalZoneEvents.ToReplay([Load()], (_, _) => true, "MooShu/MS_War/MS_War_Zone1"));
        Assert.Empty(GoalZoneEvents.ToReplay([Load()], (_, _) => true, ""));
    }
}
