// CLASSIC: selected HP/mana ACKs retain fresh saved authority and existing attached runtime offsets.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Results.Handlers;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(BadgeRulesTests))]
public sealed class ResourceRewardAcknowledgementTests {
    [Theory] [InlineData(false)] [InlineData(true)]
    public void SelectedFreshWritePublishesAfterAckAndPreservesUnrelatedStatsAndLiveAliases(bool mana) {
        using var scope = new Scope(); var f = scope.F; var live = f.Live; var stats = live.GameStats;
        Assert.True(WizardProgressionTransactions.RuntimeContextMatches(live, f.Saved));
        Assert.True(WizardResourceTransactions.PrepareNative(new ResourceReceipt(mana ? ResourceKind.Mana : ResourceKind.Health,
            live.GameObjectID, Maximum(live, mana), Maximum(live, mana), mana ? 50 : 140).Message));
        var obj = live.GameObject; var native = obj.m_inactiveBehaviors; var before = Unselected(f.Saved, mana);
        var old = Current(live, mana); var published = 0;
        f.OnSave = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(old, Current(live, mana)); Assert.Equal(0, published); };
        Assert.Equal(ResourceMutationStatus.Committed, Run(f, mana, out var receipt, value => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, f.Saves); Assert.Equal(value.Value, Current(live, mana)); published++;
        }));
        Assert.Equal(1, f.Saves); Assert.Equal(1, published); Assert.Equal(Maximum(live, mana), Current(f.Saved, mana));
        Assert.Equal(before, Unselected(f.Saved, mana)); Assert.Same(stats, live.GameStats); Assert.Same(obj, live.GameObject); Assert.Same(native, obj.m_inactiveBehaviors);
        Assert.Equal(901, live.GameStats.m_currentGold); Assert.Equal(mana ? 23 : 17, mana ? live.GameStats.m_currentHitpoints : live.GameStats.m_currentMana);
        Assert.Equal(Maximum(live, mana), receipt.Value); Assert.Equal(mana ? 50 : 140, receipt.ClientMax);
        var decoded = Assert.Single(MessageEncoder.Decode(MessageEncoder.Encode(receipt.Message))!);
        if (mana) { var message = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEMANA>(decoded); Assert.Equal(receipt.Value, message.Mana); Assert.Equal(50, message.MaxMana); }
        else { var message = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH>(decoded); Assert.Equal(live.GameObjectID, message.CharacterID); Assert.Equal(receipt.Value, message.NewHealth); Assert.Equal(140, message.NewHealthMax); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void HealthySavedNoOpRehydratesSelectedValueAndPublishesInLaneWithoutSaving(bool mana) {
        using var scope = new Scope(); var f = scope.F; SetCurrent(f.Saved, mana, Maximum(f.Live, mana)); var calls = 0;
        f.OnSave = () => Assert.Fail("fresh no-op must not save");
        Assert.Equal(ResourceMutationStatus.Unchanged, Run(f, mana, out var receipt, _ => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(0, f.Saves); Assert.Equal(Maximum(f.Live, mana), Current(f.Live, mana)); calls++;
        }));
        Assert.Equal(0, f.Saves); Assert.Equal(1, f.Opened); Assert.Equal(1, calls); Assert.NotNull(receipt.Message);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData(false, "before")] [InlineData(true, "before")] [InlineData(false, "lost")] [InlineData(true, "lost")]
    [InlineData(false, "publication")] [InlineData(true, "publication")] [InlineData(false, "callback")] [InlineData(true, "callback")]
    public void UnknownOutcomeQuarantinesBeforeLaneReleaseAndCannotOpenAQueuedRetry(bool mana, string fault) {
        using var scope = new Scope(); var f = scope.F; var old = Current(f.Live, mana); var notices = 0;
        f.FailSave = fault is "before" or "lost"; f.Durable = fault == "lost";
        if (fault == "publication") scope.Dependencies.BeforePublish = _ => throw new InvalidOperationException("authored publication failure");
        f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); };
        Assert.Equal(ResourceMutationStatus.Refused, Run(f, mana, out var receipt, _ => {
            if (fault == "callback") throw new InvalidOperationException("authored send failure"); notices++;
        }));
        Assert.Null(receipt); Assert.Equal(0, notices); Assert.Equal(1, f.Saves);
        Assert.Equal(fault == "before" ? old : Maximum(f.Live, mana), Current(f.Saved, mana));
        Assert.Equal(fault == "callback" ? Maximum(f.Live, mana) : old, Current(f.Live, mana));
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        Assert.Equal(ResourceMutationStatus.Refused, Run(f, mana, out _));
        Assert.Equal(ResourceMutationStatus.Refused, Run(f, !mana, out _)); Assert.Equal(1, f.Opened); Assert.Equal(1, f.Saves);
    }

    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void SavedNoOpPublicationFailureIsQuarantinedInsideItsNoSaveLane(bool mana, bool callback) {
        using var scope = new Scope(); var f = scope.F; SetCurrent(f.Saved, mana, Maximum(f.Live, mana));
        if (!callback) scope.Dependencies.BeforePublish = _ => throw new InvalidOperationException("authored unchanged publication");
        f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); };
        Assert.Equal(ResourceMutationStatus.Refused, Run(f, mana, out var receipt, _ => { if (callback) throw new InvalidOperationException("authored unchanged send"); }));
        Assert.Null(receipt); Assert.Equal(0, f.Saves); Assert.Equal(1, f.Opened);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); Assert.Equal(ResourceMutationStatus.Refused, Run(f, mana, out _)); Assert.Equal(1, f.Opened);
    }

    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void FailedNativePreparationOrCallerPreparationRefusesWithoutSaveOrQuarantine(bool mana, bool native) {
        using var scope = new Scope(); var f = scope.F; var old = Current(f.Live, mana);
        if (native) scope.Dependencies.Prepare = _ => false;
        var status = mana ? WizardResourceTransactions.TryRefillMana(f.Live, out _, preparePublication: _ => native)
            : WizardResourceTransactions.TryRefillHealth(f.Live, out _, preparePublication: _ => native);
        Assert.Equal(ResourceMutationStatus.Refused, status); Assert.Equal(0, f.Saves); Assert.Equal(old, Current(f.Live, mana));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ReceiptReconstructsNativeMessageFromImmutableValuesEvenIfPreparationMutatesItsTemporaryMessage(bool mana) {
        using var scope = new Scope(); var f = scope.F;
        var status = mana ? WizardResourceTransactions.TryRefillMana(f.Live, out var receipt, value => {
            ((WIZARD_12_PROTOCOL.MSG_UPDATEMANA)value.Message).Mana = 1; return true;
        }) : WizardResourceTransactions.TryRefillHealth(f.Live, out receipt, value => {
            ((WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH)value.Message).NewHealth = 1; return true;
        });
        Assert.Equal(ResourceMutationStatus.Committed, status); Assert.Equal(Maximum(f.Live, mana), receipt.Value);
        Assert.Equal(Maximum(f.Live, mana), Current(f.Saved, mana));
    }

    [Theory]
    [InlineData(true, -3, 0f, 0)] [InlineData(true, 21, 0f, 21)] [InlineData(true, 999, 0f, 67)]
    [InlineData(false, 999, 0f, 67)] [InlineData(false, 999, -2f, 67)] [InlineData(false, 999, .5f, 33)]
    [InlineData(false, 999, .333f, 22)] [InlineData(false, 999, 1.5f, 67)]
    public void ManaUsesExistingAbsoluteFlatOrTruncatedPercentTargetAndIgnoresOverfill(bool flat, int amount, float percent, int target) {
        using var scope = new Scope(); var f = scope.F;
        Assert.Equal(ResourceMutationStatus.Committed, WizardResourceTransactions.TryApplyMana(f.Live,
            new ResAddMana { m_useFlat = flat, m_manaFlat = amount, m_manaPercent = percent, m_overfill = 999 }, out var receipt));
        Assert.Equal(target, Current(f.Saved, true)); Assert.Equal(target, Current(f.Live, true)); Assert.Equal(67, receipt.RuntimeMax); Assert.Equal(50, receipt.ClientMax);
    }

    [Theory] [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(float.NegativeInfinity)] [InlineData(float.MaxValue)]
    public void MalformedUsedPercentageRefusesBeforeSaveWhileUnusedPercentageDoesNotChangeFlatMode(float percent) {
        using var scope = new Scope(); var f = scope.F;
        Assert.Equal(ResourceMutationStatus.Refused, WizardResourceTransactions.TryApplyMana(f.Live, new ResAddMana { m_manaPercent = percent }, out _));
        Assert.Equal(0, f.Saves); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        Assert.Equal(ResourceMutationStatus.Committed, WizardResourceTransactions.TryApplyMana(f.Live,
            new ResAddMana { m_useFlat = true, m_manaFlat = 11, m_manaPercent = percent }, out _)); Assert.Equal(11, f.Saved.GameStats.m_currentMana);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ReadOnlyCapClampEquivalencePreservesRawSavedLevelXpAndAllUnselectedFields(bool mana) {
        using var scope = new Scope(); var f = scope.F;
        f.Saved.MagicSchoolBehavior.Level = 8; f.Saved.MagicSchoolBehavior.ExperiencePoints = 888;
        f.Live.MagicSchoolBehavior.Level = 4; f.Live.GameStats.Level = 4;
        var before = Unselected(f.Saved, mana);
        Assert.Equal(ResourceMutationStatus.Committed, Run(f, mana, out var receipt));
        Assert.Equal(8, f.Saved.MagicSchoolBehavior.Level); Assert.Equal(888, f.Saved.MagicSchoolBehavior.ExperiencePoints);
        Assert.Equal(mana ? 70 : 180, receipt.ClientMax); Assert.Equal(before, Unselected(f.Saved, mana));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ReadOnlyExpiredRentalEquivalencePreservesSavedEquipmentAndNativeOriginals(bool mana) {
        using var scope = new Scope(); var f = scope.F; scope.Rental(expired: true);
        var before = Unselected(f.Saved, mana);
        f.OnSave = () => { Assert.All(f.Working!.Items, row => Assert.Contains(row, f.Working.Ignored)); Assert.Single(f.Working.Wizard.EquipmentBehavior.EquippedItemIds); };
        Assert.Equal(ResourceMutationStatus.Committed, Run(f, mana, out _));
        Assert.Equal(before, Unselected(f.Saved, mana)); Assert.Single(f.Saved.EquipmentBehavior.EquippedItemIds);
        Assert.Single(f.Items); Assert.Equal(77ul, f.Items[0].m_globalID.Full); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("native-character")] [InlineData("native-global")] [InlineData("native-permanent")]
    [InlineData("saved-account")] [InlineData("duplicate-character")] [InlineData("missing-runtime")]
    [InlineData("level")] [InlineData("school")] [InlineData("equipment")] [InlineData("negative-max")]
    [InlineData("active-rental")] [InlineData("preparation-max")] [InlineData("preparation-alias")]
    public void InvalidIdentityContextOrPreparationChangeCannotSaveOrPublish(string fault) {
        using var scope = new Scope(); var f = scope.F;
        if (fault == "native-character") f.Live.GameObject.m_characterId = f.Live.CharId + 1;
        if (fault == "native-global") f.Live.GameObject.m_globalID = f.Live.GameObjectID + (1UL << 40);
        if (fault == "native-permanent") f.Live.GameObject.m_permID = 0;
        if (fault == "saved-account") f.Saved.AccountId++;
        if (fault == "duplicate-character") f.OnLoad = () => f.Working!.Characters = [f.Working.Wizard, TerminalClaimFixture.CloneWizard(f.Working.Wizard)];
        if (fault == "missing-runtime") f.Live.HasInitializedRuntimeStats = false;
        if (fault == "level") f.Live.GameStats.Level++;
        if (fault == "school") f.Live.GameStats.MagicSchool = MagicSchool.Ice;
        if (fault == "equipment") f.Live.EquipmentBehavior.EquippedItemIds = [778];
        if (fault == "negative-max") f.Live.GameStats.m_baseMana = -1;
        if (fault == "active-rental") scope.Rental(expired: false);
        if (fault == "preparation-max") scope.Dependencies.Prepare = _ => { f.Live.GameStats.m_baseMana++; return true; };
        if (fault == "preparation-alias") scope.Dependencies.Prepare = _ => { f.Live.GameStats = f.Live.GameStats.CloneSnapshotWithGold(100); return true; };
        var before = Unselected(f.Saved, true); var publications = 0;
        Assert.Equal(ResourceMutationStatus.Refused, Run(f, true, out var receipt, _ => publications++));
        Assert.Null(receipt); Assert.Equal(0, f.Saves); Assert.Equal(0, publications); Assert.Equal(before, Unselected(f.Saved, true));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void FullNativeCharacterIdentityAndExpectedAccountArePreserved(bool mana) {
        using var scope = new Scope(); var f = scope.F;
        f.Saved.CharId = f.Live.CharId = (1UL << 40) + 782000;
        f.Saved.AccountId = f.Live.AccountId = (1UL << 44) + 81;
        Assert.Equal(ResourceMutationStatus.Refused, mana ? WizardResourceTransactions.TryRefillMana(f.Live, out _, expectedAccountId: f.Live.AccountId + 1)
            : WizardResourceTransactions.TryRefillHealth(f.Live, out _, expectedAccountId: f.Live.AccountId + 1)); Assert.Equal(0, f.Opened);
        var status = mana ? WizardResourceTransactions.TryRefillMana(f.Live, out var receipt, expectedAccountId: f.Live.AccountId)
            : WizardResourceTransactions.TryRefillHealth(f.Live, out receipt, expectedAccountId: f.Live.AccountId);
        Assert.Equal(ResourceMutationStatus.Committed, status); Assert.Equal(f.Live.GameObjectID, receipt.GameObjectId); Assert.Equal(f.Live.AccountId, f.Saved.AccountId);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AQueuedResourceWriteAfterLostAckIsRefusedBeforeOpeningItsOwnSession(bool mana) {
        using var scope = new Scope(); var f = scope.F; f.FailSave = f.Durable = true;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.OnSave = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var first = Task.Run(() => Run(f, mana, out _)); Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var second = Task.Run(() => Run(f, !mana, out _)); release.Set();
        Assert.Equal(ResourceMutationStatus.Refused, await first); Assert.Equal(ResourceMutationStatus.Refused, await second);
        Assert.Equal(1, f.Opened); Assert.Equal(1, f.Saves); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    private static ResourceMutationStatus Run(TerminalClaimFixture f, bool mana, out ResourceReceipt receipt, System.Action<ResourceReceipt>? publish = null)
        => mana ? WizardResourceTransactions.TryRefillMana(f.Live, out receipt, afterCommit: publish)
            : WizardResourceTransactions.TryRefillHealth(f.Live, out receipt, afterCommit: publish);
    private static int Current(Wizard wizard, bool mana) => mana ? wizard.GameStats.m_currentMana : wizard.GameStats.m_currentHitpoints;
    private static int Maximum(Wizard wizard, bool mana) => mana ? wizard.GameStats.m_baseMana : wizard.GameStats.m_baseHitpoints;
    private static void SetCurrent(Wizard wizard, bool mana, int value) { if (mana) wizard.GameStats.m_currentMana = value; else wizard.GameStats.m_currentHitpoints = value; }
    private static string Unselected(Wizard wizard, bool mana) {
        var copy = TerminalClaimFixture.CloneWizard(wizard); SetCurrent(copy, mana, 0); return JsonSerializer.Serialize(copy);
    }
    private sealed class Scope : IDisposable {
        internal readonly TerminalClaimFixture F = new();
        internal readonly ResourceMutationDependencies Dependencies = new();
        private readonly ResourceMutationDependencies? _old = WizardResourceTransactions.TestScope.Value;
        internal Scope() { WizardResourceTransactions.TestScope.Value = Dependencies; F.Live.GameStats.m_baseHitpoints += 35; F.Live.GameStats.m_baseMana += 17; }
        internal void Rental(bool expired) {
            var now = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            F.Saved.EquipmentBehavior.EquippedItemIds = [77];
            F.Saved.EquipmentBehavior.SlotList = [new EquipmentSlot { ItemId = 77, SlotType = EquipmentSlotType.Robe }];
            F.Live.EquipmentBehavior.SlotList = [new EquipmentSlot { ItemId = 77, SlotType = EquipmentSlotType.Robe }];
            F.Items.Add(new WizClientObjectItem { m_globalID = 77, m_templateID = 9342, m_characterId = F.Live.CharId,
                m_inactiveBehaviors = [new ClientTimedItemBehavior { m_expireTime = (uint)now.ToUnixTimeSeconds() + (expired ? 0u : 100u) }] });
            F.Progression.RentalNow = () => now.AddSeconds(1);
            F.Progression.RentalTemplates = _ => new WizItemTemplate { m_templateID = 9342,
                m_behaviors = [new TimedItemBehaviorTemplate { m_timerType = TimerType.TimerType_Calendar }] };
        }
        public void Dispose() { WizardResourceTransactions.TestScope.Value = _old; F.Dispose(); }
    }
}
