// CLASSIC: MB_Ironworks' plate puzzle (Knock on Wood, MB-AIR3-C02-003: the way to Brady and the T5 tower). Four floor plates
// (Activator Volume 3, 0 (3), 1, 2) flip the plaques BirdPlaque1, MoonPlaque1, BugPlate1 and TreePlate1 between "Idle Up"
// and "Idle Down"; Symbol1-4 want four plaque patterns in order and open Gate2, Symbol5-8 four others and open Gate3. The
// plaques start "Idle Up" (their trigger objects' m_startState), which the server did not track: an untracked object passes
// every ReqState, so the Moon plate's On and Off triggers both fired on one step and the patterns could not be followed
// (ios-parity qa-s255, Gate2 never opened). The zone now tracks trigger objects' start states
// (ObjectStateRules.TrackedStartStates). This replays the r806919 triggers with the server's own state checks.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class IronworksPlatePuzzleTests {

    private const string Bird = "Enter_Activator Volume 3", Moon = "Enter_Activator Volume 0 (3)",
        Bug = "Enter_Activator Volume 1", Tree = "Enter_Activator Volume 2";

    [Fact]
    public void ThePlaquesStartUpAndKaranahnPalacesUnreachableGateCheckStaysAsItWas() {
        var starts = ZoneObjectStates.StartStates(Triggers("Marleybone-MB_Station-MB_Ironworks"));
        foreach (var plaque in new[] { "BirdPlaque1", "MoonPlaque1", "BugPlate1", "TreePlate1" }) {
            Assert.Equal("Idle Up", starts[plaque]);
        }

        Assert.Equal("ClosedIdle", starts["Gate2"]);

        // Gate4 starts "Open_Idle" and Gate4Control1 waits for "Closed_Idle", which nothing sets: left untracked.
        var palace = ZoneObjectStates.StartStates(Triggers("Krokotopia-KT_Tomb-KT_KaranahnPalace"));
        Assert.DoesNotContain("Gate4", palace.Keys);
    }

    [Theory]
    [InlineData(new[] { Bird, Moon, Tree, Bug }, "Gate2")]      // Symbol1-4
    [InlineData(new[] { Tree, Moon, Bug, Bird }, "Gate3")]      // Symbol5-8
    public void TheRightStepsOpenTheirGate(string[] steps, string gate) {
        var zone = new Zone(Triggers("Marleybone-MB_Station-MB_Ironworks"), tracked: true);
        foreach (var step in steps) {
            zone.Post(step);
        }

        Assert.True(zone.States.TryGet(gate, out var state));
        Assert.Equal("OpenIdle", state);
        Assert.Equal(steps.Length, zone.Fired.Count(name => name.EndsWith("On", StringComparison.Ordinal)
            || name.EndsWith("Off", StringComparison.Ordinal))); // one plaque flip per step
    }

    [Fact]
    public void BothGatesOpenWhenOneSequenceFollowsTheOther() {
        var zone = new Zone(Triggers("Marleybone-MB_Station-MB_Ironworks"), tracked: true);
        foreach (var step in new[] { Tree, Moon, Bug, Bird }) { // Gate3; every plaque is down now
            zone.Post(step);
        }

        foreach (var step in new[] { Tree, Moon, Bug, Bird, Bird, Moon, Tree, Bug }) { // all up again, then Gate2
            zone.Post(step);
        }

        Assert.True(zone.States.TryGet("Gate2", out var gate2) && gate2 == "OpenIdle");
        Assert.True(zone.States.TryGet("Gate3", out var gate3) && gate3 == "OpenIdle");
    }

    [Fact]
    public void UntrackedTheMoonPlateFlippedTwiceOnOneStep() {
        var zone = new Zone(Triggers("Marleybone-MB_Station-MB_Ironworks"), tracked: false);
        zone.Post(Moon);
        Assert.Contains("MoonOn", zone.Fired);
        Assert.Contains("MoonOff", zone.Fired);
    }

    // The zone's trigger activation as the classic engine runs it: a trigger is armed by StartZone or Enable_<name>,
    // disarmed by Disable_<name>; an event's armed triggers are all checked before any of their results run; posted events
    // follow in order.
    private sealed class Zone {
        private readonly List<Trigger> _triggers;
        private readonly HashSet<string> _armed = new(StringComparer.Ordinal);
        public ObjectStateTable States { get; } = new();
        public List<string> Fired { get; } = [];

        public Zone(List<Trigger> triggers, bool tracked) {
            _triggers = triggers;
            if (tracked) {
                foreach (var (tag, state) in ZoneObjectStates.StartStates(triggers)) {
                    States.Track(tag, state);
                }
            }

            foreach (var t in triggers.Where(t => (t.m_activateEvents ?? []).Any(e => (string) e == "StartZone"))) {
                _armed.Add(t.m_triggerName);
            }
        }

        public void Post(string first) {
            var queue = new Queue<string>([first]);
            while (queue.Count > 0) {
                var name = queue.Dequeue();
                if (name.StartsWith("Enable_", StringComparison.Ordinal)) {
                    _armed.Add(name["Enable_".Length..]);
                } else if (name.StartsWith("Disable_", StringComparison.Ordinal)) {
                    _armed.Remove(name["Disable_".Length..]);
                }

                var passing = _triggers.Where(t => _armed.Contains(t.m_triggerName)
                        && (t.m_fireEvents ?? []).Any(e => (string) e == name)
                        && Checks(t.m_requirements).All(r => ObjectStateRules.Met(States, r.m_triggerObjName, r.m_triggerObjState) != r.m_applyNOT))
                    .ToList();
                foreach (var t in passing) {
                    Fired.Add(t.m_triggerName);
                    foreach (var result in t.m_results?.m_results ?? []) {
                        switch (result) {
                            case ClassicResModifyTriggerObject modify: States.Set(modify.ObjectName, modify.State); break;
                            case ResPostEvent post: queue.Enqueue(post.m_eventName); break;
                        }
                    }
                }
            }
        }

        private static IEnumerable<ClassicReqState> Checks(Requirement? r) => r switch {
            ClassicReqState s => [s],
            RequirementList l => (l.m_requirements ?? []).SelectMany(Checks),
            _ => [],
        };
    }

    private static List<Trigger> Triggers(string folder) {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "w101c-private", "extract", "r806919",
            "zones", folder, "triggers.xml");
        if (!File.Exists(path)) {
            Assert.Skip("the private r806919 extraction is not on this machine");
        }

        var serializer = new BindSerializer { TypeRegistry = ClassicZoneTypeRegistry.Instance };
        Assert.True(serializer.Deserialize<WizZoneTriggers>(File.ReadAllBytes(path), 1, out var data));

        return data!.m_triggers.Where(t => t is not null).ToList();
    }
}
