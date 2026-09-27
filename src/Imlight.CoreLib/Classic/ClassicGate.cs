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
 * CLASSIC RULES GATE
 * ========================================================================
 * 
 * PURPOSE:
 * The CoreLib side of the classic world gate and feature refusals: asks
 * ClassicRuntime, audits, tells the player once, and maps hub keys to
 * WorldHubZones entries.
 * 
 * USAGE EXAMPLE:
 * if (!ClassicGate.AllowsZone(zone, GetActiveWizard()?.CharId, InformGameClient)) { return; }
 * 
 * NOTE:
 * Refusals are throttled per (character, zone or feature) for two seconds,
 * because some transfer requests reach ZoneService twice. With no active
 * profile every member behaves like the stock code it replaces.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

#nullable enable

using System;
using Imcodec.Cryptography;
using Imlight.Classic;
using Imlight.Classic.Audit;
using Imlight.Classic.Travel;
using Imlight.Classic.Zones;
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Classic;

internal static class ClassicGate {

    private const string ZoneLockedError = "ERROR_ClassicZoneLocked";
    private const string FallbackLocation = "Start";

    private static readonly DenialThrottle s_throttle = new();

    /// <summary>
    /// Decides whether a player may enter <paramref name="zone"/>, auditing allowed decisions when verbose.
    /// </summary>
    /// <param name="zone">The destination zone.</param>
    /// <returns>The decision.</returns>
    internal static ZoneDecision Decide(string zone) {
        var decision = ClassicRuntime.Rules.IsZoneAllowed(zone);
        AuditIfAllowedAndVerbose(decision);

        return decision;
    }

    /// <summary>
    /// Pre-check for flows that play effects before transferring. On a denial it audits and tells the player.
    /// </summary>
    /// <param name="zone">The destination zone.</param>
    /// <param name="charId">The moving character, if known.</param>
    /// <param name="inform">Shows the player a message; null for no message.</param>
    /// <returns>True when the zone is open.</returns>
    internal static bool AllowsZone(string zone, ulong? charId, Action<string, bool>? inform) {
        var decision = Decide(zone);
        if (decision.Allowed) {
            return true;
        }

        ReportDenial(decision, charId, inform);

        return false;
    }

    /// <summary>
    /// Spiral Door pre-check: the requested hub key must be one the door lists, and its teleport zone must be
    /// open. On a denial it audits and tells the player.
    /// </summary>
    /// <param name="hubKey">The requested WorldHubZones.xml key.</param>
    /// <param name="zone">That key's universe teleport zone.</param>
    /// <param name="charId">The moving character, if known.</param>
    /// <param name="inform">Shows the player a message; null for no message.</param>
    /// <returns>True when the door may send the player.</returns>
    internal static bool AllowsWorldTeleport(string hubKey, string zone, ulong? charId, Action<string, bool>? inform) {
        var decision = ClassicRuntime.Rules.IsWorldTeleportAllowed(hubKey, zone);
        AuditIfAllowedAndVerbose(decision);
        if (decision.Allowed) {
            return true;
        }

        ReportDenial(decision, charId, inform);

        return false;
    }

    /// <summary>
    /// Spiral Door unlock check: the world of <paramref name="hubKey"/> must be unlocked for the wizard (the
    /// profile's world_unlocks rule). On a denial it audits and tells the player.
    /// </summary>
    /// <param name="hubKey">The requested WorldHubZones.xml key.</param>
    /// <param name="progress">What the wizard has done.</param>
    /// <param name="charId">The moving character, if known.</param>
    /// <param name="inform">Shows the player a message; null for no message.</param>
    /// <returns>True when the wizard has unlocked the world.</returns>
    internal static bool AllowsWorldUnlock(string hubKey, IPlayerProgress progress, ulong? charId, Action<string, bool>? inform) {
        var decision = ClassicRuntime.Rules.IsWorldUnlocked(hubKey, progress);
        if (decision.Unlocked) {
            return true;
        }

        if (s_throttle.ShouldReport(charId ?? 0, "world:" + hubKey)) {
            ClassicRuntime.Audit(new ClassicAuditEntry(ClassicAuditKind.WorldLocked, charId, hubKey, decision.Reason));
            var name = ClassicRuntime.Rules.Zones.FindWorldByHubKey(hubKey)?.Name ?? hubKey;
            inform?.Invoke(ClassicMessages.WorldLocked(name), true);
        }

        return false;
    }

    /// <summary>
    /// Audits and reports a denied transfer, and builds the refusal reply for the transfer's asker.
    /// </summary>
    /// <param name="decision">The denial.</param>
    /// <param name="charId">The moving character, if known.</param>
    /// <param name="inform">Shows the player a message; null when the client is not waiting for one.</param>
    /// <returns>A transfer response carrying an error code.</returns>
    internal static ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP RefuseTransfer(ZoneDecision decision, ulong? charId,
                                                                        Action<string, bool>? inform) {
        ReportDenial(decision, charId, inform);

        return new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
            ErrorCode = StringHash.Compute(ZoneLockedError),
            ErrorMessage = decision.PlayerMessage,
        };
    }

    /// <summary>
    /// The hub a player in <paramref name="zone"/> goes to. Without an active profile this is the stock
    /// <see cref="WorldHubZones.GetHubForZone"/> lookup.
    /// </summary>
    /// <param name="zone">The zone the player is in.</param>
    /// <param name="progress">
    /// The wizard going home, when known: a world whose world_unlocks rule the wizard has not met sends them to
    /// the fallback world's hub instead (<see cref="ClassicRules.HomeHubKeyFor"/>).
    /// </param>
    /// <returns>The hub zone and location, or null when there is none.</returns>
    internal static (string Zone, string Location)? HubFor(string zone, IPlayerProgress? progress = null) {
        if (!ClassicRuntime.IsActive) {
            var stock = WorldHubZones.GetHubForZone(zone);

            return stock is null ? null : (stock.m_hubZone, stock.m_location);
        }

        var hubKey = progress is null
            ? ClassicRuntime.Rules.HubKeyFor(zone)
            : ClassicRuntime.Rules.HomeHubKeyFor(zone, progress);
        var hub = hubKey is null ? null : WorldHubZones.GetHubForZone(hubKey);
        if (hub is null) {
            return null;
        }

        return (hub.m_hubZone, string.IsNullOrEmpty(hub.m_location) ? FallbackLocation : hub.m_location);
    }

    /// <summary>
    /// Replaces a saved zone the profile closes with an open one: the zone's own world hub, else the
    /// fallback world's hub, else the ini starting zone. Unchanged when the zone is open.
    /// </summary>
    /// <param name="charId">The character logging in.</param>
    /// <param name="zone">The saved zone.</param>
    /// <param name="location">The saved location.</param>
    /// <returns>The zone and location to send the client to.</returns>
    internal static (string Zone, string Location) FallbackIfClosed(ulong charId, string zone, string location) {
        if (!ClassicRuntime.IsActive) {
            return (zone, location);
        }

        var decision = ClassicRuntime.Rules.IsZoneAllowed(zone);
        if (decision.Allowed) {
            return (zone, location);
        }

        if (HubFor(zone) is { } hub && ClassicRuntime.Rules.IsZoneAllowed(hub.Zone).Allowed) {
            AuditFallback(charId, zone, hub.Zone, decision);

            return hub;
        }

        var startingZone = ConfigurationManager.Settings["Character.StartingZone"].AsString();
        if (startingZone.Length > 0 && ClassicRuntime.Rules.IsZoneAllowed(startingZone).Allowed) {
            AuditFallback(charId, zone, startingZone, decision);

            return (startingZone, FallbackLocation);
        }

        Logger.Error("Classic: character {CharId} is saved in closed zone {Zone} and no open hub or starting zone was found.",
            Logger.Args(charId, zone));

        return (zone, location);
    }

    /// <summary>
    /// Audits a refused feature and tells the player once per throttle window.
    /// </summary>
    /// <param name="feature">A <see cref="ClassicFeatures"/> path.</param>
    /// <param name="charId">The character, if known.</param>
    /// <param name="inform">Shows the player a message.</param>
    internal static void RefuseFeature(string feature, ulong? charId, Action<string, bool> inform) {
        if (!s_throttle.ShouldReport(charId ?? 0, "feature:" + feature)) {
            return;
        }

        ClassicRuntime.Audit(new ClassicAuditEntry(ClassicAuditKind.FeatureRefused, charId, feature,
            $"feature {feature} is off in profile {ClassicRuntime.Rules.Profile.Id}"));
        inform(ClassicMessages.FeatureUnavailable(feature), true);
    }

    /// <summary>
    /// Audits an XP gain that lands a wizard on the capped max level.
    /// </summary>
    /// <param name="charId">The character.</param>
    /// <param name="level">The level reached.</param>
    internal static void LevelCapReached(ulong charId, int level)
        => ClassicRuntime.Audit(new ClassicAuditEntry(ClassicAuditKind.LevelCapReached, charId, $"level {level}",
            $"the level cap of profile {ClassicRuntime.Rules.Profile.Id}; further XP stops at the ceiling"));

    private static void AuditIfAllowedAndVerbose(ZoneDecision decision) {
        if (decision.Allowed && ClassicRuntime.AuditVerbose) {
            ClassicRuntime.Audit(new ClassicAuditEntry(ClassicAuditKind.ZoneAllowed, null, decision.Zone, decision.Reason,
                Verbose: true));
        }
    }

    private static void ReportDenial(ZoneDecision decision, ulong? charId, Action<string, bool>? inform) {
        if (!s_throttle.ShouldReport(charId ?? 0, decision.Zone)) {
            return;
        }

        ClassicRuntime.Audit(new ClassicAuditEntry(ClassicAuditKind.ZoneDenied, charId, decision.Zone, decision.Reason));
        inform?.Invoke(decision.PlayerMessage, true);
    }

    private static void AuditFallback(ulong charId, string zone, string destination, ZoneDecision decision)
        => ClassicRuntime.Audit(new ClassicAuditEntry(ClassicAuditKind.ZoneFallback, charId, zone,
            $"saved zone is closed ({decision.Reason}); sending the client to {destination}"));

}
