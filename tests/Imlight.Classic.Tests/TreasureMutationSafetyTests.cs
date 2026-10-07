// CLASSIC: saved ownership, admission and lost-acknowledgement protection across treasure-card mutations.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class TreasureMutationSafetyTests {
    private const uint Card = 101;
    private const ulong Deck = 13;

    public TreasureMutationSafetyTests()
        => EquipmentAttachConcurrencyTests.Configure("[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");

    [Theory]
    [InlineData(5, 1, 5, 1)]
    [InlineData(1, 5, 5, 5)]
    [InlineData(3, 3, 0, 1)]
    [InlineData(3, 3, -4, 1)]
    [InlineData(1001, 1001, 2, 2)]
    public void DeleteUsesSavedOwnershipAndOneSaveRatherThanTheStaleMenu(int liveCount, int savedCount, int requested, int expected) {
        var store = new Store(savedCount);
        var live = store.Live();
        live.SpellbookBehavior.TreasureCardTemplateIds = Enumerable.Repeat(Card, liveCount).ToList();
        using var scope = store.Scope();
        Assert.True(WizardCollection.TryRemoveTreasureCards(live, Card, requested, out var removed));
        Assert.Equal(expected, removed);
        Assert.Equal(savedCount - expected, store.First.SpellbookBehavior.TreasureCardCount(Card));
        Assert.Equal(savedCount - expected, live.SpellbookBehavior.TreasureCardCount(Card));
        Assert.Equal(2, live.SpellbookBehavior.DeckTreasureCount(Deck, Card));
        Assert.Equal(1, store.SaveAttempts);
    }

    [Fact]
    public void AbsentSavedCopiesNeverAuthorizeADeleteReceipt() {
        var store = new Store(0);
        var live = store.Live();
        live.SpellbookBehavior.TreasureCardTemplateIds = [Card, Card];
        using var scope = store.Scope();
        Assert.False(WizardCollection.TryRemoveTreasureCards(live, Card, 2, out var removed));
        Assert.Equal(0, removed);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Equal(2, live.SpellbookBehavior.TreasureCardCount(Card));
        Assert.Equal(2, store.First.SpellbookBehavior.DeckTreasureCount(Deck, Card));
    }

    [Theory]
    [InlineData(998, true)]
    [InlineData(999, false)]
    [InlineData(1001, false)]
    public void GenericAcquisitionUsesFreshSavedCapacity(int savedCount, bool allowed) {
        var store = new Store(savedCount);
        var live = store.Live();
        live.SpellbookBehavior.TreasureCardTemplateIds = [];
        using var scope = store.Scope();
        Assert.Equal(allowed, WizardCollection.ChangeTreasureCard(live, 202, add: true));
        Assert.Equal(savedCount + (allowed ? 1 : 0), store.First.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(allowed ? 1 : 0, store.SaveAttempts);
    }

    [Theory]
    [InlineData(false, "purchase")]
    [InlineData(true, "purchase")]
    [InlineData(false, "add")]
    [InlineData(true, "add")]
    [InlineData(false, "delete")]
    [InlineData(true, "delete")]
    [InlineData(false, "into-deck")]
    [InlineData(true, "into-deck")]
    [InlineData(false, "from-deck")]
    [InlineData(true, "from-deck")]
    [InlineData(false, "spend")]
    [InlineData(true, "spend")]
    public void UnknownSaveNeverPublishesOrAllowsAnotherMutationFromTheOldInstance(bool committed, string operation) {
        var store = new Store(2) { FailSave = true, CommitBeforeFailure = committed };
        var live = store.Live();
        var before = JsonConvert.SerializeObject(live);
        using var scope = store.Scope();
        Assert.Throws<InvalidOperationException>(() => Mutate(operation, live));
        Assert.Equal(before, JsonConvert.SerializeObject(live));
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.True(store.QuarantinedBeforeDispose);
        Assert.Equal(1, store.SaveAttempts);
        Assert.Equal(committed ? ExpectedFree(operation) : 2, store.First.SpellbookBehavior.TreasureCardCount(Card));
        Assert.Equal(committed ? ExpectedDeck(operation) : 2, store.First.SpellbookBehavior.DeckTreasureCount(Deck, Card));
        Assert.Equal(committed && operation == "purchase" ? 90 : 100, store.First.GameStats.m_currentGold);
        store.FailSave = false;
        foreach (var next in new[] { "purchase", "add", "delete", "into-deck", "from-deck", "spend" })
            Assert.False(Mutate(next, live));
        WizardCollection.UpdateCharacterItems(live);
        Assert.Equal(1, store.SaveAttempts);
        var fresh = store.Live();
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(fresh));
        Assert.True(WizardCollection.TryPurchaseTreasureCards(fresh, 202, 1, 0));
        Assert.Equal(2, store.SaveAttempts);
    }

    [Fact]
    public void ReturningAnOwnedDeckCardStillWorksForAnOlderOverfullBook() {
        var store = new Store(1001);
        var live = store.Live();
        using var scope = store.Scope();
        Assert.True(WizardCollection.MoveTreasureCardFromDeck(live, Deck, Card, destroy: false));
        Assert.Equal(1002, store.First.SpellbookBehavior.TreasureCardCount(Card));
        Assert.Equal(1, store.First.SpellbookBehavior.DeckTreasureCount(Deck, Card));
        Assert.Equal(1, store.SaveAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATradeQuarantinesBothParticipantsBeforeReleasingTheirLanes(bool committed) {
        var store = new Store(2) { FailSave = true, CommitBeforeFailure = committed };
        store.AddSecond(1);
        var first = store.Live();
        var second = store.Live(Store.SecondId);
        var firstBefore = JsonConvert.SerializeObject(first);
        var secondBefore = JsonConvert.SerializeObject(second);
        Assert.Throws<InvalidOperationException>(() => WizardCollection.CommitTreasureCardTrade(first, [Card], second, [], store.Open, store.Load));
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(first));
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(second));
        Assert.True(store.QuarantinedBeforeDispose);
        Assert.Equal(firstBefore, JsonConvert.SerializeObject(first));
        Assert.Equal(secondBefore, JsonConvert.SerializeObject(second));
        Assert.Equal(committed ? 1 : 2, store.First.SpellbookBehavior.TreasureCardCount(Card));
        Assert.Equal(committed ? 2 : 1, store.Saved[Store.SecondId].SpellbookBehavior.TreasureCardCount(Card));
        store.FailSave = false;
        Assert.False(WizardCollection.CommitTreasureCardTrade(first, [Card], second, [], store.Open, store.Load));
        Assert.Equal(1, store.SaveAttempts);
    }

    [Theory]
    [InlineData(999, 0, false)]
    [InlineData(999, 1, true)]
    [InlineData(1001, 1, true)]
    public void TradeAdmissionUsesNetSavedFreeBookSpaceAndPreservesLegacyExchanges(int held, int gives, bool allowed) {
        var store = new Store(held);
        store.AddSecond(1);
        var first = store.Live();
        var second = store.Live(Store.SecondId);
        first.SpellbookBehavior.TreasureCardTemplateIds = [];
        Assert.Equal(allowed, WizardCollection.CommitTreasureCardTrade(first, Enumerable.Repeat(Card, gives).ToArray(), second, [Card], store.Open, store.Load));
        Assert.Equal(held + (allowed ? 1 - gives : 0), store.First.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(allowed ? 1 : 0, store.SaveAttempts);
    }

    private static bool Mutate(string operation, Wizard wizard) => operation switch {
        "purchase" => WizardCollection.TryPurchaseTreasureCards(wizard, Card, 1, 10),
        "add" => WizardCollection.ChangeTreasureCard(wizard, Card, add: true),
        "delete" => WizardCollection.TryRemoveTreasureCards(wizard, Card, 1, out _),
        "into-deck" => WizardCollection.MoveTreasureCardToDeck(wizard, Deck, Card, 7),
        "from-deck" => WizardCollection.MoveTreasureCardFromDeck(wizard, Deck, Card, destroy: false),
        "spend" => WizardCollection.MoveTreasureCardFromDeck(wizard, Deck, Card, destroy: true),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
    private static int ExpectedFree(string operation) => operation switch {
        "purchase" or "add" or "from-deck" => 3,
        "delete" or "into-deck" => 1,
        _ => 2,
    };
    private static int ExpectedDeck(string operation) => operation switch {
        "into-deck" => 3,
        "from-deck" or "spend" => 1,
        _ => 2,
    };

    private sealed class Store {
        internal const ulong FirstId = 900901, SecondId = 900902;
        internal Dictionary<ulong, Wizard> Saved = [];
        internal Wizard First => Saved[FirstId];
        private readonly List<Wizard> _live = [];
        internal bool FailSave, CommitBeforeFailure, QuarantinedBeforeDispose;
        internal int SaveAttempts;
        internal Store(int count) => Saved[FirstId] = Make(FirstId, count);
        internal void AddSecond(int count) => Saved[SecondId] = Make(SecondId, count);
        internal Wizard Live(ulong id = FirstId) { var live = Clone(Saved[id]); _live.Add(live); return live; }
        internal IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, Session>();
            var proxy = (Session)(object)session;
            proxy.Working = Saved.ToDictionary(pair => pair.Key, pair => Clone(pair.Value));
            proxy.Save = () => {
                Assert.True(WizardCollection.HoldsWriteLane);
                SaveAttempts++;
                if (FailSave && !CommitBeforeFailure) throw new InvalidOperationException("injected before save");
                Saved = proxy.Working.ToDictionary(pair => pair.Key, pair => Clone(pair.Value));
                if (FailSave) throw new InvalidOperationException("injected lost acknowledgement");
            };
            proxy.Dispose = () => {
                if (FailSave) QuarantinedBeforeDispose = WizardCollection.HoldsWriteLane
                    && _live.All(WizardCollection.IsInventorySnapshotUncertain);
            };
            return session;
        }
        internal Wizard Load(IDocumentSession session, ulong id) => ((Session)(object)session).Working[id];
        internal IDisposable Scope() {
            var previous = WizardCollection.TestStoreScope.Value;
            WizardCollection.TestStoreScope.Value = new WizardCollection.TestStore(Open, Load);
            return new Restore(() => WizardCollection.TestStoreScope.Value = previous);
        }
        private static Wizard Clone(Wizard value) => JsonConvert.DeserializeObject<Wizard>(JsonConvert.SerializeObject(value))!;
        private static Wizard Make(ulong id, int count) => new() {
            CharId = id,
            GameStats = new ServerWizGameStats(default, 1) { m_currentGold = 100 },
            SpellbookBehavior = new() {
                TreasureCardTemplateIds = Enumerable.Repeat(Card, count).ToList(),
                DeckTreasureCards = new() { [Deck] = new() { [Card] = 2 } }, DeckTreasureLedgerVersion = 1,
            },
        };
        private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
    }
    public class Session : DispatchProxy {
        internal Dictionary<ulong, Wizard> Working = [];
        internal System.Action Save = null!, Dispose = null!;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, Advanced>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "get_Advanced" => _advanced,
            "SaveChanges" => Run(Save),
            "Dispose" => Run(Dispose),
            _ => throw new NotSupportedException(method.Name),
        };
        private static object? Run(System.Action action) { action(); return null; }
    }
    public class Advanced : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
    }
}
