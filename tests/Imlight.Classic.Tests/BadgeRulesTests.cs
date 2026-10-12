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
 * CLASSIC BADGES TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The badge and treasure-card quest reward files load, their award rules
 * read as intended, and ClassicBadges awards each badge once and only when
 * earned.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter BadgeRulesTests
 *
 * NOTE:
 * The real-data cases read the monorepo's classic-data (badges-2009.yaml,
 * cards-2009.yaml) and skip outside it.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(BadgeRulesTests))] // ClassicBadges' test hooks are static.
public sealed class BadgeRulesTests {

    private static BadgeRules RealBadges() => BadgeRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, "badges", "badges-2009.yaml"));

    private static QuestCardRewards RealCards() => QuestCardRewardsLoader.Load(Path.Combine(ClassicDataFixture.Root, "quests", "cards-2009.yaml"));

    [Fact]
    public void Late2009NamesTheBadgeAndCardFiles() {
        var profile = ClassicDataFixture.LoadProfile("late-2009");

        Assert.Equal("badges/badges-2009.yaml", profile.Rules.Badges);
        Assert.Equal("quests/cards-2009.yaml", profile.Rules.QuestCards);
        Assert.Equal(profile.Rules.Badges, ClassicDataFixture.LoadProfile("arc1-2009h1").Rules.Badges);
    }

    [Fact]
    public void TheRealBadgesLoad() {
        var rules = RealBadges();

        Assert.Equal("badges-2009", rules.Id);
        Assert.Equal(98, rules.Badges.Length);
        Assert.Equal(rules.Badges.Length, rules.Badges.Select(b => b.Id).Distinct().Count());
        Assert.Equal(93, rules.Granted.Count()); // CLASSIC: only the five crafting badges are not granted.
        Assert.All(rules.Badges.Where(b => !b.IsGranted), b => Assert.Contains("crafting", ((NotGrantedBadgeAward) b.Award).Reason));
        Assert.All(rules.Badges, badge => Assert.StartsWith("BadgeFilterNames_", badge.FilterKey));
    }

    [Theory]
    [InlineData("WC-ST01-C01-006", "hero-of-unicorn-way")]      // Rattlebones Report (Fandom oldid 56740)
    [InlineData("KT-PYMHub-C01-006", "junior-archeologist")]    // Permission to Enter (56826)
    [InlineData("MS-DTH3-C01-002", "spirit-walker")]            // Everlasting Forest (65028)
    [InlineData("DS-ACAD-C01-005", "savior-of-the-spiral")]     // The Final Countdown (The Great Spyre 65368)
    public void QuestsAwardTheirBadges(string quest, string badgeId) {
        var badge = Assert.Single(RealBadges().ForQuest(quest), b => b.Id == badgeId);

        Assert.True(BadgeRules.IsEarned(badge, new Progress { Completed = { quest } }));
        Assert.False(BadgeRules.IsEarned(badge, new Progress()));
    }

    [Fact]
    public void KillBadgesAreReachedInTiers() {
        var gobbler = RealBadges().ForAdjective("Gobbler").OrderBy(b => ((KillBadgeAward) b.Award).Count).ToList();

        Assert.Equal(["gobbler-chaser", "gobbler-catcher", "gobbler-gobbler"], gobbler.Select(b => b.Id));
        var progress = new Progress { Kills = { ["Gobbler"] = 50 } };
        Assert.Equal([true, true, false], gobbler.Select(b => BadgeRules.IsEarned(b, progress)));
    }

    [Fact]
    public void AnAreaBadgeNeedsEveryQuest() {
        var rules = RealBadges();
        var watch = rules.Find("member-of-the-watch")!;
        var quests = ((AllQuestsBadgeAward) watch.Award).Quests;

        var progress = new Progress();
        progress.Completed.UnionWith(quests.Skip(1));
        Assert.False(BadgeRules.IsEarned(watch, progress));
        progress.Completed.Add(quests[0]);
        Assert.True(BadgeRules.IsEarned(watch, progress));
        Assert.Contains(watch, rules.ForQuest(quests[0]));
    }

    [Fact]
    public void RankBadgesFollowTheArenaThresholds() {
        var ranks = RealBadges().PvpRankBadges;

        Assert.Equal(["Sergeant", "Veteran", "Knight", "Captain", "Commander", "Warlord"],
            ranks.Select(b => ((PvpRankBadgeAward) b.Award).Rank));
        var progress = new Progress { Rank = "Knight" };
        Assert.Equal([true, true, true, false, false, false], ranks.Select(b => BadgeRules.IsEarned(b, progress)));
    }

    [Fact]
    public void SchoolSpellBadgesNeedEveryAvailableSpellOfTheOwnSchool() {
        var rules = RealBadges();
        var fire = rules.Find("master-of-fire")!;
        var spells = ((SchoolSpellsBadgeAward) fire.Award).Spells;
        Assert.Contains("spell.fire.fire_cat", spells);
        Assert.DoesNotContain("spell.fire.fire_shield", spells); // crossover (Sabrina), not counted
        Assert.DoesNotContain("spell.life.pixie", ((SchoolSpellsBadgeAward) rules.Find("master-of-nature")!.Award).Spells);

        var progress = new Progress { School = "fire" };
        progress.Available.UnionWith(spells);
        progress.Known.UnionWith(spells.Skip(1));
        Assert.False(BadgeRules.IsEarned(fire, progress));
        progress.Known.Add(spells[0]);
        Assert.True(BadgeRules.IsEarned(fire, progress));
        progress.School = "ice";
        Assert.False(BadgeRules.IsEarned(fire, progress));          // only your own school's badge
        progress.School = "fire";
        progress.Known.Remove(spells[0]);
        progress.Available.Remove(spells[0]);                      // a spell the profile lacks is not needed
        Assert.True(BadgeRules.IsEarned(fire, progress));
        progress.Available.Clear();
        Assert.False(BadgeRules.IsEarned(fire, progress));         // no available spell: never earned
    }

    [Fact]
    public void WorldBadgesNeedTheCommonAndOwnSchoolQuests() {
        var rules = RealBadges();
        var oasis = rules.Find("master-of-the-oasis")!;
        var area = (AreaQuestsBadgeAward) oasis.Award;
        Assert.Contains("KT-MAIN-C01-004", area.Quests);
        var balance = area.BySchool["balance"];
        Assert.Contains("KT-BAL-C04-001", balance);
        Assert.DoesNotContain("KT-BAL-C04-001", area.Quests);

        var progress = new Progress { School = "fire" };
        progress.Completed.UnionWith(area.Quests);
        Assert.True(BadgeRules.IsEarned(oasis, progress));          // fire has no Krokotopia school quests
        progress.School = "balance";
        Assert.False(BadgeRules.IsEarned(oasis, progress));
        progress.Completed.UnionWith(balance);
        Assert.True(BadgeRules.IsEarned(oasis, progress));
        Assert.Contains(oasis, rules.ForQuest(balance[0]));

        var savior = (AreaQuestsBadgeAward) rules.Find("savior-of-wizard-city")!.Award;
        Assert.Equal(7, savior.BySchool.Count);
        Assert.DoesNotContain("WC-MAIN-C01-012", savior.Quests);   // Bad Blood opens Dragonspyre
    }

    [Fact]
    public void SecretShopperCountsVisits() {
        var shopper = Assert.Single(RealBadges().ForZone("Krokotopia/Interiors/KT_ShopSecret"));

        Assert.False(BadgeRules.IsEarned(shopper, new Progress { Visits = { ["Krokotopia/Interiors/KT_ShopSecret"] = 4 } }));
        Assert.True(BadgeRules.IsEarned(shopper, new Progress { Visits = { ["Krokotopia/Interiors/KT_ShopSecret"] = 5 } }));
    }

    [Fact]
    public void BadgesWithoutARuleAreNeverEarned() {
        var crafter = RealBadges().Find("novice-crafter")!;

        Assert.False(crafter.IsGranted);
        Assert.False(BadgeRules.IsEarned(crafter, new Progress()));
    }

    [Fact]
    public void AnAwardHasExactlyOneRule() {
        using var data = new TempClassicData();
        var path = WriteBadges(data, """
            badges:
            - id: two-rules
              name: Two Rules
              name_key: WizardBadges_00000003
              title_key: WizardBadges_00000003
              filter_key: BadgeFilterNames_00000002
              world: wizard_city
              award:
                quest: [WC-ST01-C01-006]
                kills: {adjective: Gobbler, count: 1}
              confidence: verified
              sources: [{page: X, oldid: 1, date: '2010-01-01'}]
            """);

        var ex = Assert.Throws<ClassicDataException>(() => BadgeRulesLoader.Load(path));
        Assert.Contains(ex.Errors, error => error.KeyPath == "badges[0].award");
    }

    [Fact]
    public void ABadLocaleKeyIsRejected() {
        using var data = new TempClassicData();
        var path = WriteBadges(data, """
            badges:
            - id: bad-key
              name: Bad Key
              name_key: Hero of Unicorn Way
              title_key: WizardBadges_00000003
              filter_key: BadgeFilterNames_00000002
              world: wizard_city
              award: {quest: [WC-ST01-C01-006]}
              confidence: verified
              sources: [{page: X, oldid: 1, date: '2010-01-01'}]
            """);

        var ex = Assert.Throws<ClassicDataException>(() => BadgeRulesLoader.Load(path));
        Assert.Contains(ex.Errors, error => error.KeyPath == "badges[0].name_key");
    }

    [Fact]
    public void QuestCardsGoToTheRightSchools() {
        var cards = RealCards();

        var seraph = Assert.Single(cards.CardsFor("WC-ST01-C01-002", "Ice"));
        Assert.Equal(("Seraph", 1792941517u, 1), (seraph.Name, seraph.Template, seraph.Count));
        Assert.Equal(["Ghoul", "Blood Bat"], cards.CardsFor("WC-MISC-C05-003", "Life").Select(c => c.Name));
        Assert.Equal("Scald", Assert.Single(cards.CardsFor("MB-MUSEHub-C03-002", "Fire")).Name);
        Assert.Equal("Bladestorm", Assert.Single(cards.CardsFor("MB-MUSEHub-C03-002", "Balance")).Name);
        Assert.Empty(cards.CardsFor("MB-MUSEHub-C03-002", "Ice"));
        Assert.Empty(cards.CardsFor("WC-ST01-C01-001", "Fire"));
    }

    [Fact]
    public void AQuestAwardsItsBadgeOnceAndSaysSo() {
        var rules = RealBadges();
        var wizard = NewWizard();
        var sent = new List<IMessage>();
        using var hooks = new Hooks(rules);

        ClassicBadges.QuestCompleted(wizard, "WC-ST01-C01-006", sent.Add);   // not complete in the registry yet
        Assert.Empty(sent);

        wizard.QuestBehavior.SetQuestRegistryValue("WC-ST01-C01-006", "Complete", 1);
        ClassicBadges.QuestCompleted(wizard, "WC-ST01-C01-006", sent.Add);
        ClassicBadges.QuestCompleted(wizard, "WC-ST01-C01-006", sent.Add);

        var badge = Assert.IsType<GAME_5_PROTOCOL.MSG_BADGES>(Assert.Single(sent));
        Assert.Equal("WizardBadges_00000003", (string) badge.BadgeName);
        Assert.Equal(1, badge.Add);
        Assert.Equal(1, badge.Display);
        Assert.Equal(["hero-of-unicorn-way"], ClassicBadges.Earned(rules, wizard).Select(b => b.Id));
        Assert.True(hooks.Saves > 0);
    }

    [Fact]
    public void ARankedRatingAwardsEveryRankItReachesOnce() {
        var rules = RealBadges();
        var wizard = NewWizard();
        var sent = new List<IMessage>();
        using var hooks = new Hooks(rules);
        var config = Imlight.Classic.Pvp.ArenaLoader.Load(Path.Combine(ClassicDataFixture.Root, "pvp", "arena-2009.yaml"));
        var ranks = ClassicBadges.ArenaRanks;
        ClassicBadges.ArenaRanks = () => config.Ranks;
        try {
            ClassicBadges.PvpRatingChanged(wizard, 549, sent.Add);
            Assert.Empty(sent);
            ClassicBadges.PvpRatingChanged(wizard, 610, sent.Add);
            Assert.Equal(["sergeant", "veteran"], ClassicBadges.Earned(rules, wizard).Select(b => b.Id));
            Assert.Equal(2, sent.Count);
            ClassicBadges.PvpRatingChanged(wizard, 520, sent.Add);     // a later loss keeps the earned badges
            Assert.Equal(2, ClassicBadges.Earned(rules, wizard).Count);
            Assert.Equal(2, sent.Count);
        }
        finally { ClassicBadges.ArenaRanks = ranks; }
    }

    [Fact]
    public void TwoActorsEarningOneBadgeAwardItOnce() {
        var rules = RealBadges();
        using var hooks = new Hooks(rules);
        for (var round = 0; round < 100; round++) {
            var wizard = NewWizard();
            wizard.QuestBehavior.SetQuestRegistryValue("MS-DTH3-C01-002", "Complete", 1);
            var sent = new System.Collections.Concurrent.ConcurrentQueue<IMessage>();

            Parallel.For(0, 4, _ => ClassicBadges.QuestCompleted(wizard, "MS-DTH3-C01-002", sent.Enqueue));

            Assert.Single(sent);
        }
    }

    [Fact]
    public void TheBadgeListIsOneMessagePerBadge() {
        var rules = RealBadges();
        var earned = new[] { rules.Find("hero-of-unicorn-way")!, rules.Find("gobbler-chaser")! };

        var messages = ClassicBadges.ListMessages(earned);

        Assert.Equal([0u, 1u], messages.Select(m => m.CurrentBadge));
        Assert.All(messages, m => Assert.Equal(2u, m.TotalBadges));
        Assert.Equal([1, 0], messages.Select(m => (int) m.UpdateAll));
        Assert.Equal([0, 1], messages.Select(m => (int) m.LastSegment));
        Assert.NotEqual(messages[0].BadgeNameID, messages[1].BadgeNameID);
        Assert.Equal(ClassicBadges.NameId(earned[0]), messages[0].BadgeNameID);
        var none = Assert.Single(ClassicBadges.ListMessages([]));
        Assert.Equal(0u, none.TotalBadges);
    }

    private static Wizard NewWizard() => new() { CharId = 77, QuestBehavior = new ServerQuestBehavior() };

    private static string WriteBadges(TempClassicData data, string badges) {
        var dir = Path.Combine(data.Root, "badges");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "badges-test.yaml");
        const string Header = """
            id: badges-test
            profiles: [late-2009]
            provenance: [{source: test, source_date: '2010-01-01', retrieved: '2026-09-27', covers: [badges], confidence: verified}]
            license_tag: own
            """;
        File.WriteAllText(path, (Header + "\n" + badges).ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));

        return path;
    }

    private sealed class Progress : IBadgeProgress {

        public HashSet<string> Completed { get; } = [];
        public HashSet<string> Known { get; } = [];
        public HashSet<string> Available { get; } = [];
        public string? School { get; set; }
        public string? Rank { get; set; }
        private static readonly string[] s_ranks = ["Private", "Corporal", "Sergeant", "Veteran", "Knight", "Captain", "Commander", "Warlord"];
        public Dictionary<string, int> Kills { get; } = [];
        public Dictionary<string, int> Visits { get; } = [];

        public bool HasCompletedQuest(string quest) => Completed.Contains(quest);
        public int KillCount(string adjective) => Kills.GetValueOrDefault(adjective);
        public int ZoneVisits(string zone) => Visits.GetValueOrDefault(zone);
        public string? PrimarySchool => School;
        public bool KnowsSpell(string spellId) => Known.Contains(spellId);
        public bool IsSpellAvailable(string spellId) => Available.Contains(spellId);
        public bool ReachedPvpRank(string rank) => Rank is not null && Array.IndexOf(s_ranks, rank) <= Array.IndexOf(s_ranks, Rank);

    }

    private sealed class Hooks : IDisposable {

        private readonly Action<Wizard> _persist = ClassicBadges.Persist;
        private readonly Func<BadgeRules?> _rules = ClassicBadges.Rules;
        private readonly Action<Wizard, Badge> _awarded = ClassicBadges.Awarded;
        private int _saves;

        public Hooks(BadgeRules rules) {
            ClassicBadges.Rules = () => rules;
            ClassicBadges.Persist = _ => System.Threading.Interlocked.Increment(ref _saves);
            ClassicBadges.Awarded = (_, _) => { };
        }

        public int Saves => _saves;

        public void Dispose() {
            ClassicBadges.Persist = _persist;
            ClassicBadges.Rules = _rules;
            ClassicBadges.Awarded = _awarded;
        }

    }

}
