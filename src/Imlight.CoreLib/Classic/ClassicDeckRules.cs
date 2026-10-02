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
 * CLASSIC DECK RULES
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: may this card go into this deck? Exactly the stock client's own
 * check (r806919 WizardGraphicalClient 0x141abe610, used by the deck screen
 * before it asks the server), so the server never refuses a card the client
 * has already shown in the deck (it would then vanish), and never allows one
 * the client would not:
 *
 *   Treasure Card (template m_Treasure, or an enchanted card): the deck's
 *     Treasure Cards together stay under m_maxTreasureCards. No per-card limit.
 *   Any other card: the deck's other cards stay under m_maxSpells; no fusion
 *     card (m_spellFusion); copies of the card stay under m_schoolMaxInstances
 *     (the card's school is the deck's school) or m_genericMaxInstances,
 *     lowered by the card's own m_maxCopies when it has one; and the card's
 *     rank is at most m_schoolMaxRank / m_genericMaxRank (-1: no limit).
 *
 * USAGE EXAMPLE:
 * var refusal = ClassicDeckRules.CanAdd(deckTemplate, deck.m_spellList, templateId, SpellFactory.GetSpellTemplate);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic;

internal enum DeckAddRefusal {
    None,
    UnknownSpell,
    TreasureCardsFull,
    DeckFull,
    FusionCard,
    TooManyCopies,
    RankTooHigh,
}

internal static class ClassicDeckRules {
    /// <summary>The client's test for a Treasure Card entry: an enchanted card, or a template marked m_Treasure.</summary>
    internal static bool IsTreasureEntry(SpellTemplate? template, uint enchantment)
        => enchantment != 0 || template?.m_Treasure == true;

    internal static DeckAddRefusal CanAdd(DeckBehaviorTemplate deck, IReadOnlyList<SpellData>? spells, uint templateId,
                                          Func<uint, SpellTemplate?> lookup, uint enchantment = 0) {
        var template = lookup(templateId);
        if (template is null) return DeckAddRefusal.UnknownSpell;
        spells ??= [];
        var treasure = IsTreasureEntry(template, enchantment);

        var sameKind = 0;
        foreach (var entry in spells) {
            if (entry is null) continue;
            if (IsTreasureEntry(lookup(entry.m_templateID), entry.m_enchantment) == treasure) sameKind += (int) entry.m_quantity;
        }

        if (sameKind >= (treasure ? deck.m_maxTreasureCards : deck.m_maxSpells)) {
            return treasure ? DeckAddRefusal.TreasureCardsFull : DeckAddRefusal.DeckFull;
        }

        if (treasure) return DeckAddRefusal.None;

        var copies = 0;
        foreach (var entry in spells) {
            if (entry is null || entry.m_templateID != templateId) continue;
            if (!IsTreasureEntry(lookup(entry.m_templateID), entry.m_enchantment)) copies += (int) entry.m_quantity;
        }

        var schoolCard = !string.IsNullOrEmpty(template.m_sMagicSchoolName) && !string.IsNullOrEmpty(deck.m_primarySchoolName)
            && StringHash.Compute(template.m_sMagicSchoolName) == StringHash.Compute(deck.m_primarySchoolName);
        if (template.m_spellFusion != 0) return DeckAddRefusal.FusionCard;
        var limit = schoolCard ? deck.m_schoolMaxInstances : deck.m_genericMaxInstances;
        if (template.m_maxCopies > 0 && template.m_maxCopies < limit) limit = (int) template.m_maxCopies;
        if (copies >= limit) return DeckAddRefusal.TooManyCopies;

        var rankLimit = schoolCard ? deck.m_schoolMaxRank : deck.m_genericMaxRank;
        if (rankLimit > -1 && (template.m_spellRank?.m_spellRank ?? 0) > rankLimit) return DeckAddRefusal.RankTooHigh;
        return DeckAddRefusal.None;
    }

    /// <summary>The deck rules of a deck item's template, or null.</summary>
    internal static DeckBehaviorTemplate? DeckTemplateOf(uint itemTemplateId) {
        if (Imlight.CoreLib.Shared.Resources.CoreObjectFactory.GetCoreTemplate(itemTemplateId) is not WizItemTemplate item || item.m_behaviors is null) return null;
        foreach (var behavior in item.m_behaviors) {
            if (behavior is DeckBehaviorTemplate deck) return deck;
        }

        return null;
    }
}
