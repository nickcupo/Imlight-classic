// CLASSIC: Baxter in MB_Ironworks (Purloin the Plans, Bully the Boss, Knock on Wood). The zone moves him: SpawnInformant2
// (Enter_Activator Volume 0, on the way to the Blueprints) starts Spawn point 1 (MB-AIR3-NPC02, by the Factory) and
// despawns Spawn point 2 (MB-AIR3-NPC01), and 'spawn informant' later brings MB-AIR3-NPC03. The overlay had every C02
// talk goal on NPC01, so Purloin the Plans could not be handed in (ios-parity qa-s244, 2026-10-27). The r806919
// quest-helper points (Root.wad poi.dat) put those goals on NPC02.

using System;
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class IronworksBaxterTests {

    private const uint Npc01 = 39702, Npc02 = 39703, Npc03 = 39704;
    private const uint SpawnPoint2 = 145605, SpawnPoint1 = 145599, SpawnPoint0 = 145593;

    private static (string Persona, uint Actor)[] Speakers(JToken? dialogList)
        => (dialogList?["m_dialogs"] ?? new JArray())
            .SelectMany(d => d["m_dialogEntries"] ?? new JArray())
            .Select(e => ((string) e["m_personaName"]!, (uint) e["m_actorTemplateID"]!))
            .ToArray();

    private static JToken Goal(JObject quest, string name) => quest["m_goals"]!.Single(g => (string) g["m_goalName"]! == name);

    private static void SaysOnly(JToken? dialogList, string persona, uint actor)
        => Assert.All(Speakers(dialogList), s => Assert.Equal((persona + "_Persona", actor), s));

    [Fact]
    public void PurloinThePlansIsGivenByTheFirstBaxterAndHandedInToTheOneByTheFactory() {
        var quest = SunkenCityTests.QuestJson("MB-AIR3-C02-001");
        SaysOnly(quest["m_dialogList"], "MB-AIR3-NPC01", Npc01); // where Gate Crashers is handed in
        foreach (var goal in new[] { "Goal", "Goal 3" }) {
            Assert.Equal("MB-AIR3-NPC02", (string) Goal(quest, goal)["m_personaName"]!);
            SaysOnly(Goal(quest, goal)["m_dialogList"], "MB-AIR3-NPC02", Npc02);
        }
    }

    [Fact]
    public void BullyTheBossAndKnockOnWoodStayWithTheBaxterByTheFactoryUntilPopsFalls() {
        var bully = SunkenCityTests.QuestJson("MB-AIR3-C02-002");
        SaysOnly(bully["m_dialogList"], "MB-AIR3-NPC02", Npc02);
        Assert.Equal("MB-AIR3-NPC02", (string) Goal(bully, "Goal 2")["m_personaName"]!);
        SaysOnly(Goal(bully, "Goal 2")["m_dialogList"], "MB-AIR3-NPC02", Npc02);

        var knock = SunkenCityTests.QuestJson("MB-AIR3-C02-003");
        SaysOnly(knock["m_dialogList"], "MB-AIR3-NPC02", Npc02);
        Assert.Equal("MB-AIR3-NPC03", (string) Goal(knock, "Goal 4")["m_personaName"]!);
    }

    [Fact]
    public void TheZoneSwapsTheBaxtersAndANewInstanceBringsTheFactoryBaxterBackForAnOpenTalkGoal() {
        var triggers = Decode<WizZoneTriggers>("triggers.xml");
        var spawnData = Decode<SpawnManager>("spawnData.xml");

        var swap = triggers.m_triggers.Single(t => t is not null && (string) t.m_triggerName == "SpawnInformant2");
        Assert.Equal(["Enter_Activator Volume 0"], swap.m_fireEvents.Select(e => (string) e));
        Assert.Contains(swap.m_results.m_results.OfType<ResSpawn>(), s => (uint) s.m_spawnID == SpawnPoint1 && s.m_activate);
        Assert.Contains(swap.m_results.m_results.OfType<ResDespawn>(), d => (uint) d.m_spawnID == SpawnPoint2);

        var spawns = ClassicQuestSpawns.Find(null!, spawnData, triggers);
        Assert.Contains(Npc01, spawns.Placed);
        Assert.Contains(spawns.Dormant, s => s.Id == SpawnPoint1 && s.TriggerStarted && s.TemplateIds.SequenceEqual([Npc02]));
        Assert.Contains(spawns.Dormant, s => s.Id == SpawnPoint0 && s.TriggerStarted && s.TemplateIds.SequenceEqual([Npc03]));

        // A wizard who relogs at the Factory with Purloin the Plans open finds Baxter there; nobody else starts him early.
        Assert.Equal([SpawnPoint1], DormantSpawners.Plan(spawns.Dormant, _ => false, spawns.Placed, id => id == Npc02));
        Assert.Empty(DormantSpawners.Plan(spawns.Dormant, _ => false, spawns.Placed, id => id == Npc01));
    }

    private static T Decode<T>(string file) where T : PropertyClass {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "w101c-private", "extract", "r806919",
            "zones", "Marleybone-MB_Station-MB_Ironworks", file);
        if (!File.Exists(path)) {
            Assert.Skip("the private r806919 extraction is not on this machine");
        }

        var serializer = new BindSerializer { TypeRegistry = ClassicZoneTypeRegistry.Instance };
        Assert.True(serializer.Deserialize<T>(File.ReadAllBytes(path), 1, out var value));

        return value!;
    }
}
