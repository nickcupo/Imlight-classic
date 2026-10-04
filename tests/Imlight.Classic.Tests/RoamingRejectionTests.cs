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
 * CLASSIC: a creature turned away by a full duel circle (Imlight.Classic.Quests.RoamingRejection).
 */

using System;
using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

// CLASSIC: playbot ms (MooShu, 2026-10-03): a creature that spawns inside a full duel circle stays instead of being
// deleted at once (MS_Plague3_T1's Water Spirit left 28 copies in the client), and a walker that arrives still leaves.
public sealed class RoamingRejectionTests {

    [Fact]
    public void ACreatureJustSpawnedInsideAFullCircleStays() {
        Assert.False(RoamingRejection.Despawns(TimeSpan.Zero));
        Assert.False(RoamingRejection.Despawns(TimeSpan.FromMilliseconds(400)));
        Assert.False(RoamingRejection.Despawns(RoamingRejection.FreshSpawn - TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void ACreatureThatWalkedInLeaves() {
        Assert.True(RoamingRejection.Despawns(RoamingRejection.FreshSpawn));
        Assert.True(RoamingRejection.Despawns(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void FreshMeansShorterThanTheShortestRespawn() {
        // MS_Plague3_T1's Water Spirit respawns every 5 s; a fresh spawn must still be fresh when it first moves.
        Assert.True(RoamingRejection.FreshSpawn < TimeSpan.FromSeconds(5));
        Assert.True(RoamingRejection.FreshSpawn > TimeSpan.FromSeconds(1));
    }
}
