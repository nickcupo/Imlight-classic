// CLASSIC: real pet equipment/growth lanes, authored native fixtures, no private assets or player database.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Pets;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;
using Action = System.Action;
using Type = System.Type;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class PetTalentEffectTests {
    [Fact]
    public void ActualEquipUsesFreshOwnedPetAndPublishesFractionOnlyAfterAcknowledgement() {
        using var f = new Fixture(); var live = f.Live(); var alias = live.InventoryBehavior.GetItem(Fixture.PetId);
        PetProgress.Behavior(alias).m_currentStats = Fixture.Stats(240);
        f.BeforeSave = () => { Assert.Equal(0, live.GameStats.m_dmgBonusPercent?[2] ?? 0); Assert.Empty(live.GameEffects.Snapshot());
            Assert.Same(alias, live.InventoryBehavior.GetItem(Fixture.PetId)); Assert.Empty(live.EquipmentBehavior.EquippedItems); };
        Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out var effects, out var removed));
        Assert.Equal(1, f.Saves); Assert.Empty(removed); Assert.Same(alias, live.EquipmentBehavior.GetItem(Fixture.PetId));
        Assert.Equal(100, PetProgress.Stats(PetProgress.Behavior(alias).m_currentStats)["Strength"]);
        Assert.Equal(.025f, live.GameStats.m_dmgBonusPercent[2], 7); Assert.Equal(0, live.GameStats.m_dmgBonusPercent[0]);
        var effect = Assert.IsType<WizStatisticEffect>(Assert.Single(effects));
        Assert.Equal(Fixture.PetId, effect.m_originatorID.Full); Assert.Equal(-1, effect.m_lookupIndex);
        Assert.Equal(.025f, effect.m_damageBonusPercent, 7);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrLostAcknowledgementOfAtomicSwapKeepsOldPetStatsCardsAndReferences(bool durable) {
        using var f = new Fixture(Fixture.Health, Fixture.Pixie); f.AddPet(Fixture.OtherPetId, Fixture.Damage);
        var live = f.Live(); Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var old = live.EquipmentBehavior.GetItem(Fixture.PetId); var newPet = live.InventoryBehavior.GetItem(Fixture.OtherPetId);
        var card = Assert.Single(live.SpellbookBehavior.TemporarySpells); var oldEffects = live.GameEffects.Snapshot();
        var hp = live.GameStats.m_baseHitpoints; f.Fail = true; f.Durable = durable;
        f.BeforeSave = () => { Assert.Same(old, live.EquipmentBehavior.GetItem(Fixture.PetId)); Assert.Same(card, Assert.Single(live.SpellbookBehavior.TemporarySpells));
            Assert.Equal(hp, live.GameStats.m_baseHitpoints); Assert.Same(newPet, live.InventoryBehavior.GetItem(Fixture.OtherPetId)); };
        Assert.Throws<InvalidOperationException>(() => live.InventoryToEquipmentTransfer(Fixture.OtherPetId, out _, out _));
        Assert.Same(old, live.EquipmentBehavior.GetItem(Fixture.PetId)); Assert.Same(card, Assert.Single(live.SpellbookBehavior.TemporarySpells));
        Assert.Equal(hp, live.GameStats.m_baseHitpoints); Assert.Equal(oldEffects, live.GameEffects.Snapshot());
        Assert.Equal(durable ? Fixture.OtherPetId : Fixture.PetId, f.Saved.EquipmentBehavior.GetEquippedPetId());
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.False(live.InventoryToEquipmentTransfer(Fixture.OtherPetId, out _, out _)); Assert.Equal(2, f.Saves);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("collision")]
    [InlineData("bank-alias")]
    [InlineData("equipment-alias")]
    [InlineData("stale-location")]
    [InlineData("duplicate-slot")]
    public void RefusedPetSwapCannotRetireOldReceiptOrPrepareAWrite(string reason) {
        using var f = new Fixture(Fixture.Health, Fixture.Pixie); f.AddPet(Fixture.OtherPetId, Fixture.Damage);
        var live = f.Live(); Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var old = live.EquipmentBehavior.GetItem(Fixture.PetId); var card = Assert.Single(live.SpellbookBehavior.TemporarySpells);
        switch (reason) {
            case "foreign": f.Items[Fixture.OtherPetId].m_characterId = Fixture.Owner + 1; break;
            case "collision": f.Collision = f.Items[Fixture.OtherPetId] with { }; break;
            case "bank-alias": live.StorageBehavior.Items = [live.InventoryBehavior.GetItem(Fixture.OtherPetId)]; break;
            case "equipment-alias": live.EquipmentBehavior.EquippedItems.Add(live.InventoryBehavior.GetItem(Fixture.OtherPetId)); break;
            case "stale-location": f.Saved.InventoryBehavior.InventoryItemIds.Remove(Fixture.OtherPetId); break;
            case "duplicate-slot": f.Saved.EquipmentBehavior.SlotList.Add(new() { SlotType = EquipmentSlotType.Pet, ItemId = Fixture.PetId }); break;
        }
        Assert.False(live.InventoryToEquipmentTransfer(Fixture.OtherPetId, out var added, out var removed));
        Assert.Null(added); Assert.Null(removed); Assert.Equal(1, f.Saves); Assert.Same(old, live.EquipmentBehavior.GetItem(Fixture.PetId));
        Assert.Same(card, Assert.Single(live.SpellbookBehavior.TemporarySpells)); Assert.Equal(130, live.GameStats.m_baseHitpoints);
    }

    [Fact]
    public void ActualUnequipSubtractsAdmittedValuesAfterGrowthAndNativeDefinitionChange() {
        using var f = new Fixture(Fixture.Health, Fixture.Pixie); var live = f.Live();
        Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var alias = live.EquipmentBehavior.GetItem(Fixture.PetId); var unrelated = SpellFactory.GetSpell("Pet - Pixie");
        live.AddTemporarySpell(unrelated); PetProgress.Behavior(alias).m_currentStats = Fixture.Stats(200);
        f.Native["PetTalentMaxHealth01"].m_hitPointBonus = .24f;
        f.BeforeSave = () => { Assert.Equal(130, live.GameStats.m_baseHitpoints); Assert.Equal(2, live.SpellbookBehavior.TemporarySpells.Count); };
        Assert.True(live.EquipmentToInventoryTransfer(Fixture.PetId, out var removed));
        Assert.Equal(2, removed.Count); Assert.Equal(100, live.GameStats.m_baseHitpoints);
        Assert.Same(unrelated, Assert.Single(live.SpellbookBehavior.TemporarySpells)); Assert.Same(alias, live.InventoryBehavior.GetItem(Fixture.PetId));
        Assert.Equal(71, live.GameStats.m_currentHitpoints); Assert.Equal(75, live.GameStats.m_currentMana);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostUnequipAcknowledgementKeepsExactAdmittedReceipt(bool durable) {
        using var f = new Fixture(Fixture.Health, Fixture.Pixie); var live = f.Live();
        Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var card = Assert.Single(live.SpellbookBehavior.TemporarySpells); f.Fail = true; f.Durable = durable;
        Assert.Throws<InvalidOperationException>(() => live.EquipmentToInventoryTransfer(Fixture.PetId, out _));
        Assert.Equal(130, live.GameStats.m_baseHitpoints); Assert.Same(card, Assert.Single(live.SpellbookBehavior.TemporarySpells));
        Assert.Equal(Fixture.PetId, live.EquipmentBehavior.GetEquippedPetId()); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.Equal(durable ? 0UL : Fixture.PetId, f.Saved.EquipmentBehavior.GetEquippedPetId());
    }

    [Fact]
    public void RebuildAndReloadRetireOnlyOwnedCardsAndReapplyPassiveOnce() {
        using var f = new Fixture(Fixture.Health, Fixture.Pixie); var live = f.Live();
        Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var unrelated = SpellFactory.GetSpell("Pet - Pixie"); live.AddTemporarySpell(unrelated);
        for (var iteration = 0; iteration < 3; iteration++) {
            var old = live.SpellbookBehavior.TemporarySpells.First(card => !ReferenceEquals(card, unrelated));
            CharacterHelper.RecalculateGameStats(live);
            Assert.Equal(130, live.GameStats.m_baseHitpoints); Assert.Equal(2, live.GameEffects.Count);
            Assert.Equal(2, live.SpellbookBehavior.TemporarySpells.Count); Assert.Contains(unrelated, live.SpellbookBehavior.TemporarySpells);
            Assert.DoesNotContain(live.SpellbookBehavior.TemporarySpells, card => ReferenceEquals(card, old));
        }
        var reload = f.Live(); CharacterHelper.RecalculateGameStats(reload); CharacterHelper.RecalculateGameStats(reload);
        Assert.Equal(130, reload.GameStats.m_baseHitpoints); Assert.Single(reload.SpellbookBehavior.TemporarySpells);
    }

    [Fact]
    public void CapacityIsDerivedIdempotentlyAndPermitsTrainingPastInheritedBaseWithoutChangingIt() {
        using var f = new Fixture(Fixture.Capacity); var pet = f.Items[Fixture.PetId];
        PetProgress.Behavior(pet).m_currentStats = Fixture.Stats(100);
        for (var iteration = 0; iteration < 3; iteration++) {
            Assert.Equal(125, PetTalentRuntime.EffectiveMaximums(pet)["Strength"]);
            Assert.Equal(100, PetProgress.Stats(PetProgress.Behavior(pet).m_maxStats)["Strength"]);
        }
        Assert.Equal(25, Assert.Single(PetProgress.ApplyStats(pet, [new("Strength", 40)])).Change);
        Assert.Equal(125, PetProgress.Stats(PetProgress.Behavior(pet).m_currentStats)["Strength"]);
        Assert.Equal(100, PetProgress.Stats(PetProgress.Behavior(pet).m_maxStats)["Strength"]);
    }

    [Fact]
    public void AcknowledgedCapacityTrainingSurvivesInitializationAndAuthoritativeReload() {
        using var f = new Fixture(Fixture.Capacity);
        PetProgress.Behavior(f.Items[Fixture.PetId]).m_currentStats.First(stat => stat.m_name.ToString() == "Strength").m_value = 122;
        var live = f.Live(); Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        Assert.True(ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out var receipt));
        Assert.Equal(125, PetProgress.Stats(PetProgress.Behavior(receipt.Pet).m_currentStats)["Strength"]);
        Assert.False(PetProgress.EnsureInitialized(receipt.Pet));
        // Force the real missing-next-level initialization write on the authoritative row, then reload.
        PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP = 0;
        Assert.True(ClassicPetProgressTransactions.TryInitialize(live, Fixture.PetId, out var initialized, out _));
        Assert.Equal(125, PetProgress.Stats(PetProgress.Behavior(initialized).m_currentStats)["Strength"]);
        var reload = f.Live(); CharacterHelper.RecalculateGameStats(reload);
        var reloadedPet = reload.EquipmentBehavior.GetItem(Fixture.PetId);
        Assert.False(PetProgress.EnsureInitialized(reloadedPet));
        Assert.Equal(125, PetProgress.Stats(PetProgress.Behavior(reloadedPet).m_currentStats)["Strength"]);
        Assert.Equal(125, PetTalentRuntime.EffectiveMaximums(reloadedPet)["Strength"]);
        Assert.Equal(100, PetProgress.Stats(PetProgress.Behavior(reloadedPet).m_maxStats)["Strength"]);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void InitializationWritePublishesPassivesOnlyForAcknowledgedExactEquippedPet(bool equipped, bool fail, bool durable) {
        using var f = new Fixture(Fixture.Health, Fixture.Pixie);
        PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP = 0;
        if (equipped) {
            f.Saved.InventoryBehavior.InventoryItemIds.Clear();
            f.Saved.EquipmentBehavior.EquippedItemIds = [Fixture.PetId];
            f.Saved.EquipmentBehavior.SlotList = [new() { SlotType = EquipmentSlotType.Pet, ItemId = Fixture.PetId }];
        }
        var live = f.Live(); f.Fail = fail; f.Durable = durable;
        f.BeforeSave = () => { Assert.Equal(100, live.GameStats.m_baseHitpoints);
            Assert.Empty(live.SpellbookBehavior.TemporarySpells); Assert.Empty(live.GameEffects.Snapshot()); };
        if (fail) {
            Assert.Throws<InvalidOperationException>(() => ClassicPetProgressTransactions.TryInitialize(live, Fixture.PetId, out _, out _));
            Assert.Equal(100, live.GameStats.m_baseHitpoints); Assert.Empty(live.SpellbookBehavior.TemporarySpells);
            Assert.Empty(live.GameEffects.Snapshot()); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        }
        else {
            Assert.True(ClassicPetProgressTransactions.TryInitialize(live, Fixture.PetId, out _, out _));
            Assert.Equal(equipped ? 130 : 100, live.GameStats.m_baseHitpoints);
            Assert.Equal(equipped ? 1 : 0, live.SpellbookBehavior.TemporarySpells.Count);
            Assert.Equal(equipped ? 2 : 0, live.GameEffects.Count);
        }
        Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData("saved-slot")]
    [InlineData("duplicate-slot")]
    [InlineData("live-bank-alias")]
    public void StaleGrowthLocationRefusesBeforeSnackWriteOrReceiptRetirement(string conflict) {
        using var f = new Fixture(); var live = f.Live(); Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var old = Assert.Single(live.GameEffects.Snapshot());
        switch (conflict) {
            case "saved-slot": f.Saved.EquipmentBehavior.SlotList.Clear(); break;
            case "duplicate-slot": f.Saved.EquipmentBehavior.SlotList.Add(new() { SlotType = EquipmentSlotType.Pet, ItemId = Fixture.PetId }); break;
            case "live-bank-alias": live.StorageBehavior.Items = [live.EquipmentBehavior.GetItem(Fixture.PetId)]; break;
        }
        Assert.False(ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out _));
        Assert.Equal(1, f.Saves); Assert.Same(old, Assert.Single(live.GameEffects.Snapshot()));
        Assert.Equal(.025f, live.GameStats.m_dmgBonusPercent[2], 7); Assert.Equal(30, Assert.Single(live.PetSnackBehavior.Snacks).m_quantity);
    }

    [Fact]
    public void FreshAcknowledgedSnackGrowthUpdatesExistingPassiveAndPreparedNativeEffectMessages() {
        using var f = new Fixture(); var live = f.Live(); Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var prior = Assert.Single(live.GameEffects.Snapshot()); var old = .025f;
        f.BeforeSave = () => Assert.Equal(old, live.GameStats.m_dmgBonusPercent[2], 7);
        Assert.True(ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out var receipt));
        Assert.Equal(.0254f, live.GameStats.m_dmgBonusPercent[2], 7); Assert.Equal(2, f.Saves);
        Assert.DoesNotContain(prior, live.GameEffects.Snapshot());
        Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(receipt.Messages[^2]);
        Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(receipt.Messages[^1]);
        Assert.Equal(250, PetProgress.Stats(PetProgress.Behavior(receipt.Pet).m_maxStats)["Strength"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedGrowthAcknowledgementCannotPublishChangedPassiveOrItsPreparedCard(bool durable) {
        using var f = new Fixture(); var live = f.Live(); Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var old = Assert.Single(live.GameEffects.Snapshot()); f.Fail = true; f.Durable = durable;
        Assert.Throws<InvalidOperationException>(() => ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out _));
        Assert.Equal(.025f, live.GameStats.m_dmgBonusPercent[2], 7); Assert.Same(old, Assert.Single(live.GameEffects.Snapshot()));
        Assert.Equal(30, Assert.Single(live.PetSnackBehavior.Snacks).m_quantity); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.False(ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out _)); Assert.Equal(2, f.Saves);
    }

    [Fact]
    public void FailedNativePassivePreparationRefusesSnackBeforeAnySave() {
        using var f = new Fixture(); var live = f.Live(); Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        PetTalentRuntime.TestScope.Value.Serialize = _ => default;
        Assert.False(ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out var receipt));
        Assert.Null(receipt); Assert.Equal(1, f.Saves); Assert.Equal(.025f, live.GameStats.m_dmgBonusPercent[2], 7);
        Assert.Equal(30, Assert.Single(live.PetSnackBehavior.Snacks).m_quantity); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LevelUpCardIsPreparedFromGrownPetAndPublishedOnlyOnConfirmedWrite(bool fail, bool durable) {
        using var f = new Fixture(); var source = PetProgress.Behavior(f.Items[Fixture.PetId]);
        source.m_level = 1; source.m_XP = 124; source.m_requiredXP = 125;
        source.m_allTalents = [PetProgress.TalentId(Fixture.Pixie)]; source.m_expressedTalents = [];
        var live = f.Live(); Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        Assert.Empty(live.SpellbookBehavior.TemporarySpells); f.Fail = fail; f.Durable = durable;
        f.BeforeSave = () => { Assert.Empty(live.SpellbookBehavior.TemporarySpells);
            Assert.Empty(PetProgress.Behavior(live.EquipmentBehavior.GetItem(Fixture.PetId)).m_expressedTalents); };
        if (fail) {
            Assert.Throws<InvalidOperationException>(() => ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out _));
            Assert.Empty(live.SpellbookBehavior.TemporarySpells); Assert.Empty(live.GameEffects.Snapshot());
            Assert.Empty(PetProgress.Behavior(live.EquipmentBehavior.GetItem(Fixture.PetId)).m_expressedTalents);
            Assert.Equal(durable ? 1 : 0, PetProgress.Behavior(f.Items[Fixture.PetId]).m_expressedTalents.Count);
        }
        else {
            Assert.True(ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out var receipt, new Random(1)));
            Assert.Equal(PetProgress.TalentId(Fixture.Pixie), Assert.Single(receipt.Growth.NewTalents));
            Assert.Single(live.SpellbookBehavior.TemporarySpells); Assert.Single(live.GameEffects.Snapshot());
            Assert.Contains(receipt.Messages, message => message is PET_9_PROTOCOL.MSG_PETLEVELUP);
            Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(receipt.Messages[^1]);
        }
    }

    [Fact]
    public void OlderProfileAndDisabledTalentFeatureKeepPassiveAndCapacityBehaviorUnchanged() {
        using var f = new Fixture(Fixture.Capacity, Fixture.Damage); var pet = f.Items[Fixture.PetId]; var live = f.Live();
        ClassicRuntime.ResetForTests(); ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        Assert.False(PetTalentRuntime.Enabled); Assert.Null(PetTalentRuntime.Prepare(live, pet));
        Assert.Equal(100, PetTalentRuntime.EffectiveMaximums(pet)["Strength"]);
        ClassicRuntime.ResetForTests(); ClassicRuntime.Initialize(ClassicDataFixture.RealRules("october-2010-arc1"));
        Assert.False(PetTalentRuntime.Enabled); Assert.Null(PetTalentRuntime.Prepare(live, pet));
    }

    [Fact]
    public void CardProviderAgreementCannotAdmitContestedSpellOrWrongCapacityBinding() {
        using var f = new Fixture();
        Assert.False(PetTalentPolicy.IsApprovedGrantedSpell("Pet - Stormblade"));
        Assert.True(PetTalentPolicy.IsApprovedGrantedSpell("Pet - Pixie"));
        SpellFactory.GetTemplate("Pet - Pixie").m_effects[0].m_effectParam = 480;
        Assert.False(PetTalentPolicy.IsApprovedGrantedSpell("Pet - Pixie"));
        var caps = f.Talents[PetProgress.TalentId(Fixture.Capacity)]; caps.m_maxStatList[0].m_value = 65;
        Assert.False(PetTalentPolicy.IsCapacityTalent(caps));
    }

    private sealed class Fixture : IDisposable {
        internal const ulong Owner = 811000, PetId = 811001, OtherPetId = 811002, SnackId = 811003;
        private const uint TemplateId = 811100, SnackTemplate = 811101, SpellId = 811102;
        internal const string Damage = "Talent-Damage-Storm02", Health = "Talent-Health01", Pixie = "Talent-Spell-Life02", Capacity = "Talent-Stat01-Str01";
        internal Wizard Saved;
        internal Dictionary<ulong, WizClientObjectItem> Items = [];
        internal WizClientObjectItem Collision;
        internal ClientPetSnackItem Snack = new() { m_globalID = SnackId, m_characterId = Owner, m_templateID = SnackTemplate, m_quantity = 30 };
        internal readonly Dictionary<uint, PetTalentTemplate> Talents = [];
        internal readonly Dictionary<string, PetBoostPlayerStatEffectTemplate> Native = [];
        internal int Saves; internal bool Fail, Durable; internal Action BeforeSave;
        private readonly PetTalentDependencies _oldDependencies;
        private readonly PetProgressDependencies _oldProgress;
        private readonly WizardCollection.TestStore _oldStore;
        private readonly Func<IDocumentSession, List<WizClientObjectItem>> _oldRows;
        private readonly Func<IDocumentSession, List<ClientPetSnackItem>> _oldSnackRows;
        private readonly Dictionary<int, MagicSchoolTemplate> _schools, _priorSchools;
        private readonly IDictionary<ulong, CoreTemplate> _cache;
        private readonly Dictionary<ulong, CoreTemplate> _priorCache;
        private readonly TemplateManifest _manifest;
        private readonly object _levels;
        private readonly object _talentNames;
        private readonly Dictionary<uint, SpellTemplate> _spells, _priorSpells;
        private readonly Dictionary<uint, string> _spellPaths, _priorSpellPaths;
        internal Fixture(params string[] expressed) {
            EquipmentAttachConcurrencyTests.Configure("[Character]\nMaxInventoryItems=150\nPetEnergyTickInSeconds=60\nBaseGoldPouch=1000\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");
            ClassicRuntime.ResetForTests(); var rules = ClassicDataFixture.RealRules("october-2010-arc1");
            rules.OwnerExtraFeatures = () => new HashSet<string> { ClassicFeatures.PetsTalents };
            ClassicRuntime.Initialize(rules);
            _oldDependencies = PetTalentRuntime.TestScope.Value; _oldProgress = ClassicPetProgressTransactions.TestScope.Value;
            _oldStore = WizardCollection.TestStoreScope.Value; _oldRows = WizardInventoryTransactions.TestRowsScope.Value;
            _oldSnackRows = WizardPetSnackTransactions.TestRowsScope.Value;
            _schools = Field<Dictionary<int, MagicSchoolTemplate>>(typeof(MagicSchools), "s_magicSchools"); _priorSchools = new(_schools);
            _schools.Clear(); var schools = new[] { "Fire", "Ice", "Storm", "Life", "Myth", "Death", "Balance" };
            for (var index = 0; index < schools.Length; index++) _schools[index] = new() { m_schoolName = schools[index], m_schoolIndex = index };
            _cache = Field<IDictionary<ulong, CoreTemplate>>(typeof(CoreObjectFactory), "s_templateCache"); _priorCache = new(_cache);
            _levels = Field<object>(typeof(MagicLevelsConfig), "s_playerLevelConfig");
            SetField(typeof(MagicLevelsConfig), "s_playerLevelConfig", new Dictionary<string, List<MagicLevelInfo>> {
                [new ServerWizGameStats(default, 1).MagicSchool.ToString()] = [new() { m_xpToLevel = 0 },
                    new() { m_xpToLevel = 100, m_hitpoints = 100, m_mana = 100, m_petEnergy = 50 }] });
            _talentNames = Field<object>(typeof(PetProgress), "s_talentNames"); SetField(typeof(PetProgress), "s_talentNames", null);
            AddTalent(Damage, new StatisticEffectInfo { m_effectName = "PetTalentStormDamage02", m_lookupIndex = -1 });
            AddTalent(Health, new StatisticEffectInfo { m_effectName = "PetTalentMaxHealth01", m_lookupIndex = -1 });
            AddTalent(Pixie, new ProvideSpellEffectInfo { m_effectName = "ProvideSpell", m_spellName = "Pet - Pixie", m_numSpells = 1 });
            var capacity = new PetTalentTemplate { m_talentName = Capacity, m_rank = 1, m_effectList = [],
                m_maxStatList = [new PetStat { m_name = "Strength", m_value = 25 }] };
            Talents.Add(PetProgress.TalentId(Capacity), capacity); _cache[PetProgress.TalentId(Capacity)] = capacity;
            Native["PetTalentStormDamage02"] = new() { m_effectName = "PetTalentStormDamage02", m_effectCategory = "StormDamage",
                m_school = "Storm", m_buffAll = false, m_primaryStat1 = "Strength", m_primaryStat2 = "Will",
                m_secondaryStat = "Power", m_secondaryModifier = .5f, m_damageBonusPercent = .0001f };
            Native["PetTalentMaxHealth01"] = new() { m_effectName = "PetTalentMaxHealth01", m_effectCategory = "MaxHealth",
                m_school = "", m_buffAll = true, m_primaryStat1 = "Agility", m_primaryStat2 = "Will",
                m_secondaryStat = "Power", m_secondaryModifier = .5f, m_hitPointBonus = .12f };
            _cache[TemplateId] = new WizItemTemplate { m_templateID = TemplateId, m_school = "Storm", m_adjectiveList = ["Pet"],
                m_equipEffects = [], m_behaviors = [new PetItemBehaviorTemplate { m_behaviorName = "PetItemBehavior", m_Levels = [],
                    m_maxStats = Stats(100), m_startStats = Stats(100), m_favoriteSnackCategories = ["Cereal"], m_talents = [] }] };
            _cache[SnackTemplate] = new PetSnackItemTemplate { m_templateID = SnackTemplate, m_school = "Storm", m_adjectiveList = ["Cereal"],
                m_statModifierSet = new() { m_modifications = [new() { m_name = "Strength", m_change = 3 }] } };
            _spells = Field<Dictionary<uint, SpellTemplate>>(typeof(SpellFactory), "s_spellTemplates"); _priorSpells = new(_spells);
            _spellPaths = Field<Dictionary<uint, string>>(typeof(SpellFactory), "s_spellTemplatePaths"); _priorSpellPaths = new(_spellPaths);
            var spell = new SpellTemplate { m_name = "Pet - Pixie", m_sMagicSchoolName = "Life", m_sTypeName = "Heal",
                m_accuracy = 100, m_spellRank = new SpellRank { m_spellRank = 2 }, m_effects = [new SpellEffect {
                    m_effectType = kSpellEffects.kHeal, m_effectParam = 400, m_effectTarget = kEffectTarget.kSelf,
                    m_sDamageType = "Life", m_healModifier = 1f }] };
            SetEnum(spell, "m_spellSourceType", "kPet");
            _spells[StringHash.Compute("Pet - Pixie")] = spell; _spellPaths[StringHash.Compute("Pet - Pixie")] = "Spells/FixturePetPixie.xml";
            _manifest = CoreObjectFactory.TemplateManifest;
            CoreObjectFactory.TemplateManifest = new() { m_serializedTemplates = [new() { m_id = SpellId, m_filename = "Spells/FixturePetPixie.xml" },
                ..Talents.Values.Select(talent => new TemplateLocation { m_id = PetProgress.TalentId(talent.m_talentName.ToString()),
                    m_filename = "TalentData/" + talent.m_talentName + ".xml" })] };
            PetTalentRuntime.TestScope.Value = new() { Talent = id => Talents.GetValueOrDefault(id), Stat = name => Native.GetValueOrDefault(name),
                Serialize = _ => new ByteString(new byte[] { 3 }) };
            ClassicPetProgressTransactions.TestScope.Value = new() { SerializeEnd = _ => new(new byte[] { 1 }),
                SerializePet = _ => new(new byte[] { 2 }), MaxEnergy = _ => 50 };
            Saved = Wizard(); AddPet(PetId, expressed.Length == 0 ? [Damage] : expressed);
            WizardCollection.TestStoreScope.Value = new(Open, (session, _) => Proxy(session).Wizard);
            WizardInventoryTransactions.TestRowsScope.Value = session => Proxy(session).Items.Values.ToList();
            WizardPetSnackTransactions.TestRowsScope.Value = session => Proxy(session).Snacks.Values.ToList();
        }
        private void AddTalent(string name, GameEffectInfo info) {
            var talent = new PetTalentTemplate { m_talentName = name, m_rank = 2, m_effectList = [info], m_maxStatList = [] };
            Talents[PetProgress.TalentId(name)] = talent; _cache[PetProgress.TalentId(name)] = talent;
        }
        internal void AddPet(ulong id, params string[] expressed) {
            Items[id] = new() { m_globalID = id, m_characterId = Owner, m_templateID = TemplateId, m_debugName = "Authored Pet",
                m_inactiveBehaviors = [new ClientPetItemBehavior { m_level = 5, m_XP = 1950, m_requiredXP = 1950,
                    m_currentStats = Stats(100), m_maxStats = Stats(250), m_allTalents = [..expressed.Select(PetProgress.TalentId)],
                    m_expressedTalents = [..expressed.Select(PetProgress.TalentId)] }] };
            // Capacity tests use100 hereditary base; ordinary stat tests needroom to train.
            if (expressed.Contains(Capacity)) PetProgress.Behavior(Items[id]).m_maxStats = Stats(100);
            Saved.InventoryBehavior.InventoryItemIds.Add(id);
        }
        internal Wizard Live() {
            var live = CloneWizard(Saved); live.GameStats = new(default, 1); live.GameStats.SetBaseStats();
            live.GameStats.m_currentHitpoints = 71; live.GameStats.m_currentMana = 75;
            live.InventoryBehavior.Items = [..Items.Values.Where(item => live.InventoryBehavior.InventoryItemIds.Contains(item.m_globalID.Full)).Select(Clone)];
            live.EquipmentBehavior.EquippedItems = [..Items.Values.Where(item => live.EquipmentBehavior.EquippedItemIds.Contains(item.m_globalID.Full)).Select(Clone)];
            live.PetSnackBehavior.Snacks = Snack.m_quantity > 0 ? [Snack with { }] : [];
            return live;
        }
        private IDocumentSession Open() {
            var session = DispatchProxy.Create<IDocumentSession, PetProgressPersistenceTests.ProgressSession>(); var proxy = Proxy(session);
            proxy.Wizard = CloneWizard(Saved); proxy.Items = Items.ToDictionary(pair => pair.Key.ToString(), pair => Clone(pair.Value));
            if (Collision is not null) proxy.Items.Add("collision", Clone(Collision));
            proxy.Snacks = Snack.m_quantity > 0 ? new() { ["snack"] = Snack with { } } : [];
            proxy.Save = () => {
                Saves++; BeforeSave?.Invoke();
                if (Fail && !Durable) throw new InvalidOperationException("authored failed save");
                Saved = CloneWizard(proxy.Wizard); Items = proxy.Items.Values.ToDictionary(item => item.m_globalID.Full, Clone);
                Snack = proxy.Snacks.Values.SingleOrDefault() ?? Snack with { m_quantity = 0 };
                if (Fail) throw new InvalidOperationException("authored lost ACK");
            };
            return session;
        }
        private static PetProgressPersistenceTests.ProgressSession Proxy(IDocumentSession session)
            => (PetProgressPersistenceTests.ProgressSession)(object)session;
        internal static List<PetStat> Stats(int value) => PetRules.StatNames.Select(name => new PetStat {
            m_name = name, m_statID = PetProgress.StatId(name), m_value = value }).ToList();
        private static Wizard Wizard() => new() { CharId = Owner, InventoryBehavior = new() { InventoryItemIds = [], Items = [] },
            EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = [], SlotList = [] }, StorageBehavior = new() { BankItemIds = [], Items = [] },
            MagicSchoolBehavior = new() { Level = 1 }, PetOwnerBehavior = new(), PetSnackBehavior = new() { SnackItemIds = [SnackId], Snacks = [] },
            PlayerNameBehavior = new() { NameOverride = "Authored Wizard" }, SpellbookBehavior = new() { TemporarySpells = [] } };
        private static Wizard CloneWizard(Wizard wizard) {
            var clone = Wizard(); clone.InventoryBehavior.InventoryItemIds = [..wizard.InventoryBehavior.InventoryItemIds];
            clone.EquipmentBehavior.EquippedItemIds = [..wizard.EquipmentBehavior.EquippedItemIds];
            clone.EquipmentBehavior.SlotList = wizard.EquipmentBehavior.SlotList.Select(slot => new EquipmentSlot {
                SlotType = slot.SlotType, ItemId = slot.ItemId, ItemName = slot.ItemName, EquippedSince = slot.EquippedSince }).ToList();
            clone.StorageBehavior.BankItemIds = [..wizard.StorageBehavior.BankItemIds];
            clone.PetOwnerBehavior.EquippedPetGlobalId = wizard.PetOwnerBehavior.EquippedPetGlobalId;
            clone.PetOwnerBehavior.EquippedPetTemplateId = wizard.PetOwnerBehavior.EquippedPetTemplateId;
            clone.PetOwnerBehavior.PublishCommittedEnergy(wizard.PetOwnerBehavior);
            clone.PetSnackBehavior.SnackItemIds = [..wizard.PetSnackBehavior.SnackItemIds]; return clone;
        }
        private static WizClientObjectItem Clone(WizClientObjectItem item) => item with { m_inactiveBehaviors = item.m_inactiveBehaviors.Select(behavior => behavior is ClientPetItemBehavior pet
            ? pet with { m_currentStats = pet.m_currentStats.Select(stat => stat with { }).ToList(), m_maxStats = pet.m_maxStats.Select(stat => stat with { }).ToList(),
                m_allTalents = [..pet.m_allTalents], m_expressedTalents = [..pet.m_expressedTalents] } : behavior).ToList() };
        private static T Field<T>(Type type, string name) => (T)type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        private static void SetField(Type type, string name, object value) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);
        private static void SetEnum(object instance, string name, string value) {
            var field = instance.GetType().GetField(name); if (field is not null) { field.SetValue(instance, Enum.Parse(field.FieldType, value)); return; }
            var property = instance.GetType().GetProperty(name)!; property.SetValue(instance, Enum.Parse(property.PropertyType, value));
        }
        public void Dispose() {
            PetTalentRuntime.TestScope.Value = _oldDependencies; ClassicPetProgressTransactions.TestScope.Value = _oldProgress;
            WizardCollection.TestStoreScope.Value = _oldStore; WizardInventoryTransactions.TestRowsScope.Value = _oldRows;
            WizardPetSnackTransactions.TestRowsScope.Value = _oldSnackRows; CoreObjectFactory.TemplateManifest = _manifest;
            SetField(typeof(MagicLevelsConfig), "s_playerLevelConfig", _levels); SetField(typeof(PetProgress), "s_talentNames", _talentNames);
            _schools.Clear(); foreach (var row in _priorSchools) _schools[row.Key] = row.Value;
            _cache.Clear(); foreach (var row in _priorCache) _cache[row.Key] = row.Value;
            _spells.Clear(); foreach (var row in _priorSpells) _spells[row.Key] = row.Value;
            _spellPaths.Clear(); foreach (var row in _priorSpellPaths) _spellPaths[row.Key] = row.Value;
            ClassicRuntime.ResetForTests();
        }
    }
}
