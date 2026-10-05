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
 * ZONE TRIGGERS
 * ========================================================================
 *
 * PURPOSE:
 * The zone trigger state rules on hand-made trigger shapes taken from the
 * client data: a trigger that waits for its Enable_ event, the liveness
 * plan (gated, kept open, released and left-out objects, replay-safe
 * events), trigger-object presence per scope, and the table zone objects
 * and ReqTriggerState read.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter ZoneTriggerState
 *
 * NOTE:
 * The shapes are Sunken City's gate triggers (Gate01Trigger 0, the
 * "Gate of Paulson" pair), the Tomb of the Beguiler gauntlet's WallRemover
 * and the Grizzleheim library's OpenDoor checks.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ZoneTriggerStateTests {

    private sealed class Player(string name) {
        public override string ToString() => name;
    }

    private const string StartZone = ZoneTriggerLiveness.StartZone;

    // Sunken City's street, reduced: the quest posts FirstGate, the activator enables the gate trigger, entering the gate
    // volume removes the gate, its collision and Marla's stand-in and disables the gate trigger.
    private static List<TriggerFacts> SunkenStreet(bool activatorStateOnly = true) => [
        new() {
            Name = "Gate01Trigger 0", ActivateEvents = ["Enable_Gate01Trigger 0"], FireEvents = ["Enter_Gate01Activator Volume"],
            DeactivateEvents = ["Disable_Gate01Trigger 0"], PostedEvents = ["Disable_Gate01Trigger 0"],
            RemovedObjects = ["Gate (1)", "Collision", "WC_ST07_NPC02 instance"],
        },
        new() {
            Name = "TriggerFirstGateActivator", ActivateEvents = [StartZone], FireEvents = ["FirstGate"],
            PostedEvents = ["Enable_Gate01Trigger 0"], StateOnly = activatorStateOnly,
        },
        new() { Name = "Trigger (1)", ActivateEvents = [StartZone], ObjectTag = "Gate (1)" },
        new() { Name = "Trigger (2)", ActivateEvents = [StartZone], ObjectTag = "Collision" },
        new() {
            Name = "DeSpawnMarla03Trigger", ActivateEvents = ["Enable_DeSpawnMarla03Trigger"], FireEvents = ["Enter_Gate02Activator Volume"],
            DeactivateEvents = ["Disable_DeSpawnMarla03Trigger"], PostedEvents = ["Disable_DeSpawnMarla03Trigger"],
            RemovedObjects = ["Gate of Paulson"], ObjectTag = "Gate of Paulson",
        },
        new() { Name = "Trigger (3)", ActivateEvents = [StartZone], ObjectTag = "Gate of Paulson" },
        new() {
            Name = "TriggerSecondGateActivator", ActivateEvents = [StartZone], FireEvents = ["SecondGate"],
            PostedEvents = ["Enable_DeSpawnMarla03Trigger"], StateOnly = true,
        },
        new() {
            Name = "Trigger-SkeletonKeyTeleport", ActivateEvents = ["Enable_Trigger-SkeletonKeyTeleport"],
            FireEvents = ["Enter_Activator Volume-SkeletonKey"],
        },
    ];

    private static readonly string[] s_streetRoots = ["EnterZone", "Monster_Killed", "Enter_Gate01Activator Volume",
        "Enter_Gate02Activator Volume", "Enter_Activator Volume-SkeletonKey"];

    private static ZoneTriggerLiveness Street(bool activatorStateOnly = true)
        => ZoneTriggerLiveness.Analyze(SunkenStreet(activatorStateOnly), s_streetRoots, ["FirstGate", "SecondGate"],
            ["WC_ST07_NPC02 instance"]);

    [Fact]
    public void ATriggerThatWaitsForItsEnableEventStartsDisarmed() {
        var gate = new TriggerActivation<Player>(["Enable_Gate"], ["Disable_Gate"], initiallyArmed: false);
        var a = new Player("a");
        var b = new Player("b");

        Assert.True(gate.CanChange);
        Assert.False(gate.IsArmed(a));
        Assert.True(gate.Observe("Enable_Gate", a));
        Assert.True(gate.IsArmed(a));
        Assert.False(gate.IsArmed(b)); // the scope's own state
        Assert.True(gate.Observe("Disable_Gate", a));
        Assert.False(gate.IsArmed(a));
    }

    [Fact]
    public void AWaitingTriggerIsTrackedEvenWithoutADeactivateEvent() {
        var dispatch = new TriggerEventDispatch<string, Player>();
        var a = new Player("a");
        Assert.True(dispatch.Track("tower door", "TriggerTeleportTower1", ["Enable_TriggerTeleportTower1"], [], initiallyArmed: false));
        Assert.False(dispatch.IsArmed("tower door", a));

        var changed = new List<(string, string, bool)>();
        var fires = dispatch.Dispatch(["tower door"], t => t, "Enable_TriggerTeleportTower1", a,
            _ => false, _ => true, _ => false, (key, name, armed) => changed.Add((key, name, armed)));
        Assert.Empty(fires);
        Assert.Equal([("tower door", "TriggerTeleportTower1", true)], changed);
        Assert.True(dispatch.IsArmed("tower door", a));
    }

    [Fact]
    public void TheSunkenCityGateWaitsForItsQuestEventAndItsObjectsAreReleased() {
        var plan = Street();

        Assert.Contains(0, plan.Gated);
        Assert.False(plan.StartsArmed[0]);
        Assert.True(plan.CanFire[0]);
        Assert.Contains("FirstGate", plan.ReplaySafe);
        foreach (var tag in new[] { "Gate (1)", "Collision", "Gate of Paulson" }) {
            Assert.True(plan.Objects[tag].Spawn, tag);
            Assert.True(plan.Objects[tag].Releasable, tag);
            Assert.False(plan.Objects[tag].Placed, tag);
        }

        Assert.True(plan.Objects["WC_ST07_NPC02 instance"].Placed);
        Assert.Equal([2], plan.Objects["Gate (1)"].Owners);
        Assert.Equal([4, 5], plan.Objects["Gate of Paulson"].Owners);
    }

    [Fact]
    public void ATriggerWhoseEnableEventNothingPostsStaysOpen() {
        var plan = Street();
        var key = SunkenStreet().FindIndex(t => t.Name == "Trigger-SkeletonKeyTeleport");

        Assert.Contains(key, plan.KeptOpen);
        Assert.True(plan.StartsArmed[key]);
    }

    [Fact]
    public void AQuestEventThatIsNotReplaySafeDoesNotGateTheDoor() {
        // An activator that also teleports, spawns or talks cannot be posted again in a new instance; the gate it enables
        // would stay shut there, so it stays open and its objects are left out.
        var plan = Street(activatorStateOnly: false);

        Assert.DoesNotContain("FirstGate", plan.ReplaySafe);
        Assert.Contains(0, plan.KeptOpen);
        Assert.True(plan.StartsArmed[0]);
    }

    [Fact]
    public void AnObjectNothingThatFiresReleasesIsLeftOut() {
        List<TriggerFacts> tomb = [
            new() { Name = "Trigger (1)", ActivateEvents = [StartZone], ObjectTag = "Copy of DoorCollision (1)" },
            new() {
                Name = "Trigger Door01", ActivateEvents = ["Enable_Trigger Door01"], FireEvents = ["LeverUsed"],
                RemovedObjects = ["Copy of DoorCollision (1)"], ChangedObjects = ["Door3_Storm"],
            },
            new() { Name = "Trigger (2)", ActivateEvents = [StartZone], ObjectTag = "Door3_Storm" },
            new() { Name = "Trigger Starter01", ActivateEvents = [StartZone], FireEvents = ["HasQuestInTheDoghouse"],
                    PostedEvents = ["Enable_Trigger Door01"], StateOnly = true },
            new() { Name = "Pad", ActivateEvents = [StartZone], ObjectTag = "Teleporter pad" },
        ];
        var plan = ZoneTriggerLiveness.Analyze(tomb, ["EnterZone"], ["HasQuestInTheDoghouse"], []);

        Assert.Contains(1, plan.Gated); // enabled by the quest, but nothing posts LeverUsed
        Assert.False(plan.CanFire[1]);
        Assert.True(plan.Objects["Copy of DoorCollision (1)"].Stuck);
        Assert.False(plan.Objects["Copy of DoorCollision (1)"].Spawn);
        Assert.True(plan.Objects["Door3_Storm"].Stuck);
        Assert.True(plan.Objects["Teleporter pad"].Spawn); // never meant to go
    }

    [Fact]
    public void AWallWhoseTriggerAKillDisablesIsReleased() {
        List<TriggerFacts> gauntlet = [
            new() { Name = "WallRemover", ActivateEvents = [StartZone], DeactivateEvents = ["Disable_WallRemover"], ObjectTag = "Wall" },
            new() { Name = "MobWatcher1", ActivateEvents = [StartZone], FireEvents = ["Monster_Killed"],
                    PostedEvents = ["Disable_WallRemover"], ChangedObjects = ["Door"] },
            new() { Name = "Door Init", ActivateEvents = [StartZone], ObjectTag = "Door" },
            new() { Name = "WallRemover5", ActivateEvents = [StartZone], DeactivateEvents = ["Disable_WallRemover5"], ObjectTag = "Wall5" },
            new() { Name = "MobWatcher5", ActivateEvents = [StartZone], FireEvents = ["Monster_Killed"], RequirementsCanPass = false,
                    PostedEvents = ["Disable_WallRemover5"] },
        ];
        var plan = ZoneTriggerLiveness.Analyze(gauntlet, ["Monster_Killed"], [], []);

        Assert.True(plan.Objects["Wall"].Releasable);
        Assert.True(plan.Objects["Door"].Releasable);
        Assert.True(plan.Objects["Wall5"].Stuck); // its watcher's requirement never passes here
    }

    [Fact]
    public void ReplaySafetyFollowsPostedEvents() {
        List<TriggerFacts> zone = [
            new() { Name = "A", ActivateEvents = [StartZone], FireEvents = ["QuestA"], PostedEvents = ["Middle"], StateOnly = true },
            new() { Name = "B", ActivateEvents = [StartZone], FireEvents = ["Middle"], PostedEvents = ["Enable_C"], StateOnly = true },
            new() { Name = "C", ActivateEvents = ["Enable_C"], FireEvents = ["Enter_V"] },
            new() { Name = "D", ActivateEvents = [StartZone], FireEvents = ["QuestB"], PostedEvents = ["Fight"], StateOnly = true },
            new() { Name = "E", ActivateEvents = [StartZone], FireEvents = ["Fight"] }, // starts combat: not state-only
        ];
        var plan = ZoneTriggerLiveness.Analyze(zone, ["Enter_V"], ["QuestA", "QuestB"], []);

        Assert.Contains("QuestA", plan.ReplaySafe);
        Assert.Contains("Middle", plan.ReplaySafe);
        Assert.Contains("Enable_C", plan.ReplaySafe); // only arms C
        Assert.DoesNotContain("Fight", plan.ReplaySafe);
        Assert.DoesNotContain("QuestB", plan.ReplaySafe);
        Assert.Contains(2, plan.Gated);
    }

    [Fact]
    public void TheGateOfPaulsonStaysGoneWhenItsTriggerDisablesItself() {
        var plan = Street();
        var presence = new TriggerObjectPresence<string>(plan.Objects);
        var armed = plan.StartsArmed.ToArray();
        bool Armed(int i) => armed[i];
        const string instance = "instance";

        Assert.True(presence.IsPresent("Gate of Paulson", instance, Armed));
        armed[4] = true; // Enable_DeSpawnMarla03Trigger
        presence.OwnerChanged(4, instance);
        Assert.True(presence.Remove("Gate of Paulson", instance));
        Assert.False(presence.IsPresent("Gate of Paulson", instance, Armed));
        armed[4] = false; // Disable_DeSpawnMarla03Trigger
        presence.OwnerChanged(4, instance);
        Assert.False(presence.IsPresent("Gate of Paulson", instance, Armed));

        armed[4] = true; // enabled again (a replayed SecondGate): the trigger's own copy comes back
        presence.OwnerChanged(4, instance);
        Assert.True(presence.IsPresent("Gate of Paulson", instance, Armed));
        Assert.True(presence.IsPresent("Gate of Paulson", "another instance", i => plan.StartsArmed[i]));
    }

    [Fact]
    public void APlacedObjectGoesAndComesBackByName() {
        var plan = Street();
        var presence = new TriggerObjectPresence<string>(plan.Objects);
        bool Armed(int i) => plan.StartsArmed[i];

        Assert.True(presence.Manages("WC_ST07_NPC02 instance"));
        Assert.True(presence.IsPresent("WC_ST07_NPC02 instance", "p1", Armed));
        presence.Remove("WC_ST07_NPC02 instance", "p1");
        Assert.False(presence.IsPresent("WC_ST07_NPC02 instance", "p1", Armed));
        Assert.True(presence.IsPresent("WC_ST07_NPC02 instance", "p2", Armed));
        presence.Add("WC_ST07_NPC02 instance", "p1");
        Assert.True(presence.IsPresent("WC_ST07_NPC02 instance", "p1", Armed));
        Assert.False(presence.Manages("WC_ST07_NPC03 instance")); // no trigger names it here
        Assert.False(presence.Remove("Unknown", "p1"));
    }

    [Fact]
    public void ATriggerObjectWhoseTriggerWaitsAppearsWhenEnabledAndGoesWhenDisabled() {
        List<TriggerFacts> vault = [
            new() { Name = "SnakeHintControl", ActivateEvents = ["Enable_SnakeHintControl"], ObjectTag = "Tablet_Snake" },
            new() { Name = "EnablePuzzleOnDeath", ActivateEvents = [StartZone], FireEvents = ["Monster_Killed"],
                    PostedEvents = ["Enable_SnakeHintControl"] },
        ];
        var plan = ZoneTriggerLiveness.Analyze(vault, ["Monster_Killed"], [], []);
        var presence = new TriggerObjectPresence<string>(plan.Objects);
        var armed = plan.StartsArmed.ToArray();

        Assert.Contains(0, plan.Gated);
        Assert.True(plan.Objects["Tablet_Snake"].Spawn);
        Assert.False(presence.IsPresent("Tablet_Snake", "i", i => armed[i]));
        armed[0] = true;
        presence.OwnerChanged(0, "i");
        Assert.True(presence.IsPresent("Tablet_Snake", "i", i => armed[i]));
    }

}
