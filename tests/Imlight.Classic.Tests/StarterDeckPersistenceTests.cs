using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class StarterDeckPersistenceTests : IDisposable {
    private const uint Fire = 103007158, Life = 84361, DeckTemplateId = uint.MaxValue - 601;
    private const ulong DeckId = 771960;
    private readonly IDictionary<ulong, CoreTemplate> _templates;
    private readonly Dictionary<ulong, CoreTemplate?> _previous = [];

    public StarterDeckPersistenceTests() {
        EquipmentAttachConcurrencyTests.Configure("[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");
        _templates = (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Install(Fire, new SpellTemplate { m_name = "Fire Cat", m_sMagicSchoolName = "Fire", m_spellRank = new SpellRank { m_spellRank = 1 } });
        Install(Life, new SpellTemplate { m_name = "Imp", m_sMagicSchoolName = "Life", m_spellRank = new SpellRank { m_spellRank = 1 } });
        Install(DeckTemplateId, new WizItemTemplate { m_behaviors = [Rules()] });
    }

    public void Dispose() {
        foreach (var (id, previous) in _previous) {
            if (previous is null) _templates.Remove(id);
            else _templates[id] = previous;
        }
    }

    private void Install(ulong id, CoreTemplate template) {
        _previous[id] = _templates.TryGetValue(id, out var previous) ? previous : null;
        _templates[id] = template;
    }

    private static DeckBehaviorTemplate Rules() => new() {
        m_primarySchoolName = "Fire", m_maxSpells = 20, m_maxTreasureCards = 10,
        m_schoolMaxInstances = 4, m_genericMaxInstances = 4, m_schoolMaxRank = 7, m_genericMaxRank = 7,
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RefusedSavedLoadLeavesEquippedAndBackpackListsUntouched(bool equipped, bool add) {
        var store = new DeckStore(Fire, 1) { RefuseLoad = true };
        var wizard = Live(store, equipped);
        var itemCards = Behavior(Item(wizard)).m_spellList;
        var bookCards = wizard.SpellbookBehavior.SpellList;
        Assert.False(Change(wizard, store, add));
        Assert.Same(itemCards, Behavior(Item(wizard)).m_spellList);
        Assert.Same(bookCards, wizard.SpellbookBehavior.SpellList);
        Assert.Equal(1u, Assert.Single(itemCards).m_quantity);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Equal(1, store.Count(Fire));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void ThrownSaveNeverPublishesRetriesOrReportsSuccess(bool equipped, bool add, bool committed) {
        var store = new DeckStore(Fire, 1) { FailSave = true, CommitBeforeFailure = committed };
        var wizard = Live(store, equipped);
        var cards = Behavior(Item(wizard)).m_spellList;
        var book = wizard.SpellbookBehavior.SpellList;
        Assert.Throws<InvalidOperationException>(() => Change(wizard, store, add));
        Assert.Same(cards, Behavior(Item(wizard)).m_spellList);
        Assert.Same(book, wizard.SpellbookBehavior.SpellList);
        Assert.Equal(1u, Assert.Single(cards).m_quantity);
        Assert.Equal(1, store.SaveAttempts);
        Assert.Equal(committed ? (add ? 2 : 0) : 1, store.Count(Fire));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SuccessfulSavedAddOrRemovePublishesTheReloadedCountAndEquippedAlias(bool equipped, bool add) {
        var store = new DeckStore(Fire, 2);
        var wizard = Live(store, equipped);
        var previousCards = Behavior(Item(wizard)).m_spellList;
        var previousBook = wizard.SpellbookBehavior.SpellList;
        Assert.True(Change(wizard, store, add));
        var expected = add ? 3 : 1;
        Assert.Equal(expected, store.Count(Fire));
        Assert.Equal((uint) expected, Assert.Single(Behavior(Item(wizard)).m_spellList).m_quantity);
        Assert.Equal(2u, Assert.Single(previousCards).m_quantity);
        if (equipped) Assert.Same(Behavior(Item(wizard)).m_spellList, wizard.SpellbookBehavior.SpellList);
        else Assert.Same(previousBook, wizard.SpellbookBehavior.SpellList);
        var reloaded = Live(store, equipped);
        Assert.Equal((uint) expected, Assert.Single(Behavior(Item(reloaded)).m_spellList).m_quantity);
        Assert.Equal(1, store.SaveAttempts);
    }

    [Fact]
    public void FirstEquippedCardPublishesIntoThePreviouslyNullItemAndBookLists() {
        var store = new DeckStore(Fire, 0);
        var wizard = Live(store, equipped: true);
        Behavior(Item(wizard)).m_spellList = null!;
        wizard.SpellbookBehavior.SpellList = null!;
        Assert.True(Change(wizard, store, add: true));
        Assert.Equal(1, store.Count(Fire));
        Assert.Same(Behavior(Item(wizard)).m_spellList, wizard.SpellbookBehavior.SpellList);
        Assert.Equal(1u, Assert.Single(wizard.SpellbookBehavior.SpellList).m_quantity);
    }

    [Fact]
    public void DetachedValidationPreservesOtherCardsIncludingEnchantmentMetadata() {
        var store = new DeckStore(Fire, 1);
        Behavior(store.Saved).m_spellList.Add(new SpellData { m_templateID = Life, m_quantity = 2, m_enchantment = 77 });
        var wizard = Live(store, equipped: true);
        var previous = wizard.SpellbookBehavior.SpellList;
        Assert.True(Change(wizard, store, add: true));
        var other = wizard.SpellbookBehavior.SpellList.Single(card => card.m_templateID == Life);
        Assert.Equal(77u, other.m_enchantment);
        Assert.Equal(2u, other.m_quantity);
        Assert.Equal(JsonConvert.SerializeObject(Behavior(store.Saved).m_spellList), JsonConvert.SerializeObject(wizard.SpellbookBehavior.SpellList));
        Assert.Equal(1u, previous.Single(card => card.m_templateID == Fire).m_quantity);
    }

    [Fact]
    public void ACardMissingFromTheSavedDeckCannotBeRemovedOnlyFromTheLiveDeck() {
        var store = new DeckStore(Fire, 1);
        var wizard = Live(store, equipped: true);
        Behavior(store.Saved).m_spellList.Clear();
        Assert.False(Change(wizard, store, add: false));
        Assert.Equal(1u, Assert.Single(wizard.SpellbookBehavior.SpellList).m_quantity);
        Assert.Equal(0, store.SaveAttempts);
    }

    [Theory]
    [InlineData(Fire)]
    [InlineData(Life)]
    public void MissingSavedStarterDeckCannotCompleteEnrollOrTeleport(uint spell) {
        var store = new DeckStore(spell, 0) { RefuseLoad = true };
        var wizard = Live(store, equipped: true);
        var teleports = 0;
        Assert.False(TutorialService.CompleteStarterExit(() => Finish(wizard, store, spell), () => teleports++));
        Assert.False(wizard.HasRegistryValue(ClassicStart.CompletedEntry));
        Assert.False(wizard.HasRegistryValue(ClassicStart.EnrollmentEntry));
        Assert.Equal(0, teleports);
        Assert.Equal(0, store.SaveAttempts);
    }

    [Theory]
    [InlineData(Fire)]
    [InlineData(Life)]
    public void PartialStarterFailureRetriesOnlyMissingCardsThenCompletesAndTeleports(uint spell) {
        var store = new DeckStore(spell, 0) { RefuseAfterSaves = 1 };
        var wizard = Live(store, equipped: true);
        var teleports = 0;
        Assert.False(TutorialService.CompleteStarterExit(() => Finish(wizard, store, spell), () => teleports++));
        Assert.Equal(1, store.Count(spell));
        Assert.Equal(1u, Assert.Single(wizard.SpellbookBehavior.SpellList).m_quantity);
        Assert.False(wizard.HasRegistryValue(ClassicStart.CompletedEntry));
        Assert.False(wizard.HasRegistryValue(ClassicStart.EnrollmentEntry));
        Assert.Equal(0, teleports);
        store.RefuseAfterSaves = int.MaxValue;
        Assert.True(TutorialService.CompleteStarterExit(() => Finish(wizard, store, spell), () => teleports++));
        Assert.Equal(3, store.Count(spell));
        Assert.Equal(3u, Assert.Single(wizard.SpellbookBehavior.SpellList).m_quantity);
        Assert.True(wizard.HasRegistryValue(ClassicStart.CompletedEntry));
        Assert.True(wizard.HasRegistryValue(ClassicStart.EnrollmentEntry));
        Assert.Equal(1, teleports);
        Assert.Equal(3, store.SaveAttempts);
        Assert.True(Finish(wizard, store, spell));
        Assert.Equal(3, store.Count(spell));
        Assert.Equal(3, store.SaveAttempts);
    }

    [Fact]
    public void AFalseLiveOnlySuccessCannotMarkTheTutorialComplete() {
        var store = new DeckStore(Fire, 0);
        var wizard = Live(store, equipped: true);
        var calls = 0;
        var teleports = 0;
        Assert.False(TutorialService.CompleteStarterExit(() => TutorialService.CompleteSavedStarterDeck(wizard, Fire,
            (id, spell) => WizardItemCollection.SavedDeckSpellCount(id, spell, store.Open, store.Load),
            (_, _) => { calls++; return true; }, wizard.QuestBehavior.SetRegistryValue), () => teleports++));
        Assert.Equal(3, calls);
        Assert.Equal(0, store.Count(Fire));
        Assert.False(wizard.HasRegistryValue(ClassicStart.CompletedEntry));
        Assert.False(wizard.HasRegistryValue(ClassicStart.EnrollmentEntry));
        Assert.Equal(0, teleports);
    }

    [Fact]
    public void CompletedStarterDeckIsNeverRefilledAfterThePlayerEditsIt() {
        var store = new DeckStore(Fire, 0);
        var wizard = Live(store, equipped: true);
        wizard.QuestBehavior.SetRegistryValue(ClassicStart.CompletedEntry, 1);
        Assert.True(TutorialService.CompleteSavedStarterDeck(wizard, Fire,
            (_, _) => throw new InvalidOperationException("completed deck must not be read"),
            (_, _) => throw new InvalidOperationException("completed deck must not be filled"),
            (_, _) => throw new InvalidOperationException("completed marker must not be rewritten")));
        Assert.Empty(wizard.SpellbookBehavior.SpellList);
        Assert.Equal(0, store.Count(Fire));
    }

    [Fact]
    public void TheSchoolSpellMustBeKnownBeforeStarterCardsAreAdded() {
        var store = new DeckStore(Fire, 0);
        var wizard = Live(store, equipped: true);
        wizard.SpellbookBehavior.LearnedSpellTemplateIds.Clear();
        Assert.False(Finish(wizard, store, Fire));
        Assert.Empty(wizard.QuestBehavior.Registry);
        Assert.Equal(0, store.SaveAttempts);
    }

    [Fact]
    public void ActualItemLookupSerializesAnExplicitBoundedNonStaleQuery() {
        // Construct the real client's query without executing it or starting a database.
        using var store = new DocumentStore { Urls = ["http://127.0.0.1:1"], Database = "starter-deck-query" }.Initialize();
        using var session = store.OpenSession();
        var query = (IRavenQueryInspector) WizardItemCollection.DeckItemQuery(session, DeckId);
        var request = query.GetIndexQuery(false);
        Assert.True(request.WaitForNonStaleResults);
        Assert.Equal(TimeSpan.FromSeconds(5), request.WaitForNonStaleResultsTimeout);
        Assert.Contains("m_globalID", request.Query);
    }

    private static bool Change(Wizard wizard, DeckStore store, bool add, uint spell = Fire)
        => add ? wizard.AddSpellToDeck(spell, DeckId, (id, tid) => store.Change(id, tid, true))
            : wizard.RemoveSpellFromDeck(spell, DeckId, (id, tid) => store.Change(id, tid, false));

    private static bool Finish(Wizard wizard, DeckStore store, uint spell)
        => TutorialService.CompleteSavedStarterDeck(wizard, spell,
            (id, tid) => WizardItemCollection.SavedDeckSpellCount(id, tid, store.Open, store.Load),
            (tid, id) => wizard.AddSpellToDeck(tid, id, (deck, card) => store.Change(deck, card, true)),
            wizard.QuestBehavior.SetRegistryValue);

    private static Wizard Live(DeckStore store, bool equipped) {
        var item = Copy(store.Saved);
        var wizard = new Wizard();
        wizard.CharId = 771961;
        wizard.PlayerNameBehavior = new ServerWizPlayerNameBehavior { NameOverride = "Starter fixture" };
        wizard.InventoryBehavior = new ServerWizInventoryBehavior { Items = [], InventoryItemIds = [] };
        wizard.EquipmentBehavior = new ServerWizEquipmentBehavior { EquippedItems = [], EquippedItemIds = [], SlotList = [] };
        wizard.QuestBehavior = new ServerQuestBehavior();
        wizard.SpellbookBehavior = new ServerWizSpellbookBehavior { DeckTemplate = Rules(), LearnedSpellTemplateIds = [store.Spell] };
        if (equipped) {
            wizard.EquipmentBehavior.EquippedItems.Add(item);
            wizard.EquipmentBehavior.EquippedItemIds.Add(DeckId);
            wizard.EquipmentBehavior.SlotList.Add(new() { SlotType = EquipmentSlotType.Deck, ItemId = DeckId });
            wizard.SpellbookBehavior.SpellList = Behavior(item).m_spellList;
        }
        else {
            wizard.InventoryBehavior.Items.Add(item);
            wizard.InventoryBehavior.InventoryItemIds.Add(DeckId);
        }
        return wizard;
    }

    private static WizClientObjectItem Item(Wizard wizard)
        => wizard.InventoryBehavior.Items.FirstOrDefault() ?? wizard.EquipmentBehavior.EquippedItems.Single();
    private static DeckBehavior Behavior(WizClientObjectItem item) => item.m_inactiveBehaviors.OfType<DeckBehavior>().Single();
    private static WizClientObjectItem Copy(WizClientObjectItem item) => new() {
        m_globalID = item.m_globalID, m_templateID = item.m_templateID, m_characterId = item.m_characterId,
        m_inactiveBehaviors = [new DeckBehavior { m_spellList = JsonConvert.DeserializeObject<List<SpellData>>(
            JsonConvert.SerializeObject(Behavior(item).m_spellList))! }],
    };

    private sealed class DeckStore {
        internal readonly uint Spell;
        internal WizClientObjectItem Saved;
        internal bool RefuseLoad, FailSave, CommitBeforeFailure;
        internal int SaveAttempts, RefuseAfterSaves = int.MaxValue;

        internal DeckStore(uint spell, uint count) {
            Spell = spell;
            Saved = new WizClientObjectItem { m_globalID = DeckId, m_templateID = DeckTemplateId, m_characterId = 771961,
                m_inactiveBehaviors = [new DeckBehavior { m_spellList = count == 0 ? [] : [new SpellData { m_templateID = spell, m_quantity = count }] }],
            };
        }
        internal int Count(uint spell) => Behavior(Saved).m_spellList.Where(card => card.m_templateID == spell).Sum(card => (int) card.m_quantity);
        internal bool Change(ulong id, uint spell, bool add) => WizardItemCollection.ChangeDeckSpell(id, spell, add, Open, Load);
        internal IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, ItemSession>();
            var proxy = (ItemSession) (object) session;
            proxy.Save = () => {
                SaveAttempts++;
                if (FailSave && !CommitBeforeFailure) throw new InvalidOperationException("injected refused item save");
                Saved = Copy(proxy.Working!);
                if (FailSave) throw new InvalidOperationException("injected uncertain committed item save");
            };
            return session;
        }
        internal WizClientObjectItem Load(IDocumentSession session, ulong id) {
            Assert.Equal(DeckId, id);
            if (RefuseLoad || SaveAttempts >= RefuseAfterSaves) return null!;
            var item = Copy(Saved);
            ((ItemSession) (object) session).Working = item;
            return item;
        }
    }

    public class ItemSession : DispatchProxy {
        internal WizClientObjectItem? Working;
        internal System.Action Save = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            if (method!.Name == "SaveChanges") { Save(); return null; }
            if (method.Name == "Dispose") return null;
            throw new NotSupportedException(method.Name);
        }
    }
}
