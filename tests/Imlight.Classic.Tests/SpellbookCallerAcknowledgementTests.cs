// CLASSIC: ordinary learning/training callers deliver only acknowledged native receipts and close uncertain sessions.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Game.Results.Handlers;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.World;
using Xunit;
using static Imlight.Classic.Tests.SpellbookAcknowledgementTests;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class SpellbookCallerAcknowledgementTests : IDisposable {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    public SpellbookCallerAcknowledgementTests() {
        EquipmentAttachConcurrencyTests.Configure("[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n[Login Server]\nMaxAllowedCharactersPerAccount=6\n");
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
    }
    public void Dispose() => ClassicRuntime.ResetForTests();

    [Fact]
    public async Task RewardCallerSendsOneHighBitTemplateAdditionAfterSaveAndAcceptsAQuietDuplicate() {
        using var f = new Fixture(); await using var delivery = new Delivery(); var before = Snapshot(f.Live);
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Empty(delivery.Messages);
            Assert.Equal(before, Snapshot(f.Live));
        };
        Assert.True(ResLearnSpellHandler.LearnAcknowledged(f.Live, Learned, delivery.Actor, Owner));
        var messages = await delivery.Drain();
        Assert.Equal(unchecked((int)Learned), Assert.IsType<WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK>(Assert.Single(messages)).SpellID);
        Assert.Equal(1, f.Saves); Assert.Contains(Learned, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds);
        Assert.True(ResLearnSpellHandler.LearnAcknowledged(f.Live, Learned, delivery.Actor, Owner));
        Assert.Single(await delivery.Drain()); Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData("before")] [InlineData("lost")] [InlineData("publication")]
    public async Task RewardCallerClosesUncertainSnapshotsWithoutNativeSuccessOrAnExtraSave(string failure) {
        using var f = new Fixture(); await using var delivery = new Delivery(); var before = Snapshot(f.Live);
        f.FailSave = failure != "publication"; f.Durable = failure == "lost";
        if (failure == "publication") f.Dependencies.BeforePublish = _ => throw new InvalidOperationException("authored caller publication failure");
        f.OnDispose = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        };
        Assert.False(ResLearnSpellHandler.LearnAcknowledged(f.Live, Learned, delivery.Actor, Owner));
        Assert.Equal("Close", Assert.Single(await delivery.Drain())); Assert.Equal(before, Snapshot(f.Live));
        Assert.Equal(1, f.Saves); Assert.Equal(failure != "before", f.Saved.SpellbookBehavior.LearnedSpellTemplateIds.Contains(Learned));
        Assert.False(ResLearnSpellHandler.LearnAcknowledged(f.Live, Learned, delivery.Actor, Owner));
        Assert.Equal(1, f.Opened); Assert.Equal(1, f.Saves);
        Assert.All(await delivery.Drain(), message => Assert.Equal("Close", message));
    }

    [Fact]
    public async Task MissingAuthorityIsRefusalRatherThanAlreadyKnownSuccess() {
        using var f = new Fixture { Missing = true }; await using var delivery = new Delivery();
        Assert.False(ResLearnSpellHandler.LearnAcknowledged(f.Live, Learned, delivery.Actor, Owner));
        Assert.Empty(await delivery.Drain()); Assert.Equal(0, f.Saves);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public void PublicLearningAdaptersUseTheSelectedTransactionWithoutPublishingEarly() {
        using var f = new Fixture(); var learnedAlias = f.Live.SpellbookBehavior.LearnedSpellTemplateIds;
        f.OnSave = () => { Assert.DoesNotContain(Learned, learnedAlias); Assert.True(WizardCollection.HoldsWriteLane); };
        Assert.True(f.Live.LearnSpell(f.Dependencies.Spell(Learned)));
        Assert.Same(learnedAlias, f.Live.SpellbookBehavior.LearnedSpellTemplateIds);
        Assert.False(WizardCollection.LearnSpell(f.Live, Learned)); Assert.Equal(1, f.Saves);
        Assert.Contains(Learned, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds);
    }

    [Fact]
    public void TrainingCallerPublishesAdditionSavedBalanceAndCompletionInTheAckLaneAndChargesOnce() {
        using var f = new Fixture(); var sent = new List<IMessage>(); var closes = 0;
        f.OnSave = () => Assert.Empty(sent);
        void Send(IMessage message) {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, f.Saves);
            Assert.Equal(4, f.Saved.MagicSchoolBehavior.TrainingPoints); Assert.Equal(4, f.Live.MagicSchoolBehavior.TrainingPoints);
            sent.Add(message);
        }
        var entry = new NPCSpellEntry { TemplateID = Learned, Level = 5 };
        Assert.Equal(SpellbookMutationStatus.Committed, TrainService.TrainAcknowledged(f.Live, entry, 7, Send, () => closes++, Owner));
        Assert.Equal(new[] { typeof(WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK), typeof(WIZARD_12_PROTOCOL.MSG_UPDATETRAINING),
            typeof(WIZARD_12_PROTOCOL.MSG_SPELLTRAINCOMPLETE) }, sent.Select(message => message.GetType()));
        Assert.Equal(unchecked((int)Learned), ((WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK)sent[0]).SpellID);
        Assert.Equal(4, ((WIZARD_12_PROTOCOL.MSG_UPDATETRAINING)sent[1]).TrainingPoints);
        var completion = (WIZARD_12_PROTOCOL.MSG_SPELLTRAINCOMPLETE)sent[2];
        Assert.Equal((ulong)Learned, completion.SpellID); Assert.Equal(1, completion.Success); Assert.Equal("WizTraining_00000040", (string)completion.DisplayText);
        Assert.Equal(SpellbookMutationStatus.Unchanged, TrainService.TrainAcknowledged(f.Live, entry, 7, Send, () => closes++, Owner));
        Assert.Equal(1, f.Saves); Assert.Equal(3, sent.Count); Assert.Equal(0, closes);
    }

    [Theory]
    [InlineData("before")] [InlineData("lost")] [InlineData("publication")]
    public void TrainingCallerClosesAfterUncertainOutcomeWithoutRefundOrSuccessReplay(string failure) {
        using var f = new Fixture(); var before = Snapshot(f.Live); var sent = new List<IMessage>(); var closes = 0;
        f.FailSave = failure != "publication"; f.Durable = failure == "lost";
        if (failure == "publication") f.Dependencies.BeforePublish = _ => throw new InvalidOperationException("authored trainer publication failure");
        var entry = new NPCSpellEntry { TemplateID = Learned, Level = 5 };
        Assert.Equal(SpellbookMutationStatus.Refused, TrainService.TrainAcknowledged(f.Live, entry, 7, sent.Add, () => closes++, Owner));
        Assert.Equal(1, closes); Assert.Empty(sent); Assert.Equal(before, Snapshot(f.Live)); Assert.Equal(1, f.Saves);
        Assert.Equal(failure == "before" ? 5 : 4, f.Saved.MagicSchoolBehavior.TrainingPoints);
        Assert.Equal(failure != "before", f.Saved.SpellbookBehavior.LearnedSpellTemplateIds.Contains(Learned));
        Assert.Equal(SpellbookMutationStatus.Refused, TrainService.TrainAcknowledged(f.Live, entry, 7, sent.Add, () => closes++, Owner));
        Assert.Equal(1, f.Opened); Assert.Equal(1, f.Saves); Assert.Empty(sent);
    }

    [Fact]
    public void TrainingPacketPublicationFailureBlocksALaterOldBookSave() {
        using var f = new Fixture(); var closes = 0; var entry = new NPCSpellEntry { TemplateID = Learned, Level = 5 };
        Assert.Equal(SpellbookMutationStatus.Refused, TrainService.TrainAcknowledged(f.Live, entry, 7,
            _ => throw new InvalidOperationException("authored socket publication failure"), () => closes++, Owner));
        Assert.Equal(1, closes); Assert.Equal(1, f.Saves); Assert.Equal(4, f.Saved.MagicSchoolBehavior.TrainingPoints);
        WizardCollection.UpdateCharacterSpellbookBehavior(f.Live);
        Assert.Equal(1, f.Opened); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public async Task TutorialRewardWinsTheLaneBeforePaidTrainingAndPreventsDuplicatePayment() {
        using var f = new Fixture(); await using var delivery = new Delivery();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var attempted = new ManualResetEventSlim();
        f.Dependencies.Prepare = _ => { entered.Set(); Assert.True(release.Wait(Timeout)); return true; };
        var reward = Task.Run(() => ResLearnSpellHandler.LearnAcknowledged(f.Live, Learned, delivery.Actor, Owner));
        Task<SpellbookMutationStatus>? trainer = null; var trainingMessages = new List<IMessage>();
        try {
            Assert.True(entered.Wait(Timeout));
            trainer = Task.Run(() => {
                attempted.Set(); return TrainService.TrainAcknowledged(f.Live,
                    new NPCSpellEntry { TemplateID = Learned, Level = 5 }, 7, trainingMessages.Add,
                    () => throw new InvalidOperationException("unexpected close"), Owner);
            });
            Assert.True(attempted.Wait(Timeout)); Assert.False(trainer.IsCompleted); Assert.Equal(1, f.Opened);
        }
        finally { release.Set(); }
        Assert.True(await reward); Assert.Equal(SpellbookMutationStatus.Unchanged, await trainer!);
        Assert.Equal(1, f.Saves); Assert.Equal(5, f.Saved.MagicSchoolBehavior.TrainingPoints);
        Assert.Single(await delivery.Drain()); Assert.Empty(trainingMessages);
    }

    [Fact]
    public void FreshAttachUsesTheNativeOrdinaryTrackerSentinel() {
        using var f = new Fixture();
        Assert.All(f.Saved.SpellbookBehavior.GetClientBehaviorInstance().m_spellIDList,
            tracker => { Assert.False(tracker.m_isRetired); Assert.Equal(-1, tracker.m_tieredSpellGroupIndex); });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExclusionCallerEchoesTheOriginalWireIdentityOnlyAfterSaving(bool legacyHash) {
        using var f = new Fixture(); var sent = new List<IMessage>(); var closes = 0;
        var request = new WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST {
            SpellID = unchecked((int)(legacyHash ? Hash : Learned)), DeckID = Item, Exclude = 1,
        };
        var learnedBefore = f.Saved.SpellbookBehavior.LearnedSpellTemplateIds.ToArray();
        var before = Snapshot(f.Live);
        f.OnSave = () => { Assert.Empty(sent); Assert.Equal(before, Snapshot(f.Live)); };
        void Send(IMessage message) {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, f.Saves);
            Assert.Contains(Learned, f.Saved.SpellbookBehavior.ExcludedItemSpellIds[Item]); sent.Add(message);
        }
        Assert.Equal(SpellbookMutationStatus.Committed, SpellbookService.UpdateItemSpellExclusionAcknowledged(
            f.Live, request, Send, () => closes++, Owner, wire => {
                Assert.Equal(request.SpellID, wire); return Learned;
            }));
        var response = Assert.IsType<WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST>(Assert.Single(sent));
        Assert.Equal(request.SpellID, response.SpellID); Assert.Equal(Item, response.DeckID);
        Assert.Equal(1, response.Exclude); Assert.Equal(1, response.Success); Assert.Equal(0, closes);
        Assert.Equal(learnedBefore, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds);
        Assert.DoesNotContain(LiveOnly, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds);
        Assert.Equal(SpellbookMutationStatus.Unchanged, SpellbookService.UpdateItemSpellExclusionAcknowledged(
            f.Live, request, Send, () => closes++, Owner, _ => Learned));
        Assert.Equal(1, f.Saves); Assert.Equal(2, sent.Count);
        Assert.Equal(1, Assert.IsType<WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST>(sent[1]).Success);
    }

    [Theory]
    [InlineData("before")] [InlineData("lost")] [InlineData("publication")]
    public void ExclusionCallerClosesUnknownOutcomesWithoutSuccessOrFailureEcho(string failure) {
        using var f = new Fixture(); var before = Snapshot(f.Live); var sent = new List<IMessage>(); var closes = 0;
        f.FailSave = failure != "publication"; f.Durable = failure == "lost";
        if (failure == "publication") f.Dependencies.BeforePublish = _ => throw new InvalidOperationException("authored exclusion publication failure");
        var request = new WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST {
            SpellID = unchecked((int)Learned), DeckID = Item, Exclude = 1,
        };
        Assert.Equal(SpellbookMutationStatus.Refused, SpellbookService.UpdateItemSpellExclusionAcknowledged(
            f.Live, request, sent.Add, () => closes++, Owner, _ => Learned));
        Assert.Equal(1, closes); Assert.Empty(sent); Assert.Equal(before, Snapshot(f.Live)); Assert.Equal(1, f.Saves);
        Assert.Equal(failure != "before", f.Saved.SpellbookBehavior.ExcludedItemSpellIds[Item].Contains(Learned));
        Assert.Equal(SpellbookMutationStatus.Refused, SpellbookService.UpdateItemSpellExclusionAcknowledged(
            f.Live, request, sent.Add, () => closes++, Owner, _ => Learned));
        Assert.Equal(1, f.Opened); Assert.Equal(1, f.Saves); Assert.Empty(sent);
    }

    [Theory]
    [InlineData("missing")] [InlineData("unresolved")] [InlineData("foreign-deck")] [InlineData("invalid-flag")]
    public void ExclusionCallerReturnsARefusalEchoWithoutSavingOrClaimingSuccess(string refusal) {
        using var f = new Fixture { Missing = refusal == "missing" }; var sent = new List<IMessage>(); var closes = 0;
        var request = new WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST {
            SpellID = unchecked((int)Learned), DeckID = refusal == "foreign-deck" ? Item + 1 : Item,
            Exclude = refusal == "invalid-flag" ? (byte)2 : (byte)1,
        };
        Assert.Equal(SpellbookMutationStatus.Refused, SpellbookService.UpdateItemSpellExclusionAcknowledged(
            f.Live, request, sent.Add, () => closes++, Owner, _ => refusal == "unresolved" ? 0 : Learned));
        var response = Assert.IsType<WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST>(Assert.Single(sent));
        Assert.Equal(0, response.Success); Assert.Equal(request.SpellID, response.SpellID);
        Assert.Equal(request.DeckID, response.DeckID); Assert.Equal(request.Exclude, response.Exclude);
        Assert.Equal(0, f.Saves); Assert.Equal(0, closes);
    }

    [Theory]
    [InlineData("resolver")] [InlineData("failure-preparation")] [InlineData("duplicate-preparation")]
    public void ExclusionCallerKnownPreparationFailuresDoNotQuarantineACertainSnapshot(string failure) {
        using var f = new Fixture(); var sent = new List<IMessage>(); var closes = 0;
        if (failure == "duplicate-preparation") f.Saved.SpellbookBehavior.ExcludedItemSpellIds[Item].Add(Learned);
        if (failure != "resolver") f.Dependencies.Prepare = _ => throw new InvalidOperationException("authored echo preparation refusal");
        var request = new WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST {
            SpellID = unchecked((int)Learned), DeckID = Item, Exclude = 1,
        };
        Assert.Equal(SpellbookMutationStatus.Refused, SpellbookService.UpdateItemSpellExclusionAcknowledged(
            f.Live, request, sent.Add, () => closes++, Owner, _ => failure switch {
                "resolver" => throw new InvalidOperationException("authored resolver refusal"),
                "failure-preparation" => 0,
                _ => Learned,
            }));
        Assert.Equal(0, f.Saves); Assert.Equal(0, closes); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        if (failure == "resolver") Assert.Equal(0,
            Assert.IsType<WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST>(Assert.Single(sent)).Success);
        else Assert.Empty(sent);
    }

    private sealed record Drain(TaskCompletionSource<object[]> Complete);
    private sealed class Delivery : IAsyncDisposable {
        private readonly ActorSystem _system = ActorSystem.Create("spellbook-caller-" + Guid.NewGuid().ToString("N"));
        internal readonly ConcurrentQueue<object> Messages = new();
        internal readonly IActorRef Actor;
        internal Delivery() => Actor = _system.ActorOf(Props.Create(() => new DeliveryActor(Messages)), "delivery");
        internal async Task<object[]> Drain() {
            var result = new TaskCompletionSource<object[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            Actor.Tell(new Drain(result)); return await result.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        public async ValueTask DisposeAsync() => await _system.Terminate();
    }
    private sealed class DeliveryActor : ReceiveActor {
        public DeliveryActor(ConcurrentQueue<object> messages) {
            Receive<Drain>(request => request.Complete.TrySetResult(messages.ToArray()));
            ReceiveAny(message => messages.Enqueue(message));
        }
    }
}
