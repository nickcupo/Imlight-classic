// CLASSIC: native tutorial packets are fully admitted before effects, with exact held/prospective selection.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(BadgeRulesTests))] // Configuration and tutorial actor fixtures share this serial collection.
public sealed class TutorialCommandAdmissionTests {
    private const ulong Character = (1UL << 40) + 801, QuestA = (1UL << 48) + 101, QuestB = (1UL << 48) + 201;
    private const string A = "AuthoredTutorialA", B = "AuthoredTutorialB", Evil = "AuthoredOrdinaryQuest", Goal = "AuthoredSharedGoal";
    private static readonly TutorialCommandVocabulary Authored = new([A, B], [A, B], [(A, Goal), (B, Goal)],
        [(A, "AuthoredNoOp", TutorialGoalKind.NoOp), (B, "AuthoredSkip", TutorialGoalKind.Skip)], ["AuthoredEvent"], [3]);

    public TutorialCommandAdmissionTests()
        // As in TerminalClaimFixture, initialize before the first WizardCollection static hook/guard access.
        => EquipmentAttachConcurrencyTests.Configure("[Character]\nMaxInventoryItems=2\nPetEnergyTickInSeconds=60\n[Classic]\nBackpackSize=2\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");

    [Fact]
    public void SelectedHeldGoalBindsExactFullIdentitiesTemplatesAndAliasesWithoutChangingProgress() {
        var f = new Fixture(); var signature = Signature(f.Wizard);
        Assert.True(f.Admit(Command(add: A, goal: Goal), out var result));
        Assert.Equal(TutorialGoalKind.QuestGoal, result.Goal.Kind); Assert.False(result.Goal.Prospective);
        Assert.Same(f.HeldA, result.ExistingAddQuest); Assert.Same(f.HeldA, result.Goal.Quest);
        Assert.Same(f.HeldA.GoalProgress[0], result.Goal.Goal); Assert.Same(f.TemplateA, result.Goal.QuestTemplate);
        Assert.Same(f.TemplateA.m_goals[0], result.Goal.GoalTemplate);
        Assert.Equal(QuestA, result.Goal.QuestId); Assert.Equal(QuestA + 1, result.Goal.GoalId);
        Assert.Equal(-1, result.Goal.Goal.CurrentProgress); Assert.Equal(signature, Signature(f.Wizard));
        Assert.True(TutorialCommandAdmission.TryResolveAcknowledgedGoal(f.Wizard, result.Goal, out var selected, f.Resolve));
        Assert.Same(result.Goal.Quest, selected.Quest); Assert.Same(result.Goal.Goal, selected.Goal);
    }

    [Fact]
    public void QualifiedQuestSelectsItsGoalEvenWhenAnEarlierOrdinaryQuestHasTheSameGoalName() {
        var f = new Fixture(); f.Add(Quest(Evil, QuestB, f.TemplateB));
        f.Wizard.QuestBehavior.CurrentQuestInstances.Reverse();
        Assert.True(f.Admit(Command(add: A, goal: Goal), out var result));
        Assert.Same(f.HeldA, result.Goal.Quest);
        Assert.False(f.Admit(Command(goal: Goal), out _));
    }

    [Fact]
    public void BareNormalGoalRequiresOneUniqueAuthorizedHeldQuestAndGoal() {
        var f = new Fixture(); Assert.True(f.Admit(Command(goal: Goal), out var result)); Assert.Same(f.HeldA, result.Goal.Quest);
        f.Add(Quest(B, QuestB, f.TemplateB)); Assert.False(f.Admit(Command(goal: Goal), out _));
        Assert.True(f.Admit(Command(add: A, goal: Goal), out result)); Assert.Same(f.HeldA, result.Goal.Quest);
    }

    [Fact]
    public void AddAndGoalCanSelectAProspectiveTemplateWithoutAllocatingOrPublishingAnInstance() {
        var f = new Fixture(); var signature = Signature(f.Wizard);
        Assert.True(f.Admit(Command(add: B, goal: Goal), out var result));
        Assert.True(result.Goal.Prospective); Assert.Null(result.Goal.Quest); Assert.Null(result.Goal.Goal);
        Assert.Same(f.TemplateB, result.AddTemplate); Assert.Same(f.TemplateB.m_goals[0], result.Goal.GoalTemplate);
        Assert.Equal(signature, Signature(f.Wizard));
        Assert.False(TutorialCommandAdmission.TryResolveAcknowledgedGoal(f.Wizard, result.Goal, out _, f.Resolve));
        var acknowledged = Quest(B, QuestB, f.TemplateB); f.Add(acknowledged);
        Assert.True(TutorialCommandAdmission.TryResolveAcknowledgedGoal(f.Wizard, result.Goal, out var selected, f.Resolve));
        Assert.False(selected.Prospective); Assert.Same(acknowledged, selected.Quest); Assert.Same(acknowledged.GoalProgress[0], selected.Goal);
    }

    [Theory]
    [InlineData("quest-id")] [InlineData("goal-id")] [InlineData("quest-alias")] [InlineData("goal-alias")]
    [InlineData("template-alias")] [InlineData("goal-type")]
    public void AcknowledgedResolutionRejectsChangedIdentityAliasOrTemplate(string change) {
        var f = new Fixture(); Assert.True(f.Admit(Command(add: A, goal: Goal), out var result));
        if (change == "quest-id") { f.HeldA.ID += 9; f.Wizard.QuestBehavior.CurrentQuestIDs = [f.HeldA.ID]; }
        if (change == "goal-id") f.HeldA.GoalProgress[0].ID += 9;
        if (change == "quest-alias") f.Wizard.QuestBehavior.CurrentQuestInstances = [Quest(A, QuestA, f.TemplateA)];
        if (change == "goal-alias") f.HeldA.GoalProgress = [new GoalInstance { ID = QuestA + 1, OwnerCharId = Character, GoalName = Goal, GoalType = GOAL_TYPE.GOAL_TYPE_PERSONA }];
        if (change == "template-alias") f.Templates[0] = Template(A);
        if (change == "goal-type") { f.HeldA.GoalProgress[0].GoalType = (GOAL_TYPE)999; f.TemplateA.m_goals[0].m_goalType = (GOAL_TYPE)999; }
        Assert.False(TutorialCommandAdmission.TryResolveAcknowledgedGoal(f.Wizard, result.Goal, out _, f.Resolve));
    }

    [Theory]
    [InlineData("missing-template")] [InlineData("duplicate-template")] [InlineData("case-template")]
    [InlineData("missing-goal")] [InlineData("duplicate-goal")] [InlineData("wrong-type")]
    [InlineData("null-goals")] [InlineData("null-starts")] [InlineData("dangling-start")]
    public void TemplateAndGoalIdentityMustResolveBeforeAnyMutation(string corruption) {
        var f = new Fixture(); var before = Signature(f.Wizard);
        if (corruption == "missing-template") f.Templates.Clear();
        if (corruption == "duplicate-template") f.Templates.Add(Template(A));
        if (corruption == "case-template") f.Templates.Add(Template(A.ToLowerInvariant()));
        if (corruption == "missing-goal") f.TemplateA.m_goals[0].m_goalName = "DifferentGoal";
        if (corruption == "duplicate-goal") f.TemplateA.m_goals.Add(new PersonaGoalTemplate { m_goalName = Goal, m_goalType = GOAL_TYPE.GOAL_TYPE_PERSONA });
        if (corruption == "wrong-type") f.TemplateA.m_goals[0].m_goalType = (GOAL_TYPE)999;
        if (corruption == "null-goals") f.TemplateA.m_goals = null!;
        if (corruption == "null-starts") f.TemplateA.m_startGoals = null!;
        if (corruption == "dangling-start") f.TemplateA.m_startGoals = ["NotInTemplate"];
        Assert.False(f.Admit(Command(add: A, goal: Goal), out var result)); Assert.Null(result);
        Assert.Equal(before, Signature(f.Wizard));
    }

    [Theory]
    [InlineData("character")] [InlineData("native-character")] [InlineData("native-permanent")]
    [InlineData("quest-owner")] [InlineData("goal-owner")] [InlineData("quest-id")]
    [InlineData("goal-id")] [InlineData("goal-id-collision")] [InlineData("duplicate-journal")]
    [InlineData("missing-journal")] [InlineData("stale-journal")]
    [InlineData("duplicate-quest-name")] [InlineData("duplicate-goal-name")] [InlineData("uncertain")]
    public void MalformedHeldIdentityOrJournalCannotBeUsedForTutorialAuthorization(string corruption) {
        var f = new Fixture();
        if (corruption == "character") f.Wizard.CharId = 0;
        if (corruption == "native-character") f.Wizard.GameObject.m_characterId = Character + 1;
        if (corruption == "native-permanent") f.Wizard.GameObject.m_permID = Character + 99;
        if (corruption == "quest-owner") f.HeldA.OwnerCharId++;
        if (corruption == "goal-owner") f.HeldA.GoalProgress[0].OwnerCharId++;
        if (corruption == "quest-id") f.HeldA.ID = 0;
        if (corruption == "goal-id") f.HeldA.GoalProgress[0].ID = 0;
        if (corruption == "goal-id-collision") { var other = Quest(B, QuestB, f.TemplateB); other.GoalProgress[0].ID = QuestA + 1; f.Add(other); }
        if (corruption == "duplicate-journal") f.Wizard.QuestBehavior.CurrentQuestIDs.Add(QuestA);
        if (corruption == "missing-journal") f.Wizard.QuestBehavior.CurrentQuestIDs.Clear();
        if (corruption == "stale-journal") f.Wizard.QuestBehavior.CurrentQuestIDs.Add(QuestB);
        if (corruption == "duplicate-quest-name") f.Add(Quest(A, QuestB, f.TemplateA));
        if (corruption == "duplicate-goal-name") f.HeldA.GoalProgress = [.. f.HeldA.GoalProgress, new GoalInstance { ID = QuestB + 9, OwnerCharId = Character, GoalName = Goal, GoalType = GOAL_TYPE.GOAL_TYPE_PERSONA }];
        if (corruption == "uncertain") WizardCollection.MarkInventorySnapshotUncertain(f.Wizard);
        var before = Signature(f.Wizard); Assert.False(f.Admit(Command(add: A, goal: Goal), out _));
        Assert.Equal(before, Signature(f.Wizard));
    }

    [Theory]
    [InlineData("add")] [InlineData("remove")] [InlineData("goal")] [InlineData("event")] [InlineData("action")]
    public void ARejectedLaterFieldRejectsTheWholePackedCommandBeforeAnEarlierAdd(string invalid) {
        var f = new Fixture(); var command = Command(add: B, goal: Goal, eventName: "AuthoredEvent", stage: 3);
        if (invalid == "add") command.QuestToAdd = Evil;
        if (invalid == "remove") command.QuestToRemove = Evil;
        if (invalid == "goal") command.GoalToComplete = "NotAnAuthorizedGoal";
        if (invalid == "event") command.EventToPost = "UnauthorizedEvent";
        if (invalid == "action") command.Action = "UnauthorizedAction";
        var before = Signature(f.Wizard); Assert.False(f.Admit(command, out var decision)); Assert.Null(decision);
        Assert.Equal(before, Signature(f.Wizard)); Assert.DoesNotContain(f.Wizard.QuestBehavior.CurrentQuestInstances, quest => quest.QuestName == B);
    }

    [Fact]
    public void MessageFieldsAreCapturedAndLaterCallerMutationCannotChangeAnAdmittedDecision() {
        var f = new Fixture(); var command = Command(add: B, goal: Goal, remove: A, eventName: "AuthoredEvent", stage: 3);
        Assert.True(f.Admit(command, out var decision)); command.QuestToAdd = Evil; command.GoalToComplete = "Bad"; command.Value = 999;
        Assert.Equal(B, decision.QuestToAdd); Assert.Equal(A, decision.QuestToRemove); Assert.Equal(Goal, decision.Goal.GoalName);
        Assert.Equal("AuthoredEvent", decision.EventToPost); Assert.Equal("Stage", decision.Action); Assert.Equal(3, decision.Value);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(8)]
    public void NativeStageVocabularyIsAdmittedWithoutQuestChanges(int stage) {
        var wizard = EmptyWizard(); Assert.True(TutorialCommandAdmission.TryPreflight(wizard, Command(stage: stage), out var result, _ => []));
        Assert.Equal(TutorialGoalKind.None, result.Goal.Kind); Assert.Equal(stage, result.Value);
    }

    [Theory]
    [InlineData(0)] [InlineData(7)] [InlineData(9)] [InlineData(13)] [InlineData(99)] [InlineData(int.MinValue)] [InlineData(int.MaxValue)]
    public void UnsentNativeStageValuesAreRejected(int stage)
        => Assert.False(TutorialCommandAdmission.TryPreflight(EmptyWizard(), Command(stage: stage), out _, _ => []));

    [Theory]
    [InlineData("WalkAmbrose")] [InlineData("DespawnA")] [InlineData("WandFX")]
    [InlineData("DespawnM")] [InlineData("DespawnAmbrose")]
    public void TrustedInternalEventNamesDoNotAuthorizeClientEventToPost(string name) {
        var loads = 0;
        Assert.False(TutorialCommandAdmission.TryPreflight(EmptyWizard(), Command(add: "WC-TUT-C09-014", eventName: name, stage: 6),
            out _, _ => { loads++; return []; }));
        Assert.Equal(0, loads);
    }

    [Theory]
    [InlineData("WC-TUT-C03-001")] [InlineData("WC-TUT-C03-002")] [InlineData("WC-TUT-C05-001")]
    [InlineData("WC-TUT-C08-001")] [InlineData("WC-TUT-C09-014")] [InlineData("WC-TUT-C09-016")] [InlineData("Tutorial_Intro")]
    public void NativeQuestAllowlistSupportsAddAndAbsentRemoval(string name) {
        var template = Template(name); var wizard = EmptyWizard();
        Assert.True(TutorialCommandAdmission.TryPreflight(wizard, Command(add: name, remove: name), out var decision, _ => [template]));
        Assert.Same(template, decision.AddTemplate); Assert.Null(decision.ExistingAddQuest); Assert.Null(decision.ExistingRemoveQuest);
        Assert.True(TutorialCommandAdmission.TryResolveHeldQuest(wizard, name, out var absent)); Assert.Null(absent);
        Assert.Empty(wizard.QuestBehavior.CurrentQuestIDs);
    }

    [Theory]
    [InlineData("WC-TUT-C02-001")] [InlineData("WC-TUT-C04-001")] [InlineData("WC-TUT-C06-001")] [InlineData("WC-S01-001")]
    public void DisconnectedTutorialScriptsAndOrdinaryQuestsAreOutsideNativeAdmission(string name) {
        var template = Template(name); var wizard = EmptyWizard();
        Assert.False(TutorialCommandAdmission.TryPreflight(wizard, Command(add: name), out _, _ => [template]));
        Assert.False(TutorialCommandAdmission.TryPreflight(wizard, Command(remove: name), out _, _ => [template]));
    }

    [Theory]
    [InlineData("WC-TUT-C05-001", "StopRain", (int)TutorialGoalKind.NoOp)]
    [InlineData("WC-TUT-C05-001", "Transplant Player", (int)TutorialGoalKind.Cinematic)]
    [InlineData("WC-TUT-C05-001", "Despawn Malistaire", (int)TutorialGoalKind.Cinematic)]
    [InlineData("WC-TUT-C05-001", "Trigger Wand Effect", (int)TutorialGoalKind.Cinematic)]
    [InlineData("WC-TUT-C05-001", "Despawn Ambrose Inside", (int)TutorialGoalKind.Cinematic)]
    [InlineData("WC-TUT-C08-001", "SkipTutorialGoal", (int)TutorialGoalKind.Skip)]
    [InlineData("WC-TUT-C08-001", "Teleport", (int)TutorialGoalKind.Finale)]
    public void SpecialBeatsHaveExactNativePairsAndDoNotInventMissingTemplateGoals(string quest, string goal, int kind) {
        var wizard = EmptyWizard(); var template = new QuestTemplate { m_questName = quest, m_goals = [], m_startGoals = [] };
        Assert.True(TutorialCommandAdmission.TryPreflight(wizard, Command(add: quest, goal: goal), out var result, _ => [template]));
        Assert.Equal((TutorialGoalKind)kind, result.Goal.Kind); Assert.Equal(quest, result.Goal.QuestName); Assert.Null(result.Goal.GoalTemplate);
        Assert.True(TutorialCommandAdmission.TryPreflight(wizard, Command(goal: goal), out result, _ => []));
        Assert.Equal((TutorialGoalKind)kind, result.Goal.Kind); Assert.Equal(quest, result.Goal.QuestName);
        Assert.False(TutorialCommandAdmission.TryPreflight(wizard, Command(add: "Tutorial_Intro", goal: goal), out _, _ => [Template("Tutorial_Intro")]));
    }

    [Theory]
    [InlineData("ConfigurePlayer")] [InlineData("TypoGoal")] [InlineData("Stoprain")]
    public void UnprovedUnknownAndDifferentlyCasedGoalsAreNotBlanketCinematicSuccesses(string goal)
        => Assert.False(TutorialCommandAdmission.TryPreflight(EmptyWizard(), Command(goal: goal), out _, _ => []));

    [Fact]
    public void IntroCanBePreparedProspectivelyThenResolvedOnlyToItsAcknowledgedHeldGoal() {
        var wizard = EmptyWizard(); var template = Template("Tutorial_Intro", "OnlyGoal");
        Assert.True(TutorialCommandAdmission.TryPreflight(wizard, Command(add: "Tutorial_Intro", goal: "OnlyGoal"), out var result, _ => [template]));
        Assert.True(result.Goal.Prospective); Assert.Empty(wizard.QuestBehavior.CurrentQuestIDs);
        var acknowledged = Quest("Tutorial_Intro", QuestA, template); wizard.QuestBehavior.AddQuest(acknowledged);
        Assert.True(TutorialCommandAdmission.TryResolveAcknowledgedGoal(wizard, result.Goal, out var selected, _ => [template]));
        Assert.Same(acknowledged, selected.Quest); Assert.Same(acknowledged.GoalProgress[0], selected.Goal);
    }

    private static GAME_5_PROTOCOL.MSG_SERVERTUTORIALCOMMAND Command(string add = "", string goal = "", string remove = "", string eventName = "", int? stage = null)
        => new() { QuestToAdd = add, GoalToComplete = goal, QuestToRemove = remove, EventToPost = eventName, Action = stage is null ? "" : "Stage", Value = stage ?? 0 };
    private static QuestTemplate Template(string name, string goal = Goal) => new() { m_questName = name,
        m_goals = [new PersonaGoalTemplate { m_goalName = goal, m_goalType = GOAL_TYPE.GOAL_TYPE_PERSONA }], m_startGoals = [] };
    private static QuestInstance Quest(string name, ulong id, QuestTemplate template) => new() { ID = id, QuestName = name,
        OwnerCharId = Character, GoalProgress = [new GoalInstance { ID = id + 1, OwnerCharId = Character,
            GoalName = template.m_goals[0].m_goalName, GoalType = template.m_goals[0].m_goalType }] };
    private static Wizard EmptyWizard() => new() { CharId = Character, QuestBehavior = new ServerQuestBehavior() };
    private static string Signature(Wizard wizard) => JsonSerializer.Serialize(new { wizard.CharId,
        IDs = wizard.QuestBehavior.CurrentQuestIDs, Quests = wizard.QuestBehavior.CurrentQuestInstances });
    private sealed class Fixture {
        internal readonly Wizard Wizard = EmptyWizard();
        internal readonly QuestTemplate TemplateA = Template(A), TemplateB = Template(B);
        internal readonly QuestInstance HeldA;
        internal readonly List<QuestTemplate> Templates;
        internal Fixture() { HeldA = Quest(A, QuestA, TemplateA); Templates = [TemplateA, TemplateB]; Add(HeldA); }
        internal void Add(QuestInstance quest) {
            Wizard.QuestBehavior.CurrentQuestInstances.Add(quest); Wizard.QuestBehavior.CurrentQuestIDs.Add(quest.ID);
        }
        internal IEnumerable<QuestTemplate> Resolve(string _) => Templates;
        internal bool Admit(GAME_5_PROTOCOL.MSG_SERVERTUTORIALCOMMAND command, out TutorialCommandDecision result)
            => TutorialCommandAdmission.TryPreflightForTests(Wizard, command, Authored, out result, Resolve);
    }
}
