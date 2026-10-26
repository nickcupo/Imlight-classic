// CLASSIC: rules.kill_gates (classic-data/rules/kill-gates-2009.yaml). The Dragonspyre Detention and Necropolis
// gauntlets' third room (3Room01_3) opens its door and drops its wall when "Trigger-MobWatcher" sees a Vile Apparition
// (Ghost-Red-R8) defeated, but the room spawns only Ranulf Moonclaw (Wizard-Male-Spikey-Blue-Boss-R8-1) and a Death
// Minotaur, in the r806919 and the 2014 data alike: the wall never went. The watcher now waits for Ranulf.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class KillGateTests {

    private const string Detention = "DragonSpire/DS_A2_Battle/Interiors/DS_Detention_Gauntlet_3Room01_Sub/3Room01_3";
    private const string Necropolis = "DragonSpire/DS_A2_Battle/Interiors/DS_Necropolis_Gauntlet_3Room01_Sub/3Room01_3";
    private const string Ranulf = "Wizard-Male-Spikey-Blue-Boss-R8-1";

    public KillGateTests() {
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-killgate-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
    }

    private static KillGates Gates() => KillGatesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "kill-gates-2009.yaml"));

    [Fact]
    public void TheTwoGauntletRoomsWaitForRanulfAndEveryClassicProfileHasThem() {
        var gates = Gates();
        Assert.Equal([Detention, Necropolis], gates.Fixes.Select(f => f.Zone));
        Assert.All(gates.Fixes, fix => {
            Assert.Equal("Trigger-MobWatcher", fix.Trigger);
            Assert.Equal("Ghost-Red-R8.AdjRef", fix.From);
            Assert.Equal(Ranulf + ".AdjRef", fix.To);
        });
        Assert.Single(gates.For(Detention.ToLowerInvariant()));
        Assert.Empty(gates.For("Krokotopia/KT_Tomb/KT_KaranahnPalace"));

        foreach (var profile in new[] { "late-2009", "arc1-2009h1", "october-2010-arc1" }) {
            Assert.Equal("rules/kill-gates-2009.yaml", ClassicDataFixture.LoadProfile(profile).Rules.KillGates);
        }
    }

    [Theory]
    [InlineData("DragonSpire-DS_A2_Battle-Interiors-DS_Detention_Gauntlet_3Room01_Sub-3Room01_3", Detention)]
    [InlineData("DragonSpire-DS_A2_Battle-Interiors-DS_Necropolis_Gauntlet_3Room01_Sub-3Room01_3", Necropolis)]
    public void TheRoomsWatcherCountsRanulfAfterTheFix(string folder, string zone) {
        var data = Extracted(folder);
        var before = Kill(data);
        Assert.Equal(["Ghost-Red-R8.AdjRef"], before.m_adjectiveList);
        Assert.False(KilledMonster.Matches(before.m_adjectiveList, Ranulf, ["MONST_Wizards"]));

        Assert.Same(data, ClassicKillGates.Apply(Gates(), zone, data));

        var after = Kill(data);
        Assert.Equal([Ranulf + ".AdjRef"], after.m_adjectiveList);
        Assert.True(KilledMonster.Matches(after.m_adjectiveList, Ranulf, ["MONST_Wizards"]));
        Assert.False(KilledMonster.Matches(after.m_adjectiveList, "Minotaur-Death-R7", ["Giant", "Minotaur"])); // the boss, not the Minotaur
        // The watcher still drops the wall and opens the gauntlet door: clearing the room is still needed.
        var watcher = Watcher(data);
        Assert.Contains(watcher.m_results.m_results.OfType<ResPostEvent>(), p => p.m_eventName == "Disable_Trigger-ShowBlocker");
    }

    [Fact]
    public void AFixForAnotherZoneOrAnEntryTheDataNoLongerHasChangesNothing() {
        var data = Extracted("DragonSpire-DS_A2_Battle-Interiors-DS_Detention_Gauntlet_3Room01_Sub-3Room01_3");
        ClassicKillGates.Apply(Gates(), Necropolis.Replace("Necropolis", "Elsewhere"), data);
        Assert.Equal(["Ghost-Red-R8.AdjRef"], Kill(data).m_adjectiveList);

        var stale = new KillGates("kill-gates-test", [], [new KillGateFix(Detention, "Trigger-MobWatcher", "Ghost-Blue-R8.AdjRef", Ranulf + ".AdjRef")], "");
        ClassicKillGates.Apply(stale, Detention, data);
        Assert.Equal(["Ghost-Red-R8.AdjRef"], Kill(data).m_adjectiveList);
    }

    private static WizZoneTriggers Extracted(string folder) {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "w101c-private", "extract", "r806919",
            "zones", folder, "triggers.xml");
        if (!File.Exists(path)) {
            Assert.Skip("the private r806919 extraction is not on this machine");
        }

        var serializer = new BindSerializer { TypeRegistry = ClassicZoneTypeRegistry.Instance };
        Assert.True(serializer.Deserialize<WizZoneTriggers>(File.ReadAllBytes(path), 1, out var triggers));

        return triggers;
    }

    private static Trigger Watcher(WizZoneTriggers data)
        => data.m_triggers.Single(t => t is not null && (string) t.m_triggerName == "Trigger-MobWatcher");

    private static ClassicReqMonsterKilled Kill(WizZoneTriggers data)
        => Assert.Single(Flatten(Watcher(data).m_requirements).OfType<ClassicReqMonsterKilled>());

    private static IEnumerable<Requirement> Flatten(Requirement? requirement) => requirement switch {
        RequirementList list => (list.m_requirements ?? []).SelectMany(Flatten),
        null => [],
        _ => [requirement],
    };
}
