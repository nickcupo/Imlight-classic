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
 * CLASSIC: the after-duel protection (PostCombatGrace): KingsIsle's PostCombatEffect (30 s while the wizard stands
 * still) and PostCombatEffect2 (6 s after it moves), from GameEffectData/WizardEffects.xml.
 */

using System;
using System.Numerics;
using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class PostCombatGraceTests {

    private static readonly DateTime T0 = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Vector3 Spot = new(1000, -2000, 0);

    [Fact]
    public void TheDurationsAreTheClientEffectTemplates() {
        Assert.Equal(TimeSpan.FromSeconds(30), PostCombatGrace.DefaultStill);
        Assert.Equal(TimeSpan.FromSeconds(6), PostCombatGrace.DefaultMoving);
        Assert.Equal("PostCombatEffect", PostCombatGrace.StillEffectName);
        Assert.Equal("PostCombatEffect2", PostCombatGrace.MovingEffectName);
    }

    [Fact]
    public void StandingStillKeepsTheStillEffectForThirtySeconds() {
        var grace = new PostCombatGrace();
        grace.Start(T0, Spot);
        Assert.Equal(PostCombatPhase.Still, grace.Phase);
        Assert.Equal("PostCombatEffect", grace.EffectName);

        // The client repeats the spot (a turn in place, a settle): not a move.
        Assert.False(grace.Moved(Spot + new Vector3(4, -4, 0), T0.AddSeconds(10)));
        Assert.False(grace.Due(T0.AddSeconds(29.9)));
        Assert.True(grace.Protected);
        Assert.True(grace.Due(T0.AddSeconds(30)));
    }

    [Fact]
    public void MovingSwapsInTheSixSecondEffectThenCreaturesAggroAgain() {
        var grace = new PostCombatGrace();
        grace.Start(T0, Spot);
        var moved = T0.AddSeconds(12);
        Assert.True(grace.Moved(Spot + new Vector3(40, 0, 0), moved));
        Assert.Equal(PostCombatPhase.Moving, grace.Phase);
        Assert.Equal("PostCombatEffect2", grace.EffectName);
        Assert.Equal(moved.AddSeconds(6), grace.EndsUtc);
        Assert.True(grace.Protected);

        // Later moves do not restart it.
        Assert.False(grace.Moved(Spot + new Vector3(400, 0, 0), moved.AddSeconds(3)));
        Assert.Equal(moved.AddSeconds(6), grace.EndsUtc);

        Assert.False(grace.Due(moved.AddSeconds(5.9)));
        Assert.True(grace.Due(moved.AddSeconds(6)));
        Assert.Equal(PostCombatPhase.None, grace.Advance(moved.AddSeconds(6)));
        Assert.False(grace.Protected);
        Assert.Null(grace.EffectName);
    }

    [Fact]
    public void ThirtySecondsStillAlsoEndsInTheSixSecondEffect() {
        var grace = new PostCombatGrace();
        grace.Start(T0, Spot);
        Assert.Equal(PostCombatPhase.Moving, grace.Advance(T0.AddSeconds(30)));
        Assert.Equal(T0.AddSeconds(36), grace.EndsUtc);
        Assert.Equal(PostCombatPhase.None, grace.Advance(T0.AddSeconds(36)));
    }

    [Fact]
    public void ANewDuelRestartsTheStillEffectFromTheNewSpot() {
        var grace = new PostCombatGrace();
        grace.Start(T0, Spot);
        grace.Moved(Spot + new Vector3(100, 0, 0), T0.AddSeconds(1));
        var other = Spot + new Vector3(3000, 0, 0);
        grace.Start(T0.AddSeconds(60), other);
        Assert.Equal(PostCombatPhase.Still, grace.Phase);
        Assert.False(grace.Moved(other, T0.AddSeconds(61)));
        Assert.True(grace.Moved(Spot, T0.AddSeconds(62)));
    }

    [Fact]
    public void WithoutAMovingGraceAMoveEndsItAtOnce() {
        var grace = new PostCombatGrace(TimeSpan.FromSeconds(5), TimeSpan.Zero);
        grace.Start(T0, Spot);
        Assert.True(grace.Moved(Spot + new Vector3(0, 50, 0), T0.AddSeconds(1)));
        Assert.False(grace.Protected);
    }

    [Fact]
    public void ClearEndsIt() {
        var grace = new PostCombatGrace();
        grace.Start(T0, Spot);
        grace.Clear();
        Assert.False(grace.Protected);
        Assert.False(grace.Due(T0.AddDays(1)));
        Assert.False(grace.Moved(Spot + new Vector3(500, 0, 0), T0));
    }

}
