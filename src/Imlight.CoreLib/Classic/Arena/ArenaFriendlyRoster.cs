// CLASSIC: dormant arena choices are operational AI options, not historical spell/stat overrides.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Pvp;

namespace Imlight.CoreLib.Classic.Arena;

internal sealed record ArenaFriendlyChallenge(ArenaKind Kind, int Level, int School, ArenaPvpSkill Skill, int TeamSize) {
    internal ulong Id => ArenaFriendlyRoster.FirstId + (ulong) (((((int) Kind * 50 + Level - 1) * 7 + School) * 3 + (int) Skill) * 4 + TeamSize - 1) * 4;
    internal ulong[] TeamIds => [Id + 1, Id + 2];
}

internal static class ArenaFriendlyRoster {
    internal const ulong FirstId = 0xAC17_0000_0000_0000;
    internal const int ChallengeCountPerKind = 50 * 7 * 3 * 4;
    internal const int DefaultPageSize = 84;
    internal const int RowsPerMessage = 8;
    // AI's starting standings separate experience tiers. Existing saved standings always take precedence.
    internal static int InitialRating(ArenaPvpSkill skill) => skill switch {
        ArenaPvpSkill.Beginner => 400, ArenaPvpSkill.Intermediate => 600, _ => 900,
    };
    internal static ArenaPvpSkill NearestSkill(int rating) => Enum.GetValues<ArenaPvpSkill>()
        .OrderBy(skill => Math.Abs(InitialRating(skill) - rating))
        .ThenBy(skill => skill == ArenaPvpSkill.Intermediate ? 0 : 1).First();
    internal static ArenaFriendlyChallenge? Read(ulong id) {
        if (id < FirstId || id >= FirstId + (ulong) ChallengeCountPerKind * 2 * 4 || (id - FirstId) % 4 == 3) return null;
        var index = (int) ((id - FirstId) / 4);
        var size = index % 4 + 1; index /= 4;
        var skill = (ArenaPvpSkill) (index % 3); index /= 3;
        var school = index % 7; index /= 7;
        var level = index % 50 + 1;
        return new ArenaFriendlyChallenge((ArenaKind) (index / 50), level, school, skill, size);
    }
    internal static IEnumerable<ArenaFriendlyChallenge> Challenges(ArenaKind kind, int viewerLevel, int? onlyLevel = null) {
        var levels = onlyLevel is { } selected ? new[] { selected }
            : Enumerable.Range(1, 50).OrderBy(level => Math.Abs(level - viewerLevel)).ThenBy(level => level).ToArray();
        foreach (var level in levels) {
            // Interleave schools, skill tiers and team sizes so a short native page still contains a broad selection.
            for (var ordinal = 0; ordinal < 84; ordinal++)
                yield return new ArenaFriendlyChallenge(kind, level, (ordinal / 12 + ordinal % 12) % 7,
                    (ArenaPvpSkill) (ordinal / 4 % 3), ordinal % 4 + 1);
        }
    }
}
