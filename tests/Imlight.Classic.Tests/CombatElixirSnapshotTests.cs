// CLASSIC: exercise the actual seat's stored scalar copy, rather than only a combat-mode helper.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.Math;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.Classic;
using Imlight.CoreLib.Classic.Elixirs;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;
using Type = System.Type;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class CombatElixirSnapshotTests {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(191099u, false)] [InlineData(191099u, true)]
    [InlineData(191101u, false)] [InlineData(191101u, true)]
    [InlineData(191105u, false)] [InlineData(191105u, true)]
    [InlineData(191107u, false)] [InlineData(191107u, true)]
    public void InitialSeatStoresTheActualDuelModeScalarsBeforeAnySessionNotification(uint templateId, bool pvp) {
        using var f = new Fixture(templateId, pvp);
        var stats = f.Live.GameStats; var enabled = stats.GetCombatGameStats(); var saves = f.Store.Saves;
        AssertSelectedDiffers(f.Baseline, enabled, templateId);
        f.Initialize();
        Assert.Same(stats, f.Circle.ParticipantGameStats); Assert.True(f.Live.IsInDuel);
        Assert.NotSame(stats, f.Circle.CombatParticipant.m_pGameStats);
        AssertStats(pvp ? f.Baseline : enabled, f.Circle.CombatParticipant.m_pGameStats);
        AssertStats(f.Circle.ParticipantGameStats.GetCombatGameStats(), f.Circle.CombatParticipant.m_pGameStats);
        Assert.Equal(stats.m_baseHitpoints, f.Circle.CombatParticipant.m_maxPlayerHealth);
        Assert.Equal(stats.m_currentHitpoints, f.Circle.CombatParticipant.m_playerHealth);
        Assert.Equal(stats.Level, f.Circle.CombatParticipant.m_mobLevel);
        Assert.Equal(pvp ? 0 : 1, f.Live.GameEffects.Count);
        Assert.Equal(!pvp, Timer(f.Live).m_statsApplied); Assert.Equal(saves, f.Store.Saves);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData(191099u, false)] [InlineData(191099u, true)]
    [InlineData(191101u, false)] [InlineData(191101u, true)]
    [InlineData(191105u, false)] [InlineData(191105u, true)]
    [InlineData(191107u, false)] [InlineData(191107u, true)]
    public void RejoinRefreshesFreshScalarAndLiveAliasesWithoutRebuildingRoundOrOwnedMinionState(uint templateId, bool pvp) {
        using var f = new Fixture(templateId, pvp); f.Initialize(); f.Hold();
        var participant = f.Circle.CombatParticipant; var oldStats = f.Circle.ParticipantGameStats;
        var oldObject = f.Circle.ParticipantObject; var oldSnapshot = participant.m_pGameStats;
        var pips = participant.m_pipCount = new PipCount { m_genericPips = 3, m_powerPips = 2 };
        var hanging = participant.m_hangingEffects = [new SpellEffect { m_effectParam = 991 }];
        var playDeck = participant.m_pPlayDeck; var rates = participant.m_pipRoundRates;
        participant.m_myTeamTurn = true; participant.m_PipsSuspended = true;
        f.Circle.BeguiledActions = 2; f.Circle._usedPipsForExperienceGain = 6; f.Circle.AddedToDuel = true;
        var identity = f.Circle.DeferredActionIdentity; var deck = f.Circle._combatDeck;
        var card = new Spell { m_templateID = 990 }; deck.LastGivenHand.Add(card);
        var heldHand = participant.m_pHand = new Hand { m_spellList = [card] };
        var minion = f.Duel.SubCircles[6];
        var minionStats = new ServerWizGameStats(default, 2) { m_currentHitpoints = 42 };
        var minionParticipant = new CombatParticipant { m_hangingEffects = [new SpellEffect { m_effectParam = 7 }],
            m_pipCount = new PipCount { m_genericPips = 1 }, m_playerHealth = 42 };
        CombatRegressionTests.SetProperty(minion, nameof(CombatDuelSubCircle.ParticipantObject), new CoreObject { m_templateID = 2 });
        CombatRegressionTests.SetProperty(minion, nameof(CombatDuelSubCircle.ParticipantGameStats), minionStats);
        CombatRegressionTests.SetProperty(minion, nameof(CombatDuelSubCircle.CombatParticipant), minionParticipant);
        CombatRegressionTests.SetProperty(minion, nameof(CombatDuelSubCircle.IsSummonedMinion), true);
        typeof(CombatDuelSubCircle).GetField("_minionOwnerObject", Private)!.SetValue(minion, oldObject);
        var fresh = f.ReloadWithEnabledBenefits(out var baseline); var enabled = fresh.GameStats.GetCombatGameStats();
        baseline.m_currentHitpoints = enabled.m_currentHitpoints = oldStats.m_currentHitpoints;
        var replacement = new CoreObject { m_templateID = 1, m_globalID = 4300 };
        var saves = f.Store.Saves;
        Assert.True(f.Circle.TryRejoinSeat(f.Endpoint, replacement, fresh, out var receipt));
        Assert.NotNull(receipt); Assert.Same(fresh, receipt.Wizard);
        Assert.Same(participant, f.Circle.CombatParticipant); Assert.Same(fresh.GameStats, f.Circle.ParticipantGameStats);
        Assert.NotSame(oldStats, f.Circle.ParticipantGameStats); Assert.NotSame(oldSnapshot, participant.m_pGameStats);
        Assert.Same(replacement, f.Circle.ParticipantObject); Assert.Same(fresh, f.Circle._wizard);
        AssertStats(pvp ? baseline : enabled, participant.m_pGameStats);
        AssertStats(fresh.GameStats.GetCombatGameStats(), participant.m_pGameStats);
        Assert.Equal(fresh.GameStats.m_baseHitpoints, participant.m_maxPlayerHealth);
        Assert.Equal(fresh.GameStats.m_currentHitpoints, participant.m_playerHealth);
        Assert.Equal(fresh.GameStats.Level, participant.m_mobLevel);
        Assert.Same(pips, participant.m_pipCount); Assert.Same(hanging, participant.m_hangingEffects);
        Assert.Same(playDeck, participant.m_pPlayDeck); Assert.Same(rates, participant.m_pipRoundRates);
        Assert.True(participant.m_myTeamTurn); Assert.True(participant.m_PipsSuspended);
        Assert.Equal(2, f.Circle.BeguiledActions); Assert.Equal(6, f.Circle._usedPipsForExperienceGain);
        Assert.True(f.Circle.AddedToDuel); Assert.Same(identity, f.Circle.DeferredActionIdentity);
        Assert.Same(deck, f.Circle._combatDeck); Assert.Same(card, Assert.Single(f.Circle.GetCurrentHand().m_spellList));
        Assert.Same(heldHand, participant.m_pHand); Assert.Same(card, Assert.Single(heldHand.m_spellList));
        Assert.Same(replacement, typeof(CombatDuelSubCircle).GetField("_minionOwnerObject", Private)!.GetValue(minion));
        Assert.True(minion.IsSummonedMinion); Assert.Same(minionParticipant, minion.CombatParticipant);
        Assert.Same(minionStats, minion.ParticipantGameStats); Assert.Equal(42, minionStats.m_currentHitpoints);
        Assert.Equal(1, minionParticipant.m_pipCount.m_genericPips); Assert.Single(minionParticipant.m_hangingEffects);
        Assert.False(f.Circle.Disconnected); Assert.Equal(0ul, f.Circle.HeldCharacterId);
        Assert.Equal(saves, f.Store.Saves); Assert.Equal(pvp ? 0 : 1, fresh.GameEffects.Count);
    }

    [Theory]
    [InlineData(191099u)] [InlineData(191101u)] [InlineData(191105u)] [InlineData(191107u)]
    public void DetachedHeldSeatRetainsSuppressedStatsUntilTheActualRelease(uint templateId) {
        using var f = new Fixture(templateId, true); f.Initialize(); f.Hold();
        var alias = f.Circle.ParticipantGameStats; var copy = f.Circle.CombatParticipant.m_pGameStats;
        ElixirService.DetachCombatSession(f.Live);
        Assert.False(f.Live.IsInDuel); Assert.True(f.Circle.Disconnected);
        Assert.NotNull(ActiveDuels.HeldFor(f.Live.CharId, Fixture.Now));
        Assert.Same(alias, f.Live.GameStats); Assert.Same(copy, f.Circle.CombatParticipant.m_pGameStats);
        AssertStats(f.Baseline, alias.GetCombatGameStats()); AssertStats(f.Baseline, copy);
        Assert.Empty(f.Live.GameEffects.Snapshot()); Assert.False(Timer(f.Live).m_statsApplied);
        var released = ElixirService.PublishCombatTransition(f.Live, false, false);
        Assert.Single(released.OfType<GAME_5_PROTOCOL.MSG_ADDEFFECT>());
        Assert.Same(alias, f.Live.GameStats); AssertSelectedDiffers(f.Baseline, alias.GetCombatGameStats(), templateId);
        Assert.Single(f.Live.GameEffects.Snapshot()); Assert.True(Timer(f.Live).m_statsApplied);
        // The detached participant copy stays frozen; future captures use the restored live state.
        AssertStats(f.Baseline, copy);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task TrueLevelUpCannotEnterAfterEffectsButBeforeTheActualStoredSnapshot(bool rejoin, bool pvp) {
        using var f = new Fixture(191105, pvp);
        Wizard wizard = f.Live;
        if (rejoin) { f.Initialize(); f.Hold(); wizard = f.ReloadWithEnabledBenefits(out _); }
        using var reached = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var attempted = new ManualResetEventSlim(); using var finished = new ManualResetEventSlim();
        var saves = f.Store.Saves; WizGameStats? expected = null; var heldHealth = f.Circle.ParticipantGameStats?.m_currentHitpoints;
        f.BeforeSnapshot = current => {
            Assert.Same(wizard, current); Assert.True(WizardCollection.HoldsWriteLane);
            Assert.Equal(pvp ? 0 : 1, current.GameEffects.Count);
            expected = current.GameStats.GetCombatGameStats();
            if (rejoin) expected.m_currentHitpoints = heldHealth!.Value;
            reached.Set(); Assert.True(release.Wait(Timeout), "release the actual combat scalar capture");
        };
        var capture = Task.Factory.StartNew(() => rejoin
            ? f.Circle.TryRejoinSeat(f.Endpoint, new CoreObject { m_templateID = 1, m_globalID = 4300 }, wizard, out _)
            : f.TryInitialize(), TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<bool>? gain = null;
        try {
            Assert.True(reached.Wait(Timeout));
            gain = Task.Factory.StartNew(() => { attempted.Set();
                var accepted = WizardProgressionTransactions.TryGainExperience(wizard, 250, out _); finished.Set(); return accepted;
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(attempted.Wait(Timeout)); Assert.False(finished.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.Equal(saves, f.Store.Saves); Assert.Equal(2, wizard.MagicSchoolBehavior.Level);
        }
        finally {
            release.Set(); await capture.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            if (gain is not null) await gain.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        Assert.True(await capture.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.True(await gain!.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.NotNull(expected); AssertStats(expected, f.Circle.CombatParticipant.m_pGameStats);
        Assert.Equal(2, f.Circle.CombatParticipant.m_mobLevel); Assert.Equal(4, wizard.MagicSchoolBehavior.Level);
        Assert.Same(wizard.GameStats, f.Circle.ParticipantGameStats); Assert.Equal(saves + 1, f.Store.Saves);
        Assert.Equal(350, f.Store.SavedWizard.MagicSchoolBehavior.ExperiencePoints);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(wizard));
    }

    [Theory]
    [InlineData(false, "effects")] [InlineData(false, "copy")] [InlineData(false, "bytes")]
    [InlineData(true, "effects")] [InlineData(true, "copy")] [InlineData(true, "bytes")]
    public void FailedPreparationQuarantinesAndCannotReplaceOrReleaseAHeldSeat(bool rejoin, string failure) {
        using var f = new Fixture(191105, false);
        Wizard wizard = f.Live;
        if (rejoin) { f.Initialize(); f.Hold(); wizard = f.ReloadWithEnabledBenefits(out _); }
        var oldActor = f.Circle.ParticipantActor; var oldObject = f.Circle.ParticipantObject;
        var oldStats = f.Circle.ParticipantGameStats; var oldParticipant = f.Circle.CombatParticipant;
        var identity = f.Circle.DeferredActionIdentity;
        // A disabled live benefit must be prepared again on PvE entry, exercising real add-effect serialization.
        if (failure == "bytes") ElixirService.PublishCombatTransition(wizard, true, true);
        if (failure == "effects") f.Store.BeforeCombatEffects = _ => {
            Assert.True(WizardCollection.HoldsWriteLane); throw new InvalidOperationException("authored preparation failure");
        };
        if (failure == "copy") f.BeforeSnapshot = _ => {
            Assert.True(WizardCollection.HoldsWriteLane); throw new InvalidOperationException("authored capture failure");
        };
        if (failure == "bytes") f.Store.SerializeEffect = _ => new ByteString();
        var saves = f.Store.Saves;
        if (rejoin) {
            Assert.False(f.Circle.TryRejoinSeat(f.Endpoint, new CoreObject { m_templateID = 1, m_globalID = 4300 }, wizard, out var receipt));
            Assert.Null(receipt); Assert.Same(oldActor, f.Circle.ParticipantActor); Assert.Same(oldObject, f.Circle.ParticipantObject);
            Assert.Same(oldStats, f.Circle.ParticipantGameStats); Assert.Same(oldParticipant, f.Circle.CombatParticipant);
            Assert.Same(identity, f.Circle.DeferredActionIdentity); Assert.Same(f.Live, f.Circle._wizard);
            Assert.True(f.Circle.Disconnected); Assert.Equal(f.Live.CharId, f.Circle.HeldCharacterId);
            Assert.NotNull(ActiveDuels.HeldFor(f.Live.CharId, Fixture.Now));
        } else {
            Assert.Null(f.Circle.AssignParticipant(f.Endpoint, new CoreObject { m_templateID = 1, m_globalID = 4300 }));
            Assert.False(f.Circle.Occupied); Assert.Null(f.Circle.CombatParticipant); Assert.Null(f.Circle.ParticipantGameStats);
            Assert.Null(f.Circle._wizard); Assert.Null(f.Circle._combatDeck);
        }
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(wizard)); Assert.False(WizardCollection.HoldsWriteLane);
        Assert.Equal(saves, f.Store.Saves); var opened = f.Store.Opened;
        Assert.False(WizardProgressionTransactions.TryGainExperience(wizard, 250, out _));
        Assert.False(WizardPotionTransactions.TryDrink(wizard, Fixture.Now, out _)); Assert.Equal(opened, f.Store.Opened);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AnAlreadyUncertainSnapshotRefusesBeforePreparingEffectsOrChangingSeatAliases(bool rejoin) {
        using var f = new Fixture(191105, true); Wizard wizard = f.Live;
        if (rejoin) { f.Initialize(); f.Hold(); wizard = f.ReloadWithEnabledBenefits(out _); }
        var stats = f.Circle.ParticipantGameStats; var participant = f.Circle.CombatParticipant;
        var oldObject = f.Circle.ParticipantObject; var called = false;
        f.Store.BeforeCombatEffects = _ => called = true; f.BeforeSnapshot = _ => called = true;
        WizardCollection.MarkInventorySnapshotUncertain(wizard);
        if (rejoin) {
            Assert.False(f.Circle.TryRejoinSeat(f.Endpoint, new CoreObject { m_templateID = 1 }, wizard, out var receipt));
            Assert.Null(receipt); Assert.Same(stats, f.Circle.ParticipantGameStats); Assert.Same(participant, f.Circle.CombatParticipant);
            Assert.Same(oldObject, f.Circle.ParticipantObject); Assert.True(f.Circle.Disconnected);
            Assert.NotNull(ActiveDuels.HeldFor(wizard.CharId, Fixture.Now));
        } else Assert.Null(f.Circle.AssignParticipant(f.Endpoint, new CoreObject { m_templateID = 1 }));
        Assert.False(called);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualCombatServiceEmitsTheImmutableReceiptAfterMountStowAndSecondaryNotificationsCannotRepeatIt(bool pvp) {
        using var f = new Fixture(191105, pvp); f.Mount();
        if (!pvp) ElixirService.PublishCombatTransition(f.Live, true, true);
        f.Initialize(); Assert.NotNull(f.Receipt); Assert.Equal(2, f.Receipt.Messages.Length);
        var stats = f.Circle.ParticipantGameStats; var copy = f.Circle.CombatParticipant.m_pGameStats;
        var native = f.Receipt.Messages.ToArray();
        Assert.IsType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>(native[1]);
        if (pvp) Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(native[0]);
        else Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(native[0]);
        var message = new COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL { DuelActor = f.Endpoint, Duel = f.Duel,
            SubCircle = f.Circle, SlotPosition = new Vector3(2, 3, 4), ElixirReceipt = f.Receipt };
        // The consumer must use the prepared receipt. A new late mode publication here would fail this assertion.
        f.Store.BeforeCombatEffects = _ => throw new InvalidOperationException("late combat mode preparation must not run");
        var scopes = new CapturedScopes(); var packets = Channel.CreateUnbounded<IMessage>();
        var socket = f.Actors.ActorOf(Props.Create(() => new SocketProbe(packets)), "socket");
        var session = f.Actors.ActorOf(Props.CreateBy(new SessionProducer(socket)), "session");
        var instance = await session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
        var combat = f.Actors.ActorOf(Props.Create(() => new CombatProbe(instance, f.Live, f.Circle.ParticipantObject, scopes)), "combat");
        var elixir = f.Actors.ActorOf(Props.Create(() => new ElixirProbe(instance, f.Live, scopes)), "elixir");
        Assert.True(await combat.Ask<bool>(new FixtureReady(), Timeout, TestContext.Current.CancellationToken));
        Assert.True(await elixir.Ask<bool>(new FixtureReady(), Timeout, TestContext.Current.CancellationToken));
        async Task<IMessage[]> Drain() {
            await session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
            await socket.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
            var output = new List<IMessage>(); while (packets.Reader.TryRead(out var packet)) output.Add(packet); return [.. output];
        }
        Assert.True(await combat.Ask<bool>(new EntryStep(message), Timeout, TestContext.Current.CancellationToken));
        var first = await Drain(); Assert.Equal(3, first.Length);
        var stow = Assert.IsType<GAME_5_PROTOCOL.MSG_EQUIPITEM>(first[0]);
        Assert.Equal(Fixture.MountId, (ulong)stow.ItemID); Assert.Equal(0, (int)stow.IsEquip);
        Assert.Same(native[0], first[1]); Assert.Same(native[1], first[2]);
        Assert.Null(f.Live.EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Mount));
        Assert.Equal(Fixture.MountId, f.Live.CombatStowedMountId);
        foreach (var entered in new[] { true, false, true, false }) {
            Assert.True(await elixir.Ask<bool>(new SecondaryStep(entered, message), Timeout, TestContext.Current.CancellationToken));
            Assert.Empty(await Drain());
        }
        Assert.Same(stats, f.Circle.ParticipantGameStats); Assert.Same(copy, f.Circle.CombatParticipant.m_pGameStats);
        AssertStats(copy, stats.GetCombatGameStats()); Assert.True(f.Live.IsInDuel);
        Assert.Equal(pvp ? 0 : 1, f.Live.GameEffects.Count); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("uncertain")] [InlineData("different-wizard")] [InlineData("stow-uncertain")]
    public void ReceiptConsumptionRefusesUntrustedOrFailedPreparationWithoutSendingAnyNativePacket(string failure) {
        using var f = new Fixture(191105, true); f.Initialize(); var before = false; var sent = new List<IMessage>();
        var target = failure == "different-wizard" ? f.ReloadWithEnabledBenefits(out _) : f.Live;
        if (failure == "uncertain") WizardCollection.MarkInventorySnapshotUncertain(target);
        var message = new COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL { ElixirReceipt = f.Receipt, Duel = f.Duel };
        Assert.False(CombatService.CompleteCombatEntry(target, message, () => {
            before = true; Assert.Equal("stow-uncertain", failure); WizardCollection.MarkInventorySnapshotUncertain(target);
        }, sent.Add));
        Assert.Equal(failure == "stow-uncertain", before); Assert.Empty(sent);
        Assert.Empty(f.Live.GameEffects.Snapshot()); AssertStats(f.Baseline, f.Circle.CombatParticipant.m_pGameStats);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void StockAndFeatureDisabledProfilesKeepTheOriginalCaptureAndRejoinBehavior(bool rejoin, bool stock) {
        using var f = new Fixture(191105, true);
        Wizard wizard = f.Live;
        if (rejoin) { f.Initialize(); f.Hold(); wizard = f.ReloadWithEnabledBenefits(out _); }
        var previousCopy = f.Circle.CombatParticipant?.m_pGameStats; var enabled = wizard.GameStats.GetCombatGameStats();
        var called = false; f.BeforeSnapshot = _ => called = true; f.Store.BeforeCombatEffects = _ => called = true;
        var rulesField = typeof(ClassicRuntime).GetField("s_rules", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldRules = rulesField.GetValue(null); var features = ClassicRuntime.Rules.Profile.Features;
        try {
            if (stock) rulesField.SetValue(null, ClassicRules.Stock);
            else typeof(ClassicProfile).GetProperty(nameof(ClassicProfile.Features))!.SetValue(ClassicRuntime.Rules.Profile,
                new FeatureSwitches(new Dictionary<string, bool> { [ClassicFeatures.Elixirs] = false }));
            Assert.False(ElixirService.PreparesCombatSnapshots);
            if (rejoin) {
                Assert.True(f.Circle.TryRejoinSeat(f.Endpoint, new CoreObject { m_templateID = 1 }, wizard, out var receipt));
                Assert.Null(receipt); Assert.Same(previousCopy, f.Circle.CombatParticipant.m_pGameStats);
                Assert.Equal(enabled.m_currentHitpoints, f.Circle.CombatParticipant.m_playerHealth);
            } else { f.Initialize(); Assert.Null(f.Receipt); AssertStats(enabled, f.Circle.CombatParticipant.m_pGameStats); }
            Assert.False(called); Assert.Single(wizard.GameEffects.Snapshot()); Assert.True(Timer(wizard).m_statsApplied);
        }
        finally { typeof(ClassicProfile).GetProperty(nameof(ClassicProfile.Features))!.SetValue(((ClassicRules)oldRules!).Profile, features);
            rulesField.SetValue(null, oldRules); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DeferredStarterAdmissionKeepsItsPreparedReceiptUntilOneNoticeOrRefusesItIfThePairFails(bool pairedFailure) {
        using var f = new Fixture(191105, true);
        Assert.NotNull(f.Circle.AssignParticipant(f.Endpoint, new CoreObject { m_templateID = 1, m_globalID = 4201 }, deferNotification: true));
        AssertStats(f.Baseline, f.Circle.CombatParticipant.m_pGameStats);
        await f.Endpoint.Ask<ActorIdentity>(new Identify("prepared"), Timeout, TestContext.Current.CancellationToken);
        Assert.Empty(f.Messages);
        if (pairedFailure) {
            f.Circle.RefusePreparedAdmission();
            Assert.False(f.Circle.PublishAssignedParticipantNotice()); Assert.False(f.Circle.Occupied);
            Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        } else {
            Assert.True(f.Circle.PublishAssignedParticipantNotice()); Assert.False(f.Circle.PublishAssignedParticipantNotice());
        }
        await f.Endpoint.Ask<ActorIdentity>(new Identify("published"), Timeout, TestContext.Current.CancellationToken);
        var notices = f.Messages.OfType<COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL>().ToArray();
        if (pairedFailure) { Assert.Empty(notices); Assert.Contains("Close", f.Messages); }
        else {
            var notice = Assert.Single(notices); Assert.NotNull(notice.ElixirReceipt); Assert.Same(f.Live, notice.ElixirReceipt.Wizard);
            Assert.Equal(2, notice.ElixirReceipt.Messages.Length); Assert.Same(f.Circle, notice.SubCircle);
            Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualPvpAdmissionReportsFailureWithoutPublishingAJoinedSeat(bool uncertain) {
        using var f = new Fixture(191105, true); f.Circle.RemoveParticipant();
        typeof(CombatDuelComponent).GetField("_pvpLobby", Private)!.SetValue(f.Duel, true);
        if (uncertain) WizardCollection.MarkInventorySnapshotUncertain(f.Live);
        else f.BeforeSnapshot = _ => throw new InvalidOperationException("authored admission preparation failure");
        Assert.False((bool)CombatRegressionTests.Invoke(f.Duel, "PvpSeat", f.Endpoint,
            new CoreObject { m_templateID = 1, m_globalID = 4300 }, 1)!);
        await f.Endpoint.Ask<ActorIdentity>(new Identify("refused"), Timeout, TestContext.Current.CancellationToken);
        Assert.All(f.Duel.SubCircles, circle => Assert.False(circle.Occupied));
        Assert.DoesNotContain(f.Messages, message => message is COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL);
        Assert.Equal("Close", Assert.Single(f.Messages));
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("uncertain")] [InlineData("copy")] [InlineData("bytes")]
    public async Task TheActualRejoinCallerKeepsTheHoldAndOldAliasesWhenPreparationRefuses(string failure) {
        using var f = new Fixture(191105, false); f.Initialize(); f.Hold();
        var fresh = f.ReloadWithEnabledBenefits(out _); var stats = f.Circle.ParticipantGameStats;
        var obj = f.Circle.ParticipantObject; var participant = f.Circle.CombatParticipant;
        if (failure == "uncertain") WizardCollection.MarkInventorySnapshotUncertain(fresh);
        if (failure == "copy") f.BeforeSnapshot = _ => throw new InvalidOperationException("authored rejoin capture failure");
        if (failure == "bytes") { ElixirService.PublishCombatTransition(fresh, true, true); f.Store.SerializeEffect = _ => new ByteString(); }
        // Timers is intentionally absent: a refusal must return before cancelling the held-seat expiry.
        Assert.False((bool)CombatRegressionTests.Invoke(f.Duel, "TryRejoin", new CoreObject { m_templateID = 1 }, f.Endpoint, fresh)!);
        Assert.NotNull(ActiveDuels.HeldFor(fresh.CharId, Fixture.Now)); Assert.True(f.Circle.Disconnected);
        Assert.Equal(fresh.CharId, f.Circle.HeldCharacterId); Assert.Same(obj, f.Circle.ParticipantObject);
        Assert.Same(stats, f.Circle.ParticipantGameStats); Assert.Same(participant, f.Circle.CombatParticipant);
        Assert.Same(f.Live, f.Circle._wizard); Assert.True(WizardCollection.IsInventorySnapshotUncertain(fresh));
        await f.Endpoint.Ask<ActorIdentity>(new Identify("rejoin refused"), Timeout, TestContext.Current.CancellationToken);
        Assert.Equal("Close", Assert.Single(f.Messages));
    }

    private static ClientElixirBehavior Timer(Wizard wizard)
        => Assert.Single(wizard.EquipmentBehavior.GetItem(ElixirTransitionConcurrencyTests.Fixture.ItemId)
            .m_inactiveBehaviors.OfType<ClientElixirBehavior>());

    private static void AssertStats(WizGameStats expected, WizGameStats actual) {
        Assert.Equal(expected.m_baseHitpoints, actual.m_baseHitpoints); Assert.Equal(expected.m_baseMana, actual.m_baseMana);
        Assert.Equal(expected.m_currentHitpoints, actual.m_currentHitpoints); Assert.Equal(expected.m_currentMana, actual.m_currentMana);
        Assert.Equal(expected.m_accBonusPercentAll, actual.m_accBonusPercentAll);
        Assert.Equal(expected.m_powerPipBase, actual.m_powerPipBase);
        Assert.Equal(expected.m_powerPipBonusPercentAll, actual.m_powerPipBonusPercentAll);
        Assert.Equal(expected.m_dmgBonusPercentAll, actual.m_dmgBonusPercentAll);
    }

    private static void AssertSelectedDiffers(WizGameStats before, WizGameStats after, uint templateId) {
        switch (templateId) {
            case 191099: Assert.NotEqual(before.m_powerPipBonusPercentAll, after.m_powerPipBonusPercentAll); break;
            case 191101: Assert.NotEqual(before.m_accBonusPercentAll, after.m_accBonusPercentAll); break;
            case 191105: Assert.NotEqual(before.m_baseHitpoints, after.m_baseHitpoints); break;
            case 191107: Assert.NotEqual(before.m_baseMana, after.m_baseMana); break;
            default: throw new ArgumentOutOfRangeException(nameof(templateId));
        }
    }

    private sealed class Fixture : IDisposable {
        internal static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        internal const ulong MountId = 9200;
        private const uint MountTemplateId = uint.MaxValue - 4243;
        internal readonly ElixirTransitionConcurrencyTests.Fixture Store = new();
        internal Wizard Live => Store.Live;
        internal readonly ActorSystem Actors;
        internal readonly IActorRef Endpoint;
        internal readonly ConcurrentQueue<object> Messages = new();
        internal readonly CombatDuelComponent Duel;
        internal CombatDuelSubCircle Circle => Duel.SubCircles[4];
        internal readonly WizItemTemplate Template;
        internal readonly WizGameStats Baseline;
        internal readonly uint TemplateId;
        internal System.Action<Wizard>? BeforeSnapshot;
        internal CombatElixirEntryReceipt? Receipt;
        private readonly IDictionary<ulong, CoreTemplate> _cache;
        private readonly CoreTemplate? _previousTemplate;
        private readonly ElixirRuntimePublicationDependencies _previousPublication;
        private CoreTemplate? _previousMount;
        private bool _mounted;
        internal Fixture(uint templateId, bool pvp) {
            TemplateId = templateId; Template = Store.Canonical.Template(templateId);
            _cache = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory)
                .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            _previousTemplate = _cache.TryGetValue(templateId, out var previous) ? previous : null;
            _cache[templateId] = Template;
            _previousPublication = ElixirService.TestRuntimeScope.Value!;
            ElixirService.TestRuntimeScope.Value = new() { Template = _ => Template,
                SerializeEffect = effect => Store.SerializeEffect?.Invoke(effect) ?? new ByteString(new byte[] { 1 }),
                BeforeCombatEffects = wizard => Store.BeforeCombatEffects?.Invoke(wizard),
                BeforeCombatSnapshot = wizard => BeforeSnapshot?.Invoke(wizard) };
            Live.SpellbookBehavior = new(); Baseline = Live.GameStats.GetCombatGameStats();
            var bought = ElixirCollection.Purchase(Live, Store.Canonical.Item(ElixirTransitionConcurrencyTests.Fixture.ItemId, templateId),
                Template, true, Store.Canonical.Definition, (session, _) => session.Load<Account>("account/7"),
                _ => new ByteString(new byte[] { 1 }));
            Assert.True(bought.Saved); Assert.Single(Live.GameEffects.Snapshot());
            Actors = ActorSystem.Create("combat-snapshot-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
            Endpoint = Actors.ActorOf(Props.Create(() => new Sink(Messages)), "endpoint");
            var entity = (ZoneEntity)RuntimeHelpers.GetUninitializedObject(typeof(ZoneEntity));
            CombatRegressionTests.SetProperty(entity, nameof(ZoneEntity.ActiveGameObject), new CoreObject { m_globalID = 4200 });
            CombatRegressionTests.SetProperty(entity, nameof(ZoneEntity.ZoneRef), Endpoint);
            Duel = new CombatDuelComponent(entity); Duel.AttachTo(Endpoint);
            CombatRegressionTests.SetProperty(Duel, nameof(CombatDuelComponent.Duel), new Duel { m_bPVP = pvp, m_firstTeamToAct = 1 });
            CombatRegressionTests.SetProperty(Duel, nameof(CombatDuelComponent.SubCircles), Enumerable.Range(0, 8)
                .Select(slot => new CombatDuelSubCircle(Duel, 0, 0, default, slot)).ToArray());
            CombatRegressionTests.SetProperty(Circle, nameof(CombatDuelSubCircle.ParticipantActor), Endpoint);
            Circle.WorldPosition = new Vector3(0, 0, 0);
            CombatRegressionTests.SetProperty(Circle, nameof(CombatDuelSubCircle.ParticipantObject),
                new CoreObject { m_templateID = 1, m_globalID = 4201 });
            ActiveWizardDirectory.SetWizard(Endpoint, Live);
        }
        internal void Initialize() => Assert.True(TryInitialize());
        internal bool TryInitialize() {
            var arguments = new object?[] { null };
            var accepted = (bool)typeof(CombatDuelSubCircle).GetMethod("InitializePlayerSubCircle", Private)!.Invoke(Circle, arguments)!;
            Receipt = (CombatElixirEntryReceipt?)arguments[0]; return accepted;
        }
        internal void Hold() {
            Circle.HoldSeat(Now);
            ActiveDuels.Hold(new HeldSeat(Live.CharId, "Authored/Snapshot", 42, Now.AddMinutes(1)));
        }
        internal void Mount() {
            _previousMount = _cache.TryGetValue(MountTemplateId, out var previous) ? previous : null;
            _cache[MountTemplateId] = new WizItemTemplate { m_templateID = MountTemplateId,
                m_objectName = "Authored snapshot mount", m_adjectiveList = ["Mount"], m_equipEffects = [], m_behaviors = [] };
            _mounted = true; Live.PlayerNameBehavior = new() { NameOverride = "Snapshot Tester" }; Live.MountOwnerBehavior = new();
            var item = new WizClientObjectItem { m_globalID = MountId, m_templateID = MountTemplateId,
                m_characterId = Live.CharId, m_inactiveBehaviors = [] };
            Live.EquipmentBehavior.EquippedItemIds.Add(MountId); Live.EquipmentBehavior.EquippedItems.Add(item);
            Live.EquipmentBehavior.SlotList.Add(new EquipmentSlot { ItemId = MountId, SlotType = EquipmentSlotType.Mount, ItemName = "Mount" });
            Store.SavedWizard.EquipmentBehavior.EquippedItemIds.Add(MountId);
            Store.SavedWizard.EquipmentBehavior.SlotList.Add(new EquipmentSlot { ItemId = MountId, SlotType = EquipmentSlotType.Mount, ItemName = "Mount" });
        }
        internal Wizard ReloadWithEnabledBenefits(out WizGameStats baseline) {
            var wizard = (Wizard)ElixirTransitionConcurrencyTests.Fixture.Clone(Store.SavedWizard);
            wizard.Account = Live.Account; wizard.HasInitializedRuntimeStats = true; wizard.SpellbookBehavior = new();
            wizard.GameStats.Level = Live.GameStats.Level; wizard.GameStats.MagicSchool = Live.GameStats.MagicSchool;
            wizard.GameStats.m_baseHitpoints = 183; wizard.GameStats.m_baseMana = 73;
            wizard.GameStats.m_currentHitpoints = 47; wizard.GameStats.m_currentMana = 29;
            wizard.GameStats.m_powerPipBase = Live.GameStats.m_powerPipBase;
            baseline = wizard.GameStats.GetCombatGameStats();
            var item = (WizClientObjectItem)ElixirTransitionConcurrencyTests.Fixture.Clone(Live.EquipmentBehavior.GetItem(
                ElixirTransitionConcurrencyTests.Fixture.ItemId));
            Assert.Single(item.m_inactiveBehaviors.OfType<ClientElixirBehavior>()).m_statsApplied = false;
            wizard.EquipmentBehavior.EquippedItems.Add(item);
            var ledger = (ElixirLedger)Store.Documents.Values.Single(value => value is ElixirLedger);
            ElixirRuntime.PublishValidated(wizard, ledger.Copy());
            Assert.Single(ElixirRuntime.AddApprovedEffects(wizard, item, Template, false, false));
            return wizard;
        }
        public void Dispose() {
            ActiveWizardDirectory.Remove(Endpoint); ActiveDuels.Release(Live.CharId);
            Actors.Terminate().GetAwaiter().GetResult(); Actors.Dispose();
            ElixirService.TestRuntimeScope.Value = _previousPublication;
            if (_mounted) { if (_previousMount is null) _cache.Remove(MountTemplateId); else _cache[MountTemplateId] = _previousMount; }
            if (_previousTemplate is null) _cache.Remove(TemplateId); else _cache[TemplateId] = _previousTemplate;
            Store.Dispose();
        }
    }
    private sealed class Sink : ReceiveActor { public Sink(ConcurrentQueue<object> messages) { ReceiveAny(messages.Enqueue); } }
    private sealed record EntryStep(COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL Message);
    private sealed record SecondaryStep(bool Entered, COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL Message);
    private sealed record FixtureReady;
    private sealed class SocketProbe : ReceiveActor {
        // Props.Create activates actor types through their public constructors, including private nested types.
        public SocketProbe(Channel<IMessage> packets) { Receive<IMessage>(message => packets.Writer.TryWrite(message)); }
    }
    private sealed class SessionProducer(IActorRef socket) : IIndirectActorProducer {
        public Type ActorType => typeof(SessionActor);
        public ActorBase Produce() => new SessionActor(socket);
        public void Release(ActorBase actor) { }
    }
    // AsyncLocal dependencies are deliberately installed on each actor thread, then restored.
    private sealed class CapturedScopes {
        private readonly WizardCollection.TestStore? _store = WizardCollection.TestStoreScope.Value;
        private readonly Func<Raven.Client.Documents.Session.IDocumentSession, List<WizClientObjectItem>>? _rows = WizardInventoryTransactions.TestRowsScope.Value;
        private readonly ProgressionDependencies? _progression = WizardProgressionTransactions.TestScope.Value;
        private readonly ElixirRuntimePublicationDependencies? _publication = ElixirService.TestRuntimeScope.Value;
        internal IDisposable Enter() {
            var previousStore = WizardCollection.TestStoreScope.Value; var previousRows = WizardInventoryTransactions.TestRowsScope.Value;
            var previousProgression = WizardProgressionTransactions.TestScope.Value; var previousPublication = ElixirService.TestRuntimeScope.Value;
            WizardCollection.TestStoreScope.Value = _store; WizardInventoryTransactions.TestRowsScope.Value = _rows;
            WizardProgressionTransactions.TestScope.Value = _progression; ElixirService.TestRuntimeScope.Value = _publication;
            return new Restore(() => { WizardCollection.TestStoreScope.Value = previousStore; WizardInventoryTransactions.TestRowsScope.Value = previousRows;
                WizardProgressionTransactions.TestScope.Value = previousProgression; ElixirService.TestRuntimeScope.Value = previousPublication; });
        }
    }
    private sealed class CombatProbe : CombatService {
        private readonly CapturedScopes _scopes;
        public CombatProbe(SessionActor session, Wizard wizard, CoreObject obj, CapturedScopes scopes) : base(session) {
            _scopes = scopes; typeof(MessageService).GetField("_cachedWizard", Private)!.SetValue(this, wizard);
            typeof(MessageService).GetField("_cachedWizardGameObject", Private)!.SetValue(this, obj);
        }
        protected override void ConfigureReceivers() {
            Receive<FixtureReady>(_ => Sender.Tell(_scopes is not null));
            Receive<EntryStep>(step => {
                try { using var scope = _scopes.Enter(); typeof(CombatService).GetMethod("RecieveDuelAdd", Private)!.Invoke(this, [step.Message]); Sender.Tell(true); }
                catch (Exception exception) { Sender.Tell(new Status.Failure(exception)); }
            }); base.ConfigureReceivers();
        }
    }
    private sealed class ElixirProbe : ElixirService {
        private readonly CapturedScopes _scopes;
        public ElixirProbe(SessionActor session, Wizard wizard, CapturedScopes scopes) : base(session) {
            _scopes = scopes; typeof(ElixirService).GetField("_wizard", Private)!.SetValue(this, wizard);
        }
        protected override void ConfigureReceivers() {
            Receive<FixtureReady>(_ => Sender.Tell(_scopes is not null));
            Receive<string>(value => value == "ClassicElixirTick", _ => { });
            Receive<SecondaryStep>(step => {
                try { using var scope = _scopes.Enter();
                    if (step.Entered) typeof(ElixirService).GetMethod("DuelEntered", Private)!.Invoke(this, [step.Message]);
                    else typeof(ElixirService).GetMethod("DuelLeft", Private)!.Invoke(this, null);
                    Sender.Tell(true);
                } catch (Exception exception) { Sender.Tell(new Status.Failure(exception)); }
            }); base.ConfigureReceivers();
        }
        protected override void OnPreDispose() { using var scope = _scopes.Enter(); base.OnPreDispose(); }
    }
    private sealed class Restore(System.Action undo) : IDisposable { public void Dispose() => undo(); }
}
