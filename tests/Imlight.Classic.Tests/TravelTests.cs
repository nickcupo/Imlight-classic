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
 * CLASSIC TRAVEL TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The world-unlock rule (profile world_unlocks and ClassicRules.IsWorldUnlocked)
 * and the ZoneTransfer overlay merge rule.
 *
 * USAGE EXAMPLE:
 * dotnet test --filter TravelTests
 *
 * NOTE:
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Travel;
using Imlight.Classic.Zones;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class TravelTests {

    private sealed class Progress : IPlayerProgress {
        public HashSet<string> Active { get; } = [];
        public HashSet<string> Completed { get; } = [];
        public HashSet<string> Entries { get; } = [];
        public int Level { get; set; } = 1;

        public bool HasActiveQuest(string questName) => Active.Contains(questName);
        public bool HasCompletedQuest(string questName) => Completed.Contains(questName);
        public bool HasEntry(string entryName) => Entries.Contains(entryName);
    }

    private sealed record Entry(string Name, string? Value);

    private const string Worlds = """
          wizard_city: { name: Wizard City, hub_key: WizardCity, prefixes: [WizardCity] }
          krokotopia: { name: Krokotopia, hub_key: Krokotopia, prefixes: [Krokotopia] }
          grizzleheim: { name: Grizzleheim, hub_key: Grizzleheim, prefixes: [Grizzleheim, GrizzleheimLite] }
        """;

    private static ClassicRules RulesWith(string unlocks) {
        using var data = new TempClassicData();
        data.WriteProfile("p", $"""
            id: p
            title: P
            status: canonical
            cutoff: 2010-05-25
            worlds: [wizard_city, krokotopia, grizzleheim]
            {unlocks}
            """);
        data.WriteZoneFile("worlds.yaml", ZoneFixture.WorldsYaml(Worlds));

        return new ClassicRules(ClassicProfileLoader.Load(data.ProfilesPath, "p"), ZoneWorldMapLoader.Load(data.WorldsPath));
    }

    [Fact]
    public void QuestCheckPassesWhileActiveOrOnceComplete() {
        var check = new UnlockCheck(UnlockCheckKind.Quest, "WC-MAIN-C01-010", 0);
        var progress = new Progress();

        Assert.False(check.IsMet(progress));
        progress.Active.Add("WC-MAIN-C01-010");
        Assert.True(check.IsMet(progress));
        progress.Active.Clear();
        progress.Completed.Add("WC-MAIN-C01-010");
        Assert.True(check.IsMet(progress));
    }

    [Fact]
    public void QuestCompleteEntryAndLevelChecks() {
        var progress = new Progress { Level = 19 };
        progress.Active.Add("Q");
        progress.Entries.Add("QT-DS-MAIN-C01-001");

        Assert.False(new UnlockCheck(UnlockCheckKind.QuestComplete, "Q", 0).IsMet(progress));
        Assert.True(new UnlockCheck(UnlockCheckKind.Entry, "QT-DS-MAIN-C01-001", 0).IsMet(progress));
        Assert.False(new UnlockCheck(UnlockCheckKind.Level, null, 20).IsMet(progress));
        progress.Level = 20;
        Assert.True(new UnlockCheck(UnlockCheckKind.Level, null, 20).IsMet(progress));
        Assert.Equal("level 20", new UnlockCheck(UnlockCheckKind.Level, null, 20).Describe());
        Assert.Equal("quest complete Q", new UnlockCheck(UnlockCheckKind.QuestComplete, "Q", 0).Describe());
    }

    [Fact]
    public void ProfileRuleLocksTheWorldUntilAnyCheckPasses() {
        var rules = RulesWith("""
            world_unlocks:
              grizzleheim:
                any_of:
                  - level: 20
                  - quest: WC-GRZ-C01-001
                source: "test"
            """);
        var progress = new Progress { Level = 5 };

        var locked = rules.IsWorldUnlocked("Grizzleheim", progress);
        Assert.False(locked.Unlocked);
        Assert.Equal("grizzleheim", locked.WorldId);
        Assert.Contains("level 20 or quest WC-GRZ-C01-001", locked.Reason);

        progress.Active.Add("WC-GRZ-C01-001");
        Assert.True(rules.IsWorldUnlocked("Grizzleheim", progress).Unlocked);
        progress.Active.Clear();
        progress.Level = 20;
        Assert.True(rules.IsWorldUnlocked("Grizzleheim", progress).Unlocked);
    }

    [Fact]
    public void WorldWithoutARuleAndUnknownKeysAreUnlocked() {
        var rules = RulesWith("""
            world_unlocks:
              krokotopia:
                any_of: [{ quest_complete: WC-MAIN-C01-010 }]
            """);

        Assert.True(rules.IsWorldUnlocked("WizardCity", new Progress()).Unlocked);
        Assert.Null(rules.IsWorldUnlocked("WizardCity", new Progress()).Rule);
        Assert.True(rules.IsWorldUnlocked("NoSuchKey", new Progress()).Unlocked);
        Assert.False(rules.IsWorldUnlocked("Krokotopia", new Progress()).Unlocked);
    }

    [Fact]
    public void StockRulesUnlockEverything() {
        Assert.True(ClassicRules.Stock.IsWorldUnlocked("Krokotopia", new Progress()).Unlocked);
    }

    [Theory]
    [InlineData("world_unlocks:\n  atlantis:\n    any_of: [{ level: 5 }]", "unknown world id 'atlantis'")]
    [InlineData("world_unlocks:\n  krokotopia:\n    any_of: []", "any_of needs at least one check")]
    [InlineData("world_unlocks:\n  krokotopia:\n    any_of: [{ level: 5, quest: X }]", "exactly one of")]
    [InlineData("world_unlocks:\n  krokotopia:\n    any_of: [{ spell: X }]", "exactly one of")]
    [InlineData("world_unlocks:\n  krokotopia:\n    source: x", "any_of")]
    [InlineData("world_unlocks:\n  krokotopia:\n    any_of: [{ level: 0 }]", "world_unlocks.krokotopia.any_of[0].level")]
    public void BadWorldUnlocksAreRefused(string yaml, string expected) {
        using var data = new TempClassicData();
        data.WriteProfile("p", "id: p\ntitle: P\nstatus: canonical\ncutoff: 2010-05-25\n" + yaml);

        var ex = Assert.Throws<ClassicDataException>(() => ClassicProfileLoader.Load(data.ProfilesPath, "p"));
        Assert.Contains(ex.Errors, error => error.ToString().Contains(expected));
    }

    [Fact]
    public void ChildProfileReplacesOneWorldsRuleAndKeepsTheOthers() {
        using var data = new TempClassicData();
        data.WriteProfile("base", """
            id: base
            title: Base
            status: canonical
            cutoff: 2010-05-25
            world_unlocks:
              krokotopia: { any_of: [{ quest: A }] }
              grizzleheim: { any_of: [{ level: 20 }] }
            """);
        data.WriteProfile("child", """
            id: child
            title: Child
            status: optional
            extends: base
            cutoff: 2009-06-30
            world_unlocks:
              grizzleheim: { any_of: [{ level: 25 }] }
            """);

        var profile = ClassicProfileLoader.Load(data.ProfilesPath, "child");

        Assert.Equal("quest A", profile.WorldUnlocks["krokotopia"].Describe());
        Assert.Equal("level 25", profile.WorldUnlocks["grizzleheim"].Describe());
    }

    [Fact]
    public void Late2009UnlocksEachWorldWithIts2009Quest() {
        var rules = ClassicDataFixture.RealRules("late-2009");
        var unlocks = rules.Profile.WorldUnlocks;

        Assert.Equal(new[] { "dragonspyre", "grizzleheim", "krokotopia", "marleybone", "mooshu" }, unlocks.Keys.Order().ToArray());
        Assert.Equal("quest WC-MAIN-C01-010", unlocks["krokotopia"].Describe());
        Assert.Equal("quest KT-MAIN-C01-004", unlocks["marleybone"].Describe());
        Assert.Equal("quest WC-MAIN-C01-011", unlocks["mooshu"].Describe());
        Assert.Equal("quest WC-MAIN-C01-012", unlocks["dragonspyre"].Describe());
        Assert.Equal("level 20 or quest WC-GRZ-C01-001", unlocks["grizzleheim"].Describe());
        Assert.All(unlocks.Values, rule => Assert.Contains("oldid", rule.Source));

        var fresh = new Progress();
        Assert.True(rules.IsWorldUnlocked("WizardCity", fresh).Unlocked);
        foreach (var key in new[] { "Krokotopia", "Marleybone", "MooShu", "DragonSpire", "Grizzleheim" }) {
            Assert.False(rules.IsWorldUnlocked(key, fresh).Unlocked);
        }

        var veteran = new Progress { Level = 20 };
        veteran.Completed.UnionWith(["WC-MAIN-C01-010", "KT-MAIN-C01-004", "WC-MAIN-C01-011", "WC-MAIN-C01-012"]);
        foreach (var key in new[] { "Krokotopia", "Marleybone", "MooShu", "DragonSpire", "Grizzleheim" }) {
            Assert.True(rules.IsWorldUnlocked(key, veteran).Unlocked);
        }
    }

    [Fact]
    public void Late2009OpensTheGrizzleheimPreview() {
        var rules = ClassicDataFixture.RealRules("late-2009");
        var decision = rules.IsZoneAllowed("GrizzleheimLite/GH_GrizzleheimHubLite");

        Assert.True(decision.Allowed);
        Assert.Equal("grizzleheim", decision.WorldId);
        Assert.False(ClassicDataFixture.RealRules("arc1-2009h1").IsZoneAllowed("GrizzleheimLite/GH_GrizzleheimHubLite").Allowed);
    }

    [Fact]
    public void DevUnrestrictedHasNoWorldUnlocks() {
        Assert.Empty(ClassicDataFixture.LoadProfile("dev-unrestricted").WorldUnlocks);
    }

    [Fact]
    public void MergeReplacesByNameAppendsNewNamesAndRemoves() {
        List<Entry> loaded = [new("A", "a1"), new("B", "b1"), new("C", "c1")];
        List<Entry> merge = [new("B", "b2"), new("D", "d1"), new("C", null), new("", "x"), new("Z", null)];

        var result = ZoneTransferMerge.Apply(loaded, merge, entry => entry.Name, entry => entry.Value is null);

        Assert.Equal(new[] { "A:a1", "B:b2", "D:d1" }, result.Select(entry => $"{entry.Name}:{entry.Value}").ToArray());
        Assert.Equal(3, loaded.Count);
    }

    [Fact]
    public void MergeIntoNothingAndNamesAreExact() {
        var result = ZoneTransferMerge.Apply<Entry>(null, [new("Trigger 0", "x"), new("trigger 0", "y")],
            entry => entry.Name, entry => entry.Value is null);

        Assert.Equal(2, result.Count);
        Assert.Empty(ZoneTransferMerge.Apply<Entry>(null, null, entry => entry.Name, entry => false));
    }

}
