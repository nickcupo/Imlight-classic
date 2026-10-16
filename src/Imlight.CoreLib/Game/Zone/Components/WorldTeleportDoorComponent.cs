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
 * WORLD TELEPORT DOOR
 * ========================================================================
 * 
 * PURPOSE:
 * Manages world teleportation interactions for universe map doors, 
 * providing players with world selection and teleportation options.
 * 
 * USAGE EXAMPLE:
 * 
 * NOTE:
 * 
 * TODO:
 * - Implement dynamic world list fetching from database
 * 
 * Created by: Jeff
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class WorldTeleportDoorComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private const uint WORLD_DOOR_TEMPLATE_ID = 84113;

    public string ServiceName     => "UniverseMapService";
    public string NpcIcon         => "GUI/Buttons/Button_Spiral.dds";
    public string NpcNameKey      => "WizardGameObjects_00000070";
    public string NpcTextKey      => "GUI_ObjectInteract";
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => "UniverseTeleport";
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => "GUI_UniverseMap";

    public static bool ShouldAttachToEntity(CoreTemplate template) 
        => template is GameObjectTemplate goTemplate 
        && goTemplate.m_templateID == WORLD_DOOR_TEMPLATE_ID;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _) 
        => [
            new UniverseMapOption() {
                m_displayKey = DisplayKey,
                m_iconKey = NpcIcon,
                m_serviceName = ServiceName,
            }
        ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        SendWorldTeleportOptions(playerActor, playerCharacter);
        SendPlayerIntoWizbang(playerObject.m_globalID);
        SendPlayerIntoState(playerObject.m_globalID);
    }

    /// <summary>
    /// The WorldHubZones.xml keys the Spiral Door can offer, in the order it lists them.
    /// </summary>
    internal static readonly string[] SpiralDoorHubKeys = [
        "WizardCity",
        "Krokotopia",
        "Marleybone",
        "MooShu",
        "Grizzleheim",
        "DragonSpire",
        // CLASSIC: Celestia (live 2010-10-27). Only a profile whose worlds include celestia lists it (celestia-2010),
        // and there only once the wizard holds Door to the Stars (its world_unlocks rule).
        "Celestia",
    ];

    /// <summary>
    /// CLASSIC: the worlds the Spiral Door lists: the profile's open worlds, then (for a known wizard) only those the
    /// wizard has unlocked.
    /// </summary>
    /// <param name="rules">The active classic rules.</param>
    /// <param name="progress">What the wizard has done, or null when no wizard is known.</param>
    /// <returns>The hub keys to send, in door order.</returns>
    internal static List<string> ListedWorlds(Imlight.Classic.ClassicRules rules, Imlight.Classic.Travel.IPlayerProgress? progress)
        => [.. SpiralDoorHubKeys.Where(rules.IsHubKeyAllowed)
            .Where(hubKey => progress is null || rules.IsWorldUnlocked(hubKey, progress).Unlocked)];

    private void SendWorldTeleportOptions(IActorRef playerActor, Wizard playerCharacter) {
        // CLASSIC: list only the worlds the profile opens, and of those only the ones this wizard has unlocked
        // (the profile's world_unlocks: the quest that opened each world).
        var teleportDoorOptions = new WorldTeleportOptions {
            m_worldList = ListedWorlds(ClassicRuntime.Rules, playerCharacter is null ? null : new WizardProgress(playerCharacter))
        };

        // Serialize the teleport door options and send it to the player.
        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.None
        );
        if (!serializer.Serialize(teleportDoorOptions, 4, out var data)) {
            Logger.Error("Failed to serialize teleport door options.");

            return;
        }

        var teleportDoorOpen = new WIZARD_12_PROTOCOL.MSG_WORLDTELEPORTLIST {
            GlobalID = Entity.ActiveGameObject.m_globalID,
            Data = data
        };
        playerActor.Tell(teleportDoorOpen);
    }

    private void SendPlayerIntoWizbang(ulong playerObjID) {
        // Create the wiz bang message, and wrap it in a broadcast message.
        var wizBangMsg = new GAME_5_PROTOCOL.MSG_WIZBANG {
            WizBangID = StringHash.Compute(InteractWizBang),
            GameObjectID = playerObjID
        };
        var broadcastMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = wizBangMsg,
            Selfless = false,
        };

        Entity.ZoneRef.Tell(broadcastMsg);
    }

    private void SendPlayerIntoState(ulong playerObjID) {
        // Create the change state message, and wrap it in a broadcast message.
        var changeStateMsg = new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            State = StringHash.Compute(StateName),
            GameObjectID = playerObjID
        };
        var broadcastMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = changeStateMsg,
            Selfless = false,
        };

        Entity.ZoneRef.Tell(broadcastMsg);
    }

}