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
 * CLASSIC PET CANNON GAME
 * ========================================================================
 *
 * PURPOSE:
 * The server half of the Cannon Game: the wind of each shot, the flight of the
 * pet the client fired, which ring it crossed, and the points.
 *
 * USAGE EXAMPLE:
 * var game = new CannonGame(random, origin, targetFoot);
 * send cmd 12(origin); send cmd 14(game.BeginShot());
 * on client cmd 15(velocity): var shot = game.Fire(velocity); send cmd 17(shot.Ordinal, shot.Result);
 * on client cmd 13: if (game.IsOver) finish(game.Points) else send cmd 14(game.BeginShot()).
 *
 * NOTE:
 * Wire contract (native evidence, docs/playtest/2026-10-07-pet-game-native-contracts.md):
 * client 13 = ready for a shot (no body); client 15 = three float32 velocity;
 * server 12 = three float32 origin; server 14 = int32 wind speed, int32 wind
 * direction, float32 horizontal distance bound, three float32 acceleration;
 * server 17 = int32 one-based shot ordinal, int32 result index. The client's
 * stepper subtracts gravity * dt from velocity.z, adds acceleration * dt to
 * velocity and then advances the position; this class steps the same way.
 * The gravity constant itself is UNVERIFIED (PetMinigameRules.CannonGravity).
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

/// <summary>The wind of one shot, as command 14 carries it.</summary>
public sealed record CannonWind(int Speed, int DirectionDegrees, float DistanceBound, Vector3 Acceleration);

/// <summary>One fired shot: its one-based ordinal (1 is the practice shot) and result index (0..3).</summary>
public sealed record CannonShot(int Ordinal, int Result, bool Practice, bool CrossedTarget, float Distance, Vector3 End);

/// <summary>One Cannon Game.</summary>
public sealed class CannonGame {

    /// <summary>Simulation step, seconds (the client steps per frame; 60 frames a second).</summary>
    public const float Step = 1f / 60f;

    /// <summary>The longest flight simulated, seconds.</summary>
    public const float MaxFlightSeconds = 30f;

    /// <summary>The fastest launch accepted, units per second; anything faster is a forged or broken packet.</summary>
    public const float MaxLaunchSpeed = 50000f;

    private readonly Random _random;
    private readonly List<CannonShot> _shots = [];

    public CannonGame(Random random, Vector3 origin, Vector3 targetFoot, float gravity = PetMinigameRules.CannonGravity) {
        _random = random;
        Origin = origin;
        TargetCentre = targetFoot + new Vector3(0, 0, PetMinigameRules.CannonTargetCentreHeight);
        GroundZ = targetFoot.Z;
        Gravity = gravity;
        var horizontal = new Vector2(TargetCentre.X - origin.X, TargetCentre.Y - origin.Y).Length();
        DistanceBound = horizontal + PetMinigameRules.CannonDistanceMargin;
    }

    public Vector3 Origin { get; }
    public Vector3 TargetCentre { get; }
    public float GroundZ { get; }
    public float Gravity { get; }
    public float DistanceBound { get; }

    /// <summary>The wind of the shot being aimed, or null between shots.</summary>
    public CannonWind? Current { get; private set; }

    public IReadOnlyList<CannonShot> Shots => _shots;

    public bool IsOver => _shots.Count >= PetMinigameRules.CannonShots;

    /// <summary>The scored shots' result indexes (the practice shot left out).</summary>
    public IEnumerable<int> ScoredResults => _shots.Where(s => !s.Practice).Select(s => s.Result);

    /// <summary>The sum of the scored shots' values.</summary>
    public int Score => ScoredResults.Sum(r => PetMinigameRules.CannonResultValues[r]);

    public int Points => PetMinigameRules.CannonPoints(ScoredResults);

    /// <summary>Picks the wind for the next shot; null when the game is over or a shot is already being aimed.</summary>
    public CannonWind? BeginShot() {
        if (IsOver || Current is not null) {
            return null;
        }

        var speed = _random.Next(PetMinigameRules.CannonMaxWind + 1);
        var degrees = _random.Next(360);
        var radians = degrees * MathF.PI / 180f;
        var acceleration = new Vector3(MathF.Cos(radians), MathF.Sin(radians), 0) * (speed * PetMinigameRules.CannonWindAcceleration);
        Current = new CannonWind(speed, degrees, DistanceBound, acceleration);
        return Current;
    }

    /// <summary>Flies the shot the client fired; null when no shot is being aimed or the velocity is not a real one.</summary>
    public CannonShot? Fire(Vector3 velocity) {
        if (Current is not { } wind || !float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y) || !float.IsFinite(velocity.Z)
            || velocity.Length() > MaxLaunchSpeed) {
            return null;
        }

        var (crossed, distance, end) = Fly(velocity, wind.Acceleration);
        var ordinal = _shots.Count + 1;
        var result = crossed ? PetMinigameRules.CannonResult(distance) : 0;
        var shot = new CannonShot(ordinal, result, ordinal == 1, crossed, distance, end);
        _shots.Add(shot);
        Current = null;
        return shot;
    }

    /// <summary>
    /// Steps the flight as the client does and reports where it crosses the target's plane (the vertical plane through
    /// the target's centre, facing the cannon): whether it did before landing or leaving the range, and how far from
    /// the centre.
    /// </summary>
    public (bool Crossed, float Distance, Vector3 End) Fly(Vector3 velocity, Vector3 acceleration) {
        var toCannon = new Vector2(Origin.X - TargetCentre.X, Origin.Y - TargetCentre.Y);
        var normal = toCannon.LengthSquared() > 0 ? Vector2.Normalize(toCannon) : Vector2.UnitX;
        float Side(Vector3 p) => Vector2.Dot(new Vector2(p.X - TargetCentre.X, p.Y - TargetCentre.Y), normal);

        var position = Origin;
        var v = velocity;
        var side = Side(position);
        for (var t = 0f; t < MaxFlightSeconds; t += Step) {
            v.Z -= Gravity * Step;
            v += acceleration * Step;
            var next = position + v * Step;
            var nextSide = Side(next);
            if (side > 0 && nextSide <= 0) {
                var f = side / (side - nextSide);
                var hit = position + (next - position) * f;
                return (true, Vector3.Distance(hit, TargetCentre), hit);
            }

            var horizontal = new Vector2(next.X - Origin.X, next.Y - Origin.Y).Length();
            if (next.Z < GroundZ || horizontal > DistanceBound) {
                return (false, float.PositiveInfinity, next);
            }

            position = next;
            side = nextSide;
        }

        return (false, float.PositiveInfinity, position);
    }

}
