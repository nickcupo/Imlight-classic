using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic.Elixirs;
using Imlight.CoreLib.Shared.Behaviors;
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
    public void ProductionApprovalIsClosedEvenForKnownNativeElixirIds() {
        foreach (var id in new uint[] { 191099, 191101, 191103, 191105, 191107 })
            Assert.Null(ElixirRules.Approved(id));
        Assert.False(ElixirRules.EffectsEnabled(null!, false, false));
    }

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
    public void FailedCheckpointRetainsSavedAndAttachedSecondsAndRetryChargesExactlyOnce() {
        var store = new Store();
        using var scope = store.Scope();
        var live = store.Login();
        Assert.True(Activate(live, store).Saved);
        store.FailSave = true;
        Assert.False(ElixirCollection.AdvanceOnline(live, 90, store.Resolve).Saved);
        Assert.Equal(1800u, Assert.Single(store.Ledger.Active).RemainingSeconds);
        Assert.Equal(1800u, Assert.Single(ElixirRuntime.RemainingTimers(live, store.Resolve)).RemainingSeconds);
        store.FailSave = false;
        Assert.True(ElixirCollection.AdvanceOnline(live, 90, store.Resolve).Saved);
        Assert.Equal(1710u, Assert.Single(store.Ledger.Active).RemainingSeconds);
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
        internal ElixirLedger Ledger => (ElixirLedger)Documents[ElixirLedger.DocumentId(42)];
        internal Store() {
            EquipmentAttachConcurrencyTests.Configure();
            Documents["wizard/42"] = new Wizard {
                CharId = 42, InventoryBehavior = new() { InventoryItemIds = [9001], Items = new() },
                EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = new(), SlotList = [] },
                StorageBehavior = new() { BankItemIds = [], Items = new() },
            };
            Documents["item/9001"] = new WizClientObjectItem {
                m_globalID = 9001, m_characterId = 42, m_templateID = 123,
                m_inactiveBehaviors = [new ClientElixirBehavior()],
            };
        }
        internal ElixirDefinition Resolve(uint id) => Definition(id, id == 124 ? "MaxHealth" : "Damage");
        internal WizClientObjectItem Find(IDocumentSession session, ulong owner, ulong item) => session.Load<WizClientObjectItem>($"item/{item}");
        internal Wizard Login() {
            var wizard = CloneWizard(SavedWizard);
            foreach (var id in wizard.InventoryBehavior.InventoryItemIds) wizard.InventoryBehavior.Items.Add(CloneItem((WizClientObjectItem)Documents[$"item/{id}"]));
            foreach (var id in wizard.EquipmentBehavior.EquippedItemIds) wizard.EquipmentBehavior.EquippedItems.Add(CloneItem((WizClientObjectItem)Documents[$"item/{id}"]));
            return wizard;
        }
        internal IDisposable Scope() {
            var previous = WizardCollection.TestStoreScope.Value;
            WizardCollection.TestStoreScope.Value = new(Open, (session, _) => session.Load<Wizard>("wizard/42"));
            return new Restore(() => WizardCollection.TestStoreScope.Value = previous);
        }
        private IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, SessionProxy>();
            ((SessionProxy)(object)session).Owner = this;
            return session;
        }
        internal static object Clone(object value) => value switch {
            ElixirLedger ledger => ledger.Copy(), WizClientObjectItem item => CloneItem(item), Wizard wizard => CloneWizard(wizard),
            _ => throw new NotSupportedException(value.GetType().Name),
        };
        private static WizClientObjectItem CloneItem(WizClientObjectItem item) => item with {
            m_inactiveBehaviors = item.m_inactiveBehaviors.Select(b => b is ClientElixirBehavior elixir
                ? (BehaviorInstance)(elixir with { }) : b).ToList(),
        };
        private static Wizard CloneWizard(Wizard wizard) => new() {
            CharId = wizard.CharId,
            InventoryBehavior = new() { InventoryItemIds = [.. wizard.InventoryBehavior.InventoryItemIds], Items = new() },
            EquipmentBehavior = new() {
                EquippedItemIds = [.. wizard.EquipmentBehavior.EquippedItemIds], EquippedItems = new(),
                SlotList = wizard.EquipmentBehavior.SlotList.Select(s => new EquipmentSlot {
                    SlotType = s.SlotType, ItemId = s.ItemId, ItemName = s.ItemName, EquippedSince = s.EquippedSince,
                }).ToList(),
            },
            StorageBehavior = new() { BankItemIds = [.. wizard.StorageBehavior.BankItemIds], Items = new() },
        };
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
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "set_OptimisticConcurrencyMode" => null, "GetDocumentId" => Session.DocumentId(args![0]!),
            _ => throw new NotSupportedException(method.Name),
        };
    }
    private sealed class Restore(System.Action undo) : IDisposable { public void Dispose() => undo(); }
}
