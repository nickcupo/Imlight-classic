using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Inventory;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class HousingInventoryConcurrencyTests {
    private const ulong Owner = 724243;
    private const ulong ItemId = 9002;
    private const uint Template = (1u << 28) - 4243;
    private const uint Dynamic = 0x1234;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlacementAndConcurrentDiscardOrQuickSellCannotBothClaimTheOriginalItem(bool quickSell) {
        EquipmentAttachConcurrencyTests.Configure();
        var cache = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        cache.TryGetValue(Template, out var previous);
        cache[Template] = new WizItemTemplate {
            m_templateID = Template, m_adjectiveList = ["Housing", "Furniture"],
            m_behaviors = [new RenderBehaviorTemplate(), new FurnitureInfoBehaviorTemplate()],
        };
        using var saving = new ManualResetEventSlim();
        using var releaseSave = new ManualResetEventSlim();
        using var destroying = new ManualResetEventSlim();
        var documents = new Dictionary<string, object> {
            ["wizard/1"] = Wizard(),
            ["item/1"] = new WizClientObjectItem { m_globalID = ItemId, m_templateID = Template, m_characterId = Owner },
        };
        var live = Wizard();
        live.InventoryBehavior.InventoryItemIds = [];
        var blockNextSave = 0;
        HousingResult placed = default;
        var destroyed = false;
        var paidGold = 0;
        Exception? placementError = null, destroyError = null;
        Thread? placement = null, destruction = null;
        var oldScope = WizardCollection.TestStoreScope.Value;
        var store = new WizardCollection.TestStore(Open, (session, _) => session.Load<Wizard>("wizard/1"));
        WizardCollection.TestStoreScope.Value = store;
        try {
            Assert.True(live.InventoryBehavior.AddItem((WizClientObjectItem)documents["item/1"]));
            HousingCollection.Load(Owner, create: true);
            blockNextSave = 1;
            placement = new Thread(() => {
                WizardCollection.TestStoreScope.Value = store;
                try {
                    placed = HousingCollection.Place(live, Owner, ItemId, 11.234375f, -12.625f, 178.765625f, 0.73123455f,
                        (session, _, _) => session.Load<WizClientObjectItem>("item/1"));
                }
                catch (Exception ex) { placementError = ex; }
            }) { IsBackground = true };
            placement.Start();
            Assert.True(saving.Wait(TimeSpan.FromSeconds(10)), "Placement did not reach its paused transaction save.");
            destruction = new Thread(() => {
                WizardCollection.TestStoreScope.Value = store;
                destroying.Set();
                try {
                    if (quickSell) {
                        var sales = BackpackQuickSell.Execute([new(ItemId, 1), new(ItemId, 1)],
                            id => live.InventoryBehavior.HasItem(id) ? 7d : null, live.DestroyInventoryItem);
                        destroyed = sales.Count > 0;
                        paidGold = BackpackQuickSell.GoldToApply(sales, 0, 100);
                    }
                    else destroyed = live.DestroyInventoryItem(ItemId);
                }
                catch (Exception ex) { destroyError = ex; }
            }) { IsBackground = true };
            destruction.Start();
            Assert.True(destroying.Wait(TimeSpan.FromSeconds(10)));
            // The dedicated thread has no waits of its own: while the placement save is paused,
            // WaitSleepJoin proves it reached the competing character lane, before or after claiming the live item.
            Assert.True(SpinWait.SpinUntil(() => !destruction.IsAlive
                || (destruction.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
            Assert.Null(destroyError);
            Assert.True(destruction.IsAlive, "Discard unexpectedly completed while placement still held its write lane.");
            Assert.True(live.InventoryBehavior.HasItem(ItemId), "Discard claimed the live item outside the character lane.");
            releaseSave.Set();
            Assert.True(placement.Join(TimeSpan.FromSeconds(10)));
            Assert.True(destruction.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(placementError);
            Assert.Null(destroyError);
            Assert.True(placed.Saved, placed.Error);
            Assert.False(destroyed);
            Assert.Equal(0, paidGold);
            Assert.Empty(live.InventoryBehavior.InventoryItemIds);
            Assert.Empty(((Wizard)documents["wizard/1"]).InventoryBehavior.InventoryItemIds);
            Assert.False(HousingCollection.Load(Owner)!.Entries[0].Removed);
            Assert.True(HousingCollection.Pickup(live, Owner, HousingRules.PlacedGlobalId(0, Dynamic), Dynamic).Saved);
            Assert.Equal(ItemId, Assert.Single(live.InventoryBehavior.InventoryItemIds));
            Assert.Equal(ItemId, Assert.Single(live.InventoryBehavior.Items).m_globalID.Full);
        }
        finally {
            releaseSave.Set();
            placement?.Join(TimeSpan.FromSeconds(10));
            destruction?.Join(TimeSpan.FromSeconds(10));
            WizardCollection.TestStoreScope.Value = oldScope;
            if (previous is null) cache.Remove(Template);
            else cache[Template] = previous;
        }

        IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, HousingTests.HousingSessionProxy>();
            var proxy = (HousingTests.HousingSessionProxy)(object)session;
            proxy.Saved = documents;
            proxy.Fail = () => {
                if (Interlocked.Exchange(ref blockNextSave, 0) == 1) {
                    saving.Set();
                    if (!releaseSave.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Placement save was not released.");
                }
                return false;
            };
            return session;
        }
    }

    private static Wizard Wizard() => new() {
        CharId = Owner, Zone = HousingRules.DormZone,
        InventoryBehavior = new() { InventoryItemIds = [ItemId], Items = new() },
        EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = new(), SlotList = [] },
        StorageBehavior = new() { BankItemIds = [], Items = new() },
    };
}
