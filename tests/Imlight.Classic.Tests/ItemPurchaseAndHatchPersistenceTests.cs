// CLASSIC: payment and delivery cannot succeed separately, even when the acknowledgement is lost.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pets;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ItemPurchaseAndHatchPersistenceTests {
    public ItemPurchaseAndHatchPersistenceTests()
        => EquipmentAttachConcurrencyTests.Configure("[Character]\nMaxInventoryItems=150\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedBalanceAndOriginalDeliveryAreOneSaveAndPublishAfterAcknowledgement(bool crowns) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        live.GameStats.m_currentGold = 1; live.Account.Crowns = 1;
        var item = Fixture.Rental();
        f.BeforeSave = () => { Assert.Empty(live.InventoryBehavior.Items); Assert.Equal(1, live.GameStats.m_currentGold); Assert.Equal(1, live.Account.Crowns); };
        Assert.True(f.Buy(live, item, crowns, out var receipt));
        Assert.Same(item, receipt.Item); Assert.NotEqual(0, receipt.Data.Length);
        Assert.Equal(1, f.Saves); Assert.Equal(800, crowns ? f.Account.Crowns : f.Saved.GameStats.m_currentGold);
        Assert.Equal(800, crowns ? live.Account.Crowns : live.GameStats.m_currentGold);
        Assert.Equal(1000, crowns ? f.Saved.GameStats.m_currentGold : f.Account.Crowns);
        Assert.Equal(Fixture.Item, Assert.Single(f.Saved.InventoryBehavior.InventoryItemIds));
        var saved = Assert.Single(f.Rows);
        Assert.Equal(Fixture.Owner, saved.m_characterId.Full);
        Assert.Equal(1900000000u, Assert.Single(saved.m_inactiveBehaviors.OfType<ClientTimedItemBehavior>()).m_expireTime);
    }

    [Fact]
    public void ExistingZeroPriceShopItemStillDeliversWithoutChangingGold() {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        Assert.True(ClassicItemPurchases.Purchase(live, Fixture.Rental(), 0, false, out var receipt,
            serialize: _ => new ByteString(new byte[] { 1 })));
        Assert.NotNull(receipt); Assert.Equal(1, f.Saves); Assert.Equal(1000, f.Saved.GameStats.m_currentGold);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedOrLostPurchaseAcknowledgementNeverPublishesRefundsOrRepeats(bool crowns, bool durable) {
        var f = new Fixture { Fail = true, Durable = durable }; using var scope = f.Scope(); var live = f.Live();
        Assert.Throws<InvalidOperationException>(() => f.Buy(live, Fixture.Rental(), crowns, out _));
        Assert.Equal(1000, live.GameStats.m_currentGold); Assert.Equal(1000, live.Account.Crowns);
        Assert.Empty(live.InventoryBehavior.Items); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.Equal(durable ? 800 : 1000, crowns ? f.Account.Crowns : f.Saved.GameStats.m_currentGold);
        Assert.Equal(durable ? 1 : 0, f.Rows.Count);
        Assert.False(f.Buy(live, Fixture.Rental(), crowns, out var refusal)); Assert.Null(refusal); Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("foreign")]
    [InlineData("poor")]
    [InlineData("account-owner")]
    [InlineData("level")]
    [InlineData("serialize")]
    public void PurchaseRefusalsDoNotDebitOrSave(string reason) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        switch (reason) {
            case "full": f.Fill(); break;
            case "foreign": f.Rows.Add(Fixture.Rental() with { m_characterId = Fixture.Owner + 1 }); break;
            case "poor": f.Account.Crowns = 199; break;
            case "account-owner": f.Account.CharacterIds.Clear(); break;
            case "level": f.Saved.MagicSchoolBehavior.Level = 1; break;
        }
        Assert.False(f.Buy(live, Fixture.Rental(), true, out var receipt, reason == "serialize"));
        Assert.Null(receipt); Assert.Equal(0, f.Saves); Assert.Empty(live.InventoryBehavior.Items);
        Assert.Equal(reason == "poor" ? 199 : 1000, f.Account.Crowns); Assert.Equal(1000, f.Saved.GameStats.m_currentGold);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HatchSavesGoldOriginalEggCooldownAndSlotTogether(bool equipped) {
        var f = new Fixture(); f.WithParent(equipped); using var scope = f.Scope(); var live = f.Live();
        var petOwner = live.PetOwnerBehavior; var equipment = live.EquipmentBehavior;
        f.BeforeSave = () => { Assert.Equal(50000, live.GameStats.m_currentGold); Assert.Empty(petOwner.Eggs); Assert.Empty(petOwner.PetHatchTimes); };
        Assert.True(f.Hatch(live, out var receipt)); Assert.Equal(1, f.Saves);
        Assert.Equal(30000, f.Saved.GameStats.m_currentGold); Assert.Equal(30000, live.GameStats.m_currentGold);
        Assert.Equal(Fixture.Now, f.Saved.PetOwnerBehavior.PetHatchTimes[Fixture.Parent]);
        var slot = Assert.Single(f.Saved.PetOwnerBehavior.Eggs);
        Assert.Equal(Fixture.Item, slot.GlobalId); Assert.Equal(Fixture.Now + 600, slot.HatchTimeEpoch);
        Assert.Equal(600u, receipt.Seconds); Assert.Equal((uint)(Fixture.Now + 600), receipt.Finish);
        var egg = Assert.Single(f.Rows.Where(row => row.m_globalID.Full == Fixture.Item));
        Assert.Equal(200, Assert.Single(PetProgress.Behavior(egg).m_maxStats).m_value);
        Assert.Same(petOwner, live.PetOwnerBehavior); Assert.Same(equipment, live.EquipmentBehavior);
        Assert.Equal(Fixture.Item, Assert.Single(petOwner.MorphingSlots).m_globalID.Full);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("cooldown")]
    [InlineData("foreign-parent")]
    [InlineData("missing-parent")]
    [InlineData("bank-parent")]
    [InlineData("young")]
    [InlineData("poor")]
    [InlineData("serialize")]
    public void RefusedHatchDoesNotChargeStartCooldownOrCreateEggSlot(string reason) {
        var f = new Fixture(); f.WithParent(false); using var scope = f.Scope(); var live = f.Live();
        switch (reason) {
            case "full": f.Fill(); break;
            case "cooldown": f.Saved.PetOwnerBehavior.PetHatchTimes[Fixture.Parent] = Fixture.Now - 1; break;
            case "foreign-parent": f.Rows[0].m_characterId = Fixture.Owner + 1; break;
            case "missing-parent": f.Rows.Clear(); break;
            case "bank-parent": f.Saved.InventoryBehavior.InventoryItemIds = []; f.Saved.StorageBehavior.BankItemIds = [Fixture.Parent]; break;
            case "young": PetProgress.Behavior(f.Rows[0]).m_level = 1; break;
            case "poor": f.Saved.GameStats.m_currentGold = 19999; break;
        }
        Assert.False(f.Hatch(live, out var receipt, reason == "serialize")); Assert.Null(receipt);
        Assert.Equal(0, f.Saves); Assert.Empty(f.Saved.PetOwnerBehavior.Eggs); Assert.Empty(live.PetOwnerBehavior.Eggs);
        Assert.DoesNotContain(f.Rows, row => row.m_globalID.Full == Fixture.Item);
        Assert.Equal(reason == "poor" ? 19999 : 50000, f.Saved.GameStats.m_currentGold);
        if (reason != "cooldown") Assert.Empty(f.Saved.PetOwnerBehavior.PetHatchTimes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownHatchAcknowledgementLeavesLiveEggAndWalletUntouchedAndCannotRepeat(bool durable) {
        var f = new Fixture { Fail = true, Durable = durable }; f.WithParent(false); using var scope = f.Scope(); var live = f.Live();
        Assert.Throws<InvalidOperationException>(() => f.Hatch(live, out _));
        Assert.Equal(50000, live.GameStats.m_currentGold); Assert.Empty(live.PetOwnerBehavior.Eggs); Assert.Empty(live.PetOwnerBehavior.PetHatchTimes);
        Assert.Equal(durable ? 30000 : 50000, f.Saved.GameStats.m_currentGold);
        Assert.Equal(durable ? 1 : 0, f.Saved.PetOwnerBehavior.Eggs.Count);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); Assert.False(f.Hatch(live, out _)); Assert.Equal(1, f.Saves);
    }

    private sealed class Fixture {
        internal const ulong Owner = 778321, AccountId = 778322, Item = 778323, Parent = 778324;
        internal const uint Template = 77123;
        internal const long Now = 1800000000;
        internal Wizard Saved = new() { CharId = Owner, AccountId = AccountId,
            MagicSchoolBehavior = new() { Level = 50 },
            GameStats = new(default, 50) { m_currentGold = 1000 },
            InventoryBehavior = new() { InventoryItemIds = [], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = [] },
            StorageBehavior = new() { BankItemIds = [] },
            PetOwnerBehavior = new() { Eggs = [], PetHatchTimes = [] },
        };
        internal Account Account = CreateAccount(AccountId, [Owner], 1000);
        internal List<WizClientObjectItem> Rows = [];
        internal int Saves; internal bool Fail, Durable; internal System.Action? BeforeSave;
        internal static WizClientObjectItem Rental() => new() { m_globalID = Item, m_templateID = Template,
            m_characterId = Owner, m_inactiveBehaviors = [new ClientTimedItemBehavior { m_expireTime = 1900000000 }] };
        private static ByteString Data(WizClientObjectItem _) => new(new byte[] { 1, 2, 3 });
        internal bool Buy(Wizard live, WizClientObjectItem item, bool crowns, out ItemPurchaseReceipt receipt, bool bad = false)
            => ClassicItemPurchases.Purchase(live, item, 200, crowns, out receipt, 15,
                bad ? _ => default : Data, (session, _) => Session(session).Account);
        internal bool Hatch(Wizard live, out PetHatchReceipt receipt, bool bad = false) {
            var parent = new HatchParent(Template, PetRules.Adult, new Dictionary<string, int> { ["strength"] = 200 }, [], [], 10, 0);
            var egg = new WizClientObjectItem { m_globalID = Item, m_templateID = Template, m_characterId = Owner,
                m_inactiveBehaviors = [new ClientPetItemBehavior { m_level = 0, m_hatchedTimeSecs = (uint)(Now + 600),
                    m_maxStats = [new PetStat { m_name = "strength", m_value = 200 }], m_allTalents = [] }] };
            return ClassicPetHatchTransactions.TryCreate(live, Parent, parent, parent, egg, Now, out receipt, bad ? _ => default : Data);
        }
        internal void WithParent(bool equipped) {
            Saved.GameStats.m_currentGold = 50000;
            Rows.Add(new() { m_globalID = Parent, m_templateID = Template, m_characterId = Owner,
                m_inactiveBehaviors = [new ClientPetItemBehavior { m_level = (byte)PetRules.Adult, m_overallRating = 10 }] });
            if (equipped) Saved.EquipmentBehavior.EquippedItemIds = [Parent]; else Saved.InventoryBehavior.InventoryItemIds = [Parent];
        }
        internal void Fill() {
            for (ulong id = 1; Saved.InventoryBehavior.InventoryItemIds.Count < ServerWizInventoryBehavior.MaxItemsAllowed; id++) {
                Rows.Add(new() { m_globalID = id, m_templateID = Template, m_characterId = Owner });
                Saved.InventoryBehavior.InventoryItemIds.Add(id);
            }
        }
        internal Wizard Live() {
            var live = Clone(Saved);
            live.Account = Clone(Account);
            live.InventoryBehavior.Items = [..Rows.Where(row => live.InventoryBehavior.InventoryItemIds.Contains(row.m_globalID.Full)).Select(Clone)];
            live.EquipmentBehavior.EquippedItems = [..Rows.Where(row => live.EquipmentBehavior.EquippedItemIds.Contains(row.m_globalID.Full)).Select(Clone)];
            return live;
        }
        internal IDisposable Scope() {
            var oldStore = WizardCollection.TestStoreScope.Value; var oldRows = WizardInventoryTransactions.TestRowsScope.Value;
            WizardCollection.TestStoreScope.Value = new(Open, (session, _) => Session(session).Wizard);
            WizardInventoryTransactions.TestRowsScope.Value = session => Session(session).Rows;
            return new Restore(() => { WizardCollection.TestStoreScope.Value = oldStore; WizardInventoryTransactions.TestRowsScope.Value = oldRows; });
        }
        private IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, PurchaseSession>(); var proxy = Session(session);
            proxy.Wizard = Clone(Saved); proxy.Account = Clone(Account); proxy.Rows = Rows.Select(Clone).ToList();
            proxy.Save = () => {
                Assert.True(WizardCollection.HoldsWriteLane); Saves++; BeforeSave?.Invoke();
                if (Fail && !Durable) throw new InvalidOperationException("fixture refused write");
                Saved = Clone(proxy.Wizard); Account = Clone(proxy.Account); Rows = proxy.Rows.Select(Clone).ToList();
                if (Fail) throw new InvalidOperationException("fixture lost acknowledgement");
            };
            return session;
        }
        private static PurchaseSession Session(IDocumentSession session) => (PurchaseSession)(object)session;
        private static Account Clone(Account account) => CreateAccount(account.AccountId, account.CharacterIds, account.Crowns);
        private static Account CreateAccount(ulong id, IEnumerable<ulong> characters, int crowns) {
            var account = new Account { Crowns = crowns };
            typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(account, id);
            account.CharacterIds.AddRange(characters);
            return account;
        }
        private static Wizard Clone(Wizard wizard) => new() { CharId = wizard.CharId, AccountId = wizard.AccountId,
            MagicSchoolBehavior = new() { Level = wizard.MagicSchoolBehavior.Level },
            GameStats = wizard.GameStats.CloneSnapshotWithGold(wizard.GameStats.m_currentGold),
            InventoryBehavior = new() { InventoryItemIds = [..wizard.InventoryBehavior.InventoryItemIds], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [..wizard.EquipmentBehavior.EquippedItemIds], EquippedItems = [] },
            StorageBehavior = new() { BankItemIds = [..wizard.StorageBehavior.BankItemIds] },
            PetOwnerBehavior = new() { Eggs = wizard.PetOwnerBehavior.Eggs.Select(egg => egg with { }).ToList(), PetHatchTimes = new(wizard.PetOwnerBehavior.PetHatchTimes) },
        };
        private static WizClientObjectItem Clone(WizClientObjectItem item) => item with {
            m_inactiveBehaviors = item.m_inactiveBehaviors is null ? [] : item.m_inactiveBehaviors.Select(behavior => behavior switch {
                ClientTimedItemBehavior timed => timed with { }, ClientPetItemBehavior pet => pet with { }, _ => behavior,
            }).ToList(),
        };
        private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
    }
    public class PurchaseSession : DispatchProxy {
        internal Wizard Wizard = null!; internal Account Account = null!; internal List<WizClientObjectItem> Rows = [];
        internal System.Action Save = null!;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, ItemInventoryPersistenceTests.ItemAdvanced>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "get_Advanced" => _advanced, "Store" => Store((WizClientObjectItem)args![0]), "SaveChanges" => SaveNow(), "Dispose" => null,
            _ => throw new NotSupportedException(method.Name),
        };
        private object? Store(WizClientObjectItem item) { Rows.Add(item); return null; }
        private object? SaveNow() { Save(); return null; }
    }
}
