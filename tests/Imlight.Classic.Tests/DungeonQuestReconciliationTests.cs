// CLASSIC: authored dungeon successors use their own acknowledged row/journal/start-registry transaction.
using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(BadgeRulesTests))]
public sealed class DungeonQuestReconciliationTests {
    [Fact]
    public void DungeonAcknowledgementProtectsExistingOwnedOriginalsWithoutIgnoringTheNewStoredQuest() {
        using var f = new TerminalClaimFixture(); var template = Template("QA-DUNGEON-PROTECTED-GRANT");
        var existing = f.Quests[0]; var orphan = TerminalClaimFixture.CloneQuest(existing);
        orphan.ID = 783020; orphan.QuestName = "QA-DUNGEON-PROTECTED-ORPHAN"; orphan.GoalProgress[0].ID = 783021;
        var foreign = TerminalClaimFixture.CloneQuest(existing); foreign.ID = 783022; foreign.OwnerCharId++; foreign.QuestName = "QA-DUNGEON-FOREIGN";
        f.Quests.AddRange([orphan, foreign]); var journal = f.Live.QuestBehavior; QuestInstance? created = null;
        f.OnSave = () => {
            var session = f.Working!; created = session.Quests.Single(row => row.QuestName == template.m_questName);
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(2, session.Ignored.OfType<QuestInstance>().Count());
            Assert.Contains(session.Quests.Single(row => row.ID == existing.ID), session.Ignored);
            Assert.Contains(session.Quests.Single(row => row.ID == orphan.ID), session.Ignored);
            Assert.DoesNotContain(existing, session.Ignored); Assert.DoesNotContain(orphan, session.Ignored);
            Assert.DoesNotContain(session.Quests.Single(row => row.ID == foreign.ID), session.Ignored);
            Assert.DoesNotContain(created, session.Ignored); Assert.DoesNotContain(session.Wizard, session.Ignored);
            Assert.Empty(session.Deleted); Assert.Empty(session.Receipts); Assert.True(created.IsGoalActive("Start"));
            Assert.Contains(created.ID, session.Wizard.QuestBehavior.CurrentQuestIDs); Assert.Single(journal.CurrentQuestIDs);
        };
        Assert.True(Reconcile(f, [template], out var grants)); Assert.Equal(1, f.Saves); Assert.Same(created, Assert.Single(grants).Quest);
        var stored = f.Quests.Single(row => row.QuestName == template.m_questName);
        Assert.Equal(created!.ID, stored.ID); Assert.True(stored.IsGoalActive("Start")); Assert.Contains(stored.ID, f.Saved.QuestBehavior.CurrentQuestIDs);
        Assert.True(f.Quests.Single(row => row.ID == existing.ID).IsGoalActive(TerminalClaimFixture.GoalName));
        Assert.True(f.Quests.Single(row => row.ID == orphan.ID).IsGoalActive(TerminalClaimFixture.GoalName));
        Assert.Same(journal, f.Live.QuestBehavior); Assert.Equal(2, journal.CurrentQuestIDs.Count); Assert.Contains(stored.ID, journal.CurrentQuestIDs);
    }

    [Fact]
    public void FreshOwnedSuccessorRowsJournalAndPersistentStartEntriesCommitInOneSaveBeforePublication() {
        using var f = new TerminalClaimFixture(); var first = Template("QA-DUNGEON-FIRST"); var second = Template("QA-DUNGEON-SECOND");
        first.m_startResults = new() { m_results = [new ResModifyEntry { m_entryName = "QA-DungeonStarted", m_value = 1 },
            new ResPostEvent { m_eventName = "QA-DungeonTransient" }] };
        var prepared = new List<(ulong Quest, ulong Goal)>(); var journal = f.Live.QuestBehavior; var registry = journal.Registry;
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(TerminalClaimFixture.QuestId, Assert.Single(journal.CurrentQuestIDs));
            Assert.False(registry.ContainsKey("QA-DungeonStarted")); Assert.Equal(3, f.Working!.Quests.Count);
            Assert.Equal(3, f.Working.Wizard.QuestBehavior.CurrentQuestIDs.Count); Assert.Equal(1UL, f.Working.Wizard.QuestBehavior.Registry["QA-DungeonStarted"]);
            Assert.All(f.Working.Quests.Where(q => q.ID != TerminalClaimFixture.QuestId), quest => {
                Assert.Equal(TerminalClaimFixture.Character, quest.OwnerCharId); Assert.True(quest.IsGoalActive("Start"));
                Assert.Contains(quest.ID, f.Working.Wizard.QuestBehavior.CurrentQuestIDs);
            });
        };
        IReadOnlyList<IMessage> Prepare(Wizard saved, QuestTemplate template, QuestInstance quest) {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Contains(quest.ID, saved.QuestBehavior.CurrentQuestIDs);
            var goal = Assert.Single(quest.GoalProgress); prepared.Add((quest.ID, goal.ID));
            return [new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDQUEST { QuestID = quest.ID },
                new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL { QuestID = quest.ID, GoalID = goal.ID, GoalNameID = template.m_goals[0].m_goalNameID }];
        }
        Assert.True(ClassicQuestClaims.TryReconcileDungeonQuests(f.Live, [first, second], null!, f.Live.GameObject, null!, out var grants, Prepare));
        Assert.Equal(1, f.Saves); Assert.Equal(2, grants.Count); Assert.Equal(2, prepared.Count); Assert.Empty(f.Receipts); Assert.Equal(0, f.Rolls);
        Assert.Same(journal, f.Live.QuestBehavior); Assert.Same(registry, journal.Registry); Assert.Equal(45UL, registry["LiveUnrelated"]);
        Assert.Equal(1UL, registry["QA-DungeonStarted"]); Assert.Equal(3, journal.CurrentQuestIDs.Count);
        foreach (var grant in grants) {
            Assert.Same(grant.Template, grant.Template == first ? first : second);
            Assert.Equal(grant.Quest.ID, Assert.IsType<QUEST_MESSAGES_52_PROTOCOL.MSG_SENDQUEST>(grant.Messages[0]).QuestID);
            Assert.Equal(grant.Quest.GoalProgress[0].ID, Assert.IsType<QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL>(grant.Messages[1]).GoalID);
        }
        Assert.IsType<ResPostEvent>(Assert.Single(grants[0].TransientStartResults.m_results)); Assert.Empty(grants[1].TransientStartResults.m_results);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PrerequisitesUseFreshSavedCompletionInsteadOfTheStaleAttachedRegistry(bool savedComplete) {
        using var f = new TerminalClaimFixture(); var template = Template("QA-DUNGEON-GATED");
        template.m_requirements = new() { m_requirements = [new ReqEntryValue { m_isQuestRegistry = true,
            m_questName = "QA-PREDECESSOR", m_entryName = "Complete", m_numericValue = 1, m_operatorType = OPERATOR_TYPE.OPERATOR_EQUALS }] };
        f.Saved.QuestBehavior.SetQuestRegistryValue("QA-PREDECESSOR", "Complete", savedComplete ? 1UL : 0UL);
        f.Live.QuestBehavior.SetQuestRegistryValue("QA-PREDECESSOR", "Complete", savedComplete ? 0UL : 1UL);
        Assert.Equal(savedComplete, Reconcile(f, [template], out var grants));
        Assert.Equal(savedComplete ? 1 : 0, f.Saves); Assert.Equal(savedComplete ? 1 : 0, grants.Count);
        Assert.Equal(savedComplete ? 2 : 1, f.Quests.Count); Assert.Equal(901, f.Live.GameStats.m_currentGold);
    }

    [Fact]
    public void AlreadyHeldAndLegacyCompletedSuccessorsNeverRunStartResultsAgain() {
        using var f = new TerminalClaimFixture(); var held = Template("QA-DUNGEON-HELD"); var complete = Template("QA-DUNGEON-COMPLETE");
        Assert.True(Reconcile(f, [held], out var first)); Assert.Single(first); Assert.Equal(1, f.Saves);
        f.Saved.QuestBehavior.SetQuestRegistryValue(complete.m_questName, "Complete", 1);
        f.Dependencies.Prepare = _ => throw new InvalidOperationException("an existing quest cannot prepare another start");
        Assert.False(Reconcile(f, [held, complete], out var replay)); Assert.Empty(replay); Assert.Equal(1, f.Saves);
        Assert.Equal(2, f.Quests.Count); Assert.Equal(0, f.Rolls);
    }

    [Theory]
    [InlineData("preparation-null")] [InlineData("primitive-refused")] [InlineData("unsupported-start-goal")]
    public void PreparationOrUnsupportedStartingGoalRefusesBeforeRowsJournalOrRegistryBecomeDurable(string failure) {
        using var f = new TerminalClaimFixture(); var template = Template("QA-DUNGEON-REFUSED");
        template.m_startResults = new() { m_results = [new ResModifyEntry { m_entryName = "QA-DungeonStarted", m_value = 1 }] };
        if (failure == "primitive-refused") f.Dependencies.Prepare = _ => false;
        if (failure == "unsupported-start-goal") template.m_goals[0].m_activateResults = new() { m_results = [new ResPostEvent { m_eventName = "QA-UnsupportedStart" }] };
        Assert.False(ClassicQuestClaims.TryReconcileDungeonQuests(f.Live, [template], null!, f.Live.GameObject, null!, out var grants,
            (_, _, quest) => failure == "preparation-null" ? null! : [new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDQUEST { QuestID = quest.ID },
                new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL { QuestID = quest.ID, GoalID = quest.GoalProgress[0].ID }]));
        Assert.Empty(grants); Assert.Equal(0, f.Saves); Assert.Single(f.Quests); Assert.Single(f.Saved.QuestBehavior.CurrentQuestIDs);
        Assert.False(f.Saved.QuestBehavior.Registry.ContainsKey("QA-DungeonStarted")); Assert.False(f.Live.QuestBehavior.Registry.ContainsKey("QA-DungeonStarted"));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public void AnOwnedLegacyOrphanIsNeverAdoptedOrDuplicated() {
        using var f = new TerminalClaimFixture(); var template = Template("QA-DUNGEON-ORPHAN");
        var orphan = new QuestInstance(template, TerminalClaimFixture.Character); f.Quests.Add(orphan);
        Assert.False(Reconcile(f, [template], out var grants)); Assert.Empty(grants); Assert.Equal(0, f.Saves);
        Assert.Equal(2, f.Quests.Count); Assert.Single(f.Saved.QuestBehavior.CurrentQuestIDs); Assert.DoesNotContain(orphan.ID, f.Live.QuestBehavior.CurrentQuestIDs);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailedOrDurableLostAckQuarantinesInsideTheLaneAndFreshReloadDoesNotRepeatDurableStarts(bool durable) {
        using var f = new TerminalClaimFixture { FailSave = true, Durable = durable }; var template = Template("QA-DUNGEON-UNKNOWN");
        var disposed = false; f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); disposed = true; };
        Assert.Throws<InvalidOperationException>(() => Reconcile(f, [template], out _)); Assert.True(disposed);
        Assert.Single(f.Live.QuestBehavior.CurrentQuestIDs); Assert.Equal(durable ? 2 : 1, f.Quests.Count); Assert.Equal(1, f.Saves);
        Assert.False(Reconcile(f, [template], out var repeat)); Assert.Empty(repeat); Assert.Equal(1, f.Opened); Assert.Equal(1, f.Saves);
        if (durable) {
            f.OnDispose = null; f.FailSave = false; f.Live = f.Reload();
            Assert.False(Reconcile(f, [template], out var afterReload)); Assert.Empty(afterReload); Assert.Equal(1, f.Saves); Assert.Equal(2, f.Quests.Count);
        }
    }

    [Fact]
    public void AFailureInTheSecondSuccessorDiscardsTheWholePreparedBatch() {
        using var f = new TerminalClaimFixture(); var first = Template("QA-DUNGEON-BATCH-FIRST"); var second = Template("QA-DUNGEON-BATCH-SECOND");
        first.m_startResults = new() { m_results = [new ResModifyEntry { m_entryName = "QA-FirstStarted", m_value = 1 }] };
        second.m_startResults = new() { m_results = [new ResLearnSpell { m_templateID = TerminalClaimFixture.Learned }] };
        Assert.False(Reconcile(f, [first, second], out var grants)); Assert.Empty(grants); Assert.Equal(0, f.Saves);
        Assert.Single(f.Quests); Assert.Single(f.Saved.QuestBehavior.CurrentQuestIDs); Assert.False(f.Saved.QuestBehavior.Registry.ContainsKey("QA-FirstStarted"));
        Assert.False(f.Live.QuestBehavior.Registry.ContainsKey("QA-FirstStarted")); Assert.Empty(f.Saved.SpellbookBehavior.LearnedSpellTemplateIds);
    }

    [Fact]
    public void AChangedSavedZoneRefusesTheStaleAttachedZoneGrant() {
        using var f = new TerminalClaimFixture(); f.Saved.Zone = "QA/Other";
        Assert.False(Reconcile(f, [Template("QA-DUNGEON-ZONE")], out var grants)); Assert.Empty(grants);
        Assert.Equal(0, f.Saves); Assert.Single(f.Quests); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public void AStartReceiptPublicationFailureQuarantinesInsideTheOriginalAckLaneAndCannotRepeatTheStarts() {
        using var f = new TerminalClaimFixture(); var template = Template("QA-DUNGEON-PUBLICATION"); var disposed = false; var callbacks = 0;
        f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); disposed = true; };
        Assert.Throws<InvalidOperationException>(() => ClassicQuestClaims.TryReconcileDungeonQuests(f.Live, [template], null!, f.Live.GameObject, null!, out _,
            (_, _, quest) => [new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDQUEST { QuestID = quest.ID },
                new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL { QuestID = quest.ID, GoalID = quest.GoalProgress[0].ID }],
            grants => {
                Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, f.Saves); Assert.Equal(2, f.Quests.Count);
                Assert.Equal(2, f.Live.QuestBehavior.CurrentQuestIDs.Count); Assert.Single(grants); callbacks++;
                throw new InvalidOperationException("authored acknowledged dungeon receipt failure");
            }));
        Assert.True(disposed); Assert.Equal(1, callbacks); Assert.False(Reconcile(f, [template], out var replay)); Assert.Empty(replay);
        Assert.Equal(1, f.Saves); Assert.Equal(1, f.Opened); f.OnDispose = null; f.Live = f.Reload();
        Assert.False(Reconcile(f, [template], out replay)); Assert.Empty(replay); Assert.Equal(1, f.Saves); Assert.Equal(1, callbacks);
    }

    private static bool Reconcile(TerminalClaimFixture f, IReadOnlyList<QuestTemplate> templates, out IReadOnlyList<DungeonQuestGrant> grants)
        => ClassicQuestClaims.TryReconcileDungeonQuests(f.Live, templates, null!, f.Live.GameObject, null!, out grants,
            (_, _, quest) => [new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDQUEST { QuestID = quest.ID },
                new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL { QuestID = quest.ID, GoalID = quest.GoalProgress[0].ID }]);
    private static QuestTemplate Template(string name) => new() { m_questName = name,
        m_goals = [new PersonaGoalTemplate { m_goalName = "Start", m_goalNameID = 783001, m_goalType = GOAL_TYPE.GOAL_TYPE_PERSONA }],
        m_startGoals = ["Start"], m_startResults = new() { m_results = [] } };
}
