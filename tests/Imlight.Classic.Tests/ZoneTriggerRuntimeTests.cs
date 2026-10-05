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
 * The server side of the zone trigger state: the trigger-object and
 * trigger-state classes decode from the real zone data (Sunken City, the
 * Tomb of the Beguiler, the Grizzleheim library), the plan builder reads
 * Imcodec triggers, the zone table keeps state per instance or per player,
 * ReqTriggerState reads it, and the replay knows which quest results a
 * wizard's progress has run.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter ZoneTriggerRuntime
 *
 * NOTE:
 * The decoding tests read the private r806919 extraction and the served
 * 2014 packages, and skip without them.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Wad;
using Imlight.Classic.Quests;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Zone.Supervisors;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ZoneTriggerRuntimeTests {

    private static readonly string s_private = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "w101c-private");

    public ZoneTriggerRuntimeTests() {
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-ztrig-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
    }

    private static List<Trigger> ExtractTriggers(string zoneFolder) {
        var path = Path.Combine(s_private, "extract", "r806919", "zones", zoneFolder, "triggers.xml");
        if (!File.Exists(path)) {
            Assert.Skip("the private r806919 extraction is not on this machine");
        }

        var serializer = new BindSerializer { TypeRegistry = ClassicZoneTypeRegistry.Instance };
        Assert.True(serializer.Deserialize<WizZoneTriggers>(File.ReadAllBytes(path), 1, out var triggers));

        return [.. triggers.m_triggers.Where(t => t is not null)];
    }

    private static List<Trigger> ServedTriggers(string wadName) {
        var path = Path.Combine(s_private, "deploy", "classic-zones", wadName + ".wad");
        if (!File.Exists(path)) {
            Assert.Skip("the served classic zone packages are not on this machine");
        }

        using var stream = File.OpenRead(path);
        var wad = ArchiveParser.Parse(stream)!;
        var serializer = new BindSerializer { TypeRegistry = ClassicZoneTypeRegistry.Instance };
        Assert.True(serializer.Deserialize<WizZoneTriggers>(wad.OpenFile("triggers.xml")!.Value.ToArray(), 1, out var triggers));

        return [.. triggers.m_triggers.Where(t => t is not null)];
    }

    private static Trigger Named(IEnumerable<Trigger> triggers, string name) => triggers.First(t => (string) t.m_triggerName == name);

    [Fact]
    public void SunkenCityTriggerObjectsAndRemovalsDecode() {
        var triggers = ExtractTriggers("WizardCity-WC_Streets-WC_Sunken_City");

        var gate = Assert.IsType<ClassicTriggerObjectInfo>(Named(triggers, "Trigger (1)").m_triggerObjInfo);
        Assert.Equal("DynaTrigger_GauntletDoor instance (1)", (string) gate.m_zoneTag);
        Assert.Equal(4173UL, gate.m_templateID.Full);
        Assert.Equal(LoadingType.DYNAMIC_SERVER, gate.m_loadingType);
        Assert.NotEqual(0f, gate.m_location.X);
        Assert.Same(gate, ZoneTriggerPlans.SpawnableObject(Named(triggers, "Trigger (1)")));

        var removals = Named(triggers, "Gate01Trigger 0").m_results.m_results.OfType<ClassicResRemoveTriggerObject>().Select(r => r.ObjectName);
        Assert.Equal(["DynaTrigger_GauntletDoor instance (1)", "DynaTrigger_KT_DoorCollision instance", "WC_ST07_NPC02 instance"], removals);
        Assert.Null(ZoneTriggerPlans.SpawnableObject(Named(triggers, "Gate01Trigger 0"))); // no object of its own
    }

    [Fact]
    public void TheGrizzleheimLibraryChecksItsTriggersAreActive() {
        var triggers = ExtractTriggers("Grizzleheim-GH_AbandCity-GH_Library");
        var checks = Named(triggers, "OpenDoor").m_requirements.m_requirements.Cast<ClassicReqTriggerState>().ToList();

        Assert.Equal(["SandCheck", "WaterCheck", "LavaCheck", "ForestCheck"], checks.Select(c => c.m_triggerName));
        Assert.All(checks, c => Assert.Equal("TRIGGER_STATE_ACTIVE", c.m_triggerState));
        Assert.All(checks, c => Assert.True(c.WantsActive));
        Assert.True(ZoneTriggerPlans.CanPass(Named(triggers, "OpenDoor").m_requirements)); // a handler reads them
    }

    [Fact]
    public void TheTombFrostDoorsOpenByStateChange() {
        var triggers = ServedTriggers("Krokotopia-KT_Tomb-Interiors-KT_Crypt06_Map02_Ice");
        var change = Assert.Single(Named(triggers, "Prison2DoorOpener").m_results.m_results.OfType<ClassicResStateChange>());

        var door = Assert.IsType<ClassicTriggerObjectInfo>(Named(triggers, "Trigger (8)").m_triggerObjInfo);
        Assert.Equal("DynaTrigger_KT_Door_Frost instance (2)", (string) door.m_zoneTag);
        Assert.NotEqual(0f, door.m_location.X); // placed by m_locationX/Y/Z in the 2014 package
        Assert.Equal(door.m_locationX, door.m_location.X);

        Assert.Equal("DynaTrigger_KT_Door_Frost instance (2)", change.ObjectName);
        Assert.Equal("Idle_Open", change.State);
        Assert.True(change.CanExecute);
        Assert.Contains("DynaTrigger_KT_Door_Frost instance (2)", ZoneTriggerPlans.Facts(Named(triggers, "Prison2DoorOpener")).ChangedObjects);
    }

    // A hand-made street: a gate trigger enabled by a quest event, with its own object, and a check trigger.
    private static ZoneTriggerPlan Street() {
        Trigger Make(string name, string[] act, string[] fire, string[] deact, List<Result> results, ClassicTriggerObjectInfo info = null)
            => new() {
                m_triggerName = name, m_activateEvents = [.. act.Select(e => (Imcodec.IO.ByteString) e)],
                m_fireEvents = [.. fire.Select(e => (Imcodec.IO.ByteString) e)], m_deactivateEvents = [.. deact.Select(e => (Imcodec.IO.ByteString) e)],
                m_requirements = new RequirementList { m_requirements = [] }, m_results = new ResultList { m_results = results },
                m_triggerObjInfo = info ?? new TriggerObjectInfo(),
            };

        List<Trigger> triggers = [
            Make("Gate", ["Enable_Gate"], ["Enter_Gate Volume"], ["Disable_Gate"],
                [new ClassicResRemoveTriggerObject { ObjectName = "GateObject" }, new ResPostEvent { m_eventName = "Disable_Gate" }]),
            Make("Activator", ["StartZone"], ["OpenGate"], [], [new ResPostEvent { m_eventName = "Enable_Gate" }]),
            Make("Gate Init", ["StartZone"], [], [], [], new ClassicTriggerObjectInfo {
                m_templateID = 4173, m_zoneTag = "GateObject", m_loadingType = LoadingType.DYNAMIC_SERVER,
            }),
            Make("SandCheck", ["Enable_SandCheck"], [], [], []),
            Make("Sand", ["StartZone"], ["Enter_Sand Volume"], [], [new ResPostEvent { m_eventName = "Enable_SandCheck" }]),
        ];
        var volumes = new WizZoneVolumes {
            m_volumes = [new Volume { m_volumeName = "Gate Volume", m_enterEvents = ["Enter_Gate Volume"], m_exitEvents = [] },
                         new Volume { m_volumeName = "Sand Volume", m_enterEvents = ["Enter_Sand Volume"], m_exitEvents = [] }],
        };

        return ZoneTriggerPlans.Build("Test/Street", triggers, volumes, null, ["OpenGate"]);
    }

    [Fact]
    public void ThePlanBuilderReadsImcodecTriggers() {
        var plan = Street();

        Assert.Contains(0, plan.Liveness.Gated);
        Assert.Contains(3, plan.Liveness.Gated);
        Assert.Contains("OpenGate", plan.Liveness.ReplaySafe);
        var spawned = Assert.Single(plan.TriggerObjectsToSpawn());
        Assert.Equal("GateObject", (string) spawned.m_zoneTag);
        Assert.Equal(0, plan.IndexOf(plan.Triggers[0]));
        Assert.Equal(-1, plan.IndexOf(new Trigger()));
    }

    [Fact]
    public void AnInstanceSharesItsStateAndAPublicZoneKeepsEachPlayersOwn() {
        using var system = ActorSystem.Create("ztrig-table", "akka.actor.provider = local");
        var zone = system.ActorOf(Props.Empty, "zone");
        var a = system.ActorOf(Props.Empty, "a");
        var b = system.ActorOf(Props.Empty, "b");
        var plan = Street();

        var dungeon = new ZoneTriggerTable(plan, zone, shared: true);
        Assert.False(dungeon.IsArmed(0, a));
        Assert.True(dungeon.IsPresent("GateObject", a));
        Assert.Empty(dungeon.SetArmed(0, a, true)); // the gate trigger owns no object
        Assert.True(dungeon.IsArmed(0, b)); // shared
        Assert.Equal([("GateObject", false)], dungeon.SetPresent("GateObject", a, false));
        Assert.False(dungeon.IsPresent("GateObject", b)); // a wizard who joins later sees it gone
        Assert.Empty(dungeon.SetPresent("GateObject", b, false)); // no change
        Assert.True(dungeon.MarkSeen("OpenGate", a));
        Assert.False(dungeon.MarkSeen("OpenGate", b));

        var street = new ZoneTriggerTable(plan, zone, shared: false);
        street.SetArmed(0, a, true);
        Assert.True(street.IsArmed(0, a));
        Assert.False(street.IsArmed(0, b));
        street.SetPresent("GateObject", a, false);
        Assert.False(street.IsPresent("GateObject", a));
        Assert.True(street.IsPresent("GateObject", b));
        Assert.True(street.IsPresent("Not managed", a));
    }

    [Fact]
    public void ReqTriggerStateReadsTheZonesTable() {
        using var system = ActorSystem.Create("ztrig-req", "akka.actor.provider = local");
        var zone = system.ActorOf(Props.Empty, "zone");
        var player = system.ActorOf(Props.Empty, "player");
        var table = ZoneTriggerTables.Create(zone, Street(), shared: true);
        try {
            var requirements = new RequirementList {
                m_operator = Operator.ROP_AND,
                m_requirements = [new ClassicReqTriggerState { m_triggerName = "SandCheck", m_triggerState = "TRIGGER_STATE_ACTIVE" }],
            };
            bool Met() => RequirementDispatcher.EvaluateRequirements(requirements,
                new ZoneRequirementContext(requirements, player, null, new Wizard(), zone, "OpenDoor"));

            Assert.False(Met());
            table.SetArmed(3, player, true); // Enable_SandCheck
            Assert.True(Met());

            requirements.m_requirements = [new ClassicReqTriggerState { m_triggerName = "NoSuchTrigger", m_triggerState = "TRIGGER_STATE_ACTIVE" }];
            Assert.True(Met()); // a trigger the zone does not have passes
        } finally {
            ZoneTriggerTables.Remove(zone);
        }
    }

    [Fact]
    public void TheReplayKnowsWhichQuestResultsAWizardsProgressRan() {
        var quest = new QuestTemplate {
            m_questName = "WC-ST07-C02-002",
            m_startGoals = [],
            m_goals = [new PersonaGoalTemplate { m_goalName = "Goal 3" }, new WaypointGoalTemplate { m_goalName = "Goal" }],
        };
        var wizard = new Wizard { CharId = 1, QuestBehavior = new ServerQuestBehavior() };
        var start = new QuestResultSource(QuestResultWhen.Start, null);
        var talk = new QuestResultSource(QuestResultWhen.GoalComplete, "Goal 3");
        var talkActivate = new QuestResultSource(QuestResultWhen.GoalActivate, "Goal 3");
        var end = new QuestResultSource(QuestResultWhen.End, null);

        Assert.False(ZoneTriggerSupervisor.ProgressRan(wizard, quest.m_questName, start));

        var held = new QuestInstance(quest, 1);
        Assert.True(wizard.QuestBehavior.AddQuest(held));
        held.StartGoal("Goal 3");
        Assert.True(ZoneTriggerSupervisor.ProgressRan(wizard, quest.m_questName, start));
        Assert.True(ZoneTriggerSupervisor.ProgressRan(wizard, quest.m_questName, talkActivate));
        Assert.False(ZoneTriggerSupervisor.ProgressRan(wizard, quest.m_questName, talk));
        Assert.False(ZoneTriggerSupervisor.ProgressRan(wizard, quest.m_questName, end));

        held.CompleteGoal("Goal 3");
        Assert.True(ZoneTriggerSupervisor.ProgressRan(wizard, quest.m_questName, talk));

        Assert.True(wizard.QuestBehavior.SetQuestRegistryValue(quest.m_questName, "Complete", 1));
        Assert.True(wizard.QuestBehavior.RemoveQuest(quest.m_questName));
        Assert.True(ZoneTriggerSupervisor.ProgressRan(wizard, quest.m_questName, end)); // a done quest ran everything
    }

    [Fact]
    public void QuestEventsIncludeGoalCompleteEvents() {
        var quest = new QuestTemplate {
            m_questName = "Q",
            m_startResults = new ResultList { m_results = [new ResPostEvent { m_eventName = "Started" }] },
            m_goals = [new PersonaGoalTemplate {
                m_goalName = "G",
                m_completeResults = new ResultList { m_results = [new ResPostEvent { m_eventName = "TalkedTo" }] },
            }],
        };
        var events = ZoneTriggerPlans.BuildQuestEvents([quest]);

        Assert.Equal(["Started", "TalkedTo", "GoalComplete_Q_G"], events.Order(StringComparer.Ordinal).ToList()
            .OrderBy(e => e == "Started" ? 0 : e == "TalkedTo" ? 1 : 2));
    }

}
