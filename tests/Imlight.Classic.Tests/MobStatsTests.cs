using System.IO;
using Imlight.Classic.Rules;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class MobStatsTests {

    private static MobStats Real() => MobStatsLoader.Load(Path.Combine(ClassicDataFixture.Root, "progression", "mob-stats-2009.yaml"));

    [Fact]
    public void TheCanonicalProfileNamesTheMobStats() {
        Assert.Equal("progression/mob-stats-2009.yaml", ClassicDataFixture.LoadProfile("late-2009").Rules.MobStats);
        Assert.Equal("progression/mob-stats-2009.yaml", ClassicDataFixture.LoadProfile("arc1-2009h1").Rules.MobStats);
        Assert.Null(ClassicDataFixture.LoadProfile("dev-unrestricted").Rules.MobStats);
    }

    [Fact]
    public void MalistairesLairFightHasIts2009HealthAndStandInsKeepTheirs() {
        var stats = Real();
        Assert.Equal(10000, stats.HealthOf(126504));   // Malistaire-Boss-R10 (wiki Malistaire, oldid 69187, 2010-05-20)
        Assert.Null(stats.HealthOf(126893));            // DS_Malistaire, a 500-health lair stand-in
        Assert.True(stats.HealthByTemplate.Count > 300);
    }

    [Fact]
    public void TheTutorialDraconiansHaveTheir2009Schools() {
        // Dated tutorial playthroughs (YouTube lmGC4RYO1oc 2008-12-23, SWP2K-hSWR4 2009-02-24, UJoxKUf8dmI 2009-09-06) show
        // the two Draconians with a Storm and a Fire plate at 480 health; the r806919 templates say Ice for both.
        var stats = Real();
        Assert.Equal("Storm", stats.SchoolOf(35528));   // TutorialGolem
        Assert.Equal("Fire", stats.SchoolOf(126461));   // TutorialGolemMII
        Assert.Equal(480, stats.HealthOf(35528));
        Assert.Equal(480, stats.HealthOf(126461));
        Assert.Null(stats.SchoolOf(126504));            // Malistaire: health only, the template's school stays
    }

    [Fact]
    public void ReviewedCreaturesHaveTheirDatedHealthAndOctoberOnlyValuesStayOctober() {
        var path = Path.Combine(ClassicDataFixture.Root, "progression", "mob-stats-2009.yaml");
        var late = MobStatsLoader.Load(path, "late-2009");
        var october = MobStatsLoader.Load(path, "october-2010-arc1");
        Assert.Equal(4500, late.HealthOf(126737));      // Andor Bristleback, pre-July Dragonspyre
        Assert.Equal(3200, late.HealthOf(126487));      // Flamebringer (oldid 17558; the 7,000 was a bad edit)
        Assert.Equal(1265, october.HealthOf(191088));   // Fire Shrieker, Briskbreeze Tower
        Assert.Null(late.HealthOf(82143));              // Ngozi: the cutoff page's 3,600 is not used
        Assert.Equal(3400, october.HealthOf(82143));    // corrected 2010-06-18
        Assert.Equal(4100, october.HealthOf(173169));   // Rogue Fire Elemental, 2010-09-10
        Assert.Null(Real().HealthOf(173169));           // no profile: profile-only entries are skipped
        Assert.Null(october.HealthOf(126502));          // Evil Trickster keeps its template (owner check)
    }

    [Fact]
    public void AnUnknownSchoolIsRejected() {
        var dir = Directory.CreateTempSubdirectory("w101c-mobstats-");
        try {
            var path = Path.Combine(dir.FullName, "mob-stats-test.yaml");
            File.WriteAllText(path, """
                id: mob-stats-test
                profiles: [late-2009]
                license_tag: own
                provenance:
                - {source: test, source_date: '2010-05-20', retrieved: '2026-09-28', covers: [health], confidence: corroborated}
                mobs:
                - {name: A, templates: [1], health: 100, school: Shadow, source: test, source_date: '2010-01-01'}
                """);

            var error = Assert.Throws<ClassicDataException>(() => MobStatsLoader.Load(path));
            Assert.Contains("school 'Shadow'", error.Message);
        }
        finally {
            dir.Delete(true);
        }
    }

    [Fact]
    public void ATemplateListedTwiceIsRejected() {
        var dir = Directory.CreateTempSubdirectory("w101c-mobstats-");
        try {
            var path = Path.Combine(dir.FullName, "mob-stats-test.yaml");
            File.WriteAllText(path, """
                id: mob-stats-test
                profiles: [late-2009]
                license_tag: own
                provenance:
                - {source: test, source_date: '2010-05-20', retrieved: '2026-09-28', covers: [health], confidence: corroborated}
                mobs:
                - {name: A, templates: [1, 2], health: 100, source: test, source_date: '2010-01-01'}
                - {name: B, templates: [2], health: 50, source: test, source_date: '2010-01-01'}
                """);

            var error = Assert.Throws<ClassicDataException>(() => MobStatsLoader.Load(path));
            Assert.Contains("listed twice", error.Message);
        }
        finally {
            dir.Delete(true);
        }
    }

}
