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
 * SECURITY WORLD SERVER TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the security audit's (2026-10-04) checks that use CoreLib types: deck edits (no unlearned cards, no minted
 * Treasure Cards) and the friends list (teleport to friends only, stats, ignore cap).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class SecurityWorldServerTests {

    private static readonly SpellTemplate FireCat = new() { m_name = "Fire Cat", m_sMagicSchoolName = "Fire" };
    private static readonly SpellTemplate TreasureTroll = new() { m_name = "Troll", m_sMagicSchoolName = "Myth", m_Treasure = true };
    private static readonly SpellTemplate NamedTc = new() { m_name = "Fire Elf TC", m_sMagicSchoolName = "Fire" };

    // ---- HIGH 2: deck edits ----

    [Fact]
    public void OnlyALearnedNonTreasureSpellGoesInByTheSpellAdd() {
        Assert.Equal(DeckEditRefusal.None, DeckEditGuard.CanAddSpell(FireCat, learned: true));
        Assert.Equal(DeckEditRefusal.NotLearned, DeckEditGuard.CanAddSpell(FireCat, learned: false));
        Assert.Equal(DeckEditRefusal.TreasureCard, DeckEditGuard.CanAddSpell(TreasureTroll, learned: true));
        Assert.Equal(DeckEditRefusal.TreasureCard, DeckEditGuard.CanAddSpell(NamedTc, learned: true));
        Assert.Equal(DeckEditRefusal.UnknownSpell, DeckEditGuard.CanAddSpell(null, learned: true));
    }

    [Fact]
    public void TheTreasureEntryDecisionMatchesCombatsVault() {
        Assert.True(DeckEditGuard.IsTreasureEntry(FireCat, enchantment: 5, learned: true)); // enchanted
        Assert.False(DeckEditGuard.IsTreasureEntry(FireCat, enchantment: 0, learned: true));
    }

    // ---- HIGH/MED 4, MED 15, LOW: friends ----

    private static Relationship Friends() => new(1, 2, addedViaTrueFriend: false, bestFriends: false, blocked: false, isBrokenUp: false);

    [Fact]
    public void TeleportIsToAFriendOnly() {
        Assert.Equal(GoToPlayerRefusal.None, FriendRules.CheckGoTo(1, 2, Friends(), inDuel: false, targetInMinigame: false));
        Assert.Equal(GoToPlayerRefusal.NotFriend, FriendRules.CheckGoTo(1, 2, null, false, false));
        var ignored = Friends();
        ignored.Blocked = true;
        Assert.Equal(GoToPlayerRefusal.NotFriend, FriendRules.CheckGoTo(1, 2, ignored, false, false));
        var exFriend = Friends();
        exFriend.IsBrokenUp = true;
        Assert.Equal(GoToPlayerRefusal.NotFriend, FriendRules.CheckGoTo(1, 2, exFriend, false, false));
        Assert.Equal(GoToPlayerRefusal.Self, FriendRules.CheckGoTo(1, 1, Friends(), false, false));
        Assert.Equal(GoToPlayerRefusal.InDuel, FriendRules.CheckGoTo(1, 2, Friends(), inDuel: true, targetInMinigame: false));
        Assert.Equal(GoToPlayerRefusal.Minigame, FriendRules.CheckGoTo(1, 2, Friends(), inDuel: false, targetInMinigame: true));
    }

    [Fact]
    public void StatsAreForOneselfFriendsAndWizardsNearby() {
        Assert.True(FriendRules.MayViewStats(1, 1, null, targetInSameZone: false));
        Assert.True(FriendRules.MayViewStats(1, 2, Friends(), targetInSameZone: false));
        Assert.True(FriendRules.MayViewStats(1, 2, null, targetInSameZone: true));
        Assert.False(FriendRules.MayViewStats(1, 2, null, targetInSameZone: false));
    }

    [Fact]
    public void TheIgnoreListIsCapped() {
        Assert.True(FriendRules.MayIgnoreAnother(FriendRules.MaxIgnored - 1));
        Assert.False(FriendRules.MayIgnoreAnother(FriendRules.MaxIgnored));
    }

}
