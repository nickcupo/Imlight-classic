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
 */

using System;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Cantrips;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandModifyProtocol : CommandProtocol {

    private const uint SPEED_EFFECT_NAME = 6543894;

    internal override string Group { get; set; } = "mod";

    [Help("Gain one level.")]
    [Command("levelup")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    [Alias("lvlup")]
    private void LevelUpCommand() {
        // Inform the user of failure if the new level would be above the max level.
        var newLevel = (byte) (Context.Character.MagicSchoolBehavior.Level + 1);
        if (newLevel > MagicLevelsConfig.MaxLevel) {
            InformSenderClient($"You cannot set level higher than the max level ({MagicLevelsConfig.MaxLevel}).");

            return;
        }

        var msg = new CHARACTER_103_PROTOCOL.MSG_LEVELUP() { NewLevel = newLevel };
        Context.SessionActor.Tell(msg, null);
    }

    [Help("Set your level.")]
    [Command("level")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetLevelCommand(string level) {
        // Try to parse the level.
        if (!byte.TryParse(level, out var levelByte)) {
            InformSenderClient("Invalid level.");

            return;
        }

        // Inform the user of failure if the new level would be above the max level.
        if (levelByte > MagicLevelsConfig.MaxLevel) {
            InformSenderClient($"You cannot set level higher than the max level ({MagicLevelsConfig.MaxLevel}).");

            return;
        }

        var msg = new CHARACTER_103_PROTOCOL.MSG_LEVELUP() { NewLevel = levelByte };
        Context.SessionActor.Tell(msg, null);
    }

    [Help("Set your run speed.")]
    [Command("speed")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetSpeedCommand(string speedMultiplier) {
        // Try to parse the speed multiplier.
        if (!int.TryParse(speedMultiplier, out var speedMultiplierInt)) {
            InformSenderClient("Invalid speed multiplier.");

            return;
        }

        // Create the speed effect.
        var effect = new SpeedEffect() {
            m_speedMultiplier = speedMultiplierInt,
            m_effectNameID = SPEED_EFFECT_NAME,
            m_itemSlotID = 100
        };
        var coreObjectSerializer = new CoreObjectSerializer(
            behaviors: Imcodec.ObjectProperty.SerializerFlags.None
        );
        if (!coreObjectSerializer.Serialize(effect, 1, out var serializedEffect)) {
            InformSenderClient("Failed to serialize speed effect.");

            return;
        }

        // Create the network message and send it.
        var networkMessage = new GAME_5_PROTOCOL.MSG_ADDEFFECT() {
            GameObjectID = Context.Character.GameObjectID,
            EffectData = serializedEffect
        };
        Context.SessionActor.Tell(networkMessage, null);

        InformSenderClient($"Increased speed multiplier by {speedMultiplierInt}.");
    }

    [Help("Add an item by template id.")]
    [Command("additem")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AddItemCommand(string templateId) {
        // Try to parse the item id.
        if (!ulong.TryParse(templateId, out var templateIdLong)) {
            InformSenderClient("Invalid item id.");

            return;
        }

        // Check to see if this template exists.
        var template = CoreObjectFactory.GetCoreTemplate(templateIdLong);
        if (template is null) {
            InformSenderClient("Invalid item id.");

            return;
        }

        // We can't add game objects to the inventory.
        if (template is not WizItemTemplate) {
            InformSenderClient($"Cannot add objects of type {template.GetType().Name} to inventory.");

            return;
        }

        WizClientObjectItem coreObject;
        var addedItemSuccess = PetFactory.IsPetTemplate((uint) templateIdLong)
            ? Context.Character.AddHatchedPetToInventory((uint) templateIdLong, out coreObject)
            : Context.Character.AddItemToInventory(templateIdLong, out coreObject);
        if (!addedItemSuccess) {
            InformSenderClient("Could not add item to inventory.");

            return;
        }

        var coSerializer = new CoreObjectSerializer(
            behaviors: Imcodec.ObjectProperty.SerializerFlags.None
        );
        if (!coSerializer.Serialize(coreObject, 24, out var serializedCoreObject)) {
            InformSenderClient("Failed to serialize core object.");

            return;
        }

        var networkMessage = new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
            GlobalID = Context.Character.GameObjectID,
            SerializedItem = serializedCoreObject
        };
        Context.SessionActor.Tell(networkMessage, null);

        InformSenderClient($"Added item {coreObject.m_debugName} to inventory.");
    }

    // CLASSIC: QA setup for treasure card trades: add treasure cards to the book, as a drop would.
    [Help("Add treasure cards by spell template id: addtc <template> <count>.")]
    [Command("addtc")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AddTreasureCardCommand(string templateId, string count) {
        if (!uint.TryParse(templateId, out var id) || CoreObjectFactory.GetCoreTemplate(id) is not SpellTemplate spell
            || !int.TryParse(count, out var copies) || copies is < 1 or > 99) {
            InformSenderClient("Usage: addtc <spell template id> <1-99>");

            return;
        }

        for (var i = 0; i < copies; i++) {
            Game.DropTables.LootGranter.GrantTreasureCard(Context.SessionActor, Context.Character, id);
        }

        InformSenderClient($"Added {copies} {spell.m_name} treasure card(s).");
    }

    [Help("Add a pet snack by template id.")]
    [Command("addsnack")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AddSnackCommand(string templateId) {
        // Try to parse the item id.
        if (!ulong.TryParse(templateId, out var templateIdLong)) {
            InformSenderClient("Invalid item id.");

            return;
        }

        // Check to see if this template exists.
        var template = CoreObjectFactory.GetCoreTemplate(templateIdLong);
        if (template is null) {
            InformSenderClient("Invalid item id.");

            return;
        }

        // We can't non-snack objects to the snack bag.
        if (template is not PetSnackItemTemplate) {
            InformSenderClient($"Cannot add objects of type {template.GetType().Name} to snack bag.");

            return;
        }

        var addedSnackSuccess = Context.Character.AddSnack(templateIdLong, out var snackObj);
        if (!addedSnackSuccess) {
            InformSenderClient("Could not add snack to snack bag.");

            return;
        }

        var coSerializer = new CoreObjectSerializer(
            behaviors: Imcodec.ObjectProperty.SerializerFlags.None
        );
        if (!coSerializer.Serialize(snackObj, 24, out var serializedSnackObj)) {
            InformSenderClient("Failed to serialize core object.");

            return;
        }

        var networkMessage = new PET_9_PROTOCOL.MSG_PETSNACKADD {
            GlobalID = Context.Character.GameObjectID,
            Data = serializedSnackObj
        };
        Context.SessionActor.Tell(networkMessage, null);

        var acquireMessage = new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = snackObj.m_globalID,
            ItemTemplateID = (uint) snackObj.m_templateID,
            ItemLocation = 1
        };
        Context.SessionActor.Tell(acquireMessage, null);

        var updateMessage = new PET_9_PROTOCOL.MSG_PETSNACKUPDATE {
            GlobalID = Context.Character.GameObjectID,
            ItemID = snackObj.m_globalID,
            Quantity = snackObj.m_quantity
        };
        Context.SessionActor.Tell(updateMessage, null);

        InformSenderClient($"Added snack {snackObj.m_debugName} to snack bag.");
    }

    [Help("Add a reagent by template id.")]
    [Command("addreagent")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AddReagentCommand(string templateId) {
        // Try to parse the item id.
        if (!ulong.TryParse(templateId, out var templateIdLong)) {
            InformSenderClient("Invalid item id.");

            return;
        }

        // Check to see if this template exists.
        var template = CoreObjectFactory.GetCoreTemplate(templateIdLong);
        if (template is null) {
            InformSenderClient("Invalid item id.");

            return;
        }

        // We can't add non-reagent objects to the reagent bag.
        if (template is not ReagentItemTemplate) {
            InformSenderClient($"Cannot add objects of type {template.GetType().Name} to reagent bag.");

            return;
        }

        var addedReagentSuccess = Context.Character.AddReagent(templateIdLong, out var reagentObj);
        if (!addedReagentSuccess) {
            InformSenderClient("Could not add reagent to reagent bag.");

            return;
        }

        var coSerializer = new CoreObjectSerializer(
            behaviors: Imcodec.ObjectProperty.SerializerFlags.None
        );
        if (!coSerializer.Serialize(reagentObj, 27, out var serializedReagentObj)) {
            InformSenderClient("Failed to serialize core object.");

            return;
        }

        var networkMessage = new WIZARD_12_PROTOCOL.MSG_REAGENTADD {
            GlobalID = Context.Character.GameObjectID,
            Data = serializedReagentObj
        };
        Context.SessionActor.Tell(networkMessage, null);

        var acquireMessage = new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = reagentObj.m_globalID,
            ItemTemplateID = (uint) reagentObj.m_templateID,
            ItemLocation = 1
        };
        Context.SessionActor.Tell(acquireMessage, null);

        var updateMessage = new WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE {
            GlobalID = Context.Character.GameObjectID,
            ItemID = reagentObj.m_globalID,
            Quantity = reagentObj.m_quantity
        };
        Context.SessionActor.Tell(updateMessage, null);

        InformSenderClient($"Added reagent {reagentObj.m_debugName} to reagent bag.");
    }

    [Help("Rename your wizard.")]
    [Command("name")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetNameCommand([Remainder] string name) {
        // Set the name of the character.
        Context.Character.SetNameOverride(name);

        InformSenderClient($"Set name to {name}. Relog to see changes.");
    }

    [Help("Set your badge.")]
    [Command("badge")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetBadgeCommand([Remainder] string badge) {
        // Set the badge of the character.
        Context.Character.SetBadgeOverride(badge);

        InformSenderClient($"Set badge to {badge}. Relog to see changes.");
    }

    [Help("Set your gold limit.")]
    [Command("maxgold")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetMaxGoldCommand(string gold) {
        // Try to parse the gold.
        if (!int.TryParse(gold, out var goldInt)) {
            InformSenderClient("Invalid maximum gold amount.");

            return;
        }

        // Set the max gold amount.
        Context.Character.SetMaxGold(goldInt);

        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD() {
            Gold = Context.Character.GameStats.m_currentGold,
            MaxGold = goldInt
        };
        Context.SessionActor.Tell(networkMessage, null);

        InformSenderClient($"Set max gold to {goldInt}.");
    }

    [Help("Set your gold.")]
    [Command("gold")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetGoldCommand(string gold) {
        // CLASSIC: gold <amount>: sets the wizard's gold, raising the gold pouch when the amount is above it.
        if (!int.TryParse(gold, out var amount) || amount < 0) {
            InformSenderClient("Usage: gold <amount>");

            return;
        }

        var character = Context.Character;
        if (amount > character.GameStats.m_baseGoldPouch) {
            character.SetMaxGold(amount);
        }

        WizardData.Collections.WizardCollection.ChangeGold(character, (long) amount - character.GameStats.m_currentGold,
            capToPouch: false);
        Context.SessionActor.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = character.GameStats.m_currentGold,
            MaxGold = character.GameStats.m_baseGoldPouch,
        }, null);
        InformSenderClient($"Gold set to {character.GameStats.m_currentGold}.");
    }

    [Help("Add gold.")]
    [Command("addgold")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AddGoldCommand(string gold) {
        // Try to parse the gold.
        if (!int.TryParse(gold, out var goldInt)) {
            InformSenderClient("Invalid gold amount.");

            return;
        }

        // Set the gold amount.
        Context.Character.AddGold(goldInt);

        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD() {
            Gold = Context.Character.GameStats.m_currentGold,
            MaxGold = Context.Character.GameStats.m_baseGoldPouch
        };
        Context.SessionActor.Tell(networkMessage, null);

        InformSenderClient($"Added {goldInt} gold.");
    }

    [Help("Set your maximum health.")]
    [Command("maxhealth")]
    [Alias("maxhp")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetMaxHealthCommand(string health) {
        // Try to parse the health.
        if (!int.TryParse(health, out var healthInt)) {
            InformSenderClient("Invalid maximum health amount.");

            return;
        }

        Context.Character.GameStats.m_baseHitpoints = healthInt;

        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH() {
            CharacterID = Context.CharacterObject.m_globalID,
            NewHealth = Context.Character.GameStats.m_currentHitpoints,
            NewHealthMax = healthInt,
            DisplayDiff = 1,
        };
        Context.SessionActor.Tell(networkMessage, null);

        InformSenderClient($"Set max health to {healthInt}.");
    }

    [Help("Set your maximum mana.")]
    [Command("maxmana")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetMaxManaCommand(string mana) {
        // Try to parse the mana.
        if (!int.TryParse(mana, out var manaInt)) {
            InformSenderClient("Invalid maximum mana amount.");

            return;
        }

        Context.Character.GameStats.m_baseMana = manaInt;

        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEMANA() {
            Mana = Context.Character.GameStats.m_currentMana,
            MaxMana = manaInt,
            DisplayDiff = 1,
        };
        Context.SessionActor.Tell(networkMessage, null);

        InformSenderClient($"Set max mana to {manaInt}.");
    }

    // CLASSIC: QA help for the Pet Pavilion; the level up itself still comes from a pet game or snack.
    [Help("Add experience to your equipped pet without leveling it (stops 1 short of its next level).")]
    [Command("petxp")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AddPetXpCommand(string xp) {
        var pet = Services.PetGameService.EquippedPet(Context.Character);
        var behavior = PetProgress.Behavior(pet);
        if (!int.TryParse(xp, out var amount) || behavior is null || behavior.m_level == 0) {
            InformSenderClient("Equip a hatched pet and give an amount.");

            return;
        }

        PetProgress.EnsureInitialized(pet);
        var stop = (int) behavior.m_requiredXP - 1;
        behavior.m_XP = (uint) Math.Clamp((int) behavior.m_XP + amount, 0, Math.Max((int) behavior.m_XP, stop));
        WizardData.Collections.WizardItemCollection.SavePetGrowth(pet);
        InformSenderClient($"Pet XP now {behavior.m_XP} of {behavior.m_requiredXP} (level {behavior.m_level}).");
    }

    [Help("Set your maximum energy.")]
    [Command("maxenergy")]
    [Alias("maxnrg")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetMaxEnergyCommand(string energy) {
        // Try to parse the energy.
        if (!int.TryParse(energy, out var energyInt)) {
            InformSenderClient("Invalid maximum energy amount.");

            return;
        }

        Context.Character.GameStats.m_energyMax = energyInt;
        Context.Character.PetOwnerBehavior.SetEnergy(energyInt);

        var tickMsg = new PET_9_PROTOCOL.MSG_PETENERGYTICK() {
            GlobalID = Context.Character.GameObject.m_globalID,
            Energy = Context.Character.PetOwnerBehavior.Energy,
            MaxEnergy = energyInt,
            TickTime = (int) Context.Character.PetOwnerBehavior.LastEnergyTickEpoch
        };
        var maxMsg = new PET_9_PROTOCOL.MSG_PETENERGYMAX() {
            MaxEnergy = energyInt
        };

        Context.SessionActor.Tell(tickMsg, null);
        Context.SessionActor.Tell(maxMsg, null);

        InformSenderClient($"Set max energy to {energyInt}.");
    }

    [Help("Set your current health.")]
    [Command("currenthealth")]
    [Alias("currenthp")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetCurrentHealthCommand(string health) {
        // Try to parse the health.
        if (!int.TryParse(health, out var healthInt)) {
            InformSenderClient("Invalid current health amount.");

            return;
        }

        var newHealth = Math.Min(healthInt, Context.Character.GameStats.m_baseHitpoints);
        Context.Character.UpdateHealth(newHealth);

        // The client has a max health increase effect applied, so sending it here would double the health client side.
        var magicSchool = Context.Character.MagicSchoolBehavior.MagicSchool;
        var level = Context.Character.MagicSchoolBehavior.Level;
        var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(magicSchool, level);
        var normMaxHealth = baseStats.m_hitpoints;

        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH() {
            CharacterID = Context.CharacterObject.m_globalID,
            NewHealth = healthInt,
            NewHealthMax = normMaxHealth,
            DisplayDiff = 1,
        };
        Context.SessionActor.Tell(networkMessage, null);

        InformSenderClient($"Set current health to {healthInt}.");
    }

    [Help("Set your current mana.")]
    [Command("currentmana")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetCurrentManaCommand(string mana) {
        // Try to parse the mana.
        if (!int.TryParse(mana, out var manaInt)) {
            InformSenderClient("Invalid current mana amount.");

            return;
        }

        var newMana = Math.Min(manaInt, Context.Character.GameStats.m_baseMana);
        Context.Character.UpdateMana(newMana);

        // The client has a max mana increase effect applied, so sending it here would double the mana client side.
        var magicSchool = Context.Character.MagicSchoolBehavior.MagicSchool;
        var level = Context.Character.MagicSchoolBehavior.Level;
        var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(magicSchool, level);
        var normMaxMana = baseStats.m_mana;

        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEMANA() {
            Mana = manaInt,
            MaxMana = normMaxMana,
            DisplayDiff = 1,
        };
        Context.SessionActor.Tell(networkMessage, null);

        InformSenderClient($"Set current mana to {manaInt}.");
    }

    [Help("Set your current energy.")]
    [Command("currentenergy")]
    [Alias("currentnrg")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetCurrentEnergyCommand(string energy) {
        // Try to parse the energy.
        if (!int.TryParse(energy, out var energyInt)) {
            InformSenderClient("Invalid current energy amount.");

            return;
        }

        var newEnergy = Math.Min(energyInt, Context.Character.GameStats.m_energyMax);
        Context.Character.UpdateEnergy(newEnergy);

        // The client has a max energy increase effect applied, so sending it here would double the energy client side.
        var magicSchool = Context.Character.MagicSchoolBehavior.MagicSchool;
        var level = Context.Character.MagicSchoolBehavior.Level;
        var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(magicSchool, level);
        var normMaxEnergy = baseStats.m_petEnergy;

        var networkMessage = new PET_9_PROTOCOL.MSG_PETENERGYTICK() {
            GlobalID = Context.Character.GameObject.m_globalID,
            Energy = energyInt,
            MaxEnergy = normMaxEnergy,
            TickTime = (int) Context.Character.PetOwnerBehavior.LastEnergyTickEpoch
        };
        Context.SessionActor.Tell(networkMessage, null);

        InformSenderClient($"Set current energy to {energyInt}.");
    }

    [Help("Refill your health.")]
    [Command("refillhealth")]
    [Alias("refillhp", "heal")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void MaxHealthCommand() {
        var stats = Context.Character.GameStats;
        var maxHealth = stats.m_baseHitpoints;

        // The client has a max health increase effect applied, so sending it here would double the health client side.
        var magicSchool = Context.Character.MagicSchoolBehavior.MagicSchool;
        var level = Context.Character.MagicSchoolBehavior.Level;
        var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(magicSchool, level);
        var normMaxHealth = baseStats.m_hitpoints;

        Context.Character.UpdateHealth(maxHealth);

        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH() {
            CharacterID = Context.CharacterObject.m_globalID,
            NewHealth = maxHealth,
            NewHealthMax = normMaxHealth,
            DisplayDiff = 1,
        };
        Context.SessionActor.Tell(networkMessage, null);
    }

    [Help("Refill your mana.")]
    [Command("refillmana")]
    [Alias("refillmp", "rejuvenate", "rejuv")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void MaxManaCommand() {
        var stats = Context.Character.GameStats;
        var maxMana = stats.m_baseMana;

        // The client has a max mana increase effect applied, so sending it here would double the mana client side.
        var magicSchool = Context.Character.MagicSchoolBehavior.MagicSchool;
        var level = Context.Character.MagicSchoolBehavior.Level;
        var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(magicSchool, level);
        var normMaxMana = baseStats.m_mana;

        Context.Character.UpdateMana(maxMana);

        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEMANA() {
            Mana = maxMana,
            MaxMana = normMaxMana,
            DisplayDiff = 1,
        };
        Context.SessionActor.Tell(networkMessage, null);
    }

    [Help("Refill your energy.")]
    [Command("refillenergy")]
    [Alias("refillen", "refillnrg", "refillpet", "energize")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void MaxEnergyCommand() {
        // The client has a max mana increase effect applied, so sending it here would double the mana client side.
        var magicSchool = Context.Character.MagicSchoolBehavior.MagicSchool;
        var level = Context.Character.MagicSchoolBehavior.Level;
        var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(magicSchool, level);
        var normMaxEnergy = baseStats.m_petEnergy;

        Context.Character.UpdateEnergy(normMaxEnergy);

        // Inform the client of the change.
        var networkMessage = new PET_9_PROTOCOL.MSG_PETENERGYTICK() {
            GlobalID = Context.Character.GameObject.m_globalID,
            Energy = normMaxEnergy,
            MaxEnergy = normMaxEnergy,
            TickTime = (int) Context.Character.PetOwnerBehavior.LastEnergyTickEpoch
        };
        Context.SessionActor.Tell(networkMessage, null);
    }

    [Help("Gain a cantrip level.")]
    [Command("cantriplevelup")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    [Alias("clvlup")]
    private void CantripLevelUpCommand() {
        var newLevel = (byte) (Context.Character.GameStats.m_cantripLevel + 1);
        if (newLevel > CantripFactory.GetMaxCantripLevel()) {
            InformSenderClient($"You cannot set level higher than the max level (10).");

            return;
        }

        Context.Character.UpdateCantripLevel(newLevel);

        var msg = new CANTRIPSMESSAGES_57_PROTOCOL.MSG_UPDATECANTRIPXP {
            XP = 0,
            Level = newLevel
        };
        Context.SessionActor.Tell(msg, null);
    }

    [Help("Set your cantrip level.")]
    [Command("cantriplevel")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    [Alias("clvl")]
    private void SetCantripLevelCommand(string level) {
        // Try to parse the level.
        if (!byte.TryParse(level, out var levelByte)) {
            InformSenderClient("Invalid level.");

            return;
        }

        if (levelByte > CantripFactory.GetMaxCantripLevel()) {
            InformSenderClient($"You cannot set level higher than the max level (10).");

            return;
        }

        Context.Character.UpdateCantripLevel(levelByte);

        var msg = new CANTRIPSMESSAGES_57_PROTOCOL.MSG_UPDATECANTRIPXP {
            XP = 0,
            Level = levelByte
        };
        Context.SessionActor.Tell(msg, null);
    }

    [Help("Add training points.")]
    [Command("addtrainingpoints")]
    [Alias("addtp")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AddTrainingPointsCommand(string trainingPoints) {
        // Try to parse the training points.
        if (!int.TryParse(trainingPoints, out var trainingPointsInt)) {
            InformSenderClient("Invalid training points amount.");

            return;
        }

        var currentTrainingPoints = Context.Character.MagicSchoolBehavior.TrainingPoints;
        var newTrainingPoints = currentTrainingPoints + trainingPointsInt;
        Context.Character.UpdateTrainingPoints(newTrainingPoints);

        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING() {
            TrainingPoints = newTrainingPoints
        };
        Context.SessionActor.Tell(networkMessage, null);

        InformSenderClient($"Added {trainingPointsInt} training points.");
    }

    [Help("Set your training points.")]
    [Command("settrainingpoints")]
    [Alias("settp")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetTrainingPointsCommand(string trainingPoints) {
        // Try to parse the training points.
        if (!int.TryParse(trainingPoints, out var trainingPointsInt)) {
            InformSenderClient("Invalid training points amount.");

            return;
        }

        Context.Character.UpdateTrainingPoints(trainingPointsInt);

        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING() {
            TrainingPoints = trainingPointsInt
        };
        Context.SessionActor.Tell(networkMessage, null);

        InformSenderClient($"Set training points to {trainingPointsInt}.");
    }

    [Help("Fill your potions.")]
    [Command("potionmax")]
    [Alias("pmax")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetPotionMax(string potionMax) {
        if (!int.TryParse(potionMax, out var potionMaxInt)) {
            InformSenderClient("Invalid potion amount.");
            return;
        }

        Context.Character.UpdatePotions(potionMaxInt, potionMaxInt);

        // Inform the player's game client that their potion max has been updated.
        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEPOTIONS {
            PotionMax = potionMaxInt,
            PotionCharge = potionMaxInt
        };
        Context.SessionActor.Tell(networkMessage, null);
        InformSenderClient($"Set and filled potions to {potionMaxInt}.");
    }
    
    [Help("Add experience.")]
    [Command("addxp")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AddXPCommand(string xp) {
        // Try to parse the XP.
        if (!int.TryParse(xp, out var xpInt)) {
            InformSenderClient("Invalid XP amount.");

            return;
        }

       var msg = new CHARACTER_103_PROTOCOL.MSG_GAINXP() {
            XP = xpInt
        };
        Context.SessionActor.Tell(msg, null);

        InformSenderClient($"Added {xpInt} XP.");
    }


    // CLASSIC: set the account's Crowns (ClassicCrowns).
    [Help("Top Monstrology level and Animus per creature.")]
    [Command("monstrology")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void MonstrologyCommand(string animus) {
        // QA: monstrology <animus per creature>: the highest Monstrology level and that much Animus for every creature
        // the installed Monstrology cards name. Relog to see it in the tome.
        if (!int.TryParse(animus, out var amount) || amount < 0) {
            InformSenderClient("Usage: monstrology <animus per creature, up to 65535>");

            return;
        }

        if (!Game.Monstrology.MonstrologyService.Enabled) {
            InformSenderClient("Monstrology is off on this server ([Classic] Monstrology).");

            return;
        }

        var charId = Context.Character.CharId;
        var result = Game.Monstrology.MonstrologyRepository.ForPlayers().Transact(charId, state =>
            Game.Monstrology.MonstrologyRules.MaxOut(state, Game.Monstrology.MonstrologyProgression.InstalledThresholds,
                [.. Game.Monstrology.MonstrologyCardCatalog.Creatures], amount));
        var ledger = Game.Monstrology.MonstrologyRepository.ForPlayers().Read(charId);
        InformSenderClient(result == Game.Monstrology.MonstrologyResult.Applied
            ? $"Monstrology level {ledger.Level}, {ledger.Animus.Count} creatures with Animus. Relog to see the tome."
            : $"Monstrology not changed ({result}).");
    }

    [Help("Set your Crowns.")]
    [Command("setcrowns")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetCrownsCommand(string crowns) {
        if (!int.TryParse(crowns, out var amount) || amount < 0) {
            InformSenderClient("Usage: setcrowns <amount>");

            return;
        }

        var account = Context.Character.Account;
        Classic.ClassicCrowns.Add(account, amount - account.Crowns);
        Context.SessionActor.Tell(Classic.ClassicCrowns.BalanceMessage(account, Context.Character.CharId), Akka.Actor.ActorRefs.NoSender);
        InformSenderClient($"Crowns set to {account.Crowns}.");
    }

}
