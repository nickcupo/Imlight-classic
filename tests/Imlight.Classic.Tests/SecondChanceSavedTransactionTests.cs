// CLASSIC: paid chest rewards and their daily use must survive one real, isolated Raven acknowledgement.
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Classic.Settings;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.SecondChance;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Session;
using Raven.Embedded;
using Xunit;
using Action = System.Action;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class SecondChanceSavedTransactionTests(ITestOutputHelper output) {
    private const ulong Owner = 870001, AccountId = 870002, ForeignOwner = 870003, ForeignAccount = 870004;
    private const uint GearTemplate = 870101, ChestTemplate = 870102, OtherChestTemplate = 870103;
    private const uint CardTemplate = 870104, ReagentTemplate = 870105;
    private const ulong Boss = 870106, ChestId = (1UL << 48) | 870107, Instance = 870108;
    private const ulong OriginalBag = (1UL << 48) | 870111, OriginalBank = (1UL << 48) | 870112;
    private const ulong OriginalGear = (1UL << 48) | 870113, Orphan = (1UL << 48) | 870114;
    private const ulong ForeignItem = (1UL << 48) | 870115, OriginalReagent = (1UL << 48) | 870116;
    private const ulong SecondBag = (1UL << 48) | 870117, FirstReward = (1UL << 48) | 870121;
    private const string WizardDocument = "authored-second-chance/wizard/original";
    private const string AccountDocument = "authored-second-chance/account/original";
    private const string Zone = "Authored/SecondChance/Room";
    private static readonly DateTime InitialTime = new(2010, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SecondChanceChest Chest = new("Authored chest", ChestTemplate, Zone, "Authored boss", [Boss]);
    private static readonly SecondChanceRules Rules = new() {
        Id = "authored-second-chance", Profiles = [], FirstCost = 50, CostStep = 50, DailyUses = 5,
        Chests = [Chest], SourceFile = "authored fixture",
    };
    private static readonly MobRewardRules Rewards = new() {
        Id = "authored-second-chance-rewards", Profiles = [], CombatXp = new(0, 0, null, false),
        GoldByRank = ImmutableSortedDictionary<int, RankGold>.Empty, ItemDrops = DropRule.None,
        Mobs = [], SourceFile = "authored fixture",
    };

    [Fact]
    public void OneAcknowledgedSavePersistsFreshPaymentGoldUseAndCompoundAdmittedRewards() {
        Assert.False(PlayerDatabase.IsCreated);
        using var f = new Fixture(output, cards: 998, reagent: 998, uses: 2);
        var originals = f.Database.RawDocuments(f.OriginalDocuments);
        var bagAlias = Assert.Single(f.Live.InventoryBehavior.Items);
        var reagentAlias = Assert.Single(f.Live.AlchemyBehavior.Reagents);
        f.Live.Account.Crowns = 3; f.Live.GameStats.m_currentGold = 7;
        f.Roll = CompoundRoll;
        var opened = f.Open();
        Assert.Equal(SecondChanceStatus.Opened, opened.Status);
        Assert.Equal(2, opened.Quote.Used); Assert.Equal(150, opened.Quote.Cost);
        Assert.Equal(0, f.Database.AcknowledgedSaves); f.Packets.Clear();
        f.Database.AfterAcknowledgement = () => {
            // This hook runs after real Raven ACK and before the transaction publishes to attached aliases.
            Assert.Empty(f.Packets); Assert.Equal(3, f.Live.Account.Crowns); Assert.Equal(7, f.Live.GameStats.m_currentGold);
            Assert.Single(f.Live.InventoryBehavior.Items); Assert.Equal(998, reagentAlias.m_quantity);
            Assert.Equal(998, f.Live.SpellbookBehavior.TreasureCardTemplateIds.Count);
            using var fresh = f.Database.OpenReadSession();
            Assert.Equal(850, fresh.Load<Account>(AccountDocument).Crowns);
            Assert.Equal(1000, fresh.Load<Wizard>(WizardDocument).GameStats.m_currentGold);
            Assert.Equal(3, fresh.Load<SecondChanceUseRecord>(SecondChanceUseRecord.DocumentId(Owner)).Uses[Key(ChestTemplate)]);
        };
        var committed = f.Use();
        Assert.Equal(SecondChanceStatus.Committed, committed.Status);
        Assert.Equal(1, f.Database.AcknowledgedSaves); Assert.Equal(1, f.RollCalls);
        Assert.Equal(3, committed.Receipt.NewUsed); Assert.Equal(850, committed.Receipt.Balance);
        Assert.Equal(1000, committed.Receipt.Gold); Assert.Equal(20, committed.Receipt.AppliedGold);
        var item = Assert.Single(committed.Receipt.Rewards.Items);
        Assert.Equal(FirstReward, item.Item.m_globalID.Full); Assert.True(item.Data.Length > 0);
        Assert.Equal(CardTemplate, Assert.Single(committed.Receipt.Rewards.Cards).TemplateId);
        var reagent = Assert.Single(committed.Receipt.Rewards.Reagents);
        Assert.Equal(1, reagent.Acquired); Assert.Equal(999, reagent.Reagent.m_quantity); Assert.True(reagent.Data.Length > 0);
        Assert.True(committed.Receipt.Rewards.BackpackCapacityExceeded);
        Assert.Same(bagAlias, f.Live.InventoryBehavior.Items.First(owned => owned.m_globalID.Full == OriginalBag));
        Assert.Same(reagentAlias, reagent.Reagent);
        Assert.Equal(850, f.Live.Account.Crowns); Assert.Equal(1000, f.Live.GameStats.m_currentGold);
        Assert.Equal(new[] { OriginalBag, FirstReward }, f.Live.InventoryBehavior.InventoryItemIds);
        Assert.Equal(999, f.Live.SpellbookBehavior.TreasureCardTemplateIds.Count);
        Assert.Single(f.Packets.OfType<WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESULT>());
        Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>(f.Packets[0]);
        Assert.IsType<WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESULT>(f.Packets[^1]);
        var itemPacket = Assert.Single(f.Packets.OfType<GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM>());
        var itemBytes = (byte[])itemPacket.SerializedItem;
        Assert.Equal((byte)115, itemBytes[0]); Assert.Equal((byte)9, itemBytes[1]);
        Assert.Equal(GearTemplate, BinaryPrimitives.ReadUInt32LittleEndian(itemBytes.AsSpan(2, 4)));
        Assert.Equal((byte[])item.Data, itemBytes);
        Assert.Equal(new[] { .125f, .25f, .5f }, f.Live.GameStats.m_dmgReducePercent);
        Assert.Equal(new[] { .75f, .875f }, f.Live.GameStats.m_accBonusPercent);
        AssertPaidLoot(f.Packets, gold: 20, item: GearTemplate, reagentCopies: 1, card: true);

        using (var fresh = f.Database.OpenReadSession()) {
            var saved = fresh.Load<Wizard>(WizardDocument); var account = fresh.Load<Account>(AccountDocument);
            Assert.Equal(AccountId, saved.AccountId); Assert.Equal(new[] { Owner }, account.CharacterIds);
            Assert.Equal(850, account.Crowns); Assert.Equal(42, account.StartingCrownsGiven);
            Assert.Equal(new[] { OriginalBag, FirstReward }, saved.InventoryBehavior.InventoryItemIds);
            Assert.Equal(new[] { OriginalBank }, saved.StorageBehavior.BankItemIds);
            Assert.Equal(new[] { OriginalGear }, saved.EquipmentBehavior.EquippedItemIds);
            Assert.Equal(new[] { OriginalReagent }, saved.AlchemyBehavior.ReagentItemIds);
            Assert.Equal(999, saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
            Assert.Equal(CardTemplate, saved.SpellbookBehavior.TreasureCardTemplateIds[^1]);
            Assert.Equal(2, saved.SpellbookBehavior.DeckTreasureCards[OriginalGear][CardTemplate]);
            var rows = WizardInventoryTransactions.ItemQuery(fresh).ToList();
            var awarded = Assert.Single(rows, row => row.m_globalID.Full == FirstReward);
            Assert.Equal(Owner, awarded.m_characterId.Full); Assert.Equal((ulong)GearTemplate, awarded.m_templateID.Full);
            Assert.Equal(FirstReward, awarded.m_permID.Full);
            Assert.DoesNotContain(rows, row => row.m_globalID.Full == FirstReward + 1);
            Assert.Equal(WizardItemCollection.CollectionName,
                fresh.Advanced.GetMetadataFor(awarded)[Raven.Client.Constants.Documents.Metadata.Collection]);
            Assert.Equal(999, fresh.Load<ClientReagentItem>(ItemDocument(OriginalReagent)).m_quantity);
            AssertUseRecord(fresh, used: 3, other: 4, DateOnly.FromDateTime(InitialTime));
            AssertPreservedWizard(saved);
        }
        var after = f.Database.RawDocuments(f.OriginalDocuments);
        AssertUnchangedDocuments(originals, after, f.UnchangedDocuments);
        AssertPreservedChangedRows(originals, after, compound: true);

        // Make the durable limit newer than the still-open live prompt/cache. A refused fresh read writes nothing.
        using (var external = f.Database.OpenReadSession()) {
            external.Load<SecondChanceUseRecord>(SecondChanceUseRecord.DocumentId(Owner)).Uses[Key(ChestTemplate)] = 5;
            external.SaveChanges();
        }
        var beforeRefusal = f.Database.RawDocuments(f.OriginalDocuments);
        f.Packets.Clear();
        var refused = f.Use();
        Assert.Equal(SecondChanceStatus.Refused, refused.Status); Assert.Equal(ChestRefusal.QuoteChanged, refused.Refusal);
        Assert.Equal(1, f.Database.AcknowledgedSaves); Assert.Equal(1, f.RollCalls);
        Assert.DoesNotContain(f.Packets, message => message is WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESULT);
        AssertUnchangedDocuments(beforeRefusal, f.Database.RawDocuments(f.OriginalDocuments), f.OriginalDocuments);
        Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Packets.Clear();
        Assert.Equal(ChestRefusal.NoUsesLeft, f.Use().Refusal);
        Assert.Equal(1, f.Database.AcknowledgedSaves); Assert.Equal(1, f.RollCalls);
        AssertUnchangedDocuments(beforeRefusal, f.Database.RawDocuments(f.OriginalDocuments), f.OriginalDocuments);
        Assert.False(PlayerDatabase.IsCreated);
    }

    [Fact]
    public void RealSavedFullCapacitiesStillCommitOnePaidUseAndNoPhantomRewards() {
        Assert.False(PlayerDatabase.IsCreated);
        using var f = new Fixture(output, cards: 999, reagent: 999, uses: 0, fullBackpack: true);
        var before = f.Database.RawDocuments(f.OriginalDocuments);
        // Prepared reward admission must use the saved rows/references, not these stale attached capacities.
        f.Live.InventoryBehavior.InventoryItemIds.Clear(); f.Live.InventoryBehavior.Items.Clear();
        f.Live.SpellbookBehavior.TreasureCardTemplateIds.Clear(); f.Live.AlchemyBehavior.Reagents[0].m_quantity = 1;
        f.Roll = () => CompoundRoll(gold: 0);
        Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Packets.Clear();
        var result = f.Use();
        Assert.Equal(SecondChanceStatus.Committed, result.Status); Assert.Equal(1, f.Database.AcknowledgedSaves);
        Assert.Equal(1, f.RollCalls); Assert.Equal(950, result.Receipt.Balance); Assert.Equal(1, result.Receipt.NewUsed);
        Assert.Equal(0, result.Receipt.AppliedGold); Assert.Empty(result.Receipt.Rewards.Items);
        Assert.Empty(result.Receipt.Rewards.Cards); Assert.Empty(result.Receipt.Rewards.Reagents);
        Assert.True(result.Receipt.Rewards.BackpackCapacityExceeded);
        AssertPaidLoot(f.Packets, gold: 0, item: null, reagentCopies: 0, card: false);
        using (var fresh = f.Database.OpenReadSession()) {
            var saved = fresh.Load<Wizard>(WizardDocument);
            Assert.Equal(new[] { OriginalBag, SecondBag }, saved.InventoryBehavior.InventoryItemIds);
            Assert.Equal(999, saved.SpellbookBehavior.TreasureCardTemplateIds.Count);
            Assert.Equal(999, fresh.Load<ClientReagentItem>(ItemDocument(OriginalReagent)).m_quantity);
            Assert.DoesNotContain(WizardInventoryTransactions.ItemQuery(fresh).ToList(), row => row.m_globalID.Full is >= FirstReward and <= FirstReward + 1);
            AssertUseRecord(fresh, 1, 4, DateOnly.FromDateTime(InitialTime));
            AssertPreservedWizard(saved);
        }
        var after = f.Database.RawDocuments(f.OriginalDocuments);
        AssertUnchangedDocuments(before, after, f.UnchangedDocuments.Concat([WizardDocument, ItemDocument(OriginalReagent)]));
        Assert.False(PlayerDatabase.IsCreated);
    }

    [Fact]
    public void ValidEmptyRollPersistsOnlyOneChargeAndDailyUseWithNativeEmptyLoot() {
        Assert.False(PlayerDatabase.IsCreated);
        using var f = new Fixture(output, cards: 1, reagent: 5, uses: 1);
        var before = f.Database.RawDocuments(f.OriginalDocuments);
        f.Roll = () => new DropTableResult();
        Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Packets.Clear();
        var result = f.Use();
        Assert.Equal(SecondChanceStatus.Committed, result.Status); Assert.Equal(1, f.Database.AcknowledgedSaves);
        Assert.Equal(1, f.RollCalls); Assert.Equal(900, result.Receipt.Balance); Assert.Equal(2, result.Receipt.NewUsed);
        Assert.Equal(0, result.Receipt.AppliedGold); Assert.Empty(result.Receipt.Rewards.Items);
        Assert.Empty(result.Receipt.Rewards.Cards); Assert.Empty(result.Receipt.Rewards.Reagents);
        AssertPaidLoot(f.Packets, 0, null, 0, false);
        using (var fresh = f.Database.OpenReadSession()) AssertUseRecord(fresh, 2, 4, DateOnly.FromDateTime(InitialTime));
        AssertUnchangedDocuments(before, f.Database.RawDocuments(f.OriginalDocuments),
            f.UnchangedDocuments.Concat([WizardDocument, ItemDocument(OriginalReagent)]));
        Assert.False(PlayerDatabase.IsCreated);
    }

    [Fact]
    public void PositiveGoldRollPreservesOverfullSavedGoldAndCreatesOneCanonicalUseRecord() {
        Assert.False(PlayerDatabase.IsCreated);
        using var f = new Fixture(output, cards: 1, reagent: 5, uses: 0, initialGold: 1100, seedUses: false);
        var before = f.Database.RawDocuments(f.OriginalDocuments);
        f.Live.GameStats.m_currentGold = 7;
        f.Roll = () => new DropTableResult { GoldAmount = 17 };
        Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Packets.Clear();
        var result = f.Use();
        Assert.Equal(SecondChanceStatus.Committed, result.Status); Assert.Equal(1, f.Database.AcknowledgedSaves);
        Assert.Equal(1, f.RollCalls); Assert.Equal(950, result.Receipt.Balance);
        Assert.Equal(1100, result.Receipt.Gold); Assert.Equal(0, result.Receipt.AppliedGold);
        AssertPaidLoot(f.Packets, 0, null, 0, false);
        using (var fresh = f.Database.OpenReadSession()) {
            var saved = fresh.Load<Wizard>(WizardDocument);
            Assert.Equal(1100, saved.GameStats.m_currentGold); AssertPreservedWizard(saved);
            var record = fresh.Load<SecondChanceUseRecord>(SecondChanceUseRecord.DocumentId(Owner));
            Assert.NotNull(record); Assert.Equal(Owner, record.CharId); Assert.Equal("2010-10-20", record.Day);
            Assert.Equal(Key(ChestTemplate), Assert.Single(record.Uses).Key); Assert.Equal(1, Assert.Single(record.Uses).Value);
            Assert.Equal(SecondChanceUseRecord.DocumentId(Owner), fresh.Advanced.GetDocumentId(record));
            Assert.Equal(SecondChanceUseRecord.CollectionName,
                fresh.Advanced.GetMetadataFor(record)[Raven.Client.Constants.Documents.Metadata.Collection]);
            Assert.Equal(1, fresh.Query<SecondChanceUseRecord>(collectionName: SecondChanceUseRecord.CollectionName)
                .Count(row => row.CharId == Owner));
        }
        AssertUnchangedDocuments(before, f.Database.RawDocuments(f.OriginalDocuments),
            f.UnchangedDocuments.Concat([WizardDocument, ItemDocument(OriginalReagent)]));
        Assert.False(PlayerDatabase.IsCreated);
    }

    [Fact]
    public void LoadedCacheCannotResetSavedCountsAndDayRolloverUsesFreshBalanceAndOneCanonicalRecord() {
        Assert.False(PlayerDatabase.IsCreated);
        using var f = new Fixture(output, cards: 1, reagent: 5, uses: 3);
        var before = f.Database.RawDocuments(f.OriginalDocuments);
        f.Roll = () => new DropTableResult();
        Assert.Equal(SecondChanceStatus.Opened, f.Open().Status);
        Assert.Equal(200, f.Open().Quote.Cost); Assert.Equal(0, f.Database.AcknowledgedSaves);
        f.Packets.Clear(); Assert.Equal(SecondChanceStatus.Committed, f.Use().Status);
        using (var fresh = f.Database.OpenReadSession()) AssertUseRecord(fresh, 4, 4, DateOnly.FromDateTime(InitialTime));
        Assert.Equal(1, f.Database.AcknowledgedSaves);
        f.Now = InitialTime.AddDays(1);
        f.State.RecordWin(Owner, Zone, Instance, [Boss]);
        using (var external = f.Database.OpenReadSession()) {
            external.Load<Account>(AccountDocument).Crowns = 49;
            external.SaveChanges();
        }
        var next = f.Open();
        Assert.Equal(SecondChanceStatus.Opened, next.Status); Assert.Equal(0, next.Quote.Used); Assert.Equal(50, next.Quote.Cost);
        var priorRefusal = f.Database.RawDocuments(f.OriginalDocuments); f.Packets.Clear();
        var refused = f.Use();
        Assert.Equal(SecondChanceStatus.Refused, refused.Status); Assert.Equal(ChestRefusal.NotEnoughCrowns, refused.Refusal);
        Assert.Equal(1, f.Database.AcknowledgedSaves); Assert.Equal(1, f.RollCalls);
        AssertUnchangedDocuments(priorRefusal, f.Database.RawDocuments(f.OriginalDocuments), f.OriginalDocuments);
        using (var external = f.Database.OpenReadSession()) {
            external.Load<Account>(AccountDocument).Crowns = 500;
            external.SaveChanges();
        }
        Assert.Equal(SecondChanceStatus.Opened, f.Open().Status); f.Packets.Clear();
        var rollover = f.Use();
        Assert.Equal(SecondChanceStatus.Committed, rollover.Status); Assert.Equal(2, f.Database.AcknowledgedSaves);
        Assert.Equal(2, f.RollCalls); Assert.Equal(450, rollover.Receipt.Balance); Assert.Equal(1, rollover.Receipt.NewUsed);
        using (var fresh = f.Database.OpenReadSession()) {
            var record = fresh.Load<SecondChanceUseRecord>(SecondChanceUseRecord.DocumentId(Owner));
            Assert.Equal(Owner, record.CharId); Assert.Equal("2010-10-21", record.Day);
            Assert.Equal(1, Assert.Single(record.Uses).Value); Assert.Equal(Key(ChestTemplate), Assert.Single(record.Uses).Key);
            Assert.Equal(1, fresh.Query<SecondChanceUseRecord>(collectionName: SecondChanceUseRecord.CollectionName)
                .Count(row => row.CharId == Owner));
            Assert.Equal(450, fresh.Load<Account>(AccountDocument).Crowns);
            Assert.Equal(SecondChanceUseRecord.DocumentId(Owner), fresh.Advanced.GetDocumentId(record));
            Assert.Equal(SecondChanceUseRecord.CollectionName,
                fresh.Advanced.GetMetadataFor(record)[Raven.Client.Constants.Documents.Metadata.Collection]);
        }
        AssertUnchangedDocuments(before, f.Database.RawDocuments(f.OriginalDocuments),
            f.UnchangedDocuments.Concat([WizardDocument, ItemDocument(OriginalReagent)]));
        Assert.False(PlayerDatabase.IsCreated);
    }

    private static DropTableResult CompoundRoll() => CompoundRoll(17);
    private static DropTableResult CompoundRoll(int gold) => new() {
        GoldAmount = gold, Items = [Drop(GearTemplate, 99), Drop(GearTemplate, 99)],
        TreasureCards = [CardTemplate, CardTemplate, CardTemplate], Reagents = [Drop(ReagentTemplate, 3)],
    };
    private static DropItemResult Drop(ulong id, int quantity) => new() { ItemId = Key(id), Quantity = quantity };
    private static string Key(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static string ItemDocument(ulong id) => "authored-second-chance/item/" + Key(id);

    private static void AssertPaidLoot(IEnumerable<IMessage> messages, int gold, ulong? item, int reagentCopies, bool card) {
        var result = Assert.Single(messages.OfType<WIZARD_12_PROTOCOL.MSG_PAID_LOOT_ROLL_RESULT>());
        var codec = new ObjectSerializer(Behaviors: SerializerFlags.None);
        Assert.True(codec.Deserialize<LootInfoList>((byte[])result.Loot, 5, out var loot));
        Assert.NotNull(loot);
        if (gold == 0) Assert.Null(loot.m_goldInfo); else Assert.Equal(gold, loot.m_goldInfo.m_goldAmount);
        var items = loot.m_loot.OfType<ItemLootInfo>().ToArray();
        Assert.Equal((item.HasValue ? 1 : 0) + (reagentCopies > 0 ? 1 : 0), items.Length);
        if (item.HasValue) {
            var gear = Assert.Single(items, row => row.m_itemID.Full == item.Value); Assert.Equal(1, gear.m_numItems);
        }
        if (reagentCopies > 0) {
            var reagent = Assert.Single(items, row => row.m_itemID.Full == ReagentTemplate); Assert.Equal(reagentCopies, reagent.m_numItems);
        }
        var cards = loot.m_loot.OfType<TreasureCardLootInfo>().ToArray();
        if (card) Assert.Equal(StringHash.Compute("Authored second chance card"), Assert.Single(cards).m_spellID);
        else Assert.Empty(cards);
        Assert.Equal(items.Length + cards.Length, loot.m_loot.Count);
    }

    private static void AssertUseRecord(IDocumentSession session, int used, int other, DateOnly day) {
        var record = session.Load<SecondChanceUseRecord>(SecondChanceUseRecord.DocumentId(Owner));
        Assert.NotNull(record); Assert.Equal(Owner, record.CharId); Assert.Equal(SecondChanceUseRecord.DayText(day), record.Day);
        Assert.Equal(2, record.Uses.Count); Assert.Equal(used, record.Uses[Key(ChestTemplate)]);
        Assert.Equal(other, record.Uses[Key(OtherChestTemplate)]);
        Assert.Equal(SecondChanceUseRecord.DocumentId(Owner), session.Advanced.GetDocumentId(record));
        var metadata = session.Advanced.GetMetadataFor(record);
        Assert.Equal(SecondChanceUseRecord.CollectionName, metadata[Raven.Client.Constants.Documents.Metadata.Collection]);
        Assert.Equal("retained authored metadata", metadata["authored-proof"]);
    }

    private static void AssertPreservedWizard(Wizard wizard) {
        Assert.Equal(67, wizard.GameStats.m_currentHitpoints); Assert.Equal(31, wizard.GameStats.m_currentMana);
        Assert.Equal(456, wizard.GameStats.m_currentArenaPoints); Assert.Equal(457, wizard.GameStats.m_currentPvPCurrency);
        Assert.Equal(13, wizard.GameStats.m_currentEventCurrency1); Assert.Equal(17, wizard.GameStats.m_currentEventCurrency2);
        Assert.Equal(.75f, wizard.GameStats.m_potionCharge); Assert.Equal(new[] { 17, 23, 41 }, wizard.GameStats.m_spellChargeBase);
        Assert.True(wizard.GameStats.m_shadowMagicUnlocked); Assert.Equal(77, wizard.MagicSchoolBehavior.TrainingPoints);
        Assert.Equal(1234, wizard.MagicSchoolBehavior.ExperiencePoints);
        Assert.Equal(new uint[] { 870999 }, wizard.SpellbookBehavior.LearnedSpellTemplateIds);
        var slot = Assert.Single(wizard.EquipmentBehavior.SlotList);
        Assert.Equal(OriginalGear, slot.ItemId.Full); Assert.Equal(EquipmentSlotType.Deck, slot.SlotType);
        Assert.Equal(new DateTime(2010, 10, 1), slot.EquippedSince);
    }

    private static void AssertUnchangedDocuments(IReadOnlyDictionary<string, JToken> before,
        IReadOnlyDictionary<string, JToken> after, IEnumerable<string> ids) {
        foreach (var id in ids.Distinct()) {
            Assert.Equal(before[id]["@metadata"]!["@change-vector"]!.Value<string>(),
                after[id]["@metadata"]!["@change-vector"]!.Value<string>());
            Assert.True(JToken.DeepEquals(before[id], after[id]), "Unexpected saved document change: " + id);
        }
    }

    private static void AssertPreservedChangedRows(IReadOnlyDictionary<string, JToken> before,
        IReadOnlyDictionary<string, JToken> after, bool compound) {
        foreach (var id in new[] { AccountDocument, WizardDocument, SecondChanceUseRecord.DocumentId(Owner), ItemDocument(OriginalReagent) }) {
            Assert.Equal(id, after[id]["@metadata"]!["@id"]!.Value<string>());
            Assert.Equal(before[id]["@metadata"]!["@collection"]!.Value<string>(), after[id]["@metadata"]!["@collection"]!.Value<string>());
            Assert.Equal("retained authored metadata", after[id]["@metadata"]!["authored-proof"]!.Value<string>());
        }
        var oldStats = (JObject)before[WizardDocument]["GameStats"]!.DeepClone();
        var newStats = (JObject)after[WizardDocument]["GameStats"]!.DeepClone();
        oldStats.Remove("m_currentGold"); newStats.Remove("m_currentGold");
        Assert.True(JToken.DeepEquals(oldStats, newStats), "Paid gold rewrote unrelated saved stats/vectors.");
        var oldAccount = (JObject)before[AccountDocument].DeepClone();
        var newAccount = (JObject)after[AccountDocument].DeepClone();
        oldAccount.Remove("Crowns"); newAccount.Remove("Crowns"); oldAccount.Remove("@metadata"); newAccount.Remove("@metadata");
        Assert.True(JToken.DeepEquals(oldAccount, newAccount), "Paid crowns rewrote unrelated account fields.");
        if (compound) {
            var oldReagent = (JObject)before[ItemDocument(OriginalReagent)].DeepClone();
            var newReagent = (JObject)after[ItemDocument(OriginalReagent)].DeepClone();
            oldReagent.Remove("m_quantity"); newReagent.Remove("m_quantity"); oldReagent.Remove("@metadata"); newReagent.Remove("@metadata");
            Assert.True(JToken.DeepEquals(oldReagent, newReagent), "Paid reward rewrote unrelated reagent identity/state.");
        }
    }

    private sealed class Fixture : IDisposable {
        internal readonly SavedDatabase Database;
        internal readonly Wizard Live;
        internal readonly SecondChanceChests State;
        internal readonly List<IMessage> Packets = [];
        internal readonly string[] OriginalDocuments, UnchangedDocuments;
        internal DateTime Now = InitialTime;
        internal Func<DropTableResult> Roll = () => new();
        internal int RollCalls;
        private ulong _nextReward = FirstReward;
        private readonly WizardCollection.TestStore? _previousStore;
        private readonly StackRewardDependencies? _previousStack;
        private readonly SecondChanceDependencies? _previousSecondChance;
        private readonly ConcurrentDictionary<string, string> _settings;
        private readonly Dictionary<string, string?> _oldSettings = [];

        internal Fixture(ITestOutputHelper output, int cards, int reagent, int uses, bool fullBackpack = false,
            int initialGold = 980, bool seedUses = true) {
            Database = new SavedDatabase(output);
            try {
            _settings = (ConcurrentDictionary<string, string>)typeof(ClassicSettingsStore)
                .GetField("_overrides", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ClassicSettings.Store)!;
            _previousStore = WizardCollection.TestStoreScope.Value; _previousStack = ClassicStackRewards.TestScope.Value;
            _previousSecondChance = ClassicSecondChanceTransactions.TestScope.Value;
            foreach (var (key, value) in new[] { (ClassicSettingKeys.GoldMultiplier, "2"), (ClassicSettingKeys.BackpackSize, "2") }) {
                _oldSettings[key] = _settings.TryGetValue(key, out var old) ? old : null; _settings[key] = value;
            }
            Assert.Equal(2d, ClassicSettings.GoldMultiplier); Assert.Equal(2, ServerWizInventoryBehavior.MaxItemsAllowed);
            var bag = fullBackpack ? new[] { OriginalBag, SecondBag } : new[] { OriginalBag };
            var unchanged = new List<string> { ItemDocument(OriginalBag), ItemDocument(OriginalBank), ItemDocument(OriginalGear),
                ItemDocument(Orphan), ItemDocument(ForeignItem), "authored-second-chance/unrelated",
                "authored-second-chance/wizard/foreign", "authored-second-chance/account/foreign" };
            if (fullBackpack) unchanged.Add(ItemDocument(SecondBag));
            UnchangedDocuments = unchanged.ToArray();
            OriginalDocuments = [.. UnchangedDocuments, WizardDocument, AccountDocument, ItemDocument(OriginalReagent),
                .. (seedUses ? new[] { SecondChanceUseRecord.DocumentId(Owner) } : Array.Empty<string>())];
            using (var session = Database.OpenReadSession()) {
                Store(session, AuthoredAccount(AccountId, Owner), AccountDocument, AccountCollection.CollectionName);
                Store(session, AuthoredAccount(ForeignAccount, ForeignOwner), "authored-second-chance/account/foreign", AccountCollection.CollectionName);
                var wizard = SavedWizard(Owner, AccountId, bag, cards); wizard.GameStats.m_currentGold = initialGold;
                Store(session, wizard, WizardDocument, WizardCollection.CollectionName);
                Store(session, SavedWizard(ForeignOwner, ForeignAccount, [], 0), "authored-second-chance/wizard/foreign", WizardCollection.CollectionName);
                foreach (var id in new[] { OriginalBag, OriginalBank, OriginalGear, Orphan }
                    .Concat(fullBackpack ? new[] { SecondBag } : Array.Empty<ulong>()))
                    Store(session, Item(id, Owner), ItemDocument(id), WizardItemCollection.CollectionName);
                Store(session, Item(ForeignItem, ForeignOwner), ItemDocument(ForeignItem), WizardItemCollection.CollectionName);
                Store(session, new ClientReagentItem { m_globalID = OriginalReagent, m_permID = OriginalReagent,
                    m_templateID = ReagentTemplate, m_characterId = Owner, m_quantity = reagent, m_debugName = "Authored original stack" },
                    ItemDocument(OriginalReagent), WizardReagentCollection.CollectionName);
                if (seedUses) Store(session, new SecondChanceUseRecord { CharId = Owner, Day = "2010-10-20",
                    Uses = new() { [Key(ChestTemplate)] = uses, [Key(OtherChestTemplate)] = 4 } },
                    SecondChanceUseRecord.DocumentId(Owner), SecondChanceUseRecord.CollectionName);
                Store(session, new Dictionary<string, object> { ["scalar"] = 917, ["vector"] = new[] { 3, 19, 41 },
                    ["nested"] = new Dictionary<string, object> { ["retain"] = "authored original" } },
                    "authored-second-chance/unrelated", "AuthoredUnrelated");
                session.SaveChanges();
            }
            using (var session = Database.OpenReadSession()) {
                Live = session.Load<Wizard>(WizardDocument); Live.Account = session.Load<Account>(AccountDocument);
                Live.GameStats.m_dmgReducePercent = [.125f, .25f, .5f]; Live.GameStats.m_accBonusPercent = [.75f, .875f];
                Live.Account.Characters.Add(Live);
                Live.InventoryBehavior.Items = [.. WizardInventoryTransactions.ItemQuery(session)
                    .Where(row => row.m_characterId == Owner).ToList().Where(row => bag.Contains(row.m_globalID.Full))];
                Live.EquipmentBehavior.EquippedItems = [Item(OriginalGear, Owner)];
                Live.StorageBehavior.Items = [Item(OriginalBank, Owner)];
                Live.AlchemyBehavior.Reagents = [session.Load<ClientReagentItem>(ItemDocument(OriginalReagent))];
            }
            State = new SecondChanceChests(() => Now, new MisleadingUseStore(), DateOnly.FromDateTime);
            State.RecordWin(Owner, Zone, Instance, [Boss]);
            // Prime the old cache with a false zero. The real transaction must replace it with durable authority.
            Assert.Equal(5, State.UsesLeftForFixture(Owner, Chest, Rules));
            Assert.Null(WizardInventoryTransactions.TestRowsScope.Value); Assert.Null(WizardReagentCollection.TestRowsScope.Value);
            WizardCollection.TestStoreScope.Value = new(Database.OpenTransactionSession, null!);
            ClassicStackRewards.TestScope.Value = new() {
                Template = id => id == GearTemplate ? new WizItemTemplate { m_templateID = GearTemplate, m_behaviors = [], m_adjectiveList = ["Hat"] }
                    : id == CardTemplate ? new SpellTemplate { m_name = "Authored second chance card" }
                    : id == ReagentTemplate ? new ReagentItemTemplate { m_templateID = ReagentTemplate } : null!,
                Create = id => id == GearTemplate ? Item(_nextReward++, Owner)
                    : new ClientReagentItem { m_globalID = OriginalReagent, m_permID = OriginalReagent,
                        m_templateID = ReagentTemplate, m_characterId = Owner },
            };
            ClassicSecondChanceTransactions.TestScope.Value = new() { Roll = (boss, _) => {
                Assert.Equal(Boss, boss); RollCalls++; return Roll();
            } };
            }
            catch { Dispose(); throw; }
        }
        internal SecondChanceResult Open() => ClassicSecondChanceTransactions.TryOpen(Live, ChestId, Chest, Zone,
            Instance, Rules, messages => Packets.AddRange(messages), () => true, State);
        internal SecondChanceResult Use() => ClassicSecondChanceTransactions.TryUse(Live, ChestId, Zone,
            Instance, Rules, Rewards, messages => Packets.AddRange(messages), () => true, State);
        public void Dispose() {
            try {
                WizardCollection.TestStoreScope.Value = _previousStore; ClassicStackRewards.TestScope.Value = _previousStack;
                ClassicSecondChanceTransactions.TestScope.Value = _previousSecondChance;
            }
            finally {
                try {
                    foreach (var (key, old) in _oldSettings) {
                        if (old is null) _settings?.TryRemove(key, out _); else _settings![key] = old;
                    }
                }
                finally { Database.Dispose(); }
            }
        }
    }

    private sealed class MisleadingUseStore : ISecondChanceUseStore {
        public IReadOnlyDictionary<ulong, int> Load(ulong charId, DateOnly day) => new Dictionary<ulong, int> { [ChestTemplate] = 0 };
        public void Save(ulong charId, DateOnly day, IReadOnlyDictionary<ulong, int> uses)
            => throw new InvalidOperationException("The atomic proof must not use a second use-store save.");
    }

    private static Account AuthoredAccount(ulong id, ulong owner) {
        var account = new Account { Crowns = 1000, StartingCrownsGiven = 42,
            LastLoginTime = new DateTime(2010, 10, 1), LastLoginMachineId = 870199,
            LastLoginIp = "127.0.0.1", PurchasedCharacterSlots = 1, AuthLevel = AuthLevel.None, ChatMode = ChatMode.Filtered };
        typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(account, id);
        account.CharacterIds.Add(owner); return account;
    }
    private static WizClientObjectItem Item(ulong id, ulong owner) => new() {
        m_globalID = id, m_permID = id, m_templateID = GearTemplate, m_characterId = owner,
        m_debugName = "Authored original gear", m_primaryColor = 7, m_secondaryColor = 11, m_pattern = 3, m_inactiveBehaviors = [],
    };
    private static Wizard SavedWizard(ulong owner, ulong account, ulong[] bag, int cards) => new() {
        CharId = owner, AccountId = account, Zone = Zone, PlayerNameBehavior = new() { NameOverride = "Authored second chance wizard" },
        GameStats = new(default, 1) { m_baseGoldPouch = 1000, m_currentGold = 980, m_currentHitpoints = 67, m_currentMana = 31,
            m_currentArenaPoints = 456, m_currentPvPCurrency = 457, m_currentEventCurrency1 = 13, m_currentEventCurrency2 = 17,
            m_potionCharge = .75f, m_spellChargeBase = [17, 23, 41], m_dmgReducePercent = [.125f, .25f, .5f],
            m_accBonusPercent = [.75f, .875f], m_shadowMagicUnlocked = true },
        MagicSchoolBehavior = new() { Level = 1, ExperiencePoints = 1234, TrainingPoints = 77 },
        InventoryBehavior = new() { InventoryItemIds = [.. bag], Items = [] },
        StorageBehavior = new() { BankItemIds = owner == Owner ? [OriginalBank] : [], Items = [] },
        EquipmentBehavior = new() { EquippedItemIds = owner == Owner ? [OriginalGear] : [], EquippedItems = [],
            SlotList = owner == Owner ? [new EquipmentSlot { ItemId = OriginalGear, SlotType = EquipmentSlotType.Deck,
                ItemName = "Authored retained deck", EquippedSince = new DateTime(2010, 10, 1) }] : [] },
        SpellbookBehavior = new() { TreasureCardTemplateIds = Enumerable.Repeat(CardTemplate, cards).ToList(), LearnedSpellTemplateIds = [870999],
            TemporarySpells = [], DeckTreasureCards = new() { [OriginalGear] = new() { [CardTemplate] = 2 } } },
        AlchemyBehavior = new() { ReagentItemIds = owner == Owner ? [OriginalReagent] : [], Reagents = [], Recipes = [], CraftingSlots = [] },
        QuestBehavior = new(), PetSnackBehavior = new() { SnackItemIds = [], Snacks = [] },
    };
    private static void Store<T>(IDocumentSession session, T entity, string id, string collection) where T : class {
        session.Store(entity, id);
        var metadata = session.Advanced.GetMetadataFor(entity);
        metadata[Raven.Client.Constants.Documents.Metadata.Collection] = collection;
        metadata["authored-proof"] = "retained authored metadata";
    }

    private sealed class SavedDatabase : IDisposable {
        internal readonly IDocumentStore Store = null!;
        internal int AcknowledgedSaves;
        internal Action? AfterAcknowledgement;
        private readonly EmbeddedServer? _server;
        private readonly ConfigurationSnapshot _configuration = new();
        private bool _disposed;
        internal SavedDatabase(ITestOutputHelper output) {
            try {
            var root = Path.Combine(Path.GetTempPath(), "w101c-second-chance-saved-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var config = Path.Combine(root, "authored-fixture.ini");
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(root, "fixture.log")}\n"
                + $"[Classic]\nSettingsOverridePath={Path.Combine(root, "authored-settings.json")}\nBackpackSize=2\nGoldMultiplier=2\n"
                + "[Character]\nBaseGoldPouch=1000\nMaxInventoryItems=2\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=30\n");
            ConfigurationManager.Initialize(config);
            output.WriteLine("Retained authored Second Chance database and logs: " + root);
            var ctor = typeof(EmbeddedServer).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, System.Type.EmptyTypes, modifiers: null);
            Assert.NotNull(ctor); _server = (EmbeddedServer)ctor.Invoke(null); Assert.NotSame(EmbeddedServer.Instance, _server);
            var serverDirectory = Path.Combine(AppContext.BaseDirectory, "RavenDBServer");
            Assert.True(File.Exists(Path.Combine(serverDirectory, "Raven.Server.dll")), "Bundled Raven must be available.");
            var userDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
                _server.StartServer(new ServerOptions { ServerUrl = "http://127.0.0.1:0", ServerDirectory = serverDirectory,
                    DotNetPath = !string.IsNullOrWhiteSpace(host) ? host : File.Exists(userDotnet) ? userDotnet : "dotnet",
                    DataDirectory = Path.Combine(root, "data"), LogsPath = Path.Combine(root, "logs"),
                    MaxServerStartupTimeDuration = TimeSpan.FromSeconds(45), GracefulShutdownTimeout = TimeSpan.FromSeconds(10) });
                Store = _server.GetDocumentStore(new DatabaseOptions("authored-second-chance-" + Guid.NewGuid().ToString("N")) {
                    Conventions = new DocumentConventions { MaxNumberOfRequestsPerSession = 100,
                        WaitForNonStaleResultsTimeout = TimeSpan.FromSeconds(30) },
                });
            }
            catch { Dispose(); throw; }
        }
        internal IDocumentSession OpenReadSession() {
            var session = Store.OpenSession();
            session.Advanced.OnBeforeQuery += (_, args) => args.QueryCustomization.WaitForNonStaleResults(TimeSpan.FromSeconds(30));
            return session;
        }
        internal IDocumentSession OpenTransactionSession() {
            var session = OpenReadSession();
            session.Advanced.OnAfterSaveChanges += (_, _) => {
                AcknowledgedSaves++; Assert.True(WizardCollection.HoldsWriteLane); AfterAcknowledgement?.Invoke();
            };
            return session;
        }
        internal Dictionary<string, JToken> RawDocuments(IEnumerable<string> ids) {
            using var session = OpenReadSession(); using var stream = new MemoryStream();
            session.Advanced.LoadIntoStream(ids, stream);
            var data = JObject.Parse(Encoding.UTF8.GetString(stream.ToArray()));
            return ((JArray)data["Results"]!).Children<JObject>().ToDictionary(
                row => row["@metadata"]!["@id"]!.Value<string>()!, row => row.DeepClone());
        }
        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            try {
                try { Store?.Dispose(); }
                finally { _server?.Dispose(); }
            }
            finally { _configuration.Restore(); }
            // Retain all authored fixture data/logs; never reread or rewrite the preceding config file.
        }
    }

    // Initialize parses four in-memory fields. Preserve their exact values and dictionary objects,
    // including each section's original comparer, without reading or writing the preceding INI file.
    private sealed class ConfigurationSnapshot {
        private static readonly FieldInfo SettingsField = Field("s_settings"), SectionsField = Field("s_sections");
        private static readonly FieldInfo PathField = Field("s_configFilePath"), InitializedField = Field("s_isInitialized");
        private readonly Dictionary<string, string> _settings = (Dictionary<string, string>)SettingsField.GetValue(null)!;
        private readonly Dictionary<string, Dictionary<string, string>> _sections
            = (Dictionary<string, Dictionary<string, string>>)SectionsField.GetValue(null)!;
        private readonly Dictionary<string, string> _savedSettings;
        private readonly Dictionary<string, (Dictionary<string, string> Original, Dictionary<string, string> Values)> _savedSections;
        private readonly string _path = (string)PathField.GetValue(null)!;
        private readonly bool _initialized = (bool)InitializedField.GetValue(null)!;
        internal ConfigurationSnapshot() {
            _savedSettings = new(_settings, _settings.Comparer);
            _savedSections = new(_sections.Comparer);
            foreach (var (name, section) in _sections)
                _savedSections.Add(name, (section, new(section, section.Comparer)));
        }
        internal void Restore() {
            _settings.Clear();
            foreach (var (key, value) in _savedSettings) _settings.Add(key, value);
            _sections.Clear();
            foreach (var (name, saved) in _savedSections) {
                saved.Original.Clear();
                foreach (var (key, value) in saved.Values) saved.Original.Add(key, value);
                _sections.Add(name, saved.Original);
            }
            PathField.SetValue(null, _path);
            InitializedField.SetValue(null, _initialized);
        }
        private static FieldInfo Field(string name)
            => typeof(ConfigurationManager).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Configuration snapshot field is unavailable: " + name);
    }
}
