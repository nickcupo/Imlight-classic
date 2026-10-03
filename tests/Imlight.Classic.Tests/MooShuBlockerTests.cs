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
 * CLASSIC: the MooShu blockers: KingsIsle's ReqEncounterComplete (Imlight.Classic.Quests.EncounterComplete) and the
 * proximity-aggro bookkeeping after a duel (ProximityAggro) and volume arrivals (VolumeArrival).
 */

using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class MooShuBlockerTests {

    // KingsIsle's string hashes (Imcodec.Cryptography.StringHash), to show where the ids come from.
    private static uint KiHash(string input) {
        var result = 0;
        var shift1 = 0;
        var shift2 = 32;
        foreach (var c in input) {
            var cb = (byte) c;
            result ^= (cb - 32) << shift1;
            if (shift1 > 24) {
                result ^= (cb - 32) >> shift2;
                if (shift1 >= 27) {
                    shift1 -= 32;
                    shift2 += 32;
                }
            }

            shift1 += 5;
            shift2 -= 5;
        }

        return (uint) (result < 0 ? -result : result);
    }

    private static uint PropertyHash(string name, string type)
        => KiHash(type) + (name.Aggregate<char, uint>(5381, (h, c) => (h << 5) + h + c) & 0x7FFF_FFFF);

    [Fact]
    public void EncounterCompleteIdsAreTheClientNames() {
        Assert.Equal(EncounterComplete.ClassHash, KiHash("class ReqEncounterComplete"));
        Assert.Equal(EncounterComplete.EncounterNameHash, PropertyHash("m_encounterName", "std::string"));
    }

    [Fact]
    public void EncounterCompleteIsTheNamedQuestDone() {
        var done = new HashSet<string> { "MS-DTH3-C03-001" };
        Assert.True(EncounterComplete.Met(done.Contains, "MS-DTH3-C03-001"));
        Assert.False(EncounterComplete.Met(done.Contains, "MS-DTH3-C03-002"));
        Assert.False(EncounterComplete.Met(_ => true, ""));
        Assert.False(EncounterComplete.Met(_ => true, null));
    }

    [Fact]
    public void AWizardInADuelOrItsGraceIsNotCountedInsideAMonstersRadius() {
        Assert.True(ProximityAggro.Deferred(isMonster: true, inGrace: true, inDuel: false));
        Assert.True(ProximityAggro.Deferred(isMonster: true, inGrace: false, inDuel: true));
        Assert.False(ProximityAggro.Deferred(isMonster: true, inGrace: false, inDuel: false));
        Assert.False(ProximityAggro.Deferred(isMonster: false, inGrace: true, inDuel: true));
    }

    [Fact]
    public void ACreatureTriesAgainAFreeWizardStillInsideItsRadius() {
        var t0 = new System.DateTime(2026, 10, 2, 12, 0, 0, System.DateTimeKind.Utc);
        Assert.False(ProximityAggro.Retry(true, inGrace: false, inDuel: false, t0, t0.AddSeconds(4)));
        Assert.True(ProximityAggro.Retry(true, inGrace: false, inDuel: false, t0, t0.AddSeconds(5)));
        Assert.False(ProximityAggro.Retry(true, inGrace: true, inDuel: false, t0, t0.AddSeconds(30)));
        Assert.False(ProximityAggro.Retry(true, inGrace: false, inDuel: true, t0, t0.AddSeconds(30)));
        Assert.False(ProximityAggro.Retry(false, inGrace: false, inDuel: false, t0, t0.AddSeconds(30)));
    }

    [Fact]
    public void AnArrivalInsideAVolumeFiresItsTriggersButNeverATeleport() {
        Assert.True(VolumeArrival.Fires(arrivedInside: true, triggerTeleports: false));
        Assert.False(VolumeArrival.Fires(arrivedInside: true, triggerTeleports: true));
        Assert.True(VolumeArrival.Fires(arrivedInside: false, triggerTeleports: true));
    }

}
