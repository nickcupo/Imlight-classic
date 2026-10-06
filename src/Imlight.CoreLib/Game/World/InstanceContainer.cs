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
 * INSTANCE CONTAINER MANAGEMENT SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Manages zone instances for a specific player using Akka.NET actor system,
 * handling zone loading, transfer, and dynamic zone creation.
 * 
 * USAGE EXAMPLE:
 * Create InstanceContainer using InstanceContainer.Props(ownerId)
 * Handle zone transfers and dynamic zone loading for a specific player
 * 
 * NOTE:
 * Utilizes Akka.NET actor system for per-player zone instancing.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using System;
using System.Collections.Generic;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// Manages zone instances for a specific player using Akka.NET actor system.
/// </summary>
internal sealed class InstanceContainer(ulong instanceOwnerId, ulong housingDeedId = 0) : ReceiveProtocolDispatcher, IWithTimers {

    public ITimerScheduler Timers { get; set; }

    private readonly ulong _instanceOwnerId = instanceOwnerId;
    private readonly ulong _housingDeedId = housingDeedId; // CLASSIC: distinct lots may share every zone template.
    private readonly List<uint> _dynamicZoneIds = [];
    private readonly Dictionary<string, IActorRef> _zones = [];

    public static Props Props(ulong instanceOwnerId, ulong housingDeedId = 0)
        => Akka.Actor.Props.Create(() => new InstanceContainer(instanceOwnerId, housingDeedId));

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONETRANSFER))]
    public void ReceiveZoneTransfer(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message) {
        if (message.OwnerCharId != _instanceOwnerId || message.HousingDeedId != _housingDeedId) { // CLASSIC
            Sender.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP { ErrorCode = 1 });
            return;
        }
        Logger.Debug("Container (owned by: {0}) received zone transfer request for zone: {1}",
            Logger.Args(_instanceOwnerId, message.DestinationZone));

        // Throw an exception if we don't have this zone.
        if (!_zones.ContainsKey(message.DestinationZone)) {
            throw new Exception($"Zone {message.DestinationZone} not found in instance container {_instanceOwnerId}");
        }

        // Otherwise, forward this transfer message to the zone.
        _zones[message.DestinationZone].Forward(message);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS))]
    public void ReceiveZoneLoadResults(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) {
        var zoneName = message.ZoneData.m_zoneName;
        var zoneActor = CreateZone(zoneName);
        zoneActor.Tell(message);

        _zones[zoneName] = zoneActor;
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_INSTANCECONTAINERHASZONE))]
    public void ReceiveInstanceContainerHasZone(ZONE_102_PROTOCOL.MSG_INSTANCECONTAINERHASZONE message) 
        => Sender.Tell(new ZONE_102_PROTOCOL.MSG_INSTANCECONTAINERHASZONERSP {
            HasZone = (message.OwnerCharId == 0 || message.OwnerCharId == _instanceOwnerId)
                && message.HousingDeedId == _housingDeedId && _zones.ContainsKey(message.ZoneName), // CLASSIC
            OwnerCharId = _instanceOwnerId,
            HousingDeedId = _housingDeedId,
        });

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_DROPINSTANCEZONE))]
    public void ReceiveDropInstanceZone(ZONE_102_PROTOCOL.MSG_DROPINSTANCEZONE message) {
        if (!_zones.Remove(message.ZoneName, out var zoneActor)) {
            return;
        }

        _occupied.Remove(message.ZoneName); // CLASSIC
        Timers.Cancel(DungeonTimerKey(Imlight.Classic.Travel.InstanceGroups.GroupKey(message.ZoneName))); // CLASSIC: a fresh copy follows

        Logger.Information("Dropping instance zone {ZoneName} (owner {OwnerId})",
            Logger.Args(message.ZoneName, _instanceOwnerId));

        Context.Stop(zoneActor);
    }

    // CLASSIC: a zone of this container asks to be dropped (a party lost there and it is now empty); the next
    // transfer into it loads it fresh. A stale request from an older copy of the zone is ignored.
    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_DROPSELF))]
    public void ReceiveDropSelf(CLASSIC_FEATURES_PROTOCOL.MSG_DROPSELF message) {
        if (message.ZoneName is null || !_zones.TryGetValue(message.ZoneName, out var zoneActor)
                || !zoneActor.Equals(Sender)) {
            return;
        }

        _zones.Remove(message.ZoneName);
        _occupied.Remove(message.ZoneName); // CLASSIC
        Logger.Information("Resetting instance zone {ZoneName} (owner {OwnerId}).",
            Logger.Args(message.ZoneName, _instanceOwnerId));
        Context.Stop(zoneActor);

        // CLASSIC: a sigil run's container with nothing left in it is forgotten (no one can enter that run again).
        if (_zones.Count == 0 && Classic.GroupInstances.IsRun(_instanceOwnerId)) {
            Context.Parent.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_RUNCONTAINEREMPTY { OwnerId = _instanceOwnerId });
        }
    }

    // ===== CLASSIC: empty dungeons reset as a whole (2009 rule; Classic.InstanceResets) =====

    /// <summary>CLASSIC: a zone of this container became empty or occupied (Zone.UpdateEmptyRunTimer).</summary>
    internal sealed record ZoneOccupancy(string ZoneName, bool Occupied);

    /// <summary>CLASSIC: the dungeon <paramref name="Key"/> of this container has been empty for the empty lifetime.</summary>
    internal sealed record DungeonExpired(string Key);

    private readonly Dictionary<string, bool> _occupied = [];

    [MessageHandler(typeof(ZoneOccupancy))]
    public void ReceiveZoneOccupancy(ZoneOccupancy message) {
        if (message.ZoneName is null || !_zones.TryGetValue(message.ZoneName, out var zoneActor) || !zoneActor.Equals(Sender)) {
            return;
        }

        _occupied[message.ZoneName] = message.Occupied;
        var key = Imlight.Classic.Travel.InstanceGroups.GroupKey(message.ZoneName);
        if (!Classic.InstanceResets.TracksEmptyCopy(_instanceOwnerId, message.ZoneName)) {
            return;
        }

        if (AnyZoneOccupied(key)) {
            Timers.Cancel(DungeonTimerKey(key));
        }
        else if (!Timers.IsTimerActive(DungeonTimerKey(key))) {
            Timers.StartSingleTimer(DungeonTimerKey(key), new DungeonExpired(key),
                Imlight.Classic.Travel.InstanceGroups.Rules.EmptyLifetime);
        }
    }

    [MessageHandler(typeof(DungeonExpired))]
    public void ReceiveDungeonExpired(DungeonExpired message) {
        var zones = ZonesOfKey(message.Key);
        if (zones.Count == 0 || AnyZoneOccupied(message.Key)) {
            return;
        }

        // Someone is on the way in, or a fight here holds a seat: keep it another lifetime.
        var group = Imlight.Classic.Travel.InstanceGroups.GroupOf(zones[0])
                    ?? new Imlight.Classic.Rules.InstanceGroup(message.Key, Imlight.Classic.Rules.InstanceKind.Dungeon, [.. zones]);
        if (Classic.InstanceResets.IsOccupied(_instanceOwnerId, group, 0, DateTime.UtcNow)) {
            Timers.StartSingleTimer(DungeonTimerKey(message.Key), message, Imlight.Classic.Travel.InstanceGroups.Rules.EmptyLifetime);

            return;
        }

        Logger.Information("Dungeon {Dungeon} (instance {OwnerId}) has been empty {Minutes} minutes; resetting its {Count} zone(s).",
            Logger.Args(message.Key, _instanceOwnerId, Imlight.Classic.Travel.InstanceGroups.Rules.EmptyLifetime.TotalMinutes, zones.Count));
        foreach (var zone in zones) {
            if (_zones.Remove(zone, out var zoneActor)) {
                _occupied.Remove(zone);
                Context.Stop(zoneActor);
            }
        }

        // A sigil run's container with nothing left in it is forgotten (no one can enter that run again).
        if (_zones.Count == 0 && Classic.GroupInstances.IsRun(_instanceOwnerId)) {
            Context.Parent.Tell(new CLASSIC_FEATURES_PROTOCOL.MSG_RUNCONTAINEREMPTY { OwnerId = _instanceOwnerId });
        }
    }

    private static string DungeonTimerKey(string key) => "classic-empty-dungeon:" + key;

    private List<string> ZonesOfKey(string key) {
        var zones = new List<string>();
        foreach (var zone in _zones.Keys) {
            if (string.Equals(Imlight.Classic.Travel.InstanceGroups.GroupKey(zone), key, StringComparison.OrdinalIgnoreCase)) {
                zones.Add(zone);
            }
        }

        return zones;
    }

    private bool AnyZoneOccupied(string key)
        => ZonesOfKey(key).Exists(zone => _occupied.GetValueOrDefault(zone, true));

    private IActorRef CreateZone(string zoneName) {
        var zoneActorName = SanitizeZoneName(zoneName);
        var zoneId = GetNextDynamicZoneId();
        // CLASSIC: the zone knows it is an instance, so a party loss can reset it (Zone.MSG_INSTANCEPARTYLOST).
        var zone = Context.ActorOf(Zone.Core.Zone.Props(zoneName, zoneId, _instanceOwnerId, _housingDeedId),
            $"{zoneActorName}_{zoneId}");

        // Log the new zone creation.
        Logger.Information("Game world created new zone: {ZoneName}",
            Logger.Args(zoneName));

        return zone;
    }

    private static string SanitizeZoneName(string zoneName)
        => zoneName.Replace('/', '-');

    private uint GetNextDynamicZoneId() {
        uint id;
        var random = new Random();
        do {
            id = (uint) random.Next(1, int.MaxValue);
        } while (_dynamicZoneIds.Contains(id));

        _dynamicZoneIds.Add(id);

        return id;
    }

}
