// CLASSIC: the production creation path commits the ledger, debit and output before publishing attached state.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Monstrology;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;
using Action = System.Action;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class MonstrologyCardSafetyTests {
    public MonstrologyCardSafetyTests() {
        // Retain the authored fixture; permanent deletion belongs to the owner.
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "[Logging]\nLogLevel=FATAL\nLogPath=/private/tmp/monstrology-card-safety-tests.log\n[Character]\nMaxInventoryItems=20\n");
        ConfigurationManager.Initialize(path);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1000)]
    public void FullOrOverfullSavedBookRefusesNewCardDespiteEmptyAttachedBook(int savedCount) {
        var f = new Store(); f.Saved.SpellbookBehavior.TreasureCardTemplateIds = Enumerable.Repeat(777u, savedCount).ToList();
        using var scope = f.Scope();
        Assert.Equal(MonstrologyResult.Rejected, f.Create());
        Assert.Equal(0, f.SaveAttempts); Assert.Equal(0, f.Publishes);
        Assert.Equal(savedCount, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Empty(f.Live.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(1000, f.Saved.GameStats.m_currentGold); Assert.Equal(1000, f.Live.GameStats.m_currentGold);
        Assert.Equal(5, f.Ledger.Animus[123]); Assert.Empty(f.Ledger.Creations);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Fact]
    public void SavedRemainingSlotAdmitsCreationDespiteStaleOverfullAttachedBook() {
        var f = new Store(); f.Saved.SpellbookBehavior.TreasureCardTemplateIds = Enumerable.Repeat(777u, 998).ToList();
        f.Live.SpellbookBehavior.TreasureCardTemplateIds = Enumerable.Repeat(777u, 1000).ToList();
        using var scope = f.Scope();
        f.BeforeSave = () => {
            Assert.Equal(1000, f.Live.GameStats.m_currentGold);
            Assert.Equal(1000, f.Live.SpellbookBehavior.TreasureCardTemplateIds.Count);
            Assert.Equal(0, f.Publishes);
        };
        Assert.Equal(MonstrologyResult.Applied, f.Create());
        Assert.Equal(1, f.SaveAttempts); Assert.Equal(1, f.Publishes);
        Assert.Equal(999, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(f.Saved.SpellbookBehavior.TreasureCardTemplateIds, f.Live.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(1, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count(id => id == 321));
        Assert.Equal(900, f.Saved.GameStats.m_currentGold); Assert.Equal(900, f.Live.GameStats.m_currentGold);
        Assert.Equal(2, f.Ledger.Animus[123]); Assert.True(Assert.Single(f.Ledger.Creations).Value.Delivered);
        Assert.Equal(7, f.Saved.SpellbookBehavior.DeckTreasureCount(501, 888));
        Assert.Equal(7, f.Live.SpellbookBehavior.DeckTreasureCount(501, 888));
        Assert.Equal(MonstrologyResult.Replay, f.Create()); // A durable replay needs no second free slot.
        Assert.Equal(1, f.SaveAttempts); Assert.Equal(1, f.Publishes);
    }

    [Fact]
    public void NormalCreationKeepsOneAtomicSaveAndOriginalOperationIdentity() {
        var f = new Store(); using var scope = f.Scope();
        Assert.Equal(MonstrologyResult.Applied, f.Create());
        Assert.Equal(1, f.SaveAttempts); Assert.Equal(1, f.SuccessfulSaves); Assert.Equal(1, f.Publishes);
        Assert.Equal(new uint[] { 321 }, f.Saved.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(new uint[] { 321 }, f.Live.SpellbookBehavior.TreasureCardTemplateIds);
        var receipt = Assert.Single(f.Ledger.Creations);
        Assert.Equal("native-create", receipt.Key); Assert.Equal(3, receipt.Value.AnimusCost);
        Assert.Equal(100, receipt.Value.GoldCost); Assert.Equal(0UL, receipt.Value.ItemId);
        Assert.Equal(MonstrologyResult.Replay, f.Create());
        Assert.Equal(1, f.SaveAttempts); Assert.Equal(1, f.Publishes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MarkedAttachedWizardRefusesBeforeLedgerOrOutputIsStaged(bool markWhileLoading) {
        var f = new Store(); using var scope = f.Scope();
        if (markWhileLoading) f.BeforeLoad = () => WizardCollection.MarkInventorySnapshotUncertain(f.Live);
        else WizardCollection.MarkInventorySnapshotUncertain(f.Live);
        Assert.Equal(MonstrologyResult.Rejected, f.Create());
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
        Assert.Equal(0, f.LedgerLoads); Assert.Equal(0, f.SaveAttempts); Assert.Equal(0, f.Publishes);
        Assert.Equal(1000, f.Saved.GameStats.m_currentGold); Assert.Empty(f.Saved.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(5, f.Ledger.Animus[123]); Assert.Empty(f.Ledger.Creations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FailedOrLostAcknowledgementQuarantinesBeforeLaneReleaseWithoutPublishingOrRetrying(bool committed, bool houseGuest) {
        var f = new Store { FailSave = true, CommitBeforeFailure = committed }; using var scope = f.Scope();
        var guest = houseGuest ? Store.Guest() : null;
        f.OnDispose = () => {
            Assert.True(WizardCollection.HoldsWriteLane);
            Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
            f.SawQuarantineBeforeRelease = true;
        };
        Assert.Throws<InvalidOperationException>(() => f.Create(guest: guest));
        Assert.True(f.SawQuarantineBeforeRelease); Assert.False(WizardCollection.HoldsWriteLane);
        Assert.Equal(1, f.SaveAttempts); Assert.Equal(committed ? 1 : 0, f.SuccessfulSaves); Assert.Equal(0, f.Publishes);
        Assert.Equal(committed ? 900 : 1000, f.Saved.GameStats.m_currentGold);
        Assert.Equal(committed ? 2 : 5, f.Ledger.Animus[123]); Assert.Equal(committed ? 1 : 0, f.Ledger.Creations.Count);
        Assert.Equal(committed && !houseGuest ? 1 : 0, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(committed && houseGuest ? 1 : 0, f.Saved.InventoryBehavior.InventoryItemIds.Count);
        Assert.Equal(committed && houseGuest ? 1 : 0, f.Items.Count);
        Assert.Equal(1000, f.Live.GameStats.m_currentGold); Assert.Empty(f.Live.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Empty(f.Live.InventoryBehavior.InventoryItemIds); Assert.Empty(f.Live.InventoryBehavior.Items);
        f.FailSave = false;
        Assert.Equal(MonstrologyResult.Rejected, f.Create("new-operation", guest));
        Assert.Equal(1, f.SaveAttempts); Assert.Equal(0, f.Publishes); // Refusal cannot automatically retry any operation.
        f.OnDispose = null;
        f.Live = Store.Clone(f.Saved);
        // Existing house-guest identity validation refuses an already owned output; TC operation replay remains Replay.
        Assert.Equal(committed ? (houseGuest ? MonstrologyResult.Rejected : MonstrologyResult.Replay) : MonstrologyResult.Applied,
            f.Create(guest: guest));
        Assert.Equal(committed ? 1 : 2, f.SaveAttempts);
        Assert.Equal(900, f.Saved.GameStats.m_currentGold); Assert.Equal(2, f.Ledger.Animus[123]); Assert.Single(f.Ledger.Creations);
        Assert.Equal(houseGuest ? 0 : 1, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(houseGuest ? 1 : 0, f.Saved.InventoryBehavior.InventoryItemIds.Count);
    }

    [Fact]
    public void PublicationFailureAlsoMarksTheAttachedSnapshotWhileStillHoldingTheLane() {
        var f = new Store { FailPublish = true }; using var scope = f.Scope();
        f.OnDispose = () => {
            Assert.True(WizardCollection.HoldsWriteLane);
            Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live));
            f.SawQuarantineBeforeRelease = true;
        };
        Assert.Throws<InvalidOperationException>(() => f.Create());
        Assert.True(f.SawQuarantineBeforeRelease); Assert.False(WizardCollection.HoldsWriteLane);
        Assert.Equal(900, f.Saved.GameStats.m_currentGold); Assert.Equal(2, f.Ledger.Animus[123]); Assert.Single(f.Ledger.Creations);
        Assert.Equal(new uint[] { 321 }, f.Saved.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(1000, f.Live.GameStats.m_currentGold); Assert.Empty(f.Live.SpellbookBehavior.TreasureCardTemplateIds);
        f.FailPublish = false;
        Assert.Equal(MonstrologyResult.Rejected, f.Create("new-operation")); Assert.Equal(1, f.SaveAttempts);
    }

    [Theory]
    [InlineData(19, true)]
    [InlineData(20, false)]
    public void HouseGuestUsesItsExistingBackpackLimitAndDoesNotConsumeATreasureBookSlot(int inventoryCount, bool allowed) {
        var f = new Store();
        f.Saved.SpellbookBehavior.TreasureCardTemplateIds = Enumerable.Repeat(777u, 999).ToList();
        f.Saved.InventoryBehavior.InventoryItemIds = Enumerable.Range(1, inventoryCount).Select(id => (ulong)id).ToList();
        using var scope = f.Scope();
        Assert.Equal(allowed ? MonstrologyResult.Applied : MonstrologyResult.Rejected, f.Create(guest: Store.Guest()));
        Assert.Equal(allowed ? 1 : 0, f.SaveAttempts); Assert.Equal(allowed ? 1 : 0, f.Publishes);
        Assert.Equal(999, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(allowed ? inventoryCount + 1 : inventoryCount, f.Saved.InventoryBehavior.InventoryItemIds.Count);
        Assert.Equal(allowed ? 900 : 1000, f.Saved.GameStats.m_currentGold);
        Assert.Equal(allowed ? 2 : 5, f.Ledger.Animus[123]);
        if (allowed) {
            Assert.Equal(Store.GuestId, Assert.Single(f.Items).m_globalID.Full);
            Assert.Equal(Store.GuestId, Assert.Single(f.Ledger.Creations).Value.ItemId);
            Assert.Equal(f.Saved.InventoryBehavior.InventoryItemIds, f.Live.InventoryBehavior.InventoryItemIds);
        } else { Assert.Empty(f.Items); Assert.Empty(f.Ledger.Creations); }
    }

    private sealed class Store {
        internal const ulong Owner = 42, GuestId = 9123;
        internal Wizard Saved = Make(), Live = Make();
        internal MonstrologyLedger Ledger = new() { OwnerId = Owner, Animus = new() { [123] = 5 } };
        internal List<WizClientObjectItem> Items = [];
        internal bool FailSave, CommitBeforeFailure, FailPublish, SawQuarantineBeforeRelease;
        internal int SaveAttempts, SuccessfulSaves, Publishes, LedgerLoads;
        internal Action? BeforeSave, BeforeLoad, OnDispose;
        internal IDisposable Scope() {
            var previous = WizardCollection.TestStoreScope.Value;
            WizardCollection.TestStoreScope.Value = new WizardCollection.TestStore(Open, (session, id) => {
                Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(Owner, id); BeforeLoad?.Invoke();
                return ((SessionProxy)(object)session).Working;
            });
            return new Restore(() => WizardCollection.TestStoreScope.Value = previous);
        }
        internal MonstrologyResult Create(string operation = "native-create", WizClientObjectItem? guest = null)
            => MonstrologyRepository.CreateCard(Owner, new AnimusCreation(operation, Owner, 123, 321, 3, 100, true,
                guest == null ? MonstrologyCreationKind.SummonCard : MonstrologyCreationKind.HouseGuest), out _,
                guest: guest, liveWizard: Live, afterCommit: saved => {
                    Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, SuccessfulSaves);
                    if (FailPublish) throw new InvalidOperationException("Injected publication failure");
                    Live.GameStats.m_currentGold = saved.GameStats.m_currentGold;
                    if (guest == null) Live.SpellbookBehavior.TreasureCardTemplateIds = saved.SpellbookBehavior.TreasureCardTemplateIds.ToList();
                    else { Live.InventoryBehavior.InventoryItemIds = saved.InventoryBehavior.InventoryItemIds.ToList(); Live.InventoryBehavior.Items.Add(guest); }
                    Publishes++;
                });
        private IDocumentSession Open() {
            Assert.True(WizardCollection.HoldsWriteLane);
            var session = DispatchProxy.Create<IDocumentSession, SessionProxy>();
            var proxy = (SessionProxy)(object)session;
            proxy.Working = Clone(Saved);
            proxy.Ledger = new MonstrologyLedger {
                OwnerId = Ledger.OwnerId, Experience = Ledger.Experience, Level = Ledger.Level,
                Animus = new(Ledger.Animus), Extractions = new(Ledger.Extractions), Creations = new(Ledger.Creations),
            };
            proxy.Items = Items.Select(item => item with { }).ToList();
            proxy.LoadLedger = () => { Assert.True(WizardCollection.HoldsWriteLane); LedgerLoads++; };
            proxy.OnDispose = () => OnDispose?.Invoke();
            proxy.Save = () => {
                Assert.True(WizardCollection.HoldsWriteLane); SaveAttempts++; BeforeSave?.Invoke();
                if (FailSave && !CommitBeforeFailure) throw new InvalidOperationException("Injected failed save");
                Saved = Clone(proxy.Working); Ledger = proxy.Ledger; Items = proxy.Items.Select(item => item with { }).ToList(); SuccessfulSaves++;
                if (FailSave) throw new InvalidOperationException("Injected lost acknowledgement");
            };
            return session;
        }
        internal static WizClientObjectItem Guest() => new() { m_globalID = GuestId, m_templateID = 321, m_characterId = Owner };
        private static Wizard Make() => new() {
            CharId = Owner,
            GameStats = new ServerWizGameStats(default, 1) { m_currentGold = 1000, m_baseGoldPouch = 2000 },
            InventoryBehavior = new() { InventoryItemIds = [], Items = [] },
            SpellbookBehavior = new() { TreasureCardTemplateIds = [], DeckTreasureCards = new() { [501] = new() { [888] = 7 } } },
        };
        internal static Wizard Clone(Wizard wizard) => new() {
            CharId = wizard.CharId,
            GameStats = wizard.GameStats.CloneSnapshotWithGold(wizard.GameStats.m_currentGold),
            InventoryBehavior = new() { InventoryItemIds = wizard.InventoryBehavior.InventoryItemIds.ToList(), Items = [] },
            SpellbookBehavior = new() {
                TreasureCardTemplateIds = wizard.SpellbookBehavior.TreasureCardTemplateIds.ToList(),
                DeckTreasureCards = ServerWizSpellbookBehavior.CopyLedger(wizard.SpellbookBehavior.DeckTreasureCards),
            },
        };
        private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
    }

    public class SessionProxy : DispatchProxy {
        internal Wizard Working = null!;
        internal MonstrologyLedger Ledger = null!;
        internal List<WizClientObjectItem> Items = [];
        internal Action Save = null!, LoadLedger = null!, OnDispose = null!;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, AdvancedProxy>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "get_Advanced" => _advanced,
            "Load" => Load(args!),
            "Store" => StoreEntity(args!),
            "SaveChanges" => InvokeAndReturn(Save),
            "Dispose" => InvokeAndReturn(OnDispose),
            _ => throw new NotSupportedException(method.Name),
        };
        private object Load(object?[] args) { Assert.Equal(MonstrologyRepository.DocumentId(Store.Owner), args[0]); LoadLedger(); return Ledger; }
        private object? StoreEntity(object?[] args) {
            switch (args[0]) {
                case MonstrologyLedger ledger: Assert.Equal(MonstrologyRepository.DocumentId(MonstrologyCardSafetyTests.Store.Owner), args[1]); Ledger = ledger; break;
                case WizClientObjectItem guest: Items.Add(guest); break;
                default: throw new NotSupportedException();
            }
            return null;
        }
        private static object? InvokeAndReturn(Action action) { action(); return null; }
    }
    public class AdvancedProxy : DispatchProxy {
        private readonly IMetadataDictionary _metadata = DispatchProxy.Create<IMetadataDictionary, MetadataProxy>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "set_OptimisticConcurrencyMode" => null,
            "GetMetadataFor" => _metadata,
            _ => throw new NotSupportedException(method.Name),
        };
    }
    public class MetadataProxy : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            Assert.Equal("set_Item", method!.Name); Assert.Equal(Raven.Client.Constants.Documents.Metadata.Collection, args![0]);
            Assert.Equal(WizardItemCollection.CollectionName, args[1]); return null;
        }
    }
}
