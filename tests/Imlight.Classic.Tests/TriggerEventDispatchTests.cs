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
 * CLASSIC QUEST ENGINE TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The per-event trigger decision: arming is observed before any trigger
 * fires, requirements are asked only of armed listeners, and one teleport
 * per event goes to the first trigger that teleports somewhere.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 *
 * NOTE:
 * The door pairs copy r806919 triggers on one volume event, in wad order:
 * WizardCity-WC_Ravenwood "Quest-WC-LIFE-C02 Teleport" (index 22) ahead of
 * "TriggerTeleportLifeSchool" (index 33), and WizardCity-WC_NightSide
 * "TriggerTeleportDefault" ahead of the Death tower door.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class TriggerEventDispatchTests {

    private sealed class Player(string name) {
        public override string ToString() => name;
    }

    private sealed record Entry(string Name, string Fire, bool TeleportsSomewhere = false, bool Passes = true);

    private const string Volume = "Enter_Activator Volume";

    private static List<TriggerFire<Entry>> Post(TriggerEventDispatch<Entry, Player> dispatch, IEnumerable<Entry> triggers,
                                                 string eventName, Player player, List<string>? asked = null)
        => dispatch.Dispatch(triggers, entry => entry, eventName, player,
            listens: entry => entry.Fire == eventName,
            meetsRequirements: entry => {
                asked?.Add(entry.Name);

                return entry.Passes;
            },
            teleportsSomewhere: entry => entry.TeleportsSomewhere);

    private static string[] Describe(IEnumerable<TriggerFire<Entry>> fires)
        => fires.Select(fire => fire.Trigger.Name + (fire.SuppressTeleport ? " (teleport suppressed)" : "")).ToArray();

    [Fact]
    public void AnEventThatDisarmsATriggerKeepsItFromFiringOnThatEvent() {
        var dispatch = new TriggerEventDispatch<Entry, Player>();
        var selfDisarming = new Entry("Once", "Go");
        var player = new Player("p1");
        dispatch.Track(selfDisarming, selfDisarming.Name, [], ["Go"]);

        Assert.Empty(Post(dispatch, [selfDisarming], "Go", player));
        Assert.False(dispatch.IsArmed(selfDisarming, player));
    }

    [Fact]
    public void AnEventThatRearmsATriggerLetsItFireOnThatEvent() {
        var dispatch = new TriggerEventDispatch<Entry, Player>();
        var door = new Entry("Door", "Open");
        var player = new Player("p1");
        dispatch.Track(door, door.Name, ["Open"], ["Close"]);

        Assert.Empty(Post(dispatch, [door], "Close", player));
        Assert.Equal(["Door"], Describe(Post(dispatch, [door], "Open", player)));
    }

    [Fact]
    public void RequirementsAreAskedOnlyOfArmedListeners() {
        var dispatch = new TriggerEventDispatch<Entry, Player>();
        var player = new Player("p1");
        var disarmed = new Entry("Disarmed", Volume);
        var elsewhere = new Entry("Elsewhere", "Enter_Other Volume");
        var armed = new Entry("Armed", Volume);
        dispatch.Track(disarmed, disarmed.Name, [], ["Disable_Disarmed"]);
        Post(dispatch, [], "Disable_Disarmed", player);

        var asked = new List<string>();
        var fires = Post(dispatch, [disarmed, elsewhere, armed], Volume, player, asked);

        Assert.Equal(["Armed"], asked);
        Assert.Equal(["Armed"], Describe(fires));
    }

    [Fact]
    public void ATeleportWithoutADestinationNeverShadowsTheSchoolDoor() {
        var dispatch = new TriggerEventDispatch<Entry, Player>();
        var lifeC02 = new Entry("Quest-WC-LIFE-C02 Teleport", Volume, TeleportsSomewhere: false);
        var lifeSchool = new Entry("TriggerTeleportLifeSchool", Volume, TeleportsSomewhere: true);

        var fires = Post(dispatch, [lifeC02, lifeSchool], Volume, new Player("p1"));

        Assert.Equal(["Quest-WC-LIFE-C02 Teleport", "TriggerTeleportLifeSchool"], Describe(fires));
    }

    [Fact]
    public void TheQuestDoorTakesTheTeleportOnceItHasADestination() {
        var dispatch = new TriggerEventDispatch<Entry, Player>();
        var lifeC02 = new Entry("Quest-WC-LIFE-C02 Teleport", Volume, TeleportsSomewhere: true);
        var lifeSchool = new Entry("TriggerTeleportLifeSchool", Volume, TeleportsSomewhere: true);

        var fires = Post(dispatch, [lifeC02, lifeSchool], Volume, new Player("p1"));

        Assert.Equal(["Quest-WC-LIFE-C02 Teleport", "TriggerTeleportLifeSchool (teleport suppressed)"], Describe(fires));
    }

    [Fact]
    public void AQuestDoorWhoseRequirementsFailLeavesTheTeleportToTheNextDoor() {
        var dispatch = new TriggerEventDispatch<Entry, Player>();
        var lifeC02 = new Entry("Quest-WC-LIFE-C02 Teleport", Volume, TeleportsSomewhere: true, Passes: false);
        var lifeSchool = new Entry("TriggerTeleportLifeSchool", Volume, TeleportsSomewhere: true);

        var fires = Post(dispatch, [lifeC02, lifeSchool], Volume, new Player("p1"));

        Assert.Equal(["TriggerTeleportLifeSchool"], Describe(fires));
    }

    [Fact]
    public void ADefaultTriggerWithoutADestinationFiresWithoutShadowingTheTowerDoor() {
        var dispatch = new TriggerEventDispatch<Entry, Player>();
        var fallback = new Entry("TriggerTeleportDefault", Volume, TeleportsSomewhere: false);
        var tower = new Entry("Death Tower C02-002 door", Volume, TeleportsSomewhere: true);
        var plainDoor = new Entry("Plain door", Volume, TeleportsSomewhere: true);

        var fires = Post(dispatch, [fallback, tower, plainDoor], Volume, new Player("p1"));

        Assert.Equal(["TriggerTeleportDefault", "Death Tower C02-002 door", "Plain door (teleport suppressed)"], Describe(fires));
    }

    [Fact]
    public void ATeleportWithoutADestinationAfterTheDoorIsSuppressed() {
        var dispatch = new TriggerEventDispatch<Entry, Player>();
        var door = new Entry("Hedge Maze door", Volume, TeleportsSomewhere: true);
        var afterQuest = new Entry("And After Hedgemaze Quest", Volume, TeleportsSomewhere: false);

        var fires = Post(dispatch, [door, afterQuest], Volume, new Player("p1"));

        Assert.Equal(["Hedge Maze door", "And After Hedgemaze Quest (teleport suppressed)"], Describe(fires));
    }

    [Fact]
    public void ArmingIsKeptPerPlayer() {
        var dispatch = new TriggerEventDispatch<Entry, Player>();
        var trigger = new Entry("T", Volume);
        var first = new Player("p1");
        var second = new Player("p2");
        dispatch.Track(trigger, trigger.Name, [], ["Disable_T"]);

        Post(dispatch, [], "Disable_T", first);

        Assert.Empty(Post(dispatch, [trigger], Volume, first));
        Assert.Equal(["T"], Describe(Post(dispatch, [trigger], Volume, second)));
    }

    [Fact]
    public void OnlyTriggersWithADeactivateEventAreTracked() {
        var dispatch = new TriggerEventDispatch<Entry, Player>();
        var exit = new Entry("Exit", Volume);
        var reported = new List<(string Name, bool Armed)>();

        Assert.False(dispatch.Track(exit, exit.Name, ["StartZone"], []));
        dispatch.Dispatch([exit], entry => entry, "StartZone", new Player("p1"), _ => false, _ => true, _ => false,
            (name, armed) => reported.Add((name, armed)));

        Assert.Empty(reported);
        Assert.True(dispatch.IsArmed(exit, new Player("p1")));
    }

    [Fact]
    public void ClearForgetsTheArming() {
        var dispatch = new TriggerEventDispatch<Entry, Player>();
        var trigger = new Entry("T", Volume);
        var player = new Player("p1");
        dispatch.Track(trigger, trigger.Name, [], ["Disable_T"]);
        Post(dispatch, [], "Disable_T", player);

        dispatch.Clear();

        Assert.True(dispatch.IsArmed(trigger, player));
    }

}
