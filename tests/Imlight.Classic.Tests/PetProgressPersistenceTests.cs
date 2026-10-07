// CLASSIC: a pet reward, its cost and its prepared native result share one acknowledged write.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pets;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class PetProgressPersistenceTests {
    public PetProgressPersistenceTests()
        => EquipmentAttachConcurrencyTests.Configure("[Character]\nMaxInventoryItems=150\nPetEnergyTickInSeconds=60\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(999, true)]
    public void FeedingUsesFreshStackAndPetAndPublishesOnlyAcknowledgedNativeResults(int count, bool equipped) {
        var f = new Fixture(count, equipped); using var scope = f.Scope(); var live = f.Live();
        var pet = f.LivePet(live); var behavior = PetProgress.Behavior(pet);
        var name = Assert.Single(pet.m_inactiveBehaviors.OfType<ClientPetNameBehavior>());
        var unrelated = Assert.Single(pet.m_inactiveBehaviors.OfType<ClientTimedItemBehavior>());
        var snack = Assert.Single(live.PetSnackBehavior.Snacks);
        snack.m_quantity = 45; behavior.m_XP = 1;
        f.BeforeSave = () => { Assert.Equal(45, snack.m_quantity); Assert.Equal(1u, behavior.m_XP); };
        Assert.True(ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out var receipt, new Random(1)));
        Assert.Equal(1, f.Saves); Assert.Same(pet, receipt.Pet); Assert.Same(behavior, PetProgress.Behavior(receipt.Pet));
        Assert.Same(name, Assert.Single(pet.m_inactiveBehaviors.OfType<ClientPetNameBehavior>()));
        Assert.Same(unrelated, Assert.Single(pet.m_inactiveBehaviors.OfType<ClientTimedItemBehavior>()));
        Assert.Equal(122u, name.m_nameKeys); Assert.Equal(987u, unrelated.m_expireTime);
        Assert.Equal(17u, behavior.m_XP); Assert.Equal(7, receipt.Growth.Xp); Assert.Equal(SnackTaste.Loved, receipt.Taste);
        Assert.Equal(4, PetProgress.Stats(behavior.m_currentStats)["Strength"]);
        Assert.Equal(3, PetProgress.Stats(behavior.m_currentStats)["Power"]);
        Assert.Equal(17u, PetProgress.Behavior(f.Items["original/pet"]).m_XP);
        Assert.Equal(30, f.Saved.PetOwnerBehavior.Energy); Assert.Equal(30, live.PetOwnerBehavior.Energy);
        if (count == 1) {
            Assert.Empty(f.Snacks); Assert.Empty(f.Saved.PetSnackBehavior.SnackItemIds); Assert.Empty(live.PetSnackBehavior.Snacks);
            Assert.Equal(Fixture.SnackId, Assert.IsType<PET_9_PROTOCOL.MSG_PETSNACKREMOVE>(receipt.Messages[0]).ItemID);
        }
        else {
            Assert.Equal(count - 1, f.Snacks["original/snack"].m_quantity); Assert.Same(snack, Assert.Single(live.PetSnackBehavior.Snacks));
            Assert.Equal(count - 1, Assert.IsType<PET_9_PROTOCOL.MSG_PETSNACKUPDATE>(receipt.Messages[0]).Quantity);
        }
        Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDSUCCESS>(receipt.Messages[1]);
        Assert.Equal(7u, Assert.IsType<WIZARD2_53_PROTOCOL.MSG_GAINPETXP>(receipt.Messages[2]).XP);
        var refresh = Assert.IsType<GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_EQUIPITEM>(receipt.Messages[3]);
        Assert.Equal("Pet", refresh.SlotName.ToString()); Assert.NotEqual(0, refresh.SerializedItem.Length);
        Assert.Equal(7u, f.End!.m_xpGain); Assert.Equal((int)SnackTaste.Loved, f.End.m_Score);
        Assert.Equal(2u, f.End.m_statMods.m_modifications.Single(m => m.m_name == "Power").m_actualChange);
    }

    [Fact]
    public void FeedingOneLastFreshCopyCannotResurrectTheStaleLiveStack() {
        var f = new Fixture(1); using var scope = f.Scope(); var live = f.Live();
        live.PetSnackBehavior.Snacks[0].m_quantity = 99;
        Assert.True(ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out _));
        Assert.False(ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out var again));
        Assert.Null(again); Assert.Equal(1, f.Saves); Assert.Empty(f.Snacks);
        Assert.Equal(17u, PetProgress.Behavior(f.Items["original/pet"]).m_XP);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(30, 28)]
    public void FinishCommitsFreshEnergyAndGrowthTogetherAndKeepsExactSavedTick(int energy, int expected) {
        var f = new Fixture(); f.Saved.PetOwnerBehavior.SetEnergy(energy); using var scope = f.Scope(); var live = f.Live();
        var owner = live.PetOwnerBehavior; owner.SetEnergy(100);
        var pet = f.LivePet(live); var b = PetProgress.Behavior(pet); b.m_XP = 0;
        f.BeforeSave = () => { Assert.Equal(100, owner.Energy); Assert.Equal(0u, b.m_XP); };
        Assert.True(f.Finish(live, out var receipt)); Assert.Equal(1, f.Saves); Assert.Same(owner, live.PetOwnerBehavior);
        Assert.Equal(expected, owner.Energy); Assert.Equal(expected, f.Saved.PetOwnerBehavior.Energy);
        Assert.Equal(f.Saved.PetOwnerBehavior.LastEnergyTickEpoch, owner.LastEnergyTickEpoch);
        Assert.Same(pet, receipt.Pet); Assert.Same(b, PetProgress.Behavior(pet)); Assert.Equal(18u, b.m_XP);
        Assert.Equal(2, receipt.Cost); Assert.Equal(8, receipt.Growth.Xp);
        var tick = Assert.IsType<PET_9_PROTOCOL.MSG_PETENERGYTICK>(receipt.Messages[0]);
        Assert.Equal(expected, tick.Energy); Assert.Equal((int)owner.LastEnergyTickEpoch, tick.TickTime); Assert.Equal(50, tick.MaxEnergy);
        Assert.Equal("PetGameDance", Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEEND>(receipt.Messages[1]).Game.ToString());
        Assert.Equal(1, f.End!.m_Score); Assert.Equal(1u, f.End.m_wins); Assert.Equal("Track", f.End.m_statMods.m_name.ToString());
        Assert.Equal(30, f.Snacks["original/snack"].m_quantity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrowthAcrossLevelBoundaryRetainsExistingCalculationsAndNativeOrder(bool finish) {
        var f = new Fixture(); PetProgress.Behavior(f.Items["original/pet"]).m_XP = 120;
        using var scope = f.Scope(); var live = f.Live();
        Assert.True(finish ? f.Finish(live, out var receipt) : ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out receipt));
        Assert.Equal(2, receipt.Growth.NewLevel); Assert.True(receipt.Growth.LeveledUp);
        Assert.IsType<WIZARD2_53_PROTOCOL.MSG_GAINPETXP>(receipt.Messages[2]);
        var up = Assert.IsType<PET_9_PROTOCOL.MSG_PETLEVELUP>(receipt.Messages[3]);
        Assert.Equal(2, up.PetLevel); Assert.Equal(Fixture.PetId, up.GlobalID); Assert.Equal(1, up.Display);
        Assert.IsType<GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_EQUIPITEM>(receipt.Messages[4]);
    }

    [Theory]
    [InlineData("foreign-snack")]
    [InlineData("zero-stack")]
    [InlineData("missing-snack")]
    [InlineData("orphan-snack")]
    [InlineData("duplicate-snack")]
    [InlineData("unknown-snack-ref")]
    [InlineData("foreign-pet")]
    [InlineData("missing-pet")]
    [InlineData("bank-pet")]
    [InlineData("orphan-pet")]
    [InlineData("duplicate-pet")]
    [InlineData("egg")]
    [InlineData("missing-alias")]
    public void RefusedFeedCannotConsumeOrGrowOrPrepareSuccess(string reason) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        switch (reason) {
            case "foreign-snack": f.Snacks["original/snack"].m_characterId = Fixture.Owner + 1; break;
            case "zero-stack": f.Snacks["original/snack"].m_quantity = 0; break;
            case "missing-snack": f.Snacks.Clear(); break;
            case "orphan-snack": f.Saved.PetSnackBehavior.SnackItemIds.Clear(); break;
            case "duplicate-snack": f.Snacks["collision/snack"] = Clone(f.Snacks["original/snack"]); break;
            case "unknown-snack-ref": f.Saved.PetSnackBehavior.SnackItemIds.Add(321); break;
            case "foreign-pet": f.Items["original/pet"].m_characterId = Fixture.Owner + 1; break;
            case "missing-pet": f.Items.Clear(); break;
            case "bank-pet": f.Saved.InventoryBehavior.InventoryItemIds.Clear(); f.Saved.StorageBehavior.BankItemIds.Add(Fixture.PetId); break;
            case "orphan-pet": f.Saved.InventoryBehavior.InventoryItemIds.Clear(); break;
            case "duplicate-pet": f.Items["collision/pet"] = Clone(f.Items["original/pet"]); break;
            case "egg": PetProgress.Behavior(f.Items["original/pet"]).m_level = 0; break;
            case "missing-alias": live.InventoryBehavior.Items = []; break;
        }
        Assert.False(ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out var receipt));
        Assert.Null(receipt); Assert.Equal(0, f.Saves); Assert.Equal(0, f.SerializedEnds);
        Assert.Equal(30, Assert.Single(live.PetSnackBehavior.Snacks).m_quantity);
        if (reason != "missing-alias") Assert.Equal(10u, PetProgress.Behavior(f.LivePet(live)).m_XP);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData(false, "empty-end")]
    [InlineData(false, "throw-end")]
    [InlineData(false, "empty-pet")]
    [InlineData(false, "throw-pet")]
    [InlineData(true, "empty-end")]
    [InlineData(true, "throw-end")]
    [InlineData(true, "empty-pet")]
    [InlineData(true, "throw-pet")]
    public void NativePreparationFailureRefusesBeforeSaveAndLeavesOriginalsIntact(bool finish, string fail) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        if (fail.EndsWith("end")) f.Dependencies.SerializeEnd = _ => fail.StartsWith("throw") ? throw new InvalidOperationException() : default;
        else f.Dependencies.SerializePet = _ => fail.StartsWith("throw") ? throw new InvalidOperationException() : default;
        Assert.False(finish ? f.Finish(live, out var receipt) : ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out receipt));
        Assert.Null(receipt); Assert.Equal(0, f.Saves); Assert.Equal(30, f.Snacks["original/snack"].m_quantity);
        Assert.Equal(10u, PetProgress.Behavior(f.Items["original/pet"]).m_XP);
        Assert.Equal(10u, PetProgress.Behavior(f.LivePet(live)).m_XP); Assert.Equal(30, live.PetOwnerBehavior.Energy);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedOrLostAcknowledgementCannotPublishCostOrGrowthAndOldInstanceCannotRepeat(bool finish, bool durable) {
        var f = new Fixture(1) { Fail = true, Durable = durable }; using var scope = f.Scope(); var live = f.Live();
        Assert.Throws<InvalidOperationException>(() => {
            if (finish) f.Finish(live, out _); else ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out _);
        });
        Assert.Equal(1, f.Saves); Assert.Equal(10u, PetProgress.Behavior(f.LivePet(live)).m_XP);
        Assert.Equal(1, Assert.Single(live.PetSnackBehavior.Snacks).m_quantity); Assert.Equal(30, live.PetOwnerBehavior.Energy);
        Assert.Equal(durable ? (finish ? 18u : 17u) : 10u, PetProgress.Behavior(f.Items["original/pet"]).m_XP);
        Assert.Equal(durable && finish ? 28 : 30, f.Saved.PetOwnerBehavior.Energy);
        Assert.Equal(durable && !finish ? 0 : 1, f.Snacks.Count);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.False(finish ? f.Finish(live, out var receipt) : ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out receipt));
        Assert.Null(receipt); Assert.Equal(1, f.Saves);
        Assert.False(ClassicPetProgressTransactions.TryInitialize(live, Fixture.PetId, out _, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedEnergySaveFromUnknownFinishInstanceCannotOverwriteItsSavedCost(bool durable) {
        var f = new Fixture { Fail = true, Durable = durable }; using var scope = f.Scope(); var live = f.Live();
        Assert.Throws<InvalidOperationException>(() => f.Finish(live, out _));
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); Assert.Equal(1, f.Saves);
        live.PetOwnerBehavior.SetEnergy(99);
        WizardCollection.UpdateCharacterPetOwnerBehavior(live);
        Assert.Equal(1, f.Saves); Assert.Equal(durable ? 28 : 30, f.Saved.PetOwnerBehavior.Energy);
    }

    [Fact]
    public void JoinInitializationUsesFreshPetPublishesChangedAliasesAndThenRequiresNoSecondSave() {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        var pet = f.LivePet(live); var b = PetProgress.Behavior(pet);
        var saved = PetProgress.Behavior(f.Items["original/pet"]); saved.m_currentStats = []; saved.m_maxStats = []; saved.m_requiredXP = 0;
        f.BeforeSave = () => Assert.Equal(1, Assert.Single(b.m_currentStats.Where(stat => stat.m_name == "Strength")).m_value);
        Assert.True(ClassicPetProgressTransactions.TryInitialize(live, Fixture.PetId, out var initialized, out var energy));
        Assert.Equal(1, f.Saves); Assert.Same(pet, initialized); Assert.Same(b, PetProgress.Behavior(pet)); Assert.Equal(30, energy);
        Assert.Equal(200, PetProgress.Stats(b.m_maxStats)["Strength"]); Assert.Equal(125u, b.m_requiredXP);
        Assert.True(ClassicPetProgressTransactions.TryInitialize(live, Fixture.PetId, out var unchanged, out _));
        Assert.Equal(1, f.Saves); Assert.Equal(125u, PetProgress.Behavior(unchanged).m_requiredXP);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JoinInitializationLostAcknowledgementCannotPublishOrBeRepeated(bool durable) {
        var f = new Fixture { Fail = true, Durable = durable }; using var scope = f.Scope(); var live = f.Live();
        PetProgress.Behavior(f.Items["original/pet"]).m_requiredXP = 0;
        Assert.Throws<InvalidOperationException>(() => ClassicPetProgressTransactions.TryInitialize(live, Fixture.PetId, out _, out _));
        Assert.Equal(125u, PetProgress.Behavior(f.LivePet(live)).m_requiredXP);
        Assert.Equal(durable ? 125u : 0u, PetProgress.Behavior(f.Items["original/pet"]).m_requiredXP);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.False(ClassicPetProgressTransactions.TryInitialize(live, Fixture.PetId, out _, out _)); Assert.Equal(1, f.Saves);
    }

    [Fact]
    public void FullBackpackDoesNotPreventConsumingAnOwnedSnackAndGrowingItsExistingPet() {
        var f = new Fixture();
        for (ulong id = 1; f.Saved.InventoryBehavior.InventoryItemIds.Count < ServerWizInventoryBehavior.MaxItemsAllowed; id++) {
            f.Items["filler/" + id] = new() { m_globalID = id, m_characterId = Fixture.Owner,
                m_templateID = Fixture.PetTemplate, m_inactiveBehaviors = [] };
            f.Saved.InventoryBehavior.InventoryItemIds.Add(id);
        }
        using var scope = f.Scope(); var live = f.Live();
        Assert.True(ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out _));
        Assert.Equal(1, f.Saves); Assert.Equal(ServerWizInventoryBehavior.MaxItemsAllowed, live.InventoryBehavior.Items.Count);
    }

    private sealed class Fixture {
        internal const ulong Owner = 792301, PetId = 792302, SnackId = 792303;
        internal const uint PetTemplate = 792304, SnackTemplate = 792305;
        internal Wizard Saved = new() { CharId = Owner, InventoryBehavior = new() { InventoryItemIds = [], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = [] }, StorageBehavior = new() { BankItemIds = [] },
            MagicSchoolBehavior = new() { Level = 50 }, PetOwnerBehavior = new(),
            PetSnackBehavior = new() { SnackItemIds = [SnackId], Snacks = [] } };
        internal Dictionary<string, WizClientObjectItem> Items = [];
        internal Dictionary<string, ClientPetSnackItem> Snacks = [];
        internal int Saves, SerializedEnds; internal bool Fail, Durable; internal System.Action? BeforeSave;
        internal PetGameEndData? End;
        internal readonly PetProgressDependencies Dependencies = new();
        internal Fixture(int count = 30, bool equipped = false) {
            Saved.PetOwnerBehavior.SetEnergy(30);
            if (equipped) Saved.EquipmentBehavior.EquippedItemIds.Add(PetId); else Saved.InventoryBehavior.InventoryItemIds.Add(PetId);
            Items["original/pet"] = new() { m_globalID = PetId, m_characterId = Owner, m_templateID = PetTemplate,
                m_inactiveBehaviors = [new ClientPetItemBehavior { m_level = 1, m_XP = 10, m_requiredXP = 125,
                    m_maxStats = Stats(200), m_currentStats = Stats(1), m_allTalents = [999019], m_expressedTalents = [] },
                    new ClientPetNameBehavior { m_nameKeys = 122 }, new ClientTimedItemBehavior { m_expireTime = 987 }] };
            Snacks["original/snack"] = new() { m_globalID = SnackId, m_characterId = Owner, m_templateID = SnackTemplate, m_quantity = count };
            Dependencies.SerializeEnd = data => { SerializedEnds++; End = data; return new(new byte[] { 1 }); };
            Dependencies.SerializePet = _ => new(new byte[] { 2 }); Dependencies.MaxEnergy = _ => 50;
        }
        internal bool Finish(Wizard live, out PetProgressReceipt receipt)
            => ClassicPetProgressTransactions.TryFinish(live, PetId, "PetGameDance", "Track",
                [new("Strength", 2), new("Agility", 2)], 4, 1, out receipt, new Random(1));
        internal WizClientObjectItem LivePet(Wizard live) => Assert.Single(live.InventoryBehavior.Items.Concat(live.EquipmentBehavior.EquippedItems));
        internal Wizard Live() {
            var live = CloneWizard(Saved);
            live.InventoryBehavior.Items = [..Items.Values.Where(item => live.InventoryBehavior.InventoryItemIds.Contains(item.m_globalID.Full)).Select(Clone)];
            live.EquipmentBehavior.EquippedItems = [..Items.Values.Where(item => live.EquipmentBehavior.EquippedItemIds.Contains(item.m_globalID.Full)).Select(Clone)];
            live.PetSnackBehavior.Snacks = Snacks.Values.Select(Clone).ToList();
            return live;
        }
        internal IDisposable Scope() {
            var oldStore = WizardCollection.TestStoreScope.Value; var oldItems = WizardInventoryTransactions.TestRowsScope.Value;
            var oldSnacks = WizardPetSnackTransactions.TestRowsScope.Value; var oldDependencies = ClassicPetProgressTransactions.TestScope.Value;
            var cache = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory).GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var oldPet = cache.TryGetValue(PetTemplate, out var pt) ? pt : null;
            var oldSnack = cache.TryGetValue(SnackTemplate, out var st) ? st : null;
            var talentCache = typeof(PetProgress).GetField("s_talentNames", BindingFlags.Static | BindingFlags.NonPublic)!;
            var oldTalentNames = talentCache.GetValue(null);
            cache[PetTemplate] = new WizItemTemplate { m_templateID = PetTemplate, m_school = "Storm", m_adjectiveList = ["Pet"],
                m_behaviors = [new PetItemBehaviorTemplate { m_behaviorName = "PetItemBehavior", m_Levels = [],
                    m_favoriteSnackCategories = ["Cereal"], m_maxStats = Stats(200), m_startStats = Stats(1), m_talents = [] }] };
            cache[SnackTemplate] = new PetSnackItemTemplate { m_templateID = SnackTemplate, m_school = "Storm", m_adjectiveList = ["Cereal"],
                m_statModifierSet = new() { m_modifications = [new() { m_name = "Strength", m_change = 3 }, new() { m_name = "Agility", m_change = 2 }] } };
            WizardCollection.TestStoreScope.Value = new(Open, (session, _) => Session(session).Wizard);
            WizardInventoryTransactions.TestRowsScope.Value = session => Session(session).Items.Values.ToList();
            WizardPetSnackTransactions.TestRowsScope.Value = session => Session(session).Snacks.Values.ToList();
            ClassicPetProgressTransactions.TestScope.Value = Dependencies;
            return new Restore(() => { WizardCollection.TestStoreScope.Value = oldStore; WizardInventoryTransactions.TestRowsScope.Value = oldItems;
                WizardPetSnackTransactions.TestRowsScope.Value = oldSnacks; ClassicPetProgressTransactions.TestScope.Value = oldDependencies;
                talentCache.SetValue(null, oldTalentNames);
                if (oldPet is null) cache.Remove(PetTemplate); else cache[PetTemplate] = oldPet;
                if (oldSnack is null) cache.Remove(SnackTemplate); else cache[SnackTemplate] = oldSnack; });
        }
        private IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, ProgressSession>(); var proxy = Session(session);
            proxy.Wizard = CloneWizard(Saved); proxy.Items = Items.ToDictionary(pair => pair.Key, pair => Clone(pair.Value));
            proxy.Snacks = Snacks.ToDictionary(pair => pair.Key, pair => Clone(pair.Value));
            proxy.Save = () => {
                Assert.True(WizardCollection.HoldsWriteLane); Saves++; BeforeSave?.Invoke();
                if (Fail && !Durable) throw new InvalidOperationException("fixture failed save");
                Saved = CloneWizard(proxy.Wizard); Items = proxy.Items.ToDictionary(pair => pair.Key, pair => Clone(pair.Value));
                Snacks = proxy.Snacks.ToDictionary(pair => pair.Key, pair => Clone(pair.Value));
                if (Fail) throw new InvalidOperationException("fixture lost acknowledgement");
            };
            return session;
        }
        private static ProgressSession Session(IDocumentSession session) => (ProgressSession)(object)session;
        private static List<PetStat> Stats(int value) => PetRules.StatNames.Select(name => new PetStat { m_name = name,
            m_statID = PetProgress.StatId(name), m_value = value }).ToList();
        private static Wizard CloneWizard(Wizard wizard) {
            var clone = new Wizard { CharId = wizard.CharId,
                InventoryBehavior = new() { InventoryItemIds = [..wizard.InventoryBehavior.InventoryItemIds], Items = [] },
                EquipmentBehavior = new() { EquippedItemIds = [..wizard.EquipmentBehavior.EquippedItemIds], EquippedItems = [] },
                StorageBehavior = new() { BankItemIds = [..wizard.StorageBehavior.BankItemIds] },
                MagicSchoolBehavior = new() { Level = wizard.MagicSchoolBehavior.Level }, PetOwnerBehavior = new(),
                PetSnackBehavior = new() { SnackItemIds = [..wizard.PetSnackBehavior.SnackItemIds], Snacks = [] } };
            clone.PetOwnerBehavior.PublishCommittedEnergy(wizard.PetOwnerBehavior); return clone;
        }
        private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
    }
    private static ClientPetSnackItem Clone(ClientPetSnackItem snack) => snack with { };
    private static WizClientObjectItem Clone(WizClientObjectItem item) => item with {
        m_inactiveBehaviors = item.m_inactiveBehaviors.Select(behavior => behavior switch {
            ClientPetItemBehavior pet => pet with { m_currentStats = pet.m_currentStats?.Select(stat => stat with { }).ToList(),
                m_maxStats = pet.m_maxStats?.Select(stat => stat with { }).ToList(),
                m_allTalents = pet.m_allTalents is null ? null : [..pet.m_allTalents],
                m_expressedTalents = pet.m_expressedTalents is null ? null : [..pet.m_expressedTalents] },
            ClientPetNameBehavior name => name with { }, ClientTimedItemBehavior timed => timed with { }, _ => behavior,
        }).ToList(),
    };
    public class ProgressSession : DispatchProxy {
        internal Wizard Wizard = null!; internal Dictionary<string, WizClientObjectItem> Items = [];
        internal Dictionary<string, ClientPetSnackItem> Snacks = []; internal System.Action Save = null!;
        private readonly IAdvancedSessionOperations _advanced = DispatchProxy.Create<IAdvancedSessionOperations, ItemInventoryPersistenceTests.ItemAdvanced>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "get_Advanced" => _advanced, "SaveChanges" => SaveNow(), "Delete" => Delete((ClientPetSnackItem)args![0]), "Dispose" => null,
            _ => throw new NotSupportedException(method.Name),
        };
        private object? SaveNow() { Save(); return null; }
        private object? Delete(ClientPetSnackItem snack) { Snacks.Remove(Snacks.Single(pair => ReferenceEquals(pair.Value, snack)).Key); return null; }
    }
}
