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
 * CLASSIC PET DANCE GAME
 * ========================================================================
 *
 * PURPOSE:
 * The server half of the Dance Game: the arrow sequence of each round and
 * the judgement of the wizard's answer.
 *
 * USAGE EXAMPLE:
 * var game = new DanceGame(random);
 * send(MSG_PETGAMEDANCE { Moves = game.NextRound() });
 * var ok = game.Answer(clientMoves); if (game.IsOver) send END with game.Successes.
 *
 * NOTE:
 * Rules: Pet Mini Games, oldid 122046 (2010-11-18): sequences of 3, then 4,
 * and so on; the game ends after 5 rounds or 3 failures.
 * Wire format (official client, PetGameDance::MSG_PetGameDance and the
 * dance GUI's send): MSG_PETGAMEDANCE.Moves is one letter a move, 'a' + the
 * move index (0..3, the four arrow keys), both ways. The client marks the
 * round itself by comparing its answer with the sequence it got, so the
 * server must judge the same way: an exact match.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using System;
using System.Linq;

namespace Imlight.Classic.Pets;

/// <summary>One Dance Game.</summary>
public sealed class DanceGame(Random random) {

    public const int Rounds = 5;
    public const int MaxFailures = 3;
    public const int FirstLength = 3;
    public const int MoveKinds = 4;

    private readonly Random _random = random;

    /// <summary>The sequence the wizard must repeat this round, or null between rounds.</summary>
    public string Current { get; private set; }

    public int Round { get; private set; }
    public int Successes { get; private set; }
    public int Failures { get; private set; }

    public bool IsOver => Round >= Rounds || Failures >= MaxFailures;

    /// <summary>Starts the next round and returns its sequence ('a'..'d'), or null when the game is over.</summary>
    public string NextRound() {
        if (IsOver) {
            Current = null;
            return null;
        }

        var length = FirstLength + Round;
        Current = new string([.. Enumerable.Range(0, length).Select(_ => (char) ('a' + _random.Next(MoveKinds)))]);
        return Current;
    }

    /// <summary>Judges the wizard's answer to the current round; false when there is no round to answer.</summary>
    public bool Answer(string moves) {
        if (Current is null) {
            return false;
        }

        var ok = string.Equals(moves ?? "", Current, StringComparison.Ordinal);
        if (ok) {
            Successes++;
        }
        else {
            Failures++;
        }

        Round++;
        Current = null;
        return ok;
    }

    /// <summary>Stat points earned: rounds danced right out of five.</summary>
    public int Points => PetRules.GamePoints(Successes, Rounds);

}
