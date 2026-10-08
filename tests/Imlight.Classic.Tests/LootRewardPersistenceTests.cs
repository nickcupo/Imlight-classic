// CLASSIC: reward packets and display quantities describe acknowledged saved inventory, never rolled promises.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.MessageLayer;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Commands;
using Imlight.CoreLib.Game.Commands.Protocols;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Classic;
using Imlight.Classic.Settings;
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
        => EquipmentAttachConcurrencyTests.Configure("[Character]\nMaxInventoryItems=150\n[Classic]\nBackpackSize=2\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");

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

    [Fact]
    public void GearCardsAndReagentsUseOneCommitAndPublishOnlyTheFittingGearPrefix() {
        var f = new Fixture(5); f.AddGear(101); using var scope = f.Scope(); var live = f.Live();
        var results = Roll(3); results.Items = [Drop(Fixture.Gear, 1), Drop(Fixture.Gear, 1)]; results.TreasureCards = [Fixture.Card];
        var gearAlias = Assert.Single(live.InventoryBehavior.Items);
        f.BeforeSave = () => { Assert.Empty(f.Packets); Assert.Single(live.InventoryBehavior.Items); Assert.Empty(live.SpellbookBehavior.TreasureCardTemplateIds); };
        LootGranter.Grant(ActorRefs.NoSender, live, results, false);
        Assert.Single(results.Items); Assert.Equal(1, results.Items[0].Quantity); Assert.Single(results.TreasureCards);
        Assert.Equal(3, Assert.Single(results.Reagents).Quantity); Assert.Equal(1, f.SaveAttempts); Assert.Equal(1, f.AcknowledgedSaves);
        Assert.Equal(2, f.Items.Count); Assert.Equal(new ulong[] { 101, Fixture.GearId }, f.Saved.InventoryBehavior.InventoryItemIds);
        Assert.Same(gearAlias, live.InventoryBehavior.Items[0]); Assert.Equal(Fixture.GearId, live.InventoryBehavior.Items[1].m_globalID.Full);
        var packet = Assert.Single(f.Packets.OfType<GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM>());
        Assert.Equal(Fixture.GearId, BitConverter.ToUInt64((byte[])packet.SerializedItem));
        Assert.Equal(3, DropTableConverter.ToLootInfoList(results).m_loot.Count);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("full")]
    [InlineData("lost")]
    [InlineData("durable-lost")]
    public void ProductionPetRewardFactoryPreparesItsTemplateEggBeforeTheGrant(string outcome) {
        // Authored cache entry exercises the real PetFactory; no game archives or private values are used.
        var pets = (IDictionary<uint, GameObjectTemplate>)typeof(PetFactory)
            .GetField("s_petTemplates", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var id = checked((uint)Fixture.Gear);
        var previous = pets.TryGetValue(id, out var old) ? old : null;
        pets[id] = new GameObjectTemplate { m_templateID = id,
            m_behaviors = [new PetItemBehaviorTemplate { m_behaviorName = "PetItemBehavior", m_sHatchRate = "60s" }] };
        try {
            var f = new Fixture(0) { FailSave = outcome is "lost" or "durable-lost", CommitBeforeFailure = outcome == "durable-lost" };
            f.Dependencies.Create = null!;
            if (outcome == "full") for (ulong itemId = 1; itemId <= 150; itemId++) f.AddGear(itemId);
            using var scope = f.Scope(); var live = f.Live();
            var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            StackRewardReceipt receipt = null!;
            bool Grant() => ClassicStackRewards.TryGrant(live, [Drop(Fixture.Gear, 1)], [], [], out receipt);
            if (f.FailSave) Assert.Throws<InvalidOperationException>(() => Grant());
            else Assert.Equal(outcome == "success", Grant());
            if (outcome == "full") { Assert.Equal(0, f.SaveAttempts); Assert.True(receipt.BackpackCapacityExceeded); return; }
            Assert.Equal(1, f.SaveAttempts);
            if (outcome is "success" or "durable-lost") {
                var saved = Assert.Single(f.Items);
                Assert.Equal(Fixture.Char, saved.m_characterId.Full); Assert.Equal(Fixture.Gear, saved.m_templateID.Full);
                Assert.Equal(saved.m_globalID.Full, Assert.Single(f.Saved.InventoryBehavior.InventoryItemIds));
                var egg = Assert.Single(saved.m_inactiveBehaviors.OfType<ClientPetItemBehavior>());
                Assert.Equal(0, egg.m_level);
                Assert.InRange((long)egg.m_hatchedTimeSecs, before + 60, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60);
                Assert.Single(saved.m_inactiveBehaviors.OfType<ClientPetNameBehavior>());
            }
            if (outcome == "success") Assert.Same(Assert.Single(live.InventoryBehavior.Items), Assert.Single(receipt.Items).Item);
            else {
                Assert.Empty(live.InventoryBehavior.Items); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
                Assert.False(Grant()); Assert.Equal(1, f.SaveAttempts);
            }
        }
        finally { if (previous is null) pets.Remove(id); else pets[id] = previous; }
    }

    [Fact]
    public void ReagentTemplateInOrdinaryRollIsExcludedWhileValidGearCardAndReagentCommit() {
        var f = new Fixture(5); using var scope = f.Scope(); var live = f.Live();
        var templateSource = f.Dependencies.Template; var createSource = f.Dependencies.Create;
        f.Dependencies.Template = id => id == Fixture.Normal
            ? new ReagentItemTemplate { m_templateID = (uint)id, m_behaviors = [] } : templateSource(id);
        var gearCreations = 0; var reagentCreations = 0;
        f.Dependencies.Create = id => { if (id == Fixture.Normal) reagentCreations++; else if (id == Fixture.Gear) gearCreations++; return createSource(id); };
        var results = Roll(2); results.Items = [Drop(Fixture.Normal, 1), Drop(Fixture.Gear, 1)]; results.TreasureCards = [Fixture.Card];
        LootGranter.Grant(ActorRefs.NoSender, live, results, false);
        Assert.Equal(Fixture.Gear.ToString(), Assert.Single(results.Items).ItemId); Assert.Single(results.TreasureCards);
        Assert.Equal(2, Assert.Single(results.Reagents).Quantity); Assert.Equal(7, Assert.Single(f.Reagents).m_quantity);
        Assert.Equal(Fixture.Gear, Assert.Single(f.Items).m_templateID.Full); Assert.IsNotType<ClientReagentItem>(f.Items[0]);
        Assert.Equal(Fixture.GearId, Assert.Single(f.Saved.InventoryBehavior.InventoryItemIds));
        Assert.Equal(Fixture.NormalId, Assert.Single(f.Saved.AlchemyBehavior.ReagentItemIds));
        Assert.Equal(1, gearCreations); Assert.Equal(0, reagentCreations); Assert.Equal(1, f.SaveAttempts);
        Assert.Single(f.Packets.OfType<GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM>());
        Assert.Single(f.Packets.OfType<WIZARD_12_PROTOCOL.MSG_REAGENTADD>());
        Assert.Equal(3, DropTableConverter.ToLootInfoList(results).m_loot.Count);
    }

    [Fact]
    public void FullFreshSavedGearBagStillGrantsCardsAndReagentsWhenAttachedBagIsEmpty() {
        var f = new Fixture(5); f.AddGear(101); f.AddGear(102); using var scope = f.Scope(); var live = f.Live();
        live.InventoryBehavior.InventoryItemIds.Clear(); live.InventoryBehavior.Items = [];
        var results = Roll(3); results.Items = [Drop(Fixture.Gear, 1)]; results.TreasureCards = [Fixture.Card];
        LootGranter.Grant(ActorRefs.NoSender, live, results, false);
        Assert.Empty(results.Items); Assert.Single(results.TreasureCards); Assert.Equal(3, Assert.Single(results.Reagents).Quantity);
        Assert.Equal(2, f.Items.Count); Assert.Equal(new ulong[] { 101, 102 }, f.Saved.InventoryBehavior.InventoryItemIds);
        Assert.Empty(f.Packets.OfType<GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM>()); Assert.Equal(1, f.SaveAttempts);
    }

    [Fact]
    public void FullGearOnlyRewardShowsTheExistingCapacityExplanationWithoutAnAwardOrSave() {
        var f = new Fixture(0); f.AddGear(101); f.AddGear(102); using var scope = f.Scope(); var results = new DropTableResult { Items = [Drop(Fixture.Gear, 1)] };
        var live = f.Live(); live.InventoryBehavior.Items = []; live.InventoryBehavior.InventoryItemIds.Clear();
        LootGranter.Grant(ActorRefs.NoSender, live, results, true);
        Assert.Empty(results.Items); Assert.False(results.HasRewards); Assert.Equal(0, f.SaveAttempts);
        Assert.Equal("Your backpack is full, so a reward item could not be added. Make room and try again later.",
            Assert.IsType<EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE>(Assert.Single(f.Packets)).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GearSerializationFailureCancelsTheWholePreparedGearCardReagentReward(bool throws) {
        var f = new Fixture(5); using var scope = f.Scope(); var live = f.Live(); var results = Roll(3);
        results.Items = [Drop(Fixture.Gear, 1)]; results.TreasureCards = [Fixture.Card];
        f.Dependencies.SerializeItem = _ => throws ? throw new InvalidOperationException("fixture gear serialization failure") : default;
        LootGranter.Grant(ActorRefs.NoSender, live, results, false);
        Assert.Empty(results.Items); Assert.Empty(results.TreasureCards); Assert.Empty(results.Reagents); Assert.Empty(f.Packets);
        Assert.Empty(f.Items); Assert.Empty(live.InventoryBehavior.Items); Assert.Empty(f.Saved.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(5, Assert.Single(f.Reagents).m_quantity); Assert.Equal(0, f.SaveAttempts); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GearCardReagentLostAckHasNoPartialClientInventoryOrDisplayAndCannotRepeat(bool committed) {
        var f = new Fixture(5) { FailSave = true, CommitBeforeFailure = committed }; using var scope = f.Scope(); var live = f.Live();
        var results = Roll(3); results.Items = [Drop(Fixture.Gear, 1)]; results.TreasureCards = [Fixture.Card]; var oldItems = results.Items;
        Assert.Throws<InvalidOperationException>(() => LootGranter.Grant(ActorRefs.NoSender, live, results, false));
        Assert.Equal("Close", Assert.Single(f.Packets)); Assert.Same(oldItems, results.Items);
        Assert.Empty(live.InventoryBehavior.Items); Assert.Empty(live.SpellbookBehavior.TreasureCardTemplateIds); Assert.Equal(5, Assert.Single(live.AlchemyBehavior.Reagents).m_quantity);
        Assert.Equal(committed ? 1 : 0, f.Items.Count); Assert.Equal(committed ? 1 : 0, f.Saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Equal(committed ? 8 : 5, Assert.Single(f.Reagents).m_quantity);
        Assert.False(ClassicStackRewards.TryGrant(live, results.Items, results.TreasureCards, results.Reagents, out var receipt));
        Assert.Empty(receipt.Items); Assert.Equal(1, f.SaveAttempts);
    }

    [Fact]
    public void AForeignGearCollisionInAnAlreadyFullBagCancelsOtherwiseFittingCardsAndReagents() {
        var f = new Fixture(5); f.AddGear(101); f.AddGear(102);
        f.Items.Add(new() { m_globalID = Fixture.GearId, m_templateID = Fixture.Gear, m_characterId = Fixture.Char + 1 });
        using var scope = f.Scope(); var live = f.Live(); var results = Roll(3);
        results.Items = [Drop(Fixture.Gear, 1)]; results.TreasureCards = [Fixture.Card];
        LootGranter.Grant(ActorRefs.NoSender, live, results, false);
        Assert.Empty(results.Items); Assert.Empty(results.TreasureCards); Assert.Empty(results.Reagents); Assert.Empty(f.Packets);
        Assert.Equal(3, f.Items.Count); Assert.Equal(5, Assert.Single(f.Reagents).m_quantity); Assert.Equal(0, f.SaveAttempts);
    }

    [Fact]
    public void GoldLootPopupContainsOnlyTheFreshCappedAcknowledgedAmount() {
        using var f = new GoldFixture(); using var scope = f.Scope(); var live = f.Store.Live();
        f.Store.Saved.GameStats.m_currentGold = 980; live.GameStats.m_currentGold = 1; live.GameStats.m_baseGoldPouch = 9999;
        var alias = live.GameStats; var reward = new DropTableResult { GoldAmount = 100 };
        f.Store.BeforeSave = () => { Assert.Empty(f.Packets); Assert.Equal(1, alias.m_currentGold); };
        LootGranter.Grant(ActorRefs.NoSender, live, reward, true);
        Assert.Equal(1, f.Store.SaveAttempts); Assert.Equal(1000, f.Store.Saved.GameStats.m_currentGold);
        Assert.Same(alias, live.GameStats); Assert.Equal(1000, alias.m_currentGold); Assert.Equal(9999, alias.m_baseGoldPouch);
        Assert.Equal(20, reward.GoldAmount); var packets = f.Packets.ToArray(); Assert.Equal(2, packets.Length);
        var update = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>(packets[0]); Assert.Equal(1000, update.Gold); Assert.Equal(1000, update.MaxGold);
        var decoded = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>(Assert.Single(MessageEncoder.Decode(MessageEncoder.Encode(update))!));
        Assert.Equal(update.Gold, decoded.Gold); Assert.Equal(update.MaxGold, decoded.MaxGold);
        Assert.Equal(20, GoldFixture.Popup(Assert.IsType<WIZARD_12_PROTOCOL.MSG_LOOT>(packets[1])).m_goldInfo.m_goldAmount);
    }

    [Theory]
    [InlineData(1000, 1000)] [InlineData(1200, 1000)] [InlineData(100, 0)]
    public void GoldLootAtZeroHeadroomReadsAuthoritativelyWithoutSavingOrTrimmingHoldings(int balance, int pouch) {
        using var f = new GoldFixture(); using var scope = f.Scope(); f.Store.Saved.GameStats.m_currentGold = balance;
        f.Store.Saved.GameStats.m_baseGoldPouch = pouch; var live = f.Store.Live(); live.GameStats.m_currentGold = 1;
        var reward = new DropTableResult { GoldAmount = 20 }; LootGranter.Grant(ActorRefs.NoSender, live, reward, true);
        Assert.Equal(0, f.Store.SaveAttempts); Assert.Equal(balance, f.Store.Saved.GameStats.m_currentGold); Assert.Equal(1, live.GameStats.m_currentGold);
        Assert.Equal(0, reward.GoldAmount); var update = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>(Assert.Single(f.Packets));
        Assert.Equal(balance, update.Gold); Assert.Equal(pouch, update.MaxGold); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData("missing")] [InlineData("negative-balance")] [InlineData("negative-pouch")]
    [InlineData("prepare-refused")] [InlineData("prepare-threw")]
    public void GoldLootKnownRefusalAdvertisesZeroWithoutUpdateSaveOrQuarantine(string refusal) {
        using var f = new GoldFixture(); using var scope = f.Scope(); var live = f.Store.Live();
        switch (refusal) {
            case "missing": f.Store.RefuseLoad = true; break;
            case "negative-balance": f.Store.Saved.GameStats.m_currentGold = -1; break;
            case "negative-pouch": f.Store.Saved.GameStats.m_baseGoldPouch = -1; break;
            case "prepare-refused": f.Dependencies.Prepare = _ => false; break;
            case "prepare-threw": f.Dependencies.Prepare = _ => throw new InvalidOperationException("Authored gold preparation fault"); break;
        }
        var balance = f.Store.Saved.GameStats.m_currentGold; var reward = new DropTableResult { GoldAmount = 20 };
        LootGranter.Grant(ActorRefs.NoSender, live, reward, true);
        Assert.Equal(0, reward.GoldAmount); Assert.Equal(0, f.Store.SaveAttempts); Assert.Equal(balance, f.Store.Saved.GameStats.m_currentGold);
        Assert.Equal(900, live.GameStats.m_currentGold); Assert.Empty(f.Packets); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData(-20, 900, 880, true)] [InlineData(-901, 900, 900, false)]
    [InlineData(int.MinValue, int.MaxValue, int.MaxValue, false)] [InlineData(-20, 1200, 1180, true)]
    public void GoldLootRetainsReachableExactDebitsWithoutPouchNormalizationOrPositivePopup(int requested, int before, int expected, bool acknowledged) {
        using var f = new GoldFixture(); using var scope = f.Scope(); f.Store.Saved.GameStats.m_currentGold = before;
        var live = f.Store.Live(); var reward = new DropTableResult { GoldAmount = requested };
        LootGranter.Grant(ActorRefs.NoSender, live, reward, true);
        Assert.Equal(0, reward.GoldAmount); Assert.Equal(expected, f.Store.Saved.GameStats.m_currentGold);
        Assert.Equal(expected, live.GameStats.m_currentGold); Assert.Equal(acknowledged ? 1 : 0, f.Store.SaveAttempts);
        if (acknowledged) Assert.Equal(expected, Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>(Assert.Single(f.Packets)).Gold);
        else Assert.Empty(f.Packets);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Fact]
    public void GoldLootScalesOnceAndKeepsGoldXpStacksThenPopupOrder() {
        using var f = new GoldFixture(); using var scope = f.Scope(); f.SetMultiplier("2"); var live = f.Store.Live();
        var reward = Roll(1); reward.GoldAmount = 20; reward.ExperienceAmount = 1;
        LootGranter.Grant(ActorRefs.NoSender, live, reward, true);
        Assert.Equal(40, reward.GoldAmount); Assert.Equal(940, f.Store.Saved.GameStats.m_currentGold);
        var packets = f.Packets.ToArray(); Assert.Equal(5, packets.Length);
        Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>(packets[0]);
        Assert.IsType<Imlight.CoreLib.Shared.Packets.CHARACTER_103_PROTOCOL.MSG_GAINXP>(packets[1]);
        Assert.IsType<WIZARD_12_PROTOCOL.MSG_REAGENTADD>(packets[2]); Assert.IsType<WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION>(packets[3]);
        Assert.Equal(40, GoldFixture.Popup(Assert.IsType<WIZARD_12_PROTOCOL.MSG_LOOT>(packets[4])).m_goldInfo.m_goldAmount);
        Assert.Equal(2, f.Store.SaveAttempts); // Gold and stacks are separate acknowledged writes; XP remains asynchronous.
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GoldLootLostAcknowledgementQuarantinesAndStopsBeforeLaterRewardsOrPopup(bool durable) {
        using var f = new GoldFixture(); using var scope = f.Scope(); var live = f.Store.Live();
        f.Store.FailSave = true; f.Store.CommitBeforeFailure = durable;
        var reward = Roll(1); reward.GoldAmount = 20; reward.ExperienceAmount = 1;
        Assert.Throws<InvalidOperationException>(() => LootGranter.Grant(ActorRefs.NoSender, live, reward, true));
        Assert.Equal(0, reward.GoldAmount); Assert.Equal(1, f.Store.SaveAttempts); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.Equal(durable ? 920 : 900, f.Store.Saved.GameStats.m_currentGold); Assert.Equal(900, live.GameStats.m_currentGold);
        Assert.Equal("Close", Assert.Single(f.Packets)); Assert.Empty(f.Store.Reagents);
        f.Packets.Clear(); Assert.Throws<InvalidOperationException>(() => LootGranter.Grant(ActorRefs.NoSender, live, new() { GoldAmount = 20 }, true));
        Assert.Equal(1, f.Store.SaveAttempts); Assert.Equal("Close", Assert.Single(f.Packets));
    }

    [Fact]
    public void GoldLootAcknowledgedSaveWithLostLivePublicationQuarantinesWithoutAdvertisingTheReceipt() {
        using var f = new GoldFixture(); using var scope = f.Scope(); var live = f.Store.Live(); var alias = live.GameStats;
        f.Store.BeforeSave = () => live.GameStats = null!;
        var reward = new DropTableResult { GoldAmount = 20, ExperienceAmount = 1 };
        Assert.Throws<InvalidOperationException>(() => LootGranter.Grant(ActorRefs.NoSender, live, reward, true));
        Assert.Equal(920, f.Store.Saved.GameStats.m_currentGold); Assert.Equal(900, alias.m_currentGold); Assert.Equal(0, reward.GoldAmount);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); Assert.Equal("Close", Assert.Single(f.Packets));
        Assert.Equal(1, f.Store.AcknowledgedSaves);
    }

    [Fact]
    public void GoldLootUncertaintyRaisedDuringFreshLoadOrPreparationCannotSaveOrReachLaterRewards() {
        foreach (var duringLoad in new[] { true, false }) {
            using var f = new GoldFixture(); using var scope = f.Scope(); var live = f.Store.Live();
            if (duringLoad) f.OnLoad = () => WizardCollection.MarkInventorySnapshotUncertain(live);
            else f.Dependencies.Prepare = _ => { WizardCollection.MarkInventorySnapshotUncertain(live); return true; };
            var reward = new DropTableResult { GoldAmount = 20, ExperienceAmount = 1 };
            Assert.Throws<InvalidOperationException>(() => LootGranter.Grant(ActorRefs.NoSender, live, reward, true));
            Assert.Equal(0, f.Store.SaveAttempts); Assert.Equal(0, reward.GoldAmount); Assert.Equal(900, f.Store.Saved.GameStats.m_currentGold);
            Assert.Equal("Close", Assert.Single(f.Packets));
        }
    }

    [Fact]
    public async Task GoldLootConcurrentGrantsShareTheFreshLaneAndOnlyTheRemainingHeadroomAppearsInEachPopup() {
        using var f = new GoldFixture(); using var scope = f.Scope(); f.Store.Saved.GameStats.m_currentGold = 990;
        var firstLive = f.Store.Live(); var secondLive = f.Store.Live();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var attempted = new ManualResetEventSlim();
        f.Store.BeforeSave = () => { if (f.Store.SaveAttempts == 1) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } };
        var first = new DropTableResult { GoldAmount = 8 }; var second = new DropTableResult { GoldAmount = 8 };
        var a = Task.Run(() => LootGranter.Grant(ActorRefs.NoSender, firstLive, first, true));
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var b = Task.Run(() => { attempted.Set(); LootGranter.Grant(ActorRefs.NoSender, secondLive, second, true); });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5))); release.Set(); await Task.WhenAll(a, b);
        } finally { release.Set(); }
        Assert.Equal(1000, f.Store.Saved.GameStats.m_currentGold); Assert.Equal(2, f.Store.SaveAttempts);
        Assert.Equal(8, first.GoldAmount); Assert.Equal(2, second.GoldAmount);
        Assert.Equal(new[] { 2, 8 }, f.Packets.OfType<WIZARD_12_PROTOCOL.MSG_LOOT>().Select(packet => GoldFixture.Popup(packet).m_goldInfo.m_goldAmount).Order().ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GoldLootNextSaveWaitsUntilThePriorAuthoritativeUpdateIsEnqueued(bool readOnly) {
        using var f = new GoldFixture(); using var scope = f.Scope();
        if (readOnly) f.Store.Saved.GameStats.m_currentGold = 1000;
        var firstLive = f.Store.Live(); var secondLive = f.Store.Live();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var attempted = new ManualResetEventSlim();
        var sends = 0;
        f.OnSend = message => {
            if (message is WIZARD_12_PROTOCOL.MSG_UPDATEGOLD && Interlocked.Increment(ref sends) == 1) {
                entered.Set(); Assert.True(WizardCollection.HoldsWriteLane);
                Assert.Equal(readOnly ? 0 : 1, f.Store.SaveAttempts); Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }
        };
        f.Store.BeforeSave = () => {
            if (f.Store.SaveAttempts == (readOnly ? 1 : 2)) Assert.Equal(readOnly ? 1000 : 920,
                Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>(Assert.Single(f.Packets)).Gold);
        };
        var a = Task.Run(() => LootGranter.Grant(ActorRefs.NoSender, firstLive, new() { GoldAmount = 20 }, false));
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var b = Task.Run(() => { attempted.Set(); LootGranter.Grant(ActorRefs.NoSender, secondLive, new() { GoldAmount = readOnly ? -20 : 20 }, false); });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5))); release.Set(); await Task.WhenAll(a, b);
        } finally { release.Set(); }
        Assert.Equal(readOnly ? 980 : 940, f.Store.Saved.GameStats.m_currentGold); Assert.Equal(readOnly ? 1 : 2, f.Store.SaveAttempts);
        Assert.Equal(readOnly ? new[] { 1000, 980 } : new[] { 920, 940 }, f.Packets.Cast<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>().Select(packet => packet.Gold));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GoldLootSynchronousUpdateSendFailureQuarantinesBeforeAnyLaterRewardOrPopup(bool readOnly) {
        using var f = new GoldFixture(); using var scope = f.Scope();
        if (readOnly) f.Store.Saved.GameStats.m_currentGold = 1000;
        var live = f.Store.Live(); var old = live.GameStats.m_currentGold;
        f.OnSend = message => {
            if (message is WIZARD_12_PROTOCOL.MSG_UPDATEGOLD) {
                Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(readOnly ? 0 : 1, f.Store.SaveAttempts);
                throw new InvalidOperationException("Authored synchronous update enqueue failure");
            }
        };
        var reward = Roll(1); reward.GoldAmount = 20; reward.ExperienceAmount = 1;
        Assert.Throws<InvalidOperationException>(() => LootGranter.Grant(ActorRefs.NoSender, live, reward, true));
        Assert.Equal(0, reward.GoldAmount); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.Equal(readOnly ? 1000 : 920, f.Store.Saved.GameStats.m_currentGold);
        Assert.Equal(readOnly ? old : 920, live.GameStats.m_currentGold);
        Assert.Equal(readOnly ? 0 : 1, f.Store.AcknowledgedSaves); Assert.Empty(f.Store.Reagents);
        Assert.Equal("Close", Assert.Single(f.Packets));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GoldLootQueuedGrantAfterLostAckCannotSaveAgainOrAdvertiseEitherReward(bool durable) {
        using var f = new GoldFixture(); using var scope = f.Scope(); var live = f.Store.Live();
        f.Store.FailSave = true; f.Store.CommitBeforeFailure = durable;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var attempted = new ManualResetEventSlim();
        f.Store.BeforeSave = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var a = Task.Run(() => Assert.Throws<InvalidOperationException>(() => LootGranter.Grant(ActorRefs.NoSender, live, new() { GoldAmount = 20 }, true)));
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var b = Task.Run(() => { attempted.Set(); Assert.Throws<InvalidOperationException>(() => LootGranter.Grant(ActorRefs.NoSender, live, new() { GoldAmount = 20 }, true)); });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5))); release.Set(); await Task.WhenAll(a, b);
        } finally { release.Set(); }
        Assert.Equal(1, f.Store.SaveAttempts); Assert.Equal(durable ? 920 : 900, f.Store.Saved.GameStats.m_currentGold);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); Assert.Equal(2, f.Packets.Count); Assert.All(f.Packets, packet => Assert.Equal("Close", packet));
    }

    private sealed class GoldFixture : IDisposable {
        internal readonly Fixture Store = new(0);
        internal readonly ConcurrentQueue<object> Packets = new();
        internal readonly GoldRewardDependencies Dependencies = new();
        internal System.Action? OnLoad;
        internal System.Action<object>? OnSend;
        private readonly ConcurrentDictionary<string, string> _settings;
        private readonly string? _oldMultiplier;
        internal GoldFixture() {
            Store.Saved.GameStats.m_baseGoldPouch = 1000;
            _settings = (ConcurrentDictionary<string, string>)typeof(ClassicSettingsStore).GetField("_overrides", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ClassicSettings.Store)!;
            _oldMultiplier = _settings.TryGetValue(ClassicSettingKeys.GoldMultiplier, out var old) ? old : null;
            SetMultiplier("1");
        }
        internal void SetMultiplier(string value) => _settings[ClassicSettingKeys.GoldMultiplier] = value;
        internal IDisposable Scope() {
            var inner = Store.Scope(); var send = LootGranter.TestSendScope.Value;
            var gold = ClassicGoldRewards.TestScope.Value; var store = WizardCollection.TestStoreScope.Value!;
            LootGranter.TestSendScope.Value = (_, message) => { OnSend?.Invoke(message); Packets.Enqueue(message); };
            ClassicGoldRewards.TestScope.Value = Dependencies;
            WizardCollection.TestStoreScope.Value = new(store.Open, (session, id) => { OnLoad?.Invoke(); return store.Load(session, id); });
            return new GoldRestore(() => { LootGranter.TestSendScope.Value = send; ClassicGoldRewards.TestScope.Value = gold; inner.Dispose(); });
        }
        internal static LootInfoList Popup(WIZARD_12_PROTOCOL.MSG_LOOT packet) {
            var codec = new ObjectSerializer(Versionable: false);
            Assert.True(codec.Deserialize<LootInfoList>((byte[])packet.LootList, 4, out var loot)); Assert.NotNull(loot);
            return loot;
        }
        public void Dispose() {
            if (_oldMultiplier is null) _settings.TryRemove(ClassicSettingKeys.GoldMultiplier, out _);
            else _settings[ClassicSettingKeys.GoldMultiplier] = _oldMultiplier;
        }
    }
    private sealed class GoldRestore(System.Action restore) : IDisposable { public void Dispose() => restore(); }

    private static DropTableResult Roll(int quantity) => new() { Reagents = [Drop(Fixture.Normal, quantity)] };
    private static DropItemResult Drop(ulong template, int quantity) => new() { ItemId = template.ToString(), ItemName = "Reward reagent", Quantity = quantity };

    // CLASSIC: detached tracked documents model before-save refusal and committed-but-unacknowledged writes.
    private sealed class Fixture {
        internal const ulong Char = 774171, Normal = 774172, Rare = 774173, NormalId = 774174, RareId = 774175, Deck = 774176;
        internal const uint Card = 774177, OtherCard = 774178;
        internal const ulong Gear = 774179, GearId = 774180;
        internal Wizard Saved;
        internal List<ClientReagentItem> Reagents;
        internal List<WizClientObjectItem> Items = [];
        internal readonly List<object> Packets = [];
        internal readonly StackRewardDependencies Dependencies;
        internal bool FailSave, CommitBeforeFailure, RefuseLoad;
        internal int SaveAttempts, AcknowledgedSaves, Reads;
        internal System.Action? BeforeSave;
        private ulong _nextGearId = GearId;
        internal Fixture(int count, int cards = 0) {
            Reagents = count == 0 ? [] : [new() { m_globalID = NormalId, m_permID = NormalId, m_templateID = Normal, m_characterId = Char, m_quantity = count }];
            Saved = new Wizard { CharId = Char,
                GameStats = new ServerWizGameStats(default, 1) { m_currentGold = 900, m_currentArenaPoints = 100 },
                SpellbookBehavior = new() { TreasureCardTemplateIds = Enumerable.Repeat(Card, cards).ToList(),
                    DeckTreasureCards = new() { [Deck] = new() { [OtherCard] = 4 } }, DeckTreasureLedgerVersion = 1 },
                AlchemyBehavior = new() { ReagentItemIds = count == 0 ? [] : [NormalId], Reagents = [] },
                InventoryBehavior = new() { InventoryItemIds = [], Items = [] },
                EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = [] }, StorageBehavior = new() { BankItemIds = [], Items = [] },
            };
            Dependencies = new() {
                Template = id => id == Card ? new SpellTemplate { m_name = "Reward card" }
                    : id == OtherCard ? new SpellTemplate { m_name = "Other reward" }
                    : id == Normal || id == Rare ? new ReagentItemTemplate { m_templateID = (uint)id }
                    : id == Gear ? new WizItemTemplate { m_templateID = (uint)id, m_behaviors = [], m_adjectiveList = ["Hat"] } : null!,
                Create = id => id == Gear ? new WizClientObjectItem { m_globalID = _nextGearId++, m_templateID = id, m_inactiveBehaviors = [] }
                    : new ClientReagentItem { m_globalID = id == Normal ? NormalId : RareId,
                    m_permID = id == Normal ? NormalId : RareId, m_templateID = id },
                SerializeReagent = row => new ByteString(BitConverter.GetBytes(row.m_quantity)),
                SerializeItem = item => new ByteString(BitConverter.GetBytes(item.m_globalID.Full)),
            };
        }
        internal Wizard Live() {
            var live = Clone(Saved); live.AlchemyBehavior.Reagents = Reagents.Select(row => row with { }).ToList();
            live.InventoryBehavior.Items = [..Items.Where(item => live.InventoryBehavior.InventoryItemIds.Contains(item.m_globalID.Full)).Select(item => item with { })]; return live;
        }
        internal void AddGear(ulong id) {
            Items.Add(new() { m_globalID = id, m_templateID = Gear, m_characterId = Char, m_inactiveBehaviors = [] }); Saved.InventoryBehavior.InventoryItemIds.Add(id);
        }
        internal IDisposable Scope() {
            var previousStore = WizardCollection.TestStoreScope.Value;
            var previousRows = WizardReagentCollection.TestRowsScope.Value;
            var previousDependencies = ClassicStackRewards.TestScope.Value;
            var previousSend = LootGranter.TestSendScope.Value;
            var previousItems = WizardInventoryTransactions.TestRowsScope.Value;
            WizardCollection.TestStoreScope.Value = new(Open, (session, id) => {
                Assert.Equal(Char, id); return RefuseLoad ? null! : Session(session).Wizard;
            });
            WizardReagentCollection.TestRowsScope.Value = session => { Reads++; return Session(session).Rows; };
            WizardInventoryTransactions.TestRowsScope.Value = session => Session(session).Items;
            ClassicStackRewards.TestScope.Value = Dependencies;
            LootGranter.TestSendScope.Value = (_, message) => {
                if (message is not string && message is not EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE) Assert.True(AcknowledgedSaves > 0);
                Packets.Add(message);
            };
            return new Restore(() => {
                WizardCollection.TestStoreScope.Value = previousStore;
                WizardReagentCollection.TestRowsScope.Value = previousRows;
                ClassicStackRewards.TestScope.Value = previousDependencies;
                LootGranter.TestSendScope.Value = previousSend;
                WizardInventoryTransactions.TestRowsScope.Value = previousItems;
            });
        }
        private IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, RewardSession>(); var proxy = Session(session);
            proxy.Wizard = Clone(Saved); proxy.Rows = Reagents.Select(row => row with { }).ToList();
            proxy.Items = Items.Select(item => item with { }).ToList();
            proxy.Save = () => {
                Assert.True(WizardCollection.HoldsWriteLane); SaveAttempts++; BeforeSave?.Invoke();
                if (FailSave && !CommitBeforeFailure) throw new InvalidOperationException("fixture refused reward save");
                Saved = Clone(proxy.Wizard); Reagents = proxy.Rows.Select(row => row with { }).ToList();
                Items = proxy.Items.Select(item => item with { }).ToList();
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
            InventoryBehavior = new() { InventoryItemIds = [..saved.InventoryBehavior.InventoryItemIds], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [..saved.EquipmentBehavior.EquippedItemIds], EquippedItems = [] },
            StorageBehavior = new() { BankItemIds = [..saved.StorageBehavior.BankItemIds], Items = [] },
        };
        private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
    }
    public class RewardSession : DispatchProxy {
        internal Wizard Wizard = null!;
        internal List<ClientReagentItem> Rows = [];
        internal List<WizClientObjectItem> Items = [];
        internal System.Action Save = null!;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, RewardAdvanced>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "get_Advanced" => _advanced,
            "Store" => Store(args![0]!),
            "SaveChanges" => SaveNow(),
            "Dispose" => null,
            _ => throw new NotSupportedException(method.Name),
        };
        private object? Store(object row) {
            if (row is ClientReagentItem reagent) Rows.Add(reagent);
            else Items.Add(Assert.IsType<WizClientObjectItem>(row));
            return null;
        }
        private object? SaveNow() { Save(); return null; }
    }
    public class RewardAdvanced : DispatchProxy {
        private readonly IMetadataDictionary _metadata = DispatchProxy.Create<IMetadataDictionary, RewardMetadata>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "set_OptimisticConcurrencyMode" or "IgnoreChangesFor" => null, "GetMetadataFor" => _metadata, _ => throw new NotSupportedException(method.Name),
        };
    }
    public class RewardMetadata : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            Assert.Equal("set_Item", method!.Name); Assert.Equal(Raven.Client.Constants.Documents.Metadata.Collection, args![0]);
            Assert.Contains(args[1], new[] { WizardItemCollection.CollectionName, WizardReagentCollection.CollectionName }); return null;
        }
    }
}
