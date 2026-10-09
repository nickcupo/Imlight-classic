/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * HENCHMAN RULES (CLASSIC)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-09): when a duel takes a henchman hired from the Crown
 * Shop, and what the player is told when it does not. October 2009 notes:
 * henchmen "can only be purchased during a duel", "at or below your current
 * level", "act in the same round that they are summoned" and "disappear once
 * the duel has ended". The r806919 client's own texts name the refusals
 * (Error_HenchmenNotInCombat, Error_HenchmenInPVP, Error_HenchmenPlanningPhase,
 * Error_HenchmenSigilFull, CrownShopSWF_noHenchmenThisSigil) and its Crown
 * Shop item check reads the duel's m_bPVP and m_noHenchmen
 * (WizardGraphicalClient 0x1404b3290..0x1404b33ae).
 *
 * USAGE EXAMPLE:
 * var refusal = HenchmanRules.Check(inDuel, pvp, sigilForbids, planning, buyerReady, seatFree, known);
 * var text = HenchmanRules.ClientText(refusal);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

namespace Imlight.Classic.Rules;

/// <summary>Why a duel did not take a hired henchman (None: it joined).</summary>
public enum HenchmanRefusal : byte {
    None = 0,
    NotInCombat = 1,
    Pvp = 2,
    NotThisSigil = 3,
    NotPlanning = 4,
    SigilFull = 5,
    Unavailable = 6,
}

/// <summary>The henchman hire rules (see the file header).</summary>
public static class HenchmanRules {

    /// <summary>
    /// Whether a duel takes a henchman now, checked in the order the client reports them: not in combat, PvP, a circle
    /// that forbids henchmen, outside card selection, the buyer cannot hire (gone, defeated, or not a wizard), no free
    /// seat on the buyer's side, an unknown henchman.
    /// </summary>
    public static HenchmanRefusal Check(bool inDuel, bool pvp, bool sigilForbids, bool planning, bool buyerReady,
                                        bool seatFree, bool known) {
        if (!inDuel) return HenchmanRefusal.NotInCombat;
        if (pvp) return HenchmanRefusal.Pvp;
        if (sigilForbids) return HenchmanRefusal.NotThisSigil;
        if (!planning) return HenchmanRefusal.NotPlanning;
        if (!buyerReady) return HenchmanRefusal.Unavailable;
        if (!seatFree) return HenchmanRefusal.SigilFull;
        return known ? HenchmanRefusal.None : HenchmanRefusal.Unavailable;
    }

    /// <summary>The client's own text for a refusal (r806919 locale), or null for None.</summary>
    public static string? ClientText(HenchmanRefusal refusal) => refusal switch {
        HenchmanRefusal.None => null,
        HenchmanRefusal.NotInCombat => "You can only purchase a Henchman while in combat.",       // Error_HenchmenNotInCombat
        HenchmanRefusal.Pvp => "You cannot purchase a Henchman in PVP combat.",                   // Error_HenchmenInPVP
        HenchmanRefusal.NotThisSigil => "Henchmen cannot be hired in this combat.",              // CrownShopSWF_noHenchmenThisSigil
        HenchmanRefusal.NotPlanning => "You can only purchase a Henchman during card selection.", // Error_HenchmenPlanningPhase
        HenchmanRefusal.SigilFull => "Your duel circle is full, you cannot purchase a Henchman.", // Error_HenchmenSigilFull
        _ => "Henchmen cannot be hired in this combat.",
    };

    /// <summary>
    /// The henchman's level: the shop item's "Level N" (its level floor; T2-T5 are 20, 30, 40, 50). Its wizard deck is
    /// built for this level. Never below 1.
    /// </summary>
    public static int Level(CrownShopEntry item) => item.MinLevel < 1 ? 1 : item.MinLevel;

}
