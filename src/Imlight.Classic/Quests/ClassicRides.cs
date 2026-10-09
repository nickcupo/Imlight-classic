// CLASSIC: the 2009 ride zones the travel overlay sends wizards through: the Great Spyre's lift (DS_Volcano_Elevator_Up
// and _Down) and the Oasis boat to and from the Krokosphinx (KT_HubFlyingShip_ToIsland and _ToMainland).
//
// Each ride is a dynamic zone whose own triggers play it: entering the activator volume starts the platform or ship
// (ResModifyTriggerObject) and, after the ride's ResWaits, a teleport trigger sends the wizard on (r806919 zone data).
// The wizard lands inside that volume, and the classic engine never fires a teleport on arrival (VolumeArrival), so on
// its own the ride would not start until the wizard walked out and back in; the lift's last trigger also waits for an
// enter into a second volume the wizard already stands in. RideService therefore posts the start event once as a real
// enter when the wizard arrives, and sends the wizard to the ride's own destination (its final trigger's ZoneTransfer
// record) if the ride has not moved them FallbackSeconds later. No one can be left on a ride.
//
// Waits (seconds) from the zones' ResWait results: lift up 2 + 21 (+10 before re-arming), lift down 2 + 21 (+5),
// boat to the island 45, boat to the mainland 19.5.
#nullable enable
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Quests;

public sealed record ClassicRide(string Zone, string StartEvent, string FinalTrigger, double RideSeconds, double FallbackSeconds,
    string? IgnoredFireEvent = null);

public static class ClassicRides {

    private static readonly FrozenDictionary<string, ClassicRide> s_rides = new ClassicRide[] {
        new("DragonSpire/DS_A3_Kings/DS_A3Z3_Volcano/DS_Volcano_Elevator_Up", "Enter_Activator Volume", "TeleportPlayerToVolcano2", 23, 28),
        new("DragonSpire/DS_A3_Kings/DS_A3Z3_Volcano/DS_Volcano_Elevator_Down", "Enter_Activator Volume", "TeleportToVolcaon1", 23, 28),
        // The ship's EndingDock state event: the server enters a trigger object's state at once, while the client sails
        // for the 45 s ResWait; the same trigger also fires on ActivateFrom_Trigger 4, posted after that wait.
        new("Krokotopia/KT_HubFlyingShip_ToIsland", "Enter_Activator Volume 0", "Trigger", 45, 50, "KT_ShipWorld instance.EndingDock.EnterState"),
        new("Krokotopia/KT_HubFlyingShip_ToMainland", "Enter_Activator Volume 0", "Trigger 4", 19.5, 25),
    }.ToFrozenDictionary(ride => ride.Zone, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every ride.</summary>
    public static IEnumerable<ClassicRide> All => s_rides.Values;

    /// <summary>The ride of <paramref name="zone"/>, or null.</summary>
    public static ClassicRide? Find(string? zone)
        => zone is not null && s_rides.TryGetValue(zone, out var ride) ? ride : null;

    /// <summary>
    /// A ride zone trigger's results with the ZoneTransfer record's teleport: in a ride zone every ResTeleport of the
    /// trigger's own list is replaced in place, so the ride's animation and ResWait run first (stock Imlight replaces the
    /// whole list with the teleport, which made the boat land the instant it left). Elsewhere, and for a trigger without
    /// a teleport of its own, the record's teleport alone. <paramref name="isTeleport"/> tells a ResTeleport apart.
    /// </summary>
    public static List<T> ResultsWithDestination<T>(string? zone, IReadOnlyList<T>? own, T destination, Func<T, bool> isTeleport) {
        if (Find(zone) is null || own is null || !own.Any(isTeleport)) {
            return [destination];
        }

        return [.. own.Select(result => isTeleport(result) ? destination : result)];
    }

    /// <summary>
    /// A ride zone trigger's fire events without the ride's ignored event.
    /// </summary>
    public static List<T>? FireEvents<T>(string? zone, List<T>? events, Func<T, string?> name)
        => Find(zone)?.IgnoredFireEvent is { } ignored && events is not null
            && events.Any(e => string.Equals(name(e), ignored, StringComparison.Ordinal))
            ? [.. events.Where(e => !string.Equals(name(e), ignored, StringComparison.Ordinal))]
            : events;

    /// <summary>
    /// True when the fallback for the arrival stamped <paramref name="arrival"/> should move a wizard who is in
    /// <paramref name="currentZone"/> with the latest arrival <paramref name="latestArrival"/>: still on the same ride,
    /// and not a later visit of it.
    /// </summary>
    public static bool FallbackApplies(ClassicRide ride, string? currentZone, long arrival, long latestArrival)
        => string.Equals(currentZone, ride.Zone, StringComparison.OrdinalIgnoreCase) && arrival == latestArrival;

}
