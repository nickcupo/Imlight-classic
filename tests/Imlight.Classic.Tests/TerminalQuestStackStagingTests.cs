// CLASSIC: terminal claims own the enclosing save, while stack preparation/staging remain composable.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class TerminalQuestStackStagingTests {
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ValidEmptyOrSkippedUnknownRewardsStageAZeroReceiptWithoutSaving(bool unknown) {
        using var f = new Fixture();
        Assert.True(ClassicStackRewards.TryPrepare(f.Live, unknown ? [Drop(987654, 1)] : [], [], [], out var prepared));
        Assert.True(ClassicStackRewards.TryStage(f.Session, f.Saved, prepared, out var staged));
        Assert.False(staged.HasRewards); Assert.Empty(staged.Receipt.Items); Assert.Empty(staged.Receipt.Cards);
        Assert.Empty(staged.Receipt.Reagents); Assert.False(staged.Receipt.BackpackCapacityExceeded);
        Assert.Equal(0, f.Proxy.Saves); Assert.Empty(f.Proxy.Ignored);
    }

    [Fact]
    public void AFullFreshBackpackBookAndBagRemainAValidZeroWithoutPublishingStaleLiveCounts() {
        using var f = new Fixture(2, 999, 999); f.Live.SpellbookBehavior.TreasureCardTemplateIds.Clear();
        f.Live.AlchemyBehavior.Reagents[0].m_quantity = 1; f.Live.InventoryBehavior.InventoryItemIds.Clear();
        Assert.True(f.Prepare(out var prepared)); Assert.True(ClassicStackRewards.TryStage(f.Session, f.Saved, prepared, out var staged));
        Assert.False(staged.HasRewards); Assert.True(staged.Receipt.BackpackCapacityExceeded);
        Assert.Empty(staged.Receipt.Items); Assert.Empty(staged.Receipt.Cards); Assert.Empty(staged.Receipt.Reagents);
        Assert.Equal(999, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count); Assert.Equal(999, f.Proxy.Rows[0].m_quantity);
        Assert.Equal(1, f.Live.AlchemyBehavior.Reagents[0].m_quantity); Assert.Empty(f.Live.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(0, f.Proxy.Saves); Assert.Empty(f.Proxy.Ignored);
    }

    [Fact]
    public void ExistingStandaloneWrapperRetainsFalseNoSaveAndFullBackpackFlagForAValidZero() {
        using var f = new Fixture(2, 999, 999);
        Assert.False(ClassicStackRewards.TryGrant(f.Live, [Drop(Fixture.Gear, 1)], [Fixture.Card],
            [Drop(Fixture.Reagent, 3)], out var receipt));
        Assert.True(receipt.BackpackCapacityExceeded); Assert.Empty(receipt.Items); Assert.Empty(receipt.Cards);
        Assert.Empty(receipt.Reagents); Assert.Equal(0, f.Proxy.Saves);
        Assert.Equal(999, f.Live.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(999, Assert.Single(f.Live.AlchemyBehavior.Reagents).m_quantity);
    }

    [Theory]
    [InlineData("foreign-prepared-owner")] [InlineData("foreign-item")]
    [InlineData("duplicate-item")] [InlineData("foreign-row")]
    [InlineData("missing-reference")] [InlineData("duplicate-reagent")]
    public void EvenAFullRewardMustValidateAllIdentitiesBeforeAdmittingItsCardPart(string defect) {
        using var f = new Fixture(2, 0, 999); Assert.True(f.Prepare(out var prepared));
        if (defect == "foreign-prepared-owner") prepared = prepared with { OwnerCharId = Fixture.Character + 1 };
        if (defect == "foreign-item") prepared = prepared with { Items = [prepared.Items[0] with { m_characterId = Fixture.Character + 1 }] };
        if (defect == "duplicate-item") prepared = prepared with { Items = [prepared.Items[0], prepared.Items[0]] };
        if (defect == "foreign-row") f.Proxy.Rows[0].m_characterId = Fixture.Character + 1;
        if (defect == "missing-reference") f.Proxy.Rows.Clear();
        if (defect == "duplicate-reagent") f.Proxy.Rows.Add(f.Proxy.Rows[0] with { });
        Assert.False(ClassicStackRewards.TryStage(f.Session, f.Saved, prepared, out var staged)); Assert.Null(staged);
        Assert.Empty(f.Saved.SpellbookBehavior.TreasureCardTemplateIds); Assert.Empty(f.Live.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(0, f.Proxy.Saves); Assert.Empty(f.Proxy.Ignored);
    }

    [Theory]
    [InlineData("item", false)] [InlineData("item", true)]
    [InlineData("reagent", false)] [InlineData("reagent", true)]
    public void NativeByteFailureRefusesTheWholeStagedGroupWithoutSavingProtectingOrPublishing(string kind, bool throws) {
        using var f = new Fixture(0, 0, 5); var alias = f.Live.AlchemyBehavior.Reagents[0];
        ByteString Fail() => throws ? throw new InvalidOperationException("authored stack native-byte refusal") : new ByteString();
        if (kind == "item") f.Dependencies.SerializeItem = _ => Fail();
        else f.Dependencies.SerializeReagent = _ => Fail();
        Assert.True(f.Prepare(out var prepared)); Assert.False(ClassicStackRewards.TryStage(f.Session, f.Saved, prepared, out var staged));
        Assert.Null(staged); Assert.Empty(f.Live.InventoryBehavior.InventoryItemIds); Assert.Empty(f.Live.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Same(alias, Assert.Single(f.Live.AlchemyBehavior.Reagents)); Assert.Equal(5, alias.m_quantity);
        Assert.Equal(0, f.Proxy.Saves); Assert.Empty(f.Proxy.Ignored); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("zero-id")] [InlineData("wrong-template")] [InlineData("foreign-owner")]
    public void FailedNativeObjectPreparationDiffersFromAValidUnknownOrEmptyReward(string defect) {
        using var f = new Fixture();
        f.Dependencies.Create = _ => new WizClientObjectItem { m_globalID = defect == "zero-id" ? 0ul : Fixture.NewItem,
            m_templateID = defect == "wrong-template" ? Fixture.Gear + 1 : Fixture.Gear,
            m_characterId = defect == "foreign-owner" ? Fixture.Character + 1 : Fixture.Character, m_inactiveBehaviors = [] };
        Assert.False(f.Prepare(out var prepared)); Assert.Null(prepared); Assert.Equal(0, f.Proxy.Saves); Assert.Empty(f.Proxy.Ignored);
    }

    [Fact]
    public void OneOuterAcknowledgementPublishesOnlyAdmittedCountsAndPreservesExistingRuntimeAliases() {
        using var f = new Fixture(1, 998, 998); var stats = f.Live.GameStats; var bag = f.Live.InventoryBehavior;
        var book = f.Live.SpellbookBehavior; var alchemy = f.Live.AlchemyBehavior;
        var reagent = Assert.Single(alchemy.Reagents); reagent.m_quantity = 12;
        var oldItem = Assert.Single(bag.Items); f.Live.GameStats.m_currentGold = 777;
        Assert.True(f.Prepare(out var prepared)); Assert.True(ClassicStackRewards.TryStage(f.Session, f.Saved, prepared, out var staged));
        Assert.True(staged.HasRewards); Assert.Single(staged.Receipt.Items); Assert.Single(staged.Receipt.Cards);
        var acquired = Assert.Single(staged.Receipt.Reagents); Assert.Equal(1, acquired.Acquired); Assert.Equal(999, acquired.Reagent.m_quantity);
        Assert.Equal(999, BitConverter.ToInt32((byte[])acquired.Data));
        Assert.Equal(0, f.Proxy.Saves); Assert.Empty(f.Proxy.Ignored); Assert.Equal(12, reagent.m_quantity);
        Assert.Equal(998, book.TreasureCardTemplateIds.Count); Assert.Single(bag.InventoryItemIds);
        var originals = f.Proxy.Items.Where(item => item.m_globalID.Full != Fixture.NewItem).ToArray();
        WizardInventoryTransactions.ProtectUnmodifiedRows(f.Session);
        Assert.Equal(originals.Length, f.Proxy.Ignored.Count); Assert.All(originals, item => Assert.Contains(item, f.Proxy.Ignored));
        Assert.DoesNotContain(staged.Receipt.Items[0].Item, f.Proxy.Ignored);
        var receipt = WizardCollection.WithCharacterLock(Fixture.Character, () => {
            f.Session.SaveChanges(); return ClassicStackRewards.Publish(f.Live, f.Saved, staged);
        });
        Assert.Equal(1, f.Proxy.Saves); Assert.Same(stats, f.Live.GameStats); Assert.Same(bag, f.Live.InventoryBehavior);
        Assert.Same(book, f.Live.SpellbookBehavior); Assert.Same(alchemy, f.Live.AlchemyBehavior);
        Assert.Same(reagent, Assert.Single(alchemy.Reagents)); Assert.Equal(999, reagent.m_quantity);
        Assert.Same(oldItem, bag.Items.First(item => item.m_globalID.Full == oldItem.m_globalID.Full));
        Assert.Equal(2, bag.InventoryItemIds.Count); Assert.Equal(999, book.TreasureCardTemplateIds.Count);
        Assert.Equal(777, stats.m_currentGold); Assert.Equal(901, f.Saved.GameStats.m_currentGold);
        Assert.Equal(1, Assert.Single(receipt.Reagents).Acquired); Assert.Equal(Fixture.Card, Assert.Single(receipt.Cards).TemplateId);
    }

    private static DropItemResult Drop(ulong template, int quantity) => new() { ItemId = template.ToString(), Quantity = quantity };

    private sealed class Fixture : IDisposable {
        internal const ulong Character = 781000, Gear = 781001, Reagent = 781002, ReagentId = 781003, NewItem = 781004;
        internal const uint Card = 781005;
        internal readonly IDocumentSession Session;
        internal StageSession Proxy => (StageSession)(object)Session;
        internal readonly Wizard Saved, Live;
        internal readonly StackRewardDependencies Dependencies;
        private readonly StackRewardDependencies? _previous;
        private readonly Func<IDocumentSession, List<WizClientObjectItem>>? _previousItems;
        private readonly Func<IDocumentSession, List<ClientReagentItem>>? _previousRows;
        private readonly WizardCollection.TestStore? _previousStore;
        internal Fixture(int items = 0, int cards = 0, int reagent = 5) {
            EquipmentAttachConcurrencyTests.Configure("[Character]\nMaxInventoryItems=2\n[Classic]\nBackpackSize=2\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");
            Saved = WizardWith(items, cards); Live = WizardWith(items, cards);
            Session = DispatchProxy.Create<IDocumentSession, StageSession>();
            Proxy.Rows.Add(new ClientReagentItem { m_globalID = ReagentId, m_templateID = Reagent, m_characterId = Character, m_quantity = reagent });
            Live.AlchemyBehavior.Reagents.Add(Proxy.Rows[0] with { });
            for (var index = 0; index < items; index++) { var item = new WizClientObjectItem { m_globalID = (ulong)(781010 + index),
                m_templateID = Gear, m_characterId = Character, m_inactiveBehaviors = [] }; Proxy.Items.Add(item); Live.InventoryBehavior.Items.Add(item with { }); }
            // A read-only native original can be normalized by a real JSON session; the outer finalizer protects it.
            Proxy.Items.Add(new WizClientObjectItem { m_globalID = 781020, m_templateID = Gear, m_characterId = Character, m_inactiveBehaviors = [] });
            Dependencies = new() { Template = id => id == Gear ? new WizItemTemplate { m_templateID = (uint)Gear, m_behaviors = [], m_adjectiveList = ["Hat"] }
                : id == Card ? new SpellTemplate { m_name = "Authored terminal card" }
                : id == Reagent ? new ReagentItemTemplate { m_templateID = (uint)Reagent } : null!,
                Create = id => id == Gear ? new WizClientObjectItem { m_globalID = NewItem, m_templateID = Gear, m_inactiveBehaviors = [] }
                    : new ClientReagentItem { m_globalID = ReagentId, m_templateID = Reagent, m_characterId = Character },
                SerializeItem = item => new ByteString(BitConverter.GetBytes(item.m_globalID.Full)),
                SerializeReagent = row => new ByteString(BitConverter.GetBytes(row.m_quantity)) };
            _previous = ClassicStackRewards.TestScope.Value; _previousItems = WizardInventoryTransactions.TestRowsScope.Value;
            _previousRows = WizardReagentCollection.TestRowsScope.Value;
            _previousStore = WizardCollection.TestStoreScope.Value;
            ClassicStackRewards.TestScope.Value = Dependencies; WizardInventoryTransactions.TestRowsScope.Value = _ => Proxy.Items;
            WizardReagentCollection.TestRowsScope.Value = _ => Proxy.Rows;
            WizardCollection.TestStoreScope.Value = new(() => Session, (_, id) => id == Character ? Saved : null!);
        }
        internal bool Prepare(out PreparedStackRewards prepared)
            => ClassicStackRewards.TryPrepare(Live, [Drop(Gear, 99)], [Card, Card, Card], [Drop(Reagent, 3)], out prepared);
        private static Wizard WizardWith(int items, int cards) => new() { CharId = Character,
            GameStats = new(default, 1) { m_currentGold = 901 },
            SpellbookBehavior = new() { TreasureCardTemplateIds = Enumerable.Repeat(Card, cards).ToList() },
            AlchemyBehavior = new() { ReagentItemIds = [ReagentId], Reagents = [] },
            InventoryBehavior = new() { InventoryItemIds = Enumerable.Range(781010, items).Select(id => (ulong)id).ToList(), Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = [] }, StorageBehavior = new() { BankItemIds = [], Items = [] } };
        public void Dispose() { ClassicStackRewards.TestScope.Value = _previous; WizardInventoryTransactions.TestRowsScope.Value = _previousItems;
            WizardReagentCollection.TestRowsScope.Value = _previousRows; WizardCollection.TestStoreScope.Value = _previousStore; Session.Dispose(); }
    }
    public class StageSession : DispatchProxy {
        internal readonly List<WizClientObjectItem> Items = [];
        internal readonly List<ClientReagentItem> Rows = [];
        internal readonly HashSet<object> Ignored = new(ReferenceEqualityComparer.Instance);
        internal int Saves;
        private IAdvancedSessionOperations? _advanced;
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            switch (method!.Name) {
                case "get_Advanced":
                    if (_advanced is null) { _advanced = DispatchProxy.Create<IAdvancedSessionOperations, StageAdvanced>(); ((StageAdvanced)(object)_advanced).Owner = this; }
                    return _advanced;
                case "Store": if (args![0] is ClientReagentItem reagent) Rows.Add(reagent); else Items.Add(Assert.IsType<WizClientObjectItem>(args[0])); return null;
                case "SaveChanges": Assert.True(WizardCollection.HoldsWriteLane); Saves++; return null;
                case "Dispose": return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
    public class StageAdvanced : DispatchProxy {
        internal StageSession Owner = null!;
        private readonly IMetadataDictionary _metadata = DispatchProxy.Create<IMetadataDictionary, StageMetadata>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "IgnoreChangesFor" => Ignore(args![0]!), "GetMetadataFor" => _metadata,
            "set_OptimisticConcurrencyMode" => null,
            _ => throw new NotSupportedException(method.Name) };
        private object? Ignore(object row) { Assert.True(Owner.Ignored.Add(row), "the outer finalizer protects each original once"); return null; }
    }
    public class StageMetadata : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            Assert.Equal("set_Item", method!.Name); Assert.Equal(Raven.Client.Constants.Documents.Metadata.Collection, args![0]);
            Assert.Contains(args[1], new[] { WizardItemCollection.CollectionName, WizardReagentCollection.CollectionName }); return null;
        }
    }
}
