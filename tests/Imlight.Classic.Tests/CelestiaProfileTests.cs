// CLASSIC: celestia-2010 (Celestia / level 60, work package A): the profile, its inheritance of october-2010-arc1's
// data and mechanics, the level 51-60 tables, cap-driven limits, the per-profile tombstone, and a golden check that
// october-2010-arc1 and the older profiles are unchanged.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imlight.Classic.Pvp;
using Imlight.Classic.Rules;
using Imlight.Classic.Spells;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Classic.Arena;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.WizardData;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic.Elixirs;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class CelestiaProfileTests {

    private const string Celestia = ClassicProfiles.Celestia2010;
    private const string October = ClassicProfiles.October2010;

    private static ClassicSpellBook Book() => ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "spells"));

    private static void Use(string profile) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(profile));
    }

    // ---------------------------------------------------------------- the profile

    [Fact]
    public void CelestiaExtendsOctoberWithLevelSixtyAndCelestiaButIsNotCanonical() {
        var profile = ClassicDataFixture.LoadProfile(Celestia);

        Assert.Equal(ProfileStatus.Optional, profile.Status);
        Assert.Equal(October, profile.Extends);
        Assert.Equal(new[] { Celestia, October, "late-2009" }, profile.Lineage);
        Assert.True(profile.InheritsMembership);
        Assert.Equal(new[] { Celestia, October }, profile.MembershipIds);
        Assert.Equal(new DateOnly(2010, 10, 31), profile.Cutoff);
        Assert.Equal(new DateOnly(2010, 10, 27), profile.ZoneContentCutoff);
        Assert.Equal(60, profile.LevelCap);
        Assert.Equal(new[] { "wizard_city", "krokotopia", "marleybone", "mooshu", "dragonspyre", "grizzleheim", "celestia" },
            profile.Worlds!.Value);
        Assert.Equal("progression/xp-2010.yaml", profile.Rules.XpTable);
        Assert.Equal("progression/health-2010.yaml", profile.Rules.PlayerHealth);
        Assert.Equal("progression/level-stats-2010.yaml", profile.Rules.LevelStats);
        // Inherited from october-2010-arc1 unchanged.
        var october = ClassicDataFixture.LoadProfile(October);
        Assert.Equal(october.Rules.PuzzleHelpers, profile.Rules.PuzzleHelpers);
        Assert.Equal(october.Rules.Potions, profile.Rules.Potions);
        // Work package E: the 2009 mob rewards and badges plus Celestia (CelestiaLootTests).
        Assert.Equal("progression/mob-rewards-2010.yaml", profile.Rules.MobRewards);
        Assert.Equal("badges/badges-2010.yaml", profile.Rules.Badges);
        Assert.Equal(october.DisabledQuests, profile.DisabledQuests);
        // Work package B: critical and block on, with the 2014 client's percent model (ruling D3); every other switch inherited.
        Assert.Equal(october.Features.Explicit.Where(p => p.Key != ClassicFeatures.CriticalAndBlock).OrderBy(p => p.Key),
            profile.Features.Explicit.Where(p => p.Key != ClassicFeatures.CriticalAndBlock).OrderBy(p => p.Key));
        Assert.True(profile.Features.IsEnabled(ClassicFeatures.CriticalAndBlock));
        Assert.False(october.Features.IsEnabled(ClassicFeatures.CriticalAndBlock));
        Assert.Equal("rules/critical-2010.yaml", profile.Rules.Critical);
        Assert.Null(october.Rules.Critical);
        Assert.False(profile.Features.IsEnabled(ClassicFeatures.Crafting));
        // Work package D: Door to the Stars and the Celestia story are restored here only, and the Spiral Door waits on it.
        Assert.Equal("WC-MAIN-C01-013", profile.RestoredQuests[0]);
        Assert.Contains("CL-MAIN-C01-009", profile.RestoredQuests);
        Assert.Equal("WC-MAIN-C01-013", Assert.Single(profile.WorldUnlocks["celestia"].AnyOf).Name);
    }

    [Fact]
    public void TheLiveDefaultAndCanonicalProfilesAreUnchanged() {
        var profiles = ClassicProfileLoader.LoadAll(ClassicDataFixture.ProfilesPath);
        Assert.Equal("late-2009", Assert.Single(profiles, p => p.Status == ProfileStatus.Canonical).Id);
        Assert.Contains(profiles, p => p.Id == Celestia);
    }

    [Fact]
    public void CelestiaRulesOpenCelestiaButLockItsSpiralDoorUntilDoorToTheStars() {
        var celestia = ClassicDataFixture.RealRules(Celestia);
        var october = ClassicDataFixture.RealRules(October);

        Assert.True(celestia.IsHubKeyAllowed("Celestia"));
        Assert.False(october.IsHubKeyAllowed("Celestia"));
        Assert.False(october.IsZoneAllowed("Celestia/CL_Hub").Allowed);
        Assert.Equal(60, celestia.EffectiveMaxLevel(180));
        Assert.Equal(50, october.EffectiveMaxLevel(180));
    }

    // ---------------------------------------------------------------- golden: older profiles unchanged

    [Theory]
    [InlineData("late-2009", "late-2009 (canonical): cutoff 2010-05-25, level cap 50, worlds wizard_city, krokotopia, marleybone, mooshu, dragonspyre, grizzleheim; features off: pets.leveling, pets.talents, pets.hatching, pets.energy, critical_and_block, armor_piercing, shadow_magic, archmastery, gardening, fishing, jewels, cantrips, monstrology, team_up, untargeted_area_spells")]
    [InlineData(October, "october-2010-arc1 (optional): cutoff 2010-10-31, zone content cutoff 2010-05-25, level cap 50, worlds wizard_city, krokotopia, marleybone, mooshu, dragonspyre, grizzleheim; features off: crafting, pets.leveling, pets.talents, pets.hatching, pets.energy, critical_and_block, armor_piercing, shadow_magic, archmastery, gardening, fishing, jewels, cantrips, monstrology, team_up")]
    public void OlderProfilesKeepExactMembershipAndTheirSummary(string id, string summary) {
        var profile = ClassicDataFixture.LoadProfile(id);

        Assert.False(profile.InheritsMembership);
        Assert.Equal(new[] { id }, profile.MembershipIds);
        Assert.Empty(profile.RestoredQuests);
        Assert.Null(profile.Rules.LevelStats);
        Assert.Equal("progression/xp-2009.yaml", profile.Rules.XpTable);
        Assert.Equal("progression/health-2009.yaml", profile.Rules.PlayerHealth);
        Assert.Equal(summary, profile.Describe());
    }

    [Fact]
    public void MembershipInheritsOnlyThroughTheDeclaringProfile() {
        _ = ClassicDataFixture.LoadProfile(Celestia);
        _ = ClassicDataFixture.LoadProfile(October);

        Assert.True(ClassicProfiles.Lists([October], Celestia));
        Assert.True(ClassicProfiles.Lists([Celestia], Celestia));
        Assert.False(ClassicProfiles.Lists(["late-2009"], Celestia));       // no transitive reach past october
        Assert.False(ClassicProfiles.Lists(["late-2009"], October));        // october unchanged: exact ids
        Assert.False(ClassicProfiles.Lists([Celestia], October));           // a parent never inherits from a child
        Assert.False(ClassicProfiles.Lists([October], "late-2009"));
        Assert.False(ClassicProfiles.Lists(null, Celestia));
        Assert.True(ClassicProfiles.IsOctoberFamily(Celestia));
        Assert.True(ClassicProfiles.IsOctoberFamily(October));
        Assert.False(ClassicProfiles.IsOctoberFamily("late-2009"));
        Assert.False(ClassicProfiles.IsOctoberFamily("arc1-2009h1"));
        Assert.False(ClassicProfiles.IsOctoberFamily("dev-unrestricted"));
        Assert.False(ClassicProfiles.IsOctoberFamily((string?) null));
        Assert.Equal(60, ClassicProfiles.MaxLevelOf(Celestia));
        Assert.Equal(50, ClassicProfiles.MaxLevelOf(October));
        Assert.Equal(50, ClassicProfiles.MaxLevelOf("never-loaded-profile"));
    }

    [Fact]
    public void EverySpellRecordMeansTheSameInCelestiaAsInOctober() {
        _ = ClassicDataFixture.LoadProfile(Celestia);
        var book = Book();
        var celestia = new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile(Celestia));
        var october = new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile(October));

        Assert.NotEmpty(book.Records);
        // CLASSIC: the Astral records (work package C) are Celestia's own; every inherited record means the same.
        foreach (var record in book.Records.Where(record => !ClassicSpellSchema.AstralSchools.Contains(record.School))) {
            Assert.Equal(record.IsInProfile(October), record.IsInProfile(Celestia));
            Assert.Equal(october.ValuesOf(record), celestia.ValuesOf(record));
            if (record.ClientTemplate is { } path) {
                Assert.Equal(october.IsTrainable(path), celestia.IsTrainable(path));
            }
        }

        Assert.Equal(october.RecordsNotInProfile.Where(r => !ClassicSpellSchema.AstralSchools.Contains(r.School)).Select(r => r.Id),
            celestia.RecordsNotInProfile.Select(r => r.Id));
    }

    [Fact]
    public void OctoberMechanicsApplyToCelestiaAndStayOffForOlderProfiles() {
        try {
            Use(Celestia);
            Assert.True(ClassicOctoberRules.Active);
            Assert.Equal(4, ClassicOctoberRules.AutomaticStunBlocks(pvp: true));
            Assert.True(ClassicProgression.PuzzleHelpers.AppliesTo(Celestia) || !ClassicProgression.PuzzleHelpers.Profiles.Any());
            Assert.NotNull(ElixirRules.Approved(ClassicRuntime.Rules, 191099));
            Use(October);
            Assert.True(ClassicOctoberRules.Active);
            Assert.NotNull(ElixirRules.Approved(ClassicRuntime.Rules, 191099));
            Use("late-2009");
            Assert.False(ClassicOctoberRules.Active);
            Assert.Equal(1, ClassicOctoberRules.AutomaticStunBlocks(pvp: true));
            Assert.Null(ElixirRules.Approved(ClassicRuntime.Rules, 191099));
        }
        finally {
            ClassicRuntime.ResetForTests();
        }
    }

    [Fact]
    public void ThePuzzleHelperPolicyLoadsForCelestiaThroughOctober() {
        var policy = PuzzleHelpersLoader.LoadForProfile(ClassicDataFixture.LoadProfile(Celestia), ClassicDataFixture.Root);
        Assert.Equal(PuzzleHelpers.PolicyId, policy.Id);
        Assert.True(policy.AppliesTo(Celestia));
        Assert.True(policy.AppliesTo(October));
        Assert.False(policy.AppliesTo("late-2009"));
    }

    // ---------------------------------------------------------------- level 51-60 tables

    [Fact]
    public void XpToSixtyKeepsTheTwoThousandNineCurveAndAddsTheLaterChartsLevels() {
        var old = XpTableLoader.Load(Path.Combine(ClassicDataFixture.Root, "progression", "xp-2009.yaml"));
        var table = XpTableLoader.Load(Path.Combine(ClassicDataFixture.Root, "progression", "xp-2010.yaml"));

        Assert.Equal(60, table.MaxLevel);
        Assert.Equal(new[] { Celestia }, table.Profiles);
        Assert.Equal(old.Levels, table.Levels.Take(50));
        Assert.Equal(44600, table.Levels[49].XpToNext);   // 50 -> 51
        Assert.Equal(681130, table.XpToReach(51));
        Assert.Equal(46290, table.Levels[50].XpToNext);   // 51 -> 52
        Assert.Equal(1205910, table.XpToReach(60));
        Assert.Equal(75370, table.Levels[59].XpToNext);   // the ceiling at the cap
        Assert.Equal(new[] { 46290, 48995, 51820, 54775, 57860, 61080, 64440, 67940, 71580, 75370 },
            table.Levels.Skip(50).Select(l => l.XpToNext));
        Assert.Equal(60, table.LevelAt(1205910));
        Assert.Equal(50, table.LevelAt(681129));
    }

    [Fact]
    public void HealthToSixtyKeepsTwoThousandNineAndAddsTheMeasuredLaterValues() {
        var old = HealthTableLoader.Load(Path.Combine(ClassicDataFixture.Root, "progression", "health-2009.yaml"));
        var table = HealthTableLoader.Load(Path.Combine(ClassicDataFixture.Root, "progression", "health-2010.yaml"));

        Assert.Equal(60, table.MaxLevel);
        foreach (var school in HealthTable.Schools) {
            for (var level = 1; level <= 50; level++) {
                Assert.Equal(old.HealthOf(school, level), table.HealthOf(school, level));
            }
        }

        // Cells players measured in dated 2011 revisions of the Level Chart (oldid 128064 and 149359).
        Assert.Equal(1522, table.HealthOf("Fire", 51));
        Assert.Equal(1677, table.HealthOf("Fire", 58));
        Assert.Equal(1827, table.HealthOf("Life", 51));
        Assert.Equal(1331, table.HealthOf("Storm", 58));
        Assert.Equal(1797, table.HealthOf("Death", 56));
        Assert.Equal(1846, table.HealthOf("Death", 58));
        Assert.Equal(2336, table.HealthOf("Ice", 60));
        Assert.Equal(2069, table.HealthOf("Balance", 60));
        Assert.Equal(1719, table.HealthOf("Myth", 60));
        Assert.Null(table.HealthOf("Fire", 61));
        Assert.Null(table.HealthOf("Moon", 55));
        foreach (var school in HealthTable.Schools) {
            for (var level = 51; level <= 60; level++) {
                Assert.True(table.HealthOf(school, level) > table.HealthOf(school, level - 1), $"{school} {level}");
            }
        }
    }

    [Fact]
    public void ManaPipsAndTrainingPointsForFiftyOneToSixtyAreClientValuesAcceptedByTheOwner() {
        var stats = LevelStatsLoader.Load(Path.Combine(ClassicDataFixture.Root, "progression", "level-stats-2010.yaml"));

        Assert.Equal("accepted", stats.Status); // owner D7, 2026-10-16: use the client values
        Assert.Contains("D7", stats.Decision);
        Assert.Equal((51, 60), (stats.MinLevel, stats.MaxLevel));
        Assert.Null(stats.Row(50));
        Assert.Null(stats.Row(61));
        Assert.All(Enumerable.Range(51, 10), level => {
            var row = stats.Row(level)!;
            Assert.Equal(level, row.Level);
            Assert.Equal(120, row.Mana);
            Assert.Equal(0.40, row.PowerPipChance, 6);
        });
        Assert.Equal(new[] { 55, 60 }, stats.Rows.Where(r => r.TrainingPoints > 0).Select(r => r.Level));
    }

    [Fact]
    public void LevelStatsRejectGapsAndUnknownStatuses() {
        using var data = new TempClassicData();
        var path = Path.Combine(data.Root, "level-stats-test.yaml");
        File.WriteAllText(path, """
            id: level-stats-test
            profiles: [p]
            status: maybe
            min_level: 51
            max_level: 52
            license_tag: own
            provenance: []
            levels:
            - {level: 51, mana: 120, power_pip_chance: 0.4, training_points: 0}
            - {level: 53, mana: 120, power_pip_chance: 1.4, training_points: 0}
            """);
        var ex = Assert.Throws<ClassicDataException>(() => LevelStatsLoader.Load(path));
        Assert.Contains(ex.Errors, e => e.KeyPath == "status");
        Assert.Contains(ex.Errors, e => e.KeyPath == "levels[1]");
        Assert.Contains(ex.Errors, e => e.KeyPath == "levels[1].power_pip_chance");
    }

    // ---------------------------------------------------------------- cap-driven limits

    [Fact]
    public void ArenaRangeFollowsTheCapAndLevelsOneToFiftyKeepTheirIds() {
        try {
            Use(October);
            Assert.Equal(50, ArenaLevels.Cap);
            Assert.Equal(4200, ArenaFriendlyRoster.ChallengeCountPerKind);
            var octoberIds = ArenaFriendlyRoster.Challenges(ArenaKind.Ranked, 20).Select(c => c.Id).ToArray();
            Assert.Equal(4200, octoberIds.Length);
            var high = new ArenaFriendlyChallenge(ArenaKind.Practice, 55, 3, ArenaPvpSkill.Advanced, 2);
            Assert.Null(ArenaFriendlyRoster.Read(high.Id));        // not a challenge of a level 50 server
            Assert.Equal(50, ArenaLevels.Clamp(55));

            Use(Celestia);
            Assert.Equal(60, ArenaLevels.Cap);
            Assert.Equal(5040, ArenaFriendlyRoster.ChallengeCountPerKind);
            var celestia = ArenaFriendlyRoster.Challenges(ArenaKind.Ranked, 20).ToArray();
            Assert.Equal(5040, celestia.Length);
            Assert.Equal(celestia.Length, celestia.Select(c => c.Id).Distinct().Count());
            Assert.Subset(celestia.Select(c => c.Id).ToHashSet(), octoberIds.ToHashSet());
            Assert.Equal(high, ArenaFriendlyRoster.Read(high.Id));
            Assert.Equal(high, ArenaFriendlyRoster.Read(high.TeamIds[1]));
            Assert.All(Enum.GetValues<ArenaKind>().SelectMany(kind => ArenaFriendlyRoster.Challenges(kind, 60)),
                c => Assert.Equal(c, ArenaFriendlyRoster.Read(c.Id)));
            Assert.Equal(55, ArenaLevels.Clamp(55));
            Assert.Equal(60, ArenaLevels.Clamp(61));
        }
        finally {
            ClassicRuntime.ResetForTests();
        }
    }

    [Fact]
    public void ArenaIdsOfTheOriginalLevelsAreTheOriginalFormula() {
        // The 2009 layout, written out: saved Ranked standings and listings use these ids.
        static ulong Original(ArenaKind kind, int level, int school, ArenaPvpSkill skill, int size)
            => ArenaFriendlyRoster.FirstId + (ulong) (((((int) kind * 50 + level - 1) * 7 + school) * 3 + (int) skill) * 4 + size - 1) * 4;
        foreach (var kind in Enum.GetValues<ArenaKind>())
        foreach (var level in new[] { 1, 19, 50 })
        foreach (var skill in Enum.GetValues<ArenaPvpSkill>()) {
            Assert.Equal(Original(kind, level, 6, skill, 4), new ArenaFriendlyChallenge(kind, level, 6, skill, 4).Id);
        }

        Assert.True(new ArenaFriendlyChallenge(ArenaKind.Practice, 51, 0, ArenaPvpSkill.Beginner, 1).Id
            >= Original(ArenaKind.Ranked, 50, 6, ArenaPvpSkill.Advanced, 4) + 4);
    }

    [Fact]
    public void AutonomousArenaLevelsVisitEveryLevelOfTheRange() {
        Assert.Equal(11, ArenaLevels.Stride(50));
        Assert.Equal(11, ArenaLevels.Stride(60));
        foreach (var count in new[] { 1, 50, 55, 60, 66 }) {
            var stride = ArenaLevels.Stride(count);
            Assert.Equal(count, Enumerable.Range(0, count).Select(round => 1 + round * stride % count).Distinct().Count());
        }
    }

    [Fact]
    public void PvpDeckPlannerAcceptsLevelsUpToTheProfilesCap() {
        _ = ClassicDataFixture.LoadProfile(Celestia);
        _ = ClassicDataFixture.LoadProfile(October);
        var record = new ClassicSpellRecord {
            Id = "spell.fire.fixture", Name = "Fixture", School = "Fire", Kind = "trained", ClientTemplate = "Spells/Fixture.xml",
            Profiles = [October], SourceFile = "fixture",
            Values = new(SpellPips.Of(1), 1, 1, 0, "fixture",
                [new(SpellEffectKind.Damage, "Fire", 100, 100, null, null, SpellTargets.Single, null)]),
        };
        var spells = new[] { new ArenaDeckSpell(record, 1, 1, 0) };
        var limits = new ArenaDeckLimits("Fire", 14, 3, 2, 8, 8);

        Assert.NotEmpty(ArenaPvpDeckPlanner.Plan(spells, "Fire", 50, October, [October], limits));
        Assert.Empty(ArenaPvpDeckPlanner.Plan(spells, "Fire", 55, October, [October], limits));
        Assert.NotEmpty(ArenaPvpDeckPlanner.Plan(spells, "Fire", 55, Celestia, [Celestia, October], limits));
        Assert.Empty(ArenaPvpDeckPlanner.Plan(spells, "Fire", 61, Celestia, [Celestia, October], limits));
    }

    [Fact]
    public void HouseMinimumLevelsFollowTheProfilesCap() {
        _ = ClassicDataFixture.LoadProfile(Celestia);
        _ = ClassicDataFixture.LoadProfile(October);
        var source = File.ReadAllText(Path.Combine(ClassicDataFixture.Root, "housing", "houses-october-2010.yaml"));
        using var data = new TempClassicData();
        var path = Path.Combine(data.Root, "houses-october-2010.yaml");
        var levels = System.Text.RegularExpressions.Regex.Matches(source, @"minimum_level: \d+");
        Assert.NotEmpty(levels);
        File.WriteAllText(path, System.Text.RegularExpressions.Regex.Replace(source, @"minimum_level: \d+", "minimum_level: 55"));

        Assert.Throws<InvalidDataException>(() => HouseCatalog.Load(path, October));
        Assert.NotEmpty(HouseCatalog.Load(path, Celestia));
        // The real catalog means the same in both profiles.
        var real = Path.Combine(ClassicDataFixture.Root, "housing", "houses-october-2010.yaml");
        Assert.Equal(HouseCatalog.Load(real, October).Keys.Order(), HouseCatalog.Load(real, Celestia).Keys.Order());
    }

    [Fact]
    public void CrownShopMinimumLevelsFollowTheListedProfilesCap() {
        _ = ClassicDataFixture.LoadProfile(Celestia);
        _ = ClassicDataFixture.LoadProfile(October);
        var real = Path.Combine(ClassicDataFixture.Root, "rules", "crown-shop-2009.yaml");
        var source = File.ReadAllText(real);
        const string Listed = "profiles: [late-2009, arc1-2009h1, october-2010-arc1, wintertusk-2011]"; // wintertusk: its gardening seeds
        Assert.Contains(Listed, source);
        Assert.Contains("min_level: 20,", source);
        using var data = new TempClassicData();
        string Write(string text) {
            var path = Path.Combine(data.Root, "crown-shop-2009.yaml");
            File.WriteAllText(path, text);
            return path;
        }

        var high = source.Replace("min_level: 20,", "min_level: 55,");
        // Without a level-60 profile in the list, 55 is above the cap...
        Assert.Throws<ClassicDataException>(() => CrownShopCatalogLoader.Load(Write(high
            .Replace(Listed, "profiles: [late-2009, arc1-2009h1, october-2010-arc1]")
            .Replace("profiles: [wintertusk-2011], ", ""))));
        // ...with one (wintertusk-2011 for its gardening seeds, or celestia-2010) it is allowed; 65 never is.
        Assert.NotEmpty(CrownShopCatalogLoader.Load(Write(high)).Items);
        Assert.Throws<ClassicDataException>(() => CrownShopCatalogLoader.Load(Write(source.Replace("min_level: 20,", "min_level: 65,"))));
        // The real catalog offers celestia-2010 what it offers october-2010-arc1.
        var catalog = CrownShopCatalogLoader.Load(real);
        Assert.Equal(catalog.Offered(_ => true, October).Keys.Order(), catalog.Offered(_ => true, Celestia).Keys.Order());
    }

    // ---------------------------------------------------------------- per-profile tombstone

    [Fact]
    public void DoorToTheStarsIsTombstonedGloballyAndARestoringProfileKeepsIt() {
        var lines = File.ReadAllLines(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates", "_disabled.txt"));
        QuestTemplate Quest(string name) => new() { m_questName = name };

        var quests = new ConcurrentDictionary<string, QuestTemplate>(StringComparer.OrdinalIgnoreCase) { ["WC-MAIN-C01-013"] = Quest("WC-MAIN-C01-013") };
        var older = SpiralDB.ApplyTombstoneLines(lines, quests, SpiralDB.RestoredQuests(ClassicDataFixture.LoadProfile(October)));
        Assert.Contains("WC-MAIN-C01-013", older.Removed);
        Assert.Empty(older.Restored);
        Assert.False(quests.ContainsKey("WC-MAIN-C01-013"));

        using var data = new TempClassicData();
        data.WriteProfile("base", """
            id: base
            title: Base
            status: canonical
            cutoff: 2010-05-25
            """);
        data.WriteProfile("child", """
            id: child
            title: Child
            status: optional
            extends: base
            cutoff: 2010-10-31
            restored_quests: [WC-MAIN-C01-013]
            """);
        data.WriteProfile("grandchild", """
            id: grandchild
            title: Grandchild
            status: optional
            extends: child
            cutoff: 2010-10-31
            """);
        quests["WC-MAIN-C01-013"] = Quest("WC-MAIN-C01-013");
        var child = ClassicProfileLoader.Load(data.ProfilesPath, "child");
        var kept = SpiralDB.ApplyTombstoneLines(lines, quests, SpiralDB.RestoredQuests(child));
        Assert.Equal(new[] { "WC-MAIN-C01-013" }, kept.Restored);
        Assert.True(quests.ContainsKey("WC-MAIN-C01-013"));
        Assert.Equal(child.RestoredQuests, ClassicProfileLoader.Load(data.ProfilesPath, "grandchild").RestoredQuests);
        Assert.Empty(ClassicProfileLoader.Load(data.ProfilesPath, "base").RestoredQuests);
        Assert.Empty(SpiralDB.RestoredQuests(null));
    }

    [Fact]
    public void InheritsMembershipNeedsAParentAndIsNotInherited() {
        using var data = new TempClassicData();
        data.WriteProfile("base", """
            id: base
            title: Base
            status: canonical
            cutoff: 2010-05-25
            """);
        data.WriteProfile("child", """
            id: child
            title: Child
            status: optional
            extends: base
            cutoff: 2010-10-31
            inherits_membership: true
            """);
        data.WriteProfile("grandchild", """
            id: grandchild
            title: Grandchild
            status: optional
            extends: child
            cutoff: 2010-10-31
            """);
        data.WriteProfile("leaf", """
            id: leaf
            title: Leaf
            status: optional
            extends: child
            cutoff: 2010-10-31
            inherits_membership: true
            """);
        data.WriteProfile("self", """
            id: self
            title: Self
            status: optional
            cutoff: 2010-05-25
            inherits_membership: true
            """);

        Assert.Equal(new[] { "child", "base" }, ClassicProfileLoader.Load(data.ProfilesPath, "child").MembershipIds);
        Assert.Equal(new[] { "grandchild" }, ClassicProfileLoader.Load(data.ProfilesPath, "grandchild").MembershipIds);
        Assert.Equal(new[] { "leaf", "child", "base" }, ClassicProfileLoader.Load(data.ProfilesPath, "leaf").MembershipIds);
        var ex = Assert.Throws<ClassicDataException>(() => ClassicProfileLoader.Load(data.ProfilesPath, "self"));
        Assert.Contains(ex.Errors, e => e.KeyPath == "inherits_membership");
    }

}
