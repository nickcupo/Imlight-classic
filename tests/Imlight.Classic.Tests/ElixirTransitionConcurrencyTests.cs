// CLASSIC: acknowledged elixir references and canonical effects share the real character write lane.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic.Elixirs;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ElixirTransitionConcurrencyTests {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("purchase", "gain")] [InlineData("purchase", "drink")]
    [InlineData("activate", "gain")] [InlineData("activate", "drink")]
    [InlineData("cancel", "gain")] [InlineData("cancel", "drink")]
    [InlineData("expiry", "gain")] [InlineData("expiry", "drink")]
    public async Task OtherCharacterWritesCannotEnterBetweenAcknowledgedReferencesAndCompletedEffects(
        string transition, string competingOperation) {
        using var f = new Fixture(); f.Arrange(transition);
        using var reached = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var attempted = new ManualResetEventSlim(); using var completed = new ManualResetEventSlim();
        var saves = f.Saves; var oldIds = f.Live.EquipmentBehavior.EquippedItemIds.ToArray();
        var oldEffects = f.Live.GameEffects.Snapshot(); var oldCrowns = f.Live.Account.Crowns;
        f.BeforeSave = () => {
            if (f.Saves != saves) return;
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(oldIds, f.Live.EquipmentBehavior.EquippedItemIds);
            Assert.Equal(oldEffects, f.Live.GameEffects.Snapshot()); Assert.Equal(oldCrowns, f.Live.Account.Crowns);
        };
        f.BeforeEffects = wizard => {
            Assert.Same(f.Live, wizard); Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(saves + 1, f.Saves);
            Assert.Equal(oldEffects, wizard.GameEffects.Snapshot());
            Assert.Equal(transition is "purchase" or "activate", wizard.EquipmentBehavior.EquippedItemIds.Contains(Fixture.ItemId));
            reached.Set(); Assert.True(release.Wait(Timeout), "release the acknowledged runtime publication");
        };
        var mutation = Task.Factory.StartNew(() => f.Change(transition), TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<bool>? competitor = null;
        try {
            Assert.True(reached.Wait(Timeout), "reach the acknowledged reference/effect boundary");
            competitor = Task.Factory.StartNew(() => {
                attempted.Set();
                var accepted = competingOperation == "gain"
                    ? WizardProgressionTransactions.TryGainExperience(f.Live, 20, out _)
                    : WizardPotionTransactions.TryDrink(f.Live, Fixture.Now, out _);
                completed.Set(); return accepted;
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(attempted.Wait(Timeout)); Assert.False(completed.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.Equal(saves + 1, f.Saves); Assert.Equal(150, f.Live.MagicSchoolBehavior.ExperiencePoints);
            Assert.Equal(31, f.Live.GameStats.m_currentHitpoints); Assert.Equal(1.25f, f.Live.GameStats.m_potionCharge);
        }
        finally {
            release.Set();
            await mutation.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            if (competitor is not null) await competitor.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        var result = await mutation.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.True(result.Saved); Assert.NotNull(result.RuntimeMessages);
        Assert.True(await competitor!.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(saves + 2, f.Saves);
        Assert.True(WizardProgressionTransactions.RuntimeContextMatches(f.Live, f.SavedWizard));
        Assert.Equal(transition is "purchase" or "activate" ? 1 : 0, f.Live.GameEffects.Count);
        if (competingOperation == "gain") {
            Assert.Equal(170, f.Live.MagicSchoolBehavior.ExperiencePoints); Assert.Equal(170, f.SavedWizard.MagicSchoolBehavior.ExperiencePoints);
        } else {
            Assert.Equal(151, f.Live.GameStats.m_currentHitpoints); Assert.Equal(57, f.Live.GameStats.m_currentMana);
            Assert.Equal(.25f, f.Live.GameStats.m_potionCharge); Assert.Equal(.25f, f.SavedWizard.GameStats.m_potionCharge);
        }
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public void PurchaseHoldsAccountBeforeCharacterThroughItsAcknowledgedRuntimePublication() {
        using var f = new Fixture(); var called = false;
        var accountLane = typeof(AccountCollection).GetField("s_heldWriteLane", BindingFlags.Static | BindingFlags.NonPublic)!;
        f.BeforeSave = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.NotNull(accountLane.GetValue(null)); };
        f.BeforeEffects = _ => {
            called = true; Assert.True(WizardCollection.HoldsWriteLane); Assert.NotNull(accountLane.GetValue(null));
            Assert.Throws<InvalidOperationException>(() => AccountCollection.WithAccountWriteLane(Fixture.AccountId, () => true));
        };
        Assert.True(f.Change("purchase").Saved); Assert.True(called); Assert.Null(accountLane.GetValue(null));
        Assert.False(WizardCollection.HoldsWriteLane);
        Assert.Throws<InvalidOperationException>(() => WizardCollection.WithCharacterLock(Fixture.Character,
            () => AccountCollection.WithAccountWriteLane(Fixture.AccountId, () => true)));
    }

    [Theory]
    [InlineData("purchase")] [InlineData("activate")] [InlineData("cancel")] [InlineData("expiry")]
    public void PublicationFailureQuarantinesBeforeSessionDisposalAndPreventsLaterWrites(string transition) {
        using var f = new Fixture(); f.Arrange(transition); var saves = f.Saves; var disposed = false;
        f.BeforeEffects = _ => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(saves + 1, f.Saves);
            throw new InvalidOperationException("authored acknowledged runtime publication failure");
        };
        f.OnDispose = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); disposed = true;
        };
        var result = f.Change(transition); Assert.False(result.Saved); Assert.Null(result.RuntimeMessages);
        Assert.True(disposed); Assert.False(WizardCollection.HoldsWriteLane); Assert.Equal(saves + 1, f.Saves);
        Assert.Equal(transition is "purchase" or "activate", f.SavedWizard.EquipmentBehavior.EquippedItemIds.Contains(Fixture.ItemId));
        f.OnDispose = null; var opens = f.Opened;
        Assert.False(WizardProgressionTransactions.TryGainExperience(f.Live, 20, out _));
        Assert.False(WizardPotionTransactions.TryDrink(f.Live, Fixture.Now, out _));
        Assert.False(WizardCollection.UpdateCharacterGameStats(f.Live, null!, null!)); Assert.False(f.Change(transition).Saved);
        Assert.Equal(opens, f.Opened); Assert.Equal(saves + 1, f.Saves);
    }

    [Theory]
    [InlineData("purchase", false)] [InlineData("purchase", true)]
    [InlineData("activate", false)] [InlineData("activate", true)]
    [InlineData("cancel", false)] [InlineData("cancel", true)]
    [InlineData("expiry", false)] [InlineData("expiry", true)]
    public void FailedOrDurableLostSaveAcknowledgementCannotPublishReferencesEffectsOrNativeReceipts(
        string transition, bool durable) {
        using var f = new Fixture(); f.Arrange(transition); var saves = f.Saves;
        var ids = f.Live.EquipmentBehavior.EquippedItemIds.ToArray(); var effects = f.Live.GameEffects.Snapshot();
        var crowns = f.Live.Account.Crowns; var calls = 0;
        f.FailSave = true; f.Durable = durable; f.BeforeEffects = _ => calls++;
        f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); };
        var result = f.Change(transition);
        Assert.False(result.Saved); Assert.Null(result.RuntimeMessages); Assert.Equal(0, calls);
        Assert.Equal(ids, f.Live.EquipmentBehavior.EquippedItemIds); Assert.Equal(effects, f.Live.GameEffects.Snapshot());
        Assert.Equal(crowns, f.Live.Account.Crowns); Assert.Equal(saves + (durable ? 1 : 0), f.Saves);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        if (durable) Assert.Equal(transition is "purchase" or "activate", f.SavedWizard.EquipmentBehavior.EquippedItemIds.Contains(Fixture.ItemId));
        else Assert.Equal(ids, f.SavedWizard.EquipmentBehavior.EquippedItemIds);
    }

    [Theory]
    [InlineData("purchase")] [InlineData("activate")]
    public void NativeSerializationFailureAfterApplyingAnAcknowledgedEffectQuarantinesItsPartialRuntime(string transition) {
        using var f = new Fixture(); f.Arrange(transition); var saves = f.Saves;
        f.SerializeEffect = _ => new ByteString();
        f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); };
        var result = f.Change(transition);
        Assert.False(result.Saved); Assert.Null(result.RuntimeMessages); Assert.Equal(saves + 1, f.Saves);
        Assert.Single(f.Live.GameEffects.Snapshot()); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        Assert.True(f.SavedWizard.EquipmentBehavior.EquippedItemIds.Contains(Fixture.ItemId));
        var opens = f.Opened; Assert.False(WizardProgressionTransactions.TryGainExperience(f.Live, 250, out _));
        Assert.Equal(opens, f.Opened);
    }

    [Theory]
    [InlineData("purchase")] [InlineData("activate")]
    public void SuccessfulActivationCapturesNativeEffectsStateAndTimerOnlyAfterTheOneSave(string transition) {
        using var f = new Fixture(); f.Arrange(transition); var saves = f.Saves; var serialized = 0;
        f.SerializeEffect = _ => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(saves + 1, f.Saves); serialized++;
            Assert.True(Assert.Single(f.Live.EquipmentBehavior.EquippedItems).m_inactiveBehaviors
                .OfType<ClientElixirBehavior>().Single().m_statsApplied);
            return new ByteString(new byte[] { 1 });
        };
        var result = f.Change(transition); Assert.True(result.Saved); Assert.Equal(saves + 1, f.Saves); Assert.Equal(1, serialized);
        var messages = result.RuntimeMessages; Assert.NotNull(messages);
        Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(messages[0]);
        Assert.Equal((sbyte)1, Assert.IsType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>(messages[1]).EffectEnabled);
        Assert.Equal(Assert.Single(result.Ledger.Active).RemainingSeconds,
            Assert.IsType<WIZARD2_53_PROTOCOL.MSG_SETELIXIRTIMER>(messages[2]).TimerTime); Assert.Equal(3, messages.Count);
        var effect = Assert.Single(f.Live.GameEffects.Snapshot()); var damage = f.Live.GameStats.m_dmgBonusPercentAll;
        Assert.Empty(ElixirRuntime.AddApprovedEffects(f.Live, f.Live.EquipmentBehavior.GetItem(Fixture.ItemId), f.Template, false, false));
        var checkpoint = ElixirCollection.AdvanceOnline(f.Live, 1, f.Canonical.Definition);
        Assert.True(checkpoint.Saved); Assert.Empty(checkpoint.RuntimeMessages);
        Assert.Same(effect, Assert.Single(f.Live.GameEffects.Snapshot())); Assert.Equal(damage, f.Live.GameStats.m_dmgBonusPercentAll);
        Assert.True(f.Live.EquipmentBehavior.GetItem(Fixture.ItemId).m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_statsApplied);
    }

    [Theory]
    [InlineData("cancel")] [InlineData("expiry")]
    public void CommittedRemovalCapturesCleanupOrderAndReceiptReplayNeverSubtractsTwice(string transition) {
        using var f = new Fixture(); f.Arrange(transition); var effect = Assert.Single(f.Live.GameEffects.Snapshot());
        var result = f.Change(transition); Assert.True(result.Saved); Assert.Empty(f.Live.GameEffects.Snapshot());
        Assert.Equal(0f, f.Live.GameStats.m_dmgBonusPercentAll);
        var messages = result.RuntimeMessages; Assert.NotNull(messages); Assert.Equal(4, messages.Count);
        Assert.Equal(0u, Assert.IsType<WIZARD2_53_PROTOCOL.MSG_SETELIXIRTIMER>(messages[0]).TimerTime);
        Assert.Equal((sbyte)0, Assert.IsType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>(messages[1]).EffectEnabled);
        Assert.Equal(effect.m_internalID, Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(messages[2]).InternalID);
        Assert.Equal(Fixture.ItemId, Assert.IsType<GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_UNEQUIPITEM>(messages[3]).ItemID);
        Assert.Equal(messages, ElixirService.ExpireCommitted(f.Live, result, true));
        Assert.Equal(messages, ElixirService.ExpireCommitted(f.Live, result, true));
        Assert.Empty(ElixirService.ExpireCommitted(f.Live, result, false));
        Assert.Empty(f.Live.GameEffects.Snapshot()); Assert.Equal(0f, f.Live.GameStats.m_dmgBonusPercentAll);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task TrueLevelUpCannotFreezeOldElixirBenefitsBetweenAuthoritativeDuelModeAndEffects(bool leaving, bool pvp) {
        using var f = new Fixture(); Assert.True(f.Change("purchase").Saved);
        if (leaving) ElixirService.PublishCombatTransition(f.Live, true, pvp);
        using var reached = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var attempted = new ManualResetEventSlim(); using var completed = new ManualResetEventSlim();
        var effectsBefore = f.Live.GameEffects.Snapshot(); var saves = f.Saves;
        f.BeforeCombatEffects = wizard => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(!leaving, wizard.IsInDuel);
            Assert.Equal(effectsBefore, wizard.GameEffects.Snapshot()); reached.Set(); Assert.True(release.Wait(Timeout));
        };
        var mode = Task.Factory.StartNew(() => ElixirService.PublishCombatTransition(f.Live, !leaving, !leaving && pvp),
            TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<bool>? gain = null;
        try {
            Assert.True(reached.Wait(Timeout));
            gain = Task.Factory.StartNew(() => { attempted.Set();
                var accepted = WizardProgressionTransactions.TryGainExperience(f.Live, 250, out _); completed.Set(); return accepted;
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(attempted.Wait(Timeout)); Assert.False(completed.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.Equal(saves, f.Saves); Assert.Equal(150, f.Live.MagicSchoolBehavior.ExperiencePoints);
        }
        finally {
            release.Set();
            await mode.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            if (gain is not null) await gain.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        await mode.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.True(await gain!.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(saves + 1, f.Saves); Assert.Equal(350, f.SavedWizard.MagicSchoolBehavior.ExperiencePoints);
        Assert.Equal(4, f.Live.MagicSchoolBehavior.Level); Assert.Equal(191, f.Live.GameStats.m_currentHitpoints);
        Assert.Equal(77, f.Live.GameStats.m_currentMana); Assert.Equal(24, f.Live.PetOwnerBehavior.Energy);
        Assert.Equal(leaving || !pvp ? 1 : 0, f.Live.GameEffects.Count);
        Assert.Equal(leaving || !pvp ? Assert.Single(f.Canonical.Definition(Fixture.TemplateId).Effects).Value : 0f,
            f.Live.GameStats.m_dmgBonusPercentAll);
        Assert.True(WizardProgressionTransactions.RuntimeContextMatches(f.Live, f.SavedWizard));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public void FailedAuthoritativeCombatEffectPublicationQuarantinesBeforeAnotherStatWriterCanRun() {
        using var f = new Fixture(); Assert.True(f.Change("purchase").Saved); var saves = f.Saves;
        f.BeforeCombatEffects = wizard => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.True(wizard.IsInDuel);
            throw new InvalidOperationException("authored combat effect publication failure");
        };
        Assert.Throws<InvalidOperationException>(() => ElixirService.PublishCombatTransition(f.Live, true, true));
        Assert.False(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        var opens = f.Opened; Assert.False(WizardProgressionTransactions.TryGainExperience(f.Live, 250, out _));
        Assert.False(WizardPotionTransactions.TryDrink(f.Live, Fixture.Now, out _));
        Assert.Equal(opens, f.Opened); Assert.Equal(saves, f.Saves);
    }

    [Fact]
    public void DetachingAHeldPvpSessionPreservesTheRetainedStatsAliasUntilItsActualRelease() {
        using var f = new Fixture(); var purchase = f.Change("purchase"); Assert.True(purchase.Saved);
        var participantGameStats = f.Live.GameStats;
        ElixirService.PublishCombatTransition(f.Live, true, true);
        Assert.Equal(0f, participantGameStats.m_dmgBonusPercentAll); Assert.Empty(f.Live.GameEffects.Snapshot());
        var saves = f.Saves; ElixirService.DetachCombatSession(f.Live);
        Assert.False(f.Live.IsInDuel); Assert.Same(participantGameStats, f.Live.GameStats);
        Assert.Equal(saves, f.Saves); Assert.Equal(0f, participantGameStats.m_dmgBonusPercentAll);
        // CLASSIC: delayed publication/refresh must use the retained trusted PvP context after flag-only detach.
        Assert.Empty(ElixirService.PublishCommittedRuntime(f.Live, purchase));
        Assert.Empty(f.Live.GameEffects.Snapshot()); Assert.Equal(0f, participantGameStats.m_dmgBonusPercentAll);
        var released = ElixirService.PublishCombatTransition(f.Live, false, false);
        Assert.Single(released.OfType<GAME_5_PROTOCOL.MSG_ADDEFFECT>());
        Assert.Single(released.OfType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>(), packet => packet.EffectEnabled == 1);
        Assert.Same(participantGameStats, f.Live.GameStats);
        Assert.Equal(Assert.Single(f.Canonical.Definition(Fixture.TemplateId).Effects).Value, participantGameStats.m_dmgBonusPercentAll);
        Assert.True(WizardProgressionTransactions.TryGainExperience(f.Live, 250, out var gain));
        Assert.Equal(350, f.Live.MagicSchoolBehavior.ExperiencePoints); Assert.Equal(4, gain.Level);
        Assert.Same(participantGameStats, f.Live.GameStats); Assert.True(gain.Refill);
    }

    internal sealed class Fixture : IDisposable {
        internal const ulong Character = 42, AccountId = 7, ItemId = 9100;
        internal const uint TemplateId = 191103;
        internal static readonly DateTime Now = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        internal readonly ElixirTests.CanonicalFixture Canonical;
        internal readonly WizItemTemplate Template;
        internal readonly Dictionary<string, object> Documents = new();
        private readonly IDisposable _runtime;
        private readonly WizardCollection.TestStore? _oldStore;
        private readonly Func<IDocumentSession, List<WizClientObjectItem>>? _oldRows;
        private readonly ProgressionDependencies? _oldProgression;
        private readonly ElixirRuntimePublicationDependencies? _oldPublication;
        private readonly IDictionary<ulong, CoreTemplate> _templates;
        private readonly CoreTemplate? _previousTemplate;
        internal readonly Wizard Live;
        internal int Saves, Opened;
        internal bool FailSave, Durable;
        internal System.Action? BeforeSave, OnDispose;
        internal System.Action<Wizard>? BeforeEffects;
        internal System.Action<Wizard>? BeforeCombatEffects;
        internal Func<GameEffectBase, ByteString>? SerializeEffect;
        internal Wizard SavedWizard => (Wizard)Documents["wizard/42"];
        internal Fixture() {
            EquipmentAttachConcurrencyTests.Configure("[Character]\nPetEnergyTickInSeconds=60\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");
            Canonical = new(); _runtime = Canonical.OctoberRuntime(); Template = Canonical.Template(TemplateId);
            _templates = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory)
                .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            _previousTemplate = _templates.TryGetValue(TemplateId, out var previousTemplate) ? previousTemplate : null;
            _templates[TemplateId] = Template;
            var account = new Account { Crowns = 10000 }; typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(account, AccountId);
            account.CharacterIds.Add(Character); Documents["account/7"] = account;
            Documents["wizard/42"] = new Wizard { CharId = Character, AccountId = AccountId,
                GameStats = new(MagicSchool.None, 0) { m_baseHitpoints = 140, m_baseMana = 50, m_currentHitpoints = 23,
                    m_currentMana = 17, m_potionCharge = 1.25f, m_potionMax = 3, m_dmgBonusPercent = [] },
                MagicSchoolBehavior = new() { MagicSchool = MagicSchool.Fire, Level = 2, ExperiencePoints = 150 },
                PetOwnerBehavior = new() { Eggs = [], PetHatchTimes = [] },
                InventoryBehavior = new() { InventoryItemIds = [], Items = new() },
                EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = new(), SlotList = [] },
                StorageBehavior = new() { BankItemIds = [], Items = new() } };
            Live = CloneWizard(SavedWizard); Live.Account = CloneAccount(account); Live.HasInitializedRuntimeStats = true;
            Live.GameStats.Level = 2; Live.GameStats.MagicSchool = MagicSchool.Fire;
            Live.GameStats.m_baseHitpoints = 151; Live.GameStats.m_baseMana = 57;
            Live.GameStats.m_currentHitpoints = 31; Live.GameStats.m_currentMana = 19; Live.GameStats.m_powerPipBase = .375f;
            _oldStore = WizardCollection.TestStoreScope.Value; _oldRows = WizardInventoryTransactions.TestRowsScope.Value;
            _oldProgression = WizardProgressionTransactions.TestScope.Value; _oldPublication = ElixirService.TestRuntimeScope.Value;
            WizardCollection.TestStoreScope.Value = new(Open, (session, _) => session.Load<Wizard>("wizard/42"));
            WizardInventoryTransactions.TestRowsScope.Value = session => Documents.Where(pair => pair.Value is WizClientObjectItem)
                .Select(pair => session.Load<WizClientObjectItem>(pair.Key)).ToList();
            WizardProgressionTransactions.TestScope.Value = new() { LevelInfo = (_, level) => new MagicLevelInfo {
                m_level = level, m_hitpoints = 100 + 20 * level, m_mana = 30 + 10 * level, m_pipChance = level * .125f,
                m_petEnergy = 12 + 3 * level }, LevelAtXp = xp => (byte)Math.Max(1, xp / 100 + 1), XpAtLevel = level => (level - 1) * 100,
                MaxLevel = () => 4, XpCeiling = () => 350, Prepare = _ => true };
            ElixirService.TestRuntimeScope.Value = new() { Template = _ => Template,
                SerializeEffect = effect => SerializeEffect?.Invoke(effect) ?? new ByteString(new byte[] { 1 }),
                BeforeEffects = wizard => BeforeEffects?.Invoke(wizard), BeforeCombatEffects = wizard => BeforeCombatEffects?.Invoke(wizard) };
        }
        internal void Arrange(string transition) {
            if (transition == "activate") {
                var item = Canonical.Item(ItemId, TemplateId); item.m_characterId = Character;
                Documents["item/9100"] = item; SavedWizard.InventoryBehavior.InventoryItemIds.Add(ItemId);
                Live.InventoryBehavior.InventoryItemIds.Add(ItemId); Live.InventoryBehavior.Items.Add(CloneItem(item));
            } else if (transition is "cancel" or "expiry") Assert.True(Change("purchase").Saved);
        }
        internal ElixirResult Change(string transition) => transition switch {
            "purchase" => ElixirCollection.Purchase(Live, Canonical.Item(ItemId, TemplateId), Template, true,
                Canonical.Definition, (session, _) => session.Load<Account>("account/7"), _ => new ByteString(new byte[] { 1 })).Activation,
            "activate" => ElixirCollection.Activate(Live, ItemId, Canonical.Definition,
                (session, _, _) => session.Load<WizClientObjectItem>("item/9100")),
            "cancel" => ElixirCollection.Cancel(Live, ItemId, definitions: Canonical.Definition),
            "expiry" => ElixirCollection.AdvanceOnline(Live, uint.MaxValue, Canonical.Definition),
            _ => throw new ArgumentOutOfRangeException(nameof(transition)),
        };
        private IDocumentSession Open() {
            Opened++; var session = DispatchProxy.Create<IDocumentSession, SessionProxy>(); ((SessionProxy)(object)session).Owner = this; return session;
        }
        internal static object Clone(object value) => value switch {
            Wizard wizard => CloneWizard(wizard), Account account => CloneAccount(account),
            WizClientObjectItem item => CloneItem(item), ElixirLedger ledger => ledger.Copy(),
            _ => throw new NotSupportedException(value.GetType().Name),
        };
        private static WizClientObjectItem CloneItem(WizClientObjectItem item) => item with {
            m_inactiveBehaviors = item.m_inactiveBehaviors.Select(behavior => behavior is ClientElixirBehavior timer
                ? (BehaviorInstance)(timer with { }) : behavior).ToList(),
        };
        private static Wizard CloneWizard(Wizard source) {
            var stats = source.GameStats.CloneSnapshotWithGold(source.GameStats.m_currentGold);
            stats.Level = 0; stats.MagicSchool = MagicSchool.None; stats.m_powerPipBase = 0;
            stats.m_dmgBonusPercent = source.GameStats.m_dmgBonusPercent?.ToList();
            var wizard = new Wizard { CharId = source.CharId, AccountId = source.AccountId, GameStats = stats,
                MagicSchoolBehavior = new() { MagicSchool = source.MagicSchoolBehavior.MagicSchool, Level = source.MagicSchoolBehavior.Level,
                    ExperiencePoints = source.MagicSchoolBehavior.ExperiencePoints },
                PetOwnerBehavior = new() { Eggs = [], PetHatchTimes = [] },
                InventoryBehavior = new() { InventoryItemIds = source.InventoryBehavior.InventoryItemIds.ToList(), Items = new() },
                EquipmentBehavior = new() { EquippedItemIds = source.EquipmentBehavior.EquippedItemIds.ToList(), EquippedItems = new(),
                    SlotList = source.EquipmentBehavior.SlotList.Select(slot => new EquipmentSlot { ItemId = slot.ItemId,
                        SlotType = slot.SlotType, ItemName = slot.ItemName, EquippedSince = slot.EquippedSince }).ToList() },
                StorageBehavior = new() { BankItemIds = source.StorageBehavior.BankItemIds.ToList(), Items = new() } };
            wizard.PetOwnerBehavior.PublishCommittedEnergy(source.PetOwnerBehavior); return wizard;
        }
        private static Account CloneAccount(Account source) {
            var account = new Account { Crowns = source.Crowns }; typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(account, source.AccountId);
            account.CharacterIds.AddRange(source.CharacterIds); return account;
        }
        public void Dispose() {
            ElixirService.TestRuntimeScope.Value = _oldPublication; WizardProgressionTransactions.TestScope.Value = _oldProgression;
            WizardInventoryTransactions.TestRowsScope.Value = _oldRows; WizardCollection.TestStoreScope.Value = _oldStore;
            if (_previousTemplate is null) _templates.Remove(TemplateId); else _templates[TemplateId] = _previousTemplate;
            _runtime.Dispose(); Canonical.Dispose();
        }
    }

    public class SessionProxy : DispatchProxy {
        internal Fixture Owner = null!;
        private readonly Dictionary<string, object> _working = new();
        private readonly HashSet<object> _ignored = new(ReferenceEqualityComparer.Instance);
        private IAdvancedSessionOperations? _advanced;
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            switch (method!.Name) {
                case "get_Advanced":
                    if (_advanced is null) { _advanced = DispatchProxy.Create<IAdvancedSessionOperations, AdvancedProxy>(); ((AdvancedProxy)(object)_advanced).Session = this; }
                    return _advanced;
                case "Load":
                    var id = (string)args![0]!;
                    if (_working.TryGetValue(id, out var current)) return current;
                    return Owner.Documents.TryGetValue(id, out var persisted) ? _working[id] = Fixture.Clone(persisted) : null;
                case "Store": _working[(string)args![1]!] = args[0]!; return null;
                case "SaveChanges":
                    Assert.True(WizardCollection.HoldsWriteLane); Owner.BeforeSave?.Invoke();
                    if (Owner.FailSave && !Owner.Durable) throw new InvalidOperationException("authored failed elixir save");
                    foreach (var pair in _working) if (!_ignored.Contains(pair.Value)) Owner.Documents[pair.Key] = Fixture.Clone(pair.Value);
                    Owner.Saves++;
                    if (Owner.FailSave) throw new InvalidOperationException("authored durable elixir save lost acknowledgement");
                    return null;
                case "Dispose": Owner.OnDispose?.Invoke(); return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
        internal string DocumentId(object value) => _working.Single(pair => ReferenceEquals(pair.Value, value)).Key;
        internal void Ignore(object value) => _ignored.Add(value);
    }
    public class AdvancedProxy : DispatchProxy {
        internal SessionProxy Session = null!;
        private readonly IMetadataDictionary _metadata = DispatchProxy.Create<IMetadataDictionary, MetadataProxy>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "set_OptimisticConcurrencyMode" => null, "IgnoreChangesFor" => Ignore(args![0]!),
            "GetDocumentId" => Session.DocumentId(args![0]!), "GetMetadataFor" => _metadata,
            _ => throw new NotSupportedException(method.Name),
        };
        private object? Ignore(object value) { Session.Ignore(value); return null; }
    }
    public class MetadataProxy : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            Assert.Equal("set_Item", method!.Name); Assert.Equal(Raven.Client.Constants.Documents.Metadata.Collection, args![0]);
            Assert.Equal(WizardItemCollection.CollectionName, args[1]); return null;
        }
    }
}
