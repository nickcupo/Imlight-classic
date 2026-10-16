using System;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic.Gardening;

// CLASSIC: a wizard's gardening rank (December 2010: ten ranks, Novice to Grandmaster). The rank follows from the saved
// gardening XP through the profile's rank table (rules.gardening ranks: the client's GardeningXPConfig, unverified); with
// no table the stored level is used, never below rank 1 (a new gardener is a Novice).
internal static class GardenProgress {
    internal static int Rank(Wizard wizard) => RankFor(wizard?.GameStats?.m_gardeningXP ?? 0, wizard?.GameStats?.m_gardeningLevel ?? 0);

    internal static int RankFor(int xp, int stored) {
        var tables = ClassicGardening.Active;
        return tables.MaxRank > 0 ? tables.RankFor(Math.Max(0, xp)) : Math.Max(1, stored);
    }
}
