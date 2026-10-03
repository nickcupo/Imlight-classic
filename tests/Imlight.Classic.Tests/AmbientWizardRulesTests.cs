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
 * AMBIENT WIZARD RULES TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The server-independent parts of ambient wizards: settings, identity,
 * the ally brain's rule list, the ask-first help offers, the chat brain
 * and its limiter.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter AmbientWizardRulesTests
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Linq;
using Imlight.Classic.Ambient;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class AmbientWizardRulesTests {

    // ---- settings ------------------------------------------------------------------------------

    [Theory]
    [InlineData("off")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("-2")]
    [InlineData("lots")]
    public void SettingsTurnOff(string count) => Assert.False(AmbientSettings.Parse(count, "", "", "", "").Enabled);

    [Fact]
    public void BlankSettingsUseTheModestDefaultInTheDefaultZones() {
        var settings = AmbientSettings.Parse("", "", "", "", "");

        Assert.True(settings.Enabled);
        Assert.True(settings.UsesDefaultZones);
        Assert.Equal(AmbientSettings.DefaultPerZone, settings.PerZone);
        Assert.Contains(settings.Zones, z => z.Zone == "WizardCity/WC_Hub" && z.Count == AmbientSettings.DefaultPerZone);
        Assert.True(settings.Chat && settings.Battles && settings.StreetFights);
    }

    [Fact]
    public void ZoneListTakesPerZoneCountsAndCapsThem() {
        var settings = AmbientSettings.Parse("3", "WizardCity/WC_Hub:6, Krokotopia/KT_Hub, MooShu/MS_Hub:99", "false", "true", "no");

        Assert.False(settings.UsesDefaultZones);
        Assert.Equal([("WizardCity/WC_Hub", 6), ("Krokotopia/KT_Hub", 3), ("MooShu/MS_Hub", AmbientSettings.MaxPerZone)],
            settings.Zones.ToArray());
        Assert.False(settings.Chat);
        Assert.True(settings.Battles);
        Assert.True(settings.StreetFights); // "no" is not a bool: the default stays.
    }

    // ---- identity ------------------------------------------------------------------------------

    private static readonly NameTableSizes s_tables = new(FirstBoy: 145, FirstGirl: 131, Middle: 84, Last: 78);

    [Fact]
    public void SameSeedSameWizard() {
        var a = AmbientIdentity.Generate(42, "WizardCity/WC_Hub", s_tables, (1, 12));
        var b = AmbientIdentity.Generate(42, "WizardCity/WC_Hub", s_tables, (1, 12));

        Assert.Equal(a, b);
    }

    [Fact]
    public void IdentitiesStayInsideTheClassicCreationChoices() {
        for (var seed = 0; seed < 500; seed++) {
            var id = AmbientIdentity.Generate(seed, "Krokotopia/KT_Hub", s_tables, (10, 22));
            var (first, middle, last) = ((int) (id.NameKeys >> 16) & 0xFF, (int) (id.NameKeys >> 8) & 0xFF, (int) id.NameKeys & 0xFF);

            Assert.InRange(first, 0, (id.Look.Female ? s_tables.FirstGirl : s_tables.FirstBoy) - 1);
            Assert.InRange(middle, 1, s_tables.Middle);
            Assert.InRange(last, 1, s_tables.Last);
            Assert.True(s_tables.Allows(id.NameKeys, id.Look.Female));
            Assert.InRange(id.Level, 10, 22);
            Assert.InRange(id.Look.HairModel, 0, 9);
            Assert.Equal(id.Look.HairModel, id.Look.HairColor / 10);
            Assert.InRange(id.Look.SkinColor, 0, 6);
            Assert.InRange(id.Look.Face, 0, 9);
            Assert.InRange(id.Look.ClothingColor, 0, 13);
            Assert.InRange(id.Look.TrimColor, 0, 13);
        }
    }

    [Fact]
    public void LaterNamePartsAreNotClassicAndRenamesAre() {
        // "Torch" (last-name table index 229) came after 2009; 0 means no part, which the 2009 screen never allowed.
        Assert.False(s_tables.Allows(5u << 16 | 3u << 8 | 229u, female: false));
        Assert.False(s_tables.Allows(5u << 16 | 0u << 8 | 3u, female: false));
        Assert.False(s_tables.Allows(140u << 16 | 3u << 8 | 3u, female: true)); // past the 131 girl names
        Assert.True(s_tables.Allows(140u << 16 | 3u << 8 | 3u, female: false));

        for (var seed = 0; seed < 300; seed++) {
            var girl = AmbientIdentity.ClassicNameKeys(seed, female: true, s_tables);
            Assert.True(s_tables.Allows(girl, female: true));
            Assert.Equal(girl, AmbientIdentity.ClassicNameKeys(seed, female: true, s_tables));
        }
    }

    [Fact]
    public void SeedsMakeAMixOfSchoolsAndGenders() {
        var ids = Enumerable.Range(0, 200).Select(s => AmbientIdentity.Generate(s, "WizardCity/WC_Hub", s_tables, (1, 12))).ToList();

        Assert.Equal(7, ids.Select(i => i.School).Distinct().Count());
        Assert.Contains(ids, i => i.Look.Female);
        Assert.Contains(ids, i => !i.Look.Female);
        Assert.True(ids.Select(i => i.NameKeys).Distinct().Count() > 190);
    }

    [Theory]
    [InlineData("WizardCity/WC_Streets/WC_Unicorn", 50, 1, 12)]
    [InlineData("Krokotopia/KT_Hub", 50, 10, 22)]
    [InlineData("DragonSpire/DS_Hub_Cathedral", 50, 40, 50)]
    [InlineData("DragonSpire/DS_Hub_Cathedral", 30, 30, 30)]
    public void LevelsFitTheWorldAndTheCap(string zone, int cap, int min, int max)
        => Assert.Equal(((byte) min, (byte) max), AmbientIdentity.LevelsFor(zone, cap));

    // ---- ally brain ----------------------------------------------------------------------------

    private static AllyCard Hit(int index, string name, int pips, int min, int max, double accuracy = 1.0, bool all = false)
        => new(index, name, pips, accuracy, AllyCardRole.Damage, min, max, AllEnemies: all);

    private static AllyView View(int pips, AllyCard[] hand, params AllyCombatant[] combatants)
        => new(SelfSlot: 4, Pips: pips, Stunned: false, Hand: hand, Combatants: combatants);

    private static readonly AllyCombatant Me = new(4, true, 500, 500);

    [Fact]
    public void StunnedAllyPasses() {
        var view = View(5, [Hit(0, "Fire Cat", 1, 80, 120)], Me, new AllyCombatant(0, false, 300, 300)) with { Stunned = true };

        Assert.Equal(AllyMoveKind.Pass, AllyBrain.Choose(view).Kind);
    }

    [Fact]
    public void HealsTheMostHurtAllyBelowFortyPercent() {
        var heal = new AllyCard(1, "Fairy", 3, 0.9, AllyCardRole.Heal, Heal: 300);
        var view = View(3, [Hit(0, "Imp", 1, 70, 110), heal], Me,
            new AllyCombatant(5, true, 150, 500), new AllyCombatant(6, true, 100, 500), new AllyCombatant(0, false, 300, 300));

        var move = AllyBrain.Choose(view);

        Assert.Equal((AllyMoveKind.Cast, 1, 6), (move.Kind, move.HandIndex, move.TargetSlot));
    }

    [Fact]
    public void DoesNotHealAboveTheLine() {
        var heal = new AllyCard(1, "Fairy", 3, 0.9, AllyCardRole.Heal, Heal: 300);
        var view = View(3, [Hit(0, "Imp", 1, 70, 110), heal], Me, new AllyCombatant(5, true, 300, 500), new AllyCombatant(0, false, 300, 300));

        Assert.Equal(0, AllyBrain.Choose(view).HandIndex);
    }

    [Fact]
    public void UsesAnAllEnemySpellAgainstThreeOrMoreAndStillNamesATarget() {
        var view = View(4, [Hit(0, "Fire Elf", 3, 165, 165), Hit(1, "Meteor Strike", 4, 100, 140, 0.75, all: true)], Me,
            new AllyCombatant(0, false, 400, 400), new AllyCombatant(1, false, 400, 400), new AllyCombatant(2, false, 400, 400));

        var move = AllyBrain.Choose(view);

        Assert.Equal(1, move.HandIndex);
        Assert.InRange(move.TargetSlot, 0, 2); // Before May 2010 all-enemy spells took a target.
    }

    [Fact]
    public void FinishesANearlyDeadEnemyWithTheCheapestKillNotTheBiggestHit() {
        var view = View(5, [Hit(0, "Fire Cat", 1, 80, 120), Hit(1, "Fire Elemental", 5, 400, 460)], Me,
            new AllyCombatant(0, false, 60, 400), new AllyCombatant(1, false, 400, 400));

        var move = AllyBrain.Choose(view);

        Assert.Equal((0, 0), (move.HandIndex, move.TargetSlot));
    }

    [Fact]
    public void BladesOnceBeforeABigHitThenHits() {
        var blade = new AllyCard(2, "Fireblade", 0, 1.0, AllyCardRole.Blade);
        var hand = new[] { Hit(0, "Fire Cat", 1, 80, 120), Hit(1, "Fire Elemental", 4, 400, 460), blade };
        var enemy = new AllyCombatant(0, false, 900, 900);

        var first = AllyBrain.Choose(View(4, hand, Me, enemy));
        Assert.Equal(2, first.HandIndex);

        var bladed = Me with { Blades = 1 };
        var second = AllyBrain.Choose(View(4, hand, bladed, enemy));
        Assert.Equal((1, 0), (second.HandIndex, second.TargetSlot));
    }

    [Fact]
    public void SavesPipsWhenAMuchStrongerCardIsOnePipAway() {
        var hand = new[] { Hit(0, "Fire Cat", 1, 80, 120), Hit(1, "Fire Elemental", 4, 400, 460) };
        var move = AllyBrain.Choose(View(3, hand, Me, new AllyCombatant(0, false, 900, 900)));

        Assert.Equal(AllyMoveKind.Pass, move.Kind);
    }

    [Fact]
    public void FocusesTheEnemyTheTeamIsHitting() {
        var view = View(1, [Hit(0, "Fire Cat", 1, 80, 120)], Me,
            new AllyCombatant(0, false, 200, 400), new AllyCombatant(1, false, 350, 400, TeamHits: 2));

        Assert.Equal(1, AllyBrain.Choose(view).TargetSlot);
    }

    [Fact]
    public void PassesToBuildPipsWhenNothingIsCastable() {
        var view = View(0, [Hit(0, "Fire Elemental", 5, 400, 460)], Me, new AllyCombatant(0, false, 900, 900));

        Assert.Equal(AllyMoveKind.Pass, AllyBrain.Choose(view).Kind);
    }

    [Fact]
    public void SameViewSameMove() {
        var view = View(3, [Hit(0, "Fire Cat", 1, 80, 120), Hit(1, "Fire Elf", 3, 165, 165)], Me,
            new AllyCombatant(0, false, 300, 400), new AllyCombatant(1, false, 300, 400));

        Assert.Equal(AllyBrain.Choose(view), AllyBrain.Choose(view));
    }

    // ---- help offers ---------------------------------------------------------------------------

    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("yes", HelpAnswerKind.Yes)]
    [InlineData("Y", HelpAnswerKind.Yes)]
    [InlineData("Sure!", HelpAnswerKind.Yes)]
    [InlineData("ok", HelpAnswerKind.Yes)]
    [InlineData("yes please", HelpAnswerKind.Yes)]
    [InlineData("YEAH thanks", HelpAnswerKind.Yes)]
    [InlineData("no", HelpAnswerKind.No)]
    [InlineData("nah im good", HelpAnswerKind.No)]
    [InlineData("i got it", HelpAnswerKind.No)]
    [InlineData("Yes!", HelpAnswerKind.Yes)]
    [InlineData("Okay", HelpAnswerKind.Yes)]
    [InlineData("Help!", HelpAnswerKind.Yes)]
    [InlineData("Please do not join this duel.", HelpAnswerKind.No)]
    [InlineData("No thanks!", HelpAnswerKind.No)]
    [InlineData("where is the bazaar", HelpAnswerKind.None)]
    [InlineData("", HelpAnswerKind.None)]
    public void AnswersAreReadCaseInsensitively(string text, HelpAnswerKind kind) => Assert.Equal(kind, HelpOffers.Classify(text));

    [Fact]
    public void YesInsideTheWindowJoinsAndNoOneIsAskedAgainAtOnce() {
        var offers = new HelpOffers();
        Assert.True(offers.MayOffer(7, T0));
        offers.Offered(7, duelId: 99, T0);

        Assert.False(offers.MayOffer(7, T0.AddSeconds(1)));
        Assert.Equal(new HelpAnswer(HelpAnswerKind.None, 0), offers.Hear(8, "yes", T0.AddSeconds(2))); // someone else
        Assert.Equal(new HelpAnswer(HelpAnswerKind.Yes, 99), offers.Hear(7, "sure", T0.AddSeconds(5)));
        Assert.False(offers.MayOffer(7, T0.AddMinutes(1)));
        Assert.True(offers.MayOffer(7, T0.AddMinutes(4)));
    }

    [Fact]
    public void NoAnswerInTwentySecondsMeansNoAndACooldown() {
        var offers = new HelpOffers();
        offers.Offered(7, 99, T0);

        Assert.Equal(HelpAnswerKind.None, offers.Hear(7, "yes", T0.AddSeconds(25)).Kind);
        Assert.False(offers.MayOffer(7, T0.AddMinutes(2)));
        Assert.True(offers.MayOffer(7, T0.AddMinutes(3.5)));
    }

    [Fact]
    public void ANoClosesTheOfferWithACooldown() {
        var offers = new HelpOffers();
        offers.Offered(7, 99, T0);

        Assert.Equal(HelpAnswerKind.No, offers.Hear(7, "no thanks", T0.AddSeconds(3)).Kind);
        Assert.False(offers.IsOpen(7, T0.AddSeconds(4)));
        Assert.False(offers.MayOffer(7, T0.AddMinutes(2)));
    }

    // ---- chat ----------------------------------------------------------------------------------

    private static ChatContext Ctx(FriendMemory? friend = null, Func<string, string?>? where = null)
        => new("Ryan Stormblade", AmbientSchool.Storm, 14, "The Commons", "Alex Dragonflame", "Unicorn Way",
            "Rattlebones", friend, where, T0);

    [Fact]
    public void SayWithoutTheNameGetsNoReplyButAWhisperDoes() {
        Assert.Null(AmbientChatBrain.Reply("what level are you?", Ctx(), direct: false, turn: 0));
        Assert.NotNull(AmbientChatBrain.Reply("what level are you?", Ctx(), direct: true, turn: 0));
    }

    [Fact]
    public void AnswersLevelAndSchoolFromItsOwnData() {
        Assert.Contains("14", AmbientChatBrain.Reply("ryan what level are you?", Ctx(), false, 0));
        Assert.Contains("Storm", AmbientChatBrain.Reply("Ryan, what school r u", Ctx(), false, 1));
    }

    [Fact]
    public void WhereQuestionsUseTheServerLookup() {
        var reply = AmbientChatBrain.Reply("where is lady blackhope?", Ctx(where: s => s.Contains("blackhope") ? "Unicorn Way" : null), true, 0);

        Assert.Contains("Unicorn Way", reply);
    }

    [Fact]
    public void FriendsAreGreetedByNameAndRemembered() {
        var friend = new FriendMemory(5, "Alex Dragonflame", "Unicorn Way", "Rattlebones", T0.AddDays(-2), 3);
        var lines = Enumerable.Range(0, 6).Select(t => AmbientChatBrain.GreetFriend(Ctx(friend), t)).ToList();

        Assert.All(lines, line => Assert.Contains("Alex", line));
        Assert.Contains(lines, line => line!.Contains("Unicorn Way") || line.Contains("Rattlebones"));
    }

    [Fact]
    public void RepliesAreDeterministicAndClean() {
        foreach (var text in new[] { "hi ryan", "ryan want to duel?", "ryan can you help me", "thanks ryan", "bye ryan", "ryan sup" }) {
            for (var turn = 0; turn < 8; turn++) {
                var a = AmbientChatBrain.Reply(text, Ctx(), false, turn);
                Assert.Equal(a, AmbientChatBrain.Reply(text, Ctx(), false, turn));
                Assert.True(AmbientChatBrain.IsClean(a), $"{text} -> {a}");
            }
        }
    }

    [Theory]
    [InlineData("visit www.example.com", false)]
    [InlineData("call 5551234", false)]
    [InlineData("you noob", false)]
    [InlineData("i'm level 14!", true)]
    [InlineData("<b>hi</b>", false)]
    public void TheCleanCheckKeepsTwoThousandNineTone(string line, bool clean) => Assert.Equal(clean, AmbientChatBrain.IsClean(line));

    [Fact]
    public void LimiterKeepsGapsAndAPerMinuteCap() {
        var limiter = new AmbientChatLimiter();
        Assert.True(limiter.TryTake(T0));
        Assert.False(limiter.TryTake(T0.AddSeconds(2)));
        var sent = 1;
        for (var s = 7; s < 60; s += 7) {
            if (limiter.TryTake(T0.AddSeconds(s))) {
                sent++;
            }
        }

        Assert.Equal(AmbientChatLimiter.PerMinute, sent);
        Assert.True(limiter.TryTake(T0.AddSeconds(70)));
    }

    [Fact]
    public void LimiterRepliesToOneSpeakerAtATime() {
        var limiter = new AmbientChatLimiter();
        Assert.True(limiter.TryTake(T0, speaker: 9));
        Assert.False(limiter.TryTake(T0.AddSeconds(7).AddMilliseconds(-3500), speaker: 9));
    }

}
