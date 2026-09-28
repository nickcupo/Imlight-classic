using System;

namespace Imlight.Classic.Rules;

/// <summary>Per-round power-pip probability. Level-derived base and gear chances are fractions;
/// Power Play's global parameter is percentage points (+35 means +0.35, not a 35% multiplier).</summary>
public static class PowerPipRules {
    public static double Chance(float baseChance, float gearBonus, int globalPercent)
        => Math.Clamp((double) baseChance + gearBonus + globalPercent / 100.0, 0.0, 1.0);

    /// <summary>Compare a uniform roll in [0, 1) with the final chance. Zero never succeeds;
    /// one always succeeds. A strict comparison avoids the old extra 1% from an inclusive integer roll.</summary>
    public static bool GainsPowerPip(float baseChance, float gearBonus, int globalPercent, double roll) {
        if (double.IsNaN(roll) || roll < 0 || roll >= 1) {
            throw new ArgumentOutOfRangeException(nameof(roll), "A power-pip roll must be in [0, 1).");
        }

        return roll < Chance(baseChance, gearBonus, globalPercent);
    }
}
