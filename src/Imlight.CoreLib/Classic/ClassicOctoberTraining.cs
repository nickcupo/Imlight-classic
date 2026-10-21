// CLASSIC: temporary later-era eligibility explicitly approved by the owner for the October 2010 Arc 1 stage.
// Exact October eligibility remains unverified in the Cloak record; other profiles and trainers are unchanged.

using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Pvp;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

internal static class ClassicOctoberTraining {
    internal const ulong Diego = 38226;
    internal const uint Cloak = 76658083;

    /// <summary>
    /// The Stun Block template id once the trainer refresh has resolved it (0 before). wintertusk-2011 moves the card to
    /// Diego at rank Private (May 2011 notes, patch 1.140).
    /// </summary>
    internal static ulong StunBlock { get; set; }

    internal static bool Applies(ulong trainer, ulong spell)
        => trainer == Diego && (CloakApplies(spell) || StunBlockApplies(spell));

    // CLASSIC: the owner's Private-rank stand-in for Cloak belongs to october-2010-arc1 only; from celestia-2010 on Cloak
    // comes from the Sun trainer for training points (owner ruling, 2026-10-16; package C).
    private static bool CloakApplies(ulong spell)
        => spell == Cloak && ClassicOctoberRules.Active && !ClassicProfiles.IsCelestiaOrLater(ClassicRuntime.Rules.Profile);

    private static bool StunBlockApplies(ulong spell)
        => StunBlock != 0 && spell == StunBlock && ClassicRuntime.IsActive
            && ClassicProfiles.IsWintertuskFamily(ClassicRuntime.Rules.Profile);

    internal static string RequiredRank(ulong spell) => "Private";
    internal static int RequiredLevel(ulong spell) => 0;

    internal static bool CanTrain(ulong trainer, ulong spell, int level, int rating, ArenaConfig config)
        => !Applies(trainer, spell) || config is not null && level >= RequiredLevel(spell)
            && rating >= ArenaRules.MinRatingOf(RequiredRank(spell), config.Ranks);

    internal static bool CanTrain(Wizard wizard, ulong trainer, ulong spell) {
        // CLASSIC OWNER EXTRA: a Weave card is taught only to wizards of its own school (window, service and save path).
        if (!ClassicWeaving.MayTrain(wizard.MagicSchoolBehavior.MagicSchool.ToString(), spell)) return false;
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
