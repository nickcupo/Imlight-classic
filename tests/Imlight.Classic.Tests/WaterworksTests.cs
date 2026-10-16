/*
 * ========================================================================
 * THE WATERWORKS (MAY 2011, WINTERTUSK-2011 ONLY)
 * ========================================================================
 *
 * PURPOSE:
 * Pins work package F: the eight WC-TUG quests (Blad Raveneye's emergency,
 * Herman's access tower, the dive, Glurrup and the valve, the four pressure
 * latches and Sylster Glowstorm), their gating per profile (tombstoned for
 * every profile, restored by wintertusk-2011 only, inherited by any later
 * child), the zones (Waterworks 2011-05-11, Crab Alley 2010-09-21), the
 * Triton Avenue anchor post and the Crab Alley sigil, the client's own room
 * door checks, and the Waterworks gauntlet group.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter Waterworks
 *
 * NOTE:
 * A server integration test (CoreLib requirement handlers); reads the
 * monorepo's classic-data. The chain walk evaluates each quest's own
 * requirements with the server's RequirementDispatcher, step by step.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/16/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Classic.Travel;
using Imlight.Common;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class WaterworksTests : IDisposable {

    private const string Wintertusk = ClassicProfiles.Wintertusk2011;
    private const string Celestia = ClassicProfiles.Celestia2010;
    private const string October = ClassicProfiles.October2010;

    internal const string BladsEmergency = "WC-TUG-C01-001";
    internal const string AccessTower = "WC-TUG-C02-001";
    internal const string Dive = "WC-TUG-C02-002";
    internal const string Glurrup = "WC-TUG-C03-001";
    internal const string Chamber = "WizardCity/Gauntlets/WC_Triton_Gauntlet1/WC_Triton_Gauntlet_01";
    internal const string CrabAlley = "WizardCity/WC_Streets/WC_Triton_Underwater/WC_Triton_Underwater";
    private const string Triton = "WizardCity/WC_Streets/WC_Triton";

    internal static readonly string[] Chain = [
        BladsEmergency, AccessTower, Dive, Glurrup, "WC-TUG-C03-002", "WC-TUG-C03-003", "WC-TUG-C03-004", "WC-TUG-C03-005",
    ];

    internal static readonly string[] Rooms = [
        Chamber, Chamber + "a",
        .. Enumerable.Range(2, 7).Select(n => $"WizardCity/Gauntlets/WC_Triton_Gauntlet1/WC_Triton_Gauntlet_0{n}"),
    ];

    private static readonly JsonSerializerSettings s_json = new() {
        TypeNameHandling = TypeNameHandling.Auto,
        NullValueHandling = NullValueHandling.Ignore,
    };

    public WaterworksTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(new ClassicRules(ZoneFixture.Profile(levelCap: 60), ZoneFixture.MinimalMap()));
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-waterworks-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    private static string Overlay(params string[] path)
        => File.ReadAllText(Path.Combine([ClassicDataFixture.Root, "spiraldb-overlay", .. path]));

    internal static QuestTemplate LoadQuest(string name)
        => JsonConvert.DeserializeObject<QuestTemplate>(Overlay("QuestTemplates", name + ".json"), s_json)!;

    private static Wizard NewWizard(string zone) => new() {
        CharId = 1,
        Zone = zone,
        MagicSchoolBehavior = new ServerMagicSchoolBehavior { Level = 60 },
        QuestBehavior = new ServerQuestBehavior(),
    };

    private static bool Meets(RequirementList? requirements, Wizard wizard)
        => requirements is null || RequirementDispatcher.EvaluateRequirements(requirements,
            new QuestRequirementContext(requirements, null, null, wizard, ""));

    private static void Complete(Wizard wizard, QuestTemplate quest) {
        wizard.QuestBehavior.CurrentQuestInstances.RemoveAll(q => q.QuestName == quest.m_questName);
        Assert.True(wizard.QuestBehavior.SetQuestRegistryValue(quest.m_questName, "Complete", 1));
    }

    private static RequirementList TravelRequirements(string file, string trigger)
        => JsonConvert.DeserializeObject<RequirementList>(
            JObject.Parse(Overlay("ZoneTransfer", file))["Teleports"]!
                .Single(t => (string?) t["TriggerName"] == trigger)["Teleport"]!["m_requirements"]!.ToString(), s_json)!;

    private static JToken Travel(string file, string trigger)
        => JObject.Parse(Overlay("ZoneTransfer", file))["Teleports"]!.Single(t => (string?) t["TriggerName"] == trigger)["Teleport"]!;

    // ---------------------------------------------------------------- gating per profile

    [Fact]
    public void TheChainIsTombstonedForEveryProfileAndOnlyWintertuskRestoresIt() {
        var lines = File.ReadAllLines(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates", "_disabled.txt"));
        ConcurrentDictionary<string, QuestTemplate> Quests() => new(
            Chain.ToDictionary(name => name, name => new QuestTemplate { m_questName = name }), StringComparer.OrdinalIgnoreCase);

        foreach (var older in new[] { "late-2009", "arc1-2009h1", October, Celestia }) {
            var quests = Quests();
            var result = SpiralDB.ApplyTombstoneLines(lines, quests, SpiralDB.RestoredQuests(ClassicDataFixture.LoadProfile(older)));
            Assert.All(Chain, name => Assert.Contains(name, result.Removed));
            Assert.Empty(quests);
        }

        var kept = Quests();
        var restored = SpiralDB.ApplyTombstoneLines(lines, kept, SpiralDB.RestoredQuests(ClassicDataFixture.LoadProfile(Wintertusk)));
        Assert.Equal(Chain.Order(), restored.Restored.Where(Chain.Contains).Order());
        Assert.Equal(Chain.Length, kept.Count);
        Assert.Equal(Chain, ClassicDataFixture.LoadProfile(Wintertusk).RestoredQuests.Where(Chain.Contains));
    }

    [Fact]
    public void RestoredQuestsAddUpDownTheExtendsChain() {
        using var data = new TempClassicData();
        data.WriteProfile("base", """
            id: base
            title: Base
            status: canonical
            cutoff: 2010-05-25
            restored_quests: [WC-A]
            """);
        data.WriteProfile("child", """
            id: child
            title: Child
            status: optional
            extends: base
            cutoff: 2010-10-31
            restored_quests: [WC-B, wc-a]
            """);
        data.WriteProfile("grandchild", """
            id: grandchild
            title: Grandchild
            status: optional
            extends: child
            cutoff: 2011-05-31
            restored_quests: [WC-C]
            """);

        Assert.Equal(["WC-A"], ClassicProfileLoader.Load(data.ProfilesPath, "base").RestoredQuests);
        Assert.Equal(["WC-A", "WC-B"], ClassicProfileLoader.Load(data.ProfilesPath, "child").RestoredQuests);
        Assert.Equal(["WC-A", "WC-B", "WC-C"], ClassicProfileLoader.Load(data.ProfilesPath, "grandchild").RestoredQuests);
    }

    [Fact]
    public void WintertuskKeepsEveryQuestCelestiaRestores() {
        var celestia = ClassicDataFixture.LoadProfile(Celestia);
        var wintertusk = ClassicDataFixture.LoadProfile(Wintertusk);

        Assert.All(celestia.RestoredQuests, name => Assert.Contains(name, wintertusk.RestoredQuests));
        Assert.DoesNotContain(celestia.RestoredQuests, Chain.Contains);
        Assert.DoesNotContain(ClassicDataFixture.LoadProfile(October).RestoredQuests, Chain.Contains);
    }

    // ---------------------------------------------------------------- zones

    public static IEnumerable<object[]> WaterworksRooms() => Rooms.Select(zone => new object[] { zone });

    [Theory]
    [MemberData(nameof(WaterworksRooms))]
    public void TheWaterworksOpensOnlyUnderWintertusk(string room) {
        Assert.True(ClassicDataFixture.RealRules(Wintertusk).IsZoneAllowed(room).Allowed);
        Assert.False(ClassicDataFixture.RealRules(Celestia).IsZoneAllowed(room).Allowed);
        Assert.False(ClassicDataFixture.RealRules(October).IsZoneAllowed(room).Allowed);
        Assert.False(ClassicDataFixture.RealRules("late-2009").IsZoneAllowed(room).Allowed);
    }

    [Fact]
    public void CrabAlleyIsSeptember2010ContentClosedUnderTheOctoberZoneCutoff() {
        Assert.False(ClassicDataFixture.RealRules(October).IsZoneAllowed(CrabAlley).Allowed);
        Assert.True(ClassicDataFixture.RealRules(Celestia).IsZoneAllowed(CrabAlley).Allowed);
        Assert.True(ClassicDataFixture.RealRules(Wintertusk).IsZoneAllowed(CrabAlley).Allowed);
        Assert.True(ClassicDataFixture.RealRules(October).IsZoneAllowed(Triton).Allowed);
    }

    // ---------------------------------------------------------------- the chain

    [Fact]
    public void TheChainWalksInOrderAndEachQuestOpensOnlyAfterTheOneBefore() {
        var quests = Chain.Select(LoadQuest).ToArray();
        var wizard = NewWizard(Triton);

        for (var step = 0; step < quests.Length; step++) {
            var quest = quests[step];
            wizard.Zone = quest.m_requirements!.m_requirements.OfType<ReqInZone>().FirstOrDefault()?.m_zoneName
                ?? (step == 0 ? Triton : CrabAlley);

            // Every later quest is still closed, this one is open.
            for (var later = step + 1; later < quests.Length; later++) {
                Assert.False(Meets(quests[later].m_requirements, wizard), $"{quests[later].m_questName} opened before {quest.m_questName}");
            }

            Assert.True(Meets(quest.m_requirements, wizard), $"{quest.m_questName} did not open after the quest before it");
            Assert.True(wizard.QuestBehavior.AddQuest(new QuestInstance(quest, wizard.CharId)));

            // The dive (C02-002) stays held until Sylster falls; the dungeon quests need it held, not complete.
            if (quest.m_questName != Dive) {
                Complete(wizard, quest);
            }
        }
    }

    [Fact]
    public void TheDungeonQuestsAreGrantedInTheEntranceChamber() {
        foreach (var name in Chain.Skip(3)) {
            var quest = LoadQuest(name);
            Assert.Equal(Chamber, Assert.Single(quest.m_requirements!.m_requirements.OfType<ReqInZone>()).m_zoneName);
            Assert.Null((quest.m_dialogList as ActorDialogList)?.m_dialogs?.FirstOrDefault(d => d.m_dialogTag == "Prep"));
        }

        foreach (var name in Chain.Take(3)) {
            Assert.Empty(LoadQuest(name).m_requirements!.m_requirements.OfType<ReqInZone>());
        }
    }

    [Fact]
    public void EveryQuestEndsOnItsOwnRewardTable() {
        foreach (var name in Chain) {
            var end = Assert.Single(LoadQuest(name).m_endResults.m_results.OfType<ResDropTable>());
            Assert.Equal(name, end.m_tableName);
        }
    }

    [Fact]
    public void SylsterAndHisTwoMinionsArriveWhenTheLastQuestReachesHim() {
        var last = LoadQuest("WC-TUG-C03-005");
        var fight = last.m_goals.Single(g => g.m_goalName == "Goal 2");
        var results = fight.m_activateResults.m_results;

        Assert.Equal("TriDoor1Open", Assert.Single(results.OfType<ResPostEvent>()).m_eventName);
        Assert.Equal(new ulong[] { 2682700, 2682701 }, results.OfType<ResSpawn>().Select(s => s.m_spawnID).Order());
        Assert.All(results.OfType<ResSpawn>(), s => Assert.True(s.m_activate));
        // The dive's own Sylster goal and the last quest's are the same kill.
        Assert.Equal(((BountyGoalTemplate) fight).m_npcAdjectives,
            ((BountyGoalTemplate) LoadQuest(Dive).m_goals.Single(g => g.m_goalName == "Goal 2")).m_npcAdjectives);
    }

    // ---------------------------------------------------------------- client gates

    [Fact]
    public void TheAnchorPostTakesAWizardWhoHoldsOrFinishedBladsQuest() {
        var post = TravelRequirements("WizardCity-WC_Streets-WC_Triton.waterworks.json", "WC-TeleporttoCrabAlley instance");
        Assert.Equal(CrabAlley, (string?) Travel("WizardCity-WC_Streets-WC_Triton.waterworks.json", "WC-TeleporttoCrabAlley instance")["m_destinationZone"]);
        var wizard = NewWizard(Triton);

        Assert.False(Meets(post, wizard));
        Assert.True(wizard.QuestBehavior.AddQuest(new QuestInstance(LoadQuest(BladsEmergency), wizard.CharId)));
        Assert.True(Meets(post, wizard));
        Complete(wizard, LoadQuest(BladsEmergency));
        Assert.True(Meets(post, wizard));
    }

    [Fact]
    public void TheCrabAlleySigilLeadsIntoTheChamberAfterTheAccessTower() {
        var sigil = Travel("WizardCity-WC_Streets-WC_Triton_Underwater-WC_Triton_Underwater.waterworks.json", "TeleportToTriGauntlet");
        Assert.Equal(Chamber, (string?) sigil["m_destinationZone"]);

        // The served (2014) Crab Alley sigil's own requirement, as the client zone data states it.
        var client = new RequirementList {
            m_operator = Operator.ROP_AND,
            m_requirements = [new ReqHasEntry { m_entryName = "WC-TUG-C02-001_Complete", m_isQuestRegistry = false, m_operator = Operator.ROP_AND }],
        };
        var wizard = NewWizard(CrabAlley);
        Assert.False(Meets(client, wizard));
        Complete(wizard, LoadQuest(AccessTower));
        Assert.True(Meets(client, wizard));
    }

    [Theory]
    [InlineData("Trigger (7)", "WC-TUG-C03-002")]
    [InlineData("Doorto3Trigger", "WC-TUG-C03-003")]
    [InlineData("Trigger (4)", "WC-TUG-C03-004")]
    [InlineData("DoorTo7Trigger", "WC-TUG-C03-005")]
    public void EachChamberDoorOpensForTheLatchQuestItChecks(string trigger, string quest) {
        // The client trigger in WC_Triton_Gauntlet_01 checks ReqHasQuest(quest): held, or done on a later run.
        var door = new RequirementList {
            m_operator = Operator.ROP_AND,
            m_requirements = [new ReqHasQuest { m_questName = quest, m_operator = Operator.ROP_AND }],
        };
        var wizard = NewWizard(Chamber);
        Assert.False(Meets(door, wizard), trigger);
        Assert.True(wizard.QuestBehavior.AddQuest(new QuestInstance(LoadQuest(quest), wizard.CharId)), trigger);
        Assert.True(Meets(door, wizard), trigger);
        Complete(wizard, LoadQuest(quest));
        Assert.True(Meets(door, wizard), trigger);
    }

    // ---------------------------------------------------------------- resets

    [Fact]
    public void TheWaterworksIsOneGauntletOfNineRooms() {
        var rules = InstanceResetRulesLoader.Load(Path.Combine(ClassicDataFixture.Root,
            ClassicDataFixture.LoadProfile(Wintertusk).Rules.InstanceResets!));
        var group = rules.GroupOf(Rooms[4])!;

        Assert.Equal("WC_Triton_Gauntlet1", group.Id);
        Assert.Equal(InstanceKind.Gauntlet, group.Kind);
        Assert.Equal(Rooms.Order(StringComparer.OrdinalIgnoreCase), group.Zones.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(ClassicDataFixture.LoadProfile(Wintertusk).IsListedIn(rules.Profiles));
    }

    // ---------------------------------------------------------------- creatures, loot, badge

    private static string RuleFile(string profile, Func<ClassicProfile, string?> rule)
        => Path.Combine(ClassicDataFixture.Root, rule(ClassicDataFixture.LoadProfile(profile))!);

    private const ulong Sylster = 439075, Luska = 439083;

    [Fact]
    public void TheWaterworksCreaturesHaveTheirMay2011HealthOnlyUnderWintertusk() {
        var wintertusk = MobStatsLoader.Load(RuleFile(Wintertusk, p => p.Rules.MobStats), Wintertusk);
        Assert.Equal(24980, wintertusk.HealthOf(Sylster));
        Assert.Equal(21150, wintertusk.HealthOf(Luska));
        Assert.Equal(2095, wintertusk.HealthOf(469335)); // the boss-flagged Rivershell Guard copy

        foreach (var older in new[] { Celestia, October }) {
            var stats = MobStatsLoader.Load(RuleFile(older, p => p.Rules.MobStats), older);
            Assert.Null(stats.HealthOf(Sylster));
            Assert.Null(stats.HealthOf(Luska));
        }
    }

    // CLASSIC: owner delegated 2026-10-16: the eleven Celestia creatures with a dated Dec 2010 - May 2011 page get that
    // health under wintertusk-2011 only; celestia-2010 (cutoff 2010-10-31) keeps the r806919 template health.
    [Theory]
    [InlineData(267121UL, 7785)] // Queen Calypso, oldid 128083 (2011-02-11)
    [InlineData(267125UL, 6540)] // Tempus Stormfist, oldid 126501 (2011-01-22)
    [InlineData(267106UL, 3695)] // Optio Agenor, oldid 124410 (2010-12-29), below the client's 3,825
    [InlineData(267072UL, 910)]  // Crustacean Clawcutter, corrected revision 131245 (2011-04-01)
    public void CelestiaCreaturesHaveTheirDatedEarly2011HealthOnlyUnderWintertusk(ulong template, int health) {
        Assert.Equal(health, MobStatsLoader.Load(RuleFile(Wintertusk, p => p.Rules.MobStats), Wintertusk).HealthOf(template));
        foreach (var older in new[] { Celestia, October }) {
            Assert.Null(MobStatsLoader.Load(RuleFile(older, p => p.Rules.MobStats), older).HealthOf(template));
        }
    }

    // Hat/Robe/Shoe-T6-TUW-001..007 in school order, and the seven Crown-S03 Mastery Amulets.
    private static readonly string[] Schools = ["Balance", "Death", "Fire", "Ice", "Life", "Myth", "Storm"];
    private static readonly ulong[] Amulets = [.. Enumerable.Range(0, 7).Select(i => 468162UL + (ulong) i)];

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(6)]
    public void TheBossesDropOnlyTheWizardsOwnSchoolGearAndMasteryAmulets(int school) {
        var rules = MobRewardRulesLoader.Load(RuleFile(Wintertusk, p => p.Rules.MobRewards), Wintertusk);
        var random = new Random(1234);
        var luska = new List<ulong>();
        var sylster = new List<ulong>();
        for (var i = 0; i < 3000; i++) {
            luska.AddRange(rules.Roll(new MobInfo(Luska, 12, MobKind.Boss), random, 1.0, Schools[school]).Items);
            sylster.AddRange(rules.Roll(new MobInfo(Sylster, 12, MobKind.Boss), random, 1.0, Schools[school]).Items);
        }

        var hat = 455354UL + (ulong) school;
        Assert.Contains(hat, luska);
        Assert.All(luska, item => Assert.True(item == hat || Amulets.Contains(item), $"Luska dropped {item}"));
        var robe = 455361UL + (ulong) school;
        var boots = 455368UL + (ulong) school;
        Assert.Contains(robe, sylster);
        Assert.Contains(boots, sylster);
        Assert.All(sylster, item => Assert.True(item == robe || item == boots || Amulets.Contains(item), $"Sylster dropped {item}"));
    }

    [Fact]
    public void MasteryAmuletsRollAtTheRareBossRateAndTheGearKeepsItsShare() {
        // CLASSIC: May 2011 notes say only "rare"; owner delegated 2026-10-16: the loot rules' unseen_chance (inferred).
        var rules = MobRewardRulesLoader.Load(RuleFile(Wintertusk, p => p.Rules.MobRewards), Wintertusk);
        Assert.Equal(0.01, rules.ItemDrops.UnseenChance);
        foreach (var boss in new[] { Luska, Sylster }) {
            var mob = rules.Find(boss)!;
            var amulets = mob.Items.Where(e => Amulets.Contains(e.Template)).ToList();
            Assert.Equal(7, amulets.Count);
            // Own-school list: the school's gear plus the seven amulets (other schools' gear is not listed for this wizard).
            var gear = mob.Items.Count(e => !Amulets.Contains(e.Template)) / 7;
            var listed = gear + 7;
            Assert.All(amulets, e => {
                Assert.True(e.Rare);
                Assert.Null(e.Chance);
                Assert.Equal(0.01, rules.ItemDrops.ChanceOf(e, MobKind.Boss, listed, tallied: false));
            });
            var piece = mob.Items.First(e => !Amulets.Contains(e.Template));
            Assert.False(piece.Rare);
            Assert.Equal(rules.ItemDrops.Expected[MobKind.Boss] / listed, rules.ItemDrops.ChanceOf(piece, MobKind.Boss, listed, tallied: false));
        }
    }

    [Fact]
    public void NoOlderProfileDocumentsTheWaterworksBossesAndNoLaterJewelTableIsRolled() {
        foreach (var older in new[] { Celestia, October }) {
            var rules = MobRewardRulesLoader.Load(RuleFile(older, p => p.Rules.MobRewards), older);
            Assert.Null(rules.Find(Sylster));
            Assert.Null(rules.Find(Luska));
        }

        var wintertusk = MobRewardRulesLoader.Load(RuleFile(Wintertusk, p => p.Rules.MobRewards), Wintertusk);
        var gear = Enumerable.Range(0, 21).Select(i => 455354UL + (ulong) i).Concat(Amulets).ToHashSet();
        foreach (var boss in new[] { Sylster, Luska }) {
            var mob = wintertusk.Find(boss)!;
            Assert.All(mob.Items, entry => Assert.Contains(entry.Template, gear));
            Assert.Empty(mob.TreasureCards);
            // The client loot-table names (ZF/AV/AZ jewel tables among them) are never a fallback for these bosses.
            Assert.False(wintertusk.TreasureCardFallback.ContainsKey(boss));
            Assert.False(wintertusk.ReagentFallback.ContainsKey(boss));
        }
    }

    [Fact]
    public void WaterworksWarriorIsAWintertuskBadgeForYouGoFirst() {
        var wintertusk = BadgeRulesLoader.Load(RuleFile(Wintertusk, p => p.Rules.Badges), Wintertusk);
        var badge = Assert.Single(wintertusk.Badges, b => b.Id == "waterworks-warrior");
        Assert.Equal("Badges_00000492", badge.NameKey);

        foreach (var older in new[] { Celestia, October }) {
            Assert.DoesNotContain(BadgeRulesLoader.Load(RuleFile(older, p => p.Rules.Badges), older).Badges, b => b.Id == "waterworks-warrior");
        }
    }

}
