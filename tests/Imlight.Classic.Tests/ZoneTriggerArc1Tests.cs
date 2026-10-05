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
 * The zone trigger plans of every Arc 1 zone the server serves, built from
 * the real zone packages and quest data: no gated trigger waits for an
 * event nothing posts, Sunken City's gates and the Tomb of the Beguiler's
 * doors come out as the client data says, and the triggers kept open and
 * the trigger objects left out are listed.
 *
 * USAGE EXAMPLE:
 * W101C_ZTRIG_REPORT=/path/report.md dotnet test --filter ZoneTriggerArc1
 *
 * NOTE:
 * Private data: the r806919 zone packages (aurorium), the served 2014
 * replacements (deploy/classic-zones) and the pinned SpiralDB cache; the
 * tests skip without them. Quest data is the cache plus this repo's
 * overlay, tombstones applied.
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
using System.Text;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Wad;
using Imlight.Common;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic;
using Newtonsoft.Json;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ZoneTriggerArc1Tests {

    public ZoneTriggerArc1Tests() {
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-ztrig-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
    }

    private static readonly string s_private = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "w101c-private");
    private static readonly string s_r806919 = Path.Combine(s_private, "aurorium/data/V_r806919.Wizard_1_610/Data/GameData");
    private static readonly string s_served = Path.Combine(s_private, "deploy/classic-zones");
    private static readonly string s_spiralDb = Path.Combine(s_private, "run/imlight/spiraldb-cache");
    private static readonly string[] s_arc1Worlds = ["WizardCity-", "Krokotopia-", "Marleybone-", "MooShu-", "DragonSpire-", "Grizzleheim-"];

    private static readonly Lazy<HashSet<string>> s_questEvents = new(() => ZoneTriggerPlans.BuildQuestEvents(LoadQuests()));
    private static readonly Lazy<List<ZoneTriggerPlan>> s_plans = new(() => [.. Arc1Wads().Select(Plan).Where(p => p is not null)!]);

    private static void SkipWithoutData() {
        if (!Directory.Exists(s_r806919) || !Directory.Exists(Path.Combine(s_spiralDb, "QuestTemplates"))) {
            Assert.Skip("the private zone packages or SpiralDB cache are not on this machine");
        }
    }

    private static List<QuestTemplate> LoadQuests() {
        var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto, NullValueHandling = NullValueHandling.Ignore };
        var byName = new Dictionary<string, QuestTemplate>(StringComparer.OrdinalIgnoreCase);
        var overlay = Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates");
        foreach (var dir in new[] { Path.Combine(s_spiralDb, "QuestTemplates"), overlay }) {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json")) {
                try {
                    if (JsonConvert.DeserializeObject<QuestTemplate>(File.ReadAllText(file), settings) is { } quest
                            && !string.IsNullOrEmpty(quest.m_questName)) {
                        byName[quest.m_questName] = quest;
                    }
                } catch (JsonException) {
                    // A file the server would skip too.
                }
            }
        }

        var tombstones = Path.Combine(overlay, "_disabled.txt");
        if (File.Exists(tombstones)) {
            foreach (var line in File.ReadAllLines(tombstones)) {
                var name = line.Split('#')[0].Trim();
                if (name.Length > 0) {
                    byName.Remove(name);
                }
            }
        }

        return [.. byName.Values];
    }

    private static IEnumerable<string> Arc1Wads()
        => Directory.EnumerateFiles(s_r806919, "*.wad").Select(Path.GetFileNameWithoutExtension)
            .Where(name => s_arc1Worlds.Any(prefix => name!.StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)!;

    private static T? Read<T>(Archive wad, string file) where T : PropertyClass {
        if (wad.OpenFile(file) is not { } data) {
            return null;
        }

        var serializer = new BindSerializer { TypeRegistry = ClassicZoneTypeRegistry.Instance };

        return serializer.Deserialize<T>(data.ToArray(), 1, out var result) ? result : null;
    }

    private static ZoneTriggerPlan? Plan(string wadName) {
        var served = Path.Combine(s_served, wadName + ".wad");
        var path = File.Exists(served) ? served : Path.Combine(s_r806919, wadName + ".wad");
        using var stream = File.OpenRead(path);
        var wad = ArchiveParser.Parse(stream);
        if (wad is null || Read<WizZoneTriggers>(wad, "triggers.xml") is not { } triggers) {
            return null;
        }

        var zonePath = wadName.Replace('-', '/');

        return ZoneTriggerPlans.Build(zonePath, [.. triggers.m_triggers.Where(t => t is not null)!],
            Read<WizZoneVolumes>(wad, "volumes.xml"), Read<WizZoneData>(wad, "gamedata.bin"), s_questEvents.Value);
    }

    private static ZoneTriggerPlan Zone(string zonePath) {
        SkipWithoutData();

        return Assert.Single(s_plans.Value, p => p.ZonePath == zonePath);
    }

    private static int Index(ZoneTriggerPlan plan, string name)
        => Enumerable.Range(0, plan.Triggers.Count).Single(i => (string) plan.Triggers[i].m_triggerName == name);

    private static bool Teleports(Trigger trigger) => trigger.m_results?.m_results?.Any(r => r is ResTeleport) == true;

    [Fact]
    public void EveryGatedTriggerWaitsForAnEventTheServerPosts() {
        SkipWithoutData();
        foreach (var plan in s_plans.Value) {
            foreach (var i in plan.Liveness.Gated) {
                Assert.True(plan.Liveness.Triggers[i].ActivateEvents.Any(plan.Liveness.Reachable.Contains),
                    $"{plan.ZonePath}: {plan.Liveness.Triggers[i].Name}");
                Assert.False(plan.Liveness.StartsArmed[i]);
            }

            foreach (var i in plan.Liveness.KeptOpen) {
                Assert.True(plan.Liveness.StartsArmed[i]);
            }
        }
    }

    [Fact]
    public void NoTriggerObjectThatNothingReleasesIsCreated() {
        SkipWithoutData();
        foreach (var plan in s_plans.Value) {
            var spawned = plan.TriggerObjectsToSpawn().Select(info => (string) info.m_zoneTag).ToHashSet();
            foreach (var stuck in plan.Liveness.Objects.Values.Where(o => o.Stuck)) {
                Assert.DoesNotContain(stuck.Tag, spawned);
            }
        }
    }

    [Fact]
    public void SunkenCityGatesWaitForTheirQuestEventsAndTakeMarlaAway() {
        var plan = Zone("WizardCity/WC_Streets/WC_Sunken_City");
        var live = plan.Liveness;
        foreach (var gate in new[] { "Gate01Trigger 0", "DeSpawnMarla03Trigger", "GATE03",
                                     "TriggerTeleportTower1", "TriggerTeleportTower2", "TriggerTeleportTower3" }) {
            Assert.Contains(Index(plan, gate), live.Gated);
            Assert.True(live.CanFire[Index(plan, gate)], gate);
        }

        foreach (var questEvent in new[] { "FirstGate", "SecondGate", "ThirdGate", "TowerOne", "SecondTower", "ThirdTower" }) {
            Assert.Contains(questEvent, live.ReplaySafe);
        }

        var spawned = plan.TriggerObjectsToSpawn().Select(info => (string) info.m_zoneTag).ToHashSet();
        foreach (var gateObject in new[] { "DynaTrigger_GauntletDoor instance (1)", "DynaTrigger_KT_DoorCollision instance",
                                           "Gate of Paulson", "DynaTrigger_KT_DoorCollision instance (1)",
                                           "DynaTrigger_GauntletDoor instance", "DynaTrigger_KT_DoorCollision instance (3)" }) {
            Assert.Contains(gateObject, spawned);
            Assert.True(live.Objects[gateObject].Releasable, gateObject);
        }

        foreach (var marla in new[] { "WC_ST07_NPC02 instance", "WC_ST07_NPC03 instance", "WC_ST07_NPC04 instance" }) {
            Assert.True(live.Objects[marla].Placed, marla);
        }
    }

    [Fact]
    public void TombOfTheBeguilerDoorsOpenOnTheirQuestEvents() {
        var map00 = Zone("Krokotopia/KT_Tomb/Interiors/KT_Crypt06_Map00_Storm");
        Assert.Contains(Index(map00, "Trigger Teleport to Map01 (1)"), map00.Liveness.Gated);
        Assert.Contains("TalkedTo", map00.Liveness.ReplaySafe);
        Assert.Contains("HasQuestInTheDoghouse", map00.Liveness.ReplaySafe);

        var map01 = Zone("Krokotopia/KT_Tomb/Interiors/KT_Crypt06_Map01_Fire");
        Assert.Contains(Index(map01, "Trigger (2)"), map01.Liveness.Gated);
        Assert.True(map01.Liveness.Objects["DynaTrigger_KT_DoorCollision instance (1)"].Releasable); // TalkedToRhea's gate

        var gauntlet = Zone("Krokotopia/KT_Tomb/Interiors/KT_Crypt06_Map03_Gauntlet");
        foreach (var wall in new[] { "DynaTrigger_KT_DoorCollision instance", "DynaTrigger_KT_DoorCollision instance (Room5)" }) {
            Assert.True(gauntlet.Liveness.Objects[wall].Releasable, wall); // Monster_Killed disables the wall's trigger
        }
    }

    [Fact]
    public void WritesTheArc1Report() {
        SkipWithoutData();
        var path = Environment.GetEnvironmentVariable("W101C_ZTRIG_REPORT");
        var report = new StringBuilder();
        var plans = s_plans.Value;
        var gatedDoors = 0;
        var keptOpen = 0;
        var keptOpenDoors = 0;
        var stuck = 0;
        var spawned = 0;
        report.AppendLine("# Zone triggers: Arc 1 plan (generated by ZoneTriggerArc1Tests)").AppendLine();
        report.AppendLine("Zone | Trigger | Kind | Waits for | Results").AppendLine("--- | --- | --- | --- | ---");
        foreach (var plan in plans) {
            var live = plan.Liveness;
            spawned += plan.TriggerObjectsToSpawn().Count();
            foreach (var i in live.KeptOpen) {
                keptOpen++;
                var trigger = plan.Triggers[i];
                var door = Teleports(trigger);
                keptOpenDoors += door ? 1 : 0;
                var why = live.Triggers[i].ActivateEvents.Any(s_questEvents.Value.Contains)
                    ? "a quest posts it, but not replay-safe here" : "nothing the server runs posts it";
                report.AppendLine($"{plan.ZonePath} | {live.Triggers[i].Name} | kept open{(door ? " (door)" : "")} | "
                    + $"{string.Join(", ", live.Triggers[i].ActivateEvents)} ({why}) | {Describe(trigger)}");
            }

            foreach (var i in live.Gated) {
                gatedDoors += Teleports(plan.Triggers[i]) ? 1 : 0;
                if (Teleports(plan.Triggers[i])) {
                    report.AppendLine($"{plan.ZonePath} | {live.Triggers[i].Name} | gated door | "
                        + $"{string.Join(", ", live.Triggers[i].ActivateEvents)} | {Describe(plan.Triggers[i])} | enabled by "
                        + string.Join("; ", Enablers(plan, live.Triggers[i].ActivateEvents)));
                }
            }

            foreach (var o in live.Objects.Values.Where(o => o.Stuck)) {
                stuck++;
                report.AppendLine($"{plan.ZonePath} | {o.Tag} | object left out | "
                    + $"{string.Join(", ", o.Owners.Select(i => live.Triggers[i].Name))} | nothing that fires releases it");
            }
        }

        report.AppendLine().AppendLine($"Zones {plans.Count}; triggers kept open {keptOpen} ({keptOpenDoors} doors); gated doors "
            + $"{gatedDoors}; trigger objects created {spawned}; left out {stuck}.");
        if (!string.IsNullOrEmpty(path)) {
            File.WriteAllText(path, report.ToString());
        }

        Assert.NotEmpty(plans);
    }

    // Who posts the events: a firing trigger of the zone (and what it fires on), or quest results.
    private static IEnumerable<string> Enablers(ZoneTriggerPlan plan, IEnumerable<string> events) {
        foreach (var e in events.Where(plan.Liveness.Reachable.Contains)) {
            if (s_questEvents.Value.Contains(e) && plan.Liveness.ReplaySafe.Contains(e)) {
                yield return $"quest:{e}";
            }

            for (var i = 0; i < plan.Liveness.Triggers.Count; i++) {
                var t = plan.Liveness.Triggers[i];
                if (plan.Liveness.CanFire[i] && t.PostedEvents.Contains(e)) {
                    var upstream = t.FireEvents.Where(plan.Liveness.Reachable.Contains).ToList();
                    var quest = upstream.Where(u => s_questEvents.Value.Contains(u) && plan.Liveness.ReplaySafe.Contains(u)).ToList();
                    yield return $"{t.Name}<-{string.Join("/", upstream)}{(quest.Count > 0 ? " (quest)" : "")}";
                }
            }
        }
    }

    private static string Describe(Trigger trigger)
        => string.Join(" ", (trigger.m_results?.m_results ?? []).Select(r => r switch {
            null => "?",
            ResPostEvent post => $"PostEvent({post.m_eventName})",
            ClassicResTriggerObjectPresence presence => $"{(presence.Adds ? "Add" : "Remove")}({presence.ObjectName})",
            ClassicResModifyTriggerObject modify => $"Modify({modify.ObjectName}:{modify.State})",
            _ => r.GetType().Name.Replace("Classic", ""),
        }));

}
