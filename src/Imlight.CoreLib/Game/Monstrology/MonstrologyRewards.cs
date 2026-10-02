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
 * MONSTROLOGY EXTRACTION REWARDS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: what a won duel's Extract Animus hits are worth. The shape is
 * KingsIsle's own tome text (r806919 GUI2.lang):
 *  - 00000303: one Animus per monster for a successful damage hit, a
 *    second if the hit defeats it (MonstrologyPendingExtraction counts these);
 *  - 00000304: only a small chance from monsters ranked above your level;
 *  - 00000305: a chance of a bonus Animus when your level is above the rank;
 *  - 00001112: no Monstrology XP more than 5 levels above a monster's rank.
 * KingsIsle never published the numbers, so these are ours and tunable:
 * a monster one rank above you gives Animus half the time, 15 points less
 * per further rank (never below 10%); the bonus chance is 10% per level
 * above the rank (at most 50%); XP is [Classic] MonstrologyXpPerRank
 * (default 10) times the rank per Animus, doubled for bosses. With the
 * client's XP table (level 2 at 10 XP, 3 at 30, 4 at 60...) a new wizard
 * reaches level 2 with one Wizard City extraction.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Monstrology;

internal readonly record struct ExtractionOutcome(bool Success, int Animus, int Experience, bool Bonus);

internal static class MonstrologyRewards {
    internal const int DefaultXpPerRank = 10;

    internal static int XpPerRank {
        get {
            var value = ConfigurationManager.Settings["Classic.MonstrologyXpPerRank"].AsString();
            return int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : DefaultXpPerRank;
        }
    }

    /// <summary>Percent chance that an extraction from a monster of this rank succeeds at this Monstrology level.</summary>
    internal static int SuccessChance(int level, int rank)
        => rank <= level ? 100 : Math.Max(10, 50 - 15 * (rank - level - 1));

    /// <summary>Percent chance of one bonus Animus.</summary>
    internal static int BonusChance(int level, int rank)
        => level > rank ? Math.Min(50, 10 * (level - rank)) : 0;

    /// <param name="level">The wizard's Monstrology level.</param>
    /// <param name="rank">The monster's rank (its NPC level).</param>
    /// <param name="observed">Animus the hits earned (1 per damaging hit, +1 for the defeat).</param>
    /// <param name="boss">Boss monsters give double XP.</param>
    /// <param name="roll">A d100 roller (1..100), injectable for tests.</param>
    internal static ExtractionOutcome Decide(int level, int rank, int observed, bool boss, Func<int> roll, int xpPerRank = DefaultXpPerRank) {
        rank = Math.Max(1, rank);
        level = Math.Max(1, level);
        if (observed <= 0 || roll() > SuccessChance(level, rank)) return new(false, 0, 0, false);
        var bonus = BonusChance(level, rank) > 0 && roll() <= BonusChance(level, rank);
        var animus = observed + (bonus ? 1 : 0);
        var xp = level > rank + 5 ? 0 : animus * xpPerRank * rank * (boss ? 2 : 1);
        return new(true, animus, xp, bonus);
    }
}
