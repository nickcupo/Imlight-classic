// CLASSIC: ordinary inventory APIs use fresh tracked rows and one ACK before publishing a backpack change.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ItemInventoryPersistenceTests {
    public ItemInventoryPersistenceTests()
        => EquipmentAttachConcurrencyTests.Configure("[Character]\nMaxInventoryItems=150\n[Classic]\nBackpackSize=2\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");

    [Fact]
    public void OrdinaryGrantSavesRowAndReferenceOnceBeforePublishingAnyLiveChange() {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); var candidate = Fixture.Item(11);
        candidate.m_characterId = 0; candidate.m_primaryColor = 7; candidate.m_secondaryColor = 9; candidate.m_pattern = 11;
        var originalBehaviors = candidate.m_inactiveBehaviors;
        f.BeforeSave = () => {
            Assert.Empty(live.InventoryBehavior.InventoryItemIds); Assert.Empty(live.InventoryBehavior.Items);
            Assert.Equal(0ul, candidate.m_characterId.Full); Assert.Same(originalBehaviors, candidate.m_inactiveBehaviors);
        };
        Assert.True(live.AddItemToInventory(candidate));
        Assert.Same(candidate, Assert.Single(live.InventoryBehavior.Items)); Assert.Equal(Fixture.Char, candidate.m_characterId.Full);
        var row = Assert.Single(f.Items.Values); Assert.Equal(11ul, row.m_globalID.Full); Assert.Equal(Fixture.Char, row.m_characterId.Full);
        Assert.Equal((7, 9, 11), (row.m_primaryColor, row.m_secondaryColor, row.m_pattern));
        Assert.Equal(11ul, Assert.Single(f.Saved.InventoryBehavior.InventoryItemIds));
        Assert.Equal(1, f.SaveAttempts); Assert.Equal(1, f.Stored); Assert.Equal(1, f.Initialized);
        Assert.Equal(900, f.Saved.GameStats.m_currentGold); Assert.Equal(100, f.Saved.GameStats.m_currentArenaPoints);
    }

    [Fact]
    public void PreparedPetStateSurvivesWithoutAnyTemplateReinitialization() {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        var petState = new ClientPetItemBehavior { m_level = 4, m_hatchedTimeSecs = 123456 };
        var timer = new ClientTimedItemBehavior { m_expireTime = 2468 };
        var candidate = Fixture.Item(12); candidate.m_inactiveBehaviors = [petState, timer];
        Assert.True(live.AddPetToInventory(candidate));
        Assert.Equal(0, f.Initialized); Assert.Same(candidate, Assert.Single(live.InventoryBehavior.Items));
        var pet = Assert.IsType<ClientPetItemBehavior>(f.Items.Values.Single().m_inactiveBehaviors[0]);
        Assert.Equal((4, 123456u), ((int)pet.m_level, pet.m_hatchedTimeSecs));
        Assert.Equal(2468u, Assert.IsType<ClientTimedItemBehavior>(f.Items.Values.Single().m_inactiveBehaviors[1]).m_expireTime);
        Assert.Same(petState, candidate.m_inactiveBehaviors[0]); Assert.Equal(1, f.SaveAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReagentObjectCannotEnterTheOrdinaryBackpackThroughItemOrPetGrant(bool pet) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        WizClientObjectItem candidate = new ClientReagentItem { m_globalID = 13, m_templateID = Fixture.Template,
            m_characterId = Fixture.Char, m_quantity = 5, m_inactiveBehaviors = [] };
        Assert.False(pet ? live.AddPetToInventory(candidate) : live.AddItemToInventory(candidate));
        Assert.Empty(live.InventoryBehavior.Items); Assert.Empty(live.InventoryBehavior.InventoryItemIds);
        Assert.Empty(f.Items); Assert.Empty(f.Saved.InventoryBehavior.InventoryItemIds);
        Assert.Equal(5, ((ClientReagentItem)candidate).m_quantity); Assert.Equal(0, f.Initialized);
        Assert.Equal(0, f.Stored); Assert.Equal(0, f.SaveAttempts); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Fact]
    public void SessionStagingRefusesAReagentCandidateBeforeAnyRowOrReferenceChanges() {
        var f = new Fixture(); using var scope = f.Scope(); var validated = true;
        WizClientObjectItem reagent = new ClientReagentItem { m_globalID = 13, m_templateID = Fixture.Template,
            m_characterId = Fixture.Char, m_quantity = 5 };
        Assert.False(WizardCollection.CommitCharacterMutation(Fixture.Char, (session, saved) =>
            WizardInventoryTransactions.TryStageGrants(session, saved, [reagent], out _, out validated, out _),
            _ => throw new InvalidOperationException("refused candidate must never publish")));
        Assert.False(validated); Assert.Empty(f.Items); Assert.Empty(f.Saved.InventoryBehavior.InventoryItemIds);
        Assert.Equal(0, f.Stored); Assert.Equal(0, f.SaveAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshSavedCapacityRefusesAStaleEmptyLiveBagForNormalItemsAndPets(bool pet) {
        var f = new Fixture(); f.AddExisting(11); f.AddExisting(12); using var scope = f.Scope(); var live = f.Live();
        live.InventoryBehavior.InventoryItemIds.Clear(); live.InventoryBehavior.Items = [];
        var candidate = Fixture.Item(13); candidate.m_characterId = 0;
        Assert.False(pet ? live.AddPetToInventory(candidate) : live.AddItemToInventory(candidate));
        Assert.Equal(0, f.SaveAttempts); Assert.Equal(0, f.Stored); Assert.Equal(2, f.Items.Count);
        Assert.Equal(new ulong[] { 11, 12 }, f.Saved.InventoryBehavior.InventoryItemIds); Assert.Empty(live.InventoryBehavior.Items);
        Assert.Equal(0ul, candidate.m_characterId.Full); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("zero-id")]
    [InlineData("zero-template")]
    [InlineData("foreign-collision")]
    [InlineData("orphan-collision")]
    public void InvalidCandidatesCannotAdoptOrOverwriteAnyExistingRow(string invalid) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); var candidate = Fixture.Item(13);
        if (invalid == "foreign") candidate.m_characterId = Fixture.Char + 1;
        else if (invalid == "zero-id") candidate.m_globalID = 0;
        else if (invalid == "zero-template") candidate.m_templateID = 0;
        else f.Items["original/orphan"] = candidate with { m_characterId = invalid == "foreign-collision" ? Fixture.Char + 1 : Fixture.Char };
        var originalKeys = f.Items.Keys.ToArray();
        Assert.False(live.AddPetToInventory(candidate)); Assert.Equal(originalKeys, f.Items.Keys);
        Assert.Empty(live.InventoryBehavior.Items); Assert.Empty(f.Saved.InventoryBehavior.InventoryItemIds);
        Assert.Equal(0, f.Stored); Assert.Equal(0, f.SaveAttempts); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("duplicate-row")]
    [InlineData("duplicate-reference")]
    [InlineData("bank-overlap")]
    [InlineData("equipment-overlap")]
    public void CorruptSavedBackpackRefusesBeforeMutationEvenWhenItAppearsFull(string defect) {
        var f = new Fixture(); f.AddExisting(11); f.AddExisting(12);
        switch (defect) {
            case "missing": f.Items.Remove("original/11"); break;
            case "foreign": f.Items["original/11"].m_characterId = Fixture.Char + 1; break;
            case "duplicate-row": f.Items["duplicate/11"] = f.Items["original/11"] with { }; break;
            case "duplicate-reference": f.Saved.InventoryBehavior.InventoryItemIds.Add(11); break;
            case "bank-overlap": f.Saved.StorageBehavior.BankItemIds.Add(11); break;
            case "equipment-overlap": f.Saved.EquipmentBehavior.EquippedItemIds.Add(11); break;
        }
        using var scope = f.Scope(); var live = f.Live(); var original = f.Saved.InventoryBehavior.InventoryItemIds.ToArray();
        Assert.False(live.AddPetToInventory(Fixture.Item(13))); Assert.False(live.DestroyInventoryItem(11));
        Assert.Equal(original, f.Saved.InventoryBehavior.InventoryItemIds); Assert.Equal(original, live.InventoryBehavior.InventoryItemIds);
        Assert.Equal(0, f.Stored); Assert.Equal(0, f.Deleted); Assert.Equal(0, f.SaveAttempts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedOrLostGrantAckLeavesCallerAndLiveUnchangedAndNeverRetries(bool pet, bool committed) {
        var f = new Fixture { FailSave = true, CommitBeforeFailure = committed }; using var scope = f.Scope();
        var live = f.Live(); var candidate = Fixture.Item(13); candidate.m_characterId = 0;
        var originalBehaviors = candidate.m_inactiveBehaviors;
        Assert.Throws<InvalidOperationException>(() => pet ? live.AddPetToInventory(candidate) : live.AddItemToInventory(candidate));
        Assert.Empty(live.InventoryBehavior.InventoryItemIds); Assert.Empty(live.InventoryBehavior.Items);
        Assert.Equal(0ul, candidate.m_characterId.Full); Assert.Same(originalBehaviors, candidate.m_inactiveBehaviors);
        Assert.Equal(committed ? 1 : 0, f.Items.Count); Assert.Equal(committed ? 1 : 0, f.Saved.InventoryBehavior.InventoryItemIds.Count);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.False(live.AddPetToInventory(candidate)); Assert.False(live.AddItemToInventory(candidate)); Assert.Equal(1, f.SaveAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovalUsesFreshSavedReferencesAndOriginalDocumentIdWithOneSave(bool destroy) {
        var f = new Fixture(); f.AddExisting(11); f.AddExisting(12); f.AddExisting(13); using var scope = f.Scope();
        var live = f.Live(); var keptAlias = live.InventoryBehavior.Items[1];
        f.Items["original/12"].m_primaryColor = 77; // a saved metadata change the attached cache has not seen
        f.BeforeSave = () => Assert.Equal(3, live.InventoryBehavior.InventoryItemIds.Count);
        Assert.True(destroy ? live.DestroyInventoryItem(11) : live.RemoveItemFromInventory(11));
        Assert.Equal(new ulong[] { 12, 13 }, f.Saved.InventoryBehavior.InventoryItemIds); Assert.Same(keptAlias, live.InventoryBehavior.Items[0]);
        Assert.Equal(77, keptAlias.m_primaryColor);
        Assert.Equal(destroy ? new[] { "original/12", "original/13" } : new[] { "original/11", "original/12", "original/13" }, f.Items.Keys);
        Assert.Equal(destroy ? 1 : 0, f.Deleted); Assert.Equal(1, f.SaveAttempts);
        Assert.False(live.DestroyInventoryItem(11)); Assert.Equal(1, f.SaveAttempts); // not resurrected/adopted
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedOrLostRemovalAckNeverPublishesAnUnlinkOrRetries(bool destroy, bool committed) {
        var f = new Fixture { FailSave = true, CommitBeforeFailure = committed }; f.AddExisting(11); using var scope = f.Scope();
        var live = f.Live(); var alias = Assert.Single(live.InventoryBehavior.Items);
        Assert.Throws<InvalidOperationException>(() => destroy ? live.DestroyInventoryItem(11) : live.RemoveItemFromInventory(11));
        Assert.Same(alias, Assert.Single(live.InventoryBehavior.Items)); Assert.Equal(11ul, Assert.Single(live.InventoryBehavior.InventoryItemIds));
        Assert.Equal(committed ? 0 : 1, f.Saved.InventoryBehavior.InventoryItemIds.Count);
        Assert.Equal(committed && destroy ? 0 : 1, f.Items.Count); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.False(live.DestroyInventoryItem(11)); Assert.False(live.RemoveItemFromInventory(11)); Assert.Equal(1, f.SaveAttempts);
    }

    [Fact]
    public void DeckHeldTreasureCardsBlockDestructionButRemainOwnedDuringReferenceOnlyMoves() {
        var f = new Fixture(); f.AddExisting(11); f.Saved.SpellbookBehavior.DeckTreasureCards[11] = new() { [77] = 3 };
        using var scope = f.Scope(); var live = f.Live(); live.SpellbookBehavior.DeckTreasureCards.Clear();
        Assert.False(live.DestroyInventoryItem(11)); Assert.Equal(0, f.SaveAttempts); Assert.Single(f.Items);
        Assert.True(live.RemoveItemFromInventory(11)); Assert.Single(f.Items);
        Assert.Equal(3, f.Saved.SpellbookBehavior.DeckTreasureCards[11][77]); Assert.Equal(1, f.SaveAttempts);
    }

    [Theory]
    [InlineData("equipment")]
    [InlineData("bank")]
    [InlineData("housing-orphan")]
    public void ItemsOutsideTheFreshSavedBackpackCannotBeDestroyedFromAStaleLiveBag(string location) {
        var f = new Fixture(); f.AddExisting(11); var live = f.Live(); f.Saved.InventoryBehavior.InventoryItemIds.Clear();
        if (location == "equipment") f.Saved.EquipmentBehavior.EquippedItemIds.Add(11);
        if (location == "bank") f.Saved.StorageBehavior.BankItemIds.Add(11);
        using var scope = f.Scope(); Assert.False(live.DestroyInventoryItem(11));
        Assert.Single(f.Items); Assert.Equal("original/11", Assert.Single(f.Items.Keys)); Assert.Equal(0, f.SaveAttempts); Assert.Equal(0, f.Deleted);
    }

    [Fact]
    public void FullNativeIdsDoNotCollapseDistinctRowsWithTheSameLowBits() {
        var f = new Fixture(); var first = (7ul << 40) | 9; var second = (8ul << 40) | 9;
        f.AddExisting(first); using var scope = f.Scope(); var live = f.Live(); Assert.True(live.AddPetToInventory(Fixture.Item(second)));
        Assert.Equal(new[] { first, second }, f.Saved.InventoryBehavior.InventoryItemIds);
        Assert.Equal(new[] { first, second }, f.Items.Values.Select(item => item.m_globalID.Full)); Assert.Equal(1, f.SaveAttempts);
    }

    [Fact]
    public void AckRefreshesAllSavedMetadataOnExistingAliasesWithoutTouchingEquippedDeckAlias() {
        var f = new Fixture(); f.AddExisting(11); f.AddExisting(12);
        f.Items["original/12"].m_inactiveBehaviors = [new ClientPetItemBehavior { m_level = 4, m_hatchedTimeSecs = 100 },
            new ClientTimedItemBehavior { m_expireTime = 200 }];
        var deck = Fixture.Item(15); f.Items["equipment/deck"] = deck; f.Saved.EquipmentBehavior.EquippedItemIds.Add(15);
        using var scope = f.Scope(); var live = f.Live(); var alias = live.InventoryBehavior.Items[1];
        live.EquipmentBehavior.EquippedItems = [deck]; var equipmentList = live.EquipmentBehavior.EquippedItems;
        f.Items["original/12"].m_primaryColor = 71; f.Items["original/12"].m_secondaryColor = 72; f.Items["original/12"].m_pattern = 73;
        f.Items["original/12"].m_inactiveBehaviors = [new ClientPetItemBehavior { m_level = 5, m_hatchedTimeSecs = 300 },
            new ClientTimedItemBehavior { m_expireTime = 400 }];
        f.BeforeSave = () => Assert.Equal((4, 100u), ((int)Assert.IsType<ClientPetItemBehavior>(alias.m_inactiveBehaviors[0]).m_level,
            Assert.IsType<ClientPetItemBehavior>(alias.m_inactiveBehaviors[0]).m_hatchedTimeSecs));
        Assert.True(live.DestroyInventoryItem(11)); Assert.Same(alias, Assert.Single(live.InventoryBehavior.Items));
        Assert.Equal((71, 72, 73), (alias.m_primaryColor, alias.m_secondaryColor, alias.m_pattern));
        Assert.Equal((5, 300u), ((int)Assert.IsType<ClientPetItemBehavior>(alias.m_inactiveBehaviors[0]).m_level,
            Assert.IsType<ClientPetItemBehavior>(alias.m_inactiveBehaviors[0]).m_hatchedTimeSecs));
        Assert.Equal(400u, Assert.IsType<ClientTimedItemBehavior>(alias.m_inactiveBehaviors[1]).m_expireTime);
        Assert.Same(equipmentList, live.EquipmentBehavior.EquippedItems); Assert.Same(deck, Assert.Single(live.EquipmentBehavior.EquippedItems));
        Assert.Equal("equipment/deck", f.Items.Single(pair => pair.Value.m_globalID.Full == 15).Key); Assert.Equal(1, f.SaveAttempts);
    }

    [Fact]
    public void BatchValidatesARejectedSuffixBeforeAdmittingAnyFittingPrefix() {
        var f = new Fixture(); f.AddExisting(11); f.Items["foreign/13"] = Fixture.Item(13) with { m_characterId = Fixture.Char + 1 };
        using var scope = f.Scope(); var live = f.Live();
        Assert.False(WizardCollection.CommitCharacterMutation(Fixture.Char,
            (session, saved) => WizardInventoryTransactions.TryStageGrants(session, saved,
                [Fixture.Item(12), Fixture.Item(13)], out _, out _, out _), _ => throw new Exception("not reached")));
        Assert.Equal(0, f.Stored); Assert.Equal(0, f.SaveAttempts); Assert.Equal(11ul, Assert.Single(f.Saved.InventoryBehavior.InventoryItemIds));
        Assert.Equal(11ul, Assert.Single(live.InventoryBehavior.InventoryItemIds));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HatchParentLookupAcceptsExactlyOwnedBackpackOrEquipmentAndExcludesBank(bool equipped) {
        var f = new Fixture(); f.AddExisting(11);
        if (equipped) { f.Saved.InventoryBehavior.InventoryItemIds.Clear(); f.Saved.EquipmentBehavior.EquippedItemIds.Add(11); }
        using var scope = f.Scope(); using var session = f.Open(); var saved = Fixture.Session(session).Wizard;
        Assert.True(WizardInventoryTransactions.TryReadOwnedItem(session, saved, 11, out var item)); Assert.Equal(11ul, item.m_globalID.Full);
        saved.InventoryBehavior.InventoryItemIds.Clear(); saved.EquipmentBehavior.EquippedItemIds.Clear(); saved.StorageBehavior.BankItemIds.Add(11);
        Assert.False(WizardInventoryTransactions.TryReadOwnedItem(session, saved, 11, out _)); Assert.Equal(0, f.SaveAttempts);
    }

    [Fact]
    public void ProductionItemQueryRequestsEveryRowAndAnExplicitFreshIndexResult() {
        using var store = new DocumentStore { Urls = ["http://127.0.0.1:1"], Database = "item-query" }.Initialize();
        using var session = store.OpenSession(); var query = (IRavenQueryInspector)WizardInventoryTransactions.ItemQuery(session);
        var request = query.GetIndexQuery(false); Assert.True(request.WaitForNonStaleResults);
        Assert.Equal(TimeSpan.FromSeconds(5), request.WaitForNonStaleResultsTimeout);
        Assert.Contains(request.QueryParameters.Values, value => (value is int || value is long) && Convert.ToInt64(value) == int.MaxValue);
    }

    internal sealed class Fixture {
        internal const ulong Char = 775181, Template = 775182;
        internal Wizard Saved = new() { CharId = Char,
            GameStats = new ServerWizGameStats(default, 1) { m_currentGold = 900, m_currentArenaPoints = 100 },
            InventoryBehavior = new() { InventoryItemIds = [], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = [] }, StorageBehavior = new() { BankItemIds = [], Items = [] },
            SpellbookBehavior = new() { TreasureCardTemplateIds = [], DeckTreasureCards = [] },
        };
        internal Dictionary<string, WizClientObjectItem> Items = [];
        internal bool FailSave, CommitBeforeFailure; internal int SaveAttempts, Stored, Deleted, Initialized;
        internal System.Action? BeforeSave;
        internal static WizClientObjectItem Item(ulong id) => new() { m_globalID = id, m_permID = id,
            m_templateID = Template, m_characterId = Char, m_inactiveBehaviors = [] };
        internal void AddExisting(ulong id) { Items["original/" + id] = Item(id); Saved.InventoryBehavior.InventoryItemIds.Add(id); }
        internal Wizard Live() {
            var live = Clone(Saved); live.InventoryBehavior.Items = [..Items.Values.Where(item => live.InventoryBehavior.InventoryItemIds.Contains(item.m_globalID.Full)).Select(item => item with { })]; return live;
        }
        internal IDisposable Scope() {
            var oldStore = WizardCollection.TestStoreScope.Value; var oldRows = WizardInventoryTransactions.TestRowsScope.Value;
            var oldInitialize = WizardInventoryTransactions.TestInitializeScope.Value;
            var cache = (IDictionary<ulong, CoreTemplate>)typeof(Imlight.CoreLib.Shared.Resources.CoreObjectFactory)
                .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var oldTemplate = cache.TryGetValue(Template, out var previousTemplate) ? previousTemplate : null;
            cache[Template] = new WizItemTemplate { m_templateID = (uint)Template, m_adjectiveList = ["Hat"], m_behaviors = [] };
            WizardCollection.TestStoreScope.Value = new(Open, (session, id) => { Assert.Equal(Char, id); return Session(session).Wizard; });
            WizardInventoryTransactions.TestRowsScope.Value = session => Session(session).Items.Values.ToList();
            WizardInventoryTransactions.TestInitializeScope.Value = item => { Initialized++; item.m_inactiveBehaviors = [new ClientTimedItemBehavior { m_expireTime = 77 }]; };
            return new Restore(() => { WizardCollection.TestStoreScope.Value = oldStore; WizardInventoryTransactions.TestRowsScope.Value = oldRows;
                WizardInventoryTransactions.TestInitializeScope.Value = oldInitialize;
                if (oldTemplate is null) cache.Remove(Template); else cache[Template] = oldTemplate;
            });
        }
        internal IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, ItemSession>(); var proxy = Session(session);
            proxy.Wizard = Clone(Saved); proxy.Items = Items.ToDictionary(pair => pair.Key, pair => pair.Value with { });
            proxy.Store = item => { Stored++; proxy.Items["new/" + item.m_globalID.Full] = item; };
            proxy.Delete = item => { Deleted++; proxy.Items.Remove(proxy.Items.Single(pair => ReferenceEquals(pair.Value, item)).Key); };
            proxy.Save = () => {
                Assert.True(WizardCollection.HoldsWriteLane); SaveAttempts++; BeforeSave?.Invoke();
                if (FailSave && !CommitBeforeFailure) throw new InvalidOperationException("fixture refused item write");
                Saved = Clone(proxy.Wizard); Items = proxy.Items.ToDictionary(pair => pair.Key, pair => pair.Value with { });
                if (FailSave) throw new InvalidOperationException("fixture lost item acknowledgement");
            };
            return session;
        }
        internal static ItemSession Session(IDocumentSession session) => (ItemSession)(object)session;
        private static Wizard Clone(Wizard saved) => new() { CharId = saved.CharId,
            GameStats = saved.GameStats.CloneSnapshotWithGold(saved.GameStats.m_currentGold),
            InventoryBehavior = new() { InventoryItemIds = [..saved.InventoryBehavior.InventoryItemIds], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [..saved.EquipmentBehavior.EquippedItemIds], EquippedItems = [] },
            StorageBehavior = new() { BankItemIds = [..saved.StorageBehavior.BankItemIds], Items = [] },
            SpellbookBehavior = new() { TreasureCardTemplateIds = [..saved.SpellbookBehavior.TreasureCardTemplateIds],
                DeckTreasureCards = ServerWizSpellbookBehavior.CopyLedger(saved.SpellbookBehavior.DeckTreasureCards) },
        };
        private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
    }
    public class ItemSession : DispatchProxy {
        internal Wizard Wizard = null!; internal Dictionary<string, WizClientObjectItem> Items = [];
        internal Action<WizClientObjectItem> Store = null!, Delete = null!; internal System.Action Save = null!;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, ItemAdvanced>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            switch (method!.Name) {
                case "get_Advanced": return _advanced;
                case "Store": Store(Assert.IsType<WizClientObjectItem>(args![0])); return null;
                case "Delete": Delete(Assert.IsType<WizClientObjectItem>(args![0])); return null;
                case "SaveChanges": Save(); return null;
                case "Dispose": return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
    public class ItemAdvanced : DispatchProxy {
        private readonly IMetadataDictionary _metadata = DispatchProxy.Create<IMetadataDictionary, ItemMetadata>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "set_OptimisticConcurrencyMode" => null, "GetMetadataFor" => _metadata, _ => throw new NotSupportedException(method.Name),
        };
    }
    public class ItemMetadata : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            Assert.Equal("set_Item", method!.Name); Assert.Equal(Raven.Client.Constants.Documents.Metadata.Collection, args![0]);
            Assert.Equal(WizardItemCollection.CollectionName, args[1]); return null;
        }
    }
}
