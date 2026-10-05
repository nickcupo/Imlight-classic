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
 * DECK EDIT GUARD
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: what a client's deck edit may do (security audit 2026-10-04). MSG_ADDSPELLTODECK used to put any spell
 * template in a deck, and MSG_REMOVETREASURESPELLFROMDECK returned any removed card to the Treasure Card book, so a
 * learned spell added and removed again minted Treasure Cards.
 *   Add (MSG_ADDSPELLTODECK): the spell must be learned and must not be a Treasure Card template (those go through
 *     MSG_ADDTREASURESPELLTODECK, which spends a book copy).
 *   Remove as treasure (MSG_REMOVETREASURESPELLFROMDECK): handled by the deck Treasure Card ledger
 *     (WizardCollection.MoveTreasureCardFromDeck): only a card that went in as a Treasure Card comes out as one.
 *
 * NOTE:
 * IsTreasureEntry is the single "is this deck entry a Treasure Card" decision, the same one combat makes for the
 * vault (CombatDuelSubCircle: m_Treasure, a " TC" name, or a card the wizard never learned). It can later read the
 * deck Treasure Card ledger instead without touching the caller.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

#nullable enable

using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic;

internal enum DeckEditRefusal {
    None,
    UnknownSpell,
    NotLearned,
    TreasureCard,
}

internal static class DeckEditGuard {

    /// <summary>Is this deck entry a Treasure Card? (Swap point for a per-entry flag.)</summary>
    internal static bool IsTreasureEntry(SpellTemplate? template, uint enchantment, bool learned)
        => ClassicDeckRules.IsTreasureEntry(template, enchantment)
            || template?.m_name?.EndsWith(" TC") == true
            || !learned;

    /// <summary>May MSG_ADDSPELLTODECK put this spell in a deck?</summary>
    internal static DeckEditRefusal CanAddSpell(SpellTemplate? template, bool learned) {
        if (template is null) return DeckEditRefusal.UnknownSpell;
        if (!learned) return DeckEditRefusal.NotLearned;
        if (IsTreasureEntry(template, 0, learned)) return DeckEditRefusal.TreasureCard;

        return DeckEditRefusal.None;
    }

}
