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
 * CLASSIC PET GOBBLER DROP GAME
 * ========================================================================
 *
 * PURPOSE:
 * The server half of Gobbler Drop: which food falls where and when, whether
 * the pet catches it, the fullness score, the clock and its bonuses.
 *
 * USAGE EXAMPLE:
 * var game = new DropGame(settings, track, dropSpots, random);
 * every tick: foreach (var e in game.Advance(dt, petPosition, jumping)) send(e);
 * when game.IsOver: finish(game.Points).
 *
 * NOTE:
 * Every number comes from the client's own settings
 * (ThePhantomZoneWorld/PetGameDrop/GameSettings.xml, PetDropGameSettings),
 * read by the server at run time, so both sides fall at the same speeds:
 * the food of the chosen group (m_nFoodGroup == track) plus the group -1
 * bonuses, picked by weight; weights change by m_fWeightChange every
 * m_nWeightChangeInSeconds; drops per second go from start to end over the
 * time limit; food falls from m_fInitialItemHeight at m_fFallSpeed and is
 * caught within m_fCatchDistance of the pet at or below m_fPetHeight (plus
 * m_fJumpHeight while jumping), and missed at m_fGroundHeight. A time bonus
 * extends the clock, a freeze slows falling to m_fFreezeSpeed. The full
 * score is m_nFullnessScoreTarget. Heights are relative to the drop spot.
 * The jump duration and the score-to-points rule are UNVERIFIED.
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

/// <summary>One kind of falling food or bonus (PetDropFoodItem).</summary>
public sealed record DropFood(uint TemplateId, int Group, float FallSpeed, int Fullness, int TimeBonusSeconds,
    int SpeedBonusSeconds, int FreezeSeconds, float Weight, float WeightChange);

/// <summary>The game's settings (PetDropGameSettings).</summary>
public sealed record DropSettings(int FullnessTarget, int TimeLimitSeconds, int WeightChangeSeconds, float DropsPerSecondAtStart,
    float DropsPerSecondAtEnd, float CatchDistance, float FreezeSpeed, float InitialItemHeight, float GroundHeight, float PetHeight,
    float JumpHeight, float MaxScaleIncrease, IReadOnlyList<DropFood> Foods);

/// <summary>A piece of food in the air.</summary>
public sealed class DropItem(int key, DropFood food, Vector3 spot) {
    public int Key { get; } = key;
    public DropFood Food { get; } = food;
    public Vector3 Spot { get; } = spot;

    /// <summary>Height above the spot.</summary>
    public float Height { get; internal set; }

    public Vector3 Position => Spot + new Vector3(0, 0, Height);
}

public enum DropEventKind {
    /// <summary>A new piece starts to fall.</summary>
    Spawned,
    /// <summary>The pet caught it.</summary>
    Caught,
    /// <summary>It hit the ground.</summary>
    Missed,
    /// <summary>The clock ran out.</summary>
    TimesUp,
}

/// <summary>Something that happened this tick. Caught events carry the new fullness, gobbler scale and speed effect.</summary>
public sealed record DropEvent(DropEventKind Kind, DropItem? Item = null, int Fullness = 0, float Scale = 1, int SpeedBonusSeconds = 0);

/// <summary>One Gobbler Drop game.</summary>
public sealed class DropGame {

    private readonly DropSettings _settings;
    private readonly IReadOnlyList<Vector3> _spots;
    private readonly Random _random;
    private readonly List<(DropFood Food, float Weight)> _pool;
    private readonly List<DropItem> _falling = [];
    private int _nextKey = 1;
    private double _spawnCredit;
    private double _nextWeightChange;
    private double _freezeLeft;
    private double _jumpLeft;
    private bool _timesUp;

    public DropGame(DropSettings settings, int track, IReadOnlyList<Vector3> spots, Random random) {
        _settings = settings;
        _spots = spots;
        _random = random;
        _pool = settings.Foods.Where(f => f.Group == track || f.Group < 0).Select(f => (f, f.Weight)).ToList();
        TimeLimit = settings.TimeLimitSeconds;
        _nextWeightChange = settings.WeightChangeSeconds;
        // The first piece falls straight away.
        _spawnCredit = 1;
    }

    public double Elapsed { get; private set; }
    public double TimeLimit { get; private set; }
    public double TimeLeft => Math.Max(0, TimeLimit - Elapsed);
    public int Fullness { get; private set; }
    public int Caught { get; private set; }
    public int Missed { get; private set; }
    public bool IsOver => _timesUp;
    public IReadOnlyList<DropItem> Falling => _falling;
    public bool CanPlay => _pool.Count > 0 && _spots.Count > 0;

    public int Points => PetMinigameRules.ScorePoints(Fullness, _settings.FullnessTarget);

    /// <summary>The gobbler's size for the current fullness (1 to 1 + m_fMaxScaleIncrease).</summary>
    public float Scale => 1 + _settings.MaxScaleIncrease * Math.Clamp((float) Fullness / Math.Max(1, _settings.FullnessTarget), 0, 1);

    /// <summary>The pet jumped: for a moment it can catch food higher up.</summary>
    public void Jump() {
        if (!_timesUp) {
            _jumpLeft = PetMinigameRules.DropJumpSeconds;
        }
    }

    /// <summary>Advances the game by <paramref name="dt"/> seconds with the pet at <paramref name="pet"/> (null: not known yet).</summary>
    public IReadOnlyList<DropEvent> Advance(double dt, Vector3? pet) {
        var events = new List<DropEvent>();
        if (_timesUp || dt <= 0) {
            return events;
        }

        Elapsed += dt;
        _jumpLeft = Math.Max(0, _jumpLeft - dt);
        var fallFactor = _freezeLeft > 0 ? _settings.FreezeSpeed : 1f;
        _freezeLeft = Math.Max(0, _freezeLeft - dt);

        // The falling food.
        var reach = _settings.PetHeight + (_jumpLeft > 0 ? _settings.JumpHeight : 0);
        foreach (var item in _falling.ToList()) {
            item.Height -= (float) (item.Food.FallSpeed * fallFactor * dt);
            if (pet is { } p && item.Height <= reach && item.Height > _settings.GroundHeight
                && new Vector2(p.X - item.Spot.X, p.Y - item.Spot.Y).Length() <= _settings.CatchDistance) {
                _falling.Remove(item);
                events.Add(Catch(item));
                continue;
            }

            if (item.Height <= _settings.GroundHeight) {
                _falling.Remove(item);
                Missed++;
                events.Add(new DropEvent(DropEventKind.Missed, item, Fullness, Scale));
            }
        }

        if (Elapsed >= TimeLimit) {
            _timesUp = true;
            events.Add(new DropEvent(DropEventKind.TimesUp, Fullness: Fullness, Scale: Scale));
            return events;
        }

        // The weights shift every few seconds (bombs and anvils become likelier, bonuses rarer).
        while (_settings.WeightChangeSeconds > 0 && Elapsed >= _nextWeightChange) {
            _nextWeightChange += _settings.WeightChangeSeconds;
            for (var i = 0; i < _pool.Count; i++) {
                _pool[i] = (_pool[i].Food, Math.Max(0, _pool[i].Weight + _pool[i].Food.WeightChange));
            }
        }

        // New food: the rate goes from the start rate to the end rate over the time limit.
        var progress = Math.Clamp(Elapsed / Math.Max(1, _settings.TimeLimitSeconds), 0, 1);
        var rate = _settings.DropsPerSecondAtStart + (_settings.DropsPerSecondAtEnd - _settings.DropsPerSecondAtStart) * progress;
        _spawnCredit += rate * dt;
        while (_spawnCredit >= 1 && CanPlay) {
            _spawnCredit -= 1;
            var item = new DropItem(_nextKey++, PickFood(), _spots[_random.Next(_spots.Count)]) { Height = _settings.InitialItemHeight };
            _falling.Add(item);
            events.Add(new DropEvent(DropEventKind.Spawned, item, Fullness, Scale));
        }

        return events;
    }

    private DropEvent Catch(DropItem item) {
        var food = item.Food;
        Caught++;
        Fullness = Math.Max(0, Fullness + food.Fullness);
        if (food.TimeBonusSeconds > 0) {
            TimeLimit += food.TimeBonusSeconds;
        }

        if (food.FreezeSeconds > 0) {
            _freezeLeft = Math.Max(_freezeLeft, food.FreezeSeconds);
        }

        return new DropEvent(DropEventKind.Caught, item, Fullness, Scale, food.SpeedBonusSeconds);
    }

    private DropFood PickFood() {
        var total = _pool.Sum(p => p.Weight);
        if (total <= 0) {
            return _pool[_random.Next(_pool.Count)].Food;
        }

        var roll = _random.NextDouble() * total;
        foreach (var (food, weight) in _pool) {
            if (roll < weight) {
                return food;
            }

            roll -= weight;
        }

        return _pool[^1].Food;
    }

}
