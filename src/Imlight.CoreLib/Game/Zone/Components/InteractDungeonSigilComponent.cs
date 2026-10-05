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
 * INTERACT DUNGEON SIGIL COMPONENT
 * ========================================================================
 * 
 * PURPOSE:
 * Makes a client-shipped dungeon-entry sigil pad (a MinigameSigilInfo, the "Street N <name> Instance
 * Sigil" octagons, tid 107081) a press-X interactable. The pad resolves where it leads from the zone's
 * teleport table and, when it resolves, lights its Glowy swirl for each joining player and hands the
 * pressing player's session a MSG_STARTSIGILENTRY (dismount + snap + countdown + private-instance
 * transfer, owned by ZoneService).
 * 
 * USAGE EXAMPLE:
 * Attaches automatically to sigil-pad templates (ShouldAttachToEntity); a pad with no resolvable
 * destination (e.g. a gauntlet teleport) stays inert and unlit.
 * 
 * NOTE:
 * Each pad entity resolves and caches its own destination from ZoneDataCollection, so nothing global
 * tracks sigils: the zone data is the single source of truth. Matches the same Street-N naming the
 * combat sigil supervisor conventions use; the fallback derives "<parent>/Interiors/<leaf>_T<N>" for
 * towers whose entrance teleport isn't named like the pad.
 * 
 * TODO:
 * - Quest/DynaMod gating of the Glowy state (when a pad should be dark) is future work.
 * 
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 08/13/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class InteractDungeonSigilComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private const uint SIGIL_TEMPLATE_ID = 107081;
    private const float DEFAULT_PAD_RADIUS = 370.0f;

    public string ServiceName => "Interact";
    public string NpcIcon => "";
    public string NpcNameKey => "";
    public string NpcTextKey => "";
    public WizBangs WizBang => WizBangs.None;
    public string StateName => null;
    public string InteractWizBang => null;
    public string DisplayKey => null;

    public float DEFAULT_INTERACTION_RADIUS
        => SigilInfo is not null && SigilInfo.m_radius > 0 ? SigilInfo.m_radius : DEFAULT_PAD_RADIUS;

    private ResolvedSigil _resolved;
    private MinigameSigilInfo SigilInfo 
        => Entity.Info as MinigameSigilInfo;
    private bool IsAvailable
         => SigilInfo is not null && TryResolveDestination(out _);

    [GeneratedRegex(@"street\s*(\d+)\s+(.+?)\s+instance\s+sigil", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex StreetTowerRegex();
    [GeneratedRegex(@"tower\s*0*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex TowerNumberRegex();

    public static bool ShouldAttachToEntity(CoreTemplate template) {
        if (template is not GameObjectTemplate go) {
            return false;
        }

        if (go.m_templateID == SIGIL_TEMPLATE_ID) {
            return true;
        }

        // Name-based catch for other sigil-pad templates; the available-gate below keeps anything
        // that isn't a resolvable dungeon entry inert.
        if (go.m_objectName is null) {
            return false;
        }

        var name = go.m_objectName.ToString();

        return (name.Contains("Sigil") && !name.Contains("Combat"))
            || name.Contains("TeleportSemiCircle")
            || name.Contains("TeleportFullCircle");
    }

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter) {
        if (!IsAvailable) {
            return [];
        }

        // CLASSIC: a sigil offers itself only to a wizard who meets its requirement list (the quest that opens
        // the dungeon), as the client data gates it.
        if (!MeetsRequirements(null, null, playerCharacter)) {
            return [];
        }

        return [
            new InteractableOption { m_serviceName = ServiceName }
        ];
    }

    public override void OnPlayerJoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (IsAvailable && MeetsRequirements(playerActor, playerObj, playerWizard)) { // CLASSIC: dark for a wizard it refuses.
            Entity.ChangeStateExclusiveSender("Glowy", playerActor);
        }
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (!TryResolveDestination(out var sigil)) {
            return;
        }

        // CLASSIC: the requirements may have changed since the option was offered.
        if (!MeetsRequirements(playerActor, playerObject, playerCharacter)) {
            return;
        }

        var obj = Entity.ActiveGameObject;
        var pad = obj.m_location;
        var heading = obj.m_orientation.Z;
        var sigilLoc = Util.GetCompactStringFromVector(new Vector4(pad.X, pad.Y, pad.Z, heading));

        // CLASSIC: everyone who uses this sigil before its countdown ends (up to four) enters the same new instance,
        // a sigil run (2009: "you all have to interact with the circles before the countdown reaches zero";
        // Classic.GroupInstances). The first wizard starts the countdown; the others join it with the time left.
        SigilTicket ticket = null;
        if (ClassicRuntime.IsActive && playerCharacter is not null) {
            var now = DateTime.UtcNow;
            // CLASSIC (2026-10-04): a real player at a full group takes an ambient wizard's place (it steps off).
            ticket = _group?.Join(playerCharacter.CharId, now, Classic.Ambient.AmbientWizards.IsAmbient(playerActor));
            if (ticket is null) {
                if (_group is not null && _group.IsOpen(now)) {
                    return; // every slot is taken
                }

                // CLASSIC: the wizard who starts the countdown goes back to the copy of this dungeon they left "another
                // way" within the return window (2009: "Leaving any other way gives you 30 minutes to return before it
                // resets"; Classic.InstanceResets); otherwise the sigil starts a new copy.
                var resumed = InstanceResets.ResumableRun(playerCharacter.CharId, sigil.DestinationZone, now);
                if (resumed != 0) {
                    Logger.Information("Dungeon sigil -> '{0}': {1} returns to sigil run {2}.",
                        Logger.Args(sigil.DestinationZone, playerCharacter.CharId, resumed));
                }

                _group = new SigilGroup(resumed != 0 ? resumed : GroupInstances.NewRunId(now), now,
                    GroupInstances.SigilCountdownSeconds);
                ticket = _group.Join(playerCharacter.CharId, now);
            }

            Logger.Information("Dungeon sigil -> '{0}': {1} is wizard {2} of sigil run {3} ({4:0.0} s left).",
                Logger.Args(sigil.DestinationZone, playerCharacter.CharId, ticket.Slot + 1, ticket.RunId, ticket.SecondsLeft));

            // CLASSIC (2026-10-04): ambient wizards nearby may come over and step on too (AmbientZone.Dungeons).
            if (!Classic.Ambient.AmbientWizards.IsAmbient(playerActor)) {
                Classic.Ambient.AmbientDungeons.NotifySigil(Entity.ZoneRef, new Classic.Ambient.AmbientSigilNotice(_group, pad,
                    heading, (ulong) obj.m_globalID, SigilInfo.m_sigilType.ToString() ?? "", sigil.DestinationZone,
                    sigil.DestinationLoc, playerCharacter.CharId, playerCharacter.MagicSchoolBehavior?.Level ?? 1));
            }
        }

        // Hand off to the player's session, which owns dismount + snap + countdown + transfer. The pad
        // object's gid lets the session trip the client's native on-face countdown on it.
        playerActor.Tell(new ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY {
            SigilLoc = sigilLoc,
            SigilGID = (ulong) obj.m_globalID,
            SigilType = SigilInfo.m_sigilType.ToString() ?? "",
            Radius = DEFAULT_INTERACTION_RADIUS,
            DestinationZone = sigil.DestinationZone,
            DestinationLoc = sigil.DestinationLoc,
            RunId = ticket?.RunId ?? 0, // CLASSIC
            Slot = ticket?.Slot ?? 0, // CLASSIC
            CountdownSeconds = ticket?.SecondsLeft ?? 0, // CLASSIC
        });
    }

    // CLASSIC: the group gathering on this sigil (null until first used; a new one once its countdown is over).
    private SigilGroup _group;

    // CLASSIC: the sigil's own m_requirements (a MinigameSigilInfo list: the quest that opens its dungeon), read the
    // KingsIsle way when the classic quest rules are on. Stock Imlight never checked them.
    private bool MeetsRequirements(IActorRef playerActor, CoreObject playerObj, Wizard wizard) {
        // CLASSIC: a classic travel entry that carries requirements replaces the sigil's own (a hand decision with
        // 2009 evidence: the 2014 River Village's ToTower2Part2 asks for a quest Woo Ping gives inside that tower).
        var travel = TravelRequirements();
        var requirements = Imlight.Classic.Travel.SigilRequirements.Choose(SigilInfo?.m_requirements, travel,
            travel?.m_requirements is { Count: > 0 });
        if (!ClassicQuestEngine.IsActive || requirements?.m_requirements is not { Count: > 0 }) {
            return true;
        }

        if (wizard is null) {
            return false;
        }

        return RequirementDispatcher.EvaluateRequirements(
            requirements: requirements,
            context: new QuestRequirementContext(requirements, playerActor, playerObj, wizard));
    }

    // CLASSIC: the requirements the zone's travel entry for this sigil carries, or null.
    private RequirementList TravelRequirements() {
        if (!ClassicQuestEngine.IsActive || SigilInfo is null || Entity.Zone?.ZonePath is not { Length: > 0 } zonePath) {
            return null;
        }

        var teleport = MatchEntranceTeleport(SigilInfo.m_zoneTag2.ToString() ?? "", ZoneDataCollection.GetZoneData(zonePath));

        return teleport?.Teleport?.m_requirements as RequirementList;
    }

    private sealed record ResolvedSigil(string DestinationZone, string DestinationLoc);

    private static readonly Regex s_streetTowerRx
        = StreetTowerRegex();

    private static readonly Regex s_towerNumRx
        = TowerNumberRegex(); // A tower number anywhere in the tag: "ToTower01" -> 1, "Street 4 Tower 1 Instance Sigil" -> 1.

    private static string NormalizeSigilToken(string s)
        => Regex.Replace(s ?? "", "[^A-Za-z0-9]", "").ToLowerInvariant();

    private bool TryResolveDestination(out ResolvedSigil sigil) {
        sigil = null;
        if (_resolved is not null) {
            sigil = _resolved;

            return true;
        }

        if (SigilInfo is null) {
            return false;
        }

        var zonePath = Entity.Zone?.ZonePath;
        if (string.IsNullOrEmpty(zonePath)) {
            return false;
        }

        var tag = SigilInfo.m_zoneTag2.ToString() ?? "";
        var zoneData = ZoneDataCollection.GetZoneData(zonePath);

        var teleport = MatchEntranceTeleport(tag, zoneData);
        if (teleport?.Teleport is not null) {
            _resolved = new ResolvedSigil(
                teleport.Teleport.m_destinationZone ?? "",
                teleport.Teleport.m_destinationLoc ?? "");

            sigil = _resolved;

            return !string.IsNullOrEmpty(_resolved.DestinationZone);
        }

        if (TryDeriveTowerDestination(zonePath, tag, zoneData, out var destZone, out var destLoc)) {
            _resolved = new ResolvedSigil(destZone, destLoc);
            sigil = _resolved;

            return true;
        }

        return false;
    }

    internal static WizardTeleportData MatchEntranceTeleport(string tag, WizardZoneData zoneData) {
        if (zoneData?.Teleports is null || zoneData.Teleports.Count == 0) {
            return null;
        }

        var tagMatch = s_streetTowerRx.Match(tag);
        // CLASSIC: an entry named exactly like the sigil wins over the street/tower match below, which reads
        // "Street 6 Tower 1 Instance Sigil" as "street6" + "tower1" and so also matches "Street 6 CustTower 1 Instance
        // Sigil" (Colossus Boulevard lists that one first, sending Seal the Deal's Rotunda sigil into Troll Tower).
        if (tagMatch.Success && ClassicQuestEngine.IsActive && ExactTeleport(tag, zoneData) is { } exact) {
            return exact;
        }

        if (tagMatch.Success) {
            var streetTok = "street" + tagMatch.Groups[1].Value;
            var instance = NormalizeSigilToken(tagMatch.Groups[2].Value);

            return zoneData.Teleports.FirstOrDefault(t => {
                var norm = NormalizeSigilToken(t.TriggerName);

                return norm.Contains(streetTok) && norm.Contains(instance);
            });
        }

        return ExactTeleport(tag, zoneData);
    }

    // The zone teleport whose trigger name is the sigil's tag (case, spaces and punctuation ignored), or null.
    private static WizardTeleportData ExactTeleport(string tag, WizardZoneData zoneData) {
        var tagNorm = NormalizeSigilToken(tag);

        return zoneData.Teleports.FirstOrDefault(t => {
            var norm = NormalizeSigilToken(t.TriggerName);

            return norm.Length > 0 && norm == tagNorm;
        });
    }

    private static bool TryDeriveTowerDestination(string zoneName, string tag, WizardZoneData zoneData,
                                                  out string destZone, out string destLoc) {
        destZone = destLoc = null;
        var m = s_towerNumRx.Match(tag ?? "");
        if (!m.Success) {
            return false;
        }

        var slash = zoneName.LastIndexOf('/');
        if (slash < 0) {
            return false;
        }

        var instancePrefix = $"{zoneName[..slash]}/Interiors/{zoneName[(slash + 1)..]}_T";
        var candidate = instancePrefix + int.Parse(m.Groups[1].Value);
        if (ZoneDataCollection.GetZoneData(candidate) is null) {
            return false; // the instance zone must actually exist
        }

        var sibling = zoneData?.Teleports?.FirstOrDefault(t =>
            (t.Teleport?.m_destinationZone ?? "").StartsWith(instancePrefix, StringComparison.OrdinalIgnoreCase));
        if (sibling?.Teleport is null) {
            return false; // no known entry loc for this instance family; don't guess a position
        }

        destZone = candidate;
        destLoc = sibling.Teleport.m_destinationLoc ?? "";

        return true;
    }

}
