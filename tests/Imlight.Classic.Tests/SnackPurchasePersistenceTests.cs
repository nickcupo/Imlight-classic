// CLASSIC: saved snack originals/references and payment acknowledge together, including lost ACKs.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;
using Action = System.Action;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class SnackPurchasePersistenceTests {
    public SnackPurchasePersistenceTests()
        => EquipmentAttachConcurrencyTests.Configure("[Character]\nMaxInventoryItems=150\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PurchaseUsesSavedGoldAndQuantityAndPublishesOnlyAfterOneSave(bool existing) {
        var f = new Fixture(); if (existing) f.Seed(2);
        using var scope = f.Scope(); var live = f.Live(); var owner = live.PetSnackBehavior;
        live.GameStats.m_currentGold = 1;
        var alias = live.PetSnackBehavior.Snacks.FirstOrDefault(); if (alias is not null) alias.m_quantity = 998;
        f.BeforeSave = () => { Assert.Equal(1, live.GameStats.m_currentGold); Assert.Equal(existing ? 998 : 0, alias?.m_quantity ?? 0); };
        Assert.True(f.Buy(live, out var receipt)); Assert.Equal(1, f.Saves); Assert.Equal(800, live.GameStats.m_currentGold);
        Assert.Equal(800, f.Saved.GameStats.m_currentGold); Assert.Same(owner, live.PetSnackBehavior);
        var saved = Assert.Single(f.Rows); Assert.Equal(existing ? 3 : 1, saved.m_quantity);
        Assert.Equal(Fixture.SnackId, Assert.Single(f.Saved.PetSnackBehavior.SnackItemIds));
        Assert.Equal(!existing, receipt.IsNew); Assert.Equal(existing ? 0 : 1, f.Serializations);
        if (existing) Assert.Same(alias, Assert.Single(live.PetSnackBehavior.Snacks));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrLostSnackPurchaseAckDoesNotPublishRefundOrRepeat(bool durable) {
        var f = new Fixture { Fail = true, Durable = durable }; using var scope = f.Scope(); var live = f.Live();
        Assert.Throws<InvalidOperationException>(() => f.Buy(live, out _)); Assert.Equal(1, f.Saves);
        Assert.Empty(live.PetSnackBehavior.Snacks); Assert.Empty(live.PetSnackBehavior.SnackItemIds);
        Assert.Equal(1000, live.GameStats.m_currentGold); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.Equal(durable ? 800 : 1000, f.Saved.GameStats.m_currentGold); Assert.Equal(durable ? 1 : 0, f.Rows.Count);
        Assert.False(f.Buy(live, out var receipt)); Assert.Null(receipt); Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("poor")]
    [InlineData("foreign")]
    [InlineData("missing")]
    [InlineData("duplicate-ref")]
    [InlineData("duplicate-row")]
    [InlineData("duplicate-template")]
    [InlineData("overlap")]
    [InlineData("zero-count")]
    [InlineData("serialize")]
    [InlineData("collision")]
    [InlineData("wrong-template")]
    [InlineData("wrong-owner")]
    [InlineData("negative-price")]
    public void RefusalsDoNotChangeGoldOriginalOrLiveBag(string reason) {
        var f = new Fixture();
        if (reason is not ("serialize" or "collision" or "wrong-template" or "wrong-owner" or "negative-price")) f.Seed(2);
        switch (reason) {
            case "full": f.Rows[0].m_quantity = 999; break;
            case "poor": f.Saved.GameStats.m_currentGold = 199; break;
            case "foreign": f.Rows[0].m_characterId = Fixture.Owner + 1; break;
            case "missing": f.Rows.Clear(); break;
            case "duplicate-ref": f.Saved.PetSnackBehavior.SnackItemIds.Add(Fixture.SnackId); break;
            case "duplicate-row": f.Rows.Add(f.Rows[0] with { }); break;
            case "duplicate-template": f.Rows.Add(f.Rows[0] with { m_globalID = Fixture.SnackId + 1 }); f.Saved.PetSnackBehavior.SnackItemIds.Add(Fixture.SnackId + 1); break;
            case "overlap": f.Saved.InventoryBehavior.InventoryItemIds.Add(Fixture.SnackId); break;
            case "zero-count": f.Rows[0].m_quantity = 0; break;
            case "collision": f.Rows.Add(Fixture.Snack() with { m_characterId = Fixture.Owner + 1 }); break;
        }
        using var scope = f.Scope(); var live = f.Live(); var gold = live.GameStats.m_currentGold; var count = f.Rows.Sum(row => row.m_quantity);
        var create = reason switch {
            "wrong-template" => () => Fixture.Snack() with { m_templateID = Fixture.Template + 1 },
            "wrong-owner" => () => Fixture.Snack() with { m_characterId = Fixture.Owner + 1 },
            _ => (Func<ClientPetSnackItem>)Fixture.Snack,
        };
        Assert.False(WizardPetSnackTransactions.Purchase(live, Fixture.Template, reason == "negative-price" ? -1 : 200,
            out var receipt, create, reason == "serialize" ? _ => default : f.Data));
        Assert.Null(receipt); Assert.Equal(0, f.Saves); Assert.Equal(gold, live.GameStats.m_currentGold);
        Assert.Equal(gold, f.Saved.GameStats.m_currentGold); Assert.Equal(count, f.Rows.Sum(row => row.m_quantity));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Fact]
    public void OrphanOfSameTemplateIsRetainedButNeverAdopted() {
        var f = new Fixture(); f.Rows.Add(Fixture.Snack() with { m_globalID = Fixture.SnackId + 1, m_quantity = 20 });
        using var scope = f.Scope(); var live = f.Live();
        Assert.True(f.Buy(live, out var receipt)); Assert.True(receipt.IsNew); Assert.Equal(2, f.Rows.Count);
        Assert.Equal(Fixture.SnackId, Assert.Single(f.Saved.PetSnackBehavior.SnackItemIds));
        Assert.Equal(20, f.Rows.Single(row => row.m_globalID.Full == Fixture.SnackId + 1).m_quantity);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1000)]
    public void ConsumeUsesOriginalSavedQuantityIncludingLegacyOverfullStack(int quantity) {
        var f = new Fixture(); f.Seed(quantity); using var scope = f.Scope(); var live = f.Live();
        var alias = live.PetSnackBehavior.Snacks[0]; alias.m_quantity = 77;
        f.BeforeSave = () => Assert.Equal(77, alias.m_quantity);
        ClientPetSnackItem consumed = null!; List<ClientPetSnackItem> bag = [];
        Assert.True(WizardCollection.CommitCharacterMutation(live.CharId,
            (session, saved) => WizardPetSnackTransactions.TryStageConsume(session, saved, Fixture.SnackId, out consumed, out bag),
            saved => WizardPetSnackTransactions.PublishCommittedBag(live, saved, bag)));
        Assert.Equal(1, f.Saves); Assert.Equal(quantity - 1, consumed.m_quantity); Assert.Equal(1000, f.Saved.GameStats.m_currentGold);
        if (quantity == 1) { Assert.Empty(f.Rows); Assert.Empty(live.PetSnackBehavior.Snacks); Assert.Empty(f.Saved.PetSnackBehavior.SnackItemIds); }
        else { Assert.Equal(quantity - 1, Assert.Single(f.Rows).m_quantity); Assert.Same(alias, Assert.Single(live.PetSnackBehavior.Snacks)); }
    }

    [Fact]
    public void ExistingFreeSnackPurchaseRetainsFreePricing() {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        Assert.True(WizardPetSnackTransactions.Purchase(live, Fixture.Template, 0, out _, Fixture.Snack, f.Data));
        Assert.Equal(1, f.Saves); Assert.Equal(1000, f.Saved.GameStats.m_currentGold);
    }

    private sealed class Fixture {
        internal const ulong Owner = 771882, SnackId = 771884;
        internal const uint Template = 771886;
        internal Wizard Saved = new() { CharId = Owner, GameStats = new(default, 50) { m_currentGold = 1000 },
            PetSnackBehavior = new() { SnackItemIds = [], Snacks = [] },
            InventoryBehavior = new() { InventoryItemIds = [], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = [] }, StorageBehavior = new() { BankItemIds = [] } };
        internal List<ClientPetSnackItem> Rows = [];
        internal int Saves, Serializations; internal bool Fail, Durable; internal Action? BeforeSave;
        internal static ClientPetSnackItem Snack() => new() { m_globalID = SnackId, m_templateID = Template, m_characterId = Owner, m_quantity = 1 };
        internal void Seed(int quantity) { Rows.Add(Snack() with { m_quantity = quantity }); Saved.PetSnackBehavior.SnackItemIds.Add(SnackId); }
        internal bool Buy(Wizard live, out SnackPurchaseReceipt receipt)
            => WizardPetSnackTransactions.Purchase(live, Template, 200, out receipt, Snack, Data);
        internal ByteString Data(ClientPetSnackItem _) { Serializations++; return new(new byte[] { 1 }); }
        internal Wizard Live() {
            var live = Clone(Saved); live.PetSnackBehavior.Snacks = Rows.Where(row => live.PetSnackBehavior.SnackItemIds.Contains(row.m_globalID.Full)).Select(row => row with { }).ToList();
            return live;
        }
        internal IDisposable Scope() {
            var old = WizardCollection.TestStoreScope.Value; var rows = WizardPetSnackTransactions.TestRowsScope.Value;
            WizardCollection.TestStoreScope.Value = new(Open, (session, _) => Proxy(session).Wizard);
            WizardPetSnackTransactions.TestRowsScope.Value = session => Proxy(session).Rows;
            return new Restore(() => { WizardCollection.TestStoreScope.Value = old; WizardPetSnackTransactions.TestRowsScope.Value = rows; });
        }
        private IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, SnackSession>(); var proxy = Proxy(session);
            proxy.Wizard = Clone(Saved); proxy.Rows = Rows.Select(row => row with { }).ToList();
            proxy.Save = () => {
                Assert.True(WizardCollection.HoldsWriteLane); Saves++; BeforeSave?.Invoke();
                if (Fail && !Durable) throw new InvalidOperationException("fixture refused write");
                Saved = Clone(proxy.Wizard); Rows = proxy.Rows.Select(row => row with { }).ToList();
                if (Fail) throw new InvalidOperationException("fixture lost acknowledgement");
            };
            return session;
        }
        private static SnackSession Proxy(IDocumentSession session) => (SnackSession)(object)session;
        private static Wizard Clone(Wizard saved) => new() { CharId = saved.CharId,
            GameStats = saved.GameStats.CloneSnapshotWithGold(saved.GameStats.m_currentGold),
            PetSnackBehavior = new() { SnackItemIds = [..saved.PetSnackBehavior.SnackItemIds], Snacks = [] },
            InventoryBehavior = new() { InventoryItemIds = [..saved.InventoryBehavior.InventoryItemIds], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [..saved.EquipmentBehavior.EquippedItemIds], EquippedItems = [] },
            StorageBehavior = new() { BankItemIds = [..saved.StorageBehavior.BankItemIds] } };
        private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
    }
    public class SnackSession : DispatchProxy {
        internal Wizard Wizard = null!; internal List<ClientPetSnackItem> Rows = []; internal Action Save = null!;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, SnackAdvanced>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "get_Advanced" => _advanced, "SaveChanges" => SaveNow(), "Store" => Store((ClientPetSnackItem)args![0]),
            "Delete" => Delete((ClientPetSnackItem)args![0]), "Dispose" => null, _ => throw new NotSupportedException(method.Name) };
        private object? SaveNow() { Save(); return null; }
        private object? Store(ClientPetSnackItem snack) { Rows.Add(snack); return null; }
        private object? Delete(ClientPetSnackItem snack) { Assert.True(Rows.Remove(snack)); return null; }
    }
    public class SnackAdvanced : DispatchProxy {
        private readonly IMetadataDictionary _metadata = DispatchProxy.Create<IMetadataDictionary, SnackMetadata>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "set_OptimisticConcurrencyMode" => null, "GetMetadataFor" => _metadata, _ => throw new NotSupportedException(method.Name) };
    }
    public class SnackMetadata : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            Assert.Equal("set_Item", method!.Name); Assert.Equal(Raven.Client.Constants.Documents.Metadata.Collection, args![0]);
            Assert.Equal(WizardPetSnackCollection.CollectionName, args[1]); return null;
        }
    }
}
