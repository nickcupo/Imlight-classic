// CLASSIC: production trigger/quest types, deterministic stores; never opens PlayerDatabase.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Classic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class PuzzleHelpersServerTests {
    private const string Katz = "Marleybone/MB_ScotlandYard/MB_KatzLab";
    private const string Ironworks = "Marleybone/MB_Station/MB_Ironworks";
    private const string BigBen = "Marleybone/MB_BigBen/MB_BigBen";
    private const string Books = "GH-MAIN-C03-001";
    private static readonly JsonSerializerSettings s_json = new() { TypeNameHandling = TypeNameHandling.Auto, NullValueHandling = NullValueHandling.Ignore };
    private static QuestTemplate BookQuest() => JsonConvert.DeserializeObject<QuestTemplate>(File.ReadAllText(Path.Combine(
        ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates", Books + ".json")), s_json)!;
    private static Dictionary<string, QuestTemplate> Store(QuestTemplate quest) => new(StringComparer.Ordinal) { [Books] = quest };

    // Data identities only: synthetic trigger bodies exercise the production filter and object-plan boundary.
    private static (string Trigger, string Tag, ulong Template)[] Answers(string zone) => zone switch {
        Katz => [
            ("Trigger (1)", "DynaTrigger_MB_Billboard_Sun instance", 4294),
            ("Trigger (2)", "DynaTrigger_MB_Billboard_Bird instance", 4280),
            ("Trigger (3)", "DynaTrigger_MB_Billboard_Tree instance", 4296),
            ("Trigger (4)", "DynaTrigger_MB_Billboard_Bug instance", 4286),
            ("Trigger (5)", "Copy of DynaTrigger_MB_Billboard_Bug instance", 4286),
            ("Trigger (6)", "Copy of DynaTrigger_MB_Billboard_Bird instance", 4280),
            ("Trigger (7)", "Copy of DynaTrigger_MB_Billboard_Sun instance", 4294),
            ("Trigger (8)", "Copy of DynaTrigger_MB_Billboard_Tree instance", 4296),
        ],
        Ironworks => [
            ("Trigger (6)", "DynaTrigger_MB_Plaque_Snake instance", 4323),
            ("Trigger (7)", "DynaTrigger_MB_Plaque_Bug instance", 4317),
            ("Trigger (8)", "DynaTrigger_MB_Plaque_Bird instance", 4314),
        ],
        BigBen => [
            ("Trigger (7)", "DynaTrigger_MB_Plaque_Ice instance", 87116),
            ("Trigger (8)", "DynaTrigger_MB_Plaque_Bird instance", 4314),
            ("Trigger (9)", "DynaTrigger_MB_Plaque_Moon instance", 4319),
            ("Trigger (10)", "DynaTrigger_MB_Plaque_Skull instance", 4321),
        ],
        _ => throw new ArgumentException(zone),
    };

    private static Trigger ObjectTrigger(string name, string tag, ulong template) => new() {
        m_triggerName = name, m_activateEvents = ["StartZone"],
        m_triggerObjInfo = new ClassicTriggerObjectInfo {
            m_templateID = template, m_zoneTag = tag, m_loadingType = LoadingType.DYNAMIC_SERVER, m_startState = "On",
        },
    };
    private static Trigger Control(string name) => new() { m_triggerName = name, m_activateEvents = ["StartZone"] };
    private static List<Trigger> Retained(string zone) => zone switch {
        Katz => [.. Enumerable.Range(1, 4).Select(i => Control($"Trigger Puzzle01 part {i}")),
            .. Enumerable.Range(1, 4).Select(i => Control($"Trigger Puzzle02 part {i}")), Control("ControlOptionalPuzzleClues")],
        Ironworks => [
            ObjectTrigger("BillboardBirdInstance", "DynaTrigger_MB_Billboard_Bird instance", 4280),
            ObjectTrigger("BillboardBugTrigger", "DynaTrigger_MB_Billboard_Bug instance", 4286),
            ObjectTrigger("BillboardMoonTrigger", "DynaTrigger_MB_Billboard_Moon instance", 4288),
            ObjectTrigger("BillboardTreeTrigger", "DynaTrigger_MB_Billboard_Tree instance (1)", 4296),
            ObjectTrigger("BillboardBird2Trigger", "BillboardBird2", 4280),
            ObjectTrigger("BillboardMoon2Trigger", "BillboardMoon2", 4288),
            ObjectTrigger("BillboardTree2Trigger", "BillboardTree2", 4296),
            ObjectTrigger("BillboardBug2Trigger", "BillboardBug2", 4286),
            .. Enumerable.Range(1, 8).Select(i => Control($"Symbol{i}")),
        ],
        BigBen => [
            ObjectTrigger("IcePlaqueShow", "IcePlaque", 87116),
            ObjectTrigger("BirdPlaque1Trigger", "BirdPlaque1", 4314),
            ObjectTrigger("SkullPlaque1Trigger", "SkullPlaque1", 4321),
            ObjectTrigger("MoonPlaqueTrigger1", "MoonPlaque1", 4319),
            Control("HintTrigger"), Control("PlateCheck"), Control("PullLever2"),
        ],
        _ => throw new ArgumentException(zone),
    };
    private static WizZoneTriggers Triggers(PuzzleHelpers policy, string zone) {
        var answers = Answers(zone).ToDictionary(a => a.Trigger, StringComparer.Ordinal);
        return new() {
            m_triggers = [.. policy.Zones.Single(z => z.Zone == zone).RemoveTriggers.Select(name =>
                answers.TryGetValue(name, out var answer) ? ObjectTrigger(name, answer.Tag, answer.Template) : Control(name)),
                .. Retained(zone)],
        };
    }

    [Theory]
    [InlineData(Katz, 8)][InlineData(Ironworks, 3)][InlineData(BigBen, 4)]
    public void FilteringBeforePlanningRemovesRepeatAnswersAndPreservesOriginalCluesAndPuzzle(string zone, int answerCount) {
        var policy = PuzzleHelpersTests.Real();
        var original = Triggers(policy, zone);
        var targets = policy.Zones.Single(z => z.Zone == zone).RemoveTriggers;
        var retained = original.m_triggers.Where(t => !targets.Contains((string)t!.m_triggerName)).ToArray();
        var originalCount = original.m_triggers.Count;
        var filtered = ClassicPuzzleHelpers.FilterTriggers(PuzzleHelpers.OctoberProfile, policy, zone, original)!;
        Assert.NotSame(original, filtered);
        Assert.Equal(originalCount, original.m_triggers.Count); // Cached input retains every trigger and object.
        Assert.Equal(retained.Length, filtered.m_triggers.Count);
        for (var i = 0; i < retained.Length; i++) Assert.Same(retained[i], filtered.m_triggers[i]);
        var plan = ZoneTriggerPlans.Build(zone, [.. filtered.m_triggers.Where(t => t is not null)!], new WizZoneVolumes(), null, []);
        var tags = Answers(zone).Select(a => a.Tag).ToArray();
        Assert.Equal(answerCount, tags.Length);
        var spawnedTags = plan.TriggerObjectsToSpawn().Select(info => (string)info.m_zoneTag).ToArray();
        Assert.DoesNotContain(spawnedTags, tag => tags.Contains(tag, StringComparer.Ordinal));
        foreach (var trigger in retained) {
            Assert.Contains(plan.Triggers, t => ReferenceEquals(t, trigger));
            if (trigger!.m_triggerObjInfo is ClassicTriggerObjectInfo info) Assert.Contains((string)info.m_zoneTag, spawnedTags);
        }
        Assert.Same(filtered, ClassicPuzzleHelpers.FilterTriggers(PuzzleHelpers.OctoberProfile, policy, zone, filtered));
    }

    [Theory]
    [InlineData("late-2009", Katz)][InlineData("late-2009", Ironworks)][InlineData("late-2009", BigBen)]
    [InlineData("arc1-2009h1", Katz)][InlineData("arc1-2009h1", Ironworks)][InlineData("arc1-2009h1", BigBen)]
    [InlineData("dev-unrestricted", Katz)][InlineData("dev-unrestricted", Ironworks)][InlineData("dev-unrestricted", BigBen)]
    [InlineData("child-of-october", Katz)][InlineData("child-of-october", Ironworks)][InlineData("child-of-october", BigBen)]
    public void OtherProfilesKeepAllThreeZonesOriginalData(string profile, string zone) {
        var policy = PuzzleHelpersTests.Real();
        var data = Triggers(policy, zone);
        Assert.Same(data, ClassicPuzzleHelpers.FilterTriggers(profile, policy, zone, data));
    }

    [Theory]
    [InlineData(Katz, "Marleybone/MB_ScotlandYard/Interiors/MB_KatzLab_T5", "GateHintTrigger")]
    [InlineData(Katz, "Marleybone/MB_ScotlandYard/Interiors/MB_KatzLab_T6", "QuestHint")]
    [InlineData(Ironworks, "Marleybone/MB_Station/Interiors/MB_Ironworks_T1", "ShowPlaques")]
    [InlineData(BigBen, "Marleybone/MB_BigBen/MB_CounterweightEast", "HintTrigger")]
    [InlineData(BigBen, "Marleybone/MB_BigBen/MB_CounterweightWest", "HintText")]
    [InlineData(Ironworks, "WizardCity/WC_Hub", "Unrelated fixture trigger")]
    public void OriginalBossRevealsAndUnrelatedZonesKeepEvenIdenticallyNamedAnswers(string sourceZone, string zone, string reveal) {
        var policy = PuzzleHelpersTests.Real();
        var data = Triggers(policy, sourceZone);
        var clue = Control(reveal);
        data.m_triggers.Add(clue);
        Assert.Same(data, ClassicPuzzleHelpers.FilterTriggers(PuzzleHelpers.OctoberProfile, policy, zone, data));
        Assert.Same(clue, data.m_triggers.Last());
    }

    [Theory]
    [InlineData(Katz, "partial")][InlineData(Katz, "duplicate")][InlineData(Katz, "missing")]
    [InlineData(Ironworks, "partial")][InlineData(Ironworks, "duplicate")][InlineData(Ironworks, "missing")]
    [InlineData(BigBen, "partial")][InlineData(BigBen, "duplicate")][InlineData(BigBen, "missing")]
    public void TriggerMismatchIsRejectedWithoutMutatingTheInput(string zone, string mismatch) {
        var policy = PuzzleHelpersTests.Real();
        var data = Triggers(policy, zone);
        if (mismatch == "partial") data.m_triggers.RemoveAt(0);
        if (mismatch == "duplicate") data.m_triggers.Add(data.m_triggers[0]);
        var count = data.m_triggers.Count;
        Assert.Throws<PuzzleHelperApplicationException>(() => ClassicPuzzleHelpers.FilterTriggers(
            PuzzleHelpers.OctoberProfile, policy, zone, mismatch == "missing" ? null : data));
        Assert.Equal(count, data.m_triggers.Count);
    }

    [Fact]
    public void OnlySevenBookGoalFlagsChangeAndASeparateLegacyLoadKeepsAuthoredValues() {
        var policy = PuzzleHelpersTests.Real();
        var quest = BookQuest();
        var expected = JObject.FromObject(quest);
        ClassicPuzzleHelpers.ApplyQuestHelpers(PuzzleHelpers.OctoberProfile, policy, Store(quest));
        foreach (var goal in expected["m_goals"]!) {
            if (policy.QuestHelpers.Single().Goals.Contains((string)goal["m_goalName"]!)) goal["m_noQuestHelper"] = true;
        }
        Assert.True(JToken.DeepEquals(expected, JObject.FromObject(quest))); // No objective, goal-zone, text, hand-in or quest-wide edits.
        Assert.False(quest.m_noQuestHelper);
        Assert.False(quest.m_goals.Single(g => (string)g.m_goalName == "8_WizardQuestGoals_TalkNPC").m_noQuestHelper);
        ClassicPuzzleHelpers.ApplyQuestHelpers(PuzzleHelpers.OctoberProfile, policy, Store(quest)); // Idempotent.
        var older = BookQuest();
        ClassicPuzzleHelpers.ApplyQuestHelpers("late-2009", policy, Store(older));
        Assert.All(older.m_goals, g => Assert.False(g.m_noQuestHelper));
    }

    [Theory]
    [InlineData("missing_quest")][InlineData("missing_goal")][InlineData("duplicate_goal")][InlineData("wrong_type")][InlineData("later_bad_quest")]
    public void AllQuestTargetsValidateBeforeAnyGoalIsChanged(string mismatch) {
        var policy = PuzzleHelpersTests.Real();
        var quest = BookQuest();
        var store = Store(quest);
        if (mismatch == "missing_quest") store.Clear();
        if (mismatch == "missing_goal") quest.m_goals.RemoveAt(6);
        if (mismatch == "duplicate_goal") quest.m_goals.Add(quest.m_goals[0]);
        if (mismatch == "wrong_type") quest.m_goals[6].m_goalType = GOAL_TYPE.GOAL_TYPE_PERSONA;
        if (mismatch == "later_bad_quest") policy = policy with { QuestHelpers = [.. policy.QuestHelpers,
            new PuzzleQuestHelpers("Absent", ["AbsentGoal"], true, "gh-books-2009")] };
        Assert.Throws<PuzzleHelperApplicationException>(() => ClassicPuzzleHelpers.ApplyQuestHelpers(PuzzleHelpers.OctoberProfile, policy, store));
        Assert.All(quest.m_goals, g => Assert.False(g.m_noQuestHelper));
    }

    [Fact]
    public void MissingConfiguredPolicyFailsProgressionInitialization()
        => Assert.Throws<ClassicDataException>(() => ClassicProgression.Initialize(PuzzleHelpersTests.Profile(PuzzleHelpers.OctoberProfile),
            Path.Combine(Path.GetTempPath(), "w101c-missing-policy-" + Guid.NewGuid().ToString("N"))));
}
