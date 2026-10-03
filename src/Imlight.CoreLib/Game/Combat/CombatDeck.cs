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
 * COMBAT SPELL DECK MANAGEMENT SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Manages the drawing, discarding, and tracking of spell cards during combat,
 * providing randomized card selection from available spells.
 * 
 * USAGE EXAMPLE:
 * var deck = new CombatDeck(spellDatas, handSize);
 * Hand newHand = deck.GetHand();
 * deck.Discard(spell);
 * 
 * NOTE:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Spells;
using Imlight.Classic;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Manages a participant's spell deck during combat, including drawing cards and handling discards.
/// </summary>
internal class CombatDeck {

    // Card draws: a stream of the duel's seed (CombatRng.DeckStream).
    private readonly Random _rng;

    internal List<Spell> LastGivenHand { get; private set; }
    internal int TotalCardCount => (int) _spellData.Sum(s => s.Quantity);
    internal int RemainingCardCount => (int) _usedUpSpellData.Sum(s => s.Quantity);
    internal int VaultTotalCount => (int) _treasureVault.Sum(s => s.Quantity);
    internal int VaultRemainingCount => (int) _treasureVaultUsed.Sum(s => s.Quantity);
    internal int TreasureCardsInHand { get; private set; }
    // CLASSIC: a creature's deck lists each spell once with 9999 copies (CombatCreatureDeckComponent): it never runs out.
    internal bool IsEndless => _spellData.Any(s => s.Quantity >= 9999);
    internal int DistinctCardCount => _spellData.Count;

    private readonly List<CombatDeckSpellData> _spellData;
    private readonly List<CombatDeckSpellData> _treasureVault;
    private readonly byte _handSize;
    private readonly List<CombatDeckSpellData> _usedUpSpellData;
    private readonly List<CombatDeckSpellData> _treasureVaultUsed;
    private readonly List<Spell> _cardsDiscardedThisTurn;
    // Classic cards leave the draw pile on draw, and enter this pile only when actually spent/discarded.
    private readonly List<CombatDeckSpellData> _classicDiscardPile = [];
    private readonly Dictionary<Spell, SpellTemplate> _enchantedCastTemplates = new(ReferenceEqualityComparer.Instance);

    internal SpellTemplate CastTemplateFor(Spell spell, SpellTemplate original)
        => _enchantedCastTemplates.TryGetValue(spell, out var enchanted) ? enchanted : original;

    // Returns a consumed vault ID for the owning duel to persist, only after successful validation.
    internal bool TryEnchant(int sourceIndex, uint targetIndex, out uint consumedTreasureId) {
        consumedTreasureId = 0;
        if (sourceIndex < 0 || sourceIndex >= LastGivenHand.Count || targetIndex >= LastGivenHand.Count) return false;
        var source = LastGivenHand[sourceIndex];
        var target = LastGivenHand[(int) targetIndex];
        if (!ClassicHandEnchantment.TryPrepare(source, target, out var enchanted, out var template)) return false;
        LastGivenHand[(int) targetIndex] = enchanted;
        _enchantedCastTemplates.Add(enchanted, template);
        if (source.m_treasureCard) consumedTreasureId = ConsumeFromVault(source);
        else Discard(source);
        return true;
    }

    // ctor
    internal CombatDeck(List<CombatDeckSpellData> spellDatas, List<CombatDeckSpellData> treasureVault, byte handSize, Random rng = null) {
        this._rng = rng ?? new Random();
        this._spellData = spellDatas;
        this._treasureVault = treasureVault ?? [];
        this._handSize = handSize;
        this.LastGivenHand = [];
        this._cardsDiscardedThisTurn = [];
        this.TreasureCardsInHand = 0;

        // Clone the spell data into used up spell data.
        this._usedUpSpellData = [];
        foreach (var originalSpellData in _spellData) {
            _usedUpSpellData.Add(new CombatDeckSpellData() {
                TemplateId = originalSpellData.TemplateId,
                Quantity = originalSpellData.Quantity,
                IsBattleCard = originalSpellData.IsBattleCard,
                IsItemCard = originalSpellData.IsItemCard,
                IsTreasureCard = false
            });
        }

        // Clone the treasure vault.
        this._treasureVaultUsed = [];
        foreach (var vaultCard in _treasureVault) {
            _treasureVaultUsed.Add(new CombatDeckSpellData() {
                TemplateId = vaultCard.TemplateId,
                Quantity = vaultCard.Quantity,
                IsBattleCard = vaultCard.IsBattleCard,
                IsItemCard = vaultCard.IsItemCard,
                IsTreasureCard = true
            });
        }
    }

    // CLASSIC: the entry holding the copy at position <paramref name="copy"/> (0 <= copy < RemainingCardCount).
    private int WeightedIndex(int copy) {
        for (var i = 0; i < _usedUpSpellData.Count; i++) {
            copy -= (int) _usedUpSpellData[i].Quantity;
            if (copy < 0) {
                return i;
            }
        }
        return _usedUpSpellData.Count - 1;
    }

    /// <summary>
    /// Gets a new hand of spells, discarding any used or discarded cards.
    /// </summary>
    /// <returns>A new hand of spells.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a spell cannot be created from the template id.</exception>
    internal Hand GetHand() {
        var newCards = new List<Spell>();
        // Discard the cards that were used up or discarded.
        foreach (var spell in _cardsDiscardedThisTurn) {
            LastGivenHand.Remove(spell);

            var spellData = _usedUpSpellData.FirstOrDefault(s => s.TemplateId == spell.m_templateID);
            if (spellData == null) {
                // The spell may not be in this list if the previous hand used them all.
                continue;
            }

            // Decrement the quantity of the spell, or remove it if the quantity is 0.
            if (spellData.Quantity - 1 <= 0) {
                _usedUpSpellData.Remove(spellData);
            }
            else {
                spellData.Quantity--;
            }
        }
        _cardsDiscardedThisTurn.Clear();

        // Refill the hand with new cards.
        var cardsToRefill = _handSize - LastGivenHand.Count;
        for (var i = 0; i < cardsToRefill; i++) {
            if (RemainingCardCount <= 0) {
                break; // No more spells available.
            }

            // CLASSIC: every remaining copy is equally likely (a spell with 4 copies left comes up 4 times as often as one
            // with 1); this drew each distinct spell equally, whatever its count.
            var randomIndex = WeightedIndex(_rng.Next(0, RemainingCardCount));
            var spellData = _usedUpSpellData[randomIndex];
            var spellTemplateId = spellData.TemplateId;

            // Create a new spell from the template id
            var spell = SpellFactory.GetSpell(spellTemplateId)
                ?? throw new InvalidOperationException("Spell could not be created from template id.");
            spell.m_itemCard = spellData.IsItemCard;
            spell.m_battleCard = spellData.IsBattleCard;

            newCards.Add(spell);

            // Decrement the quantity of the spell, or remove it if the quantity is 0.
            if (spellData.Quantity - 1 <= 0) {
                _usedUpSpellData.RemoveAt(randomIndex);
            }
            else {
                spellData.Quantity--;
            }
        }

        // Update the LastGivenHand.
        LastGivenHand.AddRange(newCards);

        return new Hand() { m_spellList = LastGivenHand };
    }

    /// <summary>
    /// Discards a spell from the current hand. Treasure cards are returned to the vault.
    /// </summary>
    /// <param name="spell">The spell to discard.</param>
    internal void Discard(Spell spell) {
        if (ClassicRuntime.IsActive && !spell.m_treasureCard) {
            if (RemoveHeldCard(spell)) {
                _classicDiscardPile.Add(new CombatDeckSpellData {
                    TemplateId = spell.m_premutationSpellID != 0 ? spell.m_premutationSpellID : spell.m_templateID, Quantity = 1,
                    IsBattleCard = spell.m_battleCard, IsItemCard = spell.m_itemCard,
                });
            }
            // Drawing already removed this copy from the draw pile. A repeated discard is a no-op.
            return;
        }

        if (spell.m_treasureCard) {
            ReturnToVault(spell);
        }
        else {
            _cardsDiscardedThisTurn.Add(spell);

            // Free the slot now so a vault draw can be made this turn; GetHand's removal is a no-op.
            LastGivenHand.Remove(spell);
        }
    }

    /// <summary>
    /// Empties the current hand without touching the deck or the vault.
    /// </summary>
    internal void ClearHand() {
        LastGivenHand.Clear();
        _enchantedCastTemplates.Clear();
        _cardsDiscardedThisTurn.Clear();
        TreasureCardsInHand = 0;
    }

    /// <summary>
    /// Adds a spell directly to the current hand without drawing from the deck.
    /// </summary>
    internal void AddCardToHand(Spell spell) {
        LastGivenHand.Add(spell);
    }

    /// <summary>
    /// Draws a random treasure card from the vault and adds it to the current hand.
    /// </summary>
    /// <returns>The drawn spell, or null if the vault is empty or hand is full.</returns>
    internal Spell DrawFromVault() {
        if (VaultRemainingCount <= 0) {
            return null;
        }

        if (LastGivenHand.Count >= _handSize) {
            return null;
        }

        // CLASSIC: a treasure card can be drawn whenever the hand has room and the deck still holds treasure cards, as on
        // live. The rule here required more cards in all than a full hand, so a small deck (six cards and one treasure
        // card) could never draw.

        var randomIndex = _rng.Next(0, _treasureVaultUsed.Count);
        var vaultData = _treasureVaultUsed[randomIndex];

        var spell = SpellFactory.GetSpell(vaultData.TemplateId);
        if (spell == null) {
            return null;
        }

        spell.m_treasureCard = true;
        LastGivenHand.Add(spell);
        TreasureCardsInHand++;

        // Decrement or remove from vault.
        if (vaultData.Quantity - 1 <= 0) {
            _treasureVaultUsed.RemoveAt(randomIndex);
        }
        else {
            vaultData.Quantity--;
        }

        return spell;
    }

    /// <summary>
    /// Returns a treasure card to the vault (e.g., on discard or fizzle).
    /// </summary>
    internal void ReturnToVault(Spell spell) {
        if (!spell.m_treasureCard) {
            return;
        }

        // Classic removals are by card instance and idempotent: a repeated request cannot create
        // a vault copy or consume a second persistent copy with the same template ID.
        if (ClassicRuntime.IsActive) {
            if (!RemoveHeldCard(spell)) {
                return;
            }
        }
        else {
            LastGivenHand.Remove(spell);
        }
        if (TreasureCardsInHand > 0) {
            TreasureCardsInHand--;
        }

        // Return to vault used list.
        var existing = _treasureVaultUsed.Find(v => v.TemplateId == spell.m_templateID);
        if (existing != null) {
            existing.Quantity++;
        }
        else {
            _treasureVaultUsed.Add(new CombatDeckSpellData {
                TemplateId = spell.m_templateID,
                Quantity = 1,
                IsTreasureCard = true
            });
        }
    }

    /// <summary>
    /// Permanently consumes a successfully cast treasure card from the vault.
    /// Returns the template ID so the caller can persist the removal.
    /// </summary>
    internal uint ConsumeFromVault(Spell spell) {
        if (!spell.m_treasureCard) {
            return 0;
        }

        if (ClassicRuntime.IsActive) {
            if (!RemoveHeldCard(spell)) {
                return 0;
            }
        }
        else {
            LastGivenHand.Remove(spell);
        }
        if (TreasureCardsInHand > 0) {
            TreasureCardsInHand--;
        }

        // Remove from the persistent vault list (not the used copy; this removes it permanently).
        var vaultEntry = _treasureVault.Find(v => v.TemplateId == spell.m_templateID);
        if (vaultEntry != null) {
            if (vaultEntry.Quantity - 1 <= 0) {
                _treasureVault.Remove(vaultEntry);
            }
            else {
                vaultEntry.Quantity--;
            }
        }

        return spell.m_templateID;
    }

    /// <summary>
    /// Reshuffles the deck, resetting the used up spell data and clearing the discarded cards.
    /// Vault cards are NOT returned to the deck on reshuffle.
    /// </summary>
    internal void Reshuffle() {
        if (ClassicRuntime.IsActive) {
            // Return only spent/discarded regular cards. The current hand, undrawn deck and treasure
            // vault already own their copies and must not be duplicated by a full-deck reset.
            foreach (var card in _classicDiscardPile) {
                var existing = _usedUpSpellData.FirstOrDefault(s => s.TemplateId == card.TemplateId
                    && s.IsItemCard == card.IsItemCard && s.IsBattleCard == card.IsBattleCard);
                if (existing is null) {
                    _usedUpSpellData.Add(card);
                }
                else {
                    existing.Quantity += card.Quantity;
                }
            }
            _classicDiscardPile.Clear();
            return;
        }

        // Copy spell data back to used up spell data.
        _usedUpSpellData.Clear();
        foreach (var originalSpellData in _spellData) {
            _usedUpSpellData.Add(new CombatDeckSpellData() {
                TemplateId = originalSpellData.TemplateId,
                Quantity = originalSpellData.Quantity,
                IsBattleCard = originalSpellData.IsBattleCard,
                IsItemCard = originalSpellData.IsItemCard,
                IsTreasureCard = false
            });
        }

        _cardsDiscardedThisTurn.Clear();
    }
    
    private bool RemoveHeldCard(Spell spell) {
        int index = LastGivenHand.FindIndex(card => ReferenceEquals(card, spell));
        if (index < 0) {
            return false;
        }
        LastGivenHand.RemoveAt(index);
        _enchantedCastTemplates.Remove(spell);
        return true;
    }

}

