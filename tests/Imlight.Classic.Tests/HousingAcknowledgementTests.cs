// CLASSIC: authored housing transactions prove the acknowledgement and stale-save boundary.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Xunit;
using Action = System.Action;
using Type = System.Type;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class HousingAcknowledgementTests {
    [Fact]
    public void HouseRoomPlacementProtectsReadOnlyOwnershipRowsWhileSavingTheRoomAndBackpackTogether() {
        using var f = new HousingAckFixture(); f.SeedDeed(HousingAckFixture.FirstDeed);
        var identity = new HousingRoomIdentity(HousingAckFixture.Owner, HousingAckFixture.FirstDeed, HousingAckFixture.Exterior);
        var roomId = HousingLedger.DocumentId(identity);
        f.Documents[roomId] = new HousingLedger { OwnerId = HousingAckFixture.Owner, DeedId = HousingAckFixture.FirstDeed,
            Zone = HousingAckFixture.Exterior, Capacity = HousingAckFixture.Definition.ExteriorCapacity, PackageNumber = 21, SecondPackageNumber = 22, Version = 7 };
        f.Saved.Zone = HousingAckFixture.Exterior; f.Live = f.Reload(); var inventory = f.Live.InventoryBehavior;
        var deedAlias = inventory.Items.Single(item => item.m_globalID.Full == HousingAckFixture.FirstDeed);
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); var session = f.Working!;
            Assert.Contains(Assert.Single(session.Working.Values.OfType<HouseRecord>()), session.Ignored);
            Assert.Contains(Assert.Single(session.Working.Values.OfType<HousePortfolio>()), session.Ignored);
            Assert.All(session.Working.Values.OfType<WizClientObjectItem>(), original => Assert.Contains(original, session.Ignored));
            Assert.DoesNotContain(session.Working["wizard/1"], session.Ignored); Assert.DoesNotContain(session.Working[roomId], session.Ignored);
            Assert.Contains(HousingAckFixture.FurnitureId, inventory.InventoryItemIds); Assert.Empty(((HousingLedger)f.Documents[roomId]).Entries);
        };
        var result = HousingCollection.Place(f.Live, HousingAckFixture.Owner, HousingAckFixture.FurnitureId,
            HousingAckFixture.X, HousingAckFixture.Y, HousingAckFixture.Z, HousingAckFixture.Yaw, room: identity,
            afterCommit: _ => {
                Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, f.Saves);
                Assert.Equal(f.Saved.InventoryBehavior.InventoryItemIds, inventory.InventoryItemIds);
            });
        Assert.True(result.Saved, result.Error); Assert.Equal(1, f.Saves); Assert.Same(inventory, f.Live.InventoryBehavior);
        Assert.Same(deedAlias, inventory.Items.Single(item => item.m_globalID.Full == HousingAckFixture.FirstDeed));
        Assert.DoesNotContain(HousingAckFixture.FurnitureId, inventory.InventoryItemIds);
        var savedRoom = (HousingLedger)f.Documents[roomId]; Assert.Equal(8u, savedRoom.Version);
        Assert.Equal(HousingAckFixture.FurnitureId, Assert.Single(savedRoom.Entries).ItemId); Assert.Equal(HousingAckFixture.Z, savedRoom.Entries[0].Z);
        Assert.Equal(HousingAckFixture.FirstDeed, Assert.Single(((HousePortfolio)f.Documents[HousePortfolio.DocumentId(HousingAckFixture.Owner)]).DeedIds));
    }

    [Theory]
    [InlineData("place")] [InlineData("pickup")] [InlineData("update")]
    public void FreshReferencesAndNativeOriginalsPublishOnlyAfterTheSingleSaveInsideItsOriginalLane(string operation) {
        using var f = new HousingAckFixture(); f.ArrangeFurniture(operation);
        var inventory = f.Live.InventoryBehavior; var stats = f.Live.GameStats;
        var alias = inventory.Items.Single(item => item.m_globalID.Full == HousingAckFixture.OtherId);
        alias.m_debugName = "Stale native label"; f.Live.GameStats.m_currentGold = 901;
        f.AddBackpackItem(HousingAckFixture.FreshId); var beforeIds = inventory.InventoryItemIds.ToArray();
        var beforeVersion = f.Room.Version; var prepared = 0; var published = 0;
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, prepared); Assert.Equal(0, published);
            Assert.Equal(beforeIds, inventory.InventoryItemIds); Assert.Equal(901, stats.m_currentGold);
            Assert.Equal(beforeVersion, f.Room.Version); Assert.Empty(f.Working!.Deleted);
            if (operation != "update") {
                Assert.All(f.Working.Working.Values.OfType<WizClientObjectItem>(), item => Assert.Contains(item, f.Working.Ignored));
                Assert.DoesNotContain(f.Working.Working["wizard/1"], f.Working.Ignored);
            }
            else Assert.Contains(f.Working!.Working["wizard/1"], f.Working.Ignored);
        };
        var result = Run(f, operation, receipt => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(0, f.Saves); prepared++;
            Assert.Equal(beforeIds, inventory.InventoryItemIds); Assert.Equal(901, stats.m_currentGold);
            Assert.Equal(HousingAckFixture.FurnitureId, receipt.Entry.ItemId);
            if (operation == "pickup") Assert.NotEmpty((byte[])receipt.ItemData);
            return true;
        }, receipt => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(1, f.Saves); published++;
            Assert.Equal(f.Room.Version, receipt.Ledger.Version); Assert.Same(inventory, f.Live.InventoryBehavior);
            if (operation != "update") {
                Assert.Equal(f.Saved.InventoryBehavior.InventoryItemIds, inventory.InventoryItemIds);
                Assert.Same(alias, inventory.Items.Single(item => item.m_globalID.Full == HousingAckFixture.OtherId));
                Assert.Equal("Authored original", alias.m_debugName); Assert.Contains(HousingAckFixture.FreshId, inventory.InventoryItemIds);
            }
        });
        Assert.True(result.Saved, result.Error); Assert.Equal(1, prepared); Assert.Equal(1, published); Assert.Equal(1, f.Saves);
        Assert.Equal(beforeVersion + 1, f.Room.Version); Assert.Same(stats, f.Live.GameStats); Assert.Equal(901, stats.m_currentGold);
        Assert.Equal(5000, f.Saved.GameStats.m_currentGold); Assert.Contains(HousingAckFixture.ItemDocument(HousingAckFixture.FurnitureId), f.Documents.Keys);
        if (operation == "place") { Assert.False(f.Room.Entries[0].Removed); Assert.DoesNotContain(HousingAckFixture.FurnitureId, inventory.InventoryItemIds); }
        if (operation == "pickup") { Assert.True(f.Room.Entries[0].Removed); Assert.Equal(HousingAckFixture.FurnitureId, result.Item.m_globalID.Full); }
        if (operation == "update") { Assert.Equal(beforeIds, inventory.InventoryItemIds); Assert.Equal(HousingAckFixture.Z, f.Room.Entries[0].Z); }
    }

    [Theory]
    [InlineData("place", false)] [InlineData("place", true)]
    [InlineData("pickup", false)] [InlineData("pickup", true)]
    [InlineData("update", false)] [InlineData("update", true)]
    public void PublicationPreparationRefusesWithoutSavingPublishingOrQuarantining(string operation, bool throws) {
        using var f = new HousingAckFixture(); f.ArrangeFurniture(operation);
        var beforeIds = f.Live.InventoryBehavior.InventoryItemIds.ToArray(); var version = f.Room.Version; var calls = 0;
        var result = Run(f, operation, receipt => {
            Assert.True(WizardCollection.HoldsWriteLane); calls++;
            if (operation == "pickup") Assert.NotEmpty((byte[])receipt.ItemData);
            if (throws) throw new InvalidOperationException("Authored packet preparation refusal");
            return false;
        }, _ => Assert.Fail("A refused preparation cannot publish"));
        Assert.False(result.Saved); Assert.Equal(1, calls); Assert.Equal(0, f.Saves); Assert.Equal(version, f.Room.Version);
        Assert.Equal(beforeIds, f.Live.InventoryBehavior.InventoryItemIds); Assert.Equal(beforeIds, f.Saved.InventoryBehavior.InventoryItemIds);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("place", "before")] [InlineData("place", "lost")] [InlineData("place", "publication")]
    [InlineData("pickup", "before")] [InlineData("pickup", "lost")] [InlineData("pickup", "publication")]
    [InlineData("update", "before")] [InlineData("update", "lost")] [InlineData("update", "publication")]
    public async Task UnknownOutcomeQuarantinesBeforeDisposalAndBlocksTheQueuedStaleInventorySave(string operation, string failure) {
        using var f = new HousingAckFixture(); f.ArrangeFurniture(operation); var live = f.Live;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var attempted = new ManualResetEventSlim();
        var beforeIds = f.Saved.InventoryBehavior.InventoryItemIds.ToArray(); var beforeVersion = f.Room.Version; var disposed = false; var successes = 0;
        f.Failure = failure == "publication" ? null : failure;
        void Gate() { Assert.True(WizardCollection.HoldsWriteLane); entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
        if (failure != "publication") f.OnSave = Gate;
        f.OnDispose = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); disposed = true;
        };
        var transaction = Task.Run(() => Run(f, operation, null, _ => {
            if (failure == "publication") { Gate(); throw new InvalidOperationException("Authored post-ACK packet failure"); }
            successes++;
        }));
        Task? staleSave = null;
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            staleSave = Task.Run(() => { attempted.Set(); WizardCollection.UpdateCharacterItems(live); });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5))); Assert.False(staleSave.IsCompleted); Assert.Equal(1, f.Opened);
        }
        finally { release.Set(); }
        Assert.False((await transaction).Saved); await staleSave!; Assert.True(disposed); Assert.Equal(0, successes);
        Assert.Equal(1, f.Saves); Assert.Equal(1, f.Opened); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.False(Run(f, operation).Saved); WizardCollection.UpdateCharacterItems(live); Assert.Equal(1, f.Opened);
        Assert.Equal(failure == "before" ? beforeVersion : beforeVersion + 1, f.Room.Version);
        if (failure != "publication") Assert.Equal(beforeIds, live.InventoryBehavior.InventoryItemIds);
        f.Failure = null; f.OnSave = null; f.OnDispose = null; f.Live = f.Reload();
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live)); Assert.Equal(f.Saved.InventoryBehavior.InventoryItemIds, f.Live.InventoryBehavior.InventoryItemIds);
        if (failure != "before" && operation != "update") { Assert.False(Run(f, operation).Saved); Assert.Equal(1, f.Saves); }
        if (failure != "before" && operation == "update") Assert.Equal(HousingAckFixture.Z, f.Room.Entries[0].Z);
    }

    [Theory]
    [InlineData("visitor")] [InlineData("duel")] [InlineData("missing-reference")] [InlineData("foreign-original")]
    [InlineData("duplicate-reference")] [InlineData("missing-other-original")] [InlineData("lookalike-original")]
    [InlineData("foreign-room")]
    public void PlacementMustProveTheFreshWholeBackpackAndOriginalIdentity(string defect) {
        using var f = new HousingAckFixture(); var owner = HousingAckFixture.Owner;
        if (defect == "visitor") owner++;
        if (defect == "duel") f.Live.IsInDuel = true;
        if (defect == "missing-reference") f.Saved.InventoryBehavior.InventoryItemIds.Remove(HousingAckFixture.FurnitureId);
        if (defect == "foreign-original") f.Original(HousingAckFixture.FurnitureId).m_characterId = owner + 1;
        if (defect == "duplicate-reference") f.Saved.InventoryBehavior.InventoryItemIds.Add(HousingAckFixture.FurnitureId);
        if (defect == "missing-other-original") f.Documents.Remove(HousingAckFixture.ItemDocument(HousingAckFixture.OtherId));
        if (defect == "foreign-room") f.Room.OwnerId++;
        var result = HousingCollection.Place(f.Live, owner, HousingAckFixture.FurnitureId, 1, 2, 3, .5f,
            (session, id, _) => defect == "lookalike-original" ? f.Original(id) with { } : session.Load<WizClientObjectItem>(HousingAckFixture.ItemDocument(id)));
        Assert.False(result.Saved); Assert.Equal(0, f.Saves); Assert.Empty(f.Room.Entries);
        Assert.Contains(HousingAckFixture.FurnitureId, f.Live.InventoryBehavior.InventoryItemIds);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("foreign-original")] [InlineData("wrong-template")] [InlineData("wrong-dynamic")]
    [InlineData("full-backpack")] [InlineData("existing-reference")] [InlineData("bank-reference")] [InlineData("removed")]
    public void PickupRefusesInvalidOwnershipCacheIdentityOrFreshCapacityWithoutChangingTheRoom(string defect) {
        using var f = new HousingAckFixture(); f.ArrangeFurniture("pickup"); var dynamic = HousingAckFixture.Dynamic;
        if (defect == "foreign-original") f.Original(HousingAckFixture.FurnitureId).m_characterId = HousingAckFixture.Owner + 1;
        if (defect == "wrong-template") f.Room.Entries[0].TemplateId++;
        if (defect == "wrong-dynamic") dynamic++;
        if (defect == "full-backpack") for (var i = 0; i < 5; i++) f.AddBackpackItem((ulong)(789050 + i));
        if (defect == "existing-reference") f.Saved.InventoryBehavior.InventoryItemIds.Add(HousingAckFixture.FurnitureId);
        if (defect == "bank-reference") f.Saved.StorageBehavior.BankItemIds.Add(HousingAckFixture.FurnitureId);
        if (defect == "removed") f.Room.Entries[0].Removed = true;
        var version = f.Room.Version;
        Assert.False(HousingCollection.Pickup(f.Live, HousingAckFixture.Owner, HousingRules.PlacedGlobalId(0, dynamic), dynamic == HousingAckFixture.Dynamic ? dynamic : HousingAckFixture.Dynamic).Saved);
        Assert.Equal(0, f.Saves); Assert.Equal(version, f.Room.Version); Assert.Single(f.Live.InventoryBehavior.InventoryItemIds);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    [Theory]
    [InlineData("nonfinite")] [InlineData("wrong-dynamic")] [InlineData("removed")] [InlineData("version-exhausted")]
    public void MoveRefusesInvalidCoordinatesIdentityOrVersionBeforeAnySave(string defect) {
        using var f = new HousingAckFixture(); f.ArrangeFurniture("update"); var before = f.Room.Copy();
        if (defect == "removed") f.Room.Entries[0].Removed = true;
        if (defect == "version-exhausted") f.Room.Version = uint.MaxValue;
        var version = f.Room.Version;
        var result = HousingCollection.Update(f.Live, HousingAckFixture.Owner, HousingRules.PlacedGlobalId(0, HousingAckFixture.Dynamic),
            defect == "wrong-dynamic" ? HousingAckFixture.Dynamic + 1 : HousingAckFixture.Dynamic,
            defect == "nonfinite" ? float.NaN : 1, 2, 3, .5f);
        Assert.False(result.Saved); Assert.Equal(0, f.Saves); Assert.Equal(version, f.Room.Version); Assert.Equal(before.Entries[0].Z, f.Room.Entries[0].Z);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(f.Live));
    }

    internal static HousingResult Run(HousingAckFixture f, string operation, Func<HousingResult, bool>? prepare = null, System.Action<HousingResult>? publish = null)
        => operation switch {
            "place" => HousingCollection.Place(f.Live, HousingAckFixture.Owner, HousingAckFixture.FurnitureId, HousingAckFixture.X, HousingAckFixture.Y, HousingAckFixture.Z, HousingAckFixture.Yaw,
                preparePublication: prepare!, afterCommit: publish!),
            "pickup" => HousingCollection.Pickup(f.Live, HousingAckFixture.Owner, HousingRules.PlacedGlobalId(0, HousingAckFixture.Dynamic), HousingAckFixture.Dynamic,
                preparePublication: prepare!, afterCommit: publish!),
            "update" => HousingCollection.Update(f.Live, HousingAckFixture.Owner, HousingRules.PlacedGlobalId(0, HousingAckFixture.Dynamic), HousingAckFixture.Dynamic,
                HousingAckFixture.X, HousingAckFixture.Y, HousingAckFixture.Z, HousingAckFixture.Yaw, preparePublication: prepare!, afterCommit: publish!),
            _ => throw new ArgumentException(operation),
        };
}

// Tracked native rows and all document writes remain in the same authored session, including failed acknowledgements.
public sealed class HousingAckFixture : IDisposable {
    internal const ulong Owner = 789000, AccountId = 789001, FurnitureId = 789010, OtherId = 789011, FreshId = 789012, FirstDeed = 789020, SecondDeed = 789021;
    internal const uint FurnitureTemplate = 789100, DeedTemplate = 789101, Dynamic = 0x3567;
    internal const float X = 11.234375f, Y = -12.625f, Z = 178.765625f, Yaw = .73123455f;
    internal const string Exterior = "Housing/Authored/Exterior", Interior = "Housing/Authored/Interior";
    internal readonly Dictionary<string, object> Documents = new();
    internal Wizard Live;
    internal Wizard Saved => (Wizard)Documents["wizard/1"];
    internal Account SavedAccount => (Account)Documents["account/1"];
    internal HousingLedger Room => (HousingLedger)Documents[HousingLedger.DocumentId(Owner)];
    internal AckSession? Working;
    internal int Opened, Saves;
    internal string? Failure;
    internal Action? OnSave, OnDispose;
    private readonly WizardCollection.TestStore? _store;
    private readonly Func<IDocumentSession, List<WizClientObjectItem>>? _rows;
    private readonly IReadOnlyDictionary<uint, HouseDefinition>? _definitions;
    private readonly IDictionary<ulong, CoreTemplate> _cache;
    private readonly Dictionary<ulong, CoreTemplate?> _oldTemplates = new();
    internal static HouseDefinition Definition => new(DeedTemplate, 789102, "Authored ACK house", Exterior, Interior, Exterior + "_Preview", 250, 250, 200, 300, 15, true);

    internal HousingAckFixture() {
        EquipmentAttachConcurrencyTests.Configure("[Character]\nMaxInventoryItems=6\n[Classic]\nBackpackSize=6\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");
        // All hook captures follow configuration so this fixture also runs by itself.
        _store = WizardCollection.TestStoreScope.Value; _rows = WizardInventoryTransactions.TestRowsScope.Value;
        _definitions = HouseCatalog.TestDefinitions.Value;
        _cache = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory).GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (var id in new[] { FurnitureTemplate, DeedTemplate }) { _cache.TryGetValue(id, out var old); _oldTemplates[id] = old; }
        _cache[FurnitureTemplate] = new WizItemTemplate { m_templateID = FurnitureTemplate, m_adjectiveList = ["Housing", "Furniture"], m_behaviors = [new RenderBehaviorTemplate(), new FurnitureInfoBehaviorTemplate()] };
        _cache[DeedTemplate] = new WizItemTemplate { m_templateID = DeedTemplate, m_adjectiveList = ["Islands", "Deed"], m_behaviors = [new DeedBehaviorTemplate { m_behaviorName = "Deed" }] };
        HouseCatalog.TestDefinitions.Value = new Dictionary<uint, HouseDefinition> { [DeedTemplate] = Definition };
        Documents["wizard/1"] = new Wizard { CharId = Owner, AccountId = AccountId, Zone = HousingRules.DormZone,
            GameStats = new(MagicSchool.Fire, 30) { m_currentGold = 5000, m_baseGoldPouch = 10000, m_currentHitpoints = 31, m_baseHitpoints = 100, m_currentMana = 7, m_baseMana = 70, m_potionCharge = 1.25f, m_potionMax = 3 },
            MagicSchoolBehavior = new() { MagicSchool = MagicSchool.Fire, Level = 30, ExperiencePoints = 2500, TrainingPoints = 5 },
            InventoryBehavior = new() { InventoryItemIds = [], Items = [] }, EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = [], SlotList = [] },
            StorageBehavior = new() { BankItemIds = [], Items = [] }, AlchemyBehavior = new() { ReagentItemIds = [], Reagents = [] },
            SpellbookBehavior = new(), PetOwnerBehavior = new() { Eggs = [], PetHatchTimes = [] } };
        Saved.PetOwnerBehavior.SetEnergy(7);
        var account = new Account { Crowns = 7000 }; typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(account, AccountId); account.CharacterIds.Add(Owner);
        Documents["account/1"] = account;
        Documents[HousingLedger.DocumentId(Owner)] = new HousingLedger { OwnerId = Owner, Zone = HousingRules.DormZone, Capacity = HousingRules.DormCapacity, PackageNumber = 11, Version = 7 };
        AddBackpackItem(FurnitureId); AddBackpackItem(OtherId); Live = Reload(); Install();
    }
    internal void Install() {
        WizardCollection.TestStoreScope.Value = new(Open, (session, owner) => owner == Owner ? session.Load<Wizard>("wizard/1") : null!);
        WizardInventoryTransactions.TestRowsScope.Value = session => Documents.Where(pair => pair.Value is WizClientObjectItem)
            .Select(pair => session.Load<WizClientObjectItem>(pair.Key)).ToList();
    }
    internal static string ItemDocument(ulong id) => $"AuthoredHousingItems/{id}";
    internal WizClientObjectItem Original(ulong id) => Documents.Values.OfType<WizClientObjectItem>().Single(item => item.m_globalID.Full == id);
    internal void AddBackpackItem(ulong id) {
        Documents[ItemDocument(id)] = new WizClientObjectItem { m_globalID = id, m_templateID = FurnitureTemplate, m_characterId = Owner, m_debugName = "Authored original", m_inactiveBehaviors = [] };
        Saved.InventoryBehavior.InventoryItemIds.Add(id);
    }
    internal void ArrangeFurniture(string operation) {
        if (operation != "place") {
            Assert.True(Room.TryPlace(new HousingEntry { ItemId = FurnitureId, ItemDocumentId = ItemDocument(FurnitureId), TemplateId = FurnitureTemplate, X = 1, Y = 2, Z = 3, Yaw = .5f }, out _));
            Saved.InventoryBehavior.InventoryItemIds.Remove(FurnitureId);
        }
        Live = Reload();
    }
    internal static WizClientObjectItem Deed(ulong id) => new() { m_globalID = id, m_templateID = DeedTemplate, m_debugName = "Authored deed", m_inactiveBehaviors = [new DeedBehavior()] };
    internal void SeedDeed(ulong id, bool equipped = false) {
        var item = Deed(id); item.m_characterId = Owner; var lot = id + 1000;
        item.m_inactiveBehaviors.OfType<DeedBehavior>().Single().m_lotInstanceGID = lot;
        var document = $"ClassicHouseItems/{Owner}/{id}"; Documents[document] = item;
        Documents[HouseRecord.DocumentId(Owner, id)] = new HouseRecord { OwnerId = Owner, DeedId = id, TemplateId = DeedTemplate, ItemDocumentId = document, LotInstanceId = lot, ExteriorZone = Exterior, InteriorZone = Interior, PreviewZone = Exterior + "_Preview" };
        if (!Documents.TryGetValue(HousePortfolio.DocumentId(Owner), out var value)) Documents[HousePortfolio.DocumentId(Owner)] = value = new HousePortfolio { OwnerId = Owner };
        ((HousePortfolio)value).DeedIds.Add(id);
        if (equipped) {
            Saved.EquipmentBehavior.EquippedItemIds.Add(id); Saved.EquipmentBehavior.SlotList.Add(new() { ItemId = id, SlotType = EquipmentSlotType.Islands, ItemName = item.m_debugName, EquippedSince = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc) });
        }
        else Saved.InventoryBehavior.InventoryItemIds.Add(id);
    }
    internal Account LoadAccount(IDocumentSession session, ulong id) { Assert.Equal(AccountId, id); return session.Load<Account>("account/1"); }
    internal Wizard Reload() {
        var wizard = CloneWizard(Saved); wizard.Account = CloneAccount(SavedAccount);
        wizard.InventoryBehavior.Items = [..wizard.InventoryBehavior.InventoryItemIds.Select(id => CloneItem(Original(id)))];
        wizard.EquipmentBehavior.EquippedItems = [..wizard.EquipmentBehavior.EquippedItemIds.Select(id => CloneItem(Original(id)))];
        wizard.HasInitializedRuntimeStats = true; return wizard;
    }
    internal IDocumentSession Open() {
        Opened++; var session = DispatchProxy.Create<IDocumentSession, AckSession>(); Working = (AckSession)(object)session;
        var proxy = Working; proxy.Documents = Documents;
        proxy.Save = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Saves++; OnSave?.Invoke();
            if (Failure == "before") throw new InvalidOperationException("Authored failure before durable write");
            foreach (var pair in proxy.Working) if (!proxy.Ignored.Contains(pair.Value)) Documents[pair.Key] = Clone(pair.Value);
            if (Failure == "lost") throw new InvalidOperationException("Authored durable write lost ACK");
        };
        proxy.DisposeSession = () => OnDispose?.Invoke(); return session;
    }
    internal static Wizard CloneWizard(Wizard wizard) {
        var clone = new Wizard { CharId = wizard.CharId, AccountId = wizard.AccountId, Zone = wizard.Zone,
            GameStats = wizard.GameStats.CloneSnapshotWithGold(wizard.GameStats.m_currentGold),
            MagicSchoolBehavior = new() { MagicSchool = wizard.MagicSchoolBehavior.MagicSchool, Level = wizard.MagicSchoolBehavior.Level, ExperiencePoints = wizard.MagicSchoolBehavior.ExperiencePoints, TrainingPoints = wizard.MagicSchoolBehavior.TrainingPoints },
            InventoryBehavior = new() { InventoryItemIds = [..wizard.InventoryBehavior.InventoryItemIds], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [..wizard.EquipmentBehavior.EquippedItemIds], EquippedItems = [], SlotList = wizard.EquipmentBehavior.SlotList.Select(slot => new EquipmentSlot { ItemId = slot.ItemId, SlotType = slot.SlotType, ItemName = slot.ItemName, EquippedSince = slot.EquippedSince }).ToList() },
            StorageBehavior = new() { BankItemIds = [..wizard.StorageBehavior.BankItemIds], Items = [] },
            AlchemyBehavior = new() { ReagentItemIds = [..wizard.AlchemyBehavior.ReagentItemIds], Reagents = [] }, SpellbookBehavior = new(), PetOwnerBehavior = new() { Eggs = [], PetHatchTimes = [] } };
        clone.PetOwnerBehavior.PublishCommittedEnergy(wizard.PetOwnerBehavior); return clone;
    }
    internal static Account CloneAccount(Account account) {
        var clone = new Account { Crowns = account.Crowns }; typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(clone, account.AccountId);
        clone.CharacterIds.AddRange(account.CharacterIds); return clone;
    }
    internal static WizClientObjectItem CloneItem(WizClientObjectItem item) => item with { m_inactiveBehaviors = item.m_inactiveBehaviors?.Select(behavior => behavior is DeedBehavior deed ? (BehaviorInstance)(deed with { }) : behavior).ToList()! };
    internal static object Clone(object value) => value switch {
        Wizard wizard => CloneWizard(wizard), Account account => CloneAccount(account), WizClientObjectItem item => CloneItem(item),
        HousingLedger room => room.Copy(), HouseRecord house => house.Copy(), HousePortfolio portfolio => portfolio.Copy(),
        HouseLocation location => location.Copy(), HousingPackageAllocator allocator => new HousingPackageAllocator { NextPackageNumber = allocator.NextPackageNumber },
        _ => throw new NotSupportedException(value.GetType().Name),
    };
    public void Dispose() {
        WizardCollection.TestStoreScope.Value = _store; WizardInventoryTransactions.TestRowsScope.Value = _rows; HouseCatalog.TestDefinitions.Value = _definitions;
        foreach (var pair in _oldTemplates) { if (pair.Value is null) _cache.Remove(pair.Key); else _cache[pair.Key] = pair.Value; }
    }
    public class AckSession : DispatchProxy {
        internal Dictionary<string, object> Documents = null!;
        internal readonly Dictionary<string, object> Working = new();
        internal readonly HashSet<object> Ignored = new(ReferenceEqualityComparer.Instance), Deleted = new(ReferenceEqualityComparer.Instance);
        internal Action Save = null!, DisposeSession = null!;
        private IAdvancedSessionOperations? _advanced;
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            switch (method!.Name) {
                case "get_Advanced":
                    if (_advanced is null) { _advanced = DispatchProxy.Create<IAdvancedSessionOperations, AckAdvanced>(); ((AckAdvanced)(object)_advanced).Owner = this; }
                    return _advanced;
                case "Load":
                    var id = (string)args![0]!; if (Working.TryGetValue(id, out var tracked)) return tracked;
                    return Documents.TryGetValue(id, out var saved) ? Working[id] = Clone(saved) : null;
                case "Query": return typeof(AckSession).GetMethod(nameof(Query), BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(method.GetGenericArguments()[0]).Invoke(this, null);
                case "Store":
                    var storedId = (string)args![1]!;
                    if (Working.TryGetValue(storedId, out var original)) Assert.Same(original, args[0]);
                    else Assert.False(Documents.ContainsKey(storedId), "An untracked Store cannot replace an existing document");
                    Working[storedId] = args[0]!; return null;
                case "Delete": Deleted.Add(args![0]!); throw new InvalidOperationException("Housing must retain its native original documents");
                case "SaveChanges": Save(); return null;
                case "Dispose": DisposeSession(); return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
        private IRavenQueryable<T> Query<T>() {
            var rows = Documents.Where(pair => pair.Value is T).Select(pair => {
                if (!Working.TryGetValue(pair.Key, out var row)) Working[pair.Key] = row = Clone(pair.Value);
                return (T)row;
            }).ToList();
            var query = DispatchProxy.Create<IRavenQueryable<T>, AckQuery<T>>(); var proxy = (AckQuery<T>)(object)query;
            proxy.Rows = rows.AsQueryable(); proxy.Self = query; return query;
        }
    }
    public class AckQuery<T> : DispatchProxy {
        internal IQueryable<T> Rows = null!; internal IRavenQueryable<T> Self = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "Customize" => Self, "get_Provider" => Rows.Provider, "get_Expression" => Rows.Expression, "get_ElementType" => typeof(T), "GetEnumerator" => Rows.GetEnumerator(),
            _ => throw new NotSupportedException(method.Name),
        };
    }
    public class AckAdvanced : DispatchProxy {
        internal AckSession Owner = null!;
        private readonly IMetadataDictionary _metadata = DispatchProxy.Create<IMetadataDictionary, AckMetadata>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "set_OptimisticConcurrencyMode" => null,
            "GetDocumentId" => Owner.Working.Single(pair => ReferenceEquals(pair.Value, args![0])).Key,
            "GetMetadataFor" => _metadata,
            "IgnoreChangesFor" => Ignore(args![0]!),
            _ => throw new NotSupportedException(method.Name),
        };
        private object? Ignore(object row) {
            var added = Owner.Ignored.Add(row);
            // Raven permits repeated protection of the same portfolio during target/replacement ownership reads.
            if (row is WizClientObjectItem) Assert.True(added, "Each read-only native original is protected once");
            return null;
        }
    }
    public class AckMetadata : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) {
            Assert.Equal("set_Item", method!.Name); Assert.Equal(Raven.Client.Constants.Documents.Metadata.Collection, args![0]);
            Assert.Equal(WizardItemCollection.CollectionName, args[1]); return null;
        }
    }
}
