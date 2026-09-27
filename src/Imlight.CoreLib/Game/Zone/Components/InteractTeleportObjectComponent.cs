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
 * INTERACT TELEPORT OBJECT COMPONENT
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC travel: makes a press-X teleport object work, such as the
 * Dragonspyre Basilica portals (DS_Teleport_ToLibrary, DS_Teleporter_ToAcademy,
 * DS_Teleporter_ToNecropolis) and the Olde Town boat to the Grizzleheim
 * preview (WC_TeleportToGrizzleLite). Their destination lives in the
 * template's server-side InteractableBehavior, which the server cannot read,
 * so it comes from a SpiralDB ZoneTransfer entry named after the placed
 * object's m_zoneTag (the classic travel overlay generates them).
 *
 * USAGE EXAMPLE:
 * Attaches to teleport and portal object templates; an object whose zone has
 * no entry for its zone tag stays inert.
 *
 * NOTE:
 * The entry's ResTeleport m_requirements (the object's own quest gate, read
 * from the client template) must pass for the wizard to see the option and
 * to travel. Only active under the classic quest rules.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractTeleportObjectComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private const uint SIGIL_TEMPLATE_ID = 107081;
    private const uint WORLD_DOOR_TEMPLATE_ID = 84113;

    public string ServiceName => "Interact";
    public string NpcIcon => "";
    public string NpcNameKey => "";
    public string NpcTextKey => "";
    public WizBangs WizBang => WizBangs.None;
    public string StateName => null;
    public string InteractWizBang => null;
    public string DisplayKey => null;

    private ResTeleport _teleport;
    private bool _resolved;

    public static bool ShouldAttachToEntity(CoreTemplate template) {
        if (template is not GameObjectTemplate go || go.m_objectName is null
                || go.m_templateID is SIGIL_TEMPLATE_ID or WORLD_DOOR_TEMPLATE_ID) {
            return false;
        }

        var name = go.m_objectName.ToString();
        if (name.Contains("Sigil")) {
            return false; // InteractDungeonSigilComponent's
        }

        return name.Contains("Teleport", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Portal", StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter) {
        if (Teleport is null || !MeetsRequirements(null, null, playerCharacter)) {
            return [];
        }

        return [
            new InteractableOption { m_serviceName = ServiceName }
        ];
    }

    public override void OnPlayerJoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        // The Basilica portals are placed "Off"; light them for a wizard who may use them.
        if (Teleport is not null && MeetsRequirements(playerActor, playerObj, playerWizard)
                && string.Equals(Entity.Info?.m_startState, "Off", StringComparison.Ordinal)) {
            Entity.ChangeStateExclusiveSender("On", playerActor);
        }
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        var teleport = Teleport;
        if (teleport is null || !MeetsRequirements(playerActor, playerObject, playerCharacter)) {
            return;
        }

        playerActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = teleport.m_destinationZone,
            DestinationLocation = teleport.m_destinationLoc,
            SendToClient = true,
            OwnerCharId = playerCharacter?.CharId ?? 0,
        });
    }

    // The zone's ZoneTransfer entry named after this placement's m_zoneTag, or null.
    private ResTeleport Teleport {
        get {
            if (_resolved) {
                return _teleport;
            }

            _resolved = true;
            if (!ClassicQuestEngine.IsActive) {
                return null;
            }

            var tag = Entity.Info?.m_zoneTag?.ToString();
            var zonePath = Entity.Zone?.ZonePath;
            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(zonePath)) {
                return null;
            }

            var entry = ZoneDataCollection.GetZoneData(zonePath)?.Teleports?
                .FirstOrDefault(teleport => string.Equals(teleport.TriggerName, tag, StringComparison.Ordinal));
            if (entry?.Teleport is { } teleport && !string.IsNullOrEmpty(teleport.m_destinationZone)) {
                _teleport = teleport;
            }

            return _teleport;
        }
    }

    private bool MeetsRequirements(IActorRef playerActor, CoreObject playerObj, Wizard wizard) {
        var requirements = Teleport?.m_requirements;
        if (requirements?.m_requirements is not { Count: > 0 }) {
            return true;
        }

        if (wizard is null) {
            return false;
        }

        return RequirementDispatcher.EvaluateRequirements(
            requirements: requirements,
            context: new QuestRequirementContext(requirements, playerActor, playerObj, wizard));
    }

}
