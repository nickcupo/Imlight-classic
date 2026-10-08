// CLASSIC: authored terminal claims exercise the real tracked transaction and its single ACK boundary.
using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.Classic.Spells;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Xunit;
using Action = System.Action;
using Type = System.Type;

namespace Imlight.Classic.Tests;

[Collection(nameof(BadgeRulesTests))]
public sealed class TerminalQuestClaimPersistenceTests {
    [Theory]
    [InlineData(600, 500, 30, 600, 0)]
    [InlineData(500, 500, 30, 500, 0)]
    [InlineData(490, 500, 30, 500, 10)]
    [InlineData(50, 0, int.MaxValue, 50, 0)]
    [InlineData(int.MaxValue, int.MaxValue - 10, int.MaxValue, int.MaxValue, 0)]
    [InlineData(int.MaxValue - 3, int.MaxValue, int.MaxValue, int.MaxValue, 3)]
    [InlineData(0, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue)]
    public void TerminalQuestGoldPreservesSavedHoldingsAndAdvertisesOnlyAcknowledgedHeadroom(
        int before, int pouch, int requested, int balance, int acquired) {
        using var f = new TerminalClaimFixture(); ConfigureNativeGoldPreparation(f);
        f.Saved.GameStats.m_currentGold = before; f.Saved.GameStats.m_baseGoldPouch = pouch;
        f.Reward.GoldAmount = requested;
        var stats = f.Live.GameStats; var journal = f.Live.QuestBehavior; var published = 0;
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(901, stats.m_currentGold);
            Assert.True(f.Expected.IsGoalActive(TerminalClaimFixture.GoalName)); Assert.Equal(0, published);
            Assert.Equal(balance, f.Working!.Wizard.GameStats.m_currentGold);
            Assert.Single(f.Working.Deleted); Assert.Single(f.Working.Receipts);
            Assert.Equal(balance, Assert.Single(f.Prepared.OfType<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>()).Gold);
        };
        f.AfterCommit = claim => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, f.Saves); published++;
            Assert.Equal(acquired, claim.Receipt.Gold);
            Assert.True(journal.HasCompletedQuest(TerminalClaimFixture.QuestName));
        };
        Assert.Equal(QuestClaimStatus.Committed, f.Claim(out var claim));
        Assert.Equal(1, f.Saves); Assert.Equal(1, f.Rolls); Assert.Equal(1, published);
        Assert.Equal(balance, f.Saved.GameStats.m_currentGold); Assert.Equal(pouch, f.Saved.GameStats.m_baseGoldPouch);
        // A zero gain keeps the existing no-live-write behavior, while its packet reports the fresh wallet.
        Assert.Equal(acquired == 0 ? 901 : balance, stats.m_currentGold);
        Assert.Same(stats, f.Live.GameStats); Assert.Same(journal, f.Live.QuestBehavior);
        Assert.Empty(f.Quests); Assert.Empty(f.Saved.QuestBehavior.CurrentQuestIDs);
        Assert.True(f.Saved.QuestBehavior.HasCompletedQuest(TerminalClaimFixture.QuestName));
        Assert.Equal(acquired, claim.Receipt.Gold); Assert.Equal(acquired, Assert.Single(f.Receipts).Value.Gold);
        Assert.Equal(TerminalClaimFixture.GoalId, Assert.Single(claim.Receipt.CompletedGoalIds));
        var update = Assert.Single(claim.GoalActions.Select(action => action.Message).OfType<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>());
        Assert.Equal(balance, update.Gold); Assert.Equal(pouch, update.MaxGold); Assert.Empty(claim.EndActions);
        Assert.Equal(acquired == 0 ? Array.Empty<int>() : new[] { acquired }, DecodeGoldPopups(claim.GoalActions));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        Assert.Equal(QuestClaimStatus.AlreadyClaimed, f.Claim(out var replay)); Assert.Null(replay);
        Assert.Equal(1, f.Saves); Assert.Equal(1, f.Rolls); Assert.Equal(1, published);
    }

    [Theory]
    [InlineData(490, 500, 497, 500, 10, 7, 3)]
    [InlineData(600, 500, 600, 600, 0, 0, 0)]
    [InlineData(int.MaxValue - 10, int.MaxValue, int.MaxValue - 3, int.MaxValue, 10, 7, 3)]
    public void TerminalQuestGoldAllocatesGoalAndEndSourcesAgainstOneFreshWalletInOrder(
        int before, int pouch, int firstBalance, int finalBalance, int acquired, int firstGold, int secondGold) {
        using var f = new TerminalClaimFixture(); ConfigureNativeGoldPreparation(f);
        f.Saved.GameStats.m_currentGold = before; f.Saved.GameStats.m_baseGoldPouch = pouch;
        f.Goal.m_completeResults.m_results.Add(new ResDropTable { m_tableName = "QA-SECOND-GOAL-REWARD" });
        f.Template.m_endResults.m_results = [new ResDropTable { m_tableName = "QA-END-REWARD" }];
        var rolled = new List<string>();
        f.Dependencies.RollQuestReward = (table, _) => {
            f.Rolls++; rolled.Add(table);
            return new() { GoldAmount = table == "QA-GOAL-REWARD" ? 7 : table == "QA-SECOND-GOAL-REWARD" ? 11 : 13 };
        };
        var published = 0;
        f.OnSave = () => {
            Assert.Equal(0, published); Assert.Equal(901, f.Live.GameStats.m_currentGold);
            Assert.True(f.Expected.IsGoalActive(TerminalClaimFixture.GoalName));
            Assert.Equal(finalBalance, f.Working!.Wizard.GameStats.m_currentGold);
        };
        f.AfterCommit = claim => { Assert.Equal(1, f.Saves); Assert.True(WizardCollection.HoldsWriteLane); published++; };
        Assert.Equal(QuestClaimStatus.Committed, f.Claim(out var claim));
        Assert.Equal(new[] { "QA-GOAL-REWARD", "QA-SECOND-GOAL-REWARD", "QA-END-REWARD" }, rolled);
        Assert.Equal(1, f.Saves); Assert.Equal(3, f.Rolls); Assert.Equal(1, published);
        Assert.Equal(finalBalance, f.Saved.GameStats.m_currentGold); Assert.Equal(acquired, claim.Receipt.Gold);
        Assert.Equal(acquired, Assert.Single(f.Receipts).Value.Gold); Assert.Empty(f.Quests);
        Assert.True(f.Live.QuestBehavior.HasCompletedQuest(TerminalClaimFixture.QuestName));
        Assert.Equal(new[] { firstBalance, finalBalance }, claim.GoalActions.Select(action => action.Message)
            .OfType<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>().Select(update => update.Gold));
        Assert.Equal(finalBalance, Assert.Single(claim.EndActions.Select(action => action.Message)
            .OfType<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>()).Gold);
        Assert.Equal(firstGold == 0 ? Array.Empty<int>() : new[] { firstGold, secondGold }, DecodeGoldPopups(claim.GoalActions));
        Assert.Empty(DecodeGoldPopups(claim.EndActions));
        Assert.Equal(acquired == 0 ? 901 : finalBalance, f.Live.GameStats.m_currentGold);
        Assert.Equal(QuestClaimStatus.AlreadyClaimed, f.Claim(out _)); Assert.Equal(1, f.Saves); Assert.Equal(3, f.Rolls);
    }

    [Theory]
    [InlineData("update-refused")] [InlineData("update-throws")]
    [InlineData("loot-empty")] [InlineData("loot-throws")]
    public void TerminalQuestGoldPreparationFailureCannotSaveOrPublishStagedHeadroom(string failure) {
        using var f = new TerminalClaimFixture(); ConfigureNativeGoldPreparation(f);
        f.Saved.GameStats.m_currentGold = 490; f.Reward.GoldAmount = 30; var published = 0;
        var prepare = f.Dependencies.Prepare;
        if (failure.StartsWith("update", StringComparison.Ordinal)) f.Dependencies.Prepare = message => {
            if (message is WIZARD_12_PROTOCOL.MSG_UPDATEGOLD) {
                if (failure == "update-throws") throw new InvalidOperationException("Authored gold preparation refusal");
                return false;
            }
            return prepare(message);
        };
        if (failure == "loot-empty") f.Dependencies.SerializeLoot = (_, _) => new ByteString();
        if (failure == "loot-throws") f.Dependencies.SerializeLoot = (_, _) => throw new InvalidOperationException("Authored gold loot preparation refusal");
        f.AfterCommit = _ => published++;
        Assert.Equal(QuestClaimStatus.Refused, f.Claim(out var claim)); Assert.Null(claim);
        Assert.Equal(0, f.Saves); Assert.Equal(0, published); Assert.Empty(f.Receipts); Assert.Single(f.Quests);
        Assert.Equal(490, f.Saved.GameStats.m_currentGold); Assert.Equal(901, f.Live.GameStats.m_currentGold);
        Assert.True(f.Expected.IsGoalActive(TerminalClaimFixture.GoalName));
        Assert.False(f.Live.QuestBehavior.HasCompletedQuest(TerminalClaimFixture.QuestName));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    private static void ConfigureNativeGoldPreparation(TerminalClaimFixture fixture) {
        fixture.Dependencies.SerializeLoot = (loot, flags) => {
            fixture.Loot.Add((loot, flags));
            Assert.True(new ObjectSerializer(Versionable: false).Serialize(loot, flags, out var data));
            return data;
        };
        fixture.Dependencies.Prepare = message => {
            fixture.Prepared.Add(message); return MessageEncoder.Encode(message).Length > 0;
        };
    }

    private static int[] DecodeGoldPopups(IEnumerable<QuestClaimAction> actions)
        => actions.Select(action => action.Message).OfType<WIZARD_12_PROTOCOL.MSG_LOOT>().Select(packet => {
            Assert.True(new ObjectSerializer(Versionable: false).Deserialize<LootInfoList>((byte[])packet.LootList, 4, out var loot));
            return Assert.IsType<GoldLootInfo>(loot.m_goldInfo).m_goldAmount;
        }).ToArray();

    [Fact]
    public void TerminalAcknowledgementProtectsCapturedSideQuestOriginalsButDeletesTheTargetAndStoresTheReceipt() {
        using var f = new TerminalClaimFixture(); f.Reward.GoldAmount = 15;
        var side = TerminalClaimFixture.CloneQuest(f.Quests[0]); side.ID = 782030; side.QuestName = "QA-PROTECTED-SIDE"; side.GoalProgress[0].ID = 782031;
        var orphan = TerminalClaimFixture.CloneQuest(side); orphan.ID = 782032; orphan.QuestName = "QA-PROTECTED-ORPHAN"; orphan.GoalProgress[0].ID = 782033;
        var foreign = TerminalClaimFixture.CloneQuest(side); foreign.ID = 782034; foreign.OwnerCharId++; foreign.QuestName = "QA-FOREIGN-READ";
        f.Quests.AddRange([side, orphan, foreign]); f.Saved.QuestBehavior.CurrentQuestIDs.Add(side.ID);
        f.Live = f.Reload(); f.Live.GameStats.m_currentGold = 901; var journal = f.Live.QuestBehavior;
        f.OnSave = () => {
            var session = f.Working!; var target = session.Quests.Single(row => row.ID == TerminalClaimFixture.QuestId);
            var trackedSide = session.Quests.Single(row => row.ID == side.ID); var trackedOrphan = session.Quests.Single(row => row.ID == orphan.ID);
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(2, session.Ignored.OfType<QuestInstance>().Count());
            Assert.Contains(trackedSide, session.Ignored); Assert.Contains(trackedOrphan, session.Ignored);
            Assert.DoesNotContain(side, session.Ignored); Assert.DoesNotContain(orphan, session.Ignored);
            Assert.DoesNotContain(session.Quests.Single(row => row.ID == foreign.ID), session.Ignored);
            Assert.Contains(target, session.Deleted); Assert.DoesNotContain(target, session.Ignored);
            Assert.DoesNotContain(Assert.Single(session.Receipts).Value, session.Ignored); Assert.DoesNotContain(session.Wizard, session.Ignored);
            Assert.True(trackedSide.IsGoalActive(TerminalClaimFixture.GoalName)); Assert.True(trackedOrphan.IsGoalActive(TerminalClaimFixture.GoalName));
            Assert.False(journal.HasCompletedQuest(TerminalClaimFixture.QuestName)); Assert.Equal(901, f.Live.GameStats.m_currentGold);
        };
        Assert.Equal(QuestClaimStatus.Committed, f.Claim(out var claim)); Assert.Equal(1, f.Saves);
        Assert.DoesNotContain(f.Quests, row => row.ID == TerminalClaimFixture.QuestId);
        Assert.True(f.Quests.Single(row => row.ID == side.ID).IsGoalActive(TerminalClaimFixture.GoalName));
        Assert.True(f.Quests.Single(row => row.ID == orphan.ID).IsGoalActive(TerminalClaimFixture.GoalName));
        Assert.Equal(side.ID, Assert.Single(f.Saved.QuestBehavior.CurrentQuestIDs)); Assert.Same(journal, f.Live.QuestBehavior);
        Assert.True(journal.HasCompletedQuest(TerminalClaimFixture.QuestName)); Assert.Equal(115, f.Saved.GameStats.m_currentGold);
        Assert.Equal(115, f.Live.GameStats.m_currentGold); Assert.Equal(TerminalClaimFixture.QuestId, Assert.Single(f.Receipts).Value.QuestId);
        Assert.Equal(15, claim.Receipt.Gold); Assert.Equal(1, f.Rolls);
    }

    [Theory]
    [InlineData("expected-owner")] [InlineData("expected-id")] [InlineData("expected-name")]
    [InlineData("expected-goal-owner")] [InlineData("expected-goal-id")]
    [InlineData("expected-goal-type")] [InlineData("duplicate-expected-goal")]
    [InlineData("template-goal-id")] [InlineData("duplicate-template-goal")]
    public void InvalidExpectedIdentityRefusesBeforeOpeningOrRolling(string defect) {
        using var f = new TerminalClaimFixture();
        switch (defect) {
            case "expected-owner": f.Expected.OwnerCharId++; break;
            case "expected-id": f.Expected.ID = 0; break;
            case "expected-name": f.Expected.QuestName = "QA-FOREIGN"; break;
            case "expected-goal-owner": f.Expected.GoalProgress[0].OwnerCharId++; break;
            case "expected-goal-id": f.Expected.GoalProgress[0].ID = 0; break;
            case "expected-goal-type": f.Expected.GoalProgress[0].GoalType = (GOAL_TYPE)99; break;
            case "duplicate-expected-goal": f.Expected.GoalProgress = [f.Expected.GoalProgress[0], f.Expected.GoalProgress[0]]; break;
            case "template-goal-id": f.Template.m_goals[0] = f.Goal with { m_goalNameID = 9 }; break;
            case "duplicate-template-goal": f.Template.m_goals.Add(f.Goal); break;
        }
        Assert.Equal(QuestClaimStatus.Refused, f.Claim(out var receipt)); Assert.Null(receipt);
        Assert.Equal(0, f.Opened); Assert.Equal(0, f.Rolls); Assert.Equal(0, f.Saves);
        Assert.True(f.Live.QuestBehavior.CurrentQuestInstances[0].IsGoalActive(TerminalClaimFixture.GoalName));
    }

    [Theory]
    [InlineData("missing-wizard")] [InlineData("saved-owner")]
    [InlineData("missing-journal")] [InlineData("zero-reference")] [InlineData("duplicate-reference")]
    [InlineData("missing-row")] [InlineData("foreign-row")]
    [InlineData("duplicate-owned-row")] [InlineData("duplicate-global-id")]
    [InlineData("duplicate-quest-name")] [InlineData("fresh-goal-owner")]
    [InlineData("fresh-goal-id")] [InlineData("fresh-goal-type")]
    [InlineData("inactive-goal")] [InlineData("completed-goal")]
    [InlineData("duplicate-fresh-goal")] [InlineData("legacy-complete")]
    public void FreshJournalOwnershipAndGoalStateMustBeProvenBeforeRewards(string defect) {
        using var f = new TerminalClaimFixture();
        var quest = f.Quests[0];
        switch (defect) {
            case "missing-wizard": f.MissingWizard = true; break;
            case "saved-owner": f.Saved.CharId++; break;
            case "missing-journal": f.Saved.QuestBehavior = null!; break;
            case "zero-reference": f.Saved.QuestBehavior.CurrentQuestIDs.Add(0); break;
            case "duplicate-reference": f.Saved.QuestBehavior.CurrentQuestIDs.Add(quest.ID); break;
            case "missing-row": f.Quests.Clear(); break;
            case "foreign-row": quest.OwnerCharId++; break;
            case "duplicate-owned-row": f.Quests.Add(TerminalClaimFixture.CloneQuest(quest)); break;
            case "duplicate-global-id": var foreign = TerminalClaimFixture.CloneQuest(quest); foreign.OwnerCharId++; f.Quests.Add(foreign); break;
            case "duplicate-quest-name": var second = TerminalClaimFixture.CloneQuest(quest); second.ID++; f.Quests.Add(second); f.Saved.QuestBehavior.CurrentQuestIDs.Add(second.ID); break;
            case "fresh-goal-owner": quest.GoalProgress[0].OwnerCharId++; break;
            case "fresh-goal-id": quest.GoalProgress[0].ID++; break;
            case "fresh-goal-type": quest.GoalProgress[0].GoalType = (GOAL_TYPE)99; break;
            case "inactive-goal": quest.GoalProgress = [new GoalInstance { ID = TerminalClaimFixture.GoalId, OwnerCharId = TerminalClaimFixture.Character, GoalName = TerminalClaimFixture.GoalName, GoalType = f.Goal.m_goalType }]; break;
            case "completed-goal": quest.CompleteGoal(TerminalClaimFixture.GoalName); break;
            case "duplicate-fresh-goal": quest.GoalProgress = [quest.GoalProgress[0], quest.GoalProgress[0]]; break;
            case "legacy-complete": f.Saved.QuestBehavior.SetQuestRegistryValue(TerminalClaimFixture.QuestName, "Complete", 1); break;
        }
        Assert.Equal(QuestClaimStatus.Refused, f.Claim(out var receipt)); Assert.Null(receipt);
        Assert.Equal(0, f.Rolls); Assert.Equal(0, f.Saves); Assert.Empty(f.Receipts);
        Assert.Equal(901, f.Live.GameStats.m_currentGold); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        Assert.True(f.Expected.IsGoalActive(TerminalClaimFixture.GoalName));
    }

    [Theory]
    [InlineData("stock")] [InlineData("uninitialized")] [InlineData("no-logic")]
    [InlineData("Tutorial_Intro")] [InlineData("WC-TUT-C03-001")] [InlineData("WC-TUT-C05-001")]
    public void ExistingStockAndDedicatedTutorialPathsRemainUnclaimed(string path) {
        using var f = new TerminalClaimFixture();
        if (path == "stock") TerminalClaimFixture.SetRules(ClassicRules.Stock);
        else if (path == "uninitialized") TerminalClaimFixture.SetRules(null);
        else if (path == "no-logic") f.Template.m_goalLogic.Clear();
        else f.Template.m_questName = path;
        Assert.Equal(QuestClaimStatus.Legacy, f.Claim(out var receipt)); Assert.Null(receipt);
        Assert.False(ClassicQuestClaims.UsesTerminalClaims(f.Template));
        Assert.Equal(0, f.Opened); Assert.Equal(0, f.Rolls); Assert.Equal(0, f.Saves);
    }

    [Fact]
    public void ARealNextGoalDecisionDoesNotSaveTheProposedFinalGoalOrRunRewards() {
        using var f = new TerminalClaimFixture();
        f.Template.m_goals.Add(new PersonaGoalTemplate { m_goalName = "Next", m_goalNameID = 782004, m_goalType = GOAL_TYPE.GOAL_TYPE_PERSONA });
        f.Template.m_goalLogic = [new GoalCompleteLogic { m_goalsAND = [TerminalClaimFixture.GoalName], m_goalsToAdd = ["Next"] }];
        Assert.Equal(QuestClaimStatus.NonTerminal, f.Claim(out var receipt)); Assert.Null(receipt);
        Assert.True(f.Working!.Quests[0].IsGoalActive(TerminalClaimFixture.GoalName));
        Assert.True(f.Quests[0].IsGoalActive(TerminalClaimFixture.GoalName));
        Assert.True(f.Expected.IsGoalActive(TerminalClaimFixture.GoalName));
        Assert.Equal(0, f.Rolls); Assert.Equal(0, f.Saves); Assert.Empty(f.Working.Deleted);
    }

    [Theory]
    [InlineData("matching")] [InlineData("owner")] [InlineData("quest")]
    [InlineData("goal")] [InlineData("name")] [InlineData("goal-name")]
    public void AReceiptIsCheckedBeforeRowsAndNeverRerolledOrRepublished(string mismatch) {
        using var f = new TerminalClaimFixture();
        var old = f.MatchingReceipt();
        switch (mismatch) {
            case "owner": old.CharId++; break;
            case "quest": old.QuestId++; break;
            case "goal": old.GoalId++; break;
            case "name": old.QuestName = "QA-OTHER"; break;
            case "goal-name": old.GoalName = "Other"; break;
        }
        f.Receipts[ClassicQuestClaims.ReceiptId(TerminalClaimFixture.Character, TerminalClaimFixture.QuestId)] = old;
        f.Quests.Clear(); // An acknowledged receipt must not need the already retired quest row.
        f.Dependencies.BeforePublish = _ => Assert.Fail("an old receipt cannot publish a fresh claim");
        Assert.Equal(mismatch == "matching" ? QuestClaimStatus.AlreadyClaimed : QuestClaimStatus.Refused, f.Claim(out var claim));
        Assert.Null(claim); Assert.Equal(0, f.Working!.Queries); Assert.Equal(1, f.Working.ReceiptLoads);
        Assert.Equal(0, f.Rolls); Assert.Equal(0, f.Saves); Assert.Equal(901, f.Live.GameStats.m_currentGold);
        Assert.True(f.Live.QuestBehavior.CurrentQuestInstances[0].IsGoalActive(TerminalClaimFixture.GoalName));
        Assert.False(f.Live.QuestBehavior.HasCompletedQuest(TerminalClaimFixture.QuestName));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AValidZeroRewardStillRetiresTheOwnedQuestAndStoresItsReceiptOnce(bool unknownDrop) {
        using var f = new TerminalClaimFixture();
        if (unknownDrop) f.Reward.Items.Add(new() { ItemId = "987654", Quantity = 1 });
        var questBehavior = f.Live.QuestBehavior; var stats = f.Live.GameStats;
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.True(f.Expected.IsGoalActive(TerminalClaimFixture.GoalName));
            Assert.False(f.Live.QuestBehavior.HasCompletedQuest(TerminalClaimFixture.QuestName));
            Assert.Equal(TerminalClaimFixture.QuestId, Assert.Single(f.Live.QuestBehavior.CurrentQuestIDs));
            Assert.Single(f.Working!.Deleted); Assert.Single(f.Working.Receipts);
        };
        Assert.Equal(QuestClaimStatus.Committed, f.Claim(out var claim)); Assert.NotNull(claim);
        Assert.Equal(1, f.Saves); Assert.Equal(1, f.Rolls); Assert.Empty(f.Quests);
        Assert.Empty(f.Saved.QuestBehavior.CurrentQuestIDs); Assert.True(f.Saved.QuestBehavior.HasCompletedQuest(TerminalClaimFixture.QuestName));
        Assert.Same(questBehavior, f.Live.QuestBehavior); Assert.Same(stats, f.Live.GameStats);
        Assert.Empty(f.Live.QuestBehavior.CurrentQuestIDs); Assert.True(f.Live.QuestBehavior.HasCompletedQuest(TerminalClaimFixture.QuestName));
        Assert.Equal(TerminalClaimFixture.GoalId, Assert.Single(claim.Receipt.CompletedGoalIds));
        Assert.Empty(claim.Receipt.Rewards); Assert.Empty(claim.Stack.Items); Assert.Empty(claim.Stack.Cards); Assert.Empty(claim.Stack.Reagents);
        Assert.Equal(QuestClaimStatus.AlreadyClaimed, f.Claim(out var replay)); Assert.Null(replay);
        Assert.Equal(1, f.Saves); Assert.Equal(1, f.Rolls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailedOrDurableLostAckQuarantinesBeforeDisposalAndCannotRetryTheLiveClaim(bool durable) {
        using var f = new TerminalClaimFixture { FailSave = true, Durable = durable };
        f.Reward.GoldAmount = 30; var disposed = false;
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
            Assert.Equal(901, f.Live.GameStats.m_currentGold); Assert.True(f.Expected.IsGoalActive(TerminalClaimFixture.GoalName));
        };
        f.OnDispose = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
            Assert.Equal(901, f.Live.GameStats.m_currentGold); disposed = true;
        };
        Assert.Throws<InvalidOperationException>(() => f.Claim(out _)); Assert.True(disposed);
        Assert.Equal(durable ? 130 : 100, f.Saved.GameStats.m_currentGold);
        Assert.Equal(durable ? 1 : 0, f.Receipts.Count); Assert.Equal(durable ? 0 : 1, f.Quests.Count);
        Assert.True(f.Expected.IsGoalActive(TerminalClaimFixture.GoalName));
        Assert.Equal(QuestClaimStatus.Refused, f.Claim(out var retry)); Assert.Null(retry);
        Assert.Equal(1, f.Opened); Assert.Equal(1, f.Rolls); Assert.Equal(1, f.Saves);
        Assert.False(WizardCollection.ChangeGold(f.Live, 1, false)); Assert.Equal(1, f.Saves);
        if (durable) {
            f.OnDispose = null; f.Live = f.Reload();
            Assert.Equal(QuestClaimStatus.AlreadyClaimed, f.Claim(out _)); Assert.Equal(1, f.Rolls); Assert.Equal(1, f.Saves);
        }
    }

    [Fact]
    public void PublicationFailureIsQuarantinedInsideTheLaneAfterTheSingleDurableWrite() {
        using var f = new TerminalClaimFixture(); var observed = false;
        f.Reward.GoldAmount = 30;
        f.Dependencies.BeforePublish = wizard => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Single(f.Receipts); Assert.Empty(f.Quests);
            Assert.Equal(901, wizard.GameStats.m_currentGold); throw new InvalidOperationException("authored publication failure");
        };
        f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); observed = true; };
        Assert.Throws<InvalidOperationException>(() => f.Claim(out _)); Assert.True(observed);
        Assert.Equal(130, f.Saved.GameStats.m_currentGold); Assert.Equal(901, f.Live.GameStats.m_currentGold);
        Assert.Equal(QuestClaimStatus.Refused, f.Claim(out _)); Assert.Equal(1, f.Saves); Assert.Equal(1, f.Rolls);
    }

    [Theory]
    [InlineData("primitive")] [InlineData("primitive-throws")]
    [InlineData("loot-empty")] [InlineData("loot-throws")]
    public void NativePreparationFailureDiscardsTheWholeClaimWithoutSuccessOrQuarantine(string failure) {
        using var f = new TerminalClaimFixture(); f.Reward.GoldAmount = 30;
        if (failure == "primitive") f.Dependencies.Prepare = _ => false;
        if (failure == "primitive-throws") f.Dependencies.Prepare = _ => throw new InvalidOperationException("authored message refusal");
        if (failure == "loot-empty") f.Dependencies.SerializeLoot = (_, _) => new ByteString();
        if (failure == "loot-throws") f.Dependencies.SerializeLoot = (_, _) => throw new InvalidOperationException("authored loot refusal");
        Assert.Equal(QuestClaimStatus.Refused, f.Claim(out var claim)); Assert.Null(claim);
        Assert.Equal(0, f.Saves); Assert.Empty(f.Receipts); Assert.Single(f.Quests);
        Assert.Equal(100, f.Saved.GameStats.m_currentGold); Assert.Equal(901, f.Live.GameStats.m_currentGold);
        Assert.True(f.Expected.IsGoalActive(TerminalClaimFixture.GoalName)); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public async Task ACompetingCurrencyWriteCannotOvertakeClaimAcknowledgementOrPublication() {
        using var f = new TerminalClaimFixture(); f.Reward.GoldAmount = 30;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var attempted = new ManualResetEventSlim(); var publications = new List<string>();
        f.AfterCommit = claim => {
            Assert.True(WizardCollection.HoldsWriteLane); entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(130, f.Live.GameStats.m_currentGold); Assert.Equal(30, claim.Receipt.Gold);
            publications.Add("claim");
        };
        var claimTask = Task.Run(() => f.Claim(out _));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var currencyTask = Task.Run(() => { attempted.Set(); var changed = WizardCollection.ChangeGold(f.Live, 7, false); publications.Add("currency"); return changed; });
        Assert.True(attempted.Wait(TimeSpan.FromSeconds(5))); Assert.False(currencyTask.IsCompleted);
        Assert.Equal(130, f.Saved.GameStats.m_currentGold); Assert.Equal(130, f.Live.GameStats.m_currentGold); Assert.Equal(1, f.Opened);
        release.Set(); Assert.Equal(QuestClaimStatus.Committed, await claimTask); Assert.True(await currencyTask);
        Assert.Equal(["claim", "currency"], publications); Assert.Equal(137, f.Saved.GameStats.m_currentGold);
        Assert.Equal(137, f.Live.GameStats.m_currentGold); Assert.Equal(2, f.Saves); Assert.Equal(1, f.Rolls);
    }

    [Fact]
    public void AcknowledgedNativeReceiptCallbackFailureQuarantinesBeforeDisposalWithoutRerollOrSecondSave() {
        using var f = new TerminalClaimFixture(); f.Reward.GoldAmount = 30; var callbacks = 0; var disposed = false;
        f.AfterCommit = claim => {
            Assert.True(WizardCollection.HoldsWriteLane); callbacks++; Assert.Equal(130, f.Live.GameStats.m_currentGold);
            Assert.True(f.Live.QuestBehavior.HasCompletedQuest(TerminalClaimFixture.QuestName)); Assert.Single(f.Receipts);
            Assert.Equal(30, claim.Receipt.Gold); throw new InvalidOperationException("authored native receipt consumer failure");
        };
        f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); disposed = true; };
        Assert.Throws<InvalidOperationException>(() => f.Claim(out _)); Assert.True(disposed);
        Assert.Equal(1, callbacks); Assert.Equal(1, f.Saves); Assert.Equal(1, f.Rolls); Assert.Empty(f.Quests);
        Assert.Equal(QuestClaimStatus.Refused, f.Claim(out _)); Assert.Equal(1, callbacks); Assert.Equal(1, f.Saves);
    }

    [Fact]
    public void AllPersistentRewardsUseFreshBalancesAndShareOneSaveWithFinalGoalRetirement() {
        using var f = new TerminalClaimFixture();
        f.Reward = new() { GoldAmount = 30, ExperienceAmount = 150, TrainingPoints = 2, GrantsPotionSlot = true };
        f.Template.m_endResults.m_results = [new ResDropTable { m_tableName = "QA-END-REWARD" },
            new ResLearnSpell { m_templateID = TerminalClaimFixture.Learned },
            new ResModifyEntry { m_entryName = "QA-Granted", m_value = 4 },
            new ResAddDynaMod { m_zoneName = "QA/Claim", m_dynaModClientTag = "AuthoredDoor", m_dynaModState = "Open" }];
        f.Dependencies.RollQuestReward = (table, _) => {
            f.Rolls++; return table == "QA-GOAL-REWARD" ? f.Reward : new() { GoldAmount = 90, ExperienceAmount = 120, TrainingPoints = 3, GrantsPotionSlot = true };
        };
        var badge = new Badge("authored-terminal", "Authored claim", "QA_Badge", null, "QA_Title", "QA_Filter", "wizard_city", new QuestBadgeAward([TerminalClaimFixture.QuestName]));
        ClassicBadges.Rules = () => new() { Id = "authored-badges", Profiles = [], SourceFile = "authored-fixture", Badges = [badge] };
        f.Dynamods.Add(new(TerminalClaimFixture.Character) { Dynamods = [new Dynamod { ZoneName = "QA/Other", ClientTag = "Unrelated", ModState = "Kept" }] });
        f.Live.GameStats.m_currentHitpoints = 31; f.Live.GameStats.m_currentMana = 19;
        f.Live.MagicSchoolBehavior.ExperiencePoints = 9; f.Live.MagicSchoolBehavior.TrainingPoints = 71;
        f.Live.GameStats.m_potionCharge = 9; f.Live.GameStats.m_potionMax = 9;
        var stats = f.Live.GameStats; var school = f.Live.MagicSchoolBehavior; var book = f.Live.SpellbookBehavior;
        var registry = f.Live.QuestBehavior.Registry; var temporary = new Spell { m_templateID = 782090 };
        book.TemporarySpells.Add(temporary); var deck = book.DeckTreasureCards; deck[782091] = new() { [TerminalClaimFixture.Card] = 3 };
        f.OnSave = () => {
            Assert.Equal(901, stats.m_currentGold); Assert.Equal(9, school.ExperiencePoints); Assert.Equal(71, school.TrainingPoints);
            Assert.Equal(9f, stats.m_potionCharge); Assert.Equal(9f, stats.m_potionMax); Assert.Equal(31, stats.m_currentHitpoints);
            Assert.Empty(book.LearnedSpellTemplateIds); Assert.Empty(f.BadgeEvents);
            Assert.Equal(220, f.Working!.Wizard.GameStats.m_currentGold); Assert.Equal(350, f.Working.Wizard.MagicSchoolBehavior.ExperiencePoints);
            Assert.Equal(4, f.Working.Wizard.MagicSchoolBehavior.Level); Assert.Equal(10, f.Working.Wizard.MagicSchoolBehavior.TrainingPoints);
            Assert.Equal(5f, f.Working.Wizard.GameStats.m_potionCharge); Assert.Equal(5f, f.Working.Wizard.GameStats.m_potionMax);
            Assert.Contains(TerminalClaimFixture.Learned, f.Working.Wizard.SpellbookBehavior.LearnedSpellTemplateIds);
            Assert.True(ClassicBadges.Has(f.Working.Wizard, badge)); Assert.Single(f.Working.Receipts); Assert.Single(f.Working.Deleted);
            Assert.Equal("Open", f.Working.Dynamods[0].Dynamods.Single(row => row.ClientTag == "AuthoredDoor").ModState);
        };
        Assert.Equal(QuestClaimStatus.Committed, f.Claim(out var claim));
        Assert.Equal(1, f.Saves); Assert.Equal(2, f.Rolls); Assert.Empty(f.BadgeEvents); Assert.Single(claim.Badges);
        Assert.Equal(120, claim.Receipt.Gold); Assert.Equal(200, claim.Receipt.Experience); Assert.Equal(5, claim.Receipt.TrainingPoints);
        Assert.Equal(2, claim.Receipt.PotionSlots); Assert.Equal(TerminalClaimFixture.Learned, Assert.Single(claim.Receipt.LearnedSpells));
        Assert.Same(stats, f.Live.GameStats); Assert.Same(school, f.Live.MagicSchoolBehavior); Assert.Same(book, f.Live.SpellbookBehavior);
        Assert.Same(registry, f.Live.QuestBehavior.Registry); Assert.Same(deck, book.DeckTreasureCards); Assert.Same(temporary, Assert.Single(book.TemporarySpells));
        Assert.Equal(220, stats.m_currentGold); Assert.Equal(350, school.ExperiencePoints); Assert.Equal(4, school.Level); Assert.Equal(10, school.TrainingPoints);
        Assert.Equal(5f, stats.m_potionCharge); Assert.Equal(5f, stats.m_potionMax); Assert.Equal(TerminalClaimFixture.Table(4).m_hitpoints, stats.m_currentHitpoints);
        Assert.Equal(45UL, registry["LiveUnrelated"]); Assert.Equal(4UL, registry["QA-Granted"]); Assert.True(ClassicBadges.Has(f.Live, badge));
        Assert.Equal("Kept", f.Live.DynamodSet.Dynamods.Single(row => row.ClientTag == "Unrelated").ModState);
        Assert.Equal(2, f.Loot.Count(entry => entry.Flags == 4));
        Assert.Equal([150, 50], f.Loot.Where(entry => entry.Flags == 4).Select(entry => Assert.Single(entry.Loot.m_loot.OfType<MagicXPLootInfo>()).m_experience));
        Assert.Equal(1, claim.GoalActions.Concat(claim.EndActions).Count(action => action.Message is WIZARD_12_PROTOCOL.MSG_UPDATEXP));
        Assert.Single(claim.GoalCompletionMessages); Assert.Equal(2, claim.QuestCompletionMessages.Count);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void StackReceiptAndPopupContainOnlyTheFreshCapacityAcceptedCounts(bool full) {
        using var f = new TerminalClaimFixture();
        f.Saved.InventoryBehavior.InventoryItemIds = full ? [782020, 782021] : [782020];
        f.Items = f.Saved.InventoryBehavior.InventoryItemIds.Select(id => new WizClientObjectItem { m_globalID = id, m_characterId = TerminalClaimFixture.Character, m_templateID = TerminalClaimFixture.Gear, m_inactiveBehaviors = [] }).ToList();
        var pet = new WizClientObjectItem { m_globalID = 782029, m_characterId = TerminalClaimFixture.Character,
            m_templateID = 782030, m_inactiveBehaviors = [new ClientPetItemBehavior { m_XP = 41 }] };
        f.Items.Add(pet); // Native unrelated row is protected by the outer finalizer, not normalized or rewritten.
        f.Saved.SpellbookBehavior.TreasureCardTemplateIds = Enumerable.Repeat(TerminalClaimFixture.Card, full ? 999 : 998).ToList();
        f.Saved.AlchemyBehavior.ReagentItemIds = [TerminalClaimFixture.ReagentId];
        f.Reagents = [new ClientReagentItem { m_globalID = TerminalClaimFixture.ReagentId, m_templateID = TerminalClaimFixture.Reagent, m_characterId = TerminalClaimFixture.Character, m_quantity = full ? 999 : 998 }];
        f.Live = f.Reload(); var liveReagent = Assert.Single(f.Live.AlchemyBehavior.Reagents); liveReagent.m_quantity = 12;
        f.Live.SpellbookBehavior.TreasureCardTemplateIds.Clear(); f.Live.InventoryBehavior.InventoryItemIds.Clear();
        f.Reward = new() { Items = [new() { ItemId = TerminalClaimFixture.Gear.ToString(), Quantity = 99 },
            new() { ItemId = TerminalClaimFixture.Card.ToString(), Quantity = 3 }], Reagents = [new() { ItemId = TerminalClaimFixture.Reagent.ToString(), Quantity = 5 }] };
        f.OnSave = () => {
            Assert.Equal(12, liveReagent.m_quantity); Assert.Empty(f.Live.SpellbookBehavior.TreasureCardTemplateIds);
            Assert.Equal(f.Items.Count, f.Working!.Ignored.Count); Assert.All(f.Working.Items.Where(row => row.m_globalID.Full != TerminalClaimFixture.ItemId), row => Assert.Contains(row, f.Working.Ignored));
        };
        Assert.Equal(QuestClaimStatus.Committed, f.Claim(out var claim)); Assert.Equal(1, f.Saves);
        Assert.Equal(999, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count); Assert.Equal(999, f.Reagents[0].m_quantity);
        Assert.Equal(full ? 0 : 3, claim.Receipt.Rewards.Count);
        if (full) {
            Assert.Empty(claim.Stack.Items); Assert.Empty(claim.Stack.Cards); Assert.Empty(claim.Stack.Reagents);
            Assert.Empty(f.Loot); Assert.True(claim.Stack.BackpackCapacityExceeded); Assert.Same(liveReagent, Assert.Single(f.Live.AlchemyBehavior.Reagents));
            Assert.Equal(12, liveReagent.m_quantity); Assert.Empty(f.Live.SpellbookBehavior.TreasureCardTemplateIds);
        } else {
            Assert.Single(claim.Stack.Items); Assert.Single(claim.Stack.Cards); Assert.Equal(1, Assert.Single(claim.Stack.Reagents).Acquired);
            Assert.Same(liveReagent, Assert.Single(f.Live.AlchemyBehavior.Reagents)); Assert.Equal(999, liveReagent.m_quantity);
            var loot = Assert.Single(f.Loot); Assert.Equal(4u, loot.Flags);
            Assert.All(loot.Loot.m_loot.OfType<ItemLootInfo>(), row => Assert.Equal(1, row.m_numItems));
            var card = Assert.Single(loot.Loot.m_loot.OfType<TreasureCardLootInfo>());
            Assert.Equal(Assert.Single(claim.Stack.Cards).SpellHash, card.m_spellID);
            Assert.Equal(1, claim.Receipt.Rewards.Single(row => row.Kind == "Reagent").Count);
            Assert.Equal(1, claim.Receipt.Rewards.Single(row => row.Kind == "Item").Count);
        }
        Assert.Equal(41u, Assert.Single(f.Items.Single(row => row.m_globalID.Full == 782029).m_inactiveBehaviors.OfType<ClientPetItemBehavior>()).m_XP);
    }

    [Fact]
    public void ClassicCardCinematicKeepsTemplateIdsAndGroupedAdmittedCountsSeparateFromDropHashIds() {
        using var f = new TerminalClaimFixture();
        f.SetQuestCards(new() { Id = "authored-cards", Profiles = [], SourceFile = "authored-fixture",
            ByQuest = new Dictionary<string, ImmutableArray<QuestCard>> { [TerminalClaimFixture.QuestName] = [new("Authored", TerminalClaimFixture.Card, 3, "fire")] }.ToFrozenDictionary() });
        f.Reward.TreasureCards = [TerminalClaimFixture.Card];
        Assert.Equal(QuestClaimStatus.Committed, f.Claim(out var claim)); Assert.Equal(4, claim.Stack.Cards.Count);
        Assert.Equal(4, claim.Receipt.Rewards.Count(row => row.Kind == "TreasureCard"));
        var popup = Assert.Single(f.Loot.Where(entry => entry.Flags == 4));
        Assert.Equal(claim.Stack.Cards[0].SpellHash, Assert.Single(popup.Loot.m_loot.OfType<TreasureCardLootInfo>()).m_spellID);
        var cinematic = Assert.Single(f.Loot.Where(entry => entry.Flags == 1));
        var grouped = Assert.Single(cinematic.Loot.m_loot.OfType<TreasureCardLootInfo>());
        Assert.Equal(TerminalClaimFixture.Card, grouped.m_spellID); Assert.Equal(3, grouped.m_numItems);
        Assert.Equal(3, claim.CinematicMessages.Count(message => message is WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK));
        Assert.IsType<WIZARD_12_PROTOCOL.MSG_QUESTREWARDS>(claim.CinematicMessages.Last()); Assert.Equal(1, f.Saves);
    }

    [Fact]
    public void BothRequirementPhasesAndInnerDropGatesObserveProposedJournalBeforeAnyRewardChanges() {
        using var f = new TerminalClaimFixture(); f.Reward.ExperienceAmount = 150;
        f.Goal.m_completeResults.m_results.Add(new ResLearnSpell { m_templateID = TerminalClaimFixture.Learned,
            m_requirements = new() { m_requirements = [new ReqHasGoal { m_questName = TerminalClaimFixture.QuestName,
                m_goalName = TerminalClaimFixture.GoalName, m_requiredStatus = GoalStatusRequirement.Complete }] } });
        f.Template.m_endResults.m_results = [new ResDropTable { m_tableName = "QA-END-REWARD",
            m_requirements = new() { m_requirements = [new ReqEntryValue { m_isQuestRegistry = true, m_questName = TerminalClaimFixture.QuestName,
                m_entryName = "Complete", m_operatorType = OPERATOR_TYPE.OPERATOR_EQUALS, m_numericValue = 1 },
                new ReqMagicLevel { m_operatorType = OPERATOR_TYPE.OPERATOR_EQUALS, m_numericValue = 2 }] } }];
        var observations = new List<(string Table, bool Held, bool Final, bool Complete, int Level, int Xp)>();
        f.Dependencies.RollQuestReward = (table, saved) => {
            f.Rolls++; observations.Add((table, saved.QuestBehavior.CurrentQuestIDs.Contains(TerminalClaimFixture.QuestId),
                saved.QuestBehavior.CurrentQuestInstances.Any(q => q.IsGoalCompleted(TerminalClaimFixture.GoalName)),
                saved.QuestBehavior.HasCompletedQuest(TerminalClaimFixture.QuestName), saved.MagicSchoolBehavior.Level, saved.MagicSchoolBehavior.ExperiencePoints));
            return table == "QA-GOAL-REWARD" ? f.Reward : new() { GoldAmount = 25 };
        };
        Assert.Equal(QuestClaimStatus.Committed, f.Claim(out var claim));
        Assert.Equal([("QA-GOAL-REWARD", true, true, false, 2, 150), ("QA-END-REWARD", false, false, true, 2, 150)], observations);
        Assert.Equal(300, f.Saved.MagicSchoolBehavior.ExperiencePoints); Assert.Equal(4, f.Saved.MagicSchoolBehavior.Level);
        Assert.Equal(25, claim.Receipt.Gold); Assert.Equal(TerminalClaimFixture.Learned, Assert.Single(claim.Receipt.LearnedSpells)); Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData("gold-negative")] [InlineData("xp-negative")] [InlineData("training-negative")]
    [InlineData("training-overflow")] [InlineData("missing-items")] [InlineData("missing-cards")]
    [InlineData("missing-reagents")] [InlineData("duplicate-dynamod")]
    [InlineData("bad-spell-identity")] [InlineData("unsupported-result")]
    public void InvalidCompoundRewardCannotCommitAnOtherwiseValidFinalGoal(string defect) {
        using var f = new TerminalClaimFixture(); f.Reward.GoldAmount = 30;
        switch (defect) {
            case "gold-negative": f.Reward.GoldAmount = -1; break;
            case "xp-negative": f.Reward.ExperienceAmount = -1; break;
            case "training-negative": f.Reward.TrainingPoints = -1; break;
            case "training-overflow": f.Reward.TrainingPoints = ushort.MaxValue; break;
            case "missing-items": f.Reward.Items = null!; break;
            case "missing-cards": f.Reward.TreasureCards = null!; break;
            case "missing-reagents": f.Reward.Reagents = null!; break;
            case "duplicate-dynamod": f.Goal.m_completeResults.m_results.Add(new ResAddDynaMod { m_zoneName = "QA/Claim", m_dynaModClientTag = "Door", m_dynaModState = "Open" });
                f.Dynamods = [new(TerminalClaimFixture.Character), new(TerminalClaimFixture.Character)]; break;
            case "bad-spell-identity": f.Goal.m_completeResults.m_results.Add(new ResLearnSpell { m_templateID = TerminalClaimFixture.Learned });
                f.Dependencies.Spell = _ => new Spell { m_templateID = TerminalClaimFixture.Learned + 1 }; break;
            case "unsupported-result": f.Goal.m_completeResults.m_results.Add(new ResRemoveDynaMod()); break;
        }
        Assert.Equal(QuestClaimStatus.Refused, f.Claim(out var claim)); Assert.Null(claim);
        Assert.Equal(0, f.Saves); Assert.Empty(f.Receipts); Assert.True(f.Expected.IsGoalActive(TerminalClaimFixture.GoalName));
        Assert.Equal(100, f.Saved.GameStats.m_currentGold); Assert.Equal(901, f.Live.GameStats.m_currentGold);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }
}

// The rows are real tracked references within each authored session. LINQ evaluates production predicates.
public sealed class TerminalClaimFixture : IDisposable {
    internal const ulong Character = 782000, QuestId = 782001, GoalId = 782002;
    internal const string QuestName = "QA-TERMINAL-CLAIM", GoalName = "Final";
    internal const uint Gear = 782010, Reagent = 782011, Card = 782012, Learned = 782013;
    internal const ulong ItemId = 782014, ReagentId = 782015;
    internal Wizard Saved, Live;
    internal QuestInstance Expected;
    internal readonly PersonaGoalTemplate Goal;
    internal readonly QuestTemplate Template;
    internal List<QuestInstance> Quests = [];
    internal List<DynamodSet> Dynamods = [];
    internal List<WizClientObjectItem> Items = [];
    internal List<ClientReagentItem> Reagents = [];
    internal Dictionary<string, QuestClaimReceipt> Receipts = [];
    internal readonly QuestClaimDependencies Dependencies;
    internal readonly StackRewardDependencies Stack;
    internal readonly ProgressionDependencies Progression;
    internal DropTableResult Reward = new();
    internal int Opened, Saves, Rolls;
    internal bool MissingWizard, FailSave, Durable;
    internal Action? OnSave, OnDispose, OnLoad;
    internal System.Action<TerminalQuestClaim>? AfterCommit;
    internal ClaimSession? Working;
    internal readonly List<IMessage> Prepared = [];
    internal readonly List<(LootInfoList Loot, uint Flags)> Loot = [];
    internal readonly List<string> BadgeEvents = [];
    private readonly object? _rules;
    private readonly object? _questCards;
    private readonly WizardCollection.TestStore? _store;
    private readonly QuestClaimDependencies? _claim;
    private readonly StackRewardDependencies? _stack;
    private readonly ProgressionDependencies? _progression;
    private readonly Func<IDocumentSession, List<WizClientObjectItem>>? _items;
    private readonly Func<IDocumentSession, List<ClientReagentItem>>? _reagents;
    private readonly Func<BadgeRules?> _badgeRules;
    private readonly System.Action<Wizard> _badgePersist;
    private readonly System.Action<Wizard, Badge> _badgeAwarded;
    private static readonly FieldInfo RulesField = typeof(ClassicRuntime).GetField("s_rules", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo QuestCardsField = typeof(ClassicProgression).GetField("s_questCards", BindingFlags.Static | BindingFlags.NonPublic)!;

    internal TerminalClaimFixture() {
        EquipmentAttachConcurrencyTests.Configure("[Character]\nMaxInventoryItems=2\nPetEnergyTickInSeconds=60\n[Classic]\nBackpackSize=2\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");
        // Configuration must precede the first static hook access when this fixture runs alone.
        _store = WizardCollection.TestStoreScope.Value; _claim = ClassicQuestClaims.TestScope.Value;
        _stack = ClassicStackRewards.TestScope.Value; _progression = WizardProgressionTransactions.TestScope.Value;
        _items = WizardInventoryTransactions.TestRowsScope.Value; _reagents = WizardReagentCollection.TestRowsScope.Value;
        _badgeRules = ClassicBadges.Rules; _badgePersist = ClassicBadges.Persist; _badgeAwarded = ClassicBadges.Awarded;
        _rules = RulesField.GetValue(null);
        _questCards = QuestCardsField.GetValue(null); QuestCardsField.SetValue(null, null);
        SetRules(new ClassicRules(ZoneFixture.Profile(cutoff: new DateOnly(2010, 10, 31), levelCap: 4,
            features: new() { [ClassicFeatures.TreasureCards] = true }), ZoneFixture.MinimalMap()));
        Goal = new PersonaGoalTemplate { m_goalName = GoalName, m_goalNameID = (uint)GoalId, m_goalType = GOAL_TYPE.GOAL_TYPE_PERSONA,
            m_completeResults = new ResultList { m_results = [new ResDropTable { m_tableName = "QA-GOAL-REWARD" }] } };
        Template = new QuestTemplate { m_questName = QuestName, m_goals = [Goal], m_startGoals = [GoalName],
            m_goalLogic = [new GoalCompleteLogic { m_goalsAND = [GoalName], m_completeQuest = true }],
            m_endResults = new ResultList { m_results = [] } };
        var quest = new QuestInstance { ID = QuestId, OwnerCharId = Character, QuestName = QuestName,
            GoalProgress = [new GoalInstance { ID = GoalId, OwnerCharId = Character, GoalName = GoalName, GoalType = Goal.m_goalType }] };
        quest.StartGoal(GoalName); Quests.Add(quest); Expected = CloneQuest(quest);
        Saved = NewWizard(); Saved.QuestBehavior.CurrentQuestIDs = [QuestId];
        Saved.QuestBehavior.Registry["SavedUnrelated"] = 21; Live = Reload(); Live.QuestBehavior.CurrentQuestInstances = [Expected];
        Live.GameStats.m_currentGold = 901; Live.QuestBehavior.Registry["LiveUnrelated"] = 45;
        Dependencies = new() {
            RollQuestReward = (_, _) => { Rolls++; return Reward; },
            Spell = id => id == Learned ? new Spell { m_templateID = id, m_spellID = 782023 } : null!,
            SerializeLoot = (loot, flags) => { Loot.Add((loot, flags)); return (ByteString)BitConverter.GetBytes(782099); },
            Prepare = message => { Prepared.Add(message); return true; },
        };
        Stack = new() {
            Template = id => id switch { Gear => new WizItemTemplate { m_templateID = Gear, m_behaviors = [] },
                Reagent => new ReagentItemTemplate { m_templateID = Reagent }, Card => new SpellTemplate { m_name = "QA-Card" }, _ => null! },
            Create = id => id == Reagent ? new ClientReagentItem { m_globalID = ReagentId, m_templateID = Reagent, m_characterId = Character, m_quantity = 0 }
                : new WizClientObjectItem { m_globalID = ItemId, m_templateID = (uint)id, m_characterId = Character, m_inactiveBehaviors = [] },
            SerializeItem = item => (ByteString)BitConverter.GetBytes(item.m_globalID.Full),
            SerializeReagent = row => (ByteString)BitConverter.GetBytes(row.m_quantity),
        };
        Progression = new() { LevelInfo = (_, level) => Table(level), LevelAtXp = xp => (byte)Math.Max(1, xp / 100 + 1),
            XpAtLevel = level => (level - 1) * 100, MaxLevel = () => 4, XpCeiling = () => 350, Prepare = message => Dependencies.Prepare(message) };
        ClassicBadges.Rules = () => null; ClassicBadges.Persist = _ => Assert.Fail("a staged badge must not nest a save");
        ClassicBadges.Awarded = (_, badge) => BadgeEvents.Add(badge.Id);
        Install();
    }
    internal static void SetRules(ClassicRules? rules) => RulesField.SetValue(null, rules);
    internal void SetQuestCards(QuestCardRewards cards) => QuestCardsField.SetValue(null, cards);
    internal void Install() {
        WizardCollection.TestStoreScope.Value = new(Open, Load); ClassicQuestClaims.TestScope.Value = Dependencies;
        ClassicStackRewards.TestScope.Value = Stack; WizardProgressionTransactions.TestScope.Value = Progression;
        WizardInventoryTransactions.TestRowsScope.Value = session => ((ClaimSession)(object)session).Items;
        WizardReagentCollection.TestRowsScope.Value = session => ((ClaimSession)(object)session).Reagents;
    }
    internal QuestClaimStatus Claim(out TerminalQuestClaim result)
        => ClassicQuestClaims.TryClaim(Live, Expected, Goal, Template, null!, Live.GameObject, null!, out result, AfterCommit);
    internal QuestClaimReceipt MatchingReceipt() => new() { CharId = Character, QuestId = QuestId, GoalId = GoalId, QuestName = QuestName, GoalName = GoalName };
    internal Wizard Reload() {
        var wizard = CloneWizard(Saved); wizard.HasInitializedRuntimeStats = true;
        wizard.GameStats.Level = wizard.MagicSchoolBehavior.Level; wizard.GameStats.MagicSchool = wizard.MagicSchoolBehavior.MagicSchool;
        wizard.QuestBehavior.CurrentQuestInstances = Quests.Where(q => q.OwnerCharId == Character).Select(CloneQuest).ToList();
        wizard.InventoryBehavior.Items = [..Items.Where(item => wizard.InventoryBehavior.InventoryItemIds.Contains(item.m_globalID.Full)).Select(CloneItem)];
        wizard.AlchemyBehavior.Reagents = Reagents.Select(row => row with { }).ToList(); return wizard;
    }
    internal static MagicLevelInfo Table(int level) => new() { m_level = level, m_hitpoints = 100 + 20 * level,
        m_mana = 30 + 10 * level, m_pipChance = .125f * level, m_petEnergy = 12 + 3 * level };
    internal static Wizard NewWizard() {
        var wizard = new Wizard { CharId = Character, Zone = "QA/Claim", GameStats = new(MagicSchool.None, 0) {
            m_currentGold = 100, m_baseGoldPouch = 500, m_currentHitpoints = 23, m_baseHitpoints = 140,
            m_currentMana = 17, m_baseMana = 50, m_potionCharge = 1.25f, m_potionMax = 3 },
            MagicSchoolBehavior = new() { MagicSchool = MagicSchool.Fire, Level = 2, ExperiencePoints = 150, TrainingPoints = 5 },
            QuestBehavior = new(), SpellbookBehavior = new(), InventoryBehavior = new() { InventoryItemIds = [], Items = [] },
            StorageBehavior = new() { BankItemIds = [], Items = [] }, EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = [], SlotList = [] },
            AlchemyBehavior = new() { ReagentItemIds = [], Reagents = [] }, PetOwnerBehavior = new() { Eggs = [], PetHatchTimes = [] } };
        wizard.PetOwnerBehavior.SetEnergy(7); return wizard;
    }
    internal IDocumentSession Open() {
        Opened++; var session = DispatchProxy.Create<IDocumentSession, ClaimSession>(); var proxy = (ClaimSession)(object)session;
        Working = proxy; proxy.Wizard = CloneWizard(Saved); proxy.Quests = Quests.Select(CloneQuest).ToList();
        proxy.Dynamods = Dynamods.Select(CloneDynamods).ToList(); proxy.Items = Items.Select(CloneItem).ToList();
        proxy.Reagents = Reagents.Select(row => row with { }).ToList(); proxy.Receipts = Receipts.ToDictionary(pair => pair.Key, pair => CloneReceipt(pair.Value));
        proxy.Save = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Saves++; OnSave?.Invoke();
            if (FailSave && !Durable) throw new InvalidOperationException("authored write failure");
            Saved = CloneWizard(proxy.Wizard); Quests = proxy.Quests.Where(q => !proxy.Deleted.Contains(q))
                .Select(q => CloneQuest(proxy.Ignored.Contains(q) ? Quests.Single(original => original.ID == q.ID) : q)).ToList();
            Receipts = proxy.Receipts.ToDictionary(pair => pair.Key, pair => CloneReceipt(pair.Value)); Dynamods = proxy.Dynamods.Select(CloneDynamods).ToList();
            Items = proxy.Items.Select(row => CloneItem(proxy.Ignored.Contains(row) ? Items.Single(old => old.m_globalID.Full == row.m_globalID.Full) : row)).ToList();
            Reagents = proxy.Reagents.Select(row => row with { }).ToList();
            if (FailSave) throw new InvalidOperationException("authored durable write lost ACK");
        };
        proxy.DisposeSession = () => OnDispose?.Invoke(); return session;
    }
    internal Wizard Load(IDocumentSession session, ulong id) {
        Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(Live.CharId, id); OnLoad?.Invoke();
        return MissingWizard ? null! : ((ClaimSession)(object)session).Wizard;
    }
    internal static Wizard CloneWizard(Wizard source) {
        var clone = NewWizard(); clone.CharId = source.CharId; clone.AccountId = source.AccountId; clone.Zone = source.Zone;
        clone.GameStats = source.GameStats.CloneSnapshotWithGold(source.GameStats.m_currentGold); clone.GameStats.Level = 0; clone.GameStats.MagicSchool = MagicSchool.None;
        clone.MagicSchoolBehavior = new() { MagicSchool = source.MagicSchoolBehavior.MagicSchool, Level = source.MagicSchoolBehavior.Level,
            ExperiencePoints = source.MagicSchoolBehavior.ExperiencePoints, TrainingPoints = source.MagicSchoolBehavior.TrainingPoints, OverflowXp = source.MagicSchoolBehavior.OverflowXp };
        clone.QuestBehavior = source.QuestBehavior is null ? null! : new() { CurrentQuestIDs = source.QuestBehavior.CurrentQuestIDs?.ToList()! };
        if (clone.QuestBehavior is not null) foreach (var entry in source.QuestBehavior.Registry) clone.QuestBehavior.Registry[entry.Key] = entry.Value;
        clone.SpellbookBehavior.LearnedSpellTemplateIds = source.SpellbookBehavior.LearnedSpellTemplateIds.ToList();
        clone.SpellbookBehavior.TreasureCardTemplateIds = source.SpellbookBehavior.TreasureCardTemplateIds.ToList();
        clone.InventoryBehavior.InventoryItemIds = source.InventoryBehavior.InventoryItemIds.ToList(); clone.StorageBehavior.BankItemIds = source.StorageBehavior.BankItemIds.ToList();
        clone.EquipmentBehavior.EquippedItemIds = source.EquipmentBehavior.EquippedItemIds.ToList();
        clone.EquipmentBehavior.SlotList = source.EquipmentBehavior.SlotList.Select(slot => new EquipmentSlot { ItemId = slot.ItemId, SlotType = slot.SlotType, ItemName = slot.ItemName, EquippedSince = slot.EquippedSince }).ToList();
        clone.AlchemyBehavior.ReagentItemIds = source.AlchemyBehavior.ReagentItemIds.ToList();
        clone.PetOwnerBehavior.PublishCommittedEnergy(source.PetOwnerBehavior); return clone;
    }
    internal static QuestInstance CloneQuest(QuestInstance source) => new() { ID = source.ID, OwnerCharId = source.OwnerCharId, QuestName = source.QuestName,
        GoalProgress = source.GoalProgress.Select(goal => {
            var copy = new GoalInstance { ID = goal.ID, OwnerCharId = goal.OwnerCharId, GoalName = goal.GoalName, GoalType = goal.GoalType };
            typeof(GoalInstance).GetProperty(nameof(GoalInstance.CurrentProgress))!.SetValue(copy, goal.CurrentProgress); return copy;
        }).ToArray() };
    internal static WizClientObjectItem CloneItem(WizClientObjectItem source) => source with {
        m_inactiveBehaviors = source.m_inactiveBehaviors?.Select(behavior => behavior switch {
            ClientPetItemBehavior pet => (BehaviorInstance)(pet with { m_currentStats = pet.m_currentStats?.Select(stat => stat with { }).ToList(),
                m_maxStats = pet.m_maxStats?.Select(stat => stat with { }).ToList(),
                m_allTalents = pet.m_allTalents?.ToList(), m_expressedTalents = pet.m_expressedTalents?.ToList() }),
            ClientTimedItemBehavior timed => timed with { }, _ => behavior,
        }).ToList()!,
    };
    private static QuestClaimReceipt CloneReceipt(QuestClaimReceipt source) => new() { CharId = source.CharId, QuestId = source.QuestId, GoalId = source.GoalId,
        QuestName = source.QuestName, GoalName = source.GoalName, CompletedAtUtc = source.CompletedAtUtc, CompletedGoalIds = source.CompletedGoalIds.ToList(),
        Gold = source.Gold, Experience = source.Experience, TrainingPoints = source.TrainingPoints, PotionSlots = source.PotionSlots,
        LearnedSpells = source.LearnedSpells.ToList(), Rewards = source.Rewards.Select(row => new QuestClaimReward { Kind = row.Kind, TemplateId = row.TemplateId, ItemId = row.ItemId, Count = row.Count }).ToList() };
    private static DynamodSet CloneDynamods(DynamodSet source) => new(source.CharId) { Dynamods = source.Dynamods?.Select(row => new Dynamod { ZoneName = row.ZoneName, ClientTag = row.ClientTag, ModState = row.ModState }).ToList()! };
    public void Dispose() {
        WizardCollection.TestStoreScope.Value = _store; ClassicQuestClaims.TestScope.Value = _claim; ClassicStackRewards.TestScope.Value = _stack;
        WizardProgressionTransactions.TestScope.Value = _progression; WizardInventoryTransactions.TestRowsScope.Value = _items;
        WizardReagentCollection.TestRowsScope.Value = _reagents; RulesField.SetValue(null, _rules); QuestCardsField.SetValue(null, _questCards);
        ClassicBadges.Rules = _badgeRules; ClassicBadges.Persist = _badgePersist; ClassicBadges.Awarded = _badgeAwarded;
    }

    public class ClaimSession : DispatchProxy {
        internal Wizard Wizard = null!;
        internal List<Wizard>? Characters;
        internal List<QuestInstance> Quests = [];
        internal List<DynamodSet> Dynamods = [];
        internal List<WizClientObjectItem> Items = [];
        internal List<ClientReagentItem> Reagents = [];
        internal Dictionary<string, QuestClaimReceipt> Receipts = [];
        internal readonly HashSet<object> Deleted = new(ReferenceEqualityComparer.Instance), Ignored = new(ReferenceEqualityComparer.Instance);
        internal Action Save = null!, DisposeSession = null!;
        internal int Queries, ReceiptLoads;
        private bool _storedQuest;
        private IAdvancedSessionOperations? _advanced;
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            switch (method!.Name) {
                case "get_Advanced":
                    if (_advanced is null) { _advanced = DispatchProxy.Create<IAdvancedSessionOperations, ClaimAdvanced>(); ((ClaimAdvanced)(object)_advanced).Owner = this; }
                    return _advanced;
                case "Load": Assert.Equal(typeof(QuestClaimReceipt), method.GetGenericArguments()[0]); ReceiptLoads++; return Receipts.GetValueOrDefault((string)args![0]!);
                case "Query": Assert.False(_storedQuest, "new quest rows are staged only after all ownership queries"); Queries++; return typeof(ClaimSession).GetMethod(nameof(Query), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(method.GetGenericArguments()[0]).Invoke(this, [args]);
                case "Store": Store(args!); return null;
                case "Delete": Assert.Contains(args![0], Quests); Deleted.Add(args[0]!); return null;
                case "SaveChanges": Save(); return null;
                case "Dispose": DisposeSession(); return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
        private IRavenQueryable<T> Query<T>(object?[] args) {
            var collection = args.OfType<string>().FirstOrDefault();
            IEnumerable<T> rows;
            if (typeof(T) == typeof(QuestInstance)) { Assert.Equal(QuestInstanceCollection.CollectionName, collection); rows = Quests.Cast<T>(); }
            else if (typeof(T) == typeof(Wizard)) { Assert.Equal(WizardCollection.CollectionName, collection); rows = (Characters ?? [Wizard]).Cast<T>(); }
            else if (typeof(T) == typeof(DynamodSet)) { Assert.Equal("DynamicMod", collection); rows = Dynamods.Cast<T>(); }
            else throw new NotSupportedException(typeof(T).Name);
            var query = DispatchProxy.Create<IRavenQueryable<T>, ClaimQuery<T>>(); ((ClaimQuery<T>)(object)query).Rows = rows.AsQueryable();
            ((ClaimQuery<T>)(object)query).Self = query; return query;
        }
        private void Store(object?[] args) {
            switch (args[0]) {
                case QuestClaimReceipt receipt: Assert.Equal(ClassicQuestClaims.ReceiptId(receipt.CharId, receipt.QuestId), args.OfType<string>().Single()); Receipts.Add(args.OfType<string>().Single(), receipt); break;
                case QuestInstance quest: Assert.DoesNotContain(quest, Quests); Quests.Add(quest); _storedQuest = true; break;
                case DynamodSet dynamod: if (!Dynamods.Contains(dynamod)) Dynamods.Add(dynamod); break;
                case ClientReagentItem row: if (!Reagents.Contains(row)) Reagents.Add(row); break;
                case WizClientObjectItem item: if (!Items.Contains(item)) Items.Add(item); break;
                default: throw new NotSupportedException(args[0]?.GetType().Name);
            }
        }
    }
    public class ClaimQuery<T> : DispatchProxy {
        internal IQueryable<T> Rows = null!;
        internal IRavenQueryable<T> Self = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "Customize" => Self, "get_Provider" => Rows.Provider, "get_Expression" => Rows.Expression,
            "get_ElementType" => typeof(T), "GetEnumerator" => Rows.GetEnumerator(), _ => throw new NotSupportedException(method.Name),
        };
    }
    public class ClaimAdvanced : DispatchProxy {
        internal ClaimSession Owner = null!;
        private readonly IMetadataDictionary _metadata = DispatchProxy.Create<IMetadataDictionary, ClaimMetadata>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            if (method!.Name == "set_OptimisticConcurrencyMode") return null;
            if (method.Name == "GetMetadataFor") return _metadata;
            if (method.Name == "IgnoreChangesFor") { Assert.True(Owner.Ignored.Add(args![0]!)); return null; }
            throw new NotSupportedException(method.Name);
        }
    }
    public class ClaimMetadata : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            Assert.Equal("set_Item", method!.Name); Assert.Equal(Raven.Client.Constants.Documents.Metadata.Collection, args![0]);
            Assert.Contains(args[1], new[] { ClassicQuestClaims.ReceiptCollection, QuestInstanceCollection.CollectionName, "DynamicMod", WizardItemCollection.CollectionName, WizardReagentCollection.CollectionName }); return null;
        }
    }
}
