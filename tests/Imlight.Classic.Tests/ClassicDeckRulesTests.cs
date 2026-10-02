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
 * CLASSIC DECK RULES TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the server's deck check is the stock client's (Classic/ClassicDeckRules.cs):
 * Treasure Cards only against the deck's Treasure Card room, other cards against the
 * deck size, copy limits and rank; and the wizard's Treasure Card book reaches the
 * client at login (Game/WizardObjectLoader.cs).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ClassicDeckRulesTests {
    private const uint MythSpell = 1, FireSpell = 2, SummonTc = 3, KillTc = 4, Fusion = 5, OneCopy = 6, HighRank = 7;

    private static readonly Dictionary<uint, SpellTemplate> Templates = new() {
        [MythSpell] = new SpellTemplate { m_name = "Troll", m_sMagicSchoolName = "Myth", m_spellRank = new SpellRank { m_spellRank = 3 } },
        [FireSpell] = new SpellTemplate { m_name = "Fire Cat", m_sMagicSchoolName = "Fire", m_spellRank = new SpellRank { m_spellRank = 1 } },
        [SummonTc] = new SpellTemplate { m_name = "Summon1489286", m_sMagicSchoolName = "Myth", m_Treasure = true },
        [KillTc] = new SpellTemplate { m_name = "Kill1489286", m_sMagicSchoolName = "Myth", m_Treasure = true },
        [Fusion] = new SpellTemplate { m_name = "Fused", m_sMagicSchoolName = "Myth", m_spellFusion = 1 },
        [OneCopy] = new SpellTemplate { m_name = "Unique", m_sMagicSchoolName = "Myth", m_maxCopies = 1 },
        [HighRank] = new SpellTemplate { m_name = "Big", m_sMagicSchoolName = "Fire", m_spellRank = new SpellRank { m_spellRank = 8 } },
    };

    private static SpellTemplate? Lookup(uint id) => Templates.GetValueOrDefault(id);

    // A Myth deck: 6 spells, 3 Treasure Cards, 4 Myth copies, 2 others, ranks up to 7 (school) and 5 (others).
    private static DeckBehaviorTemplate Deck() => new() {
        m_primarySchoolName = "Myth", m_maxSpells = 6, m_maxTreasureCards = 3,
        m_schoolMaxInstances = 4, m_genericMaxInstances = 2, m_schoolMaxRank = 7, m_genericMaxRank = 5,
    };

    private static List<SpellData> Cards(params (uint Id, uint Count)[] entries)
        => entries.Select(e => new SpellData { m_templateID = e.Id, m_quantity = e.Count }).ToList();

    private static DeckAddRefusal Add(List<SpellData> deck, uint id) => ClassicDeckRules.CanAdd(Deck(), deck, id, Lookup);

    [Fact]
    public void TreasureCardsHaveNoPerCardLimitOnlyTheDecksTreasureRoom() {
        Assert.Equal(DeckAddRefusal.None, Add(Cards((SummonTc, 2)), SummonTc));          // a third copy: fine
        Assert.Equal(DeckAddRefusal.TreasureCardsFull, Add(Cards((SummonTc, 2), (KillTc, 1)), SummonTc));
    }

    [Fact]
    public void TreasureCardsDoNotUseTheMainDecksRoomAndTheMainDeckDoesNotUseTheirs() {
        var fullMain = Cards((MythSpell, 4), (FireSpell, 2));                          // 6 of 6
        Assert.Equal(DeckAddRefusal.None, Add(fullMain, SummonTc));
        Assert.Equal(DeckAddRefusal.DeckFull, Add(fullMain, MythSpell));
        Assert.Equal(DeckAddRefusal.None, Add(Cards((SummonTc, 2), (KillTc, 1)), FireSpell));
    }

    [Fact]
    public void OtherCardsKeepTheSchoolAndGenericCopyLimitsTheCardsOwnLimitAndRank() {
        Assert.Equal(DeckAddRefusal.None, Add(Cards((MythSpell, 3)), MythSpell));
        Assert.Equal(DeckAddRefusal.TooManyCopies, Add(Cards((MythSpell, 4)), MythSpell));
        Assert.Equal(DeckAddRefusal.TooManyCopies, Add(Cards((FireSpell, 2)), FireSpell));
        Assert.Equal(DeckAddRefusal.TooManyCopies, Add(Cards((OneCopy, 1)), OneCopy));
        Assert.Equal(DeckAddRefusal.FusionCard, Add(Cards(), Fusion));
        Assert.Equal(DeckAddRefusal.RankTooHigh, Add(Cards(), HighRank));
        Assert.Equal(DeckAddRefusal.UnknownSpell, Add(Cards(), 999));
    }

    [Fact]
    public void AnEnchantedCardCountsAsATreasureCard() {
        Assert.True(ClassicDeckRules.IsTreasureEntry(Templates[MythSpell], enchantment: 77));
        var deck = new List<SpellData> { new() { m_templateID = MythSpell, m_enchantment = 77, m_quantity = 3 } };
        Assert.Equal(DeckAddRefusal.TreasureCardsFull, Add(deck, KillTc));
        Assert.Equal(DeckAddRefusal.None, Add(deck, MythSpell)); // the enchanted copies are not main-deck copies
    }

    [Fact]
    public void TheTreasureBookGoesToTheClientGroupedByCard() {
        var book = WizardObjectLoader.TreasureBookSpells([694954949u, 1264810442u, 694954949u, 0u]);
        Assert.Equal(2, book.Count);
        Assert.Equal(2u, book.Single(s => s.m_templateID == 694954949u).m_quantity);
        Assert.Equal(1u, book.Single(s => s.m_templateID == 1264810442u).m_quantity);
        Assert.Empty(WizardObjectLoader.TreasureBookSpells(null!));
    }
}
