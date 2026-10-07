// CLASSIC: shared inventory safety at lost-acknowledgement, reload and pickup boundaries.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imcodec.ObjectProperty.TypeCache;
using Newtonsoft.Json;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class BazaarInventorySafetyTests {
    public BazaarInventorySafetyTests()
        => EquipmentAttachConcurrencyTests.Configure("[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostAcknowledgementMarksInsideLaneAndLogoutCannotOverwriteCommittedBag(bool committed) {
        var store = new Store { FailSave = true, CommitBeforeFailure = committed };
        var live = Store.Clone(store.Saved);
        live.AlchemyBehavior.ReagentItemIds = [11];
        using var scope = store.Scope();
        var markedInsideLane = false;
        Assert.Throws<InvalidOperationException>(() => WizardCollection.CommitCharacterMutation(live.CharId,
            (_, saved) => { saved.AlchemyBehavior.ReagentItemIds = [11, 22]; return true; }, _ => { },
            onSaveFailure: _ => {
                markedInsideLane = WizardCollection.HoldsWriteLane;
                WizardCollection.MarkInventorySnapshotUncertain(live);
            }));
        Assert.True(markedInsideLane);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.Equal(new ulong[] { 11 }, live.AlchemyBehavior.ReagentItemIds);
        Assert.Equal(committed ? new ulong[] { 11, 22 } : new ulong[] { 11 }, store.Saved.AlchemyBehavior.ReagentItemIds);
        store.FailSave = false;
        WizardCollection.UpdateCharacterItems(live);
        Assert.Equal(1, store.SaveAttempts);
        Assert.Equal(committed ? new ulong[] { 11, 22 } : new ulong[] { 11 }, store.Saved.AlchemyBehavior.ReagentItemIds);
        var reloaded = Store.Clone(store.Saved);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(reloaded));
        WizardCollection.UpdateCharacterItems(reloaded);
        Assert.Equal(2, store.SaveAttempts);
    }

    [Fact]
    public void AFailedPostCommitPublisherAlsoQuarantinesBeforeAnotherSnapshotSave() {
        var store = new Store();
        var live = Store.Clone(store.Saved);
        using var scope = store.Scope();
        Assert.Throws<InvalidOperationException>(() => WizardCollection.CommitCharacterMutation(live.CharId,
            (_, saved) => { saved.AlchemyBehavior.ReagentItemIds.Add(22); return true; },
            _ => throw new InvalidOperationException("injected publication failure"),
            onSaveFailure: _ => {
                Assert.True(WizardCollection.HoldsWriteLane);
                WizardCollection.MarkInventorySnapshotUncertain(live);
            }));
        WizardCollection.UpdateCharacterItems(live);
        Assert.Equal(new ulong[] { 11, 22 }, store.Saved.AlchemyBehavior.ReagentItemIds);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Fact]
    public void ValidationRefusalDoesNotSavePublishOrQuarantine() {
        var store = new Store();
        var live = Store.Clone(store.Saved);
        using var scope = store.Scope();
        Assert.False(WizardCollection.CommitCharacterMutation(live.CharId, (_, _) => false,
            _ => throw new Exception("must not publish"), onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live)));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.Equal(0, store.SaveAttempts);
    }

    [Theory]
    [InlineData(998, 1, true)]
    [InlineData(999, 1, false)]
    [InlineData(998, 2, false)]
    public void LibraryCapacityIsCheckedFromSavedBookRatherThanAnEmptyLiveSnapshot(int held, int quantity, bool allowed) {
        var store = new Store();
        store.Saved.SpellbookBehavior.TreasureCardTemplateIds = Enumerable.Repeat(123u, held).ToList();
        var live = Store.Clone(store.Saved);
        live.SpellbookBehavior.TreasureCardTemplateIds = [];
        using var scope = store.Scope();
        Assert.Equal(allowed, WizardCollection.TryPurchaseTreasureCards(live, 456, quantity, 10));
        Assert.Equal(allowed ? held + quantity : held, store.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(allowed ? 100 - quantity * 10 : 100, store.Saved.GameStats.m_currentGold);
        Assert.Equal(allowed ? 1 : 0, store.SaveAttempts);
    }

    [Fact]
    public void MarkedLegacyItemGrantsAndDeletesLeaveLiveAndSavedInventoryUntouched() {
        var store = new Store();
        var live = Store.Clone(store.Saved);
        var item = new WizClientObjectItem { m_globalID = 11, m_templateID = 12, m_characterId = live.CharId };
        live.InventoryBehavior.Items = [item];
        live.InventoryBehavior.InventoryItemIds = [11];
        WizardCollection.MarkInventorySnapshotUncertain(live);
        using var scope = store.Scope();
        Assert.False(live.AddItemToInventory(item));
        Assert.False(live.AddPetToInventory(item));
        Assert.False(live.RemoveItemFromInventory(11));
        Assert.False(live.DestroyInventoryItem(11));
        Assert.Same(item, Assert.Single(live.InventoryBehavior.Items));
        Assert.Equal(new ulong[] { 11 }, live.InventoryBehavior.InventoryItemIds);
        Assert.Equal(0, store.SaveAttempts);
    }

    [Fact]
    public void PickupUsesAcknowledgedAcquisitionCountInsteadOfFinalStackSize() {
        var store = new ReagentPersistenceRegressionTests.ReagentStore(5);
        using var scope = store.Scope();
        var live = store.Live();
        var receipt = InteractReagentComponent.GatherReagentReceipt(live, Assert.Single(live.AlchemyBehavior.Reagents), 2, null!);
        Assert.Equal(2, receipt.Acquired[ReagentPersistenceRegressionTests.ReagentStore.TemplateId]);
        Assert.Equal(7, Assert.Single(receipt.Reagents).m_quantity);
        Assert.Equal(ReagentPersistenceRegressionTests.ReagentStore.ItemId, Assert.Single(receipt.Reagents).m_globalID.Full);
        Assert.Equal(7, Assert.Single(store.SavedRows).m_quantity);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Fact]
    public void RefusedPickupHasNoReceiptAndMixedPickupAnnouncesOnlyActualSuccesses() {
        var store = new ReagentPersistenceRegressionTests.ReagentStore(999);
        using var scope = store.Scope();
        var live = store.Live();
        var normal = Assert.Single(live.AlchemyBehavior.Reagents);
        var rare = ReagentPersistenceRegressionTests.ReagentStore.Row(id: 3, template: 4, quantity: 1);
        Assert.Empty(InteractReagentComponent.GatherReagentReceipt(live, normal, 2, null!).Acquired);
        Assert.Equal(0, store.SaveAttempts);
        var receipt = InteractReagentComponent.GatherReagentReceipt(live, normal, 2, rare);
        Assert.Equal(1, receipt.Acquired[4]);
        Assert.False(receipt.Acquired.ContainsKey(normal.m_templateID.Full));
        Assert.Equal(3UL, Assert.Single(receipt.Reagents).m_globalID.Full);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUncertainPickupNeverPublishesAPartialReceiptOrRepeatsItsDelta(bool committed) {
        var store = new ReagentPersistenceRegressionTests.ReagentStore(5) { FailSave = true, CommitBeforeFailure = committed };
        using var scope = store.Scope();
        var live = store.Live();
        var normal = Assert.Single(live.AlchemyBehavior.Reagents);
        var rare = ReagentPersistenceRegressionTests.ReagentStore.Row(id: 3, template: 4, quantity: 1);
        Assert.Throws<InvalidOperationException>(() => InteractReagentComponent.GatherReagentReceipt(live, normal, 3, rare));
        Assert.Equal(1, store.SaveAttempts);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.Equal(committed ? 8 : 5, store.SavedRows.Single(row => row.m_templateID == normal.m_templateID).m_quantity);
        Assert.Equal(committed, store.SavedRows.Any(row => row.m_templateID.Full == 4));
        Assert.Empty(InteractReagentComponent.GatherReagentReceipt(live, normal, 3, rare).Acquired);
        Assert.Equal(1, store.SaveAttempts);
    }

    private sealed class Store {
        internal Wizard Saved = new() {
            CharId = 800731,
            InventoryBehavior = new() { InventoryItemIds = [], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [] },
            StorageBehavior = new() { BankItemIds = [] },
            PetSnackBehavior = new() { SnackItemIds = [] },
            AlchemyBehavior = new() { ReagentItemIds = [11], Reagents = [] },
            SpellbookBehavior = new() { TreasureCardTemplateIds = [], DeckTreasureCards = [] },
            GameStats = new ServerWizGameStats(default, 1) { m_currentGold = 100 },
        };
        internal bool FailSave, CommitBeforeFailure;
        internal int SaveAttempts;
        internal static Wizard Clone(Wizard saved) => JsonConvert.DeserializeObject<Wizard>(JsonConvert.SerializeObject(saved))!;
        internal IDisposable Scope() {
            var previous = WizardCollection.TestStoreScope.Value;
            WizardCollection.TestStoreScope.Value = new WizardCollection.TestStore(() => {
                var session = DispatchProxy.Create<IDocumentSession, Session>();
                var proxy = (Session)(object)session;
                proxy.Saved = Clone(Saved);
                proxy.Save = () => {
                    Assert.True(WizardCollection.HoldsWriteLane);
                    SaveAttempts++;
                    if (FailSave && !CommitBeforeFailure) throw new InvalidOperationException("injected failed save");
                    Saved = Clone(proxy.Saved);
                    if (FailSave) throw new InvalidOperationException("injected lost acknowledgement");
                };
                return session;
            }, (session, id) => { Assert.Equal(Saved.CharId, id); return ((Session)(object)session).Saved; });
            return new Restore(() => WizardCollection.TestStoreScope.Value = previous);
        }
        private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
    }
    public class Session : DispatchProxy {
        internal Wizard Saved = null!;
        internal System.Action Save = null!;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, Advanced>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "get_Advanced" => _advanced,
            "SaveChanges" => SaveAndReturn(),
            "Dispose" => null,
            _ => throw new NotSupportedException(method.Name),
        };
        private object? SaveAndReturn() { Save(); return null; }
    }
    public class Advanced : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
    }
}
