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
 * WIZARD SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages wizard character state, including location tracking, 
 * level management, and game state synchronization.
 * 
 * USAGE EXAMPLE:
 * Internal service handling wizard-specific interactions within 
 * the game server session.
 * 
 * NOTE:
 * - Updates wizard location and orientation
 * - Manages level-up mechanics and stat updates
 * - Provides internal wizard state management
 * 
 * TODO:
 * - Enhance level-up stat calculation
 * - Review location and orientation compression logic
 * 
 * Created by: Jooty, Joji
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using Akka.Actor;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.Shared.Character;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Math;
using Microsoft.Extensions.ObjectPool;
using System;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Services;

internal class WizardService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const float ORIENTATION_TOLERANCE = 1.035f;

    private Wizard _activeWizard;
    private CoreObject _activeWizardGameObject;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new WizardService(parentActor));

    protected override void OnPreDispose() {
        try {
            _activeWizard?.SaveLocation();
        }
        catch (Exception ex) {
            Logger.Error("Failed to save wizard location during disconnect: {0}",
                Logger.Args(ex));
        }
        finally {
            base.OnPreDispose();
        }
    }

    // CLASSIC: the directory answers for this session only while it lives.
    protected override void PostStop() {
        ActiveWizardDirectory.Remove(SessionActor?.ActorRef);
        base.PostStop();
    }

    #region Internal Handlers

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP))]
    private void ReceiveZoneAddPlayerResponse(ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP message) {
        _activeWizardGameObject = message.WizardGameObject;
        ActiveWizardDirectory.SetGameObject(SessionActor.ActorRef, _activeWizardGameObject); // CLASSIC: no Ask needed.
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_SETACTIVEWIZARD))]
    private void ReceiveSetActiveWizard(CHARACTER_103_PROTOCOL.MSG_SETACTIVEWIZARD message) {
        _activeWizard = message.Wizard;
        ActiveWizardDirectory.SetWizard(SessionActor.ActorRef, _activeWizard); // CLASSIC: no Ask needed.
        _activeWizard.UpdateLastLoginTime((uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD))]
    private void ReceiveQueryActiveWIzard(CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD message)
        => Sender.Tell(new CHARACTER_103_PROTOCOL.MSG_CHARACTER() {
            Wizard = _activeWizard,
            WizardGameObject = _activeWizardGameObject
        });

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_LEVELUP))]
    private void ReceiveSetLevel(CHARACTER_103_PROTOCOL.MSG_LEVELUP message) {
        // This is the internal level up message. It most likely happened due to a developer command.
        var levelUpSuccess = _activeWizard.SetLevel(message.NewLevel);
        if (!levelUpSuccess) {
            return;
        }

        // CLASSIC: the GM level commands (.mod level, .mod lvlup) also set the XP to the start of the new level when
        // the wizard's XP lies outside it, so the level holds: the next login warned of an XP/level mismatch and the
        // next XP gain put the level back.
        var school = _activeWizard.MagicSchoolBehavior;
        if (MagicLevelsConfig.GetPlayerLevelAtExperience(school.ExperiencePoints) != school.Level) {
            school.ExperiencePoints = MagicLevelsConfig.GetExperiencePointsAtLevel(school.Level);
            WizardCollection.UpdateCharacterLevel(_activeWizard);
        }

        var levelUpMessage = new WIZARD_12_PROTOCOL.MSG_LEVELUP {
            GlobalID = _activeWizard.GameObjectID,
            NewLevel = _activeWizard.MagicSchoolBehavior.Level,
            Data = "0000000000"
        };
        ZoneBroadcast(levelUpMessage, false);

        // Leveling up the player will set their new stats and heal them.
        // We need to do the code below to echo those changes to the client.
        var magicSchool = _activeWizard.MagicSchoolBehavior.MagicSchool;
        // CLASSIC: SetLevel may clamp to the level cap, so echo the stats of the level actually set.
        var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(magicSchool, _activeWizard.MagicSchoolBehavior.Level);

        // Update health — heal to full and sync server state with what the client was told.
        _activeWizard.UpdateHealth(_activeWizard.GameStats.m_baseHitpoints);
        var healthMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH() {
            CharacterID = _activeWizardGameObject.m_globalID,
            NewHealth = baseStats.m_hitpoints,
            NewHealthMax = baseStats.m_hitpoints,
            DisplayDiff = 1,
        };
        SendToSocket(healthMessage);

        // Update mana.
        _activeWizard.UpdateMana(_activeWizard.GameStats.m_baseMana);
        var manaMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEMANA() {
            Mana = baseStats.m_mana,
            MaxMana = baseStats.m_mana,
            DisplayDiff = 1,
        };
        SendToSocket(manaMessage);

        // Update power pips
        var powerPipsMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEPOWERPIP() { PowerPip = baseStats.m_pipChance };
        SendToSocket(powerPipsMessage);

        // Update energy.
        _activeWizard.UpdateEnergy(baseStats.m_petEnergy);
        var petEnergyMessage = new PET_9_PROTOCOL.MSG_PETENERGYMAX() {
            MaxEnergy = baseStats.m_petEnergy
        };
        SendToSocket(petEnergyMessage);
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_GAINXP))]
    private void ReceiveGainXP(CHARACTER_103_PROTOCOL.MSG_GAINXP message) {
        var beforeLevel = _activeWizard.MagicSchoolBehavior.Level;
        var beforeXP = _activeWizard.MagicSchoolBehavior.ExperiencePoints;
        // CLASSIC: the client is told the XP actually applied, and nothing when the level cap stops it all.
        var xpGained = _activeWizard.AddExperiencePoints(message.XP);
        if (xpGained == 0 && message.XP != 0) {
            return;
        }

        var afterLevel = _activeWizard.MagicSchoolBehavior.Level;

        if (beforeLevel != afterLevel) {
            // The player leveled up. AddExperiencePoints already called SetLevel internally,
            // so we just need to broadcast the level-up and send stat updates to the client.
            var levelUpMessage = new WIZARD_12_PROTOCOL.MSG_LEVELUP {
                GlobalID = _activeWizard.GameObjectID,
                NewLevel = afterLevel,
                Data = "0000000000"
            };
            ZoneBroadcast(levelUpMessage, false);

            // Send stat updates to client (health, mana, power pips, energy).
            var magicSchool = _activeWizard.MagicSchoolBehavior.MagicSchool;
            var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(magicSchool, afterLevel);

            // Heal to full and sync server state with what the client was told.
            _activeWizard.UpdateHealth(_activeWizard.GameStats.m_baseHitpoints);
            var healthMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH() {
                CharacterID = _activeWizardGameObject.m_globalID,
                NewHealth = baseStats.m_hitpoints,
                NewHealthMax = baseStats.m_hitpoints,
                DisplayDiff = 1,
            };
            SendToSocket(healthMessage);

            _activeWizard.UpdateMana(_activeWizard.GameStats.m_baseMana);
            var manaMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEMANA() {
                Mana = baseStats.m_mana,
                MaxMana = baseStats.m_mana,
                DisplayDiff = 1,
            };
            SendToSocket(manaMessage);

            var powerPipsMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEPOWERPIP() {
                PowerPip = baseStats.m_pipChance
            };
            SendToSocket(powerPipsMessage);

            _activeWizard.UpdateEnergy(baseStats.m_petEnergy);
            var petEnergyMessage = new PET_9_PROTOCOL.MSG_PETENERGYMAX() {
                MaxEnergy = baseStats.m_petEnergy
            };
            SendToSocket(petEnergyMessage);
        }

        // CLASSIC: audit the gain that lands a wizard on the capped max level.
        if (beforeLevel != afterLevel && afterLevel == MagicLevelsConfig.MaxLevel && MagicLevelsConfig.MaxLevelXp is not null) {
            ClassicGate.LevelCapReached(_activeWizard.CharId, afterLevel);
        }

        // Inform the client of the XP change.
        var addXpMsg = new WIZARD_12_PROTOCOL.MSG_UPDATEXP {
            GlobalID = _activeWizard.GameObjectID,
            XP = xpGained,
            OldXP = beforeXP,
        };
        SendToSocket(addXpMsg);
    }

    #endregion

    #region Game Handlers

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENTMOVE))]
    private void ReceiveClientMove(GAME_5_PROTOCOL.MSG_CLIENTMOVE message) {
        // CLASSIC: a client may move before its MSG_ATTACH has set the wizard.
        if (_activeWizard is null) {
            Logger.Debug("SessionActor {SessionId} ignored a move sent before the attach.",
                Logger.Args(SessionActor.SessionID));

            return;
        }

        // Save the player's location and direction on interval.
        // Restore actual location information, as it is compressed by a factor of 4 and unsigned.
        // Yaw is represented in radians in the client, but transmitted to the server as degrees.
        var position = new Vector3(
            unchecked((short) message.LocationX * 4),
            unchecked((short) message.LocationY * 4),
            unchecked((short) message.LocationZ * 4));
        _activeWizard.Location = position;

        // Direction is a byte and it's packed. Unpack it and convert it to radians.
        var initDir = message.Direction;
        var degrees = initDir * (360f / byte.MaxValue) * ORIENTATION_TOLERANCE;
        var radians = degrees * (System.MathF.PI / 180f);
        _activeWizard.Orientation = new Vector3(0, 0, radians);
    }

    #endregion
    
}
