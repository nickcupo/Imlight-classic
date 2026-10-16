// CLASSIC: the arena's level range follows the active profile's level cap (50 in the 2009 and October 2010
// profiles, 60 in celestia-2010) instead of a literal 50. Ids of levels 1-50 keep their original layout, so saved
// Ranked standings and every existing id are unchanged; levels 51 and up live in a separate block after them.
#nullable enable
using System;
using Imlight.Classic;

namespace Imlight.CoreLib.Classic.Arena;

internal static class ArenaLevels {
    /// <summary>The levels of the original id layout (levels 1-50).</summary>
    internal const int Arc1Levels = ClassicProfiles.Arc1LevelCap;
    /// <summary>The most levels the id blocks reserve room for (levels 51-100 in the extension block).</summary>
    internal const int Supported = 100;
    /// <summary>The profile's level cap (50 without a cap or before the rules load), within the id space.</summary>
    internal static int Cap => Math.Clamp(ClassicRuntime.IsInitialized ? ClassicProfiles.MaxLevelOf(ClassicRuntime.Rules.Profile.Id)
        : Arc1Levels, 1, Supported);
    internal static int Clamp(int level) => Math.Clamp(level, 1, Cap);
    /// <summary>A step coprime with <paramref name="count"/>: 11 for 50 and 60, so autonomous levels visit every level.</summary>
    internal static int Stride(int count) {
        var step = 11;
        while (Gcd(step, Math.Max(1, count)) != 1) step++;
        return step;
    }
    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return Math.Abs(a); }
}
