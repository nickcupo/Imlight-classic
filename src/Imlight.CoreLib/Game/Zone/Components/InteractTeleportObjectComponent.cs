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
 * The teleport stones (Grizzleheim's rune stones, Dragonspyre's, and the Oct
 * 2009 Marleybone and MooShu stones) work the same way: each placed stone has one entry, leading to the
 * other stone of its pair in the same zone. The client's own flow is a plain
 * press-X interaction: the template's InteractableBehavior has one option per
 * zone the template is placed in (a ReqInZone each), so a stone never offers a
 * destination menu; the client answers the offered InteractableOption with
 * MSG_INTERACTOPTION. (MSG_ZONEGATELIST and ZoneGateOption, the later zone gate
 * menu, are in the r806919 message layer but no Arc 1 template uses them.)
 *
 * NOTE:
 * The entry's ResTeleport m_requirements (the object's own quest gate, read
 * from the client template) must pass for the wizard to see the option and
 * to travel. The entry's Classic data adds the profile feature the object
 * needs (hub_teleporters for the Marleybone and MooShu stones), the client tags
 * the wizard must have turned On first (the stone pair's discovery) and the
 * template's icon, title and prompt keys. Only active under the classic quest
 * rules.
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
using Imlight.Classic;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractTeleportObjectComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private const uint SIGIL_TEMPLATE_ID = 107081;
    private const uint WORLD_DOOR_TEMPLATE_ID = 84113;
    private const string ON_STATE = "On";
    private const string OFF_STATE = "Off";

    public string ServiceName => "Interact";
    public string NpcIcon => Entry?.Classic?.Icon ?? "";
    public string NpcNameKey => Entry?.Classic?.TitleKey ?? "";
    public string NpcTextKey => Entry?.Classic?.TextKey ?? "";
    public WizBangs WizBang => WizBangs.None;
    public string StateName => null;
    public string InteractWizBang => null;
    public string DisplayKey => null;

    private WizardTeleportData _entry;
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

        // The Grizzleheim rune stones (GH_RunestoneMainBearClaw, GH_Runestone_BearClaw, ...) are teleport stones too,
        // and the travel overlay names the others (the Dragonspyre battledrake, the Grand Chasm time crystal).
        return name.Contains("Teleport", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Portal", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("GH_Runestone", StringComparison.Ordinal) && !name.Contains("Minor"))
            || (ClassicQuestEngine.IsActive && ZoneDataCollection.IsTeleportObjectTemplate(name));
    }

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter) {
        if (Entry is null || !MayUse(null, null, playerCharacter)) {
            return [];
        }

        return [
            new InteractableOption { m_serviceName = ServiceName }
        ];
    }

    public override void OnPlayerJoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (Entry is null || !FeatureOn) {
            return;
        }

        // An undiscovered stone shows unlit to the wizard who has not found it (2009: the stone lights when found;
        // the discovery trigger's own dynamod turns it On).
        if (NeedsDiscovery && ClassicRuntime.Rules.TeleportStonesNeedDiscovery && !Discovered(playerWizard)) {
            Entity.ChangeStateExclusiveSender(OFF_STATE, playerActor);

            return;
        }

        // The Basilica portals are placed "Off"; light them for a wizard who may use them. An object whose option needs
        // a state is opened by its zone event, not here.
        if (Entry.Classic?.RequiresState is not { Length: > 0 } && MayUse(playerActor, playerObj, playerWizard)
                && string.Equals(Entity.Info?.m_startState, OFF_STATE, StringComparison.Ordinal)) {
            Entity.ChangeStateExclusiveSender(ON_STATE, playerActor);
        }
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        var teleport = Entry?.Teleport;
        if (teleport is null || !MayUse(playerActor, playerObject, playerCharacter)) {
            return;
        }

        playerActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = teleport.m_destinationZone,
            DestinationLocation = teleport.m_destinationLoc,
            SendToClient = true,
            OwnerCharId = playerCharacter?.CharId ?? 0,
        });
    }

    // The zone's ZoneTransfer entry for this placement: the one named after its m_zoneTag, or, when placements share
    // a tag, the "<tag>#<n>" entry whose Classic.At is nearest this placement. Null when there is none.
    private WizardTeleportData Entry {
        get {
            if (_resolved) {
                return _entry;
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

            var candidates = ZoneDataCollection.GetZoneData(zonePath)?.Teleports?
                .Where(entry => entry?.Teleport is { } teleport && !string.IsNullOrEmpty(teleport.m_destinationZone)
                    && (string.Equals(entry.TriggerName, tag, StringComparison.Ordinal)
                        || (entry.TriggerName?.StartsWith(tag + "#", StringComparison.Ordinal) ?? false)))
                .ToList() ?? [];
            _entry = candidates.Count <= 1 ? candidates.FirstOrDefault() : candidates.MinBy(DistanceTo);

            return _entry;
        }
    }

    private float DistanceTo(WizardTeleportData entry) {
        var at = entry.Classic?.At;
        if (at is not { Count: >= 3 } || Entity.ActiveGameObject is not { } obj) {
            return float.MaxValue;
        }

        var here = obj.m_location;

        var dx = at[0] - here.X;
        var dy = at[1] - here.Y;
        var dz = at[2] - here.Z;

        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private bool FeatureOn
        => Entry?.Classic?.Feature is not { Length: > 0 } feature || ClassicRuntime.Rules.IsFeatureEnabled(feature);

    private bool NeedsDiscovery => Entry?.Classic?.DiscoveredBy is { Count: > 0 };

    private bool Discovered(Wizard wizard) {
        if (!NeedsDiscovery) {
            return true;
        }

        var zonePath = Entity.Zone?.ZonePath;

        return wizard is not null
            && Entry.Classic.DiscoveredBy.Any(tag => wizard.HasDynamod(zonePath, tag, ON_STATE));
    }

    private bool InRequiredState(Wizard wizard) {
        var state = Entry?.Classic?.RequiresState;
        if (string.IsNullOrEmpty(state)) {
            return true;
        }

        var tag = Entity.Info?.m_zoneTag?.ToString();

        return string.Equals(Entity.Info?.m_startState?.ToString(), state, StringComparison.OrdinalIgnoreCase)
            || (wizard is not null && !string.IsNullOrEmpty(tag) && wizard.HasDynamod(Entity.Zone?.ZonePath, tag, state));
    }

    private bool MayUse(IActorRef playerActor, CoreObject playerObj, Wizard wizard) {
        if (!FeatureOn || !Discovered(wizard) || !InRequiredState(wizard)) {
            return false;
        }

        var requirements = Entry?.Teleport?.m_requirements;
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
