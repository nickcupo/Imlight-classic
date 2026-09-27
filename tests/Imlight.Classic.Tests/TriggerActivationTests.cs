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
 * Per-player trigger arming, replayed on Rattlebones' tower
 * (WizardCity-WC_Streets-Interiors-WC_Unicorn_T2) and the tutorial door.
 * 
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 * 
 * NOTE:
 * PostEvents mirrors ZoneTriggerSupervisor: every trigger observes an event
 * before any trigger fires on it, and a fired trigger's posted events queue
 * behind it in order, as the zone actor's mailbox does.
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

public sealed class TriggerActivationTests {

    private sealed class Player(string name) {
        public override string ToString() => name;
    }

    private sealed record TriggerData(string Name, string[] Fire, string[] Activate, string[] Deactivate, string[] Posts);

    // TR_ActivateCombat's results: initiate combat, then post its own disable and its own fire event.
    private static readonly TriggerData s_activateCombat = new("TR_ActivateCombat",
        Fire: ["Enter_Activator Volume", "ActivateFrom_TR_ActivateCombat"],
        Activate: ["StartZone"],
        Deactivate: ["Disable_TR_ActivateCombat"],
        Posts: ["Disable_TR_ActivateCombat", "ActivateFrom_TR_ActivateCombat"]);

    private static readonly TriggerData s_towerExit = new("Teleport location (Street1 Tower2 Exit)",
        Fire: ["Enter_TeleportVol_Teleport location (Street1 Tower2 Exit)"], Activate: ["StartZone"], Deactivate: [], Posts: []);

    // Replays each post, and everything its fires post, before the next; returns each fire as "trigger:player",
    // capped at 50 fires.
    private static List<string> PostEvents(IReadOnlyList<TriggerData> triggers, bool honourDeactivation,
                                           params (string Event, Player Player)[] posts) {
        var states = triggers.ToDictionary(trigger => trigger,
            trigger => new TriggerActivation<Player>(trigger.Activate, trigger.Deactivate));
        var fired = new List<string>();
        foreach (var initial in posts) {
            var queue = new Queue<(string Event, Player Player)>([initial]);
            Drain(queue);
        }

        return fired;

        void Drain(Queue<(string Event, Player Player)> queue) {
            while (queue.TryDequeue(out var post) && fired.Count < 50) {
                if (honourDeactivation) {
                    foreach (var state in states.Values) {
                        state.Observe(post.Event, post.Player);
                    }
                }

                foreach (var trigger in triggers) {
                    if (!trigger.Fire.Contains(post.Event) || !states[trigger].IsArmed(post.Player)) {
                        continue;
                    }

                    fired.Add(trigger.Name + ":" + post.Player);
                    foreach (var posted in trigger.Posts) {
                        queue.Enqueue((posted, post.Player));
                    }
                }
            }
        }
    }

    [Fact]
    public void RattlebonesTowerStartsCombatOnce() {
        var player = new Player("p1");

        var fired = PostEvents([s_activateCombat, s_towerExit], true, ("Enter_Activator Volume", player));

        Assert.Equal(new[] { "TR_ActivateCombat:p1" }, fired);
    }

    [Fact]
    public void WithoutDeactivationTheTowerTriggerRefiresForever() {
        var fired = PostEvents([s_activateCombat], false, ("Enter_Activator Volume", new Player("p1")));

        Assert.Equal(50, fired.Count);
    }

    [Fact]
    public void ReenteringTheVolumeStaysQuietForThatPlayerOnly() {
        var first = new Player("p1");
        var second = new Player("p2");

        var fired = PostEvents([s_activateCombat], true,
            ("Enter_Activator Volume", first),
            ("Enter_Activator Volume", first),
            ("Enter_Activator Volume", second));

        Assert.Equal(new[] { "TR_ActivateCombat:p1", "TR_ActivateCombat:p2" }, fired);
    }

    [Fact]
    public void ActivateEventRearms() {
        // WizardCity-Tutorial_Interior "TeleporterStuff": CloseDoorTrigger disarms it, OpenDoorTrigger re-arms it.
        var door = new TriggerActivation<Player>(["Enable_TeleporterStuff"], ["Disable_TeleporterStuff"]);
        var player = new Player("p1");

        Assert.True(door.CanDisarm);
        Assert.True(door.IsArmed(player));
        Assert.True(door.Observe("Disable_TeleporterStuff", player));
        Assert.False(door.IsArmed(player));
        Assert.False(door.Observe("Disable_TeleporterStuff", player));
        Assert.True(door.Observe("Enable_TeleporterStuff", player));
        Assert.True(door.IsArmed(player));
    }

    [Fact]
    public void TriggersWithoutDeactivateEventsNeverDisarm() {
        var exit = new TriggerActivation<Player>(["StartZone"], []);
        var player = new Player("p1");

        Assert.False(exit.CanDisarm);
        Assert.False(exit.Observe("Disable_Anything", player));
        Assert.True(exit.IsArmed(player));
    }

    [Fact]
    public void EventsWithoutAPlayerKeepTheirOwnState() {
        var trigger = new TriggerActivation<Player>([], ["Disable_T"]);
        var player = new Player("p1");

        Assert.True(trigger.Observe("Disable_T", null));
        Assert.False(trigger.IsArmed(null));
        Assert.True(trigger.IsArmed(player));
        Assert.False(trigger.Observe(null, player));
    }

}
