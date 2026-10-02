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
 * CLASSIC: the state objects and ReqState (Imlight.Classic.Quests.StateObjects), with the Temple of Storms mind puzzle
 * (Krokotopia/KT_Tomb/KT_TempleOfStorms, 2014 package) replayed from its trigger data.
 */

using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Quests;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class StateObjectPuzzleTests {

    // KingsIsle's string hashes (Imcodec.Cryptography.StringHash), to show where the ReqState ids come from.
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

        if (result < 0) {
            result = -result;
        }

        return (uint) result;
    }

    private static uint PropertyHash(string name, string type) {
        var djb2 = name.Aggregate<char, uint>(5381, (h, c) => (h << 5) + h + c);
        return KiHash(type) + (djb2 & 0x7FFF_FFFF);
    }

    [Fact]
    public void ReqStateIdsAreTheClientNames() {
        Assert.Equal(ReqStateIds.ClassHash, KiHash("class ReqState"));
        Assert.Equal(ReqStateIds.ApplyNotHash, PropertyHash("m_applyNOT", "bool"));
        Assert.Equal(ReqStateIds.ObjectNameHash, PropertyHash("m_triggerObjName", "std::string"));
        Assert.Equal(ReqStateIds.ObjectStateHash, PropertyHash("m_triggerObjState", "std::string"));
    }

    [Fact]
    public void UntrackedObjectsPassAndTrackedOnesCompare() {
        var table = new ObjectStateTable();
        Assert.True(ObjectStateRules.Met(null, "Bug1", "Idle_On"));
        Assert.True(ObjectStateRules.Met(table, "DynaTrigger_KT_TeleporterMind", "On"));

        table.Track("Bug1", "Idle_Off");
        Assert.False(ObjectStateRules.Met(table, "Bug1", "Idle_On"));
        table.Track("Bug1", "Idle_On"); // tracking again keeps the state
        Assert.False(ObjectStateRules.Met(table, "Bug1", "Idle_On"));
        Assert.True(table.Set("Bug1", "Idle_On"));
        Assert.False(table.Set("Bug1", "Idle_On"));
        Assert.True(ObjectStateRules.Met(table, "Bug1", "Idle_On"));
    }

    [Fact]
    public void EnterStateEventsNameTagAndState() {
        Assert.Equal("Part2Moon1.Idle_On.EnterState", ObjectStateRules.EnterStateEvent("Part2Moon1", "Idle_On"));
        Assert.True(ObjectStateRules.TryParseEnterState("KT_Lever_Palace instance (1).Off.EnterState", out var tag, out var state));
        Assert.Equal("KT_Lever_Palace instance (1)", tag);
        Assert.Equal("Off", state);
        Assert.False(ObjectStateRules.TryParseEnterState("PuzzleComplete", out _, out _));
        Assert.False(ObjectStateRules.TryParseEnterState(".EnterState", out _, out _));
        Assert.Equal(["Bug1", "Sun1"], ObjectStateRules.ListenedTags(["Bug1.Idle_On.EnterState", "StartZone",
            "Sun1.Idle_Off.EnterState", "Bug1.Idle_Off.EnterState"]).Order());
    }

    // KT_Obelisk_BirdStates (r806919 StateData/KT), the "Basic" category.
    private static readonly StateNode[] Obelisk = [
        new("Idle_Off", [("Idle_On", "Turn_On")]),
        new("Idle_On", [("Idle_Off", "Turn_Off")]),
        new("Turn_On", [("Idle_On", null)], AutoState: "Idle_On"),
        new("Turn_Off", [("Idle_Off", null)], AutoState: "Idle_Off"),
    ];

    [Fact]
    public void AClickTogglesAnObeliskThroughItsTransition() {
        Assert.True(StateClick.TryNext(Obelisk, "Idle_Off", out var next, out var shown));
        Assert.Equal(("Idle_On", "Turn_On"), (next, shown));
        Assert.True(StateClick.TryNext(Obelisk, "Idle_On", out next, out shown));
        Assert.Equal(("Idle_Off", "Turn_Off"), (next, shown));
        Assert.False(StateClick.TryNext(Obelisk, "Missing", out _, out _));

        // A target that is an auto transition rests in its auto state.
        StateNode[] viaAuto = [new("A", [("ToB", null)]), new("ToB", [("B", null)], AutoState: "B"), new("B", [("A", null)])];
        Assert.True(StateClick.TryNext(viaAuto, "A", out next, out shown));
        Assert.Equal(("B", "ToB"), (next, shown));
    }

    // ---- The Temple of Storms mind puzzle, from the 2014 triggers.xml (ReqState values decoded from the raw file). ----

    private sealed record Req(string Tag, string State);

    private sealed record Trig(string Name, string Activate, string[] Fire, Req[] Reqs, (string Tag, string State)[] SetStates,
        string[] Posts);

    private static Req On(string tag) => new(tag, "Idle_On");
    private static Req Off(string tag) => new(tag, "Idle_Off");

    private static readonly Trig[] Temple = [
        new("TestofMindMasterPuz1", "StartZone",
            ["Bug1.Idle_On.EnterState", "Moon1.Idle_Off.EnterState", "Bird1.Idle_On.EnterState", "Tree1.Idle_On.EnterState",
             "Sun1.Idle_Off.EnterState", "Snake1.Idle_On.EnterState"],
            [On("Bug1"), Off("Moon1"), On("Bird1"), On("Tree1"), Off("Sun1"), On("Snake1")], [],
            ["ActivateFrom_Trigger TestofMindMasterPuz1"]),
        new("Victory", "StartZone",
            ["Part2Moon2.Idle_On.EnterState", "Part2Moon3.Idle_On.EnterState", "Part2Moon4.Idle_On.EnterState",
             "Part2Moon1.Idle_On.EnterState", "Part2Sun1.Idle_Off.EnterState", "Part2Sun2.Idle_Off.EnterState",
             "Part2Sun3.Idle_Off.EnterState", "Part2Sun4.Idle_Off.EnterState"],
            [On("Part2Moon2"), On("Part2Moon1"), On("Part2Moon4"), On("Part2Moon3"),
             Off("Part2Sun1"), Off("Part2Sun2"), Off("Part2Sun4"), Off("Part2Sun3")], [], ["ActivateFrom_Victory"]),
        new("SettingSuns1", "StartZone", ["Part2Moon1.Idle_On.EnterState"], [],
            [("Part2Sun1", "Idle_Off"), ("Part2Sun2", "Idle_Off")], []),
        new("SettingSun2", "StartZone", ["Part2Moon3.Idle_On.EnterState"], [],
            [("Part2Sun3", "Idle_Off"), ("Part2Sun4", "Idle_Off")], []),
        new("MoonOff2", "StartZone", ["Part2Moon4.Idle_On.EnterState"], [],
            [("Part2Moon1", "Idle_Off"), ("Part2Moon3", "Idle_Off")], []),
        new("MoonOff1", "StartZone", ["Part2Moon2.Idle_On.EnterState"], [],
            [("Part2Moon1", "Idle_Off"), ("Part2Moon3", "Idle_Off")], []),
        new("SnakeTrigger", "Enable_SnakeTrigger", ["Part3Snake1.Idle_On.EnterState"],
            [On("Part3Tree1"), On("Part3Snake1"), Off("Part3Bug1"), On("Part3Sun1")], [], ["Enable_BugTrigger"]),
        new("SunTrigger", "StartZone", ["Part3Sun1.Idle_On.EnterState"],
            [On("Part3Sun1"), Off("Part3Tree1"), Off("Part3Snake1"), Off("Part3Bug1")], [], ["Enable_TreeTrigger"]),
        new("BugTrigger", "Enable_BugTrigger", ["Part3Bug1.Idle_On.EnterState"],
            [On("Part3Tree1"), On("Part3Snake1"), On("Part3Bug1"), On("Part3Sun1")], [], ["ActivateFrom_BugTrigger"]),
        new("TreeTrigger", "Enable_TreeTrigger", ["Part3Tree1.Idle_On.EnterState"],
            [On("Part3Tree1"), Off("Part3Snake1"), Off("Part3Bug1"), On("Part3Sun1")], [], ["Enable_SnakeTrigger"]),
        new("PuzzleVictory", "StartZone", ["ActivateFrom_BugTrigger"], [], [], ["PuzzleComplete"]),
    ];

    // gamedata: the placed start states.
    private static readonly (string Tag, string Start)[] Placed = [
        ("Bug1", "Idle_Off"), ("Moon1", "Idle_Off"), ("Bird1", "Idle_Off"), ("Tree1", "Idle_Off"), ("Sun1", "Idle_Off"),
        ("Snake1", "Idle_Off"),
        ("Part2Sun1", "Idle_On"), ("Part2Sun2", "Idle_On"), ("Part2Sun4", "Idle_On"), ("Part2Sun3", "Idle_On"),
        ("Part2Moon3", "Idle_Off"), ("Part2Moon2", "Idle_Off"), ("Part2Moon4", "Idle_Off"), ("Part2Moon1", "Idle_Off"),
        ("Part3Tree1", "Idle_Off"), ("Part3Snake1", "Idle_Off"), ("Part3Bug1", "Idle_Off"), ("Part3Sun1", "Idle_Off"),
    ];

    private sealed class TempleRun {

        private readonly ObjectStateTable _table = new();
        private readonly HashSet<string> _armed;
        public readonly List<string> Posted = [];

        public TempleRun() {
            foreach (var (tag, start) in Placed) {
                _table.Track(tag, start);
            }

            _armed = Temple.Where(t => t.Activate == "StartZone").Select(t => t.Name).ToHashSet();
        }

        public void Click(string tag) {
            _table.TryGet(tag, out var current);
            Assert.True(StateClick.TryNext(Obelisk, current, out var next, out _));
            if (_table.Set(tag, next)) {
                Post(ObjectStateRules.EnterStateEvent(tag, next));
            }
        }

        private void Post(string eventName) {
            Posted.Add(eventName);
            foreach (var trigger in Temple.Where(t => t.Activate == eventName)) {
                _armed.Add(trigger.Name);
            }

            foreach (var trigger in Temple.Where(t => _armed.Contains(t.Name) && t.Fire.Contains(eventName)).ToList()) {
                var met = RequirementFold.Evaluate(trigger.Reqs, r => ObjectStateRules.Met(_table, r.Tag, r.State),
                    _ => false, _ => false);
                if (!met) {
                    continue;
                }

                foreach (var (tag, state) in trigger.SetStates) {
                    if (_table.Set(tag, state)) {
                        Post(ObjectStateRules.EnterStateEvent(tag, state));
                    }
                }

                foreach (var posted in trigger.Posts) {
                    Post(posted);
                }
            }
        }

        public bool Saw(string eventName) => Posted.Contains(eventName);

    }

    [Fact]
    public void TheRightClicksSolveAllThreeParts() {
        var run = new TempleRun();
        foreach (var tag in new[] { "Bug1", "Bird1", "Tree1", "Snake1" }) {
            run.Click(tag);
        }

        Assert.True(run.Saw("ActivateFrom_Trigger TestofMindMasterPuz1"));

        foreach (var tag in new[] { "Part2Moon2", "Part2Moon4", "Part2Moon1", "Part2Moon3" }) {
            run.Click(tag);
        }

        Assert.True(run.Saw("ActivateFrom_Victory"));

        foreach (var tag in new[] { "Part3Sun1", "Part3Tree1", "Part3Snake1", "Part3Bug1" }) {
            run.Click(tag);
        }

        Assert.True(run.Saw("PuzzleComplete"));
    }

    [Fact]
    public void WrongCombinationsDoNotSolve() {
        // Part 1 with the moon lit as well.
        var run = new TempleRun();
        foreach (var tag in new[] { "Moon1", "Bug1", "Bird1", "Tree1", "Snake1" }) {
            run.Click(tag);
        }

        Assert.False(run.Saw("ActivateFrom_Trigger TestofMindMasterPuz1"));
        run.Click("Moon1"); // turning it off again solves it
        Assert.True(run.Saw("ActivateFrom_Trigger TestofMindMasterPuz1"));

        // Part 2 lighting moons 1 and 3 first: moons 2 and 4 put them out again.
        run = new TempleRun();
        foreach (var tag in new[] { "Part2Moon1", "Part2Moon3", "Part2Moon2", "Part2Moon4" }) {
            run.Click(tag);
        }

        Assert.False(run.Saw("ActivateFrom_Victory"));

        // Part 3 out of order, then everything lit.
        run = new TempleRun();
        foreach (var tag in new[] { "Part3Tree1", "Part3Sun1", "Part3Snake1", "Part3Bug1" }) {
            run.Click(tag);
        }

        Assert.False(run.Saw("PuzzleComplete"));

        // A single first click does not solve anything (the undecoded checks used to pass).
        run = new TempleRun();
        run.Click("Bug1");
        run.Click("Part2Moon1");
        run.Click("Part3Sun1");
        Assert.DoesNotContain(run.Posted, e => e.StartsWith("ActivateFrom_") && e != "ActivateFrom_BugTrigger");
        Assert.False(run.Saw("PuzzleComplete"));
    }

}
