// CLASSIC: paid modifications operate on tracked originals and publish their native receipt only after ACK.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.Bit;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class PaidItemChangePersistenceTests {
    public PaidItemChangePersistenceTests()
        => EquipmentAttachConcurrencyTests.Configure("[Character]\nMaxInventoryItems=150\n[Classic]\nBackpackSize=2\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DyeSavesOriginalColorsAndFreshPaymentOnceAndPreparesNativeFlagsBeforeSave(bool equipped) {
        var f = new Fixture(equipped); using var scope = f.Scope(); var live = f.Live();
        var original = f.Target(live); live.GameStats.m_currentGold = 1;
        f.BeforeSave = () => {
            Assert.Equal((1, 2, 3), (original.m_primaryColor, original.m_secondaryColor, original.m_pattern));
            Assert.Equal(1, live.GameStats.m_currentGold); Assert.Equal(100, f.Saved.GameStats.m_currentGold);
            Assert.Equal((7, 8, 9), (f.Working!.Target.m_primaryColor, f.Working.Target.m_secondaryColor, f.Working.Target.m_pattern));
            Assert.Equal(equipped ? 2 : 0, f.Masks.Count);
        };
        Assert.True(f.Dye(live, out var receipt)); Assert.Same(original, receipt.Item);
        Assert.Equal((7, 8, 9), (original.m_primaryColor, original.m_secondaryColor, original.m_pattern));
        Assert.Equal(40, receipt.Cost); Assert.Equal(60, receipt.Gold); Assert.Equal(60, live.GameStats.m_currentGold);
        Assert.Equal(1, f.SaveAttempts); Assert.Equal(new[] { Fixture.Document }, f.Rows.Keys);
        Assert.Equal((7, 8, 9), (f.Stored.m_primaryColor, f.Stored.m_secondaryColor, f.Stored.m_pattern));
        Assert.Equal(equipped ? new ulong[] { Fixture.Id } : Array.Empty<ulong>(), f.Saved.EquipmentBehavior.EquippedItemIds);
        Assert.Equal(equipped ? Array.Empty<ulong>() : new ulong[] { Fixture.Id }, f.Saved.InventoryBehavior.InventoryItemIds);
        if (equipped) {
            Assert.Equal(new[] { ClassicPaidItemChanges.EquippedItemMask, (PropertyFlags)1 }, f.Masks);
            Assert.Equal("Hat", receipt.EquippedDye.SlotName);
            Assert.Equal(new byte[] { 7, 8, 9 }, (byte[])receipt.EquippedDye.LocalData);
            Assert.Equal(new byte[] { 17, 18, 19 }, (byte[])receipt.EquippedDye.PublicData);
        }
        else Assert.Null(receipt.EquippedDye);
    }

    [Theory]
    [InlineData(false, 40)]
    [InlineData(true, 40)]
    [InlineData(false, 0)]
    public void RenameSavesPackedAndUnpackedNamesWithGoldAndRetainsOriginalPetAliases(bool equipped, int cost) {
        var f = new Fixture(equipped, pet: true) { Cost = cost }; using var scope = f.Scope(); var live = f.Live();
        var original = f.Target(live); var behaviors = original.m_inactiveBehaviors;
        var name = original.m_inactiveBehaviors.OfType<ClientPetNameBehavior>().Single();
        var growth = original.m_inactiveBehaviors.OfType<ClientPetItemBehavior>().Single();
        var timer = original.m_inactiveBehaviors.OfType<ClientTimedItemBehavior>().Single();
        // Growth changed in the saved original since attach; renaming must preserve that saved state.
        f.Stored.m_inactiveBehaviors.OfType<ClientPetItemBehavior>().Single().m_level = 5;
        live.GameStats.m_currentGold = 1;
        f.BeforeSave = () => {
            Assert.Equal(Fixture.OldName, name.m_nameKeys); Assert.Equal(3, growth.m_level);
            Assert.Equal(1, live.GameStats.m_currentGold);
            Assert.Equal(Fixture.NewName, f.Working!.Target.m_inactiveBehaviors.OfType<ClientPetNameBehavior>().Single().m_nameKeys);
            Assert.Equal(Fixture.OldName, f.Stored.m_inactiveBehaviors.OfType<ClientPetNameBehavior>().Single().m_nameKeys);
        };
        Assert.True(f.Rename(live, out var receipt)); Assert.Same(original, receipt.Item);
        Assert.Same(behaviors, original.m_inactiveBehaviors); Assert.Same(name, original.m_inactiveBehaviors.OfType<ClientPetNameBehavior>().Single());
        Assert.Same(growth, original.m_inactiveBehaviors.OfType<ClientPetItemBehavior>().Single());
        Assert.Same(timer, original.m_inactiveBehaviors.OfType<ClientTimedItemBehavior>().Single());
        Assert.Equal(Fixture.NewName, name.m_nameKeys); Assert.Equal((2, 0, 3), (growth.m_firstName, growth.m_middleName, growth.m_lastName));
        Assert.Equal(5, growth.m_level); Assert.Equal(345u, growth.m_hatchedTimeSecs); Assert.Equal(567u, timer.m_expireTime);
        var savedGrowth = f.Stored.m_inactiveBehaviors.OfType<ClientPetItemBehavior>().Single();
        Assert.Equal((2, 0, 3), (savedGrowth.m_firstName, savedGrowth.m_middleName, savedGrowth.m_lastName));
        Assert.Equal(Fixture.NewName, f.Stored.m_inactiveBehaviors.OfType<ClientPetNameBehavior>().Single().m_nameKeys);
        Assert.Equal(100 - cost, f.Saved.GameStats.m_currentGold); Assert.Equal(100 - cost, receipt.Gold);
        Assert.Equal(cost, receipt.Cost); Assert.Equal(1, f.SaveAttempts); Assert.Equal(new[] { Fixture.Document }, f.Rows.Keys);
    }

    [Fact]
    public void DyePreservesWornDeckSpellbookAliasAndEveryUnrelatedBehaviorAndLocationList() {
        var f = new Fixture(equipped: true);
        f.Stored.m_inactiveBehaviors = [new DeckBehavior { m_spellList = [new SpellData { m_templateID = 77, m_quantity = 2 }] },
            new ClientTimedItemBehavior { m_expireTime = 567 }];
        using var scope = f.Scope(); var live = f.Live(); var original = f.Target(live);
        var deck = original.m_inactiveBehaviors.OfType<DeckBehavior>().Single();
        live.SpellbookBehavior.SpellList = deck.m_spellList;
        var behaviors = original.m_inactiveBehaviors; var equipment = live.EquipmentBehavior; var slots = equipment.SlotList;
        var equipmentItems = equipment.EquippedItems; var bag = live.InventoryBehavior.Items; var bank = live.StorageBehavior.Items;
        Assert.True(f.Dye(live, out _)); Assert.Same(original, f.Target(live)); Assert.Same(behaviors, original.m_inactiveBehaviors);
        Assert.Same(deck, original.m_inactiveBehaviors.OfType<DeckBehavior>().Single()); Assert.Same(deck.m_spellList, live.SpellbookBehavior.SpellList);
        Assert.Equal(2u, Assert.Single(deck.m_spellList).m_quantity);
        Assert.Same(equipment, live.EquipmentBehavior); Assert.Same(equipmentItems, equipment.EquippedItems); Assert.Same(slots, equipment.SlotList);
        Assert.Same(bag, live.InventoryBehavior.Items); Assert.Same(bank, live.StorageBehavior.Items); Assert.Equal(1, f.SaveAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSavedBackpackDoesNotPreventModificationOfAnExistingOriginal(bool rename) {
        var f = new Fixture(pet: rename); f.Fill(); using var scope = f.Scope(); var live = f.Live();
        var ids = f.Saved.InventoryBehavior.InventoryItemIds.ToArray();
        Assert.True(rename ? f.Rename(live, out _) : f.Dye(live, out _));
        Assert.Equal(ids, f.Saved.InventoryBehavior.InventoryItemIds); Assert.Equal(ids, live.InventoryBehavior.InventoryItemIds);
        Assert.Equal(ServerWizInventoryBehavior.MaxItemsAllowed, f.Rows.Count); Assert.Equal(1, f.SaveAttempts);
    }

    [Theory]
    [InlineData("bank", false)]
    [InlineData("orphan", false)]
    [InlineData("foreign", false)]
    [InlineData("missing", false)]
    [InlineData("duplicate-row", false)]
    [InlineData("overlap", false)]
    [InlineData("poor", false)]
    [InlineData("bank", true)]
    [InlineData("orphan", true)]
    [InlineData("foreign", true)]
    [InlineData("missing", true)]
    [InlineData("duplicate-row", true)]
    [InlineData("overlap", true)]
    [InlineData("poor", true)]
    [InlineData("missing-name", true)]
    [InlineData("invalid-name", true)]
    [InlineData("negative-price", false)]
    [InlineData("negative-price", true)]
    public void KnownRefusalUsesFreshSavedOwnershipEligibilityAndWalletWithoutAnySave(string reason, bool rename) {
        var f = new Fixture(pet: true); var live = f.Live(); var original = f.Target(live); var gold = f.Saved.GameStats.m_currentGold;
        switch (reason) {
            case "bank": f.Saved.InventoryBehavior.InventoryItemIds = []; f.Saved.StorageBehavior.BankItemIds = [Fixture.Id]; break;
            case "orphan": f.Saved.InventoryBehavior.InventoryItemIds = []; break;
            case "foreign": f.Stored.m_characterId = Fixture.Owner + 1; break;
            case "missing": f.Rows.Clear(); break;
            case "duplicate-row": f.Rows["duplicate"] = Fixture.CloneItem(f.Stored); break;
            case "overlap": f.Saved.EquipmentBehavior.EquippedItemIds = [Fixture.Id]; break;
            case "poor": f.Saved.GameStats.m_currentGold = 39; break;
            case "missing-name": f.Stored.m_inactiveBehaviors.RemoveAll(behavior => behavior is ClientPetNameBehavior); break;
            case "invalid-name": f.ValidName = false; break;
            case "negative-price": f.Cost = -1; break;
        }
        live.GameStats.m_currentGold = 999;
        using var scope = f.Scope();
        PaidItemChangeReceipt receipt;
        Assert.False(rename ? f.Rename(live, out receipt) : f.Dye(live, out receipt)); Assert.Null(receipt);
        Assert.Equal(0, f.SaveAttempts); Assert.Equal(reason == "poor" ? 39 : gold, f.Saved.GameStats.m_currentGold);
        Assert.Equal(999, live.GameStats.m_currentGold); Assert.Equal((1, 2, 3), (original.m_primaryColor, original.m_secondaryColor, original.m_pattern));
        Assert.Equal(Fixture.OldName, original.m_inactiveBehaviors.OfType<ClientPetNameBehavior>().Single().m_nameKeys);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData("local")]
    [InlineData("public")]
    [InlineData("throw")]
    [InlineData("slot")]
    public void NativePreparationFailureCannotSavePaymentPaintLiveColorsOrProduceReceipt(string failure) {
        var f = new Fixture(equipped: true) { SerializationFailure = failure }; using var scope = f.Scope(); var live = f.Live();
        if (failure == "slot") f.Template.m_adjectiveList = [];
        Assert.False(f.Dye(live, out var receipt)); Assert.Null(receipt); Assert.Equal(0, f.SaveAttempts);
        Assert.Equal(100, f.Saved.GameStats.m_currentGold); Assert.Equal(100, live.GameStats.m_currentGold);
        Assert.Equal((1, 2, 3), (f.Target(live).m_primaryColor, f.Target(live).m_secondaryColor, f.Target(live).m_pattern));
        Assert.Equal((1, 2, 3), (f.Stored.m_primaryColor, f.Stored.m_secondaryColor, f.Stored.m_pattern));
    }

    [Theory]
    [InlineData(-1, 8, 9)]
    [InlineData(7, 32, 9)]
    [InlineData(7, 8, 32)]
    public void EveryDyeLayerRetainsItsExistingFiveBitRange(int primary, int secondary, int pattern) {
        var f = new Fixture(); using var scope = f.Scope();
        Assert.False(f.Dye(f.Live(), out _, primary, secondary, pattern)); Assert.Equal(0, f.SaveAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PetColorEligibilityUsesTheFreshCurrentColorWhenTemplateOffersNoChoices(bool allowed) {
        var f = new Fixture(pet: true) { IsPet = true }; f.Template.m_numPrimaryColors = 1;
        f.Template.m_numSecondaryColors = 1; f.Template.m_numPatterns = 1;
        var live = f.Live(); f.Stored.m_primaryColor = 5;
        using var scope = f.Scope();
        Assert.Equal(allowed, f.Dye(live, out var receipt, allowed ? 5 : 1, 2, 3));
        Assert.Equal(allowed ? 1 : 0, f.SaveAttempts);
        Assert.Equal(allowed ? 5 : 1, f.Target(live).m_primaryColor);
        if (!allowed) Assert.Null(receipt);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedOrDurableLostAcknowledgementDoesNotPublishModificationPaymentOrRetry(bool rename, bool durable) {
        var f = new Fixture(equipped: true, pet: rename) { FailSave = true, Durable = durable };
        using var scope = f.Scope(); var live = f.Live(); var original = f.Target(live); var behaviors = original.m_inactiveBehaviors;
        PaidItemChangeReceipt receipt = null!;
        Assert.Throws<InvalidOperationException>(() => rename ? f.Rename(live, out receipt) : f.Dye(live, out receipt));
        Assert.Null(receipt); Assert.Equal(100, live.GameStats.m_currentGold); Assert.Same(original, f.Target(live));
        Assert.Same(behaviors, original.m_inactiveBehaviors); Assert.Equal((1, 2, 3), (original.m_primaryColor, original.m_secondaryColor, original.m_pattern));
        if (rename) Assert.Equal(Fixture.OldName, original.m_inactiveBehaviors.OfType<ClientPetNameBehavior>().Single().m_nameKeys);
        Assert.Equal(durable ? 60 : 100, f.Saved.GameStats.m_currentGold);
        if (rename) Assert.Equal(durable ? Fixture.NewName : Fixture.OldName, f.Stored.m_inactiveBehaviors.OfType<ClientPetNameBehavior>().Single().m_nameKeys);
        else Assert.Equal(durable ? 7 : 1, f.Stored.m_primaryColor);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.False(rename ? f.Rename(live, out _) : f.Dye(live, out _)); Assert.Equal(1, f.SaveAttempts);
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("type", false)]
    [InlineData("template", false)]
    [InlineData("owner", false)]
    [InlineData("missing", true)]
    [InlineData("type", true)]
    [InlineData("template", true)]
    [InlineData("pet-behavior", true)]
    public void KnownImpossibleLivePublicationRefusesBeforePaymentOrSave(string reason, bool rename) {
        var f = new Fixture(pet: rename); using var scope = f.Scope(); var live = f.Live();
        var original = f.Target(live);
        switch (reason) {
            case "missing": live.InventoryBehavior.Items = []; break;
            case "type": live.InventoryBehavior.Items = [new ClientReagentItem { m_globalID = Fixture.Id,
                m_templateID = Fixture.TemplateId, m_characterId = Fixture.Owner, m_inactiveBehaviors = original.m_inactiveBehaviors }]; break;
            case "template": original.m_templateID = Fixture.TemplateId + 1; break;
            case "owner": original.m_characterId = Fixture.Owner + 1; break;
            case "pet-behavior": original.m_inactiveBehaviors.RemoveAll(behavior => behavior is ClientPetNameBehavior); break;
        }
        PaidItemChangeReceipt receipt;
        Assert.False(rename ? f.Rename(live, out receipt) : f.Dye(live, out receipt)); Assert.Null(receipt);
        Assert.Equal(100, f.Saved.GameStats.m_currentGold); Assert.Equal(100, live.GameStats.m_currentGold);
        Assert.Equal(0, f.SaveAttempts); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnexpectedPublicationFailureAfterDurableSaveIsUncertainAndCannotBeRefundedOrRetried(bool rename) {
        var f = new Fixture(pet: rename); using var scope = f.Scope(); var live = f.Live();
        f.BeforeSave = () => live.InventoryBehavior.Items = [];
        Assert.Throws<InvalidOperationException>(() => rename ? f.Rename(live, out _) : f.Dye(live, out _));
        Assert.Equal(60, f.Saved.GameStats.m_currentGold); Assert.Equal(100, live.GameStats.m_currentGold);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.False(rename ? f.Rename(live, out _) : f.Dye(live, out _)); Assert.Equal(1, f.SaveAttempts);
    }

    [Fact]
    public async Task DyeAndRenameContendingForTheSameSavedWalletCannotBothSpendIt() {
        var f = new Fixture(pet: true) { Cost = 70 }; using var scope = f.Scope(); var dyeLive = f.Live(); var nameLive = f.Live();
        var results = await Task.WhenAll(Task.Run(() => f.Dye(dyeLive, out _), TestContext.Current.CancellationToken),
            Task.Run(() => f.Rename(nameLive, out _), TestContext.Current.CancellationToken));
        Assert.Single(results.Where(result => result)); Assert.Equal(30, f.Saved.GameStats.m_currentGold); Assert.Equal(1, f.SaveAttempts);
    }

    private sealed class Fixture {
        internal const ulong Owner = 817411, Id = 817412; internal const uint TemplateId = 817413;
        internal const uint OldName = 0x00010001, NewName = 0xAB020003;
        internal const string Document = "original/paid-item";
        internal Wizard Saved;
        internal Dictionary<string, WizClientObjectItem> Rows = [];
        internal WizClientObjectItem Stored => Rows[Document];
        internal WizItemTemplate Template = new() { m_templateID = TemplateId, m_adjectiveList = ["Hat"], m_baseCost = 40,
            m_numPrimaryColors = 10, m_numSecondaryColors = 10, m_numPatterns = 10 };
        internal int Cost = 40, SaveAttempts; internal bool IsPet, ValidName = true, FailSave, Durable;
        internal string? SerializationFailure; internal System.Action? BeforeSave; internal ItemSession? Working;
        internal readonly List<PropertyFlags> Masks = [];
        internal Fixture(bool equipped = false, bool pet = false) {
            Saved = new() { CharId = Owner, GameStats = new(default, 1) { m_currentGold = 100, m_baseGoldPouch = 200 },
                InventoryBehavior = new() { InventoryItemIds = equipped ? [] : [Id], Items = [] },
                EquipmentBehavior = new() { EquippedItemIds = equipped ? [Id] : [], EquippedItems = [], SlotList = [] },
                StorageBehavior = new() { BankItemIds = [], Items = [] },
                SpellbookBehavior = new() { TreasureCardTemplateIds = [], DeckTreasureCards = [] } };
            Rows[Document] = new() { m_globalID = Id, m_permID = Id, m_characterId = Owner, m_templateID = TemplateId,
                m_primaryColor = 1, m_secondaryColor = 2, m_pattern = 3, m_inactiveBehaviors = pet
                    ? [new ClientPetNameBehavior { m_nameKeys = OldName }, new ClientPetItemBehavior { m_level = 3,
                        m_firstName = 1, m_middleName = 0, m_lastName = 1, m_hatchedTimeSecs = 345 }, new ClientTimedItemBehavior { m_expireTime = 567 }]
                    : [] };
        }
        internal void Fill() {
            for (ulong id = 1; Saved.InventoryBehavior.InventoryItemIds.Count < ServerWizInventoryBehavior.MaxItemsAllowed; id++) {
                Saved.InventoryBehavior.InventoryItemIds.Add(id);
                Rows["original/" + id] = new() { m_globalID = id, m_characterId = Owner, m_templateID = TemplateId, m_inactiveBehaviors = [] };
            }
        }
        internal WizClientObjectItem Target(Wizard live) => live.InventoryBehavior.GetItem(Id) ?? live.EquipmentBehavior.GetItem(Id);
        internal Wizard Live() {
            var live = CloneWizard(Saved);
            live.InventoryBehavior.Items = [..Rows.Values.Where(item => live.InventoryBehavior.InventoryItemIds.Contains(item.m_globalID.Full)).Select(CloneItem)];
            live.EquipmentBehavior.EquippedItems = [..Rows.Values.Where(item => live.EquipmentBehavior.EquippedItemIds.Contains(item.m_globalID.Full)).Select(CloneItem)];
            return live;
        }
        internal bool Dye(Wizard live, out PaidItemChangeReceipt receipt, int primary = 7, int secondary = 8, int pattern = 9)
            => ClassicPaidItemChanges.Dye(live, Id, primary, secondary, pattern, out receipt,
                id => { Assert.Equal((ulong)TemplateId, id); return Template; },
                (template, first, second) => { Assert.Same(Template, template); Assert.Equal(primary, first); Assert.Equal(secondary, second); return Cost; },
                _ => IsPet, Serialize);
        internal bool Rename(Wizard live, out PaidItemChangeReceipt receipt)
            => ClassicPaidItemChanges.Rename(live, Id, NewName, out receipt, keys => ValidName && keys == NewName, () => Cost);
        private ByteString Serialize(PropertyClass item, PropertyFlags mask) {
            Masks.Add(mask);
            if (SerializationFailure == "throw") throw new InvalidOperationException("fixture cannot prepare native bytes");
            if (item is WizClientObjectItem original) {
                Assert.Equal(ClassicPaidItemChanges.EquippedItemMask, mask);
                Assert.Equal((7, 8, 9), (original.m_primaryColor, original.m_secondaryColor, original.m_pattern));
                return SerializationFailure == "local" ? default : new ByteString(new byte[] { 7, 8, 9 });
            }
            var appearance = Assert.IsType<WizardEquippedItemInfo>(item);
            Assert.Equal((PropertyFlags)1, mask); Assert.Equal((Bui5)7, appearance.m_baseColor);
            Assert.Equal((Bui5)8, appearance.m_trimColor); Assert.Equal((Bui5)9, appearance.m_pattern);
            return SerializationFailure == "public" ? default : new ByteString(new byte[] { 17, 18, 19 });
        }
        internal IDisposable Scope() {
            var oldStore = WizardCollection.TestStoreScope.Value; var oldRows = WizardInventoryTransactions.TestRowsScope.Value;
            WizardCollection.TestStoreScope.Value = new(Open, (session, _) => Session(session).Wizard);
            WizardInventoryTransactions.TestRowsScope.Value = session => Session(session).Rows.Values.ToList();
            return new Restore(() => { WizardCollection.TestStoreScope.Value = oldStore; WizardInventoryTransactions.TestRowsScope.Value = oldRows; });
        }
        private IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, ItemSession>(); var proxy = Session(session); Working = proxy;
            proxy.Wizard = CloneWizard(Saved); proxy.Rows = Rows.ToDictionary(pair => pair.Key, pair => CloneItem(pair.Value));
            proxy.Save = () => {
                Assert.True(WizardCollection.HoldsWriteLane); SaveAttempts++; BeforeSave?.Invoke();
                if (FailSave && !Durable) throw new InvalidOperationException("fixture refused modification");
                Saved = CloneWizard(proxy.Wizard); Rows = proxy.Rows.ToDictionary(pair => pair.Key, pair => CloneItem(pair.Value));
                if (FailSave) throw new InvalidOperationException("fixture lost modification acknowledgement");
            };
            return session;
        }
        private static ItemSession Session(IDocumentSession session) => (ItemSession)(object)session;
        internal static WizClientObjectItem CloneItem(WizClientObjectItem item) => item with {
            m_inactiveBehaviors = item.m_inactiveBehaviors.Select(behavior => behavior switch {
                ClientPetNameBehavior name => name with { }, ClientPetItemBehavior pet => pet with { },
                ClientTimedItemBehavior timer => timer with { },
                DeckBehavior deck => deck with { m_spellList = deck.m_spellList?.Select(card => card with { }).ToList() },
                _ => behavior,
            }).ToList(),
        };
        private static Wizard CloneWizard(Wizard wizard) => new() { CharId = wizard.CharId,
            GameStats = wizard.GameStats.CloneSnapshotWithGold(wizard.GameStats.m_currentGold),
            InventoryBehavior = new() { InventoryItemIds = [..wizard.InventoryBehavior.InventoryItemIds], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [..wizard.EquipmentBehavior.EquippedItemIds], EquippedItems = [],
                SlotList = wizard.EquipmentBehavior.SlotList.Select(slot => new EquipmentSlot { SlotType = slot.SlotType, ItemId = slot.ItemId }).ToList() },
            StorageBehavior = new() { BankItemIds = [..wizard.StorageBehavior.BankItemIds], Items = [] },
            SpellbookBehavior = new() { TreasureCardTemplateIds = [..wizard.SpellbookBehavior.TreasureCardTemplateIds],
                DeckTreasureCards = ServerWizSpellbookBehavior.CopyLedger(wizard.SpellbookBehavior.DeckTreasureCards) } };
        private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
    }

    public class ItemSession : DispatchProxy {
        internal Wizard Wizard = null!; internal Dictionary<string, WizClientObjectItem> Rows = [];
        internal WizClientObjectItem Target => Rows[Fixture.Document]; internal System.Action Save = null!;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, ItemInventoryPersistenceTests.ItemAdvanced>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "get_Advanced" => _advanced, "SaveChanges" => SaveNow(), "Dispose" => null,
            _ => throw new NotSupportedException(method.Name),
        };
        private object? SaveNow() { Save(); return null; }
    }
}
