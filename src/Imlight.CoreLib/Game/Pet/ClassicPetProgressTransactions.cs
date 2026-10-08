// CLASSIC: pet progress and its snack/energy cost are one acknowledged, freshly owned write.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pets;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Pet;

internal sealed record PetProgressReceipt(WizClientObjectItem Pet, PetGrowth Growth,
    IReadOnlyList<PetStatChange> Applied, IReadOnlyList<IMessage> Messages, int Cost, SnackTaste Taste) {
    internal PetTalentTransition Talents { get; init; }
}

// CLASSIC: fixtures replace only native preparation; ownership, calculations and the commit remain real.
internal sealed class PetProgressDependencies {
    internal Func<PetGameEndData, ByteString> SerializeEnd;
    internal Func<WizClientObjectItem, ByteString> SerializePet;
    internal Func<Wizard, int> MaxEnergy;
}

internal static class ClassicPetProgressTransactions {
    internal static readonly AsyncLocal<PetProgressDependencies> TestScope = new();

    internal static bool TryInitialize(Wizard live, ulong petId, out WizClientObjectItem pet, out int energy)
        => TryInitialize(live, petId, false, null, out pet, out energy);

    // CLASSIC: game admission requires the selected pet to remain in the exact fresh Pet slot.
    // The generic owned-pet initializer above still accepts backpack pets.
    internal static bool TryInitializeForGame(Wizard live, ulong petId, out WizClientObjectItem pet, out int energy,
        Func<bool> contextStillValid = null)
        => TryInitialize(live, petId, true, contextStillValid, out pet, out energy);

    private static bool TryInitialize(Wizard live, ulong petId, bool requireEquipped, Func<bool> contextStillValid,
        out WizClientObjectItem pet, out int energy) {
        pet = null; energy = 0;
        if (!Usable(live)) return false;
        WizClientObjectItem snapshot = null, published = null;
        PetTalentReceipt talentReceipt = null;
        var availableEnergy = 0;
        var unchanged = false;
        var committed = WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
            if (!Usable(live) || saved.PetOwnerBehavior is null
                || contextStillValid?.Invoke() == false
                || (requireEquipped && (!HasExactEquippedPet(saved, petId) || !HasExactEquippedPet(live, petId)))
                || !WizardInventoryTransactions.TryReadOwnedItem(session, saved, petId, out snapshot)
                || !OwnedPetCanPublish(live, saved, snapshot)) return false;
            availableEnergy = saved.PetOwnerBehavior.Energy;
            if (contextStillValid?.Invoke() == false) return false;
            if (!PetProgress.EnsureInitialized(snapshot)) {
                unchanged = contextStillValid?.Invoke() != false;
                return false;
            }
            WizardInventoryTransactions.ProtectUnmodifiedRows(session, snapshot);
            talentReceipt = PetTalentRuntime.Prepare(live, snapshot);
            return contextStillValid?.Invoke() != false;
        }, saved => {
            published = WizardInventoryTransactions.PublishCommittedOwnedItem(live, saved, snapshot);
            PetTalentRuntime.Publish(live, published, talentReceipt);
        },
            onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        if (!committed && !unchanged) return false;
        // No mutation is published for an unchanged authoritative read, and no needless save is issued.
        pet = committed ? published : snapshot;
        energy = availableEnergy;
        return true;
    }

    private static bool HasExactEquippedPet(Wizard wizard, ulong petId) {
        var equipment = wizard?.EquipmentBehavior;
        var slots = equipment?.SlotList;
        return petId != 0 && slots is not null
            && slots.Count(slot => slot is { SlotType: EquipmentSlotType.Pet }) == 1
            && slots.Any(slot => slot is { SlotType: EquipmentSlotType.Pet } && slot.ItemId == petId)
            && equipment.EquippedItemIds?.Count(id => id == petId) == 1
            && wizard.InventoryBehavior?.InventoryItemIds is { } inventory && !inventory.Contains(petId)
            && wizard.StorageBehavior?.BankItemIds?.Contains(petId) != true
            // CLASSIC: materialized aliases can conflict even when their saved ID lists omit this pet.
            && wizard.InventoryBehavior?.Items?.Any(item => item is not null && item.m_globalID.Full == petId) != true
            && wizard.StorageBehavior?.Items?.Any(item => item is not null && item.m_globalID.Full == petId) != true;
    }

    internal static bool TryFinish(Wizard live, ulong petId, string game, string trackName,
        IReadOnlyList<PetStatChange> trackChanges, int points, int wins, out PetProgressReceipt receipt,
        Random random = null, Func<bool> contextStillValid = null) {
        receipt = null;
        if (!Usable(live) || live.PetOwnerBehavior is null || game is null || trackChanges is null) return false;
        PetProgressReceipt prepared = null;
        var committed = WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
            if (!Usable(live) || saved.PetOwnerBehavior is null || contextStillValid?.Invoke() == false
                || !WizardInventoryTransactions.TryReadOwnedItem(session, saved, petId, out var pet)
                || !OwnedPetCanPublish(live, saved, pet) || contextStillValid?.Invoke() == false) return false;
            var b = PetProgress.Behavior(pet);
            var cost = PetRules.EnergyCost(b.m_level);
            saved.PetOwnerBehavior.SetEnergy(Math.Max(0, saved.PetOwnerBehavior.Energy - cost));
            var applied = PetProgress.ApplyStats(pet, PetRules.DistributePoints(points, trackChanges));
            var growth = PetProgress.AddXp(pet, PetRules.GameXp(points, b.m_level), random ?? Random.Shared);
            try {
                var data = SerializeEnd(EndData(wins, trackName ?? "", trackChanges, applied, growth.Xp, wins));
                if (data.Length == 0) return false;
                var max = TestScope.Value?.MaxEnergy?.Invoke(saved)
                    ?? Shared.Character.MagicLevelsConfig.GetPlayerLevelInfo(saved.MagicSchoolBehavior.MagicSchool,
                        saved.MagicSchoolBehavior.Level).m_petEnergy;
                List<IMessage> messages = [new PET_9_PROTOCOL.MSG_PETENERGYTICK {
                    GlobalID = live.GameObjectID, Energy = saved.PetOwnerBehavior.Energy,
                    MaxEnergy = max, TickTime = (int)saved.PetOwnerBehavior.LastEnergyTickEpoch },
                    new PET_9_PROTOCOL.MSG_PETGAMEEND { Game = game, Data = data }];
                if (!PrepareGrowthMessages(live, pet, growth, messages)) return false;
                var talents = PetTalentRuntime.PrepareTransition(live, pet);
                messages.AddRange(talents.Messages);
                prepared = new(pet, growth, applied, messages, cost, default) { Talents = talents };
            }
            catch (Exception) { return false; }
            WizardInventoryTransactions.ProtectUnmodifiedRows(session, pet);
            // CLASSIC: this is a pre-save refusal, not an atomic scene fence across SaveChanges.
            return contextStillValid?.Invoke() != false;
        }, saved => {
            var pet = WizardInventoryTransactions.PublishCommittedOwnedItem(live, saved, prepared.Pet);
            live.PetOwnerBehavior.PublishCommittedEnergy(saved.PetOwnerBehavior);
            PetTalentRuntime.Publish(live, pet, prepared.Talents?.Receipt);
            prepared = prepared with { Pet = pet };
        }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        if (committed) receipt = prepared;
        return committed;
    }

    internal static bool TryFeed(Wizard live, ulong petId, ulong snackId, out PetProgressReceipt receipt,
        Random random = null, Func<bool> contextStillValid = null) {
        receipt = null;
        if (!Usable(live) || snackId == 0) return false;
        List<ClientPetSnackItem> snackBag = [];
        PetProgressReceipt prepared = null;
        var committed = WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
            if (!Usable(live) || contextStillValid?.Invoke() == false
                || !WizardInventoryTransactions.TryReadOwnedItem(session, saved, petId, out var pet)
                || !OwnedPetCanPublish(live, saved, pet)
                || !WizardPetSnackTransactions.TryReadOwnedBag(session, saved, out var before)
                || contextStillValid?.Invoke() == false) return false;
            var selected = before.SingleOrDefault(snack => snack.m_globalID.Full == snackId);
            if (selected is null || CoreObjectFactory.GetCoreTemplate(selected.m_templateID) is not PetSnackItemTemplate template
                || !WizardPetSnackTransactions.TryStageConsume(session, saved, snackId, out var snack, out snackBag)) return false;
            var changes = (template.m_statModifierSet?.m_modifications ?? []).Where(m => m is not null)
                .Select(m => new PetStatChange(m.m_name.ToString(), m.m_change)).ToList();
            var taste = PetRules.Taste(PetProgress.FavouriteSnackKinds((uint)pet.m_templateID),
                PetProgress.School((uint)pet.m_templateID), template.m_adjectiveList?.Select(a => a.ToString()) ?? [],
                template.m_school.ToString());
            var fed = PetRules.Feed(changes, taste);
            var applied = PetProgress.ApplyStats(pet, fed.Changes);
            var growth = PetProgress.AddXp(pet, fed.Xp, random ?? Random.Shared);
            try {
                var data = SerializeEnd(EndData((int)taste, "", fed.Changes, applied, growth.Xp, 0));
                if (data.Length == 0) return false;
                List<IMessage> messages = [snack.m_quantity > 0
                    ? new PET_9_PROTOCOL.MSG_PETSNACKUPDATE { GlobalID = live.GameObjectID,
                        ItemID = snack.m_globalID, Quantity = snack.m_quantity }
                    : new PET_9_PROTOCOL.MSG_PETSNACKREMOVE { GlobalID = live.GameObjectID, ItemID = snackId },
                    new PET_9_PROTOCOL.MSG_PETGAMESNACKFEEDSUCCESS { Data = data }];
                if (!PrepareGrowthMessages(live, pet, growth, messages)) return false;
                var talents = PetTalentRuntime.PrepareTransition(live, pet);
                messages.AddRange(talents.Messages);
                prepared = new(pet, growth, applied, messages, 0, taste) { Talents = talents };
            }
            catch (Exception) { return false; }
            WizardInventoryTransactions.ProtectUnmodifiedRows(session, pet);
            return contextStillValid?.Invoke() != false;
        }, saved => {
            var pet = WizardInventoryTransactions.PublishCommittedOwnedItem(live, saved, prepared.Pet);
            WizardPetSnackTransactions.PublishCommittedBag(live, saved, snackBag);
            PetTalentRuntime.Publish(live, pet, prepared.Talents?.Receipt);
            prepared = prepared with { Pet = pet };
        }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        if (committed) receipt = prepared;
        return committed;
    }

    private static bool Usable(Wizard live)
        => live is not null && live.CharId != 0 && !WizardCollection.IsInventorySnapshotUncertain(live);

    private static bool OwnedPetCanPublish(Wizard live, Wizard saved, WizClientObjectItem pet) {
        if (PetProgress.Behavior(pet) is not { m_level: > 0 }
            || !WizardInventoryTransactions.CanPublishOwnedItem(live, saved, pet)) return false;
        if (!PetTalentRuntime.Enabled) return true;
        var id = pet.m_globalID.Full;
        // CLASSIC: the receipt may refresh only the same fresh Pet slot. A contradictory bag/bank/materialized
        // alias is refused before growth or resource staging, rather than silently refreshing stale equipment.
        if (saved.EquipmentBehavior?.EquippedItemIds?.Contains(id) == true)
            return HasExactEquippedPet(saved, id) && PetTalentRuntime.HasExactEquippedPet(live, id);
        return live.InventoryBehavior?.InventoryItemIds?.Count(itemId => itemId == id) == 1
            && live.EquipmentBehavior?.EquippedItemIds?.Contains(id) != true
            && live.EquipmentBehavior?.EquippedItems?.Any(item => item is not null && item.m_globalID.Full == id) != true
            && live.StorageBehavior?.BankItemIds?.Contains(id) != true
            && live.StorageBehavior?.Items?.Any(item => item is not null && item.m_globalID.Full == id) != true;
    }

    private static bool PrepareGrowthMessages(Wizard live, WizClientObjectItem pet, PetGrowth growth,
        List<IMessage> messages) {
        var b = PetProgress.Behavior(pet);
        messages.Add(new WIZARD2_53_PROTOCOL.MSG_GAINPETXP { PetGID = pet.m_globalID, XP = (uint)Math.Max(0, growth.Xp) });
        if (growth.LeveledUp) messages.Add(new PET_9_PROTOCOL.MSG_PETLEVELUP {
            GlobalID = pet.m_globalID, OverallRating = (byte)Math.Min(255u, b.m_overallRating),
            ActiveRating = (byte)Math.Min(255u, b.m_activeRating), PetLevel = b.m_level,
            NewTalent = growth.NewTalents.LastOrDefault(), NewDerbyPower = 0, NewJewel = 0, Display = 1 });
        if (CoreObjectFactory.GetCoreTemplate(pet.m_templateID) is not WizItemTemplate template
            || ItemHelper.GetItemSlot(template) is not { } slot) return false;
        var data = TestScope.Value?.SerializePet is { } serialize ? serialize(pet) : SerializePet(pet);
        if (data.Length == 0) return false;
        messages.Add(new GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_EQUIPITEM {
            GlobalID = live.GameObjectID, SlotName = slot.SlotType.ToString(), IsValid = 1, SerializedItem = data });
        return true;
    }

    private static PetGameEndData EndData(int score, string setName, IEnumerable<PetStatChange> asked,
        IReadOnlyList<PetStatChange> applied, int xp, int wins) {
        var actual = applied.ToDictionary(a => a.Stat, a => a.Change, StringComparer.OrdinalIgnoreCase);
        return new() { m_Score = score, m_statMods = new() { m_name = setName,
            m_modifications = asked.Where(c => c.Change != 0).Select(c => new PetStatModification {
                m_name = c.Stat, m_change = c.Change, m_actualChange = (uint)Math.Max(0, actual.GetValueOrDefault(c.Stat)),
            }).ToList(), m_scene = "", m_gameScoreFactor = [] },
            m_xpGain = (uint)Math.Max(0, xp), m_wins = (uint)Math.Max(0, wins) };
    }

    private static ByteString SerializeEnd(PetGameEndData data) {
        if (TestScope.Value?.SerializeEnd is { } serialize) return serialize(data);
        var serializer = new ObjectSerializer(Behaviors: SerializerFlags.None);
        return serializer.Serialize(data, (PropertyFlags)5, out var bytes) ? bytes : default;
    }

    private static ByteString SerializePet(WizClientObjectItem pet) {
        var serializer = new CoreObjectSerializer(behaviors: SerializerFlags.None);
        return serializer.Serialize(pet, PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit,
            out var bytes) ? bytes : default;
    }
}
