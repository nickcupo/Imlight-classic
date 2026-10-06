// CLASSIC: temporary later-era eligibility explicitly approved by the owner for the October 2010 Arc 1 stage.
// Exact October eligibility remains unverified in the Cloak record; other profiles and trainers are unchanged.

using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Pvp;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

internal static class ClassicOctoberTraining {
    internal const ulong Diego = 38226;
    internal const uint Cloak = 76658083;

    internal static bool Applies(ulong trainer, ulong spell)
        => ClassicOctoberRules.Active && trainer == Diego && spell == Cloak;

    internal static string RequiredRank(ulong spell) => "Private";
    internal static int RequiredLevel(ulong spell) => 0;

    internal static bool CanTrain(ulong trainer, ulong spell, int level, int rating, ArenaConfig config)
        => !Applies(trainer, spell) || config is not null && level >= RequiredLevel(spell)
            && rating >= ArenaRules.MinRatingOf(RequiredRank(spell), config.Ranks);

    internal static bool CanTrain(Wizard wizard, ulong trainer, ulong spell) {
        if (!Applies(trainer, spell)) return true;
        var config = ClassicArena.Config;
        if (config is null || wizard.MagicSchoolBehavior.Level < RequiredLevel(spell)) return false;
        var rating = new ArenaLadderCollection.Raven().Load(wizard.CharId)?.Rating ?? config.StartRating;
        return CanTrain(trainer, spell, wizard.MagicSchoolBehavior.Level, rating, config);
    }

    // CLASSIC: the client's existing requirement displays the approved minimum rank; the server enforces it too.
    internal static Requirement RankRequirement(ulong spell) => new ReqArenaSeasonTitle {
        m_applyNOT = false, m_operator = Operator.ROP_AND,
        m_tournament = ClassicArena.Config?.RankedTournament ?? "PvPSanctioned",
        m_title = RequiredRank(spell),
    };
}
