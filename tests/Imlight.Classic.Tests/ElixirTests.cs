using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic.Elixirs;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ElixirTests {
    // Deliberately synthetic protocol fixtures, not authorization for any historical product/effect.
    private static ElixirDefinition Definition(uint id, params string[] families)
        => new(id, 1800, families, true, false, "synthetic protocol fixture; no historical effect approval");

    [Fact]
    public void ProductionApprovalUsesOnlyTheTenDatedProductsInTheOctoberProfile() {
        var october = ClassicDataFixture.RealRules("october-2010-arc1");
        for (uint id = 191099; id <= 191108; id++) {
            var definition = ElixirRules.Approved(october, id);
            Assert.NotNull(definition);
            Assert.Equal(id % 2 == 1 ? 1800u : 3600u, definition.DurationSeconds);
            Assert.True(definition.CombatEnabled);
            Assert.False(definition.PvpEnabled);
            foreach (var profile in new[] { "late-2009", "arc1-2009h1", "dev-unrestricted" })
                Assert.Null(ElixirRules.Approved(ClassicDataFixture.RealRules(profile), id));
        }
        Assert.Null(ElixirRules.Approved(october, 191109));
        Assert.False(ElixirRules.EffectsEnabled(null!, false, false));
    }

    [Theory]
    [InlineData(191117u, "Fire", .15f, .10f)]
    [InlineData(191118u, "Ice", .10f, .20f)]
    [InlineData(191119u, "Storm", .15f, .10f)]
    [InlineData(191120u, "Myth", .10f, .15f)]
    [InlineData(191121u, "Life", .05f, .20f)]
    [InlineData(191122u, "Death", .10f, .15f)]
    public void SchoolBattleElixirsAreOctoberOnlyAndMatchTheirNativeTemplates(uint id, string school, float accuracy, float damage) {
        using var canonical = new CanonicalFixture();
        var october = ClassicDataFixture.RealRules("october-2010-arc1");
        var definition = ElixirRules.Approved(october, id);
        Assert.NotNull(definition);
        Assert.Equal(1800u, definition.DurationSeconds); Assert.Equal(300, definition.Crowns);
        Assert.Equal(["Battle" + school], definition.Families);
        Assert.Equal([accuracy, damage], definition.Effects!.Select(e => e.Value));
        foreach (var profile in new[] { "late-2009", "arc1-2009h1", "dev-unrestricted" })
            Assert.Null(ElixirRules.Approved(ClassicDataFixture.RealRules(profile), id));
        var template = new WizItemTemplate {
            m_templateID = id, m_behaviors = [new ElixirBehaviorTemplate {
                m_timerType = TimerType.TimerType_Game, m_expireTime = "1800", m_combatEnabled = true, m_PvPEnabled = false,
                m_typeList = ["Battle" + school] }],
            m_equipEffects = [.. definition.Effects!.Select(e => (GameEffectInfo) new StatisticEffectInfo { m_effectName = e.Name, m_lookupIndex = e.LookupIndex })],
        };
        Assert.True(ElixirRules.MatchesNative(definition, template));
    }

    [Fact]
    public void TheUnsettledBattleGoldXpAndRegenerationElixirsStayClosed() {
        var october = ClassicDataFixture.RealRules("october-2010-arc1");
        foreach (var id in new uint[] { 191109, 191110, 191111, 191112, 191113, 191114, 191115, 191116, 191123 })
            Assert.Null(ElixirRules.Approved(october, id));
    }

    [Fact]
    public void NativeCanonicalValuesUseFractionsForDamageAccuracyAndPowerAndFlatHealthMana() {
        using var canonical = new CanonicalFixture();
        foreach (var id in Enumerable.Range(191099, 10).Select(i => (uint)i)) {
            var definition = canonical.Definition(id);
            var template = canonical.Template(id);
            Assert.True(ElixirRules.MatchesNative(definition, template));
            var info = Assert.IsType<StatisticEffectInfo>(Assert.Single(template.m_equipEffects));
            var effect = Assert.IsType<WizStatisticEffect>(GameEffectFactory.CreateEffectFromInfo(info, 0));
            var expected = id switch { <= 191100 => .20f, <= 191104 => .15f, _ => 500f };
            var actual = id switch { <= 191100 => effect.m_powerPipBonusPercent,
                <= 191102 => effect.m_accuracyBonusPercent, <= 191104 => effect.m_damageBonusPercent,
                <= 191106 => effect.m_hitPointBonus, _ => effect.m_manaBonus };
            Assert.Equal(expected, actual);
            template.m_equipEffects[0] = info with { m_lookupIndex = info.m_lookupIndex - 1 };
            Assert.False(ElixirRules.MatchesNative(definition, template));
        }
    }

    [Fact]
    public void ApprovedNativeEffectsApplyOnceSuppressInPvpRemoveExactlyAndKeepOnlineTimer() {
        using var canonical = new CanonicalFixture(); using var runtime = canonical.OctoberRuntime();
        new Store(); // existing runtime fixture initializes the resource-independent configuration.
        foreach (var id in Enumerable.Range(191099, 10).Select(i => (uint)i)) {
            var wizard = new Wizard { CharId = 42, GameStats = new ServerWizGameStats(default!, 1),
                EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = new(), SlotList = [] } };
            var item = canonical.Item(9100, id); item.m_characterId = 42;
            var definition = canonical.Definition(id);
            item.m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_expireTime = definition.DurationSeconds;
            Assert.True(wizard.EquipmentBehavior.AppendElixirItem(item));
            ElixirRuntime.PublishValidated(wizard, new ElixirLedger { OwnerId = 42, Active = [new ElixirEntry {
                ItemId = 9100, ItemDocumentId = "fixture/9100", TemplateId = id,
                Families = [.. definition.Families], RemainingSeconds = definition.DurationSeconds,
            }] });
            var template = canonical.Template(id);
            var baseline = Value(wizard, id);
            Assert.Single(ElixirRuntime.AddApprovedEffects(wizard, item, template, false, false));
            Assert.Equal(baseline + Assert.Single(definition.Effects!).Value, Value(wizard, id), 5);
            Assert.Empty(ElixirRuntime.AddApprovedEffects(wizard, item, template, false, false));
            Assert.False(ElixirRuntime.CanApplyEffects(wizard, item, template, true, true));
            Assert.Single(ElixirRuntime.RemoveItemEffects(wizard, item.m_globalID, template));
            Assert.Equal(baseline, Value(wizard, id), 5);
            Assert.Equal(definition.DurationSeconds, Assert.Single(ElixirRuntime.RemainingTimers(wizard)).RemainingSeconds);
        }
        static float Value(Wizard wizard, uint id) => id switch {
            <= 191100 => wizard.GameStats.m_powerPipBonusPercentAll,
            <= 191102 => wizard.GameStats.m_accBonusPercentAll,
            <= 191104 => wizard.GameStats.m_dmgBonusPercentAll,
            <= 191106 => wizard.GameStats.m_baseHitpoints,
            _ => wizard.GameStats.m_baseMana,
        };
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonfiniteNativeCanonicalValuesCannotGrantEffectsEvenWithValidatedActiveOwnership(float nativeValue) {
        using var canonical = new CanonicalFixture(); using var runtime = canonical.OctoberRuntime();
        new Store();
        var wizard = new Wizard { CharId = 42, GameStats = new ServerWizGameStats(default!, 1),
            EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = new(), SlotList = [] } };
        var item = canonical.Item(9100, 191103); item.m_characterId = 42;
        item.m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_expireTime = 1800;
        Assert.True(wizard.EquipmentBehavior.AppendElixirItem(item));
        ElixirRuntime.PublishValidated(wizard, new ElixirLedger { OwnerId = 42, Active = [new ElixirEntry {
            ItemId = 9100, ItemDocumentId = "fixture/9100", TemplateId = 191103, Families = ["Damage"], RemainingSeconds = 1800,
        }] });
        Assert.True(ElixirRuntime.HasValidatedEntry(wizard, item));
        var template = canonical.Template(191103);
        canonical.SetCanonicalValue("Damage_AllSchools", 114, nativeValue);
        var baseline = wizard.GameStats.m_dmgBonusPercentAll;
        Assert.False(ElixirRules.MatchesNative(canonical.Definition(191103), template));
        Assert.False(ElixirRuntime.CanApplyEffects(wizard, item, template, false, false));
        Assert.Empty(ElixirRuntime.AddApprovedEffects(wizard, item, template, false, false));
        Assert.Empty(wizard.GameEffects.Snapshot());
        Assert.Equal(baseline, wizard.GameStats.m_dmgBonusPercentAll);
        Assert.False(item.m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_statsApplied);
    }

    [Fact]
    public void ImmediatePurchaseCommitsOneDebitOriginalConsumedItemLedgerAndEquipmentAndRejectsReplay() {
        using var canonical = new CanonicalFixture();
        var store = new Store(); using var scope = store.Scope(); var live = store.Login();
        var fresh = canonical.Item(9100, 191103);
        var result = Buy(live, fresh, store, canonical);
        Assert.True(result.Saved);
        Assert.Equal(1, store.Saves);
        Assert.Equal(9775, store.SavedAccount.Crowns);
        Assert.Equal(9775, live.Account.Crowns);
        Assert.Equal(9100ul, Assert.Single(store.Ledger.Active).ItemId);
        Assert.Equal(9100ul, Assert.Single(live.EquipmentBehavior.EquippedItemIds));
        Assert.Equal(new ulong[] { 9001 }, store.SavedWizard.InventoryBehavior.InventoryItemIds);
        Assert.Equal(1800u, ((WizClientObjectItem)store.Documents["ClassicElixirItems/42/9100"])
            .m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_expireTime);
        Assert.False(Buy(live, canonical.Item(9100, 191103), store, canonical).Saved);
        Assert.False(Buy(live, canonical.Item(9101, 191104), store, canonical).Saved); // same family, even Major.
        Assert.Equal(1, store.Saves);
        Assert.Equal(9775, live.Account.Crowns);
    }

    [Theory]
    [InlineData("save-later")]
    [InlineData("unknown-duel")]
    [InlineData("pvp")]
    [InlineData("foreign-item")]
    [InlineData("insufficient")]
    [InlineData("serialize")]
    [InlineData("save")]
    public void RefusedOrFailedImmediatePurchaseCannotChargeConsumeOrPublishAnything(string failure) {
        using var canonical = new CanonicalFixture();
        var store = new Store(); using var scope = store.Scope(); var live = store.Login();
        var fresh = canonical.Item(9100, 191103);
        if (failure == "unknown-duel") live.IsInDuel = true;
        if (failure == "pvp") ElixirRules.SetCombatContext(live, true, true);
        if (failure == "foreign-item") fresh.m_characterId = 99;
        if (failure == "insufficient") store.SavedAccount.Crowns = 224;
        if (failure == "save") store.FailSave = true;
        var initialBalance = store.SavedAccount.Crowns;
        var initialEffects = live.GameEffects.Snapshot().Count;
        var result = ElixirCollection.Purchase(live, fresh, canonical.Template(191103), failure != "save-later",
            canonical.Definition, store.LoadAccount, _ => failure == "serialize" ? new ByteString() : new ByteString(new byte[] { 1 }));
        Assert.False(result.Saved);
        Assert.Equal(0, store.Saves);
        Assert.Equal(initialBalance, store.SavedAccount.Crowns);
        Assert.Equal(10000, live.Account.Crowns);
        Assert.Empty(live.EquipmentBehavior.EquippedItemIds);
        Assert.False(store.Documents.ContainsKey("ClassicElixirItems/42/9100"));
        Assert.False(store.Documents.ContainsKey(ElixirLedger.DocumentId(42)));
        Assert.Equal(initialEffects, live.GameEffects.Snapshot().Count);
    }

    [Fact]
    public void ThreeActivePurchasesAndOwnedActivationUseTheSameTrustedCombatContext() {
        using var canonical = new CanonicalFixture();
        var store = new Store(); using var scope = store.Scope(); var live = store.Login();
        foreach (var pair in new[] { (9100ul, 191103u), (9101ul, 191101u), (9102ul, 191099u) })
            Assert.True(Buy(live, canonical.Item(pair.Item1, pair.Item2), store, canonical).Saved);
        var balance = live.Account.Crowns;
        Assert.False(Buy(live, canonical.Item(9103, 191105), store, canonical).Saved);
        Assert.Equal(3, store.Saves);
        Assert.Equal(balance, live.Account.Crowns);

        var ownedStore = new Store(); using var ownedScope = ownedStore.Scope(); var owned = ownedStore.Login();
        owned.IsInDuel = true;
        Assert.False(Activate(owned, ownedStore).Saved);
        ElixirRules.SetCombatContext(owned, true, true);
        Assert.False(Activate(owned, ownedStore).Saved);
        ElixirRules.SetCombatContext(owned, true, false);
        Assert.True(Activate(owned, ownedStore).Saved);
    }

    private static ElixirPurchaseResult Buy(Wizard live, WizClientObjectItem item, Store store, CanonicalFixture canonical)
        => ElixirCollection.Purchase(live, item, canonical.Template((uint)item.m_templateID.Full), true,
            canonical.Definition, store.LoadAccount, _ => new ByteString(new byte[] { 1 }));

    [Fact]
    public void ThreeDistinctFamiliesFitAndACompositeFamilyCannotEvadeOverlap() {
        var ledger = new ElixirLedger { OwnerId = 42 };
        Assert.True(ledger.TryActivate(Entry(1, "Damage")));
        Assert.True(ledger.TryActivate(Entry(2, "Accuracy")));
        Assert.False(ledger.TryActivate(Entry(3, "Damage", "Accuracy")));
        Assert.True(ledger.TryActivate(Entry(4, "PowerPip")));
        Assert.False(ledger.TryActivate(Entry(5, "MaxHealth")));
        Assert.Equal(new ulong[] { 1, 2, 4 }, ledger.Active.Select(e => e.ItemId));
    }

    [Fact]
    public void MatchingFamilyCannotReplaceOrRenewTheOriginalActiveElixir() {
        var ledger = new ElixirLedger { OwnerId = 42 };
        Assert.True(ledger.TryActivate(Entry(1, "Damage")));
        Assert.Empty(ledger.AdvanceOnline(600));
        var version = ledger.Version;
        Assert.False(ledger.TryActivate(Entry(2, "Damage")));
        Assert.False(ledger.TryActivate(Entry(1, "Damage")));
        Assert.Equal(version, ledger.Version);
        var original = Assert.Single(ledger.Active);
        Assert.Equal(1ul, original.ItemId);
        Assert.Equal("item/1", original.ItemDocumentId);
        Assert.Equal(1200u, original.RemainingSeconds);
    }

    [Fact]
    public void OnlineTimeAdvancesDuringPvpWhileBenefitIsDisabledAndOfflineLoadChargesNothing() {
        var definition = Definition(123, "Damage");
        Assert.True(ElixirRules.EffectsEnabled(definition, true, false));
        Assert.False(ElixirRules.EffectsEnabled(definition, true, true));
        var ledger = new ElixirLedger { OwnerId = 42 };
        Assert.True(ledger.TryActivate(Entry(1, "Damage")));
        var reloaded = ledger.Copy();
        Assert.Equal(1800u, Assert.Single(reloaded.Active).RemainingSeconds);
        Assert.Empty(reloaded.AdvanceOnline(60));
        Assert.Equal(1740u, Assert.Single(reloaded.Active).RemainingSeconds);
        Assert.Single(reloaded.AdvanceOnline(uint.MaxValue));
        Assert.Empty(reloaded.Active); // saturated subtraction cannot underflow and renew a consumed item.
    }

    [Fact]
    public void CalendarMountExpirationIsPreservedAndGameSecondsAreNeverUnixDates() {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var item = new WizClientObjectItem {
            m_templateID = 123, m_inactiveBehaviors = [new ClientTimedItemBehavior { m_expireTime = 1800 }],
        };
        WizItemTemplate Template(TimerType type) => new() {
            m_behaviors = [new TimedItemBehaviorTemplate { m_timerType = type }],
        };
        Assert.False(WizardItemCollection.IsExpired(item, now, _ => Template(TimerType.TimerType_Game)));
        Assert.True(WizardItemCollection.IsExpired(item, now, _ => Template(TimerType.TimerType_Calendar)));
        ((ClientTimedItemBehavior)item.m_inactiveBehaviors[0]).m_expireTime = 1_800_000_001;
        Assert.False(WizardItemCollection.IsExpired(item, now, _ => Template(TimerType.TimerType_Calendar)));
        Assert.False(WizardItemCollection.IsExpired(item, now, _ => null!));
        Assert.False(ElixirRuntime.CalendarExpired(1800, (TimerType)99, now));
    }

    [Fact]
    public void ActivationAndReplayUseTheSavedOwnedItemAndOneDurableCommit() {
        var store = new Store();
        using var scope = store.Scope();
        var live = store.Login();
        var result = Activate(live, store);
        Assert.True(result.Saved);
        Assert.True(result.HasActive);
        Assert.Equal(1, store.Saves);
        Assert.Empty(live.InventoryBehavior.InventoryItemIds);
        Assert.Equal(9001ul, Assert.Single(live.EquipmentBehavior.EquippedItemIds));
        Assert.Equal(EquipmentSlotType.Elixir, Assert.Single(live.EquipmentBehavior.SlotList).SlotType);
        Assert.Equal(1800u, Assert.Single(store.Ledger.Active).RemainingSeconds);
        Assert.Equal(1800u, store.SavedItem.m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_expireTime);
        Assert.False(Activate(live, store).Saved);
        Assert.Equal(1, store.Saves);
        Assert.Equal(9001ul, Assert.Single(store.SavedWizard.EquipmentBehavior.EquippedItemIds));
    }

    [Fact]
    public void StaleBackpackForeignDocumentAndMissingApprovalCannotConsumeAnything() {
        var store = new Store();
        using var scope = store.Scope();
        var live = store.Login();
        Assert.False(ElixirCollection.Activate(live, 9001).Saved);
        store.SavedWizard.InventoryBehavior.InventoryItemIds = [];
        Assert.False(Activate(live, store).Saved);
        store.SavedWizard.InventoryBehavior.InventoryItemIds = [9001];
        store.SavedItem.m_characterId = 99;
        Assert.False(Activate(live, store).Saved);
        Assert.Equal(0, store.Saves);
        Assert.Equal(9001ul, Assert.Single(live.InventoryBehavior.InventoryItemIds));
    }

    [Fact]
    public void FailedCommitLeavesAllMembershipAndTimerStateUnchanged() {
        var store = new Store { FailSave = true };
        using var scope = store.Scope();
        var live = store.Login();
        Assert.False(Activate(live, store).Saved);
        Assert.Equal(0, store.Saves);
        Assert.Equal(9001ul, Assert.Single(live.InventoryBehavior.InventoryItemIds));
        Assert.Empty(live.EquipmentBehavior.EquippedItemIds);
        Assert.Equal(9001ul, Assert.Single(store.SavedWizard.InventoryBehavior.InventoryItemIds));
        Assert.False(store.Documents.ContainsKey(ElixirLedger.DocumentId(42)));
        Assert.Equal(0u, store.SavedItem.m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_expireTime);
    }

    [Fact]
    public void ExpirationCommitsOriginalItemTimerAndRemovesOnlyItsOwnSlot() {
        var store = new Store();
        using var scope = store.Scope();
        var live = store.Login();
        Assert.True(Activate(live, store).Saved);
        Assert.True(ElixirCollection.AdvanceOnline(live, 1799, store.Resolve).Saved);
        Assert.Equal(1u, store.SavedItem.m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_expireTime);
        var expired = ElixirCollection.AdvanceOnline(live, 1, store.Resolve);
        Assert.True(expired.Saved);
        Assert.False(expired.HasActive);
        Assert.Equal(9001ul, Assert.Single(expired.Removed!).ItemId);
        Assert.Empty(live.EquipmentBehavior.EquippedItemIds);
        Assert.Empty(store.SavedWizard.EquipmentBehavior.SlotList);
        Assert.Empty(store.SavedWizard.InventoryBehavior.InventoryItemIds);
        Assert.Equal(0u, store.SavedItem.m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_expireTime);
        Assert.True(store.Documents.ContainsKey("item/9001")); // retained original document, never recreated/refunded.
    }

    [Fact]
    public void PerItemElapsedDoesNotChargeNewlyActivatedItemsAndRejectsForeignIds() {
        var store = new Store();
        using var scope = store.Scope();
        var live = store.Login();
        Assert.True(Activate(live, store).Saved);
        store.Documents["item/9002"] = new WizClientObjectItem {
            m_globalID = 9002, m_characterId = 42, m_templateID = 124, m_inactiveBehaviors = [new ClientElixirBehavior()],
        };
        store.SavedWizard.InventoryBehavior.InventoryItemIds = [9002];
        Assert.True(live.InventoryBehavior.AddItem((WizClientObjectItem)Store.Clone(store.Documents["item/9002"])));
        Assert.True(ElixirCollection.Activate(live, 9002, store.Resolve, store.Find).Saved);
        var version = store.Ledger.Version;
        Assert.False(ElixirCollection.AdvanceOnline(live, new Dictionary<ulong, uint> { [9900] = 1 }, store.Resolve).Saved);
        Assert.False(ElixirCollection.AdvanceOnline(live, new Dictionary<ulong, uint> { [9900] = 0 }, store.Resolve).NoWork);
        Assert.True(ElixirCollection.AdvanceOnline(live, new Dictionary<ulong, uint> { [9001] = 0 }, store.Resolve).NoWork);
        Assert.Equal(version, store.Ledger.Version);
        Assert.True(ElixirCollection.AdvanceOnline(live, new Dictionary<ulong, uint> { [9001] = 60 }, store.Resolve).Saved);
        Assert.Equal(1740u, store.Ledger.Active.Single(e => e.ItemId == 9001).RemainingSeconds);
        Assert.Equal(1800u, store.Ledger.Active.Single(e => e.ItemId == 9002).RemainingSeconds);
        Assert.Equal(version + 1, store.Ledger.Version);
    }

    [Fact]
    public void FailedCheckpointRetainsSecondsAndRequiresFreshValidatedReloadBeforeChargingExactlyOnce() {
        var store = new Store();
        using var scope = store.Scope();
        var live = store.Login();
        Assert.True(Activate(live, store).Saved);
        store.FailSave = true;
        Assert.False(ElixirCollection.AdvanceOnline(live, 90, store.Resolve).Saved);
        Assert.Equal(1800u, Assert.Single(store.Ledger.Active).RemainingSeconds);
        Assert.Equal(1800u, Assert.Single(ElixirRuntime.RemainingTimers(live, store.Resolve)).RemainingSeconds);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        store.FailSave = false;
        var saves = store.Saves;
        Assert.False(ElixirCollection.AdvanceOnline(live, 90, store.Resolve).Saved);
        Assert.Equal(saves, store.Saves); Assert.Equal(1800u, Assert.Single(store.Ledger.Active).RemainingSeconds);
        var reloaded = store.Login(); Assert.True(ElixirCollection.LoadValidated(reloaded, store.Resolve));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(reloaded));
        Assert.True(ElixirCollection.AdvanceOnline(reloaded, 90, store.Resolve).Saved);
        Assert.Equal(1710u, Assert.Single(store.Ledger.Active).RemainingSeconds);
        Assert.Equal(1710u, Assert.Single(ElixirRuntime.RemainingTimers(reloaded, store.Resolve)).RemainingSeconds);
        saves = store.Saves; Assert.False(ElixirCollection.AdvanceOnline(live, 90, store.Resolve).Saved);
        Assert.Equal(saves, store.Saves); Assert.Equal(1710u, Assert.Single(store.Ledger.Active).RemainingSeconds);
    }

    [Fact]
    public void EquippedApprovedItemWithoutValidatedLedgerCannotStartTimerOrApplyEffects() {
        var store = new Store();
        using var scope = store.Scope();
        var live = store.Login();
        var orphan = (WizClientObjectItem)Store.Clone(store.SavedItem);
        orphan.m_inactiveBehaviors.OfType<ClientElixirBehavior>().Single().m_expireTime = 1800;
        Assert.True(live.EquipmentBehavior.AppendElixirItem(orphan));
        Assert.Empty(ElixirRuntime.RemainingTimers(live, store.Resolve));
        Assert.False(ElixirRuntime.HasValidatedEntry(live, orphan));
        Assert.False(ElixirRuntime.CanApplyEffects(live, orphan, new WizItemTemplate {
            m_behaviors = [new ElixirBehaviorTemplate { m_typeList = ["Damage"] }],
        }));
        Assert.False(ElixirCollection.LoadValidated(live, store.Resolve));
        Assert.Empty(ElixirRuntime.RemainingTimers(live, store.Resolve));

        live = store.Login();
        Assert.True(Activate(live, store).Saved);
        live = store.Login(); // separate attached wizard, never inherits another session's cache.
        Assert.Empty(ElixirRuntime.RemainingTimers(live, store.Resolve));
        Assert.True(ElixirCollection.LoadValidated(live, store.Resolve));
        Assert.Single(ElixirRuntime.RemainingTimers(live, store.Resolve));
    }

    [Fact]
    public void RemovingOneElixirPreservesTheOtherTwoAndEffectIdsNeverReuseRemovedIds() {
        EquipmentAttachConcurrencyTests.Configure();
        var wizard = new Wizard { CharId = 42, EquipmentBehavior = new() {
            EquippedItemIds = [], EquippedItems = new(), SlotList = [],
        } };
        foreach (var id in new ulong[] { 1, 2, 3 })
            Assert.True(wizard.EquipmentBehavior.AppendElixirItem(new WizClientObjectItem { m_globalID = id }));
        Assert.True(wizard.EquipmentBehavior.RemoveElixirItem(2));
        Assert.Equal(new ulong[] { 1, 3 }, wizard.EquipmentBehavior.SlotList.Select(s => (ulong)s.ItemId));
        wizard.GameEffects.Add(new WizStatisticEffect { m_internalID = 100 });
        Assert.Equal(101, ElixirRuntime.NextEffectId(wizard));
        wizard.GameEffects.Clear();
        Assert.Equal(102, ElixirRuntime.NextEffectId(wizard));
    }

    private static ElixirEntry Entry(ulong id, params string[] families) => new() {
        ItemId = id, ItemDocumentId = $"item/{id}", TemplateId = 123, RemainingSeconds = 1800, Families = [.. families],
    };
    private static ElixirResult Activate(Wizard live, Store store)
        => ElixirCollection.Activate(live, 9001, store.Resolve, store.Find);

    internal sealed class Store {
        internal Dictionary<string, object> Documents = new();
        internal bool FailSave;
        internal int Saves;
        internal Wizard SavedWizard => (Wizard)Documents["wizard/42"];
        internal WizClientObjectItem SavedItem => (WizClientObjectItem)Documents["item/9001"];
        internal Account SavedAccount => (Account)Documents["account/7"];
        internal ElixirLedger Ledger => (ElixirLedger)Documents[ElixirLedger.DocumentId(42)];
        internal Store() {
            EquipmentAttachConcurrencyTests.Configure();
            Documents["wizard/42"] = new Wizard {
                CharId = 42, AccountId = 7, InventoryBehavior = new() { InventoryItemIds = [9001], Items = new() },
                EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = new(), SlotList = [] },
                StorageBehavior = new() { BankItemIds = [], Items = new() },
            };
            Documents["item/9001"] = new WizClientObjectItem {
                m_globalID = 9001, m_characterId = 42, m_templateID = 123,
                m_inactiveBehaviors = [new ClientElixirBehavior()],
            };
            var account = new Account { Crowns = 10000 };
            typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(account, 7ul);
            account.CharacterIds.Add(42); Documents["account/7"] = account;
        }
        internal ElixirDefinition Resolve(uint id) => Definition(id, id == 124 ? "MaxHealth" : "Damage");
        internal WizClientObjectItem Find(IDocumentSession session, ulong owner, ulong item) => session.Load<WizClientObjectItem>($"item/{item}");
        internal Account LoadAccount(IDocumentSession session, ulong id) { Assert.Equal(7ul, id); return session.Load<Account>("account/7"); }
        internal Wizard Login() {
            var wizard = CloneWizard(SavedWizard);
            wizard.Account = CloneAccount(SavedAccount);
            foreach (var id in wizard.InventoryBehavior.InventoryItemIds) wizard.InventoryBehavior.Items.Add(CloneItem(Documents.Values.OfType<WizClientObjectItem>().Single(i => i.m_globalID == id)));
            foreach (var id in wizard.EquipmentBehavior.EquippedItemIds) wizard.EquipmentBehavior.EquippedItems.Add(CloneItem(Documents.Values.OfType<WizClientObjectItem>().Single(i => i.m_globalID == id)));
            return wizard;
        }
        internal IDisposable Scope() {
            var previous = WizardCollection.TestStoreScope.Value;
            var previousRows = WizardInventoryTransactions.TestRowsScope.Value;
            var previousPublication = ElixirService.TestRuntimeScope.Value;
            if (previousPublication is null) {
                var templates = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory)
                    .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                // CLASSIC: cached authored templates only; unsupported synthetic IDs stay disabled without resource reads.
                ElixirService.TestRuntimeScope.Value = new() {
                    Template = id => templates.TryGetValue(id, out var template) ? template as WizItemTemplate : null!,
                    SerializeEffect = _ => new ByteString(new byte[] { 1 }),
                };
            }
            WizardCollection.TestStoreScope.Value = new(Open, (session, _) => session.Load<Wizard>("wizard/42"));
            // CLASSIC: ordinary trash now validates the fresh backpack against tracked original rows. Keep this
            // in the shared scope because the native service probe opens its own scope on its actor thread.
            WizardInventoryTransactions.TestRowsScope.Value = session => Documents
                .Where(pair => pair.Value is WizClientObjectItem)
                .Select(pair => session.Load<WizClientObjectItem>(pair.Key)).ToList();
            return new Restore(() => {
                WizardCollection.TestStoreScope.Value = previous;
                WizardInventoryTransactions.TestRowsScope.Value = previousRows;
                ElixirService.TestRuntimeScope.Value = previousPublication;
            });
        }
        private IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, SessionProxy>();
            ((SessionProxy)(object)session).Owner = this;
            return session;
        }
        internal static object Clone(object value) => value switch {
            ElixirLedger ledger => ledger.Copy(), WizClientObjectItem item => CloneItem(item), Wizard wizard => CloneWizard(wizard),
            Account account => CloneAccount(account),
            _ => throw new NotSupportedException(value.GetType().Name),
        };
        private static WizClientObjectItem CloneItem(WizClientObjectItem item) => item with {
            m_inactiveBehaviors = item.m_inactiveBehaviors.Select(b => b is ClientElixirBehavior elixir
                ? (BehaviorInstance)(elixir with { }) : b).ToList(),
        };
        private static Wizard CloneWizard(Wizard wizard) => new() {
            CharId = wizard.CharId, AccountId = wizard.AccountId, IsInDuel = wizard.IsInDuel,
            InventoryBehavior = new() { InventoryItemIds = [.. wizard.InventoryBehavior.InventoryItemIds], Items = new() },
            EquipmentBehavior = new() {
                EquippedItemIds = [.. wizard.EquipmentBehavior.EquippedItemIds], EquippedItems = new(),
                SlotList = wizard.EquipmentBehavior.SlotList.Select(s => new EquipmentSlot {
                    SlotType = s.SlotType, ItemId = s.ItemId, ItemName = s.ItemName, EquippedSince = s.EquippedSince,
                }).ToList(),
            },
            StorageBehavior = new() { BankItemIds = [.. wizard.StorageBehavior.BankItemIds], Items = new() },
        };
        private static Account CloneAccount(Account account) {
            var copy = new Account { Crowns = account.Crowns };
            typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(copy, account.AccountId);
            copy.CharacterIds.AddRange(account.CharacterIds); return copy;
        }
    }

    public class SessionProxy : DispatchProxy {
        private Store _owner = null!;
        private readonly Dictionary<string, object> _working = new();
        private IAdvancedSessionOperations? _advanced;
        internal Store Owner { get => _owner; set => _owner = value; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            switch (method!.Name) {
                case "get_Advanced":
                    if (_advanced is null) {
                        _advanced = DispatchProxy.Create<IAdvancedSessionOperations, AdvancedProxy>();
                        ((AdvancedProxy)(object)_advanced).Session = this;
                    }
                    return _advanced;
                case "Load": {
                    var id = (string)args![0]!;
                    if (_working.TryGetValue(id, out var current)) return current;
                    return Owner.Documents.TryGetValue(id, out var persisted) ? _working[id] = Store.Clone(persisted) : null;
                }
                case "Store": _working[(string)args![1]!] = args[0]!; return null;
                case "SaveChanges":
                    if (Owner.FailSave) throw new IOException("Injected elixir save failure.");
                    foreach (var pair in _working) Owner.Documents[pair.Key] = Store.Clone(pair.Value);
                    Owner.Saves++;
                    return null;
                case "Dispose": return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
        internal string DocumentId(object value) => _working.Single(pair => ReferenceEquals(pair.Value, value)).Key;
    }
    public class AdvancedProxy : DispatchProxy {
        internal SessionProxy Session = null!;
        private readonly IMetadataDictionary _metadata = DispatchProxy.Create<IMetadataDictionary, MetadataProxy>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "set_OptimisticConcurrencyMode" or "IgnoreChangesFor" => null, "GetDocumentId" => Session.DocumentId(args![0]!),
            "GetMetadataFor" => _metadata,
            _ => throw new NotSupportedException(method.Name),
        };
    }
    public class MetadataProxy : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            if (method!.Name != "set_Item") throw new NotSupportedException(method.Name);
            Assert.Equal(Raven.Client.Constants.Documents.Metadata.Collection, args![0]);
            Assert.Equal(WizardItemCollection.CollectionName, args[1]); return null;
        }
    }

    // The decoded canonical bindings are authored fixtures, with only the approved index
    // populated. Saving/restoring global tables prevents a synthetic test approval leaking.
    internal sealed class CanonicalFixture : IDisposable {
        private readonly FieldInfo _effects = typeof(CanonicalStatEffects).GetField("s_effectTable", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly FieldInfo _tables = typeof(GameEffectRuleData).GetField("s_statTables", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? _previousEffects, _previousTables;
        private readonly ClassicRules _rules = ClassicDataFixture.RealRules("october-2010-arc1");
        internal CanonicalFixture() {
            _previousEffects = _effects.GetValue(null); _previousTables = _tables.GetValue(null);
            var effects = new List<GameEffectTemplate>(); var tables = new Dictionary<string, WizardStatTable>();
            foreach (var row in new[] {
                ("CanonicalPowerPip", "PowerPips", "PowerPips_AllSchools", 119, .20f),
                ("CanonicalAllAccuracy", "AllAccuracy", "Accuracy_AllSchools", 114, .15f),
                ("CanonicalAllDamage", "AllDamage", "Damage_AllSchools", 114, .15f),
                ("CanonicalMaxHealth", "MaxHealth", "MaxHealth_AllSchools", 499, 500f),
                ("CanonicalMaxMana", "MaxMana", "MaxMana_AllSchools", 402, 500f),
            }) {
                effects.Add(new WizStatisticEffectTemplate { m_effectName = row.Item1, m_effectCategory = row.Item2, m_statTableName = row.Item3 });
                var values = Enumerable.Repeat(0f, row.Item4 + 1).ToList(); values[row.Item4] = row.Item5;
                tables[row.Item3] = new WizardStatTable { m_statVector = values };
            }
            foreach (var school in new[] { "Fire", "Ice", "Storm", "Myth", "Life", "Death" }) {
                foreach (var family in new[] { "Accuracy", "Damage" }) {
                    var table = family + "_" + school;
                    effects.Add(new WizStatisticEffectTemplate { m_effectName = "Canonical" + school + family,
                        m_effectCategory = school + family, m_statTableName = table });
                    var values = Enumerable.Repeat(0f, 120).ToList();
                    values[104] = .05f; values[109] = .10f; values[114] = .15f; values[119] = .20f;
                    tables[table] = new WizardStatTable { m_statVector = values };
                }
            }
            _effects.SetValue(null, new GameEffectTemplateList { m_effectTemplates = effects });
            _tables.SetValue(null, tables);
        }
        internal ElixirDefinition Definition(uint id) => ElixirRules.Approved(_rules, id)!;
        internal void SetCanonicalValue(string table, int index, float value)
            => ((Dictionary<string, WizardStatTable>)_tables.GetValue(null)!)[table].m_statVector[index] = value;
        internal IDisposable OctoberRuntime() {
            var field = typeof(ClassicRuntime).GetField("s_rules", BindingFlags.Static | BindingFlags.NonPublic)!;
            var previous = field.GetValue(null); field.SetValue(null, _rules);
            return new Restore(() => field.SetValue(null, previous));
        }
        internal void AddMasteryFixtures() {
            var effects = (GameEffectTemplateList)_effects.GetValue(null)!;
            var tables = (Dictionary<string, WizardStatTable>)_tables.GetValue(null)!;
            // The flag path does not depend on a numerical table benefit.
            tables["MasteryFixture"] = new WizardStatTable { m_statVector = [0f] };
            foreach (var school in new[] { "Balance", "Death", "Fire", "Ice", "Life", "Myth", "Storm" })
                effects.m_effectTemplates.Add(new WizStatisticEffectTemplate {
                    m_effectName = "Canonical" + school + "Mastery", m_effectCategory = "Mastery", m_statTableName = "MasteryFixture",
                });
        }
        internal WizItemTemplate Template(uint id) {
            var definition = Definition(id); var effect = Assert.Single(definition.Effects!);
            return new WizItemTemplate {
                m_templateID = id, m_behaviors = [new ElixirBehaviorTemplate {
                    m_timerType = TimerType.TimerType_Game, m_expireTime = definition.DurationSeconds.ToString(),
                    m_combatEnabled = true, m_PvPEnabled = false, m_typeList = [.. definition.Families],
                }], m_equipEffects = [new StatisticEffectInfo { m_effectName = effect.Name, m_lookupIndex = effect.LookupIndex }],
            };
        }
        internal WizClientObjectItem Item(ulong id, uint template) => new() {
            m_globalID = id, m_templateID = template, m_inactiveBehaviors = [new ClientElixirBehavior()],
        };
        public void Dispose() { _effects.SetValue(null, _previousEffects); _tables.SetValue(null, _previousTables); }
    }
    private sealed class Restore(System.Action undo) : IDisposable { public void Dispose() => undo(); }
}
