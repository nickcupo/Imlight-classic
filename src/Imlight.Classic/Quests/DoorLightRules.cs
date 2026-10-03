using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Quests;

/// <summary>Door visuals reflect the first eligible resolved teleport, never a later alternative.</summary>
public static class DoorLightRules {
    public readonly record struct Route(string Destination, bool Armed, bool RequirementsMet) { public bool ZoneAllowed { get; init; } = true; }

    // Match ZoneService transfer policy without emitting per-refresh audit traffic.
    public static Route ApplyZonePolicy(Route route, ClassicRules rules) => route with { ZoneAllowed = rules.IsZoneAllowed(route.Destination).Allowed };

    /// <summary>Null binding means no decision: preserve the placed client state.</summary>
    public static string? State(IReadOnlyList<Route>? boundRoutes, IEnumerable<string> activeDestinations) {
        if (boundRoutes is null) return null;
        var route = boundRoutes.FirstOrDefault(r => r.Armed && r.RequirementsMet && !string.IsNullOrEmpty(r.Destination));
        if (string.IsNullOrEmpty(route.Destination) || !route.ZoneAllowed) return "Off";
        return activeDestinations.Any(d => string.Equals(d, route.Destination, StringComparison.OrdinalIgnoreCase)) ? "Quest" : "On";
    }
}
