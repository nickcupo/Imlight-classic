// CLASSIC: deed payment/ownership/selection have one acknowledgement and retain native aliases.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;
using Type = System.Type;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class HouseAcknowledgementTests {
    private static readonly FieldInfo AccountLane = typeof(AccountCollection).GetField("s_heldWriteLane", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("equip")] [InlineData("swap")] [InlineData("unequip")]
    public async Task ActualEquipmentCallerEmitsPrivateSelectionPacketsInTheirOriginalOrderAfterAck(string operation) {
        using var f = new HousingAckFixture(); Arrange(f, operation); using var actors = await EquipmentActorFixture.Create(f);
        f.OnSave = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.Empty(actors.Packets); };
        Assert.True(await actors.Select(operation)); var packets = await actors.Drain();
        var expected = operation == "swap" ? 2 : 1; Assert.Equal(expected, packets.Length);
        var changes = packets.Select(packet => Assert.IsType<GAME_5_PROTOCOL.MSG_EQUIPITEM>(packet)).ToArray();
        if (operation == "swap") { Assert.Equal(HousingAckFixture.FirstDeed, changes[0].ItemID); Assert.Equal((byte)0, changes[0].IsEquip); }
        var last = changes[^1]; Assert.Equal(operation == "swap" ? HousingAckFixture.SecondDeed : HousingAckFixture.FirstDeed, last.ItemID);
        Assert.Equal(operation == "unequip" ? (byte)0 : (byte)1, last.IsEquip); Assert.Equal("Islands", (string)last.SlotName);
        Assert.Equal(1, f.Saves); Assert.False(actors.Session.IsDisposed); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualEquipmentCallerClosesAnUncertainSelectionWithoutSendingNativeSuccess(bool durable) {
        using var f = new HousingAckFixture(); Arrange(f, "equip"); using var actors = await EquipmentActorFixture.Create(f);
        f.Failure = durable ? "lost" : "before";
        Assert.True(await actors.Select("equip")); await actors.Closed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(actors.Packets, packet => packet is GAME_5_PROTOCOL.MSG_EQUIPITEM);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(f.Live)); Assert.Equal(1, f.Saves);
        WizardCollection.UpdateCharacterItems(f.Live); Assert.Equal(1, f.Opened);
        Assert.Equal(durable, f.Saved.EquipmentBehavior.EquippedItemIds.Contains(HousingAckFixture.FirstDeed));
    }

    [Fact]
    public async Task ActualEquipmentCallerKeepsAKnownRefusalOpenWithoutNativeSelectionSuccess() {
        using var f = new HousingAckFixture(); Arrange(f, "equip");
        ((HouseRecord)f.Documents[HouseRecord.DocumentId(HousingAckFixture.Owner, HousingAckFixture.FirstDeed)]).OwnerId++;
        using var actors = await EquipmentActorFixture.Create(f); Assert.True(await actors.Select("equip"));
        var packets = await actors.Drain(); Assert.DoesNotContain(packets, packet => packet is GAME_5_PROTOCOL.MSG_EQUIPITEM);
        Assert.False(actors.Session.IsDisposed); Assert.Equal(0, f.Saves); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task QueuedNativeEquipOrUnequipClosesTheUncertainSnapshotBeforeStaleOwnershipCanAddAnInfraction(bool equip) {
        using var f = new HousingAckFixture(); WizardCollection.MarkInventorySnapshotUncertain(f.Live);
        using var actors = await EquipmentActorFixture.Create(f); Assert.True(await actors.NativeSelect(equip));
        await actors.Closed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Empty(f.Live.Account.InfractionIds); Assert.Empty(f.SavedAccount.InfractionIds);
        Assert.Equal(0, f.Opened); Assert.Equal(0, f.Saves); Assert.DoesNotContain(actors.Packets, packet => packet is GAME_5_PROTOCOL.MSG_EQUIPITEM);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PurchaseUsesFreshWalletAndAllSavedReferencesWhileItsCandidateAndLiveAliasesWaitForAck(bool crowns) {
        using var f = new HousingAckFixture(); var candidate = HousingAckFixture.Deed(HousingAckFixture.FirstDeed);
        var originalBehavior = candidate.m_inactiveBehaviors.OfType<DeedBehavior>().Single();
        var inventory = f.Live.InventoryBehavior; var stats = f.Live.GameStats; var account = f.Live.Account;
        var alias = inventory.Items.Single(item => item.m_globalID.Full == HousingAckFixture.OtherId);
        alias.m_debugName = "Stale label"; f.AddBackpackItem(HousingAckFixture.FreshId);
        stats.m_currentGold = 901; account.Crowns = 902; var beforeIds = inventory.InventoryItemIds.ToArray();
        var prepared = 0; var published = 0;
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal((int?)(HousingAckFixture.AccountId & 255), (int?)AccountLane.GetValue(null));
            Assert.Equal(1, prepared); Assert.Equal(0, published); Assert.Equal(beforeIds, inventory.InventoryItemIds);
            Assert.Equal(901, stats.m_currentGold); Assert.Equal(902, account.Crowns);
            Assert.Equal(0UL, candidate.m_characterId.Full); Assert.Equal(0UL, originalBehavior.m_lotInstanceGID.Full);
            var newItem = f.Working!.Working.Values.OfType<WizClientObjectItem>().Single(item => item.m_globalID.Full == HousingAckFixture.FirstDeed);
            Assert.DoesNotContain(newItem, f.Working.Ignored);
            Assert.All(f.Working.Working.Values.OfType<WizClientObjectItem>().Where(item => item.m_globalID.Full != HousingAckFixture.FirstDeed), item => Assert.Contains(item, f.Working.Ignored));
            Assert.DoesNotContain(Assert.Single(f.Working.Working.Values.OfType<HouseRecord>()), f.Working.Ignored);
            Assert.DoesNotContain(Assert.Single(f.Working.Working.Values.OfType<HousePortfolio>()), f.Working.Ignored);
            Assert.DoesNotContain(f.Working.Working["wizard/1"], f.Working.Ignored);
        };
        var result = HouseCollection.Purchase(f.Live, candidate, crowns ? HouseCurrency.Crowns : HouseCurrency.Gold, f.LoadAccount,
            receipt => {
                Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(0, f.Saves); prepared++;
                Assert.NotEmpty((byte[])receipt.ItemData); Assert.NotSame(candidate, receipt.Item);
                Assert.NotSame(originalBehavior, receipt.Item.m_inactiveBehaviors.OfType<DeedBehavior>().Single());
                Assert.Equal(crowns ? 5000 : 4800, receipt.Gold); Assert.Equal(10000, receipt.MaxGold); Assert.Equal(crowns ? 6700 : 7000, receipt.Crowns);
                Assert.Equal(beforeIds, inventory.InventoryItemIds); return true;
            }, receipt => {
                Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, f.Saves); published++;
                Assert.Same(inventory, f.Live.InventoryBehavior); Assert.Same(stats, f.Live.GameStats); Assert.Same(account, f.Live.Account);
                Assert.Equal(f.Saved.InventoryBehavior.InventoryItemIds, inventory.InventoryItemIds);
                Assert.Contains(HousingAckFixture.FreshId, inventory.InventoryItemIds);
                Assert.Same(alias, inventory.Items.Single(item => item.m_globalID.Full == HousingAckFixture.OtherId)); Assert.Equal("Authored original", alias.m_debugName);
                Assert.Contains(receipt.Record.ItemDocumentId, f.Documents.Keys);
            });
        Assert.True(result.Saved, result.Error); Assert.Equal(1, f.Saves); Assert.Equal(1, prepared); Assert.Equal(1, published);
        Assert.Equal(crowns ? 5000 : 4800, stats.m_currentGold); Assert.Equal(crowns ? 6700 : 7000, account.Crowns);
        Assert.Equal(HousingAckFixture.FirstDeed, Assert.Single(((HousePortfolio)f.Documents[HousePortfolio.DocumentId(HousingAckFixture.Owner)]).DeedIds));
        Assert.Equal(result.Record.LotInstanceId, f.Original(HousingAckFixture.FirstDeed).m_inactiveBehaviors.OfType<DeedBehavior>().Single().m_lotInstanceGID.Full);
        Assert.Equal(0UL, candidate.m_characterId.Full); Assert.Equal(0UL, originalBehavior.m_lotInstanceGID.Full);
        Assert.Equal(31, stats.m_currentHitpoints); Assert.Equal(7, stats.m_currentMana); Assert.Equal(1.25f, stats.m_potionCharge); Assert.Equal(7, f.Live.PetOwnerBehavior.Energy);
        Assert.False(HouseCollection.Purchase(f.Live, candidate, crowns ? HouseCurrency.Crowns : HouseCurrency.Gold, f.LoadAccount).Saved); Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData("equip")] [InlineData("swap")] [InlineData("unequip")]
    public void SelectionPublishesFreshMembershipButKeepsMovedAndUnrelatedWornNativeAliases(string operation) {
        using var f = new HousingAckFixture(); Arrange(f, operation);
        var inventory = f.Live.InventoryBehavior; var equipment = f.Live.EquipmentBehavior; var bank = f.Live.StorageBehavior;
        var targetId = operation == "swap" ? HousingAckFixture.SecondDeed : HousingAckFixture.FirstDeed;
        var target = inventory.Items.Concat(equipment.EquippedItems).Single(item => item.m_globalID.Full == targetId);
        var old = equipment.EquippedItems.SingleOrDefault(item => item.m_globalID.Full == HousingAckFixture.FirstDeed);
        const ulong unrelatedId = 789030;
        var unrelated = f.Original(HousingAckFixture.OtherId) with { m_globalID = unrelatedId, m_debugName = "Runtime worn original" };
        f.Documents[HousingAckFixture.ItemDocument(unrelatedId)] = HousingAckFixture.CloneItem(unrelated);
        f.Saved.EquipmentBehavior.EquippedItemIds.Add(unrelatedId); f.Live.EquipmentBehavior.EquippedItemIds.Add(unrelatedId); f.Live.EquipmentBehavior.EquippedItems.Add(unrelated);
        f.AddBackpackItem(HousingAckFixture.FreshId); var beforeBag = inventory.InventoryItemIds.ToArray(); var beforeWorn = equipment.EquippedItemIds.ToArray();
        var published = 0; f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(beforeBag, inventory.InventoryItemIds); Assert.Equal(beforeWorn, equipment.EquippedItemIds);
            Assert.Equal(0, published); Assert.Empty(f.Working!.Deleted);
            Assert.All(f.Working.Working.Values.OfType<WizClientObjectItem>(), item => Assert.Contains(item, f.Working.Ignored));
            Assert.All(f.Working.Working.Values.OfType<HouseRecord>(), record => Assert.Contains(record, f.Working.Ignored));
            Assert.Contains(Assert.Single(f.Working.Working.Values.OfType<HousePortfolio>()), f.Working.Ignored);
            Assert.DoesNotContain(f.Working.Working["wizard/1"], f.Working.Ignored);
        };
        var result = HouseCollection.SetEquipped(f.Live, targetId, operation != "unequip", receipt => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(0, f.Saves); Assert.NotEmpty((byte[])receipt.ItemData);
            if (operation == "swap") Assert.NotEmpty((byte[])receipt.ReplacedData); return true;
        }, _ => { Assert.True(WizardCollection.HoldsWriteLane); published++; Assert.Equal(f.Saved.InventoryBehavior.InventoryItemIds, inventory.InventoryItemIds); });
        Assert.True(result.Saved, result.Error); Assert.Equal(1, f.Saves); Assert.Equal(1, published);
        Assert.Same(inventory, f.Live.InventoryBehavior); Assert.Same(equipment, f.Live.EquipmentBehavior); Assert.Same(bank, f.Live.StorageBehavior);
        Assert.Contains(HousingAckFixture.FreshId, inventory.InventoryItemIds); Assert.Same(unrelated, equipment.EquippedItems.Single(item => item.m_globalID.Full == unrelatedId));
        var selected = equipment.SlotList.Where(slot => slot.SlotType == EquipmentSlotType.Islands).ToArray();
        if (operation == "unequip") { Assert.Empty(selected); Assert.Same(target, inventory.Items.Single(item => item.m_globalID.Full == targetId)); }
        else { Assert.Equal(targetId, Assert.Single(selected).ItemId.Full); Assert.Same(target, equipment.EquippedItems.Single(item => item.m_globalID.Full == targetId)); }
        if (operation == "swap") Assert.Same(old, inventory.Items.Single(item => item.m_globalID.Full == HousingAckFixture.FirstDeed));
        Assert.Equal(f.Saved.EquipmentBehavior.EquippedItemIds, equipment.EquippedItemIds); Assert.Equal(5000, f.Live.GameStats.m_currentGold);
        Assert.False(HouseCollection.SetEquipped(f.Live, targetId, operation != "unequip").Saved); Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData("gold", false)] [InlineData("gold", true)] [InlineData("crowns", false)] [InlineData("crowns", true)]
    [InlineData("equip", false)] [InlineData("equip", true)] [InlineData("unequip", false)] [InlineData("unequip", true)]
    public void PreparedNativePublicationCanRefuseBeforeAnyDeedWalletOrSelectionSave(string operation, bool throws) {
        using var f = new HousingAckFixture(); Arrange(f, operation); var beforeBag = f.Saved.InventoryBehavior.InventoryItemIds.ToArray();
        var beforeWorn = f.Saved.EquipmentBehavior.EquippedItemIds.ToArray(); var called = 0;
        bool Prepare() { Assert.True(WizardCollection.HoldsWriteLane); called++; if (throws) throw new InvalidOperationException("Authored deed native preparation refusal"); return false; }
        Assert.False(Run(f, operation, Prepare, () => Assert.Fail("Refused receipt cannot publish")));
        Assert.Equal(1, called); Assert.Equal(0, f.Saves); Assert.Equal(5000, f.Saved.GameStats.m_currentGold); Assert.Equal(7000, f.SavedAccount.Crowns);
        Assert.Equal(beforeBag, f.Saved.InventoryBehavior.InventoryItemIds); Assert.Equal(beforeBag, f.Live.InventoryBehavior.InventoryItemIds);
        Assert.Equal(beforeWorn, f.Saved.EquipmentBehavior.EquippedItemIds); Assert.Equal(beforeWorn, f.Live.EquipmentBehavior.EquippedItemIds);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("gold", "before")] [InlineData("gold", "lost")] [InlineData("gold", "publication")]
    [InlineData("crowns", "before")] [InlineData("crowns", "lost")] [InlineData("crowns", "publication")]
    [InlineData("equip", "before")] [InlineData("equip", "lost")] [InlineData("equip", "publication")]
    [InlineData("swap", "before")] [InlineData("swap", "lost")] [InlineData("swap", "publication")]
    [InlineData("unequip", "before")] [InlineData("unequip", "lost")] [InlineData("unequip", "publication")]
    public async Task FailedAcknowledgementOrReceiptPublicationQuarantinesBeforeTheQueuedStaleSaveCanAcquireItsLane(string operation, string failure) {
        using var f = new HousingAckFixture(); Arrange(f, operation); var live = f.Live;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var attempted = new ManualResetEventSlim();
        var bag = f.Saved.InventoryBehavior.InventoryItemIds.ToArray(); var worn = f.Saved.EquipmentBehavior.EquippedItemIds.ToArray();
        var disposed = false; var successes = 0; f.Failure = failure == "publication" ? null : failure;
        void Gate() { Assert.True(WizardCollection.HoldsWriteLane); entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
        if (failure != "publication") f.OnSave = Gate;
        f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); disposed = true; };
        var transaction = Task.Run(() => Run(f, operation, null, () => {
            if (failure == "publication") { Gate(); throw new InvalidOperationException("Authored deed receipt publication failure"); }
            successes++;
        }));
        Task? staleSave = null;
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            staleSave = Task.Run(() => { attempted.Set(); WizardCollection.UpdateCharacterItems(live); });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5))); Assert.False(staleSave.IsCompleted); Assert.Equal(1, f.Opened);
        } finally { release.Set(); }
        Assert.False(await transaction); await staleSave!; Assert.True(disposed); Assert.Equal(0, successes); Assert.Equal(1, f.Saves); Assert.Equal(1, f.Opened);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); Assert.False(Run(f, operation)); WizardCollection.UpdateCharacterItems(live); Assert.Equal(1, f.Opened);
        if (failure == "before") { Assert.Equal(bag, f.Saved.InventoryBehavior.InventoryItemIds); Assert.Equal(worn, f.Saved.EquipmentBehavior.EquippedItemIds); }
        if (failure != "publication") { Assert.Equal(bag, live.InventoryBehavior.InventoryItemIds); Assert.Equal(worn, live.EquipmentBehavior.EquippedItemIds); }
        if (operation is "gold" or "crowns") {
            Assert.Equal(failure == "before" || operation == "crowns" ? 5000 : 4800, f.Saved.GameStats.m_currentGold);
            Assert.Equal(failure == "before" || operation == "gold" ? 7000 : 6700, f.SavedAccount.Crowns);
            Assert.Equal(failure == "before" ? 0 : 1, f.Documents.Values.OfType<HouseRecord>().Count());
        }
        f.Failure = null; f.OnSave = null; f.OnDispose = null; f.Live = f.Reload();
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live)); Assert.Equal(f.Saved.InventoryBehavior.InventoryItemIds, f.Live.InventoryBehavior.InventoryItemIds);
        Assert.Equal(f.Saved.EquipmentBehavior.EquippedItemIds, f.Live.EquipmentBehavior.EquippedItemIds);
        if (failure != "before") { Assert.False(Run(f, operation)); Assert.Equal(1, f.Saves); }
    }

    [Theory]
    [InlineData("foreign-candidate")] [InlineData("fresh-level")] [InlineData("fresh-gold")] [InlineData("full-backpack")]
    [InlineData("portfolio-owner")] [InlineData("portfolio-duplicates")] [InlineData("three-owned")]
    [InlineData("foreign-account")] [InlineData("account-membership")] [InlineData("fresh-crowns")]
    public void PurchaseRefusesWrongIdentityFreshEligibilityCapacityOrAccountAuthorityWithoutAWrite(string defect) {
        using var f = new HousingAckFixture(); var candidate = HousingAckFixture.Deed(HousingAckFixture.FirstDeed); var crowns = false;
        if (defect == "foreign-candidate") candidate.m_characterId = HousingAckFixture.Owner + 1;
        if (defect == "fresh-level") f.Saved.MagicSchoolBehavior.Level = 1;
        if (defect == "fresh-gold") f.Saved.GameStats.m_currentGold = 1;
        if (defect == "full-backpack") for (var i = 0; i < 4; i++) f.AddBackpackItem((ulong)(789050 + i));
        if (defect == "portfolio-owner") f.Documents[HousePortfolio.DocumentId(HousingAckFixture.Owner)] = new HousePortfolio { OwnerId = HousingAckFixture.Owner + 1 };
        if (defect == "portfolio-duplicates") f.Documents[HousePortfolio.DocumentId(HousingAckFixture.Owner)] = new HousePortfolio { OwnerId = HousingAckFixture.Owner, DeedIds = [3, 3] };
        if (defect == "three-owned") { f.SeedDeed(789040); f.SeedDeed(789041); f.SeedDeed(789042); }
        if (defect == "foreign-account") { crowns = true; typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(f.SavedAccount, HousingAckFixture.AccountId + 1); }
        if (defect == "account-membership") { crowns = true; f.SavedAccount.CharacterIds.Clear(); }
        if (defect == "fresh-crowns") { crowns = true; f.SavedAccount.Crowns = 1; }
        Assert.False(HouseCollection.Purchase(f.Live, candidate, crowns ? HouseCurrency.Crowns : HouseCurrency.Gold, f.LoadAccount).Saved);
        Assert.Equal(0, f.Saves); Assert.DoesNotContain(HousingAckFixture.FirstDeed, f.Saved.InventoryBehavior.InventoryItemIds);
        Assert.Equal(5000, f.Live.GameStats.m_currentGold); Assert.Equal(7000, f.Live.Account.Crowns); Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("foreign-record")] [InlineData("foreign-original")] [InlineData("zero-record-lot")] [InlineData("missing-record")]
    [InlineData("ambiguous-slot")] [InlineData("fresh-full-backpack")]
    public void SelectionRefusesInvalidFreshDeedIdentityAndCapacityWithoutChangingTheSelectedAlias(string defect) {
        using var f = new HousingAckFixture(); var unequip = defect == "fresh-full-backpack"; Arrange(f, unequip ? "unequip" : "equip");
        var record = (HouseRecord)f.Documents[HouseRecord.DocumentId(HousingAckFixture.Owner, HousingAckFixture.FirstDeed)];
        if (defect == "foreign-record") record.OwnerId++;
        if (defect == "foreign-original") f.Original(HousingAckFixture.FirstDeed).m_characterId = HousingAckFixture.Owner + 1;
        if (defect == "zero-record-lot") record.LotInstanceId = 0;
        if (defect == "missing-record") f.Documents.Remove(HouseRecord.DocumentId(HousingAckFixture.Owner, HousingAckFixture.FirstDeed));
        if (defect == "ambiguous-slot") {
            f.SeedDeed(HousingAckFixture.SecondDeed, true);
            f.Saved.EquipmentBehavior.SlotList.Add(new() { ItemId = HousingAckFixture.SecondDeed, SlotType = EquipmentSlotType.Islands });
        }
        if (defect == "fresh-full-backpack") for (var i = 0; i < 4; i++) f.AddBackpackItem((ulong)(789050 + i));
        var beforeBag = f.Live.InventoryBehavior.InventoryItemIds.ToArray(); var beforeWorn = f.Live.EquipmentBehavior.EquippedItemIds.ToArray();
        Assert.False(HouseCollection.SetEquipped(f.Live, HousingAckFixture.FirstDeed, !unequip).Saved); Assert.Equal(0, f.Saves);
        Assert.Equal(beforeBag, f.Live.InventoryBehavior.InventoryItemIds); Assert.Equal(beforeWorn, f.Live.EquipmentBehavior.EquippedItemIds);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    internal static void Arrange(HousingAckFixture f, string operation) {
        if (operation == "equip") f.SeedDeed(HousingAckFixture.FirstDeed);
        if (operation == "swap") { f.SeedDeed(HousingAckFixture.FirstDeed, true); f.SeedDeed(HousingAckFixture.SecondDeed); }
        if (operation == "unequip") f.SeedDeed(HousingAckFixture.FirstDeed, true);
        f.Live = f.Reload();
    }
    private static bool Run(HousingAckFixture f, string operation, Func<bool>? prepare = null, System.Action? publish = null)
        => operation is "gold" or "crowns"
            ? HouseCollection.Purchase(f.Live, HousingAckFixture.Deed(HousingAckFixture.FirstDeed), operation == "crowns" ? HouseCurrency.Crowns : HouseCurrency.Gold, f.LoadAccount,
                prepare is null ? null! : _ => prepare(), publish is null ? null! : _ => publish()).Saved
            : HouseCollection.SetEquipped(f.Live, operation == "swap" ? HousingAckFixture.SecondDeed : HousingAckFixture.FirstDeed, operation != "unequip",
                prepare is null ? null! : _ => prepare(), publish is null ? null! : _ => publish()).Saved;

    private sealed class EquipmentActorFixture : IDisposable {
        internal readonly ConcurrentQueue<IMessage> Packets = new();
        internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ActorSystem System;
        internal SessionActor Session = null!;
        private IActorRef _session = null!, _socket = null!, _equipment = null!;
        private EquipmentActorFixture() => System = ActorSystem.Create("house-ack-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        internal static async Task<EquipmentActorFixture> Create(HousingAckFixture fixture) {
            var actors = new EquipmentActorFixture();
            try {
                actors._socket = actors.System.ActorOf(Props.Create(() => new SocketProbe(actors.Packets)), "socket");
                actors._session = actors.System.ActorOf(Props.CreateBy(new SessionProducer(actors._socket)), "session");
                actors.Session = await actors._session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
                var scopes = new CapturedScopes();
                actors._equipment = actors.System.ActorOf(Props.Create(() => new EquipmentProbe(actors.Session, fixture, scopes)), "equipment");
                Assert.True(await actors._equipment.Ask<bool>(new Ready(), Timeout, TestContext.Current.CancellationToken));
                var watcher = actors.System.ActorOf(Props.Create(() => new CloseWatcher(actors._session, actors.Closed)), "watcher");
                Assert.True(await watcher.Ask<bool>(new Ready(), Timeout, TestContext.Current.CancellationToken));
                return actors;
            } catch { actors.Dispose(); throw; }
        }
        internal Task<bool> Select(string operation) => _equipment.Ask<bool>(new SelectStep(operation), Timeout, TestContext.Current.CancellationToken);
        internal Task<bool> NativeSelect(bool equip) => _equipment.Ask<bool>(new NativeSelectStep(equip), Timeout, TestContext.Current.CancellationToken);
        internal async Task<IMessage[]> Drain() {
            await _session.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
            await _socket.Ask<bool>(new Ready(), Timeout, TestContext.Current.CancellationToken);
            var packets = new List<IMessage>(); while (Packets.TryDequeue(out var packet)) packets.Add(packet); return packets.ToArray();
        }
        public void Dispose() { System.Terminate().GetAwaiter().GetResult(); System.Dispose(); }
    }
    private sealed record Ready;
    private sealed record SelectStep(string Operation);
    private sealed record NativeSelectStep(bool Equip);
    private sealed class SocketProbe : ReceiveActor {
        public SocketProbe(ConcurrentQueue<IMessage> packets) { Receive<Ready>(_ => Sender.Tell(true)); Receive<IMessage>(packets.Enqueue); ReceiveAny(_ => { }); }
    }
    private sealed class CloseWatcher : ReceiveActor {
        public CloseWatcher(IActorRef session, TaskCompletionSource closed) { Context.Watch(session); Receive<Ready>(_ => Sender.Tell(true)); Receive<Terminated>(_ => closed.TrySetResult()); }
    }
    private sealed class SessionProducer(IActorRef socket) : IIndirectActorProducer {
        public Type ActorType => typeof(SessionActor);
        public ActorBase Produce() => new SessionActor(socket);
        public void Release(ActorBase actor) { }
    }
    private sealed class EquipmentProbe : EquipmentService {
        private readonly HousingAckFixture _fixture;
        private readonly CapturedScopes _scopes;
        public EquipmentProbe(SessionActor session, HousingAckFixture fixture, CapturedScopes scopes) : base(session) {
            _fixture = fixture; _scopes = scopes;
            typeof(MessageService).GetField("_cachedWizard", Private)!.SetValue(this, fixture.Live);
            typeof(MessageService).GetField("_cachedWizardGameObject", Private)!.SetValue(this, fixture.Live.GameObject);
        }
        protected override void ConfigureReceivers() {
            Receive<Ready>(_ => Sender.Tell(_fixture is not null && _scopes is not null));
            Receive<SelectStep>(step => {
                try {
                    using var scope = _scopes.Enter();
                    typeof(EquipmentService).GetMethod("SelectHouse", Private)!.Invoke(this, [_fixture.Live,
                        step.Operation == "swap" ? HousingAckFixture.SecondDeed : HousingAckFixture.FirstDeed, step.Operation != "unequip"]);
                    Sender.Tell(true);
                } catch (Exception exception) { Sender.Tell(new Status.Failure(exception)); }
            });
            Receive<NativeSelectStep>(step => {
                try {
                    using var scope = _scopes.Enter();
                    typeof(EquipmentService).GetMethod("ReceiveEquipItem", Private)!.Invoke(this,
                        [new GAME_5_PROTOCOL.MSG_EQUIPITEM { ItemID = 789999, SlotName = "Islands", IsEquip = step.Equip ? (byte)1 : (byte)0 }]);
                    Sender.Tell(true);
                } catch (Exception exception) { Sender.Tell(new Status.Failure(exception)); }
            });
            Receive<string>(text => text == "DoneDisposing", _ => { }); base.ConfigureReceivers();
        }
    }
    private sealed class CapturedScopes {
        private readonly WizardCollection.TestStore? _store = WizardCollection.TestStoreScope.Value;
        private readonly Func<IDocumentSession, List<WizClientObjectItem>>? _rows = WizardInventoryTransactions.TestRowsScope.Value;
        private readonly IReadOnlyDictionary<uint, HouseDefinition>? _definitions = HouseCatalog.TestDefinitions.Value;
        internal IDisposable Enter() {
            var oldStore = WizardCollection.TestStoreScope.Value; var oldRows = WizardInventoryTransactions.TestRowsScope.Value; var oldDefinitions = HouseCatalog.TestDefinitions.Value;
            WizardCollection.TestStoreScope.Value = _store; WizardInventoryTransactions.TestRowsScope.Value = _rows; HouseCatalog.TestDefinitions.Value = _definitions;
            return new Restore(() => { WizardCollection.TestStoreScope.Value = oldStore; WizardInventoryTransactions.TestRowsScope.Value = oldRows; HouseCatalog.TestDefinitions.Value = oldDefinitions; });
        }
    }
    private sealed class Restore(System.Action undo) : IDisposable { public void Dispose() => undo(); }
}
