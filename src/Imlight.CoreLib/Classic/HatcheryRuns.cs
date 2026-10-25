#nullable enable

using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// CLASSIC: the pairs a Pet Hatchery sigil sent into a private copy of ThePhantomZoneWorld/PetGameMorph, by the copy's
/// run id (its instance owner). The wizard who stepped on first is side 0, the second side 1: the r806919 morph window
/// takes its side from MSG_PETGAMEINIT's Track (HatcherySigilComponent, PetGameService.Morph).
/// </summary>
internal static class HatcheryRuns {

    /// <summary>The morph game's zone.</summary>
    internal const string MorphZone = "ThePhantomZoneWorld/PetGameMorph";
    /// <summary>The Hatchery, and where a wizard goes back to from the morph game.</summary>
    internal const string HatcheryZone = "WizardCity/Interiors/WC_Hatchery";
    internal const string HatcheryLocation = "InsideHatchery";

    /// <summary>How long a pair may take to arrive (a slow zone load); a run is forgotten after this.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private sealed record Run(ulong[] Sides, DateTime ExpiresUtc);

    private static readonly ConcurrentDictionary<ulong, Run> s_runs = new();

    /// <summary>Records a pair under <paramref name="runId"/>; side i is <paramref name="sides"/>[i].</summary>
    internal static void Register(ulong runId, ulong[] sides, DateTime nowUtc) {
        foreach (var (id, run) in s_runs) {
            if (run.ExpiresUtc <= nowUtc) s_runs.TryRemove(id, out _);
        }
        s_runs[runId] = new Run([.. sides], nowUtc + Lifetime);
    }

    /// <summary>The side of <paramref name="charId"/> in run <paramref name="runId"/>, or null when it is not one of
    /// that run's pair (or the run is gone).</summary>
    internal static int? SideOf(ulong runId, ulong charId, DateTime nowUtc) {
        if (runId == 0 || !s_runs.TryGetValue(runId, out var run) || run.ExpiresUtc <= nowUtc) return null;
        var side = Array.IndexOf(run.Sides, charId);
        return side < 0 ? null : side;
    }

    /// <summary>The morph lobby key of a run: one pair, one copy of the zone.</summary>
    internal static string LobbyKey(ulong runId) => $"{MorphZone}#{runId}";

    internal static void ClearForTests() => s_runs.Clear();

    internal static int CountForTests => s_runs.Count(r => r.Value.Sides.Length > 0);
}
