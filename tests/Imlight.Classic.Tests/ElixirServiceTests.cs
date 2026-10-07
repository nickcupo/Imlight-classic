using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic.Elixirs;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ElixirServiceTests {
    [Fact]
    public void AFailedCheckpointRetainsDebtAndACommittedCheckpointRetainsFractions() {
        var clock = new ElixirOnlineClock(1000);
        clock.Begin([1], 0);
        Assert.Empty(clock.Pending([1], 750));
        var failed = clock.Pending([1], 1750);
        Assert.Equal(1u, failed[1]);
        var retry = clock.Pending([1], 2750);
        Assert.Equal(2u, retry[1]);
        clock.Commit(retry);
        Assert.Empty(clock.Pending([1], 2999));
        Assert.Equal(1u, clock.Pending([1], 3000)[1]);
    }

    [Fact]
    public void ANewItemDoesNotInheritElapsedTimeFromAnExistingItem() {
        var clock = new ElixirOnlineClock(1000);
        clock.Begin([1], 1000);
        var elapsed = clock.Pending([1, 2], 5500);
        Assert.Equal(4u, elapsed[1]);
        Assert.False(elapsed.ContainsKey(2));
        clock.Commit(elapsed);
        elapsed = clock.Pending([1, 2], 6500);
        Assert.Equal(1u, elapsed[1]);
        Assert.Equal(1u, elapsed[2]);
    }

    [Fact]
    public void ANewOnlineSessionNeverChargesTheOfflineGap() {
        var clock = new ElixirOnlineClock(1000);
        clock.Begin([1], 0);
        clock.Commit(clock.Pending([1], 2000));
        clock.Begin([1], 1_000_000_000);
        Assert.Empty(clock.Pending([1], 1_000_000_999));
        Assert.Equal(1u, clock.Pending([1], 1_000_001_000)[1]);
    }

    [Fact]
    public void RemovingAnItemDropsItsAnchorAndReappearingStartsANewOne() {
        var clock = new ElixirOnlineClock(1000);
        clock.Begin([1, 2], 0);
        Assert.Single(clock.Pending([2], 5000));
        var elapsed = clock.Pending([1, 2], 6000);
        Assert.False(elapsed.ContainsKey(1));
        Assert.Equal(6u, elapsed[2]);
    }

    [Fact]
    public void DuplicateOrZeroIdsCannotDoubleChargeAndBackwardTicksCannotCharge() {
        var clock = new ElixirOnlineClock(1000);
        clock.Begin([0, 1, 1], 1000);
        Assert.Empty(clock.Pending([0, 1, 1], 500));
        var elapsed = clock.Pending([0, 1, 1], 2000);
        Assert.Equal(1u, Assert.Single(elapsed).Value);
        Assert.Throws<InvalidOperationException>(() => clock.Commit(new System.Collections.Generic.Dictionary<ulong, uint> { [2] = 1 }));
    }

    [Fact]
    public void CommittedExpiryClearsTimerBenefitAndOwnEffectBeforeRemovingEquipmentWithoutARefund() {
        using var fixture = new ExpiryFixture();
        var result = fixture.Expire();
        Assert.True(result.Saved);
        var messages = ElixirService.ExpireCommitted(fixture.Wizard, result, true, fixture.TemplateFor);

        Assert.Equal(4, messages.Count);
        var timer = Assert.IsType<WIZARD2_53_PROTOCOL.MSG_SETELIXIRTIMER>(messages[0]);
        Assert.Equal(9001ul, timer.GlobalID);
        Assert.Equal(0u, timer.TimerTime);
        var state = Assert.IsType<WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE>(messages[1]);
        Assert.Equal(9001ul, state.parentID);
        Assert.Equal((sbyte)0, state.EffectEnabled);
        var effect = Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(messages[2]);
        Assert.Equal(fixture.Wizard.GameObjectID, effect.GameObjectID);
        Assert.Equal(fixture.ExpiredEffect.m_effectNameID, effect.EffectNameID);
        Assert.Equal(fixture.ExpiredEffect.m_internalID, effect.InternalID);
        var equipment = Assert.IsType<GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_UNEQUIPITEM>(messages[3]);
        Assert.Equal(fixture.Wizard.GameObjectID, equipment.GlobalID);
        Assert.Equal(9001ul, equipment.ItemID);
        Assert.DoesNotContain(messages, message => message is GAME_5_PROTOCOL.MSG_EQUIPITEM
            or GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM);

        Assert.Equal(1, fixture.TemplateReadsUnderLock);
        fixture.AssertOnlyConsumedEffectRemoved();
        Assert.Empty(fixture.Wizard.InventoryBehavior.InventoryItemIds);
        Assert.Empty(fixture.Wizard.EquipmentBehavior.EquippedItemIds);
        Assert.Empty(fixture.Store.SavedWizard.InventoryBehavior.InventoryItemIds);
        Assert.Equal(0u, fixture.Store.SavedItem.m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_expireTime);
        Assert.True(fixture.Store.Documents.ContainsKey("item/9001"));
    }

    [Fact]
    public void FailedExpiryCheckpointCannotRemoveEffectsOrPublishClientCleanup() {
        using var fixture = new ExpiryFixture();
        fixture.Store.FailSave = true;
        var result = fixture.Expire();
        Assert.False(result.Saved);
        Assert.Empty(ElixirService.ExpireCommitted(fixture.Wizard, result, true, fixture.TemplateFor));
        Assert.Equal(0, fixture.TemplateReadsUnderLock);
        Assert.Equal(12f, fixture.Wizard.GameStats.m_dmgBonusPercentAll);
        Assert.Contains(fixture.ExpiredEffect, fixture.Wizard.GameEffects.Snapshot());
        Assert.Equal(9001ul, Assert.Single(fixture.Wizard.EquipmentBehavior.EquippedItemIds));
        Assert.Equal(1800u, Assert.Single(fixture.Store.Ledger.Active).RemainingSeconds);
    }

    [Fact]
    public void FinalOfflineFlushRemovesExpiredStatsUnderLockWithoutSendingPackets() {
        using var fixture = new ExpiryFixture();
        var result = fixture.Expire();
        Assert.True(result.Saved);
        Assert.Empty(ElixirService.ExpireCommitted(fixture.Wizard, result, false, fixture.TemplateFor));
        Assert.Equal(1, fixture.TemplateReadsUnderLock);
        fixture.AssertOnlyConsumedEffectRemoved();
        Assert.Empty(fixture.Wizard.InventoryBehavior.InventoryItemIds);
    }

    [Fact]
    public async Task TheProductionElixirActorConstructsWithoutStartingAnUnauthenticatedClock() {
        EquipmentAttachConcurrencyTests.Configure();
        using var system = ActorSystem.Create("elixir-service-transport", "akka.actor.provider = local");
        var actor = system.ActorOf(Props.Create(() => new ElixirService(null!)), "elixirs");
        try {
            var identity = await actor.Ask<ActorIdentity>(new Identify("ready"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(actor, identity.Subject);
        }
        finally { await system.Terminate(); }
    }

    // Synthetic already-applied effects exercise real expiry/stat cleanup without approving
    // any production elixir or claiming historical numeric values.
    private sealed class ExpiryFixture : IDisposable {
        private readonly IDisposable _storeScope;
        private readonly FieldInfo _canonicalTable = typeof(CanonicalStatEffects).GetField("s_effectTable",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? _previousCanonicalTable;
        private readonly ElixirRuntimePublicationDependencies? _previousPublication;
        internal readonly ElixirTests.Store Store = new();
        internal readonly Wizard Wizard;
        internal readonly WizStatisticEffect ExpiredEffect;
        private readonly WizStatisticEffect _otherElixir;
        private readonly WizStatisticEffect _gear;
        internal int TemplateReadsUnderLock;

        internal ExpiryFixture() {
            _storeScope = Store.Scope();
            Wizard = Store.Login();
            Assert.True(ElixirCollection.Activate(Wizard, 9001, Store.Resolve, Store.Find).Saved);
            Wizard.GameStats = new ServerWizGameStats(default!, 1) { m_dmgBonusPercentAll = 12f };
            _previousCanonicalTable = _canonicalTable.GetValue(null);
            _canonicalTable.SetValue(null, new GameEffectTemplateList {
                m_effectTemplates = [new WizStatisticEffectTemplate { m_effectName = "DamageAll" }],
            });
            ExpiredEffect = Effect(9001, "Elixir", 100, 3);
            _otherElixir = Effect(9002, "Elixir", 101, 4);
            _gear = Effect(9001, "Hat", 102, 5); // same origin ID cannot evade exact slot ownership.
            Wizard.GameEffects.Add(ExpiredEffect);
            Wizard.GameEffects.Add(_otherElixir);
            Wizard.GameEffects.Add(_gear);
            _previousPublication = ElixirService.TestRuntimeScope.Value;
            ElixirService.TestRuntimeScope.Value = new() { Template = TemplateFor };
        }

        internal ElixirResult Expire() => ElixirCollection.AdvanceOnline(Wizard, 1800, Store.Resolve);
        internal WizItemTemplate TemplateFor(uint id) {
            Assert.True(WizardCollection.HoldsWriteLane);
            Assert.Equal(123u, id);
            TemplateReadsUnderLock++;
            return new WizItemTemplate { m_equipEffects = [new StatisticEffectInfo { m_effectName = "DamageAll" }] };
        }
        internal void AssertOnlyConsumedEffectRemoved() {
            var effects = Wizard.GameEffects.Snapshot();
            Assert.DoesNotContain(ExpiredEffect, effects);
            Assert.Contains(_otherElixir, effects);
            Assert.Contains(_gear, effects);
            Assert.Equal(2, effects.Count);
            Assert.Equal(9f, Wizard.GameStats.m_dmgBonusPercentAll);
        }
        private static WizStatisticEffect Effect(ulong originator, string slot, int id, float bonus) => new() {
            m_originatorID = originator, m_itemSlotID = StringHash.Compute(slot), m_internalID = id,
            m_effectNameID = StringHash.Compute("DamageAll"), m_damageBonusPercent = bonus,
        };
        public void Dispose() {
            _canonicalTable.SetValue(null, _previousCanonicalTable);
            ElixirService.TestRuntimeScope.Value = _previousPublication;
            _storeScope.Dispose();
        }
    }
}
