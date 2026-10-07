// CLASSIC: reward packets and display quantities describe acknowledged saved inventory, never rolled promises.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Commands;
using Imlight.CoreLib.Game.Commands.Protocols;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class LootRewardPersistenceTests {
    public LootRewardPersistenceTests()
        => EquipmentAttachConcurrencyTests.Configure("[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");

    [Theory]
    [InlineData(998, 1)]
    [InlineData(999, 0)]
    [InlineData(1000, 0)]
    public void RolledThreeReagentsDisplayOnlyCopiesActuallyAcquired(int count, int acquired) {
        var f = new Fixture(count); using var scope = f.Scope();
        var live = f.Live(); var results = Roll(3);
        LootGranter.Grant(ActorRefs.NoSender, live, results, showPopup: acquired == 0);
        Assert.Equal(count + acquired, Assert.Single(f.Reagents).m_quantity);
        Assert.Equal(count + acquired, Assert.Single(live.AlchemyBehavior.Reagents).m_quantity);
        Assert.Equal(acquired == 0 ? 0 : 1, f.SaveAttempts);
        if (acquired == 0) { Assert.Empty(results.Reagents); Assert.Empty(f.Packets); }
        else {
            Assert.Equal(acquired, Assert.Single(results.Reagents).Quantity);
            var display = Assert.IsType<ItemLootInfo>(Assert.Single(DropTableConverter.ToLootInfoList(results).m_loot));
            Assert.Equal(acquired, display.m_numItems);
            Assert.Equal(Fixture.Normal, display.m_itemID.Full);
            Assert.Single(f.Packets.OfType<WIZARD_12_PROTOCOL.MSG_REAGENTADD>());
            Assert.Equal(Fixture.NormalId, Assert.Single(f.Packets.OfType<WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION>()).ItemGlobalID);
        }
    }

    [Fact]
    public void DuplicateNormalAndRareRollsCommitOnceAndUseCanonicalNativeIds() {
        var f = new Fixture(5); using var scope = f.Scope(); var live = f.Live();
        var results = Roll(2); results.Reagents.Add(Drop(Fixture.Normal, 3)); results.Reagents.Add(Drop(Fixture.Rare, 1));
        f.BeforeSave = () => { Assert.Empty(f.Packets); Assert.Equal(5, live.AlchemyBehavior.Reagents[0].m_quantity); };
        LootGranter.Grant(ActorRefs.NoSender, live, results, false);
        Assert.Equal(new[] { 5, 1 }, results.Reagents.Select(drop => drop.Quantity));
        Assert.Equal(new[] { 10, 1 }, f.Reagents.Select(row => row.m_quantity));
        Assert.Equal(new[] { Fixture.NormalId, Fixture.RareId }, f.Saved.AlchemyBehavior.ReagentItemIds);
        Assert.Equal(new[] { Fixture.NormalId, Fixture.RareId }, f.Packets.OfType<WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION>().Select(packet => packet.ItemGlobalID));
        Assert.Equal(new[] { 10, 1 }, f.Packets.OfType<WIZARD_12_PROTOCOL.MSG_REAGENTADD>()
            .Select(packet => BitConverter.ToInt32((byte[])packet.Data)));
        Assert.Equal(1, f.SaveAttempts); Assert.Equal(1, f.Reads);
        Assert.Equal(2, DropTableConverter.ToLootInfoList(results).m_loot.Count);
    }

    [Fact]
    public void CombinedCardsAndReagentsCommitOnceBeforeAnyClientPacketAndKeepDeckLedger() {
        var f = new Fixture(998, 998); using var scope = f.Scope(); var live = f.Live(); var results = Roll(3);
        results.Reagents.Add(Drop(Fixture.Rare, 1)); results.TreasureCards = [Fixture.Card, Fixture.OtherCard, Fixture.Card];
        var oldStats = live.GameStats; var oldDeck = ServerWizSpellbookBehavior.CopyLedger(live.SpellbookBehavior.DeckTreasureCards);
        f.BeforeSave = () => {
            Assert.Empty(f.Packets); Assert.Equal(998, live.SpellbookBehavior.TreasureCardTemplateIds.Count);
            Assert.Equal(998, Assert.Single(live.AlchemyBehavior.Reagents).m_quantity);
        };
        LootGranter.Grant(ActorRefs.NoSender, live, results, false);
        Assert.Equal(new[] { Fixture.Card }, results.TreasureCards);
        Assert.Equal(new[] { StringHash.Compute("Reward card") }, results.TreasureCardSpellIds);
        Assert.Equal(999, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(999, live.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(new[] { 1, 1 }, results.Reagents.Select(drop => drop.Quantity));
        Assert.Equal(new[] { 999, 1 }, f.Reagents.Select(row => row.m_quantity));
        Assert.Equal(oldDeck[Fixture.Deck], f.Saved.SpellbookBehavior.DeckTreasureCards[Fixture.Deck]);
        Assert.Equal(oldDeck[Fixture.Deck], live.SpellbookBehavior.DeckTreasureCards[Fixture.Deck]);
        Assert.Same(oldStats, live.GameStats); Assert.Equal(900, f.Saved.GameStats.m_currentGold);
        Assert.Equal(1, f.SaveAttempts); Assert.Single(f.Packets.OfType<WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK>());
        Assert.Equal(3, DropTableConverter.ToLootInfoList(results).m_loot.Count);
    }

    [Fact]
    public void FreshSavedBookCapacityWinsOverAStaleEmptyAttachedBook() {
        var f = new Fixture(0, 999); using var scope = f.Scope(); var live = f.Live();
        live.SpellbookBehavior.TreasureCardTemplateIds.Clear(); var results = new DropTableResult { TreasureCards = [Fixture.Card] };
        LootGranter.Grant(ActorRefs.NoSender, live, results, false);
        Assert.Empty(results.TreasureCards); Assert.Empty(results.TreasureCardSpellIds); Assert.Empty(f.Packets);
        Assert.Equal(999, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count); Assert.Equal(0, f.SaveAttempts);
        Assert.Empty(DropTableConverter.ToLootInfoList(results).m_loot);
    }

    [Fact]
    public void FullValidReagentStillAllowsAFittingTreasureCard() {
        var f = new Fixture(999); using var scope = f.Scope(); var live = f.Live(); var results = Roll(3);
        results.TreasureCards = [Fixture.Card]; LootGranter.Grant(ActorRefs.NoSender, live, results, false);
        Assert.Single(results.TreasureCards); Assert.Empty(results.Reagents);
        Assert.Single(f.Saved.SpellbookBehavior.TreasureCardTemplateIds); Assert.Equal(999, Assert.Single(f.Reagents).m_quantity);
        Assert.Single(f.Packets); Assert.IsType<WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK>(f.Packets[0]);
        Assert.Equal(1, f.SaveAttempts);
    }

    [Theory]
    [InlineData("foreign-candidate")]
    [InlineData("foreign-row")]
    [InlineData("missing-reference")]
    public void InvalidEvenFullReagentRefusesTheEntireCardReward(string defect) {
        var f = new Fixture(999); using var scope = f.Scope(); var live = f.Live();
        if (defect == "foreign-candidate") live.AlchemyBehavior.Reagents[0].m_characterId = Fixture.Char + 1;
        else if (defect == "foreign-row") f.Reagents[0].m_characterId = Fixture.Char + 1;
        else f.Reagents.Clear();
        var results = Roll(3); results.TreasureCards = [Fixture.Card];
        LootGranter.Grant(ActorRefs.NoSender, live, results, false);
        Assert.Empty(results.TreasureCards); Assert.Empty(results.Reagents); Assert.Empty(f.Packets);
        Assert.Empty(f.Saved.SpellbookBehavior.TreasureCardTemplateIds); Assert.Equal(0, f.SaveAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrLostAcknowledgementPublishesNothingClosesAndCannotRepeat(bool committed) {
        var f = new Fixture(5) { FailSave = true, CommitBeforeFailure = committed }; using var scope = f.Scope();
        var live = f.Live(); var results = Roll(3); results.TreasureCards = [Fixture.Card];
        var originalCards = results.TreasureCards; var originalReagents = results.Reagents;
        f.BeforeSave = () => Assert.Empty(f.Packets);
        Assert.Throws<InvalidOperationException>(() => LootGranter.Grant(ActorRefs.NoSender, live, results, false));
        Assert.Same(originalCards, results.TreasureCards); Assert.Same(originalReagents, results.Reagents);
        Assert.Equal("Close", Assert.Single(f.Packets)); Assert.Empty(results.TreasureCardSpellIds);
        Assert.Empty(live.SpellbookBehavior.TreasureCardTemplateIds); Assert.Equal(5, Assert.Single(live.AlchemyBehavior.Reagents).m_quantity);
        Assert.Equal(committed ? 8 : 5, Assert.Single(f.Reagents).m_quantity);
        Assert.Equal(committed ? 1 : 0, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.False(ClassicStackRewards.TryGrant(live, originalCards, originalReagents, out var receipt));
        Assert.Empty(receipt.Cards); Assert.Empty(receipt.Reagents); Assert.Equal(1, f.SaveAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnserializableReagentCancelsStagedCardsAndReagentsBeforeAnySave(bool throws) {
        var f = new Fixture(5); using var scope = f.Scope(); var live = f.Live(); var results = Roll(3);
        results.TreasureCards = [Fixture.Card]; results.Reagents.Add(Drop(Fixture.Rare, 1));
        f.Dependencies.SerializeReagent = _ => throws ? throw new InvalidOperationException("fixture serialization failure") : default;
        LootGranter.Grant(ActorRefs.NoSender, live, results, false);
        Assert.Empty(results.TreasureCards); Assert.Empty(results.Reagents); Assert.Empty(f.Packets);
        Assert.Empty(f.Saved.SpellbookBehavior.TreasureCardTemplateIds); Assert.Equal(5, Assert.Single(f.Reagents).m_quantity);
        Assert.Empty(live.SpellbookBehavior.TreasureCardTemplateIds); Assert.Equal(5, Assert.Single(live.AlchemyBehavior.Reagents).m_quantity);
        Assert.Equal(0, f.SaveAttempts); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Fact]
    public void InvalidTemplatesAreExcludedWhileValidCardsKeepRolledOrder() {
        var f = new Fixture(0, 997); using var scope = f.Scope(); var live = f.Live();
        var results = new DropTableResult { TreasureCards = [123, Fixture.OtherCard, Fixture.Card, Fixture.OtherCard],
            Reagents = [Drop(123, 5), Drop(Fixture.Normal, 0)] };
        LootGranter.Grant(ActorRefs.NoSender, live, results, false);
        Assert.Equal(new[] { Fixture.OtherCard, Fixture.Card }, results.TreasureCards);
        Assert.Equal(new[] { StringHash.Compute("Other reward"), StringHash.Compute("Reward card") }, results.TreasureCardSpellIds);
        Assert.Empty(results.Reagents); Assert.Equal(1, f.SaveAttempts);
        Assert.Equal(results.TreasureCards, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.TakeLast(2));
    }

    [Fact]
    public void QuestPopupUsesAcceptedCountAndPreservesItsTemplateIdConvention() {
        var f = new Fixture(0, 998); using var scope = f.Scope(); var live = f.Live(); List<LootInfo> loot = [];
        f.BeforeSave = () => { Assert.Empty(f.Packets); Assert.Empty(loot); };
        Assert.True(QuestService.GrantClassicQuestCardBatch(live, [Fixture.OtherCard, Fixture.Card, Fixture.Card], loot, message => f.Packets.Add(message)));
        var display = Assert.IsType<TreasureCardLootInfo>(Assert.Single(loot));
        Assert.Equal(Fixture.OtherCard, display.m_spellID); Assert.Equal(1, display.m_numItems);
        Assert.Equal((int)StringHash.Compute("Other reward"), Assert.IsType<WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK>(Assert.Single(f.Packets)).SpellID);
        Assert.Equal(999, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count); Assert.Equal(1, f.SaveAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuestFailedOrLostAckHasNoPhantomCardOrPopup(bool committed) {
        var f = new Fixture(0) { FailSave = true, CommitBeforeFailure = committed }; using var scope = f.Scope();
        var live = f.Live(); List<LootInfo> loot = [];
        Assert.Throws<InvalidOperationException>(() => QuestService.GrantClassicQuestCardBatch(live, [Fixture.Card, Fixture.OtherCard], loot, message => f.Packets.Add(message)));
        Assert.Empty(loot); Assert.Empty(f.Packets); Assert.Empty(live.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(committed ? 2 : 0, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); Assert.Equal(1, f.SaveAttempts);
    }

    [Fact]
    public void RefusedLoadAndAlreadyFullQuestBookProduceNoPacketsOrPopup() {
        foreach (var missing in new[] { false, true }) {
            var f = new Fixture(0, 999) { RefuseLoad = missing }; using var scope = f.Scope();
            List<LootInfo> loot = [];
            Assert.False(QuestService.GrantClassicQuestCardBatch(f.Live(), [Fixture.Card], loot, message => f.Packets.Add(message)));
            Assert.Empty(loot); Assert.Empty(f.Packets); Assert.Equal(0, f.SaveAttempts);
        }
    }

    [Fact]
    public void UnparseableGearRollDoesNotAppearInTheRewardWindow() {
        var f = new Fixture(0); using var scope = f.Scope(); var results = new DropTableResult {
            Items = [new() { ItemId = "invalid item", Quantity = 1 }],
        };
        LootGranter.Grant(ActorRefs.NoSender, f.Live(), results, false);
        Assert.Empty(results.Items); Assert.Empty(DropTableConverter.ToLootInfoList(results).m_loot); Assert.Empty(f.Packets);
        Assert.Equal(0, f.SaveAttempts);
    }

    [Theory]
    [InlineData(998, 1)]
    [InlineData(999, 0)]
    public async Task ActualQaCommandUsesOneBatchAndConfirmsOnlyTheFittingCount(int savedCards, int accepted) {
        var templates = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var previous = templates.TryGetValue(Fixture.Card, out var old) ? old : null;
        templates[Fixture.Card] = new SpellTemplate { m_name = "Reward card" };
        using var system = ActorSystem.Create("loot-qa-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        try {
            var f = new Fixture(0, savedCards); using var scope = f.Scope(); var channel = Channel.CreateUnbounded<object>();
            var sink = system.ActorOf(Props.Create(() => new Recorder(channel.Writer)));
            var context = new CommandContext { Character = f.Live(), SessionActor = sink,
                Account = new Account("", "", "") { AuthLevel = AuthLevel.QualityAssurance } };
            Assert.True(new CommandModifyProtocol().Execute("addtc", context, Fixture.Card.ToString(), "3"));
            var message = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal($"Added {accepted} Reward card treasure card(s).", Assert.IsType<EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE>(message).Message);
            Assert.False(channel.Reader.TryRead(out _)); Assert.Equal(accepted == 0 ? 0 : 1, f.SaveAttempts);
            Assert.Equal(accepted, f.Packets.OfType<WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK>().Count());
            Assert.Equal(savedCards + accepted, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
        }
        finally {
            await system.Terminate();
            if (previous is null) templates.Remove(Fixture.Card); else templates[Fixture.Card] = previous;
        }
    }

    private sealed class Recorder : ReceiveActor {
        public Recorder(ChannelWriter<object> writer) => ReceiveAny(message => writer.TryWrite(message));
    }

    private static DropTableResult Roll(int quantity) => new() { Reagents = [Drop(Fixture.Normal, quantity)] };
    private static DropItemResult Drop(ulong template, int quantity) => new() { ItemId = template.ToString(), ItemName = "Reward reagent", Quantity = quantity };

    // CLASSIC: detached tracked documents model before-save refusal and committed-but-unacknowledged writes.
    private sealed class Fixture {
        internal const ulong Char = 774171, Normal = 774172, Rare = 774173, NormalId = 774174, RareId = 774175, Deck = 774176;
        internal const uint Card = 774177, OtherCard = 774178;
        internal Wizard Saved;
        internal List<ClientReagentItem> Reagents;
        internal readonly List<object> Packets = [];
        internal readonly StackRewardDependencies Dependencies;
        internal bool FailSave, CommitBeforeFailure, RefuseLoad;
        internal int SaveAttempts, AcknowledgedSaves, Reads;
        internal System.Action? BeforeSave;
        internal Fixture(int count, int cards = 0) {
            Reagents = count == 0 ? [] : [new() { m_globalID = NormalId, m_permID = NormalId, m_templateID = Normal, m_characterId = Char, m_quantity = count }];
            Saved = new Wizard { CharId = Char,
                GameStats = new ServerWizGameStats(default, 1) { m_currentGold = 900, m_currentArenaPoints = 100 },
                SpellbookBehavior = new() { TreasureCardTemplateIds = Enumerable.Repeat(Card, cards).ToList(),
                    DeckTreasureCards = new() { [Deck] = new() { [OtherCard] = 4 } }, DeckTreasureLedgerVersion = 1 },
                AlchemyBehavior = new() { ReagentItemIds = count == 0 ? [] : [NormalId], Reagents = [] },
            };
            Dependencies = new() {
                Template = id => id == Card ? new SpellTemplate { m_name = "Reward card" }
                    : id == OtherCard ? new SpellTemplate { m_name = "Other reward" }
                    : id == Normal || id == Rare ? new ReagentItemTemplate { m_templateID = (uint)id } : null!,
                Create = id => new ClientReagentItem { m_globalID = id == Normal ? NormalId : RareId,
                    m_permID = id == Normal ? NormalId : RareId, m_templateID = id },
                SerializeReagent = row => new ByteString(BitConverter.GetBytes(row.m_quantity)),
            };
        }
        internal Wizard Live() {
            var live = Clone(Saved); live.AlchemyBehavior.Reagents = Reagents.Select(row => row with { }).ToList(); return live;
        }
        internal IDisposable Scope() {
            var previousStore = WizardCollection.TestStoreScope.Value;
            var previousRows = WizardReagentCollection.TestRowsScope.Value;
            var previousDependencies = ClassicStackRewards.TestScope.Value;
            var previousSend = LootGranter.TestSendScope.Value;
            WizardCollection.TestStoreScope.Value = new(Open, (session, id) => {
                Assert.Equal(Char, id); return RefuseLoad ? null! : Session(session).Wizard;
            });
            WizardReagentCollection.TestRowsScope.Value = session => { Reads++; return Session(session).Rows; };
            ClassicStackRewards.TestScope.Value = Dependencies;
            LootGranter.TestSendScope.Value = (_, message) => {
                if (message is not string) Assert.True(AcknowledgedSaves > 0);
                Packets.Add(message);
            };
            return new Restore(() => {
                WizardCollection.TestStoreScope.Value = previousStore;
                WizardReagentCollection.TestRowsScope.Value = previousRows;
                ClassicStackRewards.TestScope.Value = previousDependencies;
                LootGranter.TestSendScope.Value = previousSend;
            });
        }
        private IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, RewardSession>(); var proxy = Session(session);
            proxy.Wizard = Clone(Saved); proxy.Rows = Reagents.Select(row => row with { }).ToList();
            proxy.Save = () => {
                Assert.True(WizardCollection.HoldsWriteLane); SaveAttempts++; BeforeSave?.Invoke();
                if (FailSave && !CommitBeforeFailure) throw new InvalidOperationException("fixture refused reward save");
                Saved = Clone(proxy.Wizard); Reagents = proxy.Rows.Select(row => row with { }).ToList();
                if (FailSave) throw new InvalidOperationException("fixture lost reward acknowledgement");
                AcknowledgedSaves++;
            };
            return session;
        }
        private static RewardSession Session(IDocumentSession session) => (RewardSession)(object)session;
        private static Wizard Clone(Wizard saved) => new() { CharId = saved.CharId,
            GameStats = saved.GameStats.CloneSnapshotWithGold(saved.GameStats.m_currentGold),
            SpellbookBehavior = new() { TreasureCardTemplateIds = [..saved.SpellbookBehavior.TreasureCardTemplateIds],
                DeckTreasureCards = ServerWizSpellbookBehavior.CopyLedger(saved.SpellbookBehavior.DeckTreasureCards),
                DeckTreasureLedgerVersion = saved.SpellbookBehavior.DeckTreasureLedgerVersion },
            AlchemyBehavior = new() { ReagentItemIds = [..saved.AlchemyBehavior.ReagentItemIds], Reagents = [] },
        };
        private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
    }
    public class RewardSession : DispatchProxy {
        internal Wizard Wizard = null!;
        internal List<ClientReagentItem> Rows = [];
        internal System.Action Save = null!;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, ReagentPersistenceRegressionTests.ReagentAdvanced>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "get_Advanced" => _advanced,
            "Store" => Store(Assert.IsType<ClientReagentItem>(args![0])),
            "SaveChanges" => SaveNow(),
            "Dispose" => null,
            _ => throw new NotSupportedException(method.Name),
        };
        private object? Store(ClientReagentItem row) { Rows.Add(row); return null; }
        private object? SaveNow() { Save(); return null; }
    }
}
