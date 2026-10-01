using System;
using System.Linq;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Monstrology;

internal static class MonstrologyProgression {
    private static readonly Lazy<int[]> s_thresholds = new(() => {
        using var stream = RootArchiveLoader.GetFileStream("MonsterMagicXPConfig.xml")
            ?? throw new InvalidOperationException("Installed MonsterMagicXPConfig.xml is missing");
        return Thresholds(stream.ToArray());
    });
    internal static int[] InstalledThresholds => s_thresholds.Value;
    // Owned PE 141e3ffe0 sums costs before the requested level. 141e40020 compares XP with running costs.
    internal static int[] Thresholds(byte[] bind) {
        var levels = MonstrologyMetadata.ReadLevels(bind, out var maximum);
        var ordered = levels.OrderBy(x => x.Level).ToArray();
        if (ordered.Length != maximum + 1 || ordered.Where((x,i) => x.Level != i).Any()
            || ordered[0].ExperienceValue != 0 || ordered.Skip(1).Any(x => x.ExperienceValue <= 0))
            throw new ArgumentException("Installed Monstrology progression is not contiguous");
        var result = new int[ordered.Length];
        for (var i = 1; i < result.Length; i++) result[i] = checked(result[i-1] + ordered[i-1].ExperienceValue);
        return result;
    }
}
