/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * WIZARD RUNTIME AND PERSISTED STATE
 * ========================================================================
 *
 * PURPOSE:
 * Holds player data and runtime state shared by character services.
 *
 * USAGE EXAMPLE:
 * Services access the active wizard through their session.
 *
 * NOTE:
 * GameEffects provides synchronized operations and detached snapshots.
 *
 * TODO:
 *
 * Created by: Jay with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 09/28/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Implementations;
using Imlight.CoreLib.Shared.Utilities;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imcodec.Types;
using Imlight.CoreLib.Game.Pet;

namespace Imlight.CoreLib.WizardData.Models.Player;

[Serializable]
public class Wizard {

    public ulong AccountId { get; set; }
    public ulong CharId {
        get;
        set {
            var gameObjectId = GetGameObjectId(value);
            field = value;
            GameObject.m_characterId = (GID) value;
            GameObject.m_globalID = gameObjectId;
            GameObject.m_permID = gameObjectId;
        }
    }
    [JsonIgnore] public ulong GameObjectID => GetGameObjectId(CharId);

    // Player objects use a separate ID from saved characters. Zero means no player.
    // Keep this mapping here so offline callers do not need an attached Wizard.
    public static ulong GetGameObjectId(ulong charId) => charId == 0 ? 0 : checked(charId + 2);

    // Only for player object IDs; item, NPC and zone IDs use their own identities.
    public static bool TryGetCharacterId(ulong gameObjectId, out ulong charId) {
        charId = gameObjectId > 2 ? gameObjectId - 2 : 0;
        return charId != 0;
    }
    public string Zone { get; set; }
    public string ZoneDisplayName { get; set; }
    public string PreviousZone { get; set; }

    public ulong InteriorStowedMountId { get; set; }

    /// <summary>
    /// CLASSIC: the mount the server took off for a duel (<see cref="StowMountForDuel"/>), saved, so a session that
    /// ends mid-fight (logout, a dropped client, a server restart) gets it back at the next attach
    /// (<see cref="RestoreDuelStowedMount"/>) instead of finding it in the backpack.
    /// </summary>
    public ulong CombatStowedMountId { get; set; }
    public string MarkedZone { get; set; }
    public string MarkedZoneDisplayName { get; set; }
    public uint LastLoginTime { get; set; }
    public long TimeHomeLastClicked { get; set; }
    public byte World { get; set; }
    public Vector3 Location {
        get => GameObject.m_location;
        set {
            GameObject.m_location = value;
            _hasLocation = true;
        }
    }
    public Vector3 Orientation {
        get => GameObject.m_orientation;
        set {
            GameObject.m_orientation = value;
            _hasOrientation = true;
        }
    }

    public Vector3 MarkedLocation { get; set; }
    public Vector3 MarkedOrientation { get; set; }

    public WizardCharacterBehavior WizardAvatar { get; set; }
    public ServerWizPlayerNameBehavior PlayerNameBehavior { get; set; }
    public ServerWizInventoryBehavior InventoryBehavior { get; set; }
    public ServerWizStorageBehavior StorageBehavior { get; set; } = new(); // CLASSIC: the dorm bank (BankService)
    public ServerWizEquipmentBehavior EquipmentBehavior { get; set; }
    public ServerMagicSchoolBehavior MagicSchoolBehavior { get; set; }
    public ServerWizSpellbookBehavior SpellbookBehavior { get; set; }
    public ServerMountOwnerBehavior MountOwnerBehavior { get; set; }
    public ServerPetSnackBehavior PetSnackBehavior { get; set; }
    public ServerAlchemyBehavior AlchemyBehavior { get; set; }
    public ServerFriendBehavior FriendsBehavior { get; set; }
    [JsonIgnore] public ServerObjectStateBehavior ObjectStateBehavior { get; set; }
    public ServerWizGameStats GameStats { get; set; }
    public ServerPetOwnerBehavior PetOwnerBehavior { get; set; }
    public ServerQuestBehavior QuestBehavior { get; set; }

    [JsonIgnore] public Account Account;
    // Holds character data before attachment and is replaced by the initialized player object.
    [JsonIgnore] public WizClientObject GameObject {
        get;
        set {
            ArgumentNullException.ThrowIfNull(value);
            value.m_characterId = (GID) CharId;
            value.m_globalID = GameObjectID;
            value.m_permID = GameObjectID;
            if (_hasLocation) {
                value.m_location = field.m_location;
            }
            if (_hasOrientation) {
                value.m_orientation = field.m_orientation;
            }
            field = value;
            HasInitializedGameObject = true;
        }
    } = new();
    // Set when attachment replaces the offline data object; does not imply zone entry.
    [JsonIgnore] public bool HasInitializedGameObject { get; private set; }
    [JsonIgnore] public WizardEffectCollection GameEffects { get; } = new();
    // CLASSIC: set only after the complete lane-protected base/gear/effect recalculation; never persisted.
    [JsonIgnore] internal bool HasInitializedRuntimeStats { get; set; }
    [JsonIgnore] public string GameServerIp;
    [JsonIgnore] public ushort GameServerPort;
    [JsonIgnore] public string QueuedZoneName;
    [JsonIgnore] public string QueuedZoneLocation;
    [JsonIgnore] internal DynamodSet DynamodSet { get; set; }
    [JsonIgnore] internal bool IsInCombatGrace { get; set; }
    // CLASSIC: potion eligibility and runtime stat changes share the character lane.
    [JsonIgnore] private bool _isInDuel;
    [JsonIgnore] internal bool IsInDuel {
        get => _isInDuel;
        set => WizardCollection.WithCharacterLock(CharId, () => { _isInDuel = value; return true; });
    }

    /// <summary>
    /// Tracks hatched pets for MSG_PETTOMEPETADDED. Key: pet global ID, Value: pet template ID.
    /// Runtime-only; not persisted (the pet tome behavior blob handles persistence).
    /// </summary>
    [JsonIgnore] public readonly Dictionary<ulong, uint> OwnedPets = [];

    [JsonIgnore] private bool _hasLocation;
    [JsonIgnore] private bool _hasOrientation;
    [JsonIgnore] private readonly uint _defaultPetTemplateId = 126412; // Black Cat Pet;
    private const string TutorialStartingZone = "WizardCity/Tutorial_Exterior";

    // Constructor: Used for deserialization. If this is not present, the default constructor will be used.
    [JsonConstructor]
    public Wizard() { }

    // Constructor: Used for character creation.
    public Wizard(MagicSchool wizardSchoolType, WizardCharacterBehavior avatar, uint nameIndices, byte level = 1) {
        CharId = RandomGen.GenerateGUID();
        Zone = ConfigurationManager.Settings["Character.TutorialDisabled"].AsBool()
            ? ClassicStart.IsActive ? ClassicStart.StartingZone : ConfigurationManager.Settings["Character.StartingZone"] // CLASSIC
            : TutorialStartingZone;
        World = ConfigurationManager.Settings["Character.StartingWorld"].AsByte();
        LastLoginTime = (uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Do behaviors.
        WizardAvatar = avatar;
        InitializeDefaultEquipment();
        InitializePlayerName(nameIndices);
        InitializeMagicSchoolBehavior(wizardSchoolType, level);
        InitializeSpellbookBehavior();
        InitializeMountOwnerBehavior();
        InitializeWizardGameStats(wizardSchoolType, level);
        InitializeDefaultPetSnackBehavior();
        InitializePetOwnerBehavior();
        InitializeDefaultInventory();
        InitializeAlchemyBehavior();

        ObjectStateBehavior = new ServerObjectStateBehavior("PlayerMobileStates");
        QuestBehavior = new ServerQuestBehavior();

        DynamodSet = new DynamodSet(CharId);
        DynamodCollection.AddDynamodSet(DynamodSet);
    }

    /// <summary>
    /// CLASSIC: an ambient wizard (Classic/Ambient): built like a new character but never written to the database (no
    /// DynamodSet row; WizardCollection ignores its character id). Its friends come from BuddyRelationshipCollection.
    /// </summary>
    internal static Wizard CreateAmbient(ulong charId, MagicSchool school, WizardCharacterBehavior avatar, uint nameIndices,
                                         byte level, string zone) {
        var wizard = new Wizard {
            CharId = charId,
            Zone = zone,
            World = 1,
            LastLoginTime = (uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            WizardAvatar = avatar,
        };
        wizard.InitializeDefaultEquipment();
        wizard.InitializePlayerName(nameIndices);
        wizard.InitializeMagicSchoolBehavior(school, level);
        wizard.InitializeSpellbookBehavior();
        wizard.InitializeMountOwnerBehavior();
        wizard.InitializeWizardGameStats(school, level);
        wizard.InitializeDefaultPetSnackBehavior();
        wizard.InitializePetOwnerBehavior();
        wizard.InitializeDefaultInventory();
        wizard.InitializeAlchemyBehavior();
        wizard.ObjectStateBehavior = new ServerObjectStateBehavior("PlayerMobileStates");
        wizard.QuestBehavior = new ServerQuestBehavior();
        wizard.FriendsBehavior = new ServerFriendBehavior();
        wizard.DynamodSet = new DynamodSet(charId);

        return wizard;
    }

    public WizClientObject GetInitializedGameObject()
        => HasInitializedGameObject ? GameObject : null;

    public void SaveLocation()
        => WizardCollection.UpdateCharacterLocation(this, Location, Orientation.Z);

    public void SetPersistentLocation(Vector3 loc) {
        Location = loc;

        // Persistent save.
        WizardCollection.UpdateCharacterLocation(this, loc, Orientation.Z);
    }

    public void SetPersistentOrientation(float orientation) {
        Orientation = new Vector3(0, 0, orientation);

        // Persistent save.
        WizardCollection.UpdateCharacterLocation(this, Location, Orientation.Z);
    }

    public void SetZone(string zone, string zoneDisplayName) {
        PreviousZone = Zone;

        Zone = zone;
        ZoneDisplayName = zoneDisplayName;

        // Persistent save.
        WizardCollection.UpdateCharacterZone(this, zone, zoneDisplayName);
    }

    // CLASSIC: fresh numeric balances, runtime level deltas and refill effects commit before publication.
    public bool SetLevel(byte level)
        => WizardProgressionTransactions.TrySetLevel(this, level, resetMismatchedXp: false, out _, refill: false);

    public int AddExperiencePoints(int xp)
        => WizardProgressionTransactions.TryGainExperience(this, xp, out var receipt, refill: false) ? receipt.AppliedXp : 0;

    public void RemoveExperiencePoints(int xp)
        => WizardProgressionTransactions.TryRemoveExperience(this, xp, out _);

    public void SetMarkedLocation(Vector3 loc, Vector3 orientation, string zone, string zoneDisplayName) {
        MarkedLocation = loc;
        MarkedOrientation = orientation;
        MarkedZone = zone;
        MarkedZoneDisplayName = zoneDisplayName;

        // Persistent save.
        WizardCollection.UpdateCharacterMarkedLocation(this, loc, orientation, zone, zoneDisplayName);
    }

    public void SetTimeHomeLastClicked(long time) {
        TimeHomeLastClicked = time;

        WizardCollection.UpdateCharacterTimeWentHome(this, time);
    }

    public void SetMaxGold(int maxGold) {
        GameStats.m_baseGoldPouch = maxGold;

        // Persistent save.
        WizardCollection.UpdateCharacterGameStats(this);
    }

    public void AddGold(int gold)
        => WizardCollection.ChangeGold(this, gold, capToPouch: true);

    /// <summary>CLASSIC: gives back gold a failed purchase took (not capped at the pouch, so nothing is lost).</summary>
    public void RefundGold(int gold) {
        if (gold > 0) {
            WizardCollection.ChangeGold(this, gold, capToPouch: false);
        }
    }

    /// <summary>CLASSIC: spends the gold only if the saved balance covers it (never below zero).</summary>
    /// <returns>True if it was spent.</returns>
    public bool RemoveGold(int gold)
        => WizardCollection.TrySpendGold(this, gold);

    public void UpdateHealth(int newHealth) {
        WizardCollection.WithCharacterLock(CharId, () => {
            if (WizardCollection.IsInventorySnapshotUncertain(this)) return false;
            GameStats.m_currentHitpoints = newHealth;

            // Persistent save. CLASSIC: best effort here - this runs inside duel resolution, and a database timeout thrown out of it
            // ended the planning-phase handler and left the duel waiting forever. The new value is in memory and is saved with the next write.
            try {
                WizardCollection.UpdateCharacterGameStats(this);
            }
            catch (Exception ex) {
                Logger.Error("Saving health {0} of a wizard failed ({1}); it is kept in memory.", Logger.Args(newHealth, ex.GetType().Name));
            }

            return true;
        });
    }

    public void UpdateMaxHealth(int newMaxHealth) {
        WizardCollection.WithCharacterLock(CharId, () => {
            if (WizardCollection.IsInventorySnapshotUncertain(this)) return false;
            GameStats.m_baseHitpoints = newMaxHealth;

            // Persistent save.
            WizardCollection.UpdateCharacterGameStats(this);

            return true;
        });
    }

    public void UpdateMana(int newMana) {
        WizardCollection.WithCharacterLock(CharId, () => {
            if (WizardCollection.IsInventorySnapshotUncertain(this)) return false;
            GameStats.m_currentMana = newMana;

            // Persistent save (best effort, as UpdateHealth: it runs inside duel resolution).
            try {
                WizardCollection.UpdateCharacterGameStats(this);
            }
            catch (Exception ex) {
                Logger.Error("Saving mana {0} of a wizard failed ({1}); it is kept in memory.", Logger.Args(newMana, ex.GetType().Name));
            }

            return true;
        });
    }

    public void UpdateEnergy(int newEnergy) {
        WizardCollection.WithCharacterLock(CharId, () => {
            if (WizardCollection.IsInventorySnapshotUncertain(this)) return false;
            PetOwnerBehavior.SetEnergy(newEnergy);

            // Persistent save.
            WizardCollection.UpdateCharacterPetOwnerBehavior(this);

            return true;
        });
    }

    public void UpdateMaxMana(int newMaxMana) {
        WizardCollection.WithCharacterLock(CharId, () => {
            if (WizardCollection.IsInventorySnapshotUncertain(this)) return false;
            GameStats.m_baseMana = newMaxMana;

            // Persistent save.
            WizardCollection.UpdateCharacterGameStats(this);

            return true;
        });
    }

    public void UpdateCantripLevel(byte newCantripLevel) {
        GameStats.m_cantripLevel = newCantripLevel;

        // Persistent save.
        WizardCollection.UpdateCharacterGameStats(this);
    }

    public void UpdateLastLoginTime(uint time) {
        LastLoginTime = time;

        // Persistent save.
        WizardCollection.UpdateCharacterLastLoginTime(this);
    }

    public void UpdateTrainingPoints(int newTrainingPoints) {
        MagicSchoolBehavior.TrainingPoints = newTrainingPoints;

        // Persistent save.
        WizardCollection.UpdateCharacterTrainingPoints(this);
    }

    public bool AddItemToInventory(ulong itemId, out WizClientObjectItem item) {
        item = null;
        if (WizardCollection.IsInventorySnapshotUncertain(this)) return false;
        var candidate = CoreObjectFactory.FinalizeCoreObject(itemId) as WizClientObjectItem;
        if (!AddItemToInventory(candidate)) return false;
        item = candidate;
        return true;
    }

    // CLASSIC: the saved item and reference commit together before the live bag changes.
    public bool AddItemToInventory(WizClientObjectItem item)
        => WizardInventoryTransactions.Add(this, item, initializeBehaviors: true);

    public bool AddHatchedPetToInventory(uint templateId, out WizClientObjectItem pet) {
        // The pet factory owns the pet's behavior state, so this skips the template
        // re-initialization that AddItemToInventory does.
        pet = PetFactory.CreateHatchedPet(CharId, templateId);

        return AddPetToInventory(pet);
    }

    /// <summary>
    /// CLASSIC: adds a pet PetFactory made (bought, hatched or granted) without re-initializing its behaviors from the template.
    /// </summary>
    public bool AddPetToInventory(WizClientObjectItem pet)
        => WizardInventoryTransactions.Add(this, pet, initializeBehaviors: false);

    // CLASSIC: moves unlink the saved reference, preserving the original item row for the destination.
    public bool RemoveItemFromInventory(ulong itemId)
        => WizardInventoryTransactions.Remove(this, itemId, destroy: false);

    /// <summary>
    /// CLASSIC: takes an item out of the backpack for good (sold, trashed) and deletes its saved document, which used to
    /// stay behind forever. False, and nothing deleted, when the backpack no longer holds it: the removal is the check,
    /// so of two actors spending the same item only one succeeds.
    /// </summary>
    public bool DestroyInventoryItem(ulong itemId)
        => WizardInventoryTransactions.Remove(this, itemId, destroy: true);

    // CLASSIC: the reference save and application/removal of runtime gear offsets share the progression lane.
    public bool InventoryToEquipmentTransfer(ulong itemId, out List<GameEffectBase> equipEffects, out List<GameEffectBase> unequipEffects) {
        var result = WizardCollection.WithCharacterLock(CharId, () => {
            if (WizardCollection.IsInventorySnapshotUncertain(this))
                return (Success: false, Equip: (List<GameEffectBase>)null, Unequip: (List<GameEffectBase>)null);
            var success = InventoryToEquipmentTransferLocked(itemId, out var added, out var removed);
            return (Success: success, Equip: added, Unequip: removed);
        });
        equipEffects = result.Equip; unequipEffects = result.Unequip;
        return result.Success;
    }

    private bool InventoryToEquipmentTransferLocked(ulong itemId, out List<GameEffectBase> equipEffects, out List<GameEffectBase> unequipEffects) {
        equipEffects = null;
        unequipEffects = null;

        // CLASSIC: find the item's slot before it leaves the backpack; an item with no slot stays where it is.
        var inventoryItem = InventoryBehavior.GetItem(itemId);
        if (inventoryItem is null) {
            Logger.Warning("Tried to equip item with global id {0} that does not exist in player inventory.", Logger.Args(itemId));
            return false;
        }

        // Get the template for this item. Using this template we can get the slot this object should be on.
        var template = ItemHelper.GetItemTemplate(inventoryItem);
        var slot = EquipRules.SlotOf(template);
        if (slot is null) {
            Logger.Warning("Tried to equip item {0} (template {1}), which has no equipment slot.",
                Logger.Args(itemId, inventoryItem.m_templateID.Full));
            return false;
        }

        // Remove the item from the inventory.
        if (!InventoryBehavior.RemoveItem(inventoryItem)) {
            Logger.Warning("Tried to equip item with global id {0} that does not exist in player inventory.", Logger.Args(itemId));
            return false;
        }

        // Get the item that is currently in the slot, if there is one. We want to remove its effects.
        var replacedItem = EquipmentBehavior.GetItemInSlot(slot.SlotType);
        if (replacedItem != null) {
            if (!EquipmentToInventoryTransfer(replacedItem.m_globalID, out unequipEffects)) {
                Logger.Warning("Could not replace item {0} from slot {1}.",
                    Logger.Args(replacedItem.m_globalID, slot.SlotType));

                InventoryBehavior.AddItem(inventoryItem); // CLASSIC: back to the backpack, not lost.
                return false;
            }
        }

        // Add the item to the equipment.
        var equipResult = EquipmentBehavior.EquipItem(inventoryItem, slot.SlotType);
        if (!equipResult) {
            Logger.Warning("Tried to equip item with global id {0} that is already equipped.",
                Logger.Args(itemId));

            InventoryBehavior.AddItem(inventoryItem); // CLASSIC: back to the backpack, not lost.
            return false;
        }

        // If this object is a mount, we'll also want to update the mount owner behavior.
        if (slot.SlotType == EquipmentSlotType.Mount) {
            EquipMount(template, inventoryItem);
        }
        if (slot.SlotType == EquipmentSlotType.Deck) {
            InformSpellbookOfNewDeck(template, inventoryItem.m_globalID);
        }
        if (slot.SlotType == EquipmentSlotType.Pet) {
            EquipPet(template, inventoryItem);
        }

        // Persistent save.
        WizardCollection.UpdateCharacterItems(this);

        // Debug log.
        Logger.Debug("{0} equips item {1}", Logger.Args(PlayerNameBehavior.GetWizardName(), itemId));

        equipEffects = CharacterEffectHelper.AddEffectsToWizard(this, template);

        return true;
    }

    public bool EquipmentToInventoryTransfer(ulong itemId, out List<GameEffectBase> unequipEffects) {
        var result = WizardCollection.WithCharacterLock(CharId, () => {
            if (WizardCollection.IsInventorySnapshotUncertain(this)) return (Success: false, Effects: (List<GameEffectBase>)null);
            var success = EquipmentToInventoryTransferLocked(itemId, out var removed);
            return (Success: success, Effects: removed);
        });
        unequipEffects = result.Effects;
        return result.Success;
    }

    private bool EquipmentToInventoryTransferLocked(ulong itemId, out List<GameEffectBase> unequipEffects) {
        unequipEffects = null;

        // Get the actual item. We'll also grab the template to remove the effects from the wizard.
        var item = EquipmentBehavior.EquippedItems.FirstOrDefault(i => i.m_globalID == itemId);
        var template = ItemHelper.GetItemTemplate(item);
        var slot = ItemHelper.GetItemSlot(template);

        // Remove the item from the equipment.
        var unequipResult = EquipmentBehavior.UnequipItem(itemId);
        if (!unequipResult) {
            Logger.Warning("Tried to unequip item with global id {0} that is not equipped.",
                Logger.Args(itemId));

            return false;
        }

        // Add the item to the inventory.
        var invAddResult = InventoryBehavior.AddItem(item);
        if (!invAddResult) {
            Logger.Warning("Tried to add item with global id {0} to inventory, but it already exists.",
                Logger.Args(itemId));

            return false;
        }

        // If this object is a mount, we'll also want to update the mount owner behavior.
        if (slot.SlotType == EquipmentSlotType.Mount) {
            UnequipMount();
        }
        if (slot.SlotType == EquipmentSlotType.Pet) {
            UnequipPet();
        }

        // Persistent save.
        WizardCollection.UpdateCharacterItems(this);

        // Debug log.
        Logger.Debug("{0} unequips item {1}",
            Logger.Args(PlayerNameBehavior.GetWizardName(), itemId));

        unequipEffects = CharacterEffectHelper.RemoveEffectsFromWizard(this, template);

        return true;
    }

    /// <summary>
    /// CLASSIC: takes the equipped mount off for a duel and saves which one it was. Returns its id, or, when no mount
    /// is equipped, the one a session that dropped mid-fight already stowed (a rejoin); 0 when there is none.
    /// <paramref name="removedEffects"/> is set only when a mount was taken off here.
    /// </summary>
    internal ulong StowMountForDuel(out byte slot, out List<GameEffectBase> removedEffects) {
        slot = 255;
        removedEffects = null;

        var mount = EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Mount);
        if (mount is null) {
            return CombatStowedMountId;
        }

        var mountId = mount.m_globalID;
        slot = EquipmentBehavior.GetSlotOfItem(mountId);
        if (!EquipmentToInventoryTransfer(mountId, out removedEffects)) {
            return 0;
        }

        CombatStowedMountId = mountId;
        WizardCollection.UpdateCharacterCombatStowedMount(this);

        return mountId;
    }

    /// <summary>
    /// CLASSIC: the duel is over (or the wizard attached with a mount still stowed from one): puts the stowed mount
    /// back on and forgets it. In a zone without mounts it becomes the interior stow, worn again outdoors. Returns
    /// true when the mount was equipped here (<paramref name="equipEffects"/> then holds its effects).
    /// </summary>
    internal bool RestoreDuelStowedMount(bool zoneDisallowsMounts, out List<GameEffectBase> equipEffects) {
        equipEffects = null;
        var mountId = CombatStowedMountId;
        if (mountId == 0) {
            return false;
        }

        CombatStowedMountId = 0;
        WizardCollection.UpdateCharacterCombatStowedMount(this);

        // Sold, traded or worn again since: nothing to restore.
        if (InventoryBehavior.GetItem(mountId) is null || EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Mount) is not null) {
            return false;
        }

        if (zoneDisallowsMounts) {
            InteriorStowedMountId = mountId;
            WizardCollection.UpdateCharacterInteriorStowedMount(this);

            return false;
        }

        return InventoryToEquipmentTransfer(mountId, out equipEffects, out _);
    }

    public bool AddSnack(ulong snackTemplateId, out ClientPetSnackItem snackObj) {
        if (PetSnackBehavior.HasSnack(snackTemplateId)) {
            snackObj = PetSnackBehavior.GetSnack(snackTemplateId);
        }
        else {
            snackObj = (ClientPetSnackItem) CoreObjectFactory.FinalizeCoreObject(snackTemplateId);
            snackObj.m_characterId = (GID) CharId;
            snackObj.m_quantity = 1;
        }

        return AddSnack(snackObj);
    }

    public bool AddSnack(ClientPetSnackItem snack) {
        if (snack is null) {
            Logger.Warning("Cannot add snack to snack bag because that snack does not exist.");
            return false;
        }

        CoreObjectFactory.InitializeCoreObjectBehaviors(snack, snack.m_templateID);

        // Ensure that the item is associated with this Wizard.
        snack.m_characterId = (GID) CharId;

        var success = PetSnackBehavior.AddSnack(snack);
        if (!success) {
            Logger.Warning("Could not add snack {0} to player {1}'s snackbag.",
                Logger.Args(snack.m_globalID, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        if (snack.m_quantity > 1) {
            // Persistent save.
            WizardPetSnackCollection.UpdateSnack(snack);
            WizardCollection.UpdateCharacterItems(this);

            return true;
        }

        // Persistent save.
        WizardPetSnackCollection.AddSnack(snack);
        WizardCollection.UpdateCharacterItems(this);

        return true;
    }

    public bool RemoveSnack(ulong globalId, out ClientPetSnackItem snack) {
        if (!PetSnackBehavior.RemoveSnack(globalId, out snack)) {
            Logger.Warning("Could not remove snack with global ID {0} from player {1}'s snackbag.",
                Logger.Args(globalId, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        if (snack.m_quantity <= 0) {
            // Persistent save.
            WizardPetSnackCollection.RemoveSnack(snack);
            WizardCollection.UpdateCharacterItems(this);

            return true;
        }

        // Persistent save.
        WizardPetSnackCollection.UpdateSnack(snack);
        WizardCollection.UpdateCharacterItems(this);

        return true;
    }

    public bool AddReagent(ulong reagentTemplateId, out ClientReagentItem reagentObj) {
        reagentObj = null;
        if (WizardCollection.IsInventorySnapshotUncertain(this)) return false; // CLASSIC: reload after uncertain ACK.
        // CLASSIC: return the saved stack's native identity/count, not a newly allocated duplicate identity.
        var candidate = AlchemyBehavior?.GetReagent(reagentTemplateId)
            ?? CoreObjectFactory.FinalizeCoreObject(reagentTemplateId) as ClientReagentItem;
        return WizardReagentCollection.AddReagent(this, candidate, out reagentObj);
    }

    public bool AddReagent(ClientReagentItem reagent) {
        if (WizardCollection.IsInventorySnapshotUncertain(this)) return false; // CLASSIC
        if (reagent is null) {
            Logger.Warning("Cannot add reagent to reagent bag because that reagent does not exist.");

            return false;
        }

        // CLASSIC: the saved quantity and wizard bag reference commit together before live publication.
        // The candidate may alias a five-copy live stack; this request adds one, never its six-copy snapshot.
        if (!WizardReagentCollection.AddReagent(this, reagent, out var updated)) {
            Logger.Warning("Could not add reagent {0} to player {1}'s reagent bag.",
                Logger.Args(reagent.m_globalID, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        // CLASSIC: node pickup callers serialize their input after this returns. Preserve that reference while
        // reporting the actual owned native stack, and only change it after the save is acknowledged.
        reagent.m_globalID = updated.m_globalID;
        reagent.m_permID = updated.m_permID;
        reagent.m_characterId = updated.m_characterId;
        reagent.m_quantity = updated.m_quantity;
        return true;
    }

    public bool RemoveReagent(ulong globalId, out ClientReagentItem reagent) {
        reagent = null;
        if (WizardCollection.IsInventorySnapshotUncertain(this)) return false; // CLASSIC
        // CLASSIC: stage the decrement from the saved count; last-copy removal writes zero/deletion once.
        if (!WizardReagentCollection.RemoveReagent(this, globalId, out reagent)) {
            Logger.Warning("Could not remove reagent with global ID {0} from player {1}'s reagent bag.",
                Logger.Args(globalId, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        return true;
    }

    public void SetNameOverride(string newName) {
        PlayerNameBehavior.NameOverride = newName;

        // Persistent save.
        WizardCollection.UpdateCharacterNameOverride(this);
    }

    public void SetBadgeOverride(string newBadge) {
        PlayerNameBehavior.BadgeTitle = newBadge;

        // Persistent save.
        WizardCollection.UpdateCharacterBadgeOverride(this);
    }

    public bool LearnSpell(Spell spell) {
        // CLASSIC: ordinary rewards and training must never publish learned state before the saved acknowledgement.
        if (WizardSpellbookTransactions.IsActive)
            return spell is not null && WizardSpellbookTransactions.TryLearn(this, spell.m_templateID, out _)
                == SpellbookMutationStatus.Committed;

        if (SpellbookBehavior.LearnedSpellTemplateIds.Contains(spell.m_templateID)) {
            Logger.Debug("{0} Tried to learn spell with template ID {1} that is already known.", // CLASSIC: harmless
                Logger.Args(PlayerNameBehavior.GetWizardName(), spell.m_templateID));

            return false;
        }

        SpellbookBehavior.AddSpellToBook(spell);

        // Persistent save.
        WizardCollection.LearnSpell(this, spell.m_templateID);

        return true;
    }

    public bool UnlearnSpell(uint spellTemplateId) {
        if (!SpellbookBehavior.LearnedSpellTemplateIds.Contains(spellTemplateId)) {
            Logger.Warning("{0} Tried to unlearn spell with template ID {1} that is not known.",
                Logger.Args(PlayerNameBehavior.GetWizardName(), spellTemplateId));

            return false;
        }

        SpellbookBehavior.RemoveSpellFromBook(spellTemplateId);

        // Persistent save.
        WizardCollection.UnlearnSpell(this, spellTemplateId);

        return true;
    }

    public void AddTemporarySpell(Spell spell) {
        SpellbookBehavior.AddTemporarySpellToBook(spell);
    }

    public void RemoveTemporarySpell(uint spellTemplateId) {
        SpellbookBehavior.RemoveTemporarySpellFromBook(spellTemplateId);
    }

    public bool AddSpellToDeck(uint spellTemplateId, ulong deckId)
        => AddSpellToDeck(spellTemplateId, deckId, WizardItemCollection.AddSpellToDeck);

    internal bool AddSpellToDeck(uint spellTemplateId, ulong deckId, Func<ulong, uint, bool> persist)
        => ChangeRegularDeckSpell(spellTemplateId, deckId, add: true, persist);

    public bool RemoveSpellFromDeck(uint spellTemplateId, ulong deckId)
        => RemoveSpellFromDeck(spellTemplateId, deckId, WizardItemCollection.RemoveSpellFromDeck);

    internal bool RemoveSpellFromDeck(uint spellTemplateId, ulong deckId, Func<ulong, uint, bool> persist)
        => ChangeRegularDeckSpell(spellTemplateId, deckId, add: false, persist);

    // CLASSIC: validate a detached list; publish to the item and equipped book only after the item save commits.
    // A refused lookup leaves both live lists untouched. A thrown save has uncertain status and is never retried.
    private bool ChangeRegularDeckSpell(uint spellTemplateId, ulong deckId, bool add, Func<ulong, uint, bool> persist) {
        var item = InventoryBehavior.Items.FirstOrDefault(candidate => candidate.m_globalID == deckId);
        var equipped = item is null;
        item ??= EquipmentBehavior.EquippedItems.FirstOrDefault(candidate => candidate.m_globalID == deckId);
        if (item is null || !CoreObjectFactory.FindBehaviorInstance<DeckBehavior>(item, out var deck)) {
            Logger.Warning("Could not find deck {0} in player {1}'s inventory or equipment.",
                Logger.Args(deckId, PlayerNameBehavior.GetWizardName()));
            return false;
        }

        List<SpellData> cards;
        if (equipped) {
            // Use the real spellbook's existing rules without exposing an uncommitted mutation to live readers.
            var candidate = new ServerWizSpellbookBehavior {
                PrimarySchool = SpellbookBehavior.PrimarySchool,
                GenericMaxRank = SpellbookBehavior.GenericMaxRank,
                SchoolMaxRank = SpellbookBehavior.SchoolMaxRank,
                GenericMaxInstances = SpellbookBehavior.GenericMaxInstances,
                SchoolMaxInstances = SpellbookBehavior.SchoolMaxInstances,
                MaxSpells = SpellbookBehavior.MaxSpells,
                MaxTreasureCards = SpellbookBehavior.MaxTreasureCards,
                DeckTemplate = SpellbookBehavior.DeckTemplate,
                SpellList = CopyRegularDeckCards(SpellbookBehavior.SpellList),
            };
            if (!(add ? candidate.AddSpellToDeck(spellTemplateId) : candidate.RemoveSpellFromDeck(spellTemplateId))) return false;
            cards = candidate.SpellList;
        }
        else {
            cards = CopyRegularDeckCards(deck.m_spellList);
            if (add && Classic.ClassicDeckRules.DeckTemplateOf((uint) item.m_templateID) is { } template) {
                var refusal = Classic.ClassicDeckRules.CanAdd(template, cards, spellTemplateId,
                    id => CoreObjectFactory.GetCoreTemplate(id) as SpellTemplate);
                if (refusal != Classic.DeckAddRefusal.None) return false;
            }

            var card = cards.FirstOrDefault(candidate => candidate.m_templateID == spellTemplateId);
            if (add) {
                if (card is null) cards.Add(new SpellData { m_templateID = spellTemplateId, m_quantity = 1 });
                else card.m_quantity = checked(card.m_quantity + 1);
            }
            else {
                if (card is null) return false;
                if (card.m_quantity > 1) card.m_quantity--;
                else cards.Remove(card);
            }
        }

        bool saved;
        try {
            saved = persist(deckId, spellTemplateId);
        }
        catch (Exception) {
            Logger.Error("Saved deck change threw for player {0}, deck {1}, spell {2}; commit status is uncertain and the change will not be retried.",
                Logger.Args(CharId, deckId, spellTemplateId));
            throw;
        }
        if (!saved) {
            Logger.Warning("Saved deck change refused for player {0}, deck {1}, spell {2}, add {3}.",
                Logger.Args(CharId, deckId, spellTemplateId, add));
            return false;
        }

        deck.m_spellList = cards;
        if (equipped) SpellbookBehavior.SpellList = cards;
        return true;
    }

    private static List<SpellData> CopyRegularDeckCards(IEnumerable<SpellData> cards)
        => cards is null ? [] : JsonConvert.DeserializeObject<List<SpellData>>(JsonConvert.SerializeObject(cards));

    /// <summary>
    /// Adds a treasure card to a deck, consuming one copy from the player's treasure card book.
    /// </summary>
    /// <param name="spellTemplateId">The template ID of the spell.</param>
    /// <param name="deckId">The global ID of the deck item.</param>
    /// <returns>True if the card was added successfully.</returns>
    public bool AddTreasureCardToDeck(uint spellTemplateId, ulong deckId) {
        // CLASSIC: the card goes into the deck's Treasure Cards (the ledger, SpellbookBehavior.DeckTreasureCards), not
        // into the deck's card list, where a Treasure Card of a known spell became a regular card that was never spent
        // and took no deck place. Book and ledger change in one save; the deck's Treasure Card places are its
        // template's m_maxTreasureCards.
        var deck = HeldDeck(deckId);
        if (deck is null) {
            Logger.Warning("Player {0} tried to add treasure card {1} to deck {2} they do not hold.",
                Logger.Args(PlayerNameBehavior.GetWizardName(), spellTemplateId, deckId));

            return false;
        }

        var places = Classic.ClassicDeckRules.DeckTemplateOf((uint) deck.m_templateID)?.m_maxTreasureCards ?? SpellbookBehavior.MaxTreasureCards;

        return WizardCollection.MoveTreasureCardToDeck(this, deckId, spellTemplateId, places);
    }

    /// <summary>
    /// Removes a treasure card from a deck, returning it to the player's treasure card book
    /// (unless <paramref name="destroy"/> is true).
    /// </summary>
    /// <param name="spellTemplateId">The template ID of the spell.</param>
    /// <param name="deckId">The global ID of the deck item.</param>
    /// <param name="destroy">If true, the card is destroyed instead of returned to the book.</param>
    /// <returns>True if the card was removed successfully.</returns>
    public bool RemoveTreasureCardFromDeck(uint spellTemplateId, ulong deckId, bool destroy = false)
        // CLASSIC: only a card the ledger has in that deck (one that went in as a Treasure Card) comes out, so a regular
        // deck card can never be turned into a book Treasure Card.
        => WizardCollection.MoveTreasureCardFromDeck(this, deckId, spellTemplateId, destroy);

    /// <summary>CLASSIC: a Treasure Card of the deck was cast (or spent as an enchantment): it is gone for good.</summary>
    public bool ConsumeDeckTreasureCard(uint spellTemplateId, ulong deckId)
        => WizardCollection.MoveTreasureCardFromDeck(this, deckId, spellTemplateId, destroy: true);

    // CLASSIC: a deck item this wizard holds (backpack or equipped), or null.
    private WizClientObjectItem HeldDeck(ulong deckId) {
        var item = InventoryBehavior.Items.FirstOrDefault(i => i.m_globalID == deckId)
            ?? EquipmentBehavior.EquippedItems.FirstOrDefault(i => i.m_globalID == deckId);

        return item is not null && CoreObjectFactory.FindBehaviorInstance<DeckBehavior>(item, out _) ? item : null;
    }

    /// <summary>
    /// CLASSIC: once per wizard, moves the Treasure Cards older saves kept inside the card lists of the decks the
    /// wizard holds into the ledger: entries the client counts as Treasure Cards (template m_Treasure, an enchanted
    /// entry, a " TC" name) and entries of spells the wizard has not learned (the old server's rule for them). They
    /// leave the deck's card list, which then holds regular cards only. Returns how many copies moved.
    /// </summary>
    internal int MigrateDeckTreasureCards() {
        if (SpellbookBehavior is null || SpellbookBehavior.DeckTreasureLedgerVersion >= 1) {
            return 0;
        }

        var learned = SpellbookBehavior.LearnedSpellTemplateIds;
        var found = new Dictionary<ulong, Dictionary<uint, int>>();
        var decks = InventoryBehavior.Items.Concat(EquipmentBehavior.EquippedItems)
            .Where(item => item is not null).DistinctBy(item => (ulong) item.m_globalID).ToList();
        foreach (var deck in decks) {
            if (!CoreObjectFactory.FindBehaviorInstance<DeckBehavior>(deck, out var deckBehavior) || deckBehavior.m_spellList is null) {
                continue;
            }

            foreach (var entry in deckBehavior.m_spellList.Where(entry => entry is not null)) {
                if (!IsLegacyDeckTreasure(entry, learned)) {
                    continue;
                }

                var deckId = (ulong) deck.m_globalID;
                if (!found.TryGetValue(deckId, out var cards)) {
                    cards = [];
                    found[deckId] = cards;
                }

                cards[entry.m_templateID] = (cards.TryGetValue(entry.m_templateID, out var n) ? n : 0) + (int) entry.m_quantity;
            }
        }

        if (!WizardCollection.RecordMigratedDeckTreasureCards(this, found)) {
            return 0;
        }

        var moved = 0;
        foreach (var (deckId, cards) in found) {
            foreach (var (templateId, copies) in cards) {
                for (var i = 0; i < copies; i++) {
                    RemoveSpellFromDeck(templateId, deckId);
                    moved++;
                }
            }
        }

        if (moved > 0) {
            Logger.Information("Wizard {0}: {1} deck Treasure Card(s) moved to the Treasure Card ledger.", Logger.Args(CharId, moved));
        }

        return moved;
    }

    // CLASSIC: a deck entry of an older save that was a Treasure Card.
    internal static bool IsLegacyDeckTreasure(SpellData entry, ICollection<uint> learned, Func<uint, SpellTemplate> lookup = null) {
        var template = lookup is null ? CoreObjectFactory.GetCoreTemplate(entry.m_templateID) as SpellTemplate : lookup(entry.m_templateID);
        if (Classic.ClassicDeckRules.IsTreasureEntry(template, entry.m_enchantment)
            || template?.m_name?.EndsWith(" TC", StringComparison.Ordinal) == true) {
            return true;
        }

        return learned is { Count: > 0 } && !learned.Contains(entry.m_templateID);
    }

    public ObjState EnterState(string stateName)
        => ObjectStateBehavior.SetState(stateName);

    public bool AddDynamod(string zoneName, string clientTag, string modState) {
        DynamodSet ??= new DynamodSet(CharId);

        var dynamod = new Dynamod {
            ZoneName = zoneName,
            ClientTag = clientTag,
            ModState = modState
        };

        var addSuccess = DynamodSet.AddDynamod(dynamod);

        if (!addSuccess) {
            Logger.Warning("Could not add Dynamod to player {0}'s DynamodSet.",
                Logger.Args(PlayerNameBehavior.GetWizardName()));

            return false;
        }

        // Persistent save.
        DynamodCollection.UpdateDynamodSet(DynamodSet, dynamod);

        return true;
    }

    // CLASSIC: true when this wizard has turned the client tag to the state in the zone (a teleport stone's discovery).
    public bool HasDynamod(string zoneName, string clientTag, string modState)
        => DynamodSet?.Dynamods?.Any(dynamod => dynamod is not null
            && string.Equals(dynamod.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(dynamod.ClientTag, clientTag, StringComparison.OrdinalIgnoreCase)
            && string.Equals(dynamod.ModState, modState, StringComparison.OrdinalIgnoreCase)) == true;

    public bool RemoveDynamod(string clientTag) {
        if (DynamodSet is null) {
            return false;
        }

        var removeSuccess = DynamodSet.RemoveDynamod(clientTag);

        if (!removeSuccess) {
            return false;
        }

        // Persistent save.
        DynamodCollection.RemoveDynamod(DynamodSet.CharId, clientTag);

        return true;
    }

    public bool AddPendingFriendRequest(ulong characterId) {
        var addSuccess = FriendsBehavior.AddPendingFriendRequest(characterId);
        if (!addSuccess) {
            Logger.Warning("Could not add pending friend request ({0}) for player {1}.",
                Logger.Args(characterId, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        return true;
    }

    public bool AddOrRepairRelationship(ulong newFriendId) {
        // Create a new relationship. We're then going to add it to the collection. The collection will return
        // an existing, restored relationship if one exists. Otherwise, it'll return what we send it as a parameter.
        var newRelationship = new Relationship {
            FirstPlayerId = this.CharId,
            SecondPlayerId = newFriendId,
            RelationshipId = RandomGen.GenerateGUID(),
            RelationshipEpochInSeconds = (uint) DateTimeOffset.Now.ToUnixTimeSeconds(),
        };
        var newOrExistingRelationship = BuddyRelationshipCollection.AddRelationship(newRelationship);

        // If the relationship epoch is different, this is a restored relationship.
        if (newRelationship.RelationshipEpochInSeconds != newOrExistingRelationship.RelationshipEpochInSeconds) {
            // This is an existing relationship that has now been restored.
            Logger.Debug("Player {0} has restored a relationship with player {1}.",
                Logger.Args(PlayerNameBehavior.GetWizardName(), newFriendId));

            FriendsBehavior.AddOrUpdateRelationship(newOrExistingRelationship);
        }
        else {
            // Otherwise, this is a new relationship.
            Logger.Debug("Player {0} has added player {1} as a friend.",
                Logger.Args(PlayerNameBehavior.GetWizardName(), newFriendId));

            FriendsBehavior.AddOrUpdateRelationship(newRelationship);
        }

        return true;
    }

    public bool AddOrRepairRelationship(Relationship relationship) {
        FriendsBehavior.AddOrUpdateRelationship(relationship);

        return true;
    }

    public bool RemovePendingFriendRequest(ulong friendId) {
        var removeSuccess = FriendsBehavior.RemovePendingFriendRequest(friendId);
        if (!removeSuccess) {
            Logger.Warning("Could not remove pending friend request ({0}) for player {1}.",
                Logger.Args(friendId, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        return true;
    }

    public bool RemoveFriend(ulong friendId) {
        var relationship = FriendsBehavior.Breakup(friendId);
        if (relationship is null) {
            Logger.Warning("Could not remove friend ({0}) for player {1}.",
                Logger.Args(friendId, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        // Persistent save.
        BuddyRelationshipCollection.UpdateRelationship(relationship);

        return true;
    }

    public bool IgnorePlayer(ulong playerId) {
        // CLASSIC: the ignore is this wizard's, written to the shared row by owner (IgnoreRules), and this wizard's copy
        // of the row is replaced with the stored one. It used to save this wizard's whole copy, undoing anything the
        // other wizard had written to the row since this login.
        var relationship = BuddyRelationshipCollection.SetIgnore(this.CharId, playerId, ignore: true);
        if (relationship is null) {
            Logger.Warning("Could not ignore player ({0}) for player {1}.",
                Logger.Args(playerId, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        FriendsBehavior.MirrorRelationship(relationship);

        return true;
    }

    public bool UnignorePlayer(ulong playerId) {
        if (!FriendsBehavior.HasPlayerBlocked(this.CharId, playerId)) {
            Logger.Warning("Could not unignore player ({0}) for player {1}.",
                Logger.Args(playerId, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        // CLASSIC: lifts this wizard's ignore only (see IgnorePlayer).
        var relationship = BuddyRelationshipCollection.SetIgnore(this.CharId, playerId, ignore: false);
        if (relationship is null) {
            FriendsBehavior.Unignore(this.CharId, playerId);

            return true;
        }

        FriendsBehavior.MirrorRelationship(relationship);

        return true;
    }

    public bool AddQuest(QuestInstance quest) {
        // CLASSIC: originals and journal publish together only after one acknowledged fresh write.
        if (ClassicQuestEngine.IsActive)
            return WizardQuestTransactions.TryAdd(this, quest, out _) != QuestMutationStatus.Refused;
        var addSuccess = QuestBehavior.AddQuest(quest);
        if (!addSuccess) {
            Logger.Warning("Could not add quest {0} for player {1}.",
                Logger.Args(quest.QuestName, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        // Persistent save.
        WizardCollection.UpdateCharacterQuestBehavior(this);
        QuestInstanceCollection.AddQuestInstance(quest);

        return true;
    }

    public bool RemoveQuest(string questName) {
        // CLASSIC: use the caller's held full identity rather than deleting an arbitrary name match.
        if (ClassicQuestEngine.IsActive)
            return WizardQuestTransactions.TryRemove(this, WizardQuestTransactions.Held(this, questName), out _) != QuestMutationStatus.Refused;
        var removeSuccess = QuestBehavior.RemoveQuest(questName);
        if (!removeSuccess) {
            Logger.Warning("Could not remove quest {0} for player {1}.",
                Logger.Args(questName, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        // Persistent save.
        WizardCollection.UpdateCharacterQuestBehavior(this);
        QuestInstanceCollection.RemoveQuestInstance(CharId, questName);

        return true;
    }

    public bool HasQuest(string questName)
        => QuestBehavior.HasQuest(questName);

    public bool HasCompletedQuest(string questName)
        => QuestBehavior.HasCompletedQuest(questName);

    public bool CompleteQuest(string questName) {
        // CLASSIC: exact original deletion and selected completion marker share one acknowledged write.
        if (ClassicQuestEngine.IsActive)
            return WizardQuestTransactions.TryComplete(this, WizardQuestTransactions.Held(this, questName), out _) != QuestMutationStatus.Refused;
        var questStatus = QuestBehavior.CompleteQuest(questName);
        if (!questStatus) {
            Logger.Warning("Could not mark quest {0} as completed for player {1}.",
                Logger.Args(questName, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        // Persistent save.
        WizardCollection.UpdateCharacterQuestBehavior(this);
        QuestInstanceCollection.RemoveQuestInstance(CharId, questName);

        return questStatus;
    }

    public bool StartQuestGoal(string questName, string goalName) {
        if (ClassicQuestEngine.IsActive) { // CLASSIC: preserve held goal aliases after the saved original ACK.
            var held = WizardQuestTransactions.Held(this, questName);
            return WizardQuestTransactions.TryStartGoal(this, held, WizardQuestTransactions.HeldGoal(held, goalName), out _) != QuestMutationStatus.Refused;
        }
        var startSuccess = QuestBehavior.StartQuestGoal(questName, goalName);
        if (!startSuccess) {
            Logger.Warning("Could not start quest goal {0} for quest {1} for player {2}.",
                Logger.Args(goalName, questName, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        var questId = QuestBehavior.CurrentQuestInstances
            .FirstOrDefault(q => q is not null && q.QuestName == questName).ID;
        if (questId is 0) {
            Logger.Error("Could not find quest ID for quest {0} for player {1}.",
                Logger.Args(questName, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        // Persistent save.
        WizardCollection.UpdateCharacterQuestBehavior(this);
        QuestInstanceCollection.StartQuestGoal(questId, goalName);

        return true;
    }

    public bool IncrementQuestGoal(string questName, string goalName) {
        if (ClassicQuestEngine.IsActive) { // CLASSIC
            var held = WizardQuestTransactions.Held(this, questName);
            return WizardQuestTransactions.TryIncrementGoal(this, held, WizardQuestTransactions.HeldGoal(held, goalName), out _) != QuestMutationStatus.Refused;
        }
        var incrementSuccess = QuestBehavior.IncrementQuestGoal(questName, goalName);
        if (!incrementSuccess) {
            Logger.Warning("Could not increment quest goal {0} for quest {1} for player {2}.",
                Logger.Args(goalName, questName, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        var questId = QuestBehavior.CurrentQuestInstances
            .FirstOrDefault(q => q is not null && q.QuestName == questName).ID;
        if (questId is 0) {
            Logger.Error("Could not find quest ID for quest {0} for player {1}.",
                Logger.Args(questName, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        // Persistent save.
        WizardCollection.UpdateCharacterQuestBehavior(this);
        QuestInstanceCollection.IncrementQuestGoal(questId, goalName);

        return true;
    }

    public bool CompleteQuestGoal(string questName, string goalName) {
        if (ClassicQuestEngine.IsActive) { // CLASSIC
            var held = WizardQuestTransactions.Held(this, questName);
            return WizardQuestTransactions.TryCompleteGoal(this, held, WizardQuestTransactions.HeldGoal(held, goalName), out _) != QuestMutationStatus.Refused;
        }
        var completeSuccess = QuestBehavior.CompleteQuestGoal(questName, goalName);
        if (!completeSuccess) {
            Logger.Warning("Could not complete quest goal {0} for quest {1} for player {2}.",
                Logger.Args(goalName, questName, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        var questId = QuestBehavior.CurrentQuestInstances
            .FirstOrDefault(q => q is not null && q.QuestName == questName).ID;
        if (questId is 0) {
            Logger.Error("Could not find quest ID for quest {0} for player {1}.",
                Logger.Args(questName, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        // Persistent save.
        WizardCollection.UpdateCharacterQuestBehavior(this);
        QuestInstanceCollection.CompleteQuestGoal(questId, goalName);

        return true;
    }

    public bool HasRegistryValue(string key)
        => QuestBehavior.HasRegistryValue(key);

    public bool HasQuestRegistryValue(string questName, string key)
        => QuestBehavior.HasQuestRegistryValue(questName, key);

    public ulong GetRegistryValue(string key)
        => QuestBehavior.GetRegistryValue(key);

    public ulong GetQuestRegistryValue(string questName, string key)
        => QuestBehavior.GetQuestRegistryValue(questName, key);

    public bool SetRegistryValue(string key, ulong value) {
        if (ClassicQuestEngine.IsActive) // CLASSIC: change only the selected fresh registry entry.
            return WizardQuestTransactions.TrySetRegistry(this, key, value, out _) != QuestMutationStatus.Refused;
        var setSuccess = QuestBehavior.SetRegistryValue(key, value);
        if (!setSuccess) {
            Logger.Warning("Could not set registry value {0} for player {1}.",
                Logger.Args(key, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        // Persistent save.
        WizardCollection.UpdateCharacterQuestBehavior(this);

        return true;
    }

    public bool SetQuestRegistryValue(string questName, string key, ulong value) {
        if (ClassicQuestEngine.IsActive) // CLASSIC
            return WizardQuestTransactions.TrySetQuestRegistry(this, questName, key, value, out _) != QuestMutationStatus.Refused;
        var setSuccess = QuestBehavior.SetQuestRegistryValue(questName, key, value);
        if (!setSuccess) {
            Logger.Warning("Could not set quest registry value {0} for quest {1} for player {2}.",
                Logger.Args(key, questName, PlayerNameBehavior.GetWizardName()));

            return false;
        }

        // Persistent save.
        WizardCollection.UpdateCharacterQuestBehavior(this);

        return true;
    }

    public void UpdatePotions(Single newPotionCharge, Single newPotionMax)
        => WizardPotionTransactions.TrySetPotions(this, newPotionCharge, newPotionMax, out _);

    internal void AfterDatabaseLoad() {
        AfterDatabaseLoadWizardGameStats();
        AfterDatabaseLoadSpellbookBehavior();
        AfterDatabaseloadMountOwnerBehavior();
        AfterDatabaseLoadPetOwnerBehavior();
        AfterDatabaseLoadAlchemyBehavior();
        AfterDatabaseLoadQuestBehavior();

        ObjectStateBehavior ??= new ServerObjectStateBehavior("PlayerMobileStates");
        FriendsBehavior ??= new ServerFriendBehavior();
        QuestBehavior ??= new ServerQuestBehavior();
        DynamodSet ??= new DynamodSet(CharId);
    }

    private void EquipMount(WizItemTemplate template, WizClientObjectItem item) {
        var mountEquipSuccess = MountOwnerBehavior.EquipMount(template, item);
        if (!mountEquipSuccess) {
            Logger.Warning("Could not equip mount {0} to player {1}.",
                Logger.Args(template.m_objectName, PlayerNameBehavior.GetWizardName()));

            return;
        }

        // Persistent save.
        WizardCollection.UpdateCharacterMount(this);
    }

    private void UnequipMount() {
        MountOwnerBehavior.UnequipMount();

        // Persistent save.
        WizardCollection.UpdateCharacterMount(this);
    }

    private void EquipPet(WizItemTemplate template, WizClientObjectItem item) {
        PetOwnerBehavior.EquipPet(template, item);

        // Persistent save.
        WizardCollection.UpdateCharacterPetOwnerBehavior(this);
    }

    private void UnequipPet() {
        PetOwnerBehavior.UnequipPet();

        // Persistent save.
        WizardCollection.UpdateCharacterPetOwnerBehavior(this);
    }

    private void InformSpellbookOfNewDeck(WizItemTemplate template, ulong deckGlobalId) {
        // The caller of this method has already equipped the deck to the player.
        // This method just updates the spellbook behavior to reflect the new deck.

        // Get the actual item from equipment.
        var deckItem = EquipmentBehavior.EquippedItems.FirstOrDefault(i => i.m_globalID == deckGlobalId);
        if (deckItem is null) {
            Logger.Error("Could not find deck item with global ID {0}.",
                Logger.Args(deckGlobalId));

            return;
        }

        // Get the deck behavior.
        if (!CoreObjectFactory.FindBehaviorInstance<DeckBehavior>(deckItem, out var deckBehavior)) {
            Logger.Error("Could not find deck behavior for item with global ID {0}.",
                Logger.Args(deckGlobalId));

            return;
        }

        var deckEquipSuccess = SpellbookBehavior.EquipDeck(template, deckBehavior);
        if (!deckEquipSuccess) {
            Logger.Warning("Could not equip deck {0} to player {1}.",
                Logger.Args(template.m_objectName, PlayerNameBehavior.GetWizardName()));

            return;
        }
    }

    private void InitializeDefaultInventory()
        => InventoryBehavior = new ServerWizInventoryBehavior {
            Items = [],
            InventoryItemIds = []
        };

    /// <summary>
    /// Adds the starter kit items (config-driven) to the inventory and database.
    /// Called on first attach: in the tutorial, or on first login when the tutorial is disabled.
    /// </summary>
    public List<WizClientObjectItem> GrantStarterItems(IEnumerable<ulong> templateIds) {
        var itemsToAdd = new List<WizClientObjectItem>();
        foreach (var templateId in templateIds) {
            var cObj = (WizClientObjectItem) CoreObjectFactory.FinalizeCoreObject(templateId);
            CoreObjectFactory.InitializeCoreObjectBehaviors(cObj, templateId);
            cObj.m_characterId = (GID) CharId;

            itemsToAdd.Add(cObj);
            InventoryBehavior.InventoryItemIds.Add(cObj.m_globalID);
            InventoryBehavior.Items.Add(cObj);
        }

        // The default pet must be created through the pet factory. 
        var defaultPet = ClassicStart.IsActive ? null : PetFactory.CreateHatchedPet(CharId, _defaultPetTemplateId); // CLASSIC: a 2009 wizard started without a pet.
        if (defaultPet is null && !ClassicStart.IsActive) { // CLASSIC
            Logger.Error("Could not create default pet (template {0}) for Wizard {1}.",
                Logger.Args(_defaultPetTemplateId, CharId));
        }
        else if (defaultPet is not null) { // CLASSIC
            // Add pet to inventory.
            itemsToAdd.Add(defaultPet);
            InventoryBehavior.InventoryItemIds.Add(defaultPet.m_globalID);
            InventoryBehavior.Items.Add(defaultPet);
        }

        // This is a different method that bulk uploads items to the database.
        var success = WizardItemCollection.AddDefaultItems(itemsToAdd);
        if (!success) {
            Logger.Error("Could not add default items for Wizard {0} to database.",
                Logger.Args(CharId));
        }

        WizardCollection.UpdateCharacterItems(this);

        return itemsToAdd;
    }

    private void InitializeDefaultEquipment()
        => EquipmentBehavior = new ServerWizEquipmentBehavior {
            SlotList = [],
            EquippedItemIds = [],
            EquippedItems = [],
        };

    private void InitializePlayerName(uint nameIndices) {
        PlayerNameBehavior = new ServerWizPlayerNameBehavior {
            NameIndices = nameIndices,
            UseRank = false,
            Gender = WizardAvatar.m_eGender,
            Race = WizardAvatar.m_eRace,
            ChatPermissions = 0,
            PvpIconId = 0,
            LocaleId = 0,
            FriendlyPlayer = false,
            Volunteer = false,
            GuildName = 0,
        };
    }

    private void InitializeMagicSchoolBehavior(MagicSchool school, byte level) {
        MagicSchoolBehavior = new ServerMagicSchoolBehavior {
            MagicSchool = school,
            // CLASSIC: the XP a wizard of this level starts with (0 at level 1). An ambient wizard is made at its level,
            // and the stats recalculation inside its construction read XP 0 for level N: 960 "XP/Level mismatch" lines
            // per start on live.
            ExperiencePoints = level > 1 ? MagicLevelsConfig.GetExperiencePointsAtLevel(level) : 0,
            Level = level,
            TrainingPoints = 0,
            OverflowXp = 0,
            LevelIsLocked = 0,
            EquippedTeleportEffect = 0,
        };
    }

    private void InitializeSpellbookBehavior() {
        SpellbookBehavior = new ServerWizSpellbookBehavior();
    }

    private void InitializeDefaultPetSnackBehavior() {
        PetSnackBehavior = new ServerPetSnackBehavior() {
            Snacks = [],
            SnackItemIds = []
        };
    }

    private void AfterDatabaseLoadSpellbookBehavior() {
        // Find the deck in our equipment.
        var idInSlot = EquipmentBehavior.SlotList.FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck)?.ItemId;
        if (idInSlot is null) {
            // Normal behavior; we just don't have a deck equipped.
            return;
        }

        // Get the actual item.
        var deckItem = EquipmentBehavior.EquippedItems.FirstOrDefault(i => i.m_globalID == idInSlot);
        if (deckItem is null) {
            Logger.Error("Could not find deck item with global ID {0}.",
                Logger.Args(idInSlot));

            return;
        }

        // Get the deck behavior.
        if (!CoreObjectFactory.FindBehaviorInstance<DeckBehavior>(deckItem, out var deckBehavior)) {
            Logger.Error("Could not find deck behavior for item with global ID {0}.",
                Logger.Args(idInSlot));

            return;
        }

        // Get the template. This gives us information like the max instance count, what school the deck is, etc.
        var deckTemplate = CoreObjectFactory.GetCoreTemplate(deckItem.m_templateID);
        if (deckTemplate is null) {
            Logger.Error("Could not find deck template with global ID {0}.",
                Logger.Args(idInSlot));

            return;
        }

        // Get the DeckBehaviorTemplate within the deck template.
        if (deckTemplate.m_behaviors.FirstOrDefault(b => b is DeckBehaviorTemplate) is not DeckBehaviorTemplate deckBehaviorTemplate) {
            Logger.Error("Could not find deck behavior template within deck template with global ID {0}.",
                Logger.Args(idInSlot));

            return;
        }

        // Finally, initialize the spellbook behavior with the deck behavior.
        SpellbookBehavior.InitializeProperties(deckBehaviorTemplate);
        SpellbookBehavior.InitializeSpells(deckBehavior);
    }

    private void InitializeMountOwnerBehavior() {
        MountOwnerBehavior = new ServerMountOwnerBehavior();
    }

    private void AfterDatabaseloadMountOwnerBehavior() {
        // FOund our mount in the equipment.
        var idInSlot = EquipmentBehavior.SlotList.FirstOrDefault(s => s.SlotType == EquipmentSlotType.Mount)?.ItemId;
        if (idInSlot is null) {
            // Normal behavior; we just don't have a mount equipped.
            return;
        }

        // Get the actual item.
        var mountItem = EquipmentBehavior.EquippedItems.FirstOrDefault(i => i.m_globalID == idInSlot);
        if (mountItem is null) {
            Logger.Error("Could not find mount item with global ID {0}.",
                Logger.Args(idInSlot));

            return;
        }

        // Get the template for this item.
        var mountTemplate = ItemHelper.GetItemTemplate(mountItem);
        MountOwnerBehavior.EquipMount(mountTemplate, mountItem);
    }

    private void InitializeWizardGameStats(MagicSchool school, byte level) {
        GameStats = new ServerWizGameStats(school, level);
        CharacterHelper.RecalculateGameStats(this);

        GameStats.m_currentHitpoints = GameStats.m_baseHitpoints;
        GameStats.m_currentMana = GameStats.m_baseMana;
    }

    private void InitializePetOwnerBehavior() {
        PetOwnerBehavior = new ServerPetOwnerBehavior {
            MaxSlots = 1
        };
        PetOwnerBehavior.SetEnergy(GameStats.m_energyMax);
    }

    private void InitializeAlchemyBehavior() {
        AlchemyBehavior = new ServerAlchemyBehavior() {
            Reagents = [],
            Recipes = [],
            CraftingSlots = [],
            ReagentItemIds = []
        };
    }

    private void AfterDatabaseLoadPetOwnerBehavior() {
        var magicSchool = MagicSchoolBehavior.MagicSchool;
        var level = MagicSchoolBehavior.Level;
        var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(magicSchool, level);
        var normMaxEnergy = baseStats.m_petEnergy;
        GameStats.m_energyMax = normMaxEnergy;

        if (PetOwnerBehavior is null) {
            PetOwnerBehavior = new ServerPetOwnerBehavior();
            PetOwnerBehavior.SetEnergy(GameStats.m_energyMax);

            return;
        }

        // Rebuild runtime MorphingSlots from persisted Eggs DTO.
        PetOwnerBehavior.RebuildRuntimeSlots();

        // If the last energy tick is in the future, don't bother.
        if (PetOwnerBehavior.LastEnergyTickEpoch > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) {
            return;
        }

        // Deduce how much energy has been regained since the last tick.
        var energyTickIntervalInSeconds = PetOwnerBehavior.EnergyTickIntervalInSeconds;
        var lastEnergyTickEpoch = PetOwnerBehavior.LastEnergyTickEpoch;
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var timeDifference = currentTime - lastEnergyTickEpoch;
        var energyRegained = timeDifference / energyTickIntervalInSeconds;

        // If the player has regained more energy than their max, set it to the max.
        if (PetOwnerBehavior.Energy + energyRegained > normMaxEnergy) {
            UpdateEnergy(normMaxEnergy);
        }
        else {
            UpdateEnergy((int) (PetOwnerBehavior.Energy + energyRegained));
        }
    }

    private void AfterDatabaseLoadWizardGameStats() {
        // CLASSIC: a wizard saved above the profile's cap plays at the cap. In memory only; the next level or XP save persists it.
        MagicSchoolBehavior.Level = ClassicRuntime.Rules.ClampLevel(MagicSchoolBehavior.Level, MagicLevelsConfig.MaxLevel);
        MagicSchoolBehavior.ExperiencePoints = ClassicRuntime.Rules.ClampXp(MagicSchoolBehavior.ExperiencePoints, MagicLevelsConfig.MaxLevelXp);

        var highestLevelWizard = Account.GetHighestLevelWizard();
        var highestLevelOnAcc = highestLevelWizard.MagicSchoolBehavior.Level;
        // CLASSIC: the account's highest level is capped the same way.
        highestLevelOnAcc = ClassicRuntime.Rules.ClampLevel(highestLevelOnAcc, MagicLevelsConfig.MaxLevel);

        GameStats.Level = MagicSchoolBehavior.Level;
        GameStats.MagicSchool = MagicSchoolBehavior.MagicSchool;
        GameStats.m_schoolID = (uint) MagicSchoolBehavior.MagicSchool;
        GameStats.m_highestCharacterLevelOnAccount = highestLevelOnAcc;
    }

    private void AfterDatabaseLoadAlchemyBehavior()
        => AlchemyBehavior ??= new ServerAlchemyBehavior() {
            Reagents = [],
            Recipes = [],
            CraftingSlots = [],
            ReagentItemIds = []
        };

    private void AfterDatabaseLoadQuestBehavior() {
        if (ClassicQuestEngine.IsActive) { // CLASSIC: durable references decide survivors, exact originals decide deletions.
            if (WizardQuestTransactions.ReconcileLoadedJournal(this, out _) == QuestMutationStatus.Refused) {
                WizardCollection.MarkInventorySnapshotUncertain(this);
                Logger.Error("Classic quest journal for {0} could not be safely loaded; refusing the session.", Logger.Args(CharId));
                throw new InvalidOperationException("The Classic quest journal needs an authoritative reload.");
            }
            return;
        }
        QuestBehavior ??= new ServerQuestBehavior();

        // Ensure that we have no duplicate quest instances active and remove completed quests.
        var uniqueQuests = new List<QuestInstance>();
        foreach (var quest in QuestBehavior.CurrentQuestInstances.ToList()) {
            // Remove duplicate quests.
            if (uniqueQuests.Any(q => q.QuestName == quest.QuestName)) {
                Logger.Warning("Found duplicate quest instance {0} for player {1}. Removing duplicate.",
                    Logger.Args(quest.QuestName, PlayerNameBehavior.GetWizardName()));

                QuestBehavior.RemoveQuestInstanceObject(quest); // CLASSIC
                QuestInstanceCollection.RemoveQuestInstance(CharId, quest.QuestName);

                continue;
            }

            // Remove completed quests.
            if (QuestBehavior.HasCompletedQuest(quest.QuestName)) {
                QuestBehavior.RemoveQuestInstanceObject(quest); // CLASSIC
                QuestInstanceCollection.RemoveQuestInstance(CharId, quest.QuestName);

                continue;
            }

            uniqueQuests.Add(quest);
        }

        // Prune stale quest IDs whose instance docs were removed above, and persist.
        QuestBehavior.ReplaceQuests(uniqueQuests); // CLASSIC: both lists at once, under the behavior's lock.
        WizardCollection.UpdateCharacterQuestBehavior(this);
    }

}
