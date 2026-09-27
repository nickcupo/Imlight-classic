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
 * ARC 1 FINAL REVIEW TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The engine fixes of the arc1/final review: a zone actor's wizard query
 * gives up on a session that is gone, and a trigger whose teleport
 * requirements fail leaves the event's teleport to the next trigger.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 * The dispatch case is MS_Plague_Zone2_RiverVillage's 'Enter_Activator
 * Volume': 'Teleport MS_Plague2_T2' (closed to holders of MS-PLAG2-C01-003)
 * ahead of 'Teleport MS_Plague2_T2_Part2' on the same volume.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Diagnostics;
using System.Linq;
using Akka.Actor;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ArcFinalReviewTests {

    private sealed class Silent : ReceiveActor {
        public Silent() => ReceiveAny(_ => { });
    }

    [Fact]
    public void WizardQueryToAGoneSessionReturnsNullInsteadOfBlocking() {
        using var system = ActorSystem.Create("player-query-test");
        var gone = system.ActorOf(Props.Create(() => new Silent()), "gone");
        system.Stop(gone);

        var watch = Stopwatch.StartNew();
        var answered = PlayerQuery.TryActiveWizard(gone, TimeSpan.FromSeconds(1), out var wizard, out var error);
        watch.Stop();

        Assert.False(answered);
        Assert.Null(wizard);
        Assert.NotNull(error);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void WizardQueryToNobodyReturnsNull()
        => Assert.Null(PlayerQuery.ActiveWizard(ActorRefs.Nobody, "test"));

    private sealed record Trig(string Name, bool Passes, bool Teleports);

    [Fact]
    public void AFailedTeleportRequirementLeavesTheEventToTheNextDoor() {
        var dispatch = new TriggerEventDispatch<string, string>();
        Trig[] triggers = [
            new("Teleport MS_Plague2_T2", Passes: false, Teleports: true),  // its entry closes to 003's holders
            new("Teleport MS_Plague2_T2_Part2", Passes: true, Teleports: true),
        ];

        var fires = dispatch.Dispatch(triggers, t => t.Name, "Enter_Activator Volume", "wizard",
            listens: _ => true, meetsRequirements: t => t.Passes, teleportsSomewhere: t => t.Teleports);

        var fire = Assert.Single(fires);
        Assert.Equal("Teleport MS_Plague2_T2_Part2", fire.Trigger.Name);
        Assert.False(fire.SuppressTeleport);
    }

    [Fact]
    public void WhileTheFirstDoorPassesItKeepsTheTeleport() {
        var dispatch = new TriggerEventDispatch<string, string>();
        Trig[] triggers = [
            new("Teleport MS_Plague2_T2", Passes: true, Teleports: true),
            new("Teleport MS_Plague2_T2_Part2", Passes: true, Teleports: true),
        ];

        var fires = dispatch.Dispatch(triggers, t => t.Name, "Enter_Activator Volume", "wizard",
            listens: _ => true, meetsRequirements: t => t.Passes, teleportsSomewhere: t => t.Teleports);

        Assert.Equal(2, fires.Count);
        Assert.False(fires.First().SuppressTeleport);
        Assert.True(fires.Last().SuppressTeleport);
    }

}
