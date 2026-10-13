/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
 * TRIGGER-STARTED NPC SPAWNERS
 * ========================================================================
 *
 * PURPOSE:
 * An NPC that a zone trigger reveals after a kill (the Bonekeeper in KT_AhnicTomb_T2) is started for a wizard whose open
 * talk goal waits for it, so a new instance does not strand a wizard whose kill goal is already done.
 *
 * Created by: Nick with Claude Code (claude-sonnet-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/13/2026
 */

using System.Collections.Generic;
using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class TriggerStartedNpcSpawnerTests {

    private static readonly SpawnerInfo s_bonekeeper = new(900, false, [82387u], TriggerStarted: true);
    private static readonly SpawnerInfo s_dormantBoss = new(901, false, [126501u]);

    [Fact]
    public void ATriggerStartedSpawnerIsStartedOnlyForAnOpenTalkGoal() {
        Assert.Empty(DormantSpawners.Plan([s_bonekeeper], id => id == 82387u));
        Assert.Equal([900u], DormantSpawners.Plan([s_bonekeeper], _ => false, null, id => id == 82387u));
        Assert.Empty(DormantSpawners.Plan([s_bonekeeper], _ => false, null, id => id == 1u));
    }

    [Fact]
    public void ATriggerStartedSpawnerNeverStartsABossForAKillGoal() {
        var boss = s_dormantBoss with { TriggerStarted = true };
        Assert.Empty(DormantSpawners.Plan([boss], id => id == 126501u, null, _ => false));
    }

    [Fact]
    public void ATemplateTheZoneAlreadyPlacesIsNotStartedAgain() {
        Assert.Empty(DormantSpawners.Plan([s_bonekeeper], _ => false, new HashSet<uint> { 82387u }, id => id == 82387u));
    }

}
