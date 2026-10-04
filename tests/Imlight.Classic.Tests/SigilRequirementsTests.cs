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
 * CLASSIC: which requirement list gates a dungeon sigil (Imlight.Classic.Travel.SigilRequirements).
 */

using Imlight.Classic.Travel;
using Xunit;

namespace Imlight.Classic.Tests;

// CLASSIC: playbot ms (MooShu, 2026-10-04): the 2014 River Village's ToTower2Part2 sigil asked for a quest given inside its
// own tower; a classic travel entry with requirements replaces the sigil's list, an entry without them leaves it.
public sealed class SigilRequirementsTests {

    private sealed record Reqs(string Name);

    [Fact]
    public void ATravelEntryWithRequirementsReplacesTheSigilsOwn() {
        var own = new Reqs("QT-MS-PLAG2-C03-001");
        var travel = new Reqs("ReqHasQuest MS-PLAG2-C01-003");
        Assert.Same(travel, SigilRequirements.Choose(own, travel, travelHasAny: true));
    }

    [Fact]
    public void AnEmptyOrMissingTravelListKeepsTheSigilsOwn() {
        var own = new Reqs("QT-MS-PLAG2-C01-002");
        Assert.Same(own, SigilRequirements.Choose(own, new Reqs("empty"), travelHasAny: false));
        Assert.Same(own, SigilRequirements.Choose(own, null, travelHasAny: true));
        Assert.Null(SigilRequirements.Choose<Reqs>(null, null, travelHasAny: false));
    }
}
