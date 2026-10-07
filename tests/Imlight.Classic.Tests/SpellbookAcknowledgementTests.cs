// CLASSIC: learned spells, trainer debit and selected item-card exclusions use fresh authority and publish after ACK.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Xunit;
using Action = System.Action;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class SpellbookAcknowledgementTests : IDisposable {
    internal const ulong Character = (1UL << 40) + 941771, Owner = (1UL << 44) + 71, Item = (1UL << 42) + 113;
    internal const uint Prior = 781102, Learned = 0xf012341f, LiveOnly = 781104, OtherExcluded = 781105, Hash = 0xe042341f;

    public SpellbookAcknowledgementTests() {
        EquipmentAttachConcurrencyTests.Configure("[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n[Login Server]\nMaxAllowedCharactersPerAccount=6\n");
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
    }
    public void Dispose() => ClassicRuntime.ResetForTests();

    [Theory]
    [InlineData("learn")] [InlineData("train")] [InlineData("exclude")]
    public void RealNativeFallbackPreparesTheGeneratedFramesBeforeSaving(string operation) {
        using var f = new Fixture(); f.Dependencies.Prepare = null;
        Assert.False(WizardSpellbookTransactions.PrepareNative(null!));
        Assert.Equal(SpellbookMutationStatus.Committed, Run(f, operation, out var receipt, prepared => {
            Assert.Equal(0, f.Saves); Assert.True(WizardCollection.HoldsWriteLane);
            foreach (var message in prepared.Messages) {
                Assert.True(WizardSpellbookTransactions.PrepareNative(message));
                var decoded = Assert.Single(MessageEncoder.Decode(MessageEncoder.Encode(message))!);
                Assert.Equal(message.GetType(), decoded.GetType());
                if (decoded is WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK add) Assert.Equal(unchecked((int)Learned), add.SpellID);
                if (decoded is WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST exclusion) {
                    Assert.Equal(Item, exclusion.DeckID); Assert.Equal(unchecked((int)Learned), exclusion.SpellID);
                    Assert.Equal(1, exclusion.Success);
                }
            }
            return true;
        }));
        Assert.Equal(1, f.Saves); Assert.NotEmpty(receipt.Messages); Assert.Empty(f.Prepared);
    }

    [Theory]
    [InlineData("learn")] [InlineData("train")] [InlineData("exclude")]
    public void OneFreshWritePublishesOnlySelectedStateAndPreservesManagedNativeAndDeckAliases(string operation) {
        using var f = new Fixture(); var live = f.Live; var book = live.SpellbookBehavior;
        var learned = book.LearnedSpellTemplateIds; var exclusions = book.ExcludedItemSpellIds; var selected = exclusions[Item];
        var unrelated = exclusions[Item + 1]; var cards = book.TreasureCardTemplateIds; var ledger = book.DeckTreasureCards;
        var temporary = book.TemporarySpells; var deckCards = book.SpellList; var school = live.MagicSchoolBehavior;
        var native = f.Native; var nativeList = native.m_spellIDList; var retained = nativeList[0]; var nativeSchool = f.NativeSchool;
        var objectInstance = live.GameObject; var behaviors = objectInstance.m_inactiveBehaviors; var before = Snapshot(live);
        var prepared = 0; var published = 0;
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(before, Snapshot(live));
            Assert.Equal(operation == "train" ? 4 : 5, f.Working!.Wizard.MagicSchoolBehavior.TrainingPoints);
            Assert.All(f.Working.Items, row => Assert.Contains(row, f.Working.Ignored));
            Assert.DoesNotContain(f.Working.Wizard, f.Working.Ignored);
        };
        Assert.Equal(SpellbookMutationStatus.Committed, Run(f, operation, out var receipt, result => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(0, f.Saves); Assert.Equal(before, Snapshot(live));
            Assert.Equal(operation == "train" ? 4 : 5, result.TrainingPoints);
            Assert.IsAssignableFrom<IReadOnlyList<uint>>(result.LearnedSpells); prepared++; return true;
        }, result => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, f.Saves);
            Assert.Same(book, live.SpellbookBehavior); Assert.Same(native, f.Native); published++;
        }));
        Assert.Equal(1, f.Saves); Assert.Equal(1, prepared); Assert.Equal(1, published);
        Assert.Same(book, live.SpellbookBehavior); Assert.Same(learned, book.LearnedSpellTemplateIds);
        Assert.Same(exclusions, book.ExcludedItemSpellIds); Assert.Same(selected, exclusions[Item]); Assert.Same(unrelated, exclusions[Item + 1]);
        Assert.Same(cards, book.TreasureCardTemplateIds); Assert.Same(ledger, book.DeckTreasureCards);
        Assert.Same(temporary, book.TemporarySpells); Assert.Same(deckCards, book.SpellList); Assert.Same(school, live.MagicSchoolBehavior);
        Assert.Same(objectInstance, live.GameObject); Assert.Same(behaviors, objectInstance.m_inactiveBehaviors);
        Assert.Same(native, f.Native); Assert.Same(nativeList, native.m_spellIDList); Assert.Same(retained, nativeList[0]);
        Assert.True(retained.m_isRetired); Assert.Equal(17, retained.m_tieredSpellGroupIndex); Assert.Same(nativeSchool, f.NativeSchool);
        Assert.Equal(32, live.GameStats.m_currentMana); Assert.Equal(100, f.Saved.GameStats.m_currentGold);
        Assert.Equal("original item", f.Items[0].m_debugName); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
        if (operation == "exclude") {
            Assert.Equal(new[] { Prior, LiveOnly }, learned); Assert.Equal(new[] { Prior, Learned }.OrderBy(id => id), selected.OrderBy(id => id));
            Assert.Equal(new[] { Prior }, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds); Assert.Equal(77, school.TrainingPoints);
            var message = Assert.IsType<WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST>(Assert.Single(receipt.Messages));
            Assert.Equal(Item, message.DeckID); Assert.Equal(unchecked((int)Learned), message.SpellID); Assert.Equal(1, message.Success);
        }
        else {
            Assert.Equal(new[] { Prior, Learned }, learned); Assert.Equal(learned, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds);
            var added = nativeList[1]; Assert.Equal(Learned, added.m_spellID); Assert.False(added.m_isRetired); Assert.Equal(-1, added.m_tieredSpellGroupIndex);
            Assert.Equal(operation == "train" ? 4 : 77, school.TrainingPoints);
            Assert.Equal(operation == "train" ? 4 : 77, nativeSchool.m_trainingPoints);
            Assert.Equal(operation == "train" ? 3 : 1, receipt.Messages.Count);
            Assert.Equal(unchecked((int)Learned), Assert.IsType<WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK>(receipt.Messages[0]).SpellID);
            if (operation == "train") {
                Assert.Equal(4, Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATETRAINING>(receipt.Messages[1]).TrainingPoints);
                Assert.Equal("WizTraining_00000040", Assert.IsType<WIZARD_12_PROTOCOL.MSG_SPELLTRAINCOMPLETE>(receipt.Messages[2]).DisplayText);
            }
        }
    }

    [Theory]
    [InlineData("learn")] [InlineData("train")]
    public void SavedKnownLiveMissingIsUnchangedRehydrationWithoutSavePaymentPreparationOrNotice(string operation) {
        using var f = new Fixture(); f.Saved.SpellbookBehavior.LearnedSpellTemplateIds.Add(Learned);
        var book = f.Live.SpellbookBehavior; var ids = book.LearnedSpellTemplateIds; var native = f.Native; var nativeIds = native.m_spellIDList;
        var callbacks = 0;
        f.Dependencies.Prepare = _ => throw new InvalidOperationException("unchanged must not prepare packets");
        Assert.Equal(SpellbookMutationStatus.Unchanged, Run(f, operation, out var receipt, _ => { callbacks++; return true; }, _ => callbacks++));
        Assert.Equal(0, callbacks); Assert.Equal(0, f.Saves); Assert.Equal(1, f.Opened); Assert.Empty(receipt.Messages);
        Assert.Same(book, f.Live.SpellbookBehavior); Assert.Same(ids, book.LearnedSpellTemplateIds);
        Assert.Same(native, f.Native); Assert.Same(nativeIds, native.m_spellIDList);
        Assert.Equal(new[] { Prior, Learned }, ids); Assert.Equal(ids, nativeIds.Select(tracker => tracker.m_spellID));
        Assert.Equal(5, f.Saved.MagicSchoolBehavior.TrainingPoints); Assert.Equal(77, f.Live.MagicSchoolBehavior.TrainingPoints);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public void LiveKnownSavedMissingStillCommitsOneFreshAddition() {
        using var f = new Fixture(); f.Live.SpellbookBehavior.LearnedSpellTemplateIds.Add(Learned);
        Assert.Equal(SpellbookMutationStatus.Committed, Run(f, "learn", out _));
        Assert.Equal(1, f.Saves); Assert.Equal(1, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds.Count(id => id == Learned));
        Assert.Equal(new[] { Prior, Learned }, f.Live.SpellbookBehavior.LearnedSpellTemplateIds);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void SameSelectedExclusionReconcilesOnlyThatFreshSetWithoutSavingOrNotifying(bool exclude) {
        using var f = new Fixture();
        if (exclude) f.Saved.SpellbookBehavior.ExcludedItemSpellIds[Item].Add(Learned);
        else f.Live.SpellbookBehavior.ExcludedItemSpellIds[Item].Add(Learned);
        var selected = f.Live.SpellbookBehavior.ExcludedItemSpellIds[Item]; var unrelated = f.Live.SpellbookBehavior.ExcludedItemSpellIds[Item + 1];
        var learned = f.Live.SpellbookBehavior.LearnedSpellTemplateIds.ToArray();
        var callbacks = 0;
        Assert.Equal(SpellbookMutationStatus.Unchanged, WizardSpellbookTransactions.TrySetItemSpellExclusion(f.Live, Item, Learned, exclude,
            out var receipt, _ => { callbacks++; return true; }, _ => callbacks++, Owner));
        Assert.Equal(0, f.Saves); Assert.Equal(0, callbacks); Assert.Empty(receipt.Messages);
        Assert.Same(selected, f.Live.SpellbookBehavior.ExcludedItemSpellIds[Item]); Assert.Same(unrelated, f.Live.SpellbookBehavior.ExcludedItemSpellIds[Item + 1]);
        Assert.Equal(f.Saved.SpellbookBehavior.ExcludedItemSpellIds[Item].OrderBy(id => id), selected.OrderBy(id => id));
        Assert.Equal(learned, f.Live.SpellbookBehavior.LearnedSpellTemplateIds);
    }

    [Fact]
    public void RemovingTheLastSelectedExclusionClearsTheHeldSetAndRetainsOtherKeys() {
        using var f = new Fixture(); f.Saved.SpellbookBehavior.ExcludedItemSpellIds[Item] = [Learned];
        f.Live.SpellbookBehavior.ExcludedItemSpellIds[Item] = [Learned]; var held = f.Live.SpellbookBehavior.ExcludedItemSpellIds[Item];
        var unrelated = f.Live.SpellbookBehavior.ExcludedItemSpellIds[Item + 1];
        Assert.Equal(SpellbookMutationStatus.Committed, WizardSpellbookTransactions.TrySetItemSpellExclusion(f.Live, Item, Learned, false, out _));
        Assert.Empty(held); Assert.False(f.Live.SpellbookBehavior.ExcludedItemSpellIds.ContainsKey(Item));
        Assert.False(f.Saved.SpellbookBehavior.ExcludedItemSpellIds.ContainsKey(Item)); Assert.Same(unrelated, f.Live.SpellbookBehavior.ExcludedItemSpellIds[Item + 1]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExclusionEchoRetainsTheCallersVerifiedWireIdentity(bool legacyHash) {
        using var f = new Fixture(); var wire = unchecked((int)(legacyHash ? Hash : Learned));
        Assert.Equal(SpellbookMutationStatus.Committed, WizardSpellbookTransactions.TrySetItemSpellExclusion(f.Live, Item, Learned, true,
            out var receipt, expectedAccountId: Owner, wireSpellId: wire));
        Assert.Equal(wire, Assert.IsType<WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST>(Assert.Single(receipt.Messages)).SpellID);
        Assert.Equal(Learned, receipt.TemplateId); Assert.Equal(Hash, receipt.SpellHash);
    }

    [Theory]
    [InlineData("missing-character")] [InlineData("foreign-character")] [InlineData("foreign-account")]
    [InlineData("duplicate-character")] [InlineData("missing-book")] [InlineData("missing-list")]
    [InlineData("saved-permanent-id")] [InlineData("live-character-id")] [InlineData("live-object-id")]
    [InlineData("live-permanent-id")] [InlineData("authenticated-account")] [InlineData("account-membership")]
    [InlineData("spell-identity")] [InlineData("missing-spell")]
    [InlineData("saved-zero")] [InlineData("saved-duplicate")]
    [InlineData("native-zero")] [InlineData("native-duplicate")]
    public void InvalidFreshOrCallerIdentityIsRefusedWithoutFabricatingDuplicateSuccess(string defect) {
        using var f = new Fixture();
        switch (defect) {
            case "missing-character": f.Missing = true; break;
            case "foreign-character": f.Saved.CharId += 1UL << 32; break;
            case "foreign-account": f.Saved.AccountId += 1UL << 32; break;
            case "duplicate-character": f.DuplicateCharacter = true; break;
            case "missing-book": f.Saved.SpellbookBehavior = null!; break;
            case "missing-list": f.Saved.SpellbookBehavior.LearnedSpellTemplateIds = null!; break;
            case "saved-permanent-id": f.Saved.GameObject.m_permID = f.Saved.GameObjectID + (1UL << 32); break;
            case "live-character-id": f.Live.GameObject.m_characterId = Character + (1UL << 32); break;
            case "live-object-id": f.Live.GameObject.m_globalID = f.Live.GameObjectID + (1UL << 32); break;
            case "live-permanent-id": f.Live.GameObject.m_permID = f.Live.GameObjectID + (1UL << 32); break;
            case "account-membership": f.Live.Account!.CharacterIds.Clear(); break;
            case "spell-identity": f.Dependencies.Spell = _ => new Spell { m_templateID = Learned + 1 }; break;
            case "missing-spell": f.Dependencies.Spell = _ => null!; break;
            case "saved-zero": f.Saved.SpellbookBehavior.LearnedSpellTemplateIds.Add(0); break;
            case "saved-duplicate": f.Saved.SpellbookBehavior.LearnedSpellTemplateIds.Add(Prior); break;
            case "native-zero": f.Native.m_spellIDList.Add(new SpellIDTracker { m_spellID = 0 }); break;
            case "native-duplicate": f.Native.m_spellIDList.Add(new SpellIDTracker { m_spellID = Prior }); break;
        }
        var before = Snapshot(f.Live);
        Assert.Equal(SpellbookMutationStatus.Refused, Run(f, "learn", out var receipt, owner: defect == "authenticated-account" ? Owner + (1UL << 32) : Owner));
        Assert.Null(receipt); Assert.Equal(0, f.Saves); Assert.Equal(before, Snapshot(f.Live));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("missing")] [InlineData("foreign")] [InlineData("collision")]
    [InlineData("banked")] [InlineData("unreferenced")]
    public void ExclusionCannotAdoptAnUnownedMissingOrCollidingFullItemIdentity(string defect) {
        using var f = new Fixture();
        switch (defect) {
            case "missing": f.Items.RemoveAt(0); break;
            case "foreign": f.Items[0].m_characterId = Character + (1UL << 32); break;
            case "collision": f.Items.Add(CloneItem(f.Items[0])); break;
            case "banked": f.Saved.EquipmentBehavior.EquippedItemIds.Clear(); f.Saved.StorageBehavior.BankItemIds.Add(Item); break;
            case "unreferenced": f.Saved.EquipmentBehavior.EquippedItemIds.Clear(); break;
        }
        var before = Snapshot(f.Live); Assert.Equal(SpellbookMutationStatus.Refused, Run(f, "exclude", out var receipt));
        Assert.Null(receipt); Assert.Equal(0, f.Saves); Assert.Equal(before, Snapshot(f.Live)); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("points")] [InlineData("level")] [InlineData("prerequisite")]
    [InlineData("template-width")] [InlineData("prerequisite-width")] [InlineData("fresh-eligibility")]
    [InlineData("negative-level")] [InlineData("missing-template")]
    public void TrainerRulesUseSavedSchoolLevelPrerequisiteAndPointsWithoutAnySeparateDebit(string defect) {
        using var f = new Fixture(); var entry = new NPCSpellEntry { TemplateID = Learned, Level = 5 };
        switch (defect) {
            case "points": f.Saved.MagicSchoolBehavior.TrainingPoints = 0; break;
            case "level": f.Saved.MagicSchoolBehavior.Level = 1; break;
            case "prerequisite": entry.RequiredSpellID = LiveOnly; break;
            case "template-width": entry.TemplateID += 1UL << 32; break;
            case "prerequisite-width": entry.RequiredSpellID = (1UL << 32) + Prior; break;
            case "negative-level": entry.Level = -1; break;
            case "missing-template": f.Dependencies.Template = _ => null!; break;
        }
        var before = Snapshot(f.Live);
        Assert.Equal(SpellbookMutationStatus.Refused, WizardSpellbookTransactions.TryTrain(f.Live, entry, 7, out var receipt,
            expectedAccountId: Owner, validateFresh: saved => defect != "fresh-eligibility"));
        Assert.Null(receipt); Assert.Equal(0, f.Saves); Assert.Equal(before, Snapshot(f.Live)); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public void OwnSchoolTrainingUsesTheFreshSchoolAndCostsZeroEvenWhenLiveSchoolDiffers() {
        using var f = new Fixture(); f.Saved.MagicSchoolBehavior.MagicSchool = MagicSchool.Ice;
        f.Saved.MagicSchoolBehavior.TrainingPoints = 0;
        Assert.Equal(SpellbookMutationStatus.Committed, Run(f, "train", out var receipt));
        Assert.Equal(0, receipt.Cost); Assert.Equal(0, f.Saved.MagicSchoolBehavior.TrainingPoints);
        Assert.Equal(MagicSchool.Fire, f.Live.MagicSchoolBehavior.MagicSchool); Assert.Equal(0, f.Live.MagicSchoolBehavior.TrainingPoints);
        Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData("missing-book")] [InlineData("duplicate-book")]
    public void MalformedAttachedNativeBookRefusesBeforeTheSavedMutation(string defect) {
        using var f = new Fixture();
        if (defect == "missing-book") f.Live.GameObject.m_inactiveBehaviors.Remove(f.Native);
        else f.Live.GameObject.m_inactiveBehaviors.Add(new ClientSpellbookBehavior { m_spellIDList = [] });
        var before = Snapshot(f.Live);
        Assert.Equal(SpellbookMutationStatus.Refused, Run(f, "learn", out var receipt));
        Assert.Null(receipt); Assert.Equal(0, f.Saves); Assert.Equal(before, Snapshot(f.Live));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("learn", false)] [InlineData("learn", true)]
    [InlineData("train", false)] [InlineData("train", true)]
    [InlineData("exclude", false)] [InlineData("exclude", true)]
    public void KnownPreparationFailureIsNotAnUnknownSavedWrite(string operation, bool throws) {
        using var f = new Fixture(); var before = Snapshot(f.Live); var callbacks = 0;
        Assert.Equal(SpellbookMutationStatus.Refused, Run(f, operation, out var receipt, _ => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(0, f.Saves); Assert.Equal(before, Snapshot(f.Live));
            if (throws) throw new InvalidOperationException("authored prepare refusal"); return false;
        }, _ => callbacks++));
        Assert.Null(receipt); Assert.Equal(0, f.Saves); Assert.Equal(0, callbacks); Assert.Equal(before, Snapshot(f.Live));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NativePacketPreparationRefusalLeavesSavedAndLiveBooksUntouched(bool throws) {
        using var f = new Fixture(); var before = Snapshot(f.Live);
        f.Dependencies.Prepare = _ => { if (throws) throw new InvalidOperationException("authored native serializer refusal"); return false; };
        Assert.Equal(SpellbookMutationStatus.Refused, Run(f, "train", out var receipt));
        Assert.Null(receipt); Assert.Equal(0, f.Saves); Assert.Equal(before, Snapshot(f.Live));
        Assert.Equal(5, f.Saved.MagicSchoolBehavior.TrainingPoints); Assert.DoesNotContain(Learned, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public void PreparedLearnedReceiptCannotMutateTheStagedSavedWrite() {
        using var f = new Fixture();
        Assert.Equal(SpellbookMutationStatus.Committed, Run(f, "learn", out _, receipt => {
            var ids = Assert.IsAssignableFrom<IList<uint>>(receipt.LearnedSpells);
            Assert.Throws<NotSupportedException>(() => ids.Add(LiveOnly));
            Assert.Equal(new[] { Prior }, f.Working!.Wizard.SpellbookBehavior.LearnedSpellTemplateIds); return true;
        }));
        Assert.Equal(new[] { Prior, Learned }, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds);
    }

    [Theory]
    [InlineData("learn", "before")] [InlineData("learn", "lost")] [InlineData("learn", "publication")]
    [InlineData("train", "before")] [InlineData("train", "lost")] [InlineData("train", "publication")]
    [InlineData("exclude", "before")] [InlineData("exclude", "lost")] [InlineData("exclude", "publication")]
    public void UnknownWritesQuarantineBeforeLaneReleaseAndPermitOnlyAFreshReload(string operation, string failure) {
        using var f = new Fixture(); var live = f.Live; var before = Snapshot(live); var disposed = false;
        f.FailSave = failure != "publication"; f.Durable = failure == "lost";
        if (failure == "publication") f.Dependencies.BeforePublish = _ => {
            Assert.Equal(1, f.Saves); Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(before, Snapshot(live));
            throw new InvalidOperationException("authored publication failure");
        };
        f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); disposed = true; };
        Assert.Equal(SpellbookMutationStatus.Refused, Run(f, operation, out var receipt)); Assert.Null(receipt);
        Assert.True(disposed); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); Assert.Equal(before, Snapshot(live)); Assert.Equal(1, f.Saves);
        var durable = failure != "before";
        Assert.Equal(operation != "exclude" && durable, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds.Contains(Learned));
        Assert.Equal(operation == "exclude" && durable, f.Saved.SpellbookBehavior.ExcludedItemSpellIds[Item].Contains(Learned));
        Assert.Equal(operation == "train" && durable ? 4 : 5, f.Saved.MagicSchoolBehavior.TrainingPoints);
        foreach (var retry in new[] { "learn", "train", "exclude" }) Assert.Equal(SpellbookMutationStatus.Refused, Run(f, retry, out _));
        WizardCollection.UpdateCharacterItems(live); WizardCollection.UpdateCharacterSpellbookBehavior(live);
        Assert.Equal(1, f.Opened); Assert.Equal(1, f.Saves);
        f.FailSave = false; f.OnDispose = null; f.Dependencies.BeforePublish = null; f.Live = f.Reload();
        Assert.Equal(SpellbookMutationStatus.Committed, WizardSpellbookTransactions.TryLearn(f.Live, Learned + 1, out _));
        Assert.Equal(2, f.Saves); Assert.Contains(Learned + 1, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds);
    }

    [Fact]
    public void UncertaintyRaisedByTheFreshLoadRefusesBeforePreparationOrSave() {
        using var f = new Fixture(); var before = Snapshot(f.Live);
        f.OnLoad = () => WizardCollection.MarkInventorySnapshotUncertain(f.Live);
        Assert.Equal(SpellbookMutationStatus.Refused, Run(f, "learn", out _));
        Assert.Equal(1, f.Opened); Assert.Equal(0, f.Saves); Assert.Empty(f.Prepared); Assert.Equal(before, Snapshot(f.Live));
    }

    [Fact]
    public async Task CallbackFailureQuarantinesBeforeAQueuedStaleLearnCanEnter() {
        using var f = new Fixture(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var attempted = new ManualResetEventSlim(); using var inventoryAttempted = new ManualResetEventSlim(); var live = f.Live;
        var disposed = false;
        f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); disposed = true; };
        var transaction = Task.Run(() => Run(f, "learn", out _, publish: _ => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, f.Saves); Assert.Contains(Learned, live.SpellbookBehavior.LearnedSpellTemplateIds);
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); throw new InvalidOperationException("authored native callback failure");
        }));
        Task<SpellbookMutationStatus>? stale = null;
        Task? inventory = null;
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            stale = Task.Run(() => { attempted.Set(); return WizardSpellbookTransactions.TryLearn(live, Learned + 1, out _); });
            inventory = Task.Run(() => { inventoryAttempted.Set(); WizardCollection.UpdateCharacterItems(live); });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5))); Assert.False(stale.IsCompleted); Assert.Equal(1, f.Opened);
            Assert.True(inventoryAttempted.Wait(TimeSpan.FromSeconds(5))); Assert.False(inventory.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Equal(SpellbookMutationStatus.Refused, await transaction); Assert.Equal(SpellbookMutationStatus.Refused, await stale!);
        await inventory!; Assert.True(disposed);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); Assert.Equal(1, f.Opened); Assert.Equal(1, f.Saves);
        Assert.Contains(Learned, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds); Assert.DoesNotContain(Learned + 1, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds);
    }

    [Fact]
    public async Task ConcurrentTrainerRequestsChargeOnceAndKeepOneLearnedReference() {
        using var f = new Fixture(); var published = 0;
        var first = Task.Run(() => Run(f, "train", out _, publish: _ => published++));
        var second = Task.Run(() => Run(f, "train", out _, publish: _ => published++));
        var statuses = await Task.WhenAll(first, second);
        Assert.Equal(1, statuses.Count(status => status == SpellbookMutationStatus.Committed));
        Assert.Equal(1, statuses.Count(status => status == SpellbookMutationStatus.Unchanged));
        Assert.Equal(1, f.Saves); Assert.Equal(1, published); Assert.Equal(4, f.Saved.MagicSchoolBehavior.TrainingPoints);
        Assert.Equal(1, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds.Count(id => id == Learned)); Assert.Equal(3, f.Prepared.Count);
    }

    [Fact]
    public async Task ConcurrentLearnAndExclusionRetainBothSelectedDurableChanges() {
        using var f = new Fixture();
        var learn = Task.Run(() => Run(f, "learn", out _)); var exclusion = Task.Run(() => Run(f, "exclude", out _));
        Assert.All(await Task.WhenAll(learn, exclusion), status => Assert.Equal(SpellbookMutationStatus.Committed, status));
        Assert.Equal(2, f.Saves); Assert.Equal(new[] { Prior, Learned }, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds);
        Assert.Contains(Learned, f.Saved.SpellbookBehavior.ExcludedItemSpellIds[Item]);
        Assert.Equal(new[] { Prior, Learned }, f.Live.SpellbookBehavior.LearnedSpellTemplateIds);
        Assert.Contains(Learned, f.Live.SpellbookBehavior.ExcludedItemSpellIds[Item]);
        Assert.False(f.Saved.SpellbookBehavior.LearnedSpellTemplateIds.Contains(LiveOnly));
    }

    private static SpellbookMutationStatus Run(Fixture f, string operation, out SpellbookReceipt receipt,
        Func<SpellbookReceipt, bool>? prepare = null, Action<SpellbookReceipt>? publish = null, ulong owner = Owner)
        => operation switch {
            "train" => WizardSpellbookTransactions.TryTrain(f.Live, new NPCSpellEntry { TemplateID = Learned, Level = 5 }, 7,
                out receipt, prepare, publish, owner),
            "exclude" => WizardSpellbookTransactions.TrySetItemSpellExclusion(f.Live, Item, Learned, true, out receipt, prepare, publish, owner),
            _ => WizardSpellbookTransactions.TryLearn(f.Live, Learned, out receipt, prepare, publish, owner),
        };

    internal static string Snapshot(Wizard wizard) => string.Join("|", wizard.CharId, wizard.AccountId,
        wizard.GameObject.m_characterId.Full, wizard.GameObject.m_globalID.Full, wizard.GameObject.m_permID.Full,
        string.Join(",", wizard.SpellbookBehavior.LearnedSpellTemplateIds), wizard.MagicSchoolBehavior.TrainingPoints,
        string.Join(";", wizard.SpellbookBehavior.ExcludedItemSpellIds.OrderBy(pair => pair.Key).Select(pair => pair.Key + ":" + string.Join(",", pair.Value.OrderBy(id => id)))),
        string.Join(";", wizard.GameObject.m_inactiveBehaviors.OfType<ClientSpellbookBehavior>().SelectMany(book => book.m_spellIDList)
            .Select(tracker => $"{tracker.m_spellID},{tracker.m_isRetired},{tracker.m_tieredSpellGroupIndex}")));

    internal sealed class Fixture : IDisposable {
        private readonly WizardCollection.TestStore? _store = WizardCollection.TestStoreScope.Value;
        private readonly SpellbookMutationDependencies? _dependencies = WizardSpellbookTransactions.TestScope.Value;
        private readonly Func<IDocumentSession, List<WizClientObjectItem>>? _rows = WizardInventoryTransactions.TestRowsScope.Value;
        internal Wizard Saved, Live;
        internal readonly SpellbookMutationDependencies Dependencies;
        internal readonly List<IMessage> Prepared = [];
        internal List<WizClientObjectItem> Items = [new() { m_globalID = Item, m_characterId = Character, m_templateID = 9922,
            m_debugName = "original item", m_inactiveBehaviors = [new DeckBehavior { m_spellList = [new SpellData { m_templateID = Prior, m_quantity = 3 }] }] },
            new() { m_globalID = Item + 99, m_characterId = Character + 7, m_templateID = 9923, m_debugName = "unrelated original", m_inactiveBehaviors = [] }];
        internal int Opened, Saves;
        internal bool Missing, DuplicateCharacter, FailSave, Durable;
        internal Action? OnLoad, OnSave, OnDispose;
        internal SpellbookSession? Working;
        internal ClientSpellbookBehavior Native => Live.GameObject.m_inactiveBehaviors.OfType<ClientSpellbookBehavior>().Single();
        internal ClientMagicSchoolBehavior NativeSchool => Live.GameObject.m_inactiveBehaviors.OfType<ClientMagicSchoolBehavior>().Single();

        internal Fixture() {
            Saved = NewWizard(); Live = Reload(); Live.SpellbookBehavior.LearnedSpellTemplateIds.Add(LiveOnly);
            Live.SpellbookBehavior.ExcludedItemSpellIds[Item].Add(OtherExcluded); Live.SpellbookBehavior.ExcludedItemSpellIds[Item + 1] = [LiveOnly];
            Live.MagicSchoolBehavior.TrainingPoints = 77; NativeSchool.m_trainingPoints = 77;
            Dependencies = new() {
                Spell = id => new Spell { m_templateID = id, m_spellID = Hash },
                Template = _ => new SpellTemplate { m_sMagicSchoolName = "Ice" },
                Prepare = message => { Prepared.Add(message); return true; },
            };
            WizardCollection.TestStoreScope.Value = new(Open, Load); WizardSpellbookTransactions.TestScope.Value = Dependencies;
            WizardInventoryTransactions.TestRowsScope.Value = session => ((SpellbookSession)(object)session).Items;
        }
        internal Wizard Reload() {
            var wizard = CloneWizard(Saved); var native = wizard.SpellbookBehavior.GetClientBehaviorInstance();
            foreach (var tracker in native.m_spellIDList) { tracker.m_isRetired = true; tracker.m_tieredSpellGroupIndex = 17; }
            wizard.GameObject = new WizClientObject { m_inactiveBehaviors = [native, wizard.MagicSchoolBehavior.GetClientBehaviorInstance()] };
            wizard.Account = new Account(); typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(wizard.Account, wizard.AccountId);
            wizard.Account.CharacterIds.Add(wizard.CharId);
            var deck = CloneItem(Items[0]); wizard.EquipmentBehavior.EquippedItems = [deck];
            wizard.SpellbookBehavior.SpellList = deck.m_inactiveBehaviors.OfType<DeckBehavior>().Single().m_spellList;
            return wizard;
        }
        private IDocumentSession Open() {
            Opened++; var session = DispatchProxy.Create<IDocumentSession, SpellbookSession>(); var proxy = (SpellbookSession)(object)session;
            Working = proxy; proxy.Wizard = CloneWizard(Saved); proxy.Items = Items.Select(CloneItem).ToList();
            proxy.Characters = [proxy.Wizard]; if (DuplicateCharacter) proxy.Characters.Add(CloneWizard(proxy.Wizard));
            proxy.DisposeSession = () => OnDispose?.Invoke();
            proxy.Save = () => {
                Assert.True(WizardCollection.HoldsWriteLane); Assert.True(proxy.OptimisticWrites); Saves++; OnSave?.Invoke();
                if (FailSave && !Durable) throw new InvalidOperationException("authored spellbook write failure");
                Saved = CloneWizard(proxy.Wizard);
                Items = proxy.Items.Select(row => CloneItem(proxy.Ignored.Contains(row) ? Items.First(old => old.m_globalID.Full == row.m_globalID.Full) : row)).ToList();
                if (FailSave) throw new InvalidOperationException("authored durable lost spellbook ACK");
            };
            return session;
        }
        private Wizard Load(IDocumentSession session, ulong character) {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(Character, character); OnLoad?.Invoke();
            var proxy = (SpellbookSession)(object)session;
            WizardInventoryTransactions.CaptureReadRows(session, proxy.Items);
            foreach (var row in proxy.Items) row.m_debugName = "normalized read";
            return Missing ? null! : proxy.Wizard;
        }
        public void Dispose() {
            WizardCollection.TestStoreScope.Value = _store; WizardSpellbookTransactions.TestScope.Value = _dependencies;
            WizardInventoryTransactions.TestRowsScope.Value = _rows;
        }
    }

    private static Wizard NewWizard() => new() { CharId = Character, AccountId = Owner,
        GameStats = new(MagicSchool.Fire, 10) { m_currentGold = 100, m_currentMana = 32 },
        MagicSchoolBehavior = new() { MagicSchool = MagicSchool.Fire, Level = 10, TrainingPoints = 5 },
        SpellbookBehavior = new() { LearnedSpellTemplateIds = [Prior], TreasureCardTemplateIds = [OtherExcluded],
            TemporarySpells = [new Spell { m_templateID = OtherExcluded }], ExcludedItemSpellIds = new() { [Item] = [Prior] },
            DeckTreasureCards = new() { [Item] = new() { [OtherExcluded] = 2 } }, DeckTreasureLedgerVersion = 1 },
        InventoryBehavior = new() { InventoryItemIds = [], Items = [] },
        EquipmentBehavior = new() { EquippedItemIds = [Item], EquippedItems = [], SlotList = [new EquipmentSlot { ItemId = Item, SlotType = EquipmentSlotType.Deck }] },
        StorageBehavior = new() { BankItemIds = [], Items = [] },
    };

    private static Wizard CloneWizard(Wizard source) {
        var clone = NewWizard(); clone.CharId = source.CharId; clone.AccountId = source.AccountId;
        clone.GameObject.m_characterId = source.GameObject.m_characterId; clone.GameObject.m_globalID = source.GameObject.m_globalID;
        clone.GameObject.m_permID = source.GameObject.m_permID;
        clone.GameStats = source.GameStats.CloneSnapshotWithGold(source.GameStats.m_currentGold);
        clone.MagicSchoolBehavior = new() { MagicSchool = source.MagicSchoolBehavior.MagicSchool, Level = source.MagicSchoolBehavior.Level,
            TrainingPoints = source.MagicSchoolBehavior.TrainingPoints, ExperiencePoints = source.MagicSchoolBehavior.ExperiencePoints };
        clone.SpellbookBehavior = source.SpellbookBehavior is null ? null! : new() {
            LearnedSpellTemplateIds = source.SpellbookBehavior.LearnedSpellTemplateIds?.ToList()!,
            TreasureCardTemplateIds = source.SpellbookBehavior.TreasureCardTemplateIds.ToList(),
            TemporarySpells = source.SpellbookBehavior.TemporarySpells.ToList(),
            ExcludedItemSpellIds = source.SpellbookBehavior.ExcludedItemSpellIds.ToDictionary(pair => pair.Key, pair => new HashSet<uint>(pair.Value)),
            DeckTreasureCards = ServerWizSpellbookBehavior.CopyLedger(source.SpellbookBehavior.DeckTreasureCards),
            DeckTreasureLedgerVersion = source.SpellbookBehavior.DeckTreasureLedgerVersion,
        };
        clone.InventoryBehavior.InventoryItemIds = source.InventoryBehavior.InventoryItemIds.ToList();
        clone.EquipmentBehavior.EquippedItemIds = source.EquipmentBehavior.EquippedItemIds.ToList();
        clone.EquipmentBehavior.SlotList = source.EquipmentBehavior.SlotList.ToList(); clone.StorageBehavior.BankItemIds = source.StorageBehavior.BankItemIds.ToList();
        return clone;
    }

    private static WizClientObjectItem CloneItem(WizClientObjectItem source) => new() { m_globalID = source.m_globalID,
        m_characterId = source.m_characterId, m_templateID = source.m_templateID, m_debugName = source.m_debugName,
        m_inactiveBehaviors = source.m_inactiveBehaviors.ToList() };

    public class SpellbookSession : DispatchProxy {
        internal Wizard Wizard = null!;
        internal List<Wizard> Characters = [];
        internal List<WizClientObjectItem> Items = [];
        internal readonly HashSet<object> Ignored = new(ReferenceEqualityComparer.Instance);
        internal bool OptimisticWrites;
        internal Action Save = null!, DisposeSession = null!;
        private IAdvancedSessionOperations? _advanced;
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            switch (method!.Name) {
                case "get_Advanced":
                    if (_advanced is null) { _advanced = DispatchProxy.Create<IAdvancedSessionOperations, SpellbookAdvanced>(); ((SpellbookAdvanced)(object)_advanced).Owner = this; }
                    return _advanced;
                case "Query": return typeof(SpellbookSession).GetMethod(nameof(Query), BindingFlags.NonPublic | BindingFlags.Instance)!
                    .MakeGenericMethod(method.GetGenericArguments()[0]).Invoke(this, [args]);
                case "SaveChanges": Save(); return null;
                case "Dispose": DisposeSession(); return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
        private IRavenQueryable<T> Query<T>(object?[] args) {
            Assert.Equal(typeof(Wizard), typeof(T)); Assert.Equal(WizardCollection.CollectionName, args.OfType<string>().Single());
            var query = DispatchProxy.Create<IRavenQueryable<T>, SpellbookQuery<T>>(); var proxy = (SpellbookQuery<T>)(object)query;
            proxy.Rows = Characters.Cast<T>().AsQueryable(); proxy.Self = query; return query;
        }
    }
    public class SpellbookQuery<T> : DispatchProxy {
        internal IQueryable<T> Rows = null!;
        internal IRavenQueryable<T> Self = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "Customize" => Self, "get_Provider" => Rows.Provider, "get_Expression" => Rows.Expression,
            "get_ElementType" => typeof(T), "GetEnumerator" => Rows.GetEnumerator(), _ => throw new NotSupportedException(method.Name),
        };
    }
    public class SpellbookAdvanced : DispatchProxy {
        internal SpellbookSession Owner = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            if (method!.Name == "set_OptimisticConcurrencyMode") { Owner.OptimisticWrites = true; return null; }
            if (method.Name == "IgnoreChangesFor") { Assert.True(Owner.Ignored.Add(args![0]!)); return null; }
            throw new NotSupportedException(method.Name);
        }
    }
}
