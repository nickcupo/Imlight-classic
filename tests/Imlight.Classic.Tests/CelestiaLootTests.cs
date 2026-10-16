// CLASSIC: celestia-2010 (work package E): the Celestia drops and badges. celestia-2010 reads mob-rewards-2010 and
// badges-2010, which are the 2009 files plus Celestia blocks (tools/classic-quests/gen_celestia2010_loot.py); the
// October 2010 profile keeps the 2009 files and so serves none of the Celestia additions.
using System.IO;
using System.Linq;
using Imlight.Classic.Rules;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class CelestiaLootTests {

    private const ulong Astraeus = 283087;        // Trial of the Spheres, rank 11 boss
    private const ulong QueenCalypso = 267121;    // dated page 2011-02-11
    private const ulong Thunderfin = 267133;      // its page lists Sandstone (a December 2010 drop)
    private const ulong Sandstone = 258585;
    private const ulong WingFish = 267105;        // undocumented; client table ReagentCL-Drop-Fin

    private static MobRewardRules Rewards(string id)
        => MobRewardRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "progression", id + ".yaml"));

    private static BadgeRules Badges(string id)
        => BadgeRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "badges", id + ".yaml"));

    [Fact]
    public void CelestiaReadsTheCelestiaFilesAndOctoberKeepsThe2009Ones() {
        var celestia = ClassicDataFixture.LoadProfile(ClassicProfiles.Celestia2010);
        var october = ClassicDataFixture.LoadProfile(ClassicProfiles.October2010);

        Assert.Equal("progression/mob-rewards-2010.yaml", celestia.Rules.MobRewards);
        Assert.Equal("badges/badges-2010.yaml", celestia.Rules.Badges);
        Assert.Equal("progression/mob-rewards-2009.yaml", october.Rules.MobRewards);
        Assert.Equal("badges/badges-2009.yaml", october.Rules.Badges);
    }

    [Fact]
    public void The2010RewardsKeepEvery2009MobAndAddCelestia() {
        var old = Rewards("mob-rewards-2009");
        var cel = Rewards("mob-rewards-2010");

        foreach (var mob in old.Mobs) {
            foreach (var template in mob.Templates) {
                Assert.Equal(mob.Name, cel.Find(template)?.Name);
            }
        }

        Assert.Null(old.Find(QueenCalypso));
        Assert.NotNull(cel.Find(QueenCalypso));
        Assert.NotEmpty(cel.Find(QueenCalypso)!.Items);
        Assert.Equal(old.CombatXp.XpPerPip, cel.CombatXp.XpPerPip);
        Assert.Equal(old.GoldByRank[10], cel.GoldByRank[10]);
    }

    [Fact]
    public void RankElevenGoldStartsAtAstraeussDatedMinimum() {
        var cel = Rewards("mob-rewards-2010");
        var gold = cel.GoldFor(new MobInfo(Astraeus, 11, MobKind.Boss));

        Assert.Equal(152, gold.Min);
        Assert.True(cel.GoldByRank.ContainsKey(12));
    }

    [Fact]
    public void LaterDropsAreLeftOut() {
        var cel = Rewards("mob-rewards-2010");

        Assert.DoesNotContain(cel.Find(Thunderfin)!.Reagents, r => r.Template == Sandstone);
        Assert.True(cel.ReagentFallback.ContainsKey(WingFish));
        Assert.False(Rewards("mob-rewards-2009").ReagentFallback.ContainsKey(WingFish));
    }

    [Fact]
    public void The2010BadgesKeepEvery2009BadgeAndAddCelestia() {
        var old = Badges("badges-2009");
        var cel = Badges("badges-2010");

        Assert.Empty(old.Badges.Where(b => b.World == "celestia"));
        Assert.All(old.Badges, b => Assert.Contains(cel.Badges, c => c.Id == b.Id));
        var explorer = Assert.Single(cel.Badges, b => b.Id == "celestian-explorer");
        Assert.Equal("celestia", explorer.World);
        Assert.Equal(["CL-Explorer-C01-001"], Assert.IsType<QuestBadgeAward>(explorer.Award).AnyOf);
        var crabs = Assert.Single(cel.Badges, b => b.Id == "crab-angler");
        Assert.Equal(new KillBadgeAward("Crab", 200), crabs.Award);
        Assert.False(Assert.Single(cel.Badges, b => b.Id == "master-angler").IsGranted);
        var geographer = Assert.IsType<AllQuestsBadgeAward>(Assert.Single(cel.Badges, b => b.Id == "spiral-geographer").Award);
        Assert.Contains("CL-MAIN-C02-001", geographer.Quests);
        Assert.Contains("CL-MAIN-C01-009", geographer.Quests);
    }

}
