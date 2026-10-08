// CLASSIC: real pet equipment/growth lanes, authored native fixtures, no private assets or player database.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Pets;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
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

    [Fact]
    public void GameInitializationReturnsAcknowledgedNativeIdsThatTheNextFeedActuallyRemoves() {
        using var f = InitializedFixture(); var live = f.Live();
        Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var old = Assert.Single(live.GameEffects.Snapshot());
        PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP = 0;
        PetTalentRuntime.TestScope.Value.Serialize = null; // Execute the native serializer, not the fixture stub.
        IReadOnlyList<IMessage> messages = [];
        f.BeforeSave = () => { Assert.Empty(messages); Assert.Same(old, Assert.Single(live.GameEffects.Snapshot())); };
        Assert.True(ClassicPetProgressTransactions.TryInitializeForGame(live, Fixture.PetId, out var pet, out _, out messages));
        Assert.Equal(2, f.Saves); Assert.Same(live.EquipmentBehavior.GetItem(Fixture.PetId), pet);
        Assert.Equal(2, messages.Count);
        Assert.Equal(old.m_internalID, Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(messages[0]).InternalID);
        var current = Assert.Single(live.GameEffects.Snapshot()); Assert.NotEqual(old.m_internalID, current.m_internalID);
        Assert.Equal(current.m_internalID, DecodeNativeAdd(Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(messages[1])).m_internalID);
        f.BeforeSave = null;
        Assert.True(ClassicPetProgressTransactions.TryFeed(live, Fixture.PetId, Fixture.SnackId, out var fed));
        Assert.Equal(current.m_internalID, Assert.Single(fed.Messages.OfType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>()).InternalID);
    }

    [Fact]
    public void UnchangedGameInitializationDoesNotSaveSerializeOrChurnAdmittedStatsCardsOrIds() {
        using var f = InitializedFixture(Fixture.Health, Fixture.Pixie); var live = f.Live();
        Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var effects = live.GameEffects.Snapshot(); var card = Assert.Single(live.SpellbookBehavior.TemporarySpells);
        PetTalentRuntime.TestScope.Value.Serialize = _ => throw new InvalidOperationException("Unexpected no-op serialization");
        Assert.True(ClassicPetProgressTransactions.TryInitializeForGame(live, Fixture.PetId, out _, out _, out var messages));
        Assert.Empty(messages); Assert.Equal(1, f.Saves); Assert.Equal(effects, live.GameEffects.Snapshot());
        Assert.Same(card, Assert.Single(live.SpellbookBehavior.TemporarySpells)); Assert.Equal(130, live.GameStats.m_baseHitpoints);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailedOrLostInitializationAckExposesNoTransitionAndKeepsTheAdmittedReceipt(bool durable) {
        using var f = InitializedFixture(Fixture.Health, Fixture.Pixie); var live = f.Live();
        Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var old = live.GameEffects.Snapshot(); var card = Assert.Single(live.SpellbookBehavior.TemporarySpells);
        PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP = 0;
        f.Fail = true; f.Durable = durable; IReadOnlyList<IMessage> messages = null;
        Assert.Throws<InvalidOperationException>(() => ClassicPetProgressTransactions.TryInitializeForGame(
            live, Fixture.PetId, out _, out _, out messages));
        Assert.Empty(messages); Assert.Equal(2, f.Saves); Assert.Equal(old, live.GameEffects.Snapshot());
        Assert.Same(card, Assert.Single(live.SpellbookBehavior.TemporarySpells)); Assert.Equal(130, live.GameStats.m_baseHitpoints);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
        Assert.Equal(durable, PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP != 0);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnpreparableNativeInitializationTransitionRefusesBeforeSave(bool throws) {
        using var f = InitializedFixture(); var live = f.Live();
        Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var old = Assert.Single(live.GameEffects.Snapshot());
        PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP = 0;
        PetTalentRuntime.TestScope.Value.Serialize = _ => throws
            ? throw new InvalidOperationException("Authored native preparation refusal") : default;
        Assert.False(ClassicPetProgressTransactions.TryInitializeForGame(live, Fixture.PetId, out _, out _, out var messages));
        Assert.Empty(messages); Assert.Equal(1, f.Saves); Assert.Same(old, Assert.Single(live.GameEffects.Snapshot()));
        Assert.Equal(0u, PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InitializationContextRefusalBeforeOrDuringNativePreparationHasNoSaveOrPublication(bool duringPreparation) {
        using var f = InitializedFixture(); var live = f.Live();
        Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var old = Assert.Single(live.GameEffects.Snapshot()); var valid = duringPreparation;
        PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP = 0;
        if (duringPreparation) PetTalentRuntime.TestScope.Value.Serialize = _ => { valid = false; return new(new byte[] { 3 }); };
        Assert.False(ClassicPetProgressTransactions.TryInitializeForGame(live, Fixture.PetId, out _, out _, out var messages, () => valid));
        Assert.Empty(messages); Assert.Equal(1, f.Saves); Assert.Same(old, Assert.Single(live.GameEffects.Snapshot()));
        Assert.Equal(0u, PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualJoinPublishesInitializationEffectIdsBeforeAdmissionEvenWhenEnergyRefuses(bool tooTired) {
        using var f = InitializedFixture(); f.Saved.PetOwnerBehavior.SetEnergy(50); var live = f.Live();
        Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var old = Assert.Single(live.GameEffects.Snapshot());
        PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP = 0;
        PetTalentRuntime.TestScope.Value.Serialize = null;
        if (tooTired) f.Saved.PetOwnerBehavior.SetEnergy(0);
        using var actor = await NativeInitializerFixture.Create(live);
        f.BeforeSave = () => { Assert.Empty(actor.Packets); Assert.Same(old, Assert.Single(live.GameEffects.Snapshot())); };
        await actor.Join(); var packets = await actor.Drain();
        Assert.Equal(old.m_internalID, Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(packets[0]).InternalID);
        var added = Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(packets[1]);
        var current = Assert.Single(live.GameEffects.Snapshot());
        Assert.Equal(current.m_internalID, DecodeNativeAdd(added).m_internalID); Assert.NotEqual(old.m_internalID, current.m_internalID);
        Assert.Equal(tooTired ? 0 : 1, Assert.Single(packets.OfType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>()).Success);
        Assert.All(actor.WireSenders, sender => Assert.Equal(actor.Endpoint, sender));
        Assert.Equal(2, f.Saves);
        if (tooTired) {
            Assert.Null(await actor.Session());
            Assert.DoesNotContain(packets, packet => packet is GAME_5_PROTOCOL.MSG_NEWOBJECT or PET_9_PROTOCOL.MSG_PETGAMEINIT);
        }
        else {
            Assert.Equal(5, packets.Length);
            Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>(packets[2]);
            Assert.IsType<GAME_5_PROTOCOL.MSG_NEWOBJECT>(packets[3]); Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEINIT>(packets[4]);
            var session = await actor.Session(); Assert.NotNull(session);
            // A refused initializer operation is not an existing Session token and cannot retire that game.
            Assert.True(actor.Instance.TryCapturePetGameAttach(live, out var context));
            actor.Service.Tell(new PetGameSessionOutputRefused(packets, context), actor.Endpoint);
            Assert.Same(session, await actor.Session());
            f.BeforeSave = null; await actor.Join(); var noop = await actor.Drain();
            Assert.Equal(2, f.Saves); Assert.Same(current, Assert.Single(live.GameEffects.Snapshot()));
            Assert.DoesNotContain(noop, packet => packet is GAME_5_PROTOCOL.MSG_REMOVEEFFECT or GAME_5_PROTOCOL.MSG_ADDEFFECT);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualJoinFailedOrLostInitializationAckSendsNoNativeEffectOrAdmission(bool durable) {
        using var f = InitializedFixture(); f.Saved.PetOwnerBehavior.SetEnergy(50); var live = f.Live();
        Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var old = Assert.Single(live.GameEffects.Snapshot());
        PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP = 0;
        PetTalentRuntime.TestScope.Value.Serialize = null; f.Fail = true; f.Durable = durable;
        using var actor = await NativeInitializerFixture.Create(live);
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.Join());
        await actor.ParentBarrier(); Assert.True(actor.Instance.IsDisposed);
        Assert.Empty(await actor.Drain()); Assert.Same(old, Assert.Single(live.GameEffects.Snapshot()));
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); Assert.Equal(2, f.Saves);
    }

    [Fact]
    public async Task ActualJoinAcknowledgedNormalizationAfterAttachLossStaysDurableWithoutStaleNativeOutput() {
        using var f = InitializedFixture(); f.Saved.PetOwnerBehavior.SetEnergy(50); var live = f.Live();
        Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP = 0;
        PetTalentRuntime.TestScope.Value.Serialize = null;
        using var actor = await NativeInitializerFixture.Create(live);
        f.BeforeSave = () => actor.Instance.PublishDoorAttach(null);
        await actor.Join(); Assert.Equal(2, f.Saves);
        Assert.NotEqual(0u, PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP);
        Assert.NotEqual(0u, PetProgress.Behavior(live.EquipmentBehavior.GetItem(Fixture.PetId)).m_requiredXP);
        Assert.Empty(await actor.Drain()); Assert.Null(await actor.Session()); Assert.False(actor.Instance.IsDisposed);
    }

    [Fact]
    public async Task ActualPhantomKioskPublishesAcknowledgedEffectIdsBeforeJoinAndTeleport() {
        using var f = InitializedFixture(); f.Saved.PetOwnerBehavior.SetEnergy(50); var live = f.Live();
        Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var old = Assert.Single(live.GameEffects.Snapshot());
        PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP = 0;
        PetTalentRuntime.TestScope.Value.Serialize = null;
        using var actor = await NativeInitializerFixture.Create(live);
        f.BeforeSave = () => { Assert.Empty(actor.Output); Assert.Same(old, Assert.Single(live.GameEffects.Snapshot())); };
        await actor.Join(PetGameObjectCodec.Cannon); await actor.Drain();
        var output = actor.Output.ToArray(); Assert.Equal(4, output.Length);
        Assert.Equal(old.m_internalID, Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(output[0]).InternalID);
        Assert.Equal(Assert.Single(live.GameEffects.Snapshot()).m_internalID,
            DecodeNativeAdd(Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(output[1])).m_internalID);
        Assert.Equal(1, Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEJOINRSP>(output[2]).Success);
        var transfer = Assert.IsType<ZONE_102_PROTOCOL.MSG_ZONETRANSFER>(output[3]);
        Assert.Equal(PetGameScenes.ZoneFor(PetGameObjectCodec.Cannon, 0), transfer.DestinationZone);
        Assert.True(transfer.IsPrivate); Assert.Equal("PlayerStart", transfer.DestinationLocation);
        Assert.Equal(2, f.Saves); Assert.Null(await actor.Session());
    }

    [Fact]
    public async Task ActualPhantomArrivalPublishesItsFreshInitializationEffectIdsBeforeLogicAndInit() {
        using var f = InitializedFixture(); f.Saved.PetOwnerBehavior.SetEnergy(50); var live = f.Live();
        Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        var old = Assert.Single(live.GameEffects.Snapshot()); PetTalentRuntime.TestScope.Value.Serialize = null;
        using var actor = await NativeInitializerFixture.Create(live);
        await actor.Join(PetGameObjectCodec.Cannon); await actor.Drain(); actor.ClearOutput();
        Assert.Equal(1, f.Saves); // The kiosk read was unchanged; normalization is fresh on arrival.
        PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP = 0;
        f.BeforeSave = () => { Assert.Empty(actor.Output); Assert.Same(old, Assert.Single(live.GameEffects.Snapshot())); };
        await actor.Arrive(PetGameObjectCodec.Cannon); var packets = await actor.Drain();
        Assert.Equal(4, packets.Length);
        Assert.Equal(old.m_internalID, Assert.IsType<GAME_5_PROTOCOL.MSG_REMOVEEFFECT>(packets[0]).InternalID);
        Assert.Equal(Assert.Single(live.GameEffects.Snapshot()).m_internalID,
            DecodeNativeAdd(Assert.IsType<GAME_5_PROTOCOL.MSG_ADDEFFECT>(packets[1])).m_internalID);
        Assert.IsType<GAME_5_PROTOCOL.MSG_NEWOBJECT>(packets[2]);
        Assert.Equal(PetGameObjectCodec.Cannon, Assert.IsType<PET_9_PROTOCOL.MSG_PETGAMEINIT>(packets[3]).Game.ToString());
        Assert.DoesNotContain(packets, packet => packet is PET_9_PROTOCOL.MSG_PETGAMEJOINRSP);
        Assert.NotNull(await actor.Session()); Assert.Equal(2, f.Saves);
        Assert.All(actor.WireSenders, sender => Assert.Equal(actor.Endpoint, sender));
    }

    [Theory]
    [InlineData(false, true)] [InlineData(true, true)]
    [InlineData(false, false)] [InlineData(true, false)]
    public async Task ActualPhantomNormalizationAfterSaveAttachLossEmitsNoEffectsAdmissionOrTravel(bool arrival, bool talentEffects) {
        using var f = InitializedFixture(); f.Saved.PetOwnerBehavior.SetEnergy(50); var live = f.Live();
        Assert.True(live.InventoryToEquipmentTransfer(Fixture.PetId, out _, out _));
        PetTalentRuntime.TestScope.Value.Serialize = null;
        using var actor = await NativeInitializerFixture.Create(live);
        actor.TalentsEnabled = talentEffects; // Empty transitions must still enforce the post-ACK admission guard.
        if (arrival) { await actor.Join(PetGameObjectCodec.Cannon); await actor.Drain(); actor.ClearOutput(); }
        PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP = 0;
        f.BeforeSave = () => actor.Instance.PublishDoorAttach(null);
        if (arrival) await actor.Arrive(PetGameObjectCodec.Cannon);
        else await actor.Join(PetGameObjectCodec.Cannon);
        Assert.Equal(2, f.Saves); Assert.NotEqual(0u, PetProgress.Behavior(f.Items[Fixture.PetId]).m_requiredXP);
        Assert.Empty(await actor.Drain()); Assert.Empty(actor.Output); Assert.Null(await actor.Session());
        Assert.False(actor.Instance.IsDisposed);
        Assert.False(PetGameTransfers.TryConsume(live.Account.AccountId, live.CharId,
            PetGameScenes.ZoneFor(PetGameObjectCodec.Cannon, 0), DateTime.UtcNow, out _));
    }

    // These admission regressions start from fully initialized authoritative rows. Existing fixtures
    // intentionally also cover legacy rating normalization; do not change their saved shapes globally.
    private static Fixture InitializedFixture(params string[] expressed) {
        var fixture = new Fixture(expressed);
        PetProgress.EnsureInitialized(fixture.Items[Fixture.PetId]);
        return fixture;
    }

    private static GameEffectBase DecodeNativeAdd(GAME_5_PROTOCOL.MSG_ADDEFFECT packet) {
        var raw = (byte[])packet.EffectData;
        // Independently assert the normal native effect header used by EquipmentService, not only a codec roundtrip.
        Assert.True(raw.Length >= 6); Assert.Equal((byte)0, raw[0]); Assert.Equal((byte)0, raw[1]);
        Assert.Equal(new WizStatisticEffect().GetHash(), System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(2, 4)));
        Assert.True(new CoreObjectSerializer(versionable: false, behaviors: SerializerFlags.None).Deserialize<GameEffectBase>(raw,
            PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit, out var effect));
        return Assert.IsType<WizStatisticEffect>(effect);
    }

    // CLASSIC: real registered producer, completed attach, parent authority and socket sink. Only the
    // mailbox adapter installs this test's in-memory persistence/native fixtures for each dispatch.
    private sealed class NativeInitializerFixture : IDisposable {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private const string Dance = "PetGameDance";
        private readonly ActorSystem _system;
        private readonly Wizard _live;
        private readonly ClassicRules _rules;
        private readonly Func<IReadOnlySet<string>> _oldOwnerFeatures;
        internal bool TalentsEnabled = true;
        private readonly PetTalentDependencies _talents = PetTalentRuntime.TestScope.Value;
        private readonly PetProgressDependencies _progress = ClassicPetProgressTransactions.TestScope.Value;
        private readonly WizardCollection.TestStore _store = WizardCollection.TestStoreScope.Value;
        private readonly Func<IDocumentSession, List<WizClientObjectItem>> _rows = WizardInventoryTransactions.TestRowsScope.Value;
        private readonly Func<IDocumentSession, List<ClientPetSnackItem>> _snacks = WizardPetSnackTransactions.TestRowsScope.Value;
        internal readonly ConcurrentQueue<IMessage> Packets = new();
        // Native socket frames and trusted internal travel share this probe recipient, preserving parent send order.
        internal readonly ConcurrentQueue<object> Output = new();
        internal readonly ConcurrentQueue<IActorRef> WireSenders = new();
        internal IActorRef Endpoint, Service;
        internal SessionActor Instance;
        private IActorRef _socket, _attachSender;
        private ZoneAttachContext _attach;
        private readonly FieldInfo _games = typeof(PetGameConfigs).GetField("s_games", Static)!;
        private readonly object _oldGames, _lazy, _oldValue, _oldState;
        private readonly FieldInfo _value, _state;
        private sealed record Ready;
        private sealed record Inspect;
        private sealed record Send(object Message);
        private sealed record Snapshot(object Session);
        private NativeInitializerFixture(Wizard live) {
            _live = live; live.Zone = "QA/PetInitializer/" + Guid.NewGuid().ToString("N");
            _rules = ClassicRuntime.Rules; _oldOwnerFeatures = _rules.OwnerExtraFeatures;
            // Enable game admission only in this new actor fixture; preserve other talent/profile tests.
            _rules.OwnerExtraFeatures = () => {
                var features = new HashSet<string>(_oldOwnerFeatures()) { ClassicFeatures.PetsLeveling };
                if (TalentsEnabled) features.Add(ClassicFeatures.PetsTalents);
                else features.Remove(ClassicFeatures.PetsTalents);
                return features;
            };
            live.Account = new Account { AuthLevel = AuthLevel.None };
            typeof(Account).GetProperty(nameof(Account.AccountId))!.SetValue(live.Account, 811010UL);
            live.GameObject = new WizClientObject { m_templateID = 1, m_inactiveBehaviors = [] };
            _oldGames = _games.GetValue(null);
            _games.SetValue(null, PetGameConfigs.KioskGames.Values.ToDictionary(game => game, game => new PetGameInfo {
                m_name = game, m_energyCosts = [], m_gameIcon = "", m_trackIcons = [], m_trackToolTips = [],
                m_trackChoices = [new PetStatModificationSet { m_name = "AuthoredTrack", m_scene = "AuthoredScene",
                    m_gameScoreFactor = [], m_modifications = [new() { m_name = "Agility", m_change = 4 }] }] }, StringComparer.Ordinal));
            _lazy = typeof(PetGameConfigs).BaseType!.GetField("s_instance", Static)!.GetValue(null)!;
            _value = _lazy.GetType().GetField("_value", Private)!; _state = _lazy.GetType().GetField("_state", Private)!;
            _oldValue = _value.GetValue(_lazy); _oldState = _state.GetValue(_lazy);
            _value.SetValue(_lazy, RuntimeHelpers.GetUninitializedObject(typeof(PetGameConfigs))); _state.SetValue(_lazy, null);
            var cache = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory).GetField("s_templateCache", Static)!.GetValue(null)!;
            // The outer Fixture owns and restores the entire template cache.
            foreach (var (game, id) in PetGameObjectCodec.LogicTemplates)
                cache[id] = new WizItemTemplate { m_templateID = id, m_displayName = "", m_behaviors = [new PetGameBehaviorTemplate {
                    m_behaviorName = "PetGameBehavior", m_gameName = game }] };
            _system = ActorSystem.Create("pet-initializer-" + Guid.NewGuid().ToString("N"), "akka.actor.provider = local");
        }
        internal static async Task<NativeInitializerFixture> Create(Wizard live) {
            var f = new NativeInitializerFixture(live);
            try {
                f._socket = f._system.ActorOf(Props.Create(() => new SocketProbe(f)), "socket");
                f.Endpoint = f._system.ActorOf(Props.CreateBy(new ParentProducer(f._socket)), "parent");
                f.Instance = await f.Endpoint.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken);
                ActiveWizardDirectory.SetWizard(f.Endpoint, live); ActiveWizardDirectory.SetGameObject(f.Endpoint, live.GameObject);
                f._attachSender = f._system.ActorOf(Props.Create(() => new AttachProbe(f.Instance)), "attach");
                await f._attachSender.Ask<ActorIdentity>(new Identify("ready"), Timeout, TestContext.Current.CancellationToken);
                var fanout = f._system.ActorOf(Props.Create(() => new AttachFanout(f.Instance)), "fanout");
                await fanout.Ask<ActorIdentity>(new Identify("ready"), Timeout, TestContext.Current.CancellationToken);
                var routes = (Dictionary<Type, List<IActorRef>>)typeof(SessionActor).GetField("_dispatchTable", Private)!.GetValue(f.Instance)!;
                routes[typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE)] = [fanout];
                f._attach = new ZoneAttachContext(live.Zone, f._socket, 1, live.GameObjectID);
                f.Instance.PublishDoorAttach(f._attach);
                f.Endpoint.Tell(new SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE { AttachGeneration = f._attach.Generation,
                    ZoneActorRef = f._attach.Actor }, f._attachSender);
                await f.ParentBarrier(); Assert.True(f.Instance.TryCapturePetGameAttach(live, out _));
                f.Service = f._system.ActorOf(Props.CreateBy(new PetProducer(f)), "pet");
                Assert.True(await f.Service.Ask<bool>(new Ready(), Timeout, TestContext.Current.CancellationToken));
                var order = (List<(IActorRef Ref, Type Type)>)typeof(SessionActor).GetField("_serviceOrder", Private)!.GetValue(f.Instance)!;
                order.Add((f.Service, typeof(PetGameService)));
                foreach (var type in MessageHandlerTable.HandlersOf(typeof(PetGameService)).Keys)
                    routes[type] = type == typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE) ? [fanout, f.Service] : [f.Service];
                routes[typeof(ZONE_102_PROTOCOL.MSG_ZONETRANSFER)] = [f._socket];
                f.Endpoint.Tell(new SERVICE_101_PROTOCOL.MSG_GETALLSERVICES());
                return f;
            } catch { f.Dispose(); throw; }
        }
        internal async Task Join(string game = Dance) {
            await Service.Ask<bool>(new Send(new PET_9_PROTOCOL.MSG_PETGAMEJOIN { Game = game, Track = "0" }),
                Timeout, TestContext.Current.CancellationToken);
            await ParentBarrier(); await Session(); await ParentBarrier();
        }
        internal async Task Arrive(string game) {
            _live.PreviousZone = _live.Zone; _live.Zone = PetGameScenes.ZoneFor(game, 0);
            _attach = _attach with { Zone = _live.Zone, Generation = _attach.Generation + 1 };
            Instance.PublishDoorAttach(_attach);
            Endpoint.Tell(new SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE { AttachGeneration = _attach.Generation,
                ZoneActorRef = _attach.Actor }, _attachSender);
            await ParentBarrier(); await Session(); await ParentBarrier(); await Session();
        }
        internal void ClearOutput() { while (Output.TryDequeue(out _)) { } }
        // The same authored scene shape used by existing phantom admission tests; no game assets are read.
        private static PetGameScene AuthoredScene(string game, int track) => game == PetGameObjectCodec.Cannon
            ? new(game, track, PetGameScenes.ZoneFor(game, track), PetGameObjectCodec.CannonTemplate, Vector3.Zero,
                new Dictionary<string, Vector3> { ["Home"] = Vector3.Zero, ["PlayerStart"] = new(800, 0, 0) },
                [new(1000, 0, 0)], null, PetGameScenes.CannonTargetTemplate) : null;
        internal async Task<object> Session() => (await Service.Ask<Snapshot>(new Inspect(), Timeout,
            TestContext.Current.CancellationToken)).Session;
        internal async Task ParentBarrier() {
            if (Instance is null || Instance.IsDisposed) return;
            try { await Endpoint.Ask<SessionActor>("Identify", Timeout, TestContext.Current.CancellationToken); }
            catch (AskTimeoutException) when (Instance.IsDisposed) { }
        }
        internal async Task<IMessage[]> Drain() {
            await ParentBarrier();
            await _socket.Ask<ActorIdentity>(new Identify("drain"), Timeout, TestContext.Current.CancellationToken);
            var packets = new List<IMessage>(); while (Packets.TryDequeue(out var packet)) packets.Add(packet);
            return packets.ToArray();
        }
        private IDisposable EnterScope() {
            var oldTalents = PetTalentRuntime.TestScope.Value; var oldProgress = ClassicPetProgressTransactions.TestScope.Value;
            var oldStore = WizardCollection.TestStoreScope.Value; var oldRows = WizardInventoryTransactions.TestRowsScope.Value;
            var oldSnacks = WizardPetSnackTransactions.TestRowsScope.Value; var oldScenes = PetGameScenes.TestScope.Value;
            PetGameScenes.TestScope.Value = AuthoredScene;
            PetTalentRuntime.TestScope.Value = _talents; ClassicPetProgressTransactions.TestScope.Value = _progress;
            WizardCollection.TestStoreScope.Value = _store; WizardInventoryTransactions.TestRowsScope.Value = _rows;
            WizardPetSnackTransactions.TestRowsScope.Value = _snacks;
            return new Restore(() => { PetTalentRuntime.TestScope.Value = oldTalents; ClassicPetProgressTransactions.TestScope.Value = oldProgress;
                WizardCollection.TestStoreScope.Value = oldStore; WizardInventoryTransactions.TestRowsScope.Value = oldRows;
                WizardPetSnackTransactions.TestRowsScope.Value = oldSnacks; PetGameScenes.TestScope.Value = oldScenes; });
        }
        private sealed class SocketProbe : ReceiveActor {
            public SocketProbe(NativeInitializerFixture fixture) {
                Receive<ZONE_102_PROTOCOL.MSG_ZONETRANSFER>(transfer => fixture.Output.Enqueue(transfer));
                Receive<IMessage>(packet => { fixture.WireSenders.Enqueue(Sender); fixture.Packets.Enqueue(packet); fixture.Output.Enqueue(packet); });
            }
        }
        private sealed class AttachProbe(SessionActor parent) : AttachService(parent) { protected override void PreStart() { } }
        private sealed class AttachFanout(SessionActor parent) : MessageService(parent) {
            [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
            private void Completed(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) { }
        }
        private sealed class ParentProducer(IActorRef socket) : IIndirectActorProducer {
            public Type ActorType => typeof(SessionActor);
            public ActorBase Produce() => new SessionActor(socket);
            public void Release(ActorBase actor) { }
        }
        private sealed class PetProducer(NativeInitializerFixture fixture) : IIndirectActorProducer {
            public Type ActorType => typeof(PetGameService);
            public ActorBase Produce() {
                var service = new PetGameService(fixture.Instance);
                typeof(MessageService).GetField("_cachedWizard", Private)!.SetValue(service, fixture._live);
                typeof(MessageService).GetField("_cachedWizardGameObject", Private)!.SetValue(service, fixture._live.GameObject);
                var driver = new Driver(service, fixture);
                typeof(ActorBase).GetMethod("Become", Private, null, [typeof(Receive)], null)!.Invoke(service, [new Receive(driver.Dispatch)]);
                return service;
            }
            public void Release(ActorBase actor) { }
        }
        private sealed class Driver(PetGameService service, NativeInitializerFixture fixture) {
            internal bool Dispatch(object message) {
                var sender = (IActorRef)typeof(ActorBase).GetProperty("Sender", Private | BindingFlags.Public)!.GetValue(service)!;
                try {
                    if (message is Ready) { sender.Tell(true); return true; }
                    if (message is Inspect) { sender.Tell(new Snapshot(typeof(PetGameService).GetField("_session", Private)!.GetValue(service))); return true; }
                    var actual = message is Send send ? send.Message : message;
                    if (service.ShouldStashForPublication(actual)) { service.Stash.Stash(); return true; }
                    using var scope = fixture.EnterScope();
                    var dispatch = MessageHandlerTable.DispatcherFor(typeof(PetGameService), actual.GetType());
                    Assert.NotNull(dispatch); dispatch(service, actual);
                    if (message is Send) sender.Tell(true);
                } catch (Exception error) { sender.Tell(new Status.Failure(error)); }
                return true;
            }
        }
        private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
        public void Dispose() {
            _system.Terminate().GetAwaiter().GetResult(); _system.Dispose();
            if (Endpoint is not null) ActiveWizardDirectory.Remove(Endpoint);
            PetGameTransfers.Cancel(_live.CharId); _rules.OwnerExtraFeatures = _oldOwnerFeatures;
            _games.SetValue(null, _oldGames); _value.SetValue(_lazy, _oldValue); _state.SetValue(_lazy, _oldState);
        }
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
