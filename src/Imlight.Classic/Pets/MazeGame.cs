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
 * CLASSIC PET MAZE GAME
 * ========================================================================
 *
 * PURPOSE:
 * The server half of the Maze Game: where the snacks and bonuses lie, what
 * the pet picks up, the ghosts that freeze it (or that it eats under a
 * star), the score and the clock.
 *
 * USAGE EXAMPLE:
 * var game = new MazeGame(snackSpots, random);
 * send each game.Pickups as a new object; send cmd 0;
 * on a pet move: foreach (var e in game.PetMoved(position, ghosts)) send(e);
 * every tick: if (game.Advance(dt)) finish(game.Points).
 *
 * NOTE:
 * Native client consumer (MSG_PETGAMEMAZE): 0 = start (clock 60,000 ms,
 * score 0), 6 = snack eaten (absolute score, object), 7 = frozen 3 s,
 * 8 = ghost eaten (absolute score, ghost), 9 = speed x1.3 for 8 s,
 * 10 = clock +10 s, 11 = eat ghosts for 10 s. The client sends no pickup or
 * collision messages: the server judges them from the pet's moves.
 * The snack spots are the maze's Path 0 nodes (pathData.xml); the bonus
 * counts other than the three clocks are UNVERIFIED.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/08/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Imlight.Classic.Pets;

public enum MazePickupKind {
    Snack,
    Clock,
    SpeedBoost,
    Star,
}

/// <summary>Something lying in the maze.</summary>
public sealed record MazePickup(int Key, MazePickupKind Kind, Vector3 Position);

public enum MazeEventKind {
    /// <summary>A snack was eaten (command 6).</summary>
    Snack,
    /// <summary>A clock: +10 s (command 10).</summary>
    Clock,
    /// <summary>A speed boost (command 9).</summary>
    SpeedBoost,
    /// <summary>A star: ghosts can be eaten (command 11).</summary>
    Star,
    /// <summary>A ghost caught the pet: frozen (command 7).</summary>
    Frozen,
    /// <summary>The pet ate a ghost (command 8).</summary>
    GhostEaten,
    /// <summary>The clock ran out.</summary>
    TimesUp,
}

/// <summary>One event; <see cref="Pickup"/> or <see cref="Ghost"/> names what it was about.</summary>
public sealed record MazeEvent(MazeEventKind Kind, int Score, MazePickup? Pickup = null, ulong Ghost = 0);

/// <summary>One Maze game.</summary>
public sealed class MazeGame {

    private readonly Dictionary<int, MazePickup> _pickups = [];
    private readonly Dictionary<ulong, double> _ghostGoneUntil = [];
    private double _frozenUntil = double.NegativeInfinity;
    private double _caughtAgainAfter = double.NegativeInfinity;
    private double _immuneUntil = double.NegativeInfinity;
    private bool _timesUp;

    public MazeGame(IReadOnlyList<Vector3> spots, Random random) {
        // Place the bonuses on distinct random spots; snacks go everywhere else.
        var order = Enumerable.Range(0, spots.Count).OrderBy(_ => random.Next()).ToList();
        var kinds = Enumerable.Repeat(MazePickupKind.Clock, PetMinigameRules.MazeClocks)
            .Concat(Enumerable.Repeat(MazePickupKind.SpeedBoost, PetMinigameRules.MazeSpeedBoosts))
            .Concat(Enumerable.Repeat(MazePickupKind.Star, PetMinigameRules.MazeStars)).ToList();
        for (var i = 0; i < order.Count; i++) {
            var kind = i < kinds.Count ? kinds[i] : MazePickupKind.Snack;
            var key = order[i] + 1;
            _pickups[key] = new MazePickup(key, kind, spots[order[i]]);
        }

        TimeLimit = PetMinigameRules.MazeStartSeconds;
        SnackCount = _pickups.Values.Count(p => p.Kind == MazePickupKind.Snack);
    }

    public IReadOnlyCollection<MazePickup> Pickups => _pickups.Values;
    public int SnackCount { get; }
    public double Elapsed { get; private set; }
    public double TimeLimit { get; private set; }
    public int Score { get; private set; }
    public bool IsOver => _timesUp;
    public bool Frozen => Elapsed < _frozenUntil;
    public bool Immune => Elapsed < _immuneUntil;

    public int Points => PetMinigameRules.ScorePoints(Score, PetMinigameRules.MazeFullScore);

    /// <summary>Advances the clock; returns a TimesUp event once when it runs out.</summary>
    public MazeEvent? Advance(double dt) {
        if (_timesUp || dt <= 0) {
            return null;
        }

        Elapsed += dt;
        if (Elapsed < TimeLimit) {
            return null;
        }

        _timesUp = true;
        return new MazeEvent(MazeEventKind.TimesUp, Score);
    }

    /// <summary>The pet is at <paramref name="pet"/>; the ghosts at <paramref name="ghosts"/>.</summary>
    public IReadOnlyList<MazeEvent> PetMoved(Vector3 pet, IEnumerable<(ulong Ghost, Vector3 Position)> ghosts) {
        var events = new List<MazeEvent>();
        // CLASSIC: the existing freeze also prevents fresh pickups from later move reports.
        if (_timesUp || Frozen) {
            return events;
        }

        foreach (var pickup in _pickups.Values.ToList()) {
            if (Flat(pet, pickup.Position) > PetMinigameRules.MazePickupRadius) {
                continue;
            }

            _pickups.Remove(pickup.Key);
            switch (pickup.Kind) {
                case MazePickupKind.Snack:
                    Score++;
                    events.Add(new MazeEvent(MazeEventKind.Snack, Score, pickup));
                    break;
                case MazePickupKind.Clock:
                    TimeLimit += PetMinigameRules.MazeClockSeconds;
                    events.Add(new MazeEvent(MazeEventKind.Clock, Score, pickup));
                    break;
                case MazePickupKind.SpeedBoost:
                    events.Add(new MazeEvent(MazeEventKind.SpeedBoost, Score, pickup));
                    break;
                case MazePickupKind.Star:
                    _immuneUntil = Elapsed + PetMinigameRules.MazeImmunitySeconds;
                    events.Add(new MazeEvent(MazeEventKind.Star, Score, pickup));
                    break;
            }
        }

        foreach (var (ghost, position) in ghosts) {
            if (_ghostGoneUntil.TryGetValue(ghost, out var until) && Elapsed < until) {
                continue;
            }

            if (Flat(pet, position) > PetMinigameRules.MazeGhostRadius) {
                continue;
            }

            if (Immune) {
                _ghostGoneUntil[ghost] = Elapsed + PetMinigameRules.MazeGhostRespawnSeconds;
                Score += PetMinigameRules.MazeGhostScore;
                events.Add(new MazeEvent(MazeEventKind.GhostEaten, Score, Ghost: ghost));
            }
            else if (!Frozen && Elapsed >= _caughtAgainAfter) {
                _frozenUntil = Elapsed + PetMinigameRules.MazeFreezeSeconds;
                _caughtAgainAfter = _frozenUntil + PetMinigameRules.MazeGhostGraceSeconds;
                events.Add(new MazeEvent(MazeEventKind.Frozen, Score, Ghost: ghost));
            }
        }

        return events;
    }

    private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

}
