// CLASSIC: authored progression and potion values exercise the real character write lane and service receipts.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imcodec.Cryptography;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Classic.Elixirs;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ProgressionPotionPersistenceTests : IDisposable {
    public ProgressionPotionPersistenceTests() {
        EquipmentAttachConcurrencyTests.Configure("[Character]\nPetEnergyTickInSeconds=60\n[Database]\nDatabaseWaitForNonStaleResultsTimeout=5\n");
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(new ClassicRules(ZoneFixture.Profile(levelCap: 4), ZoneFixture.MinimalMap()));
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    [Fact]
    public void MultilevelGainUsesFreshXpAndPublishesRuntimeOffsetsAndExactEnergyTickAfterOneSave() {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        live.MagicSchoolBehavior.ExperiencePoints = 9;
        var stats = live.GameStats; var school = live.MagicSchoolBehavior; var owner = live.PetOwnerBehavior;
        var equipment = live.EquipmentBehavior; var effects = stats.m_dmgBonusPercent;
        var before = State.Of(live); var sent = new List<IMessage>(); var broadcast = new List<IMessage>();
        var sequence = new List<IMessage>();
        f.OnSave = () => {
            Assert.Equal(before, State.Of(live)); Assert.Empty(sequence);
            Assert.Equal(350, f.Working!.Wizard.MagicSchoolBehavior.ExperiencePoints);
            Assert.Equal(4, f.Working.Wizard.MagicSchoolBehavior.Level);
            Assert.Equal(191, f.Working.Wizard.GameStats.m_currentHitpoints);
            Assert.Equal(77, f.Working.Wizard.GameStats.m_currentMana);
            Assert.Equal(24, f.Working.Wizard.PetOwnerBehavior.Energy);
        };
        Assert.True(WizardService.ApplyExperience(live, 250,
            message => { Assert.False(WizardCollection.HoldsWriteLane); sent.Add(message); sequence.Add(message); },
            message => { Assert.False(WizardCollection.HoldsWriteLane); broadcast.Add(message); sequence.Add(message); },
            () => Assert.Fail("an acknowledged gain must not close the session")));
        Assert.Equal(1, f.Saves); Assert.Equal(350, school.ExperiencePoints); Assert.Equal(4, school.Level);
        Assert.Equal(4, stats.Level); Assert.Equal(191, stats.m_baseHitpoints); Assert.Equal(77, stats.m_baseMana);
        Assert.Equal(191, stats.m_currentHitpoints); Assert.Equal(77, stats.m_currentMana); Assert.Equal(.625f, stats.m_powerPipBase);
        Assert.Equal(24, owner.Energy); Assert.Equal(f.Saved.PetOwnerBehavior.LastEnergyTickEpoch, owner.LastEnergyTickEpoch);
        Assert.Same(stats, live.GameStats); Assert.Same(school, live.MagicSchoolBehavior); Assert.Same(owner, live.PetOwnerBehavior);
        Assert.Same(equipment, live.EquipmentBehavior); Assert.Same(effects, stats.m_dmgBonusPercent);
        Assert.Equal(999, stats.m_currentGold); Assert.Equal(9f, stats.m_potionCharge); Assert.Equal(9f, stats.m_potionMax);
        Assert.Equal(77, school.TrainingPoints); Assert.Equal(88, school.OverflowXp); Assert.Equal(9, owner.MaxSlots);
        Assert.Equal(87, f.Saved.GameStats.m_currentGold); Assert.Equal(1.25f, f.Saved.GameStats.m_potionCharge);
        Assert.Equal(5, f.Saved.MagicSchoolBehavior.TrainingPoints); Assert.Equal(8, f.Saved.MagicSchoolBehavior.OverflowXp);
        Assert.Equal(1, f.Saved.PetOwnerBehavior.MaxSlots);
        Assert.IsType<WIZARD_12_PROTOCOL.MSG_LEVELUP>(Assert.Single(broadcast));
        AssertLevelPackets(sequence.Take(5).ToArray(), live, 4);
        var xp = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEXP>(sequence[5]);
        Assert.Equal(live.GameObjectID, xp.GlobalID); Assert.Equal(150, xp.OldXP); Assert.Equal(200, xp.XP);
        Assert.Equal(5, sent.Count); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData(150, 2, 0, 150, 2, true)]
    [InlineData(150, 2, -20, 130, 2, true)]
    [InlineData(340, 4, 40, 350, 4, true)]
    [InlineData(350, 4, 40, 350, 4, false)]
    [InlineData(150, 2, 250, 350, 4, true)]
    public void ZeroNegativePartialCapAndCapNoOpKeepExistingXpArithmetic(
        int oldXp, int oldLevel, int requested, int expectedXp, int expectedLevel, bool save) {
        var f = new Fixture(oldXp, oldLevel); using var scope = f.Scope(); var live = f.Live();
        live.MagicSchoolBehavior.ExperiencePoints = 9; var before = State.Of(live);
        f.OnSave = () => Assert.Equal(before, State.Of(live));
        Assert.True(WizardProgressionTransactions.TryGainExperience(live, requested, out var receipt));
        Assert.Equal(oldXp, receipt.OldXp); Assert.Equal(expectedXp - oldXp, receipt.AppliedXp);
        Assert.Equal(expectedLevel, receipt.Level); Assert.Equal(save, receipt.ShouldSave);
        Assert.Equal(save ? 1 : 0, f.Saves); Assert.Equal(expectedXp, f.Saved.MagicSchoolBehavior.ExperiencePoints);
        Assert.Equal(save ? expectedXp : 9, live.MagicSchoolBehavior.ExperiencePoints);
        if (save) {
            Assert.Equal(oldXp, receipt.XpMessage.OldXP); Assert.Equal(expectedXp - oldXp, receipt.XpMessage.XP);
        } else { Assert.Null(receipt.XpMessage); Assert.Empty(receipt.LevelMessages); Assert.Equal(before, State.Of(live)); }
        if (expectedLevel == oldLevel) {
            Assert.Empty(receipt.LevelMessages); Assert.Equal(99, live.PetOwnerBehavior.Energy);
            Assert.Equal(31, live.GameStats.m_currentHitpoints); Assert.Equal(19, live.GameStats.m_currentMana);
        }
    }

    [Theory]
    [InlineData(0, 150, 2, 151, 57, .375f)]
    [InlineData(30, 120, 2, 151, 57, .375f)]
    [InlineData(80, 70, 1, 131, 47, .25f)]
    public void RemovalUsesFreshXpAndLevelDownDoesNotHealRefillEnergyOrCreatePackets(
        int requested, int xp, int level, int healthMax, int manaMax, float pips) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        live.MagicSchoolBehavior.ExperiencePoints = 999; var before = State.Of(live);
        f.OnSave = () => Assert.Equal(before, State.Of(live));
        Assert.True(WizardProgressionTransactions.TryRemoveExperience(live, requested, out var receipt));
        Assert.Equal(1, f.Saves); Assert.Equal(xp, f.Saved.MagicSchoolBehavior.ExperiencePoints);
        Assert.Equal(xp, live.MagicSchoolBehavior.ExperiencePoints); Assert.Equal(level, live.MagicSchoolBehavior.Level);
        Assert.Equal(healthMax, live.GameStats.m_baseHitpoints); Assert.Equal(manaMax, live.GameStats.m_baseMana);
        Assert.Equal(pips, live.GameStats.m_powerPipBase); Assert.Equal(31, live.GameStats.m_currentHitpoints);
        Assert.Equal(19, live.GameStats.m_currentMana); Assert.Equal(23, f.Saved.GameStats.m_currentHitpoints);
        Assert.Equal(17, f.Saved.GameStats.m_currentMana); Assert.Equal(99, live.PetOwnerBehavior.Energy);
        Assert.Equal(7, f.Saved.PetOwnerBehavior.Energy); Assert.Equal(1234u, f.Saved.PetOwnerBehavior.LastEnergyTickEpoch);
        Assert.False(receipt.Refill); Assert.Null(receipt.XpMessage); Assert.Empty(receipt.LevelMessages);
    }

    [Theory]
    [InlineData(4, true, 300, 4)]
    [InlineData(4, false, 150, 4)]
    [InlineData(2, true, 150, 2)]
    [InlineData(0, true, 0, 1)]
    [InlineData(99, true, 300, 4)]
    public void ExplicitLevelChangeRetainsResetPolicyClampAndSameLevelRefill(
        byte requested, bool reset, int xp, int level) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); var before = State.Of(live);
        f.OnSave = () => Assert.Equal(before, State.Of(live));
        Assert.True(WizardProgressionTransactions.TrySetLevel(live, requested, reset, out var receipt));
        Assert.Equal(1, f.Saves); Assert.Equal(xp, f.Saved.MagicSchoolBehavior.ExperiencePoints);
        Assert.Equal(xp, live.MagicSchoolBehavior.ExperiencePoints); Assert.Equal(level, live.MagicSchoolBehavior.Level);
        Assert.Equal(Fixture.Table(level).m_hitpoints + 11, live.GameStats.m_currentHitpoints);
        Assert.Equal(Fixture.Table(level).m_mana + 7, live.GameStats.m_currentMana);
        Assert.Equal(Fixture.Table(level).m_petEnergy, live.PetOwnerBehavior.Energy);
        Assert.Equal(f.Saved.PetOwnerBehavior.LastEnergyTickEpoch, live.PetOwnerBehavior.LastEnergyTickEpoch);
        Assert.True(receipt.Refill); Assert.True(receipt.AdjustStats); Assert.Null(receipt.XpMessage);
        AssertLevelPackets(receipt.LevelMessages, live, level);
    }

    [Fact]
    public void StaffServiceResetsXpAndBroadcastsLevelBeforePrivateRefillsOnlyAfterAck() {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); var packets = new List<IMessage>();
        var broadcasts = new List<IMessage>(); var before = State.Of(live);
        f.OnSave = () => { Assert.Equal(before, State.Of(live)); Assert.Empty(packets); };
        Assert.True(WizardService.ApplyLevel(live, 3, packets.Add, packet => { broadcasts.Add(packet); packets.Add(packet); },
            () => Assert.Fail("a successful staff refill must not close")));
        Assert.Equal(200, live.MagicSchoolBehavior.ExperiencePoints); Assert.Equal(1, f.Saves);
        AssertLevelPackets(packets, live, 3); Assert.IsType<WIZARD_12_PROTOCOL.MSG_LEVELUP>(Assert.Single(broadcasts));
    }

    [Theory]
    [InlineData("gain", false)] [InlineData("gain", true)]
    [InlineData("level", false)] [InlineData("level", true)]
    [InlineData("remove", false)] [InlineData("remove", true)]
    [InlineData("drink", false)] [InlineData("drink", true)]
    [InlineData("buy", false)] [InlineData("buy", true)]
    [InlineData("slot", false)] [InlineData("slot", true)]
    [InlineData("minigame", false)] [InlineData("minigame", true)]
    [InlineData("set", false)] [InlineData("set", true)]
    public void FailedAndDurableLostAckQuarantineBeforeDisposeWithoutPublicationAndBlockQueuedWrites(
        string operation, bool durable) {
        var f = new Fixture { Fail = true, Durable = durable }; using var scope = f.Scope(); var live = f.Live();
        var before = State.Of(live); var savedBefore = State.Of(f.Saved); var packets = new List<IMessage>();
        var closes = 0; var disposedInLane = false;
        f.OnSave = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
            Assert.Equal(before, State.Of(live)); Assert.Empty(packets); Assert.Equal(0, closes);
        };
        f.OnDispose = () => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
            Assert.Equal(before, State.Of(live)); Assert.Empty(packets); disposedInLane = true;
        };
        Assert.Throws<InvalidOperationException>(() => f.Apply(operation, live, packets.Add, packets.Add, () => {
            Assert.False(WizardCollection.HoldsWriteLane); Assert.True(disposedInLane); closes++;
        }));
        Assert.True(disposedInLane); Assert.False(WizardCollection.HoldsWriteLane);
        Assert.Equal(operation is "gain" or "level" or "drink" or "buy" ? 1 : 0, closes);
        Assert.Equal(before, State.Of(live)); Assert.Empty(packets); Assert.Equal(1, f.Saves);
        if (durable) {
            var expected = operation switch {
                "gain" => savedBefore with { Xp = 350, Level = 4, HealthMax = 191, ManaMax = 77,
                    Health = 191, Mana = 77, Energy = 24, Tick = f.Working!.Wizard.PetOwnerBehavior.LastEnergyTickEpoch },
                "level" => savedBefore with { Xp = 300, Level = 4, HealthMax = 191, ManaMax = 77,
                    Health = 191, Mana = 77, Energy = 24, Tick = f.Working!.Wizard.PetOwnerBehavior.LastEnergyTickEpoch },
                "remove" => savedBefore with { Xp = 70, Level = 1, HealthMax = 131, ManaMax = 47 },
                "drink" => savedBefore with { Health = 151, Mana = 57, Charge = .25f },
                "buy" => savedBefore with { Gold = 75, Charge = 3f },
                "slot" => savedBefore with { Charge = 4f, PotionMax = 4f },
                "minigame" => savedBefore with { Mana = 48 },
                "set" => savedBefore with { Charge = 2.5f, PotionMax = 5f },
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
            Assert.Equal(expected, State.Of(f.Saved));
        } else Assert.Equal(savedBefore, State.Of(f.Saved));
        var opens = f.Opened;
        foreach (var next in Fixture.Operations) Assert.False(f.Change(next, live));
        Assert.False(WizardCollection.UpdateCharacterGameStats(live, f.Open, f.Load));
        live.UpdateHealth(999); live.UpdateMana(999); live.UpdateMaxHealth(999); live.UpdateMaxMana(999); live.UpdateEnergy(999);
        WizardCollection.UpdateCharacterItems(live); WizardCollection.UpdateCharacterLevel(live);
        WizardCollection.UpdateCharacterPetOwnerBehavior(live);
        Assert.Equal(opens, f.Opened); Assert.Equal(1, f.Saves); Assert.Equal(before, State.Of(live));
    }

    [Theory]
    [InlineData("gain", false)] [InlineData("gain", true)]
    [InlineData("level", false)] [InlineData("level", true)]
    [InlineData("drink", false)] [InlineData("drink", true)]
    [InlineData("buy", false)] [InlineData("buy", true)]
    [InlineData("slot", false)] [InlineData("slot", true)]
    [InlineData("minigame", false)] [InlineData("minigame", true)]
    [InlineData("set", false)] [InlineData("set", true)]
    public void NativePacketPreparationRefusalOrExceptionCannotSavePublishOrQuarantine(string operation, bool throws) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        var before = State.Of(live); var savedBefore = State.Of(f.Saved); var packets = new List<IMessage>();
        var attempts = 0;
        f.Dependencies.Prepare = _ => {
            attempts++; Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(before, State.Of(live));
            if (throws) throw new InvalidOperationException("authored preparation failure");
            return false;
        };
        Assert.False(f.Apply(operation, live, packets.Add, packets.Add, () => Assert.Fail("a proven refusal must not close")));
        Assert.True(attempts > 0); Assert.Equal(0, f.Saves); Assert.Equal(savedBefore, State.Of(f.Saved));
        Assert.Equal(before, State.Of(live)); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
        if (operation == "buy") Assert.Equal(1, Assert.IsType<WIZARD_12_PROTOCOL.MSG_POTIONBUYCONFIRM>(Assert.Single(packets)).Failure);
        else Assert.Empty(packets);
    }

    [Theory]
    [InlineData("gain", 6, false)] [InlineData("gain", 6, true)]
    [InlineData("level", 5, false)] [InlineData("level", 5, true)]
    [InlineData("drink", 4, false)] [InlineData("drink", 4, true)]
    [InlineData("buy", 3, false)] [InlineData("buy", 3, true)]
    public void FailurePreparingTheLastPacketCannotPublishEarlierPreparedPacketsOrSave(
        string operation, int lastPacket, bool throws) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        var before = State.Of(live); var savedBefore = State.Of(f.Saved); var packets = new List<IMessage>();
        var prepared = 0;
        f.Dependencies.Prepare = _ => {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(before, State.Of(live)); Assert.Empty(packets);
            if (++prepared < lastPacket) return true;
            if (throws) throw new InvalidOperationException("authored final packet preparation failure");
            return false;
        };
        Assert.False(f.Apply(operation, live, packets.Add, packets.Add, () => Assert.Fail("refusal must not close")));
        Assert.Equal(lastPacket, prepared); Assert.Equal(0, f.Saves);
        Assert.Equal(before, State.Of(live)); Assert.Equal(savedBefore, State.Of(f.Saved));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
        if (operation == "buy") Assert.Equal(1, Assert.IsType<WIZARD_12_PROTOCOL.MSG_POTIONBUYCONFIRM>(Assert.Single(packets)).Failure);
        else Assert.Empty(packets);
    }

    [Theory]
    [InlineData("gain")] [InlineData("level")] [InlineData("remove")] [InlineData("drink")] [InlineData("minigame")]
    public void UninitializedRuntimeStatsCannotSupplyHealingOrLevelOffsets(string operation) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); live.HasInitializedRuntimeStats = false;
        var before = State.Of(live); var savedBefore = State.Of(f.Saved);
        f.Dependencies.Prepare = _ => throw new InvalidOperationException("uninitialized runtime must not prepare packets");
        Assert.False(f.Change(operation, live)); Assert.Equal(0, f.Saves);
        Assert.Equal(before, State.Of(live)); Assert.Equal(savedBefore, State.Of(f.Saved));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData("gain", 2)] [InlineData("gain", 4)]
    [InlineData("level", 2)] [InlineData("level", 4)]
    [InlineData("remove", 2)] [InlineData("remove", 1)]
    [InlineData("drink", 2)] [InlineData("minigame", 2)]
    public void MissingAuthoredLevelRowRefusesBeforeSaveAndPacketPreparation(string operation, int missingLevel) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        var before = State.Of(live); var savedBefore = State.Of(f.Saved);
        f.Dependencies.LevelInfo = (_, level) => level == missingLevel ? null! : Fixture.Table(level);
        f.Dependencies.Prepare = _ => throw new InvalidOperationException("missing row must not prepare packets");
        Assert.False(f.Change(operation, live)); Assert.Equal(0, f.Saves);
        Assert.Equal(before, State.Of(live)); Assert.Equal(savedBefore, State.Of(f.Saved));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData("gain", 350, 4, 191, 77, .625f)]
    [InlineData("level", 150, 4, 191, 77, .625f)]
    [InlineData("remove", 70, 1, 131, 47, .25f)]
    public void DirectWizardModelChangesKeepTheirExistingNoHealAndNoEnergyRefillSemantics(
        string operation, int xp, int level, int healthMax, int manaMax, float pips) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); var before = State.Of(live);
        var prepared = new List<IMessage>(); f.Dependencies.Prepare = packet => { prepared.Add(packet); return true; };
        f.OnSave = () => Assert.Equal(before, State.Of(live));
        switch (operation) {
            case "gain": Assert.Equal(200, live.AddExperiencePoints(250)); break;
            case "level": Assert.True(live.SetLevel(4)); break;
            case "remove": live.RemoveExperiencePoints(80); break;
        }
        Assert.Equal(1, f.Saves); Assert.Equal(xp, live.MagicSchoolBehavior.ExperiencePoints);
        Assert.Equal(level, live.MagicSchoolBehavior.Level); Assert.Equal(level, live.GameStats.Level);
        Assert.Equal(healthMax, live.GameStats.m_baseHitpoints); Assert.Equal(manaMax, live.GameStats.m_baseMana);
        Assert.Equal(pips, live.GameStats.m_powerPipBase); Assert.Equal(31, live.GameStats.m_currentHitpoints);
        Assert.Equal(19, live.GameStats.m_currentMana); Assert.Equal(23, f.Saved.GameStats.m_currentHitpoints);
        Assert.Equal(17, f.Saved.GameStats.m_currentMana); Assert.Equal(99, live.PetOwnerBehavior.Energy);
        Assert.Equal(9999u, live.PetOwnerBehavior.LastEnergyTickEpoch); Assert.Equal(7, f.Saved.PetOwnerBehavior.Energy);
        Assert.Equal(1234u, f.Saved.PetOwnerBehavior.LastEnergyTickEpoch);
        if (operation == "gain") Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEXP>(Assert.Single(prepared));
        else Assert.Empty(prepared);
    }

    [Theory]
    [InlineData("gain", "level")] [InlineData("gain", "school")] [InlineData("gain", "runtime-level")]
    [InlineData("gain", "items")] [InlineData("gain", "slots")] [InlineData("gain", "missing-equipment")]
    [InlineData("gain", "runtime-school")]
    [InlineData("level", "level")] [InlineData("level", "school")] [InlineData("level", "runtime-level")]
    [InlineData("level", "items")] [InlineData("level", "slots")] [InlineData("level", "missing-equipment")]
    [InlineData("level", "runtime-school")]
    [InlineData("remove", "level")] [InlineData("remove", "school")] [InlineData("remove", "runtime-level")]
    [InlineData("remove", "items")] [InlineData("remove", "slots")] [InlineData("remove", "missing-equipment")]
    [InlineData("remove", "runtime-school")]
    [InlineData("drink", "level")] [InlineData("drink", "school")] [InlineData("drink", "runtime-level")]
    [InlineData("drink", "items")] [InlineData("drink", "slots")] [InlineData("drink", "missing-equipment")]
    [InlineData("drink", "runtime-school")]
    [InlineData("minigame", "level")] [InlineData("minigame", "school")] [InlineData("minigame", "runtime-level")]
    [InlineData("minigame", "items")] [InlineData("minigame", "slots")] [InlineData("minigame", "missing-equipment")]
    [InlineData("minigame", "runtime-school")]
    public void RuntimeOffsetsCannotBeAppliedAcrossChangedSavedEquipmentSchoolOrLevel(string operation, string mismatch) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        switch (mismatch) {
            case "level": f.Saved.MagicSchoolBehavior.Level = 3; break;
            case "school": f.Saved.MagicSchoolBehavior.MagicSchool = MagicSchool.Ice; break;
            case "runtime-level": live.GameStats.Level = 3; break;
            case "runtime-school": live.GameStats.MagicSchool = MagicSchool.Ice; break;
            case "items": f.Saved.EquipmentBehavior.EquippedItemIds = [43]; break;
            case "slots": f.Saved.EquipmentBehavior.SlotList[0].SlotType = EquipmentSlotType.Robe; break;
            case "missing-equipment": f.Saved.EquipmentBehavior = null!; break;
        }
        var before = State.Of(live); var savedBefore = State.Of(f.Saved);
        Assert.False(f.Change(operation, live)); Assert.Equal(0, f.Saves);
        Assert.Equal(before, State.Of(live)); Assert.Equal(savedBefore, State.Of(f.Saved));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void InitializedElixirContextAcceptsEnabledAppliedAndDisabledUnappliedEffects(bool enabled) {
        using var canonical = new ElixirTests.CanonicalFixture(); using var runtime = canonical.OctoberRuntime();
        var template = canonical.Template(Fixture.ElixirTemplate); using var cache = Fixture.TemplateScope(template);
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        var item = f.AddElixir(live, canonical); var timer = Assert.Single(item.m_inactiveBehaviors.OfType<ClientElixirBehavior>());
        if (enabled) Assert.Single(ElixirRuntime.AddApprovedEffects(live, item, template, false, false));
        else { live.IsInDuel = true; ElixirRules.SetCombatContext(live, true, true); }
        var effects = live.GameEffects.Snapshot(); var damage = live.GameStats.m_dmgBonusPercentAll;
        var before = State.Of(live); f.OnSave = () => { Assert.Equal(before, State.Of(live)); Assert.Equal(effects, live.GameEffects.Snapshot()); };
        Assert.Equal(enabled, timer.m_statsApplied); Assert.Equal(enabled ? 1 : 0, effects.Count);
        Assert.True(WizardProgressionTransactions.RuntimeContextMatches(live, f.Saved));
        Assert.True(f.Change("gain", live)); Assert.Equal(1, f.Saves); Assert.Equal(350, live.MagicSchoolBehavior.ExperiencePoints);
        Assert.Equal(damage, live.GameStats.m_dmgBonusPercentAll); Assert.Equal(effects, live.GameEffects.Snapshot());
        Assert.Same(item, live.EquipmentBehavior.GetItem(Fixture.ElixirItem)); Assert.Equal(enabled, timer.m_statsApplied);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData("activation-pending")] [InlineData("missing-effect")] [InlineData("orphan-origin")]
    [InlineData("duplicate-slot-id")] [InlineData("duplicate-original-id")] [InlineData("duplicate-effect-origin")]
    [InlineData("wrong-effect-name")] [InlineData("unsupported-native-applied")] [InlineData("disabled-applied")]
    [InlineData("missing-timer")] [InlineData("duplicate-timer")] [InlineData("wrong-owner")] [InlineData("expired-applied")]
    public void ElixirContextRefusesIncompleteUnsupportedOrAmbiguousEffectsBeforePotionSave(string mismatch) {
        using var canonical = new ElixirTests.CanonicalFixture(); using var runtime = canonical.OctoberRuntime();
        var template = canonical.Template(Fixture.ElixirTemplate); using var cache = Fixture.TemplateScope(template);
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); var item = f.AddElixir(live, canonical);
        var timer = Assert.Single(item.m_inactiveBehaviors.OfType<ClientElixirBehavior>());
        if (mismatch != "activation-pending") Assert.Single(ElixirRuntime.AddApprovedEffects(live, item, template, false, false));
        var effect = live.GameEffects.Snapshot().FirstOrDefault();
        switch (mismatch) {
            case "missing-effect": live.GameEffects.Clear(); break;
            case "orphan-origin": effect!.m_originatorID = Fixture.ElixirItem + 1; break;
            case "duplicate-slot-id":
                live.EquipmentBehavior.SlotList.Add(new() { ItemId = Fixture.ElixirItem, SlotType = EquipmentSlotType.Elixir });
                f.MatchSavedEquipment(live); break;
            case "duplicate-original-id":
                var duplicate = canonical.Item(Fixture.ElixirItem, Fixture.ElixirTemplate); duplicate.m_characterId = Fixture.Character;
                live.EquipmentBehavior.EquippedItems.Add(duplicate); break;
            case "duplicate-effect-origin":
                live.GameEffects.Add(new WizStatisticEffect { m_internalID = effect!.m_internalID + 1,
                    m_originatorID = effect.m_originatorID, m_itemSlotID = effect.m_itemSlotID, m_effectNameID = effect.m_effectNameID }); break;
            case "wrong-effect-name": effect!.m_effectNameID = StringHash.Compute("authored-unapproved-effect"); break;
            case "unsupported-native-applied": Assert.Single(template.m_behaviors.OfType<ElixirBehaviorTemplate>()).m_PvPEnabled = true; break;
            case "disabled-applied": live.IsInDuel = true; ElixirRules.SetCombatContext(live, true, true); break;
            case "missing-timer": item.m_inactiveBehaviors = []; break;
            case "duplicate-timer": item.m_inactiveBehaviors.Add(new ClientElixirBehavior {
                m_expireTime = timer.m_expireTime, m_statsApplied = timer.m_statsApplied }); break;
            case "wrong-owner": item.m_characterId = Fixture.Character + 1; break;
            case "expired-applied": timer.m_expireTime = 0; break;
        }
        var before = State.Of(live); var savedBefore = State.Of(f.Saved); var effects = live.GameEffects.Snapshot();
        f.Dependencies.Prepare = _ => throw new InvalidOperationException("inconsistent elixir context must not prepare packets");
        Assert.False(WizardProgressionTransactions.RuntimeContextMatches(live, f.Saved));
        Assert.False(WizardPotionTransactions.TryDrink(live, Fixture.Now, out var receipt)); Assert.Null(receipt);
        Assert.Equal(0, f.Saves); Assert.Equal(before, State.Of(live)); Assert.Equal(savedBefore, State.Of(f.Saved));
        Assert.Equal(effects, live.GameEffects.Snapshot()); Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData("activation")] [InlineData("expiry")] [InlineData("removal")]
    public void ElixirActivationExpiryAndRemovalTransitionsAreUsableAfterTheirEffectsFinish(string transition) {
        using var canonical = new ElixirTests.CanonicalFixture(); using var runtime = canonical.OctoberRuntime();
        var template = canonical.Template(Fixture.ElixirTemplate); using var cache = Fixture.TemplateScope(template);
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); var item = f.AddElixir(live, canonical);
        var timer = Assert.Single(item.m_inactiveBehaviors.OfType<ClientElixirBehavior>());
        if (transition != "activation") Assert.Single(ElixirRuntime.AddApprovedEffects(live, item, template, false, false));
        if (transition == "expiry") timer.m_expireTime = 0;
        if (transition == "removal") { Assert.True(live.EquipmentBehavior.RemoveElixirItem(Fixture.ElixirItem)); f.MatchSavedEquipment(live); }
        var before = State.Of(live); var savedBefore = State.Of(f.Saved);
        Assert.False(WizardProgressionTransactions.RuntimeContextMatches(live, f.Saved));
        Assert.False(WizardPotionTransactions.TryDrink(live, Fixture.Now, out _)); Assert.Equal(0, f.Saves);
        Assert.Equal(before, State.Of(live)); Assert.Equal(savedBefore, State.Of(f.Saved));
        if (transition == "activation") Assert.Single(ElixirRuntime.AddApprovedEffects(live, item, template, false, false));
        else Assert.Single(ElixirRuntime.RemoveItemEffects(live, Fixture.ElixirItem, template));
        Assert.True(WizardProgressionTransactions.RuntimeContextMatches(live, f.Saved));
        var completed = State.Of(live); f.OnSave = () => Assert.Equal(completed, State.Of(live));
        Assert.True(f.Change("gain", live)); Assert.Equal(1, f.Saves);
        Assert.Equal(350, live.MagicSchoolBehavior.ExperiencePoints); Assert.Equal(350, f.Saved.MagicSchoolBehavior.ExperiencePoints);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SameLevelXpCommitsDuringPendingElixirEffectsWithoutFreezingTheirOffsets(bool expired) {
        using var canonical = new ElixirTests.CanonicalFixture(); using var runtime = canonical.OctoberRuntime();
        var template = canonical.Template(Fixture.ElixirTemplate); using var cache = Fixture.TemplateScope(template);
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); var item = f.AddElixir(live, canonical);
        if (expired) {
            Assert.Single(ElixirRuntime.AddApprovedEffects(live, item, template, false, false));
            Assert.Single(item.m_inactiveBehaviors.OfType<ClientElixirBehavior>()).m_expireTime = 0;
        }
        Assert.False(WizardProgressionTransactions.RuntimeContextMatches(live, f.Saved));
        var before = State.Of(live); var effects = live.GameEffects.Snapshot();
        f.OnSave = () => { Assert.Equal(before, State.Of(live)); Assert.Equal(effects, live.GameEffects.Snapshot()); };
        Assert.True(WizardProgressionTransactions.TryGainExperience(live, 20, out var receipt));
        Assert.Equal(1, f.Saves); Assert.Equal(170, f.Saved.MagicSchoolBehavior.ExperiencePoints);
        Assert.Equal(before with { Xp = 170 }, State.Of(live)); Assert.False(receipt.Refill); Assert.False(receipt.AdjustStats);
        Assert.Empty(receipt.LevelMessages); Assert.Equal(20, receipt.XpMessage.XP); Assert.Equal(150, receipt.XpMessage.OldXP);
        Assert.Equal(effects, live.GameEffects.Snapshot());
        Assert.False(WizardProgressionTransactions.TryGainExperience(live, 250, out _));
        Assert.Equal(1, f.Saves); Assert.Equal(170, f.Saved.MagicSchoolBehavior.ExperiencePoints);
    }

    [Theory]
    [InlineData("gain", 20, 350, false)] [InlineData("gain", -20, 330, true)] [InlineData("remove", 20, 330, false)]
    public void AttachedCapNormalizationUsesClampedSavedXpInOneSaveWithoutARefill(
        string operation, int amount, int expectedXp, bool xpPacket) {
        var f = new Fixture(999, 99); using var scope = f.Scope(); var live = f.Live(4, 9); var before = State.Of(live);
        f.OnSave = () => Assert.Equal(before, State.Of(live));
        ProgressionReceipt receipt;
        var accepted = operation == "gain" ? WizardProgressionTransactions.TryGainExperience(live, amount, out receipt)
            : WizardProgressionTransactions.TryRemoveExperience(live, amount, out receipt);
        Assert.True(accepted); Assert.Equal(1, f.Saves); Assert.Equal(4, f.Saved.MagicSchoolBehavior.Level);
        Assert.Equal(expectedXp, f.Saved.MagicSchoolBehavior.ExperiencePoints); Assert.Equal(350, receipt.OldXp);
        Assert.Equal(before with { Xp = expectedXp }, State.Of(live)); Assert.False(receipt.Refill); Assert.False(receipt.AdjustStats);
        Assert.Empty(receipt.LevelMessages); Assert.Equal(xpPacket, receipt.XpMessage is not null);
        if (xpPacket) { Assert.Equal(350, receipt.XpMessage.OldXP); Assert.Equal(-20, receipt.XpMessage.XP); }
        Assert.Equal(7, f.Saved.PetOwnerBehavior.Energy); Assert.Equal(1234u, f.Saved.PetOwnerBehavior.LastEnergyTickEpoch);
    }

    [Theory]
    [InlineData("drink", 77, .25f)] [InlineData("minigame", 58, 1.25f)]
    public void PotionHealingAndMinigameFillPersistAttachedCapNormalizationAfterAck(string operation, int mana, float charge) {
        var f = new Fixture(999, 99); using var scope = f.Scope(); var live = f.Live(4, 9); var before = State.Of(live);
        f.OnSave = () => Assert.Equal(before, State.Of(live));
        Assert.True(f.Change(operation, live)); Assert.Equal(1, f.Saves); Assert.Equal(4, f.Saved.MagicSchoolBehavior.Level);
        Assert.Equal(350, f.Saved.MagicSchoolBehavior.ExperiencePoints); Assert.Equal(350, live.MagicSchoolBehavior.ExperiencePoints);
        Assert.Equal(mana, live.GameStats.m_currentMana); Assert.Equal(mana, f.Saved.GameStats.m_currentMana);
        Assert.Equal(charge, f.Saved.GameStats.m_potionCharge); Assert.Equal(operation == "drink" ? charge : 9f, live.GameStats.m_potionCharge);
        Assert.Equal(operation == "drink" ? 191 : 31, live.GameStats.m_currentHitpoints);
        Assert.Equal(99, live.PetOwnerBehavior.Energy); Assert.Equal(9999u, live.PetOwnerBehavior.LastEnergyTickEpoch);
    }

    [Fact]
    public void PotionShopPricesTheAttachedSavedLevelWithoutPersistingUnrelatedSchoolFields() {
        var f = new Fixture(999, 99); using var scope = f.Scope(); var live = f.Live(4, 9); var priced = 0;
        Assert.True(WizardPotionTransactions.TryBuy(live, true, out var receipt, level => { priced = level; return level * 3; }));
        Assert.Equal(4, priced); Assert.Equal(24, receipt.Cost); Assert.Equal(63, f.Saved.GameStats.m_currentGold);
        Assert.Equal(99, f.Saved.MagicSchoolBehavior.Level); Assert.Equal(999, f.Saved.MagicSchoolBehavior.ExperiencePoints);
        Assert.Equal(4, live.MagicSchoolBehavior.Level); Assert.Equal(9, live.MagicSchoolBehavior.ExperiencePoints);
    }

    [Theory]
    [InlineData("gain", false)] [InlineData("gain", true)] [InlineData("drink", false)] [InlineData("drink", true)]
    [InlineData("minigame", false)] [InlineData("minigame", true)]
    public void LostAckDuringAttachedNormalizationQuarantinesWithoutPublishingEvenWhenTheClampIsDurable(
        string operation, bool durable) {
        var f = new Fixture(999, 99) { Fail = true, Durable = durable }; using var scope = f.Scope(); var live = f.Live(4, 9);
        var before = State.Of(live);
        f.OnSave = () => Assert.Equal(before, State.Of(live));
        f.OnDispose = () => { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); };
        Assert.Throws<InvalidOperationException>(() => {
            if (operation == "gain") WizardProgressionTransactions.TryGainExperience(live, 20, out _);
            else f.Change(operation, live);
        });
        Assert.Equal(before, State.Of(live)); Assert.Equal(durable ? 4 : 99, f.Saved.MagicSchoolBehavior.Level);
        Assert.Equal(durable ? 350 : 999, f.Saved.MagicSchoolBehavior.ExperiencePoints); Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData("gain")] [InlineData("drink")] [InlineData("minigame")]
    public void ProvenExpiredCalendarRentalFiltersOnlySavedEquipmentIdsAndProtectsTrackedOriginals(string operation) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); f.Rental(live, "expired");
        var pet = new WizClientObjectItem { m_globalID = 44, m_characterId = Fixture.Character, m_templateID = 9343,
            m_inactiveBehaviors = [new ClientPetItemBehavior { m_XP = 7 }] }; f.Rows.Add(pet);
        f.OnReadRows = rows => Assert.Single(rows.Single(row => row.m_globalID == 44).m_inactiveBehaviors.OfType<ClientPetItemBehavior>()).m_XP = 999;
        var slots = live.EquipmentBehavior.SlotList; var before = State.Of(live);
        f.OnSave = () => {
            Assert.Equal(before, State.Of(live)); Assert.Equal(2, f.Working!.Ignored.Count);
            Assert.All(f.Working.Rows, row => Assert.Contains(row, f.Working.Ignored));
        };
        Assert.True(f.Change(operation, live)); Assert.Equal(1, f.Saves);
        Assert.Equal(new ulong[] { 42 }, f.Saved.EquipmentBehavior.EquippedItemIds);
        Assert.Same(slots, live.EquipmentBehavior.SlotList); Assert.Equal(43ul, (ulong)slots.Single(slot => slot.SlotType == EquipmentSlotType.Robe).ItemId);
        Assert.Equal(43ul, (ulong)f.Saved.EquipmentBehavior.SlotList.Single(slot => slot.SlotType == EquipmentSlotType.Robe).ItemId);
        Assert.Equal(7u, Assert.Single(f.Rows.Single(row => row.m_globalID == 44).m_inactiveBehaviors.OfType<ClientPetItemBehavior>()).m_XP);
        Assert.Equal((uint)new DateTimeOffset(Fixture.Now).ToUnixTimeSeconds() - 1,
            Assert.Single(f.Rows.Single(row => row.m_globalID == 43).m_inactiveBehaviors.OfType<ClientTimedItemBehavior>()).m_expireTime);
    }

    [Theory]
    [InlineData("nonexpired")] [InlineData("game-timer")] [InlineData("missing")]
    [InlineData("foreign")] [InlineData("duplicate")] [InlineData("zero-expiry")] [InlineData("unknown-timer")]
    public void UnprovenSavedOnlyEquipmentCannotBeNormalizedAway(string rental) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); f.Rental(live, rental);
        var before = State.Of(live); var savedBefore = State.Of(f.Saved);
        Assert.False(f.Change("gain", live)); Assert.Equal(0, f.Saves); Assert.Equal(before, State.Of(live));
        Assert.Equal(savedBefore, State.Of(f.Saved)); Assert.Equal(new ulong[] { 42, 43 }, f.Saved.EquipmentBehavior.EquippedItemIds);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData("gain")] [InlineData("level")] [InlineData("remove")] [InlineData("drink")]
    [InlineData("buy")] [InlineData("slot")] [InlineData("minigame")] [InlineData("set")]
    public void MissingFreshCharacterIsAProvenRefusalWithoutSavingOrPublishing(string operation) {
        var f = new Fixture { Missing = true }; using var scope = f.Scope(); var live = f.Live(); var before = State.Of(live);
        Assert.False(f.Change(operation, live)); Assert.Equal(0, f.Saves); Assert.Equal(before, State.Of(live));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
        f.Missing = false; Assert.True(f.Change(operation, live)); Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData("gain")] [InlineData("level")] [InlineData("remove")] [InlineData("drink")]
    [InlineData("buy")] [InlineData("slot")] [InlineData("minigame")] [InlineData("set")]
    public void UncertaintyRaisedDuringLoadIsRecheckedInsideTheLaneBeforeStaging(string operation) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); var before = State.Of(live);
        f.OnLoad = () => { Assert.True(WizardCollection.HoldsWriteLane); WizardCollection.MarkInventorySnapshotUncertain(live); };
        Assert.False(f.Change(operation, live)); Assert.Equal(0, f.Saves); Assert.Equal(1, f.Opened);
        Assert.Equal(before, State.Of(live)); Assert.Equal(State.Of(f.Saved), State.Of(f.Working!.Wizard));
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData(true, true)] [InlineData(true, false)] [InlineData(false, true)]
    public void DrinkHealsSelectedRuntimeGlobesAndConsumesTheFreshChargeInOneAcknowledgedSave(bool health, bool mana) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        if (!health) live.GameStats.m_currentHitpoints = live.GameStats.m_baseHitpoints;
        if (!mana) live.GameStats.m_currentMana = live.GameStats.m_baseMana;
        var before = State.Of(live); var stats = live.GameStats; var owner = live.PetOwnerBehavior;
        var packets = new List<IMessage>();
        f.OnSave = () => {
            Assert.Equal(before, State.Of(live)); Assert.Empty(packets);
            Assert.Equal(.25f, f.Working!.Wizard.GameStats.m_potionCharge);
            Assert.Equal(health ? 151 : 23, f.Working.Wizard.GameStats.m_currentHitpoints);
            Assert.Equal(mana ? 57 : 17, f.Working.Wizard.GameStats.m_currentMana);
        };
        Assert.True(PotionService.ApplyDrink(live, Fixture.Now, packets.Add, () => Assert.Fail("acknowledged drink must not close")));
        Assert.Equal(1, f.Saves); Assert.Same(stats, live.GameStats); Assert.Same(owner, live.PetOwnerBehavior);
        Assert.Equal(.25f, stats.m_potionCharge); Assert.Equal(3f, stats.m_potionMax);
        Assert.Equal(151, stats.m_currentHitpoints); Assert.Equal(57, stats.m_currentMana);
        Assert.Equal(999, stats.m_currentGold); Assert.Equal(99, owner.Energy); Assert.Equal(9999u, owner.LastEnergyTickEpoch);
        Assert.Equal(7, f.Saved.PetOwnerBehavior.Energy); Assert.Equal(1234u, f.Saved.PetOwnerBehavior.LastEnergyTickEpoch);
        var index = 0; Assert.IsType<WIZARD_12_PROTOCOL.MSG_USEPOTION>(packets[index++]);
        if (health) {
            var update = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH>(packets[index++]);
            Assert.Equal(live.GameObjectID, update.CharacterID); Assert.Equal(151, update.NewHealth); Assert.Equal(140, update.NewHealthMax);
        }
        if (mana) {
            var update = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEMANA>(packets[index++]);
            Assert.Equal(57, update.Mana); Assert.Equal(50, update.MaxMana);
        }
        var charge = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEPOTIONS>(packets[index++]);
        Assert.Equal(.25f, charge.PotionCharge); Assert.Equal(3f, charge.PotionMax); Assert.Equal(index, packets.Count);
    }

    [Theory]
    [InlineData(false, 81, 2.25f)]
    [InlineData(true, 75, 3f)]
    public void BuyPaysAndFillsTogetherUsingSavedGoldChargeMaximumAndPricingLevel(bool all, int gold, float charge) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        live.GameStats.m_currentGold = 1; live.MagicSchoolBehavior.Level = 99; live.GameStats.Level = 99;
        var before = State.Of(live); var packets = new List<IMessage>(); var pricedLevel = 0;
        f.OnSave = () => {
            Assert.Equal(before, State.Of(live)); Assert.Empty(packets);
            Assert.Equal(gold, f.Working!.Wizard.GameStats.m_currentGold);
            Assert.Equal(charge, f.Working.Wizard.GameStats.m_potionCharge);
            Assert.Equal(3f, f.Working.Wizard.GameStats.m_potionMax);
        };
        Assert.True(PotionService.ApplyBuy(live, all, packets.Add, () => Assert.Fail("acknowledged buy must not close"),
            level => { pricedLevel = level; return level * 3; }));
        Assert.Equal(2, pricedLevel); Assert.Equal(1, f.Saves); Assert.Equal(gold, live.GameStats.m_currentGold);
        Assert.Equal(charge, live.GameStats.m_potionCharge); Assert.Equal(3f, live.GameStats.m_potionMax);
        Assert.Equal(31, live.GameStats.m_currentHitpoints); Assert.Equal(19, live.GameStats.m_currentMana);
        var wallet = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>(packets[0]);
        Assert.Equal(gold, wallet.Gold); Assert.Equal(500, wallet.MaxGold);
        Assert.Equal(charge, Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEPOTIONS>(packets[1]).PotionCharge);
        Assert.Equal(0, Assert.IsType<WIZARD_12_PROTOCOL.MSG_POTIONBUYCONFIRM>(packets[2]).Failure); Assert.Equal(3, packets.Count);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnaffordableOrNegativePricedBuyPublishesOnlyFailureAndCannotChargeOrFill(bool negativePrice) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        if (!negativePrice) f.Saved.GameStats.m_currentGold = 5;
        var before = State.Of(live); var savedBefore = State.Of(f.Saved); var packets = new List<IMessage>();
        Assert.False(PotionService.ApplyBuy(live, true, packets.Add, () => Assert.Fail("refused buy must not close"),
            _ => negativePrice ? -1 : 6));
        Assert.Equal(1, Assert.IsType<WIZARD_12_PROTOCOL.MSG_POTIONBUYCONFIRM>(Assert.Single(packets)).Failure);
        Assert.Equal(0, f.Saves); Assert.Equal(before, State.Of(live)); Assert.Equal(savedBefore, State.Of(f.Saved));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData("empty-drink")] [InlineData("full-drink")] [InlineData("full-buy")] [InlineData("full-minigame")]
    public void ProvenNoOpReturnsOnlyItsExistingAcknowledgementWithoutSaveOrPublication(string operation) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); var packets = new List<IMessage>();
        if (operation == "empty-drink") f.Saved.GameStats.m_potionCharge = .5f;
        if (operation == "full-drink") {
            live.GameStats.m_currentHitpoints = live.GameStats.m_baseHitpoints;
            live.GameStats.m_currentMana = live.GameStats.m_baseMana;
        }
        if (operation is "full-buy" or "full-minigame") f.Saved.GameStats.m_potionCharge = f.Saved.GameStats.m_potionMax;
        if (operation == "full-minigame") live.GameStats.m_currentMana = live.GameStats.m_baseMana;
        var before = State.Of(live); var savedBefore = State.Of(f.Saved);
        var accepted = operation switch {
            "full-buy" => PotionService.ApplyBuy(live, true, packets.Add, () => Assert.Fail("no-op must not close"), _ => 6),
            "full-minigame" => WizardPotionTransactions.TryMinigameFill(live, Fixture.Potions(), out _),
            _ => PotionService.ApplyDrink(live, Fixture.Now, packets.Add, () => Assert.Fail("no-op must not close")),
        };
        Assert.True(accepted); Assert.Equal(0, f.Saves); Assert.Equal(before, State.Of(live)); Assert.Equal(savedBefore, State.Of(f.Saved));
        if (operation == "full-buy") Assert.Equal(0, Assert.IsType<WIZARD_12_PROTOCOL.MSG_POTIONBUYCONFIRM>(Assert.Single(packets)).Failure);
        else if (operation == "full-minigame") Assert.Empty(packets);
        else Assert.IsType<WIZARD_12_PROTOCOL.MSG_USEPOTION>(Assert.Single(packets));
    }

    [Fact]
    public void DrinkingDuringADuelIsRefusedBeforePreparationOrSave() {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); live.IsInDuel = true;
        var before = State.Of(live); var packets = new List<IMessage>();
        f.Dependencies.Prepare = _ => throw new InvalidOperationException("duel refusal must not prepare a packet");
        Assert.False(PotionService.ApplyDrink(live, Fixture.Now, packets.Add, () => Assert.Fail("duel refusal must not close")));
        Assert.Empty(packets); Assert.Equal(0, f.Saves); Assert.Equal(before, State.Of(live));
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live));
    }

    [Theory]
    [InlineData(90, 1.25f, 100, 1.65f, true, true)]
    [InlineData(100, 2.8f, 100, 3f, false, true)]
    [InlineData(50, 1.25f, 100, 1.25f, true, false)]
    [InlineData(100, 3f, 100, 3f, false, false)]
    public void MinigameFillsRuntimeManaThenFreshFlasksUpToTheirSavedCapInOneSave(
        int mana, float oldCharge, int expectedMana, float expectedCharge, bool manaChanged, bool potionsChanged) {
        var f = new Fixture(); f.Saved.GameStats.m_potionCharge = oldCharge;
        using var scope = f.Scope(); var live = f.Live(); live.GameStats.m_baseMana = 100; live.GameStats.m_currentMana = mana;
        var before = State.Of(live); var stats = live.GameStats; f.OnSave = () => Assert.Equal(before, State.Of(live));
        Assert.True(WizardPotionTransactions.TryMinigameFill(live, Fixture.Potions(), out var receipt));
        Assert.Equal(manaChanged || potionsChanged ? 1 : 0, f.Saves); Assert.Same(stats, live.GameStats);
        Assert.Equal(expectedMana, stats.m_currentMana);
        Assert.Equal(potionsChanged ? expectedCharge : 9f, stats.m_potionCharge);
        Assert.Equal(expectedCharge, f.Saved.GameStats.m_potionCharge); Assert.Equal(3f, f.Saved.GameStats.m_potionMax);
        Assert.Equal(50, receipt.ManaReward); Assert.Equal(manaChanged, receipt.ManaChanged); Assert.Equal(potionsChanged, receipt.PotionsChanged);
        var index = 0;
        if (manaChanged) { var update = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEMANA>(receipt.Messages[index++]); Assert.Equal(expectedMana, update.Mana); Assert.Equal(50, update.MaxMana); }
        if (potionsChanged) { var update = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEPOTIONS>(receipt.Messages[index++]); Assert.Equal(expectedCharge, update.PotionCharge); Assert.Equal(3f, update.PotionMax); }
        Assert.Equal(index, receipt.Messages.Count); Assert.Equal(99, live.PetOwnerBehavior.Energy);
    }

    [Fact]
    public void AddSlotAndExplicitSetPublishOnlyTheirSelectedPotionScalars() {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); var stats = live.GameStats;
        Assert.True(WizardPotionTransactions.TryAddSlotAndFill(live, out var added));
        Assert.Equal(1, f.Saves); Assert.Equal(4f, stats.m_potionMax); Assert.Equal(4f, stats.m_potionCharge);
        Assert.Equal(4f, Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEPOTIONS>(Assert.Single(added.Messages)).PotionMax);
        Assert.True(WizardPotionTransactions.TrySetPotions(live, 2.5f, 5f, out var set));
        Assert.Equal(2, f.Saves); Assert.Same(stats, live.GameStats); Assert.Equal(2.5f, stats.m_potionCharge); Assert.Equal(5f, stats.m_potionMax);
        Assert.Equal(2.5f, Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEPOTIONS>(Assert.Single(set.Messages)).PotionCharge);
        Assert.Equal(999, stats.m_currentGold); Assert.Equal(31, stats.m_currentHitpoints); Assert.Equal(19, stats.m_currentMana);
        Assert.Equal(99, live.PetOwnerBehavior.Energy); Assert.Equal(1234u, f.Saved.PetOwnerBehavior.LastEnergyTickEpoch);
    }

    [Fact]
    public void OrdinaryStatsSavePreservesSavedPotionBalancesAndWalletsThenRefreshesThoseLiveScalarsAfterAck() {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); var stats = live.GameStats;
        var before = State.Of(live);
        f.OnSave = () => {
            Assert.Equal(before, State.Of(live)); var saved = f.Working!.Wizard.GameStats;
            Assert.Equal(87, saved.m_currentGold); Assert.Equal(19, saved.m_currentArenaPoints); Assert.Equal(23, saved.m_currentPvPCurrency);
            Assert.Equal(1.25f, saved.m_potionCharge); Assert.Equal(3f, saved.m_potionMax);
            Assert.Equal(31, saved.m_currentHitpoints); Assert.Equal(19, saved.m_currentMana);
        };
        Assert.True(WizardCollection.UpdateCharacterGameStats(live, f.Open, f.Load));
        Assert.Equal(1, f.Saves); Assert.Same(stats, live.GameStats); Assert.Equal(87, stats.m_currentGold);
        Assert.Equal(19, stats.m_currentArenaPoints); Assert.Equal(23, stats.m_currentPvPCurrency);
        Assert.Equal(1.25f, stats.m_potionCharge); Assert.Equal(3f, stats.m_potionMax);
        Assert.Equal(31, stats.m_currentHitpoints); Assert.Equal(19, stats.m_currentMana);
        Assert.Equal(150, f.Saved.MagicSchoolBehavior.ExperiencePoints); Assert.Equal(7, f.Saved.PetOwnerBehavior.Energy);
    }

    // CLASSIC: cross the real progression/claim ACK boundary with the existing derived mana ledger.
    public static IEnumerable<object[]> ManaProgressionCases() {
        foreach (var route in new[] { "gain", "level", "quest" })
            foreach (var reduction in new[] { "full", "half", "stacked" })
                foreach (var flatFirst in new[] { true, false }) yield return [route, reduction, flatFirst];
    }

    [Theory]
    [MemberData(nameof(ManaProgressionCases))]
    public void ManaLedgerMultilevelProgressionPublishesOnlyAfterAckAndRestoresNewUnreducedMaximum(
        string route, string reduction, bool flatFirst) {
        if (route == "quest") {
            using var quest = new TerminalClaimFixture(); var live = quest.Live;
            var pieces = AddManaPieces(live.GameStats, reduction, flatFirst); var before = State.Of(live);
            var ledger = ManaLedger.Of(live.GameStats); var native = new List<IMessage>();
            quest.Reward.ExperienceAmount = 250;
            quest.OnSave = () => { Assert.Equal(before, State.Of(live)); AssertManaLedger(ledger, live.GameStats); Assert.Empty(native); };
            quest.AfterCommit = claim => native.AddRange(claim.GoalActions.Concat(claim.EndActions).Select(action => action.Message).OfType<IMessage>());
            Assert.Equal(QuestClaimStatus.Committed, quest.Claim(out _));
            Assert.Equal(1, quest.Opened); Assert.Equal(1, quest.Saves);
            Assert.Equal(ManaMaximum(77, reduction), quest.Saved.GameStats.m_baseMana);
            Assert.Equal(quest.Saved.GameStats.m_baseMana, quest.Saved.GameStats.m_currentMana);
            AssertManaProgressed(live, native, reduction, pieces);
            return;
        }
        var f = new Fixture(); using var scope = f.Scope(); var wizard = f.Live();
        var effects = AddManaPieces(wizard.GameStats, reduction, flatFirst); var original = State.Of(wizard);
        var originalLedger = ManaLedger.Of(wizard.GameStats); var sent = new List<IMessage>();
        f.OnSave = () => { Assert.Equal(original, State.Of(wizard)); AssertManaLedger(originalLedger, wizard.GameStats); Assert.Empty(sent); };
        Assert.True(route == "gain"
            ? WizardService.ApplyExperience(wizard, 250, sent.Add, sent.Add, () => Assert.Fail("ACK must not close"))
            : WizardService.ApplyLevel(wizard, 4, sent.Add, sent.Add, () => Assert.Fail("ACK must not close")));
        Assert.Equal(1, f.Opened); Assert.Equal(1, f.Saves);
        Assert.Equal(ManaMaximum(77, reduction), f.Saved.GameStats.m_baseMana);
        Assert.Equal(f.Saved.GameStats.m_baseMana, f.Saved.GameStats.m_currentMana);
        AssertManaProgressed(wizard, sent, reduction, effects, route == "level" ? 300 : 350);
    }

    public static IEnumerable<object[]> ManaNoRefillCases() {
        foreach (var route in new[] { "gain", "remove" })
            foreach (var reduction in new[] { "full", "half", "stacked" }) yield return [route, reduction];
    }
    [Theory]
    [MemberData(nameof(ManaNoRefillCases))]
    public void ManaLedgerNoRefillKeepsCurrentEvenWhenMaximumIsLowered(string route, string reduction) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live();
        var pieces = AddManaPieces(live.GameStats, reduction); live.GameStats.m_currentMana = 91;
        var current = live.GameStats.m_currentMana; var ledger = ManaLedger.Of(live.GameStats);
        f.OnSave = () => { Assert.Equal(current, live.GameStats.m_currentMana); AssertManaLedger(ledger, live.GameStats); };
        ProgressionReceipt receipt;
        Assert.True(route == "gain" ? WizardProgressionTransactions.TryGainExperience(live, 250, out receipt, refill: false)
            : WizardProgressionTransactions.TryRemoveExperience(live, 80, out receipt));
        var restored = route == "gain" ? 77 : 47;
        Assert.Equal(ManaMaximum(restored, reduction), live.GameStats.m_baseMana);
        Assert.Equal(live.GameStats.m_baseMana, f.Saved.GameStats.m_baseMana);
        Assert.Equal(current, live.GameStats.m_currentMana); Assert.Equal(17, f.Saved.GameStats.m_currentMana);
        Assert.False(receipt.Refill); Assert.Empty(receipt.LevelMessages); Assert.Equal(1, f.Opened);
        RemoveManaPieces(live.GameStats, pieces); Assert.Equal(restored, live.GameStats.m_baseMana);
    }

    [Theory]
    [InlineData("full")] [InlineData("half")] [InlineData("stacked")]
    public void ManaLedgerCappedNoOpDoesNotSaveOrChurn(string reduction) {
        var f = new Fixture(350, 4); using var scope = f.Scope(); var live = f.Live();
        var pieces = AddManaPieces(live.GameStats, reduction, baseMaximum: 70); var before = State.Of(live);
        var ledger = ManaLedger.Of(live.GameStats);
        Assert.True(WizardProgressionTransactions.TryGainExperience(live, 100, out var receipt));
        Assert.False(receipt.ShouldSave); Assert.Equal(0, f.Saves); Assert.Equal(before, State.Of(live));
        AssertManaLedger(ledger, live.GameStats); Assert.Equal(1, f.Opened);
        RemoveManaPieces(live.GameStats, pieces); Assert.Equal(77, live.GameStats.m_baseMana);
    }

    public static IEnumerable<object[]> ManaAckFailureCases() {
        foreach (var route in new[] { "gain", "quest" })
            foreach (var reduction in new[] { "full", "half", "stacked" })
                foreach (var durable in new[] { false, true }) yield return [route, reduction, durable];
    }
    [Theory]
    [MemberData(nameof(ManaAckFailureCases))]
    public void ManaLedgerFailedOrLostAckKeepsOriginalLiveMaximumAndLedger(string route, string reduction, bool durable) {
        if (route == "quest") {
            using var quest = new TerminalClaimFixture { FailSave = true, Durable = durable };
            var pieces = AddManaPieces(quest.Live.GameStats, reduction); var before = State.Of(quest.Live);
            var ledger = ManaLedger.Of(quest.Live.GameStats); var publications = 0;
            quest.Reward.ExperienceAmount = 250; quest.AfterCommit = _ => publications++;
            quest.OnSave = () => { Assert.Equal(before, State.Of(quest.Live)); AssertManaLedger(ledger, quest.Live.GameStats); };
            Assert.Throws<InvalidOperationException>(() => quest.Claim(out _));
            Assert.Equal(before, State.Of(quest.Live)); AssertManaLedger(ledger, quest.Live.GameStats);
            Assert.True(WizardCollection.IsInventorySnapshotUncertain(quest.Live)); Assert.Equal(0, publications);
            Assert.Equal(durable ? ManaMaximum(77, reduction) : 50, quest.Saved.GameStats.m_baseMana);
            Assert.Equal(1, quest.Opened); Assert.Equal(1, quest.Saves);
            RemoveManaPieces(quest.Live.GameStats, pieces); Assert.Equal(57, quest.Live.GameStats.m_baseMana);
            return;
        }
        var f = new Fixture { Fail = true, Durable = durable }; using var scope = f.Scope(); var live = f.Live();
        var effects = AddManaPieces(live.GameStats, reduction); var original = State.Of(live); var savedLedger = ManaLedger.Of(live.GameStats);
        var native = new List<IMessage>(); var closed = 0;
        f.OnSave = () => { Assert.Equal(original, State.Of(live)); AssertManaLedger(savedLedger, live.GameStats); };
        Assert.Throws<InvalidOperationException>(() => WizardService.ApplyExperience(live, 250, native.Add, native.Add, () => closed++));
        Assert.Equal(1, closed); Assert.Empty(native); Assert.Equal(original, State.Of(live)); AssertManaLedger(savedLedger, live.GameStats);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); Assert.Equal(1, f.Opened); Assert.Equal(1, f.Saves);
        Assert.Equal(durable ? ManaMaximum(77, reduction) : 50, f.Saved.GameStats.m_baseMana);
        RemoveManaPieces(live.GameStats, effects); Assert.Equal(57, live.GameStats.m_baseMana);
    }

    public static IEnumerable<object[]> ManaRefusalCases() {
        foreach (var route in new[] { "gain", "quest" })
            foreach (var reduction in new[] { "full", "half", "stacked" }) yield return [route, reduction];
    }
    [Theory]
    [MemberData(nameof(ManaRefusalCases))]
    public void ManaLedgerNativePreparationRefusalKeepsUnchangedDerivedState(string route, string reduction) {
        if (route == "quest") {
            using var quest = new TerminalClaimFixture(); var pieces = AddManaPieces(quest.Live.GameStats, reduction);
            var before = State.Of(quest.Live); var ledger = ManaLedger.Of(quest.Live.GameStats);
            quest.Reward.ExperienceAmount = 250; quest.Dependencies.Prepare = _ => false;
            Assert.Equal(QuestClaimStatus.Refused, quest.Claim(out _)); Assert.Equal(0, quest.Saves); Assert.Equal(1, quest.Opened);
            Assert.Equal(before, State.Of(quest.Live)); AssertManaLedger(ledger, quest.Live.GameStats);
            Assert.False(WizardCollection.IsInventorySnapshotUncertain(quest.Live)); RemoveManaPieces(quest.Live.GameStats, pieces);
            Assert.Equal(57, quest.Live.GameStats.m_baseMana); return;
        }
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); var effects = AddManaPieces(live.GameStats, reduction);
        var original = State.Of(live); var old = ManaLedger.Of(live.GameStats); f.Dependencies.Prepare = _ => false;
        Assert.False(WizardProgressionTransactions.TryGainExperience(live, 250, out var receipt)); Assert.Null(receipt);
        Assert.Equal(0, f.Saves); Assert.Equal(1, f.Opened); Assert.Equal(original, State.Of(live)); AssertManaLedger(old, live.GameStats);
        Assert.False(WizardCollection.IsInventorySnapshotUncertain(live)); RemoveManaPieces(live.GameStats, effects);
        Assert.Equal(57, live.GameStats.m_baseMana);
    }

    [Theory]
    [InlineData("full")] [InlineData("half")] [InlineData("stacked")]
    public void ManaLedgerZeroDeltaLevelRefillPreservesLedgerIdentity(string reduction) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); var effects = AddManaPieces(live.GameStats, reduction);
        var ledger = ManaLedger.Of(live.GameStats);
        Assert.True(WizardProgressionTransactions.TrySetLevel(live, 2, resetMismatchedXp: false, out _));
        AssertManaLedger(ledger, live.GameStats); Assert.Equal(ManaMaximum(57, reduction), live.GameStats.m_currentMana);
        Assert.Equal(1, f.Opened); RemoveManaPieces(live.GameStats, effects); Assert.Equal(57, live.GameStats.m_baseMana);
    }

    [Theory]
    [InlineData("gain", "reset")] [InlineData("gain", "change")]
    [InlineData("quest", "reset")] [InlineData("quest", "change")]
    public void ManaLedgerReentrantReplacementCannotPublishStaleProgressionOrQuestFields(string route, string mutation) {
        var publications = 0;
        static void Mutate(ServerWizGameStats stats, string kind) {
            if (kind == "change") CharacterEffectHelper.AddStatisticEffectToStats(stats, "CanonicalMaxMana", new WizStatisticEffect { m_manaBonus = 5 });
            else {
                CharacterEffectHelper.ResetRebuiltEquipmentEffects(stats); stats.m_baseMana = 100;
                CharacterEffectHelper.AddStatisticEffectToStats(stats, "CanonicalMaxManaPercentReduce", new WizStatisticEffect { m_manaBonus = .5f, m_itemSlotID = 909 });
            }
        }
        if (route == "quest") {
            using var quest = new TerminalClaimFixture(); AddManaPieces(quest.Live.GameStats, "full");
            quest.Reward.ExperienceAmount = 250; quest.Reward.GoldAmount = 30;
            var journal = quest.Live.QuestBehavior; var ids = journal.CurrentQuestIDs.ToArray(); var entries = journal.CurrentQuestInstances.ToArray();
            var registry = journal.Registry.OrderBy(row => row.Key).ToArray(); var training = quest.Live.MagicSchoolBehavior.TrainingPoints;
            var learned = quest.Live.SpellbookBehavior.LearnedSpellTemplateIds.ToArray(); State? afterHook = null; ManaLedger? ledger = null;
            quest.Dependencies.BeforePublish = wizard => { Mutate(wizard.GameStats, mutation); afterHook = State.Of(wizard); ledger = ManaLedger.Of(wizard.GameStats); };
            quest.AfterCommit = _ => publications++;
            Assert.Throws<InvalidOperationException>(() => quest.Claim(out _));
            Assert.Equal(afterHook, State.Of(quest.Live)); AssertManaLedger(ledger!, quest.Live.GameStats);
            Assert.Same(journal, quest.Live.QuestBehavior); Assert.Equal(ids, journal.CurrentQuestIDs); Assert.Equal(entries, journal.CurrentQuestInstances);
            Assert.Equal(registry, journal.Registry.OrderBy(row => row.Key)); Assert.Equal(training, quest.Live.MagicSchoolBehavior.TrainingPoints);
            Assert.Equal(learned, quest.Live.SpellbookBehavior.LearnedSpellTemplateIds); Assert.True(quest.Expected.IsGoalActive(TerminalClaimFixture.GoalName));
            Assert.Equal(0, publications); Assert.True(WizardCollection.IsInventorySnapshotUncertain(quest.Live));
            Assert.Equal(4, quest.Saved.MagicSchoolBehavior.Level); Assert.Equal(130, quest.Saved.GameStats.m_currentGold);
            Assert.Equal(1, quest.Opened); Assert.Equal(1, quest.Saves); return;
        }
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); AddManaPieces(live.GameStats, "full");
        State? expected = null; ManaLedger? latest = null;
        f.OnSave = () => { Mutate(live.GameStats, mutation); expected = State.Of(live); latest = ManaLedger.Of(live.GameStats); };
        Assert.Throws<InvalidOperationException>(() => WizardService.ApplyExperience(live, 250, _ => publications++, _ => publications++, () => { }));
        Assert.Equal(expected, State.Of(live)); AssertManaLedger(latest!, live.GameStats); Assert.Equal(0, publications);
        Assert.True(WizardCollection.IsInventorySnapshotUncertain(live)); Assert.Equal(4, f.Saved.MagicSchoolBehavior.Level);
        Assert.Equal(1, f.Opened); Assert.Equal(1, f.Saves);
    }

    [Theory]
    [InlineData("full")] [InlineData("half")] [InlineData("stacked")]
    public void ManaLedgerSuccessiveLevelGainsAccumulateBeforeFinalReductionRemoval(string reduction) {
        var f = new Fixture(); using var scope = f.Scope(); var live = f.Live(); var effects = AddManaPieces(live.GameStats, reduction);
        Assert.True(WizardProgressionTransactions.TryGainExperience(live, 50, out _));
        Assert.Equal(ManaMaximum(67, reduction), live.GameStats.m_baseMana);
        Assert.True(WizardProgressionTransactions.TryGainExperience(live, 100, out _));
        Assert.Equal(ManaMaximum(77, reduction), live.GameStats.m_baseMana);
        Assert.Equal(2, f.Opened); Assert.Equal(2, f.Saves);
        RemoveManaPieces(live.GameStats, effects.Reverse().ToArray()); Assert.Equal(77, live.GameStats.m_baseMana);
        CharacterEffectHelper.RemoveStatisticEffectFromStats(live.GameStats, "CanonicalMaxMana", new WizStatisticEffect { m_manaBonus = 7 });
        Assert.Equal(70, live.GameStats.m_baseMana);
    }

    private static int ManaMaximum(int unreduced, string mode) => mode == "half" ? (int)Math.Floor(unreduced * .5) : 0;
    private static WizStatisticEffect[] AddManaPieces(ServerWizGameStats stats, string mode, bool flatFirst = true, int baseMaximum = 50) {
        stats.m_baseMana = baseMaximum;
        var flat = new WizStatisticEffect { m_manaBonus = 7 };
        var pieces = mode == "stacked" ? new[] { new WizStatisticEffect { m_itemSlotID = 901, m_manaBonus = .5f },
            new WizStatisticEffect { m_itemSlotID = 902, m_manaBonus = .5f } }
            : new[] { new WizStatisticEffect { m_itemSlotID = 901, m_manaBonus = mode == "half" ? .5f : 1f } };
        if (flatFirst) CharacterEffectHelper.AddStatisticEffectToStats(stats, "CanonicalMaxMana", flat);
        foreach (var piece in pieces) CharacterEffectHelper.AddStatisticEffectToStats(stats, "CanonicalMaxManaPercentReduce", piece);
        if (!flatFirst) CharacterEffectHelper.AddStatisticEffectToStats(stats, "CanonicalMaxMana", flat);
        return pieces;
    }
    private static void RemoveManaPieces(ServerWizGameStats stats, WizStatisticEffect[] pieces) {
        foreach (var piece in pieces) CharacterEffectHelper.RemoveStatisticEffectFromStats(stats, "CanonicalMaxManaPercentReduce", piece);
    }
    private static void AssertManaProgressed(Wizard wizard, List<IMessage> packets, string reduction, WizStatisticEffect[] pieces, int xp = 350) {
        Assert.Equal(4, wizard.MagicSchoolBehavior.Level); Assert.Equal(xp, wizard.MagicSchoolBehavior.ExperiencePoints);
        Assert.Equal(ManaMaximum(77, reduction), wizard.GameStats.m_baseMana); Assert.Equal(wizard.GameStats.m_baseMana, wizard.GameStats.m_currentMana);
        var mana = Assert.Single(packets.OfType<WIZARD_12_PROTOCOL.MSG_UPDATEMANA>());
        Assert.Equal(70, mana.Mana); Assert.Equal(70, mana.MaxMana);
        var health = Assert.Single(packets.OfType<WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH>());
        Assert.Equal(180, health.NewHealth); Assert.Equal(180, health.NewHealthMax);
        if (pieces.Length == 2) {
            CharacterEffectHelper.RemoveStatisticEffectFromStats(wizard.GameStats, "CanonicalMaxManaPercentReduce", pieces[0]);
            Assert.Equal(38, wizard.GameStats.m_baseMana);
            CharacterEffectHelper.RemoveStatisticEffectFromStats(wizard.GameStats, "CanonicalMaxManaPercentReduce", pieces[1]);
        } else RemoveManaPieces(wizard.GameStats, pieces);
        Assert.Equal(77, wizard.GameStats.m_baseMana);
    }
    private sealed record ManaLedger(object Identity, int Unreduced, KeyValuePair<uint, float>[] Reductions) {
        internal static ManaLedger Of(ServerWizGameStats stats) {
            var table = typeof(CharacterEffectHelper).GetField("s_manaAdjustments", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            object?[] args = [stats, null]; Assert.True((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!);
            var entry = args[1]!; var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return new(entry, (int)entry.GetType().GetField("UnreducedMaximum", flags)!.GetValue(entry)!,
                ((Dictionary<uint, float>)entry.GetType().GetField("Reductions", flags)!.GetValue(entry)!).OrderBy(row => row.Key).ToArray());
        }
    }
    private static void AssertManaLedger(ManaLedger expected, ServerWizGameStats stats) {
        var actual = ManaLedger.Of(stats); Assert.Same(expected.Identity, actual.Identity); Assert.Equal(expected.Unreduced, actual.Unreduced);
        Assert.Equal(expected.Reductions, actual.Reductions);
    }

    private static void AssertLevelPackets(IReadOnlyList<IMessage> packets, Wizard live, int level) {
        Assert.Equal(5, packets.Count); var row = Fixture.Table(level);
        var up = Assert.IsType<WIZARD_12_PROTOCOL.MSG_LEVELUP>(packets[0]);
        Assert.Equal(live.GameObjectID, up.GlobalID); Assert.Equal(level, up.NewLevel); Assert.Equal("0000000000", up.Data.ToString());
        var health = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH>(packets[1]);
        Assert.Equal(live.GameObjectID, health.CharacterID); Assert.Equal(row.m_hitpoints, health.NewHealth); Assert.Equal(row.m_hitpoints, health.NewHealthMax);
        var mana = Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEMANA>(packets[2]); Assert.Equal(row.m_mana, mana.Mana); Assert.Equal(row.m_mana, mana.MaxMana);
        Assert.Equal(row.m_pipChance, Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEPOWERPIP>(packets[3]).PowerPip);
        Assert.Equal(row.m_petEnergy, Assert.IsType<PET_9_PROTOCOL.MSG_PETENERGYMAX>(packets[4]).MaxEnergy);
    }

    private sealed record State(int Xp, int Level, int RuntimeLevel, int Health, int Mana, int HealthMax, int ManaMax,
        float Pips, int Gold, float Charge, float PotionMax, int Energy, uint Tick) {
        internal static State Of(Wizard wizard) => new(wizard.MagicSchoolBehavior.ExperiencePoints, wizard.MagicSchoolBehavior.Level,
            wizard.GameStats.Level, wizard.GameStats.m_currentHitpoints, wizard.GameStats.m_currentMana,
            wizard.GameStats.m_baseHitpoints, wizard.GameStats.m_baseMana, wizard.GameStats.m_powerPipBase,
            wizard.GameStats.m_currentGold, wizard.GameStats.m_potionCharge, wizard.GameStats.m_potionMax,
            wizard.PetOwnerBehavior.Energy, wizard.PetOwnerBehavior.LastEnergyTickEpoch);
    }

    private sealed class Fixture {
        internal const ulong Character = 947771;
        internal const uint ElixirTemplate = 191103;
        internal const ulong ElixirItem = 9100;
        internal static readonly DateTime Now = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        internal static readonly string[] Operations = ["gain", "level", "remove", "drink", "buy", "slot", "minigame", "set"];
        internal Wizard Saved;
        internal int Saves, Opened;
        internal bool Fail, Durable, Missing;
        internal System.Action? OnSave, OnLoad, OnDispose;
        internal PersistenceSession? Working;
        internal List<WizClientObjectItem> Rows = [];
        internal System.Action<List<WizClientObjectItem>>? OnReadRows;
        internal readonly ProgressionDependencies Dependencies = new() {
            LevelInfo = (_, level) => Table(level), LevelAtXp = xp => (byte)Math.Max(1, xp / 100 + 1),
            XpAtLevel = level => (level - 1) * 100, MaxLevel = () => 4, XpCeiling = () => 350, Prepare = _ => true,
        };
        internal Fixture(int xp = 150, int level = 2) {
            var table = Table(level);
            Saved = new() { CharId = Character,
                GameStats = new(MagicSchool.None, 0) { m_baseHitpoints = table.m_hitpoints, m_baseMana = table.m_mana,
                    m_currentHitpoints = 23, m_currentMana = 17, m_currentGold = 87, m_baseGoldPouch = 500,
                    m_currentArenaPoints = 19, m_currentPvPCurrency = 23, m_potionCharge = 1.25f, m_potionMax = 3,
                    m_dmgBonusPercent = [2f, 3f] },
                MagicSchoolBehavior = new() { MagicSchool = MagicSchool.Fire, Level = level, ExperiencePoints = xp, TrainingPoints = 5, OverflowXp = 8 },
                EquipmentBehavior = new() { EquippedItemIds = [42], EquippedItems = [],
                    SlotList = [new() { ItemId = 42, SlotType = EquipmentSlotType.Hat, ItemName = "authored", EquippedSince = Now }] },
                InventoryBehavior = new() { InventoryItemIds = [], Items = new() }, StorageBehavior = new() { BankItemIds = [], Items = new() },
                PetOwnerBehavior = new() { MaxSlots = 1, Eggs = [], PetHatchTimes = [] } };
            SetEnergy(Saved.PetOwnerBehavior, 7, 1234);
        }
        internal static MagicLevelInfo Table(int level) => new() { m_level = level, m_hitpoints = 100 + level * 20,
            m_mana = 30 + level * 10, m_pipChance = level * .125f, m_petEnergy = 12 + level * 3 };
        internal static PotionRules Potions() => new() { Id = "potions-authored", Profiles = [], SourceFile = "authored-test",
            MinigameManaReward = .5, FlaskPerMaxMana = 1, PricePerLevel = 3, MinPrice = 0, MaxPrice = 1000 };
        internal static IDisposable TemplateScope(WizItemTemplate template) {
            var templates = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory)
                .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var existed = templates.TryGetValue(ElixirTemplate, out var previous); templates[ElixirTemplate] = template;
            return new Restore(() => { if (existed) templates[ElixirTemplate] = previous!; else templates.Remove(ElixirTemplate); });
        }
        internal WizClientObjectItem AddElixir(Wizard live, ElixirTests.CanonicalFixture canonical) {
            var definition = canonical.Definition(ElixirTemplate); var item = canonical.Item(ElixirItem, ElixirTemplate);
            item.m_characterId = Character;
            Assert.Single(item.m_inactiveBehaviors.OfType<ClientElixirBehavior>()).m_expireTime = definition.DurationSeconds;
            Assert.True(live.EquipmentBehavior.AppendElixirItem(item)); MatchSavedEquipment(live);
            ElixirRuntime.PublishValidated(live, new ElixirLedger { OwnerId = Character, Active = [new ElixirEntry {
                ItemId = ElixirItem, ItemDocumentId = "authored/elixir", TemplateId = ElixirTemplate,
                Families = [.. definition.Families], RemainingSeconds = definition.DurationSeconds,
            }] });
            return item;
        }
        internal void MatchSavedEquipment(Wizard live) {
            Saved.EquipmentBehavior.EquippedItemIds = live.EquipmentBehavior.EquippedItemIds.ToList();
            Saved.EquipmentBehavior.SlotList = live.EquipmentBehavior.SlotList.Select(slot => new EquipmentSlot {
                ItemId = slot.ItemId, SlotType = slot.SlotType, ItemName = slot.ItemName, EquippedSince = slot.EquippedSince }).ToList();
        }
        internal void Rental(Wizard live, string state) {
            Saved.EquipmentBehavior.EquippedItemIds.Add(43);
            var slot = new EquipmentSlot { ItemId = 43, SlotType = EquipmentSlotType.Robe, ItemName = "authored-rental", EquippedSince = Now };
            Saved.EquipmentBehavior.SlotList.Add(slot);
            live.EquipmentBehavior.SlotList.Add(new() { ItemId = slot.ItemId, SlotType = slot.SlotType, ItemName = slot.ItemName, EquippedSince = slot.EquippedSince });
            var expiry = (uint)new DateTimeOffset(Now).ToUnixTimeSeconds();
            var item = new WizClientObjectItem { m_globalID = 43, m_characterId = state == "foreign" ? Character + 1 : Character,
                m_templateID = 9342, m_inactiveBehaviors = [new ClientTimedItemBehavior {
                    m_expireTime = state == "zero-expiry" ? 0 : state == "nonexpired" ? expiry + 1 : expiry - 1 }] };
            if (state != "missing") Rows.Add(item);
            if (state == "duplicate") Rows.Add(CloneRow(item));
            Dependencies.RentalNow = () => new DateTimeOffset(Now);
            Dependencies.RentalTemplates = id => { Assert.Equal(9342ul, id); return new WizItemTemplate { m_templateID = (uint)id,
                m_behaviors = [new TimedItemBehaviorTemplate { m_timerType = state == "game-timer" ? TimerType.TimerType_Game
                    : state == "unknown-timer" ? (TimerType)99 : TimerType.TimerType_Calendar }] }; };
        }
        internal Wizard Live(int? attachedLevel = null, int? attachedXp = null) {
            var live = Clone(Saved); live.MagicSchoolBehavior.Level = attachedLevel ?? live.MagicSchoolBehavior.Level;
            live.MagicSchoolBehavior.ExperiencePoints = attachedXp ?? live.MagicSchoolBehavior.ExperiencePoints;
            var row = Table(live.MagicSchoolBehavior.Level);
            live.HasInitializedRuntimeStats = true;
            live.GameStats.Level = live.MagicSchoolBehavior.Level; live.GameStats.MagicSchool = live.MagicSchoolBehavior.MagicSchool;
            live.GameStats.m_baseHitpoints = row.m_hitpoints + 11; live.GameStats.m_baseMana = row.m_mana + 7;
            live.GameStats.m_powerPipBase = row.m_pipChance + .125f; live.GameStats.m_currentHitpoints = 31; live.GameStats.m_currentMana = 19;
            live.GameStats.m_currentGold = 999; live.GameStats.m_currentArenaPoints = 999; live.GameStats.m_currentPvPCurrency = 999;
            live.GameStats.m_potionCharge = 9; live.GameStats.m_potionMax = 9;
            live.MagicSchoolBehavior.TrainingPoints = 77; live.MagicSchoolBehavior.OverflowXp = 88;
            live.PetOwnerBehavior.MaxSlots = 9; SetEnergy(live.PetOwnerBehavior, 99, 9999); return live;
        }
        internal bool Change(string operation, Wizard live) => operation switch {
            "gain" => WizardProgressionTransactions.TryGainExperience(live, 250, out _),
            "level" => WizardProgressionTransactions.TrySetLevel(live, 4, true, out _),
            "remove" => WizardProgressionTransactions.TryRemoveExperience(live, 80, out _),
            "drink" => WizardPotionTransactions.TryDrink(live, Now, out _),
            "buy" => WizardPotionTransactions.TryBuy(live, true, out _, level => level * 3),
            "slot" => WizardPotionTransactions.TryAddSlotAndFill(live, out _),
            "minigame" => WizardPotionTransactions.TryMinigameFill(live, Potions(), out _),
            "set" => WizardPotionTransactions.TrySetPotions(live, 2.5f, 5f, out _),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        internal bool Apply(string operation, Wizard live, System.Action<IMessage> send, System.Action<IMessage> broadcast, System.Action close)
            => operation switch {
                "gain" => WizardService.ApplyExperience(live, 250, send, broadcast, close),
                "level" => WizardService.ApplyLevel(live, 4, send, broadcast, close),
                "drink" => PotionService.ApplyDrink(live, Now, send, close),
                "buy" => PotionService.ApplyBuy(live, true, send, close, level => level * 3),
                _ => Change(operation, live),
            };
        internal IDisposable Scope() {
            var oldStore = WizardCollection.TestStoreScope.Value; var oldDependencies = WizardProgressionTransactions.TestScope.Value;
            var oldRows = WizardInventoryTransactions.TestRowsScope.Value;
            WizardCollection.TestStoreScope.Value = new(Open, Load); WizardProgressionTransactions.TestScope.Value = Dependencies;
            WizardInventoryTransactions.TestRowsScope.Value = session => { var rows = Session(session).Rows; OnReadRows?.Invoke(rows); return rows; };
            return new Restore(() => { WizardCollection.TestStoreScope.Value = oldStore; WizardProgressionTransactions.TestScope.Value = oldDependencies;
                WizardInventoryTransactions.TestRowsScope.Value = oldRows; });
        }
        internal IDocumentSession Open() {
            Opened++; var session = DispatchProxy.Create<IDocumentSession, PersistenceSession>(); var proxy = Session(session);
            Working = proxy; proxy.Wizard = Clone(Saved); proxy.DisposeSession = () => OnDispose?.Invoke();
            proxy.Rows = Rows.Select(CloneRow).ToList();
            Assert.False(proxy.Wizard.HasInitializedRuntimeStats);
            proxy.Save = () => {
                Assert.True(WizardCollection.HoldsWriteLane); Saves++; OnSave?.Invoke();
                if (Fail && !Durable) throw new InvalidOperationException("authored failed write");
                Saved = Clone(proxy.Wizard);
                Rows = proxy.Rows.Select((row, index) => CloneRow(proxy.Ignored.Contains(row) ? Rows[index] : row)).ToList();
                if (Fail) throw new InvalidOperationException("authored durable write lost acknowledgement");
            };
            return session;
        }
        internal Wizard Load(IDocumentSession session, ulong character) {
            Assert.True(WizardCollection.HoldsWriteLane); Assert.Equal(Character, character); OnLoad?.Invoke();
            return Missing ? null! : Session(session).Wizard;
        }
        private static PersistenceSession Session(IDocumentSession session) => (PersistenceSession)(object)session;
        private static Wizard Clone(Wizard source) {
            var stats = source.GameStats.CloneSnapshotWithGold(source.GameStats.m_currentGold);
            // CLASSIC: Raven's fresh row lacks these JsonIgnore runtime offsets.
            stats.Level = 0; stats.MagicSchool = MagicSchool.None; stats.m_powerPipBase = 0;
            stats.m_dmgBonusPercent = source.GameStats.m_dmgBonusPercent?.ToList();
            var clone = new Wizard { CharId = source.CharId, GameStats = stats,
                MagicSchoolBehavior = new() { MagicSchool = source.MagicSchoolBehavior.MagicSchool, Level = source.MagicSchoolBehavior.Level,
                    ExperiencePoints = source.MagicSchoolBehavior.ExperiencePoints, TrainingPoints = source.MagicSchoolBehavior.TrainingPoints,
                    OverflowXp = source.MagicSchoolBehavior.OverflowXp },
                EquipmentBehavior = source.EquipmentBehavior is null ? null! : new() {
                    EquippedItemIds = source.EquipmentBehavior.EquippedItemIds?.ToList(), EquippedItems = [],
                    SlotList = source.EquipmentBehavior.SlotList?.Select(slot => new EquipmentSlot { ItemId = slot.ItemId,
                        SlotType = slot.SlotType, ItemName = slot.ItemName, EquippedSince = slot.EquippedSince }).ToList() },
                PetOwnerBehavior = new() { MaxSlots = source.PetOwnerBehavior.MaxSlots, Eggs = [], PetHatchTimes = [] } };
            clone.InventoryBehavior = new() { InventoryItemIds = source.InventoryBehavior.InventoryItemIds.ToList(), Items = new() };
            clone.StorageBehavior = new() { BankItemIds = source.StorageBehavior.BankItemIds.ToList(), Items = new() };
            clone.PetOwnerBehavior.PublishCommittedEnergy(source.PetOwnerBehavior); return clone;
        }
        private static WizClientObjectItem CloneRow(WizClientObjectItem item) => item with {
            m_inactiveBehaviors = item.m_inactiveBehaviors.Select(behavior => behavior switch {
                ClientTimedItemBehavior timed => (BehaviorInstance)(timed with { }),
                ClientPetItemBehavior pet => pet with { }, _ => behavior,
            }).ToList(),
        };
        private static void SetEnergy(ServerPetOwnerBehavior owner, int energy, uint tick) {
            owner.SetEnergy(energy);
            typeof(ServerPetOwnerBehavior).GetProperty(nameof(ServerPetOwnerBehavior.LastEnergyTickEpoch))!.SetValue(owner, tick);
        }
        private sealed class Restore(System.Action restore) : IDisposable { public void Dispose() => restore(); }
    }

    public class PersistenceSession : DispatchProxy {
        internal Wizard Wizard = null!;
        internal List<WizClientObjectItem> Rows = [];
        internal HashSet<object> Ignored = new(ReferenceEqualityComparer.Instance);
        internal System.Action Save = null!, DisposeSession = null!;
        private IAdvancedSessionOperations? _advanced;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "get_Advanced" => Advanced(), "SaveChanges" => SaveNow(), "Dispose" => DisposeNow(),
            _ => throw new NotSupportedException(method.Name),
        };
        private object? SaveNow() { Save(); return null; }
        private IAdvancedSessionOperations Advanced() {
            if (_advanced is null) { _advanced = DispatchProxy.Create<IAdvancedSessionOperations, PersistenceAdvanced>();
                ((PersistenceAdvanced)(object)_advanced).Session = this; }
            return _advanced;
        }
        private object? DisposeNow() { DisposeSession(); return null; }
    }

    public class PersistenceAdvanced : DispatchProxy {
        internal PersistenceSession Session = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "set_OptimisticConcurrencyMode" => null, "IgnoreChangesFor" => Ignore(args![0]!), _ => throw new NotSupportedException(method.Name),
        };
        private object? Ignore(object value) { Assert.True(WizardCollection.HoldsWriteLane); Assert.True(Session.Ignored.Add(value)); return null; }
    }
}
