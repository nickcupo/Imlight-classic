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
 * SEEDED COMBAT RANDOMNESS
 * ========================================================================
 *
 * PURPOSE:
 * Every random roll a duel makes (accuracy, power pips, random effects,
 * card draws, first team, monster AI) comes from one seed per duel, so a
 * duel can be replayed exactly: same seed, same moves, same rolls.
 *
 * USAGE EXAMPLE:
 * var seed = CombatRng.NewDuelSeed();                     // logged at duel start
 * var rolls = CombatRng.Stream(seed, CombatRng.DuelStream);
 * var deck = CombatRng.Stream(seed, CombatRng.DeckStream(slot));
 *
 * NOTE:
 * Each consumer gets its own stream derived from the seed (SplitMix64), so
 * the monster AI, which runs on its own actor, never shares a generator with
 * the duel, and one consumer's extra rolls do not shift another's.
 * System.Random with a seed is deterministic for a given .NET runtime.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/28/2026
 */

using System;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Seeds and streams for a duel's random rolls.
/// </summary>
internal static class CombatRng {

    /// <summary>The duel's own rolls: first team, accuracy, random effects, criticals, power pips.</summary>
    internal const int DuelStream = 0;

    /// <summary>
    /// When set, every new duel uses this seed (tests, GM replay). Null: a fresh random seed per duel.
    /// </summary>
    internal static ulong? FixedSeed { get; set; }

    /// <summary>The card draws of the combatant in <paramref name="slot"/>.</summary>
    internal static int DeckStream(int slot) => 100 + slot;

    /// <summary>The monster AI of the combatant in <paramref name="slot"/>.</summary>
    internal static int AiStream(int slot) => 200 + slot;

    /// <summary>A seed for a new duel: <see cref="FixedSeed"/>, else a random one.</summary>
    internal static ulong NewDuelSeed() => FixedSeed ?? (ulong) Random.Shared.NextInt64();

    /// <summary>The generator for one stream of a duel.</summary>
    /// <param name="duelSeed">The duel's seed.</param>
    /// <param name="stream">The stream number (<see cref="DuelStream"/>, <see cref="DeckStream"/>, <see cref="AiStream"/>).</param>
    internal static Random Stream(ulong duelSeed, int stream) {
        var mixed = SplitMix64(duelSeed ^ SplitMix64((ulong) stream + 0x9E3779B97F4A7C15UL));

        return new Random((int) (mixed ^ (mixed >> 32)));
    }

    private static ulong SplitMix64(ulong x) {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;

        return x ^ (x >> 31);
    }

}
