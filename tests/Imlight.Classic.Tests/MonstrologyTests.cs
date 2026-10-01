using System;
using System.Text.Json;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Game.Monstrology;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class MonstrologyTests {
    private static MonstrologyLedger State() => new() { OwnerId = 42 };
    private static ExtractionAward Award(string id = "duel1:round1:cast1:mob1") => new(id, 42, 123, 3, 15, true, true, true);
    private static AnimusCreation Create(string id = "create1") => new(id, 42, 123, 321, 2, 0, true);
    [Fact] public void AwardPersistsAndReplayDoesNotMintTwice() {
        var state = State();
        Assert.Equal(MonstrologyResult.Applied, MonstrologyRules.Award(state, Award(), new[] { 0, 10, 30 }));
        var restored = JsonSerializer.Deserialize<MonstrologyLedger>(JsonSerializer.Serialize(state))!;
        Assert.Equal(1, restored.Level); Assert.Equal(15, restored.Experience); Assert.Equal(3, restored.Animus[123]);
        Assert.Equal(MonstrologyResult.Replay, MonstrologyRules.Award(restored, Award(), new[] { 0, 10, 30 }));
        Assert.Equal(3, restored.Animus[123]);
        Assert.Equal(MonstrologyResult.Rejected, MonstrologyRules.Award(restored, Award() with { Animus = 100 }, new[] { 0, 10, 30 }));
    }
    [Fact] public void IneligibleMissedNotVictoriousForeignAndMalformedAwardsCannotMutate() {
        foreach (var award in new[] { Award() with { Eligible = false }, Award() with { SuccessfulHit = false },
            Award() with { Victory = false }, Award() with { OwnerId = 43 }, Award() with { Creature = 0 },
            Award() with { Animus = -1 }, Award() with { Experience = -1 }, Award() with { OperationId = "" } }) {
            var state = State();
            Assert.Equal(MonstrologyResult.Rejected, MonstrologyRules.Award(state, award, new[] { 0, 10 }));
            Assert.Empty(state.Animus); Assert.Empty(state.Extractions); Assert.Equal(0, state.Experience);
        }
    }
    [Fact] public void InvalidProgressionAndOverflowFailBeforeMutation() {
        foreach (var thresholds in new[] { new int[0], new[] { 1, 10 }, new[] { 0, 10, 10 }, new[] { 0, 10, 9 } })
            Assert.Equal(MonstrologyResult.Rejected, MonstrologyRules.Award(State(), Award(), thresholds));
        var state = State(); state.Experience = int.MaxValue;
        Assert.Equal(MonstrologyResult.Rejected, MonstrologyRules.Award(state, Award(), new[] { 0, 10 }));
        Assert.Empty(state.Animus);
    }
    [Fact] public void CreationDebitsOnceAndPersistsUndeliveredEntitlement() {
        var state = State(); MonstrologyRules.Award(state, Award(), new[] { 0, 10 });
        Assert.Equal(MonstrologyResult.Applied, MonstrologyRules.ReserveCreation(state, Create()));
        var restored = JsonSerializer.Deserialize<MonstrologyLedger>(JsonSerializer.Serialize(state))!;
        Assert.Equal(1, restored.Animus[123]); Assert.False(restored.Creations["create1"].Delivered);
        Assert.Equal((uint)321, restored.Creations["create1"].OutputTemplate);
        Assert.Equal(MonstrologyResult.Replay, MonstrologyRules.ReserveCreation(restored, Create()));
        Assert.Equal(MonstrologyResult.Rejected, MonstrologyRules.ReserveCreation(restored, Create() with { OutputTemplate = 999 }));
        Assert.Equal(MonstrologyResult.InsufficientAnimus, MonstrologyRules.ReserveCreation(restored, Create("create2")));
        Assert.Equal(1, restored.Animus[123]); Assert.Single(restored.Creations);
    }
    [Fact] public void UnverifiedGoldOrForeignCreationNeverDebits() {
        var state = State(); MonstrologyRules.Award(state, Award(), new[] { 0, 10 });
        Assert.Equal(MonstrologyResult.UnresolvedGoldContract, MonstrologyRules.ReserveCreation(state, Create() with { GoldCost = 1 }));
        Assert.Equal(MonstrologyResult.Rejected, MonstrologyRules.ReserveCreation(state, Create() with { ValidatedStockRecipe = false }));
        Assert.Equal(MonstrologyResult.Rejected, MonstrologyRules.ReserveCreation(state, Create() with { OwnerId = 1 }));
        Assert.Empty(state.Creations); Assert.Equal(3, state.Animus[123]);
    }
    [Fact] public void DeliveredCreationChecksBothBalancesAndOperationIdentity() {
        var state = State(); MonstrologyRules.Award(state,Award(),new[] {0,10});
        var request = Create() with { GoldCost = 20 };
        Assert.Equal(MonstrologyResult.InsufficientGold, MonstrologyRules.DeliverCard(state,request,19));
        Assert.Equal(3,state.Animus[123]); Assert.Empty(state.Creations);
        Assert.Equal(MonstrologyResult.Applied,MonstrologyRules.DeliverCard(state,request,20));
        Assert.True(state.Creations["create1"].Delivered); Assert.Equal(20,state.Creations["create1"].GoldCost);
        Assert.Equal(MonstrologyResult.Replay,MonstrologyRules.DeliverCard(state,request,0));
        Assert.Equal(1,state.Animus[123]);
        Assert.Equal(MonstrologyResult.Rejected,MonstrologyRules.DeliverCard(state,request with {GoldCost=19},20));
    }
    [Fact] public void DocumentIdentityIsStableAndRejectsUnknownOwner() {
        Assert.Equal("MonstrologyLedgers/42", MonstrologyRepository.DocumentId(42));
        Assert.Throws<ArgumentOutOfRangeException>(() => MonstrologyRepository.DocumentId(0));
    }
    [Fact] public void GeneratedEssenceProjectionPreservesOwnerTemplateAndCount() {
        var payload = MonstrologyContracts.Tracking(42, new ExtractionReceipt(123, 3, 15));
        var entry = Assert.Single(payload.m_essenceTrackingList);
        Assert.Equal(42UL, entry.m_ownerGID.Full); Assert.Equal(123U, entry.m_templateID);
        Assert.Equal(3, entry.m_essencesCollected); Assert.Equal(3, payload.m_collectedEssenceCount);
        Assert.Equal(0, payload.m_failedToCollectReason);
        Assert.Throws<ArgumentException>(() => MonstrologyContracts.Tracking(0, new ExtractionReceipt(123, 3, 15)));
    }
    [Fact] public void DisabledAndForeignHookCannotAccessRepository() {
        Assert.Equal(MonstrologyResult.Rejected, MonstrologyContracts.CommitExtraction(null!, false, new MonstrologySessionPolicy(), 42, Award(), new[] { 0, 10 }));
        Assert.Equal(MonstrologyResult.Rejected, MonstrologyContracts.CommitExtraction(null!, true, new MonstrologySessionPolicy(), 43, Award(), new[] { 0, 10 }));
    }
    [Fact] public void ActualGeneratedStockProgressionPayloadRoundTrips() {
        var source = new WIZARD2_53_PROTOCOL.MSG_UPDATEMONSTERMAGICXP { GlobalID = 42, XP = 15, Level = 2 };
        var result = Assert.IsType<WIZARD2_53_PROTOCOL.MSG_UPDATEMONSTERMAGICXP>(Assert.Single(MessageEncoder.Decode(MessageEncoder.Encode(source))!));
        Assert.Equal(source.GlobalID, result.GlobalID); Assert.Equal(source.XP, result.XP); Assert.Equal(source.Level, result.Level);
    }
}
