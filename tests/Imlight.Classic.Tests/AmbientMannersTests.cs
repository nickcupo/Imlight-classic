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
 * AMBIENT MANNERS TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The "would a real player do this?" rules for ambient wizards (owner,
 * 2026-10-04): help offers only for duels a wizard can see, a moment after
 * it noticed, never to a player plainly winning or who said no; lines of
 * sight over collision; spots off doorways and apart; lingering after a
 * duel then walking on; dungeon grouping (who fits, slots, following
 * behind, giving up, settings); the dungeon deck planner; chat lines.
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter AmbientMannersTests
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Imlight.Classic.Ambient;
using Imlight.Classic.Spells;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class AmbientMannersTests {

    private static readonly float[,] s_identity = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
    private static readonly DateTime T0 = new(2026, 10, 4, 20, 0, 0, DateTimeKind.Utc);

    private static IEnumerable<NavTriangle> Square(float x0, float y0, float x1, float y1, float z = 0) {
        yield return new NavTriangle(new(x0, y0, z), new(x1, y0, z), new(x1, y1, z));
        yield return new NavTriangle(new(x0, y0, z), new(x1, y1, z), new(x0, y1, z));
    }

    private static OfferFacts Facts(float distance = 600, bool see = true, double seenFor = 6, DuelOdds? odds = null, int free = 2,
                                    bool pvp = false, bool quiet = false)
        => new(distance, see, TimeSpan.FromSeconds(seenFor), TimeSpan.FromSeconds(3), odds ?? DuelOdds.Unknown, free, pvp, quiet);

    // ---- help offers ----------------------------------------------------------------------------

    [Fact]
    public void AWizardOffersOnlyForADuelItCanSeeNearby() {
        Assert.Equal(OfferVerdict.Offer, HelpManners.Judge(Facts()));
        Assert.Equal(OfferVerdict.TooFar, HelpManners.Judge(Facts(distance: 1500)));
        Assert.Equal(OfferVerdict.TooFar, HelpManners.Judge(Facts(distance: 9000))); // the old 9000-unit reach
        Assert.Equal(OfferVerdict.OutOfSight, HelpManners.Judge(Facts(see: false)));
        Assert.Equal(OfferVerdict.Offer, HelpManners.Judge(Facts(distance: HelpManners.ViewDistance)));
    }

    [Fact]
    public void AWizardTakesAMomentToNoticeBeforeItOffers() {
        Assert.Equal(OfferVerdict.NotYet, HelpManners.Judge(Facts(seenFor: 0)));
        Assert.Equal(OfferVerdict.NotYet, HelpManners.Judge(Facts(seenFor: 2.9)));
        Assert.Equal(OfferVerdict.Offer, HelpManners.Judge(Facts(seenFor: 3)));
        var times = Enumerable.Range(0, 200).Select(HelpManners.ReactionTime).ToList();
        Assert.All(times, t => Assert.InRange(t.TotalSeconds, 2.0, 5.0));
        Assert.True(times.Distinct().Count() > 10); // not every wizard the same
    }

    [Fact]
    public void NobodyOffersHelpToAPlayerWinningEasily() {
        Assert.Equal(OfferVerdict.WinningEasily, HelpManners.Judge(Facts(odds: new DuelOdds(1, 0.2, 0.9))));
        Assert.Equal(OfferVerdict.WinningEasily, HelpManners.Judge(Facts(odds: new DuelOdds(3, 0.35, 0.8))));
        Assert.Equal(OfferVerdict.WinningEasily, HelpManners.Judge(Facts(odds: new DuelOdds(0, 0, 0.5))));
        Assert.Equal(OfferVerdict.Offer, HelpManners.Judge(Facts(odds: new DuelOdds(1, 0.45, 0.3)))); // the player is low and the enemy is not
        Assert.Equal(OfferVerdict.Offer, HelpManners.Judge(Facts(odds: new DuelOdds(2, 0.8, 0.9))));
        Assert.Equal(OfferVerdict.Offer, HelpManners.Judge(Facts(odds: DuelOdds.Unknown)));
    }

    [Fact]
    public void NoOfferForFullOrPvpDuelsOrAQuietPlayer() {
        Assert.Equal(OfferVerdict.Full, HelpManners.Judge(Facts(free: 0)));
        Assert.Equal(OfferVerdict.Pvp, HelpManners.Judge(Facts(pvp: true)));
        Assert.Equal(OfferVerdict.PlayerQuiet, HelpManners.Judge(Facts(quiet: true)));
    }

    [Fact]
    public void ANoQuietsTheWholeZoneForThatPlayer() {
        var memory = new ZoneHelpMemory();
        Assert.False(memory.IsQuiet(7, T0));
        memory.SaidNo(7, T0);
        Assert.True(memory.IsQuiet(7, T0.AddMinutes(9)));
        Assert.True(memory.AnyQuiet([3, 7], T0.AddMinutes(1)));
        Assert.False(memory.IsQuiet(3, T0));
        Assert.False(memory.IsQuiet(7, T0 + HelpManners.QuietAfterNo));

        memory.Ignored(8, T0);
        Assert.True(memory.IsQuiet(8, T0.AddMinutes(4)));
        Assert.False(memory.IsQuiet(8, T0.AddMinutes(5)));

        // A later, shorter quiet does not cut a no short.
        memory.SaidNo(9, T0);
        memory.Ignored(9, T0);
        Assert.True(memory.IsQuiet(9, T0.AddMinutes(8)));
    }

    [Fact]
    public void ADuelIsSeenFromItsFirstNotice() {
        var memory = new ZoneHelpMemory();
        Assert.Equal(T0, memory.Seen(5, T0));
        Assert.Equal(T0, memory.Seen(5, T0.AddSeconds(4)));
        memory.Forget(5);
        Assert.Equal(T0.AddSeconds(9), memory.Seen(5, T0.AddSeconds(9)));
    }

    [Fact]
    public void AnOfferLeftUnansweredIsReportedOnce() {
        var offers = new HelpOffers();
        offers.Offered(11, 99, T0);
        Assert.Empty(offers.TakeLapsed(T0.AddSeconds(5)));
        Assert.Equal([11UL], offers.TakeLapsed(T0.AddSeconds(25)));
        Assert.Empty(offers.TakeLapsed(T0.AddSeconds(26)));

        offers.Offered(12, 99, T0.AddMinutes(10));
        Assert.Equal(HelpAnswerKind.Yes, offers.Hear(12, "sure", T0.AddMinutes(10).AddSeconds(3)).Kind);
        Assert.Empty(offers.TakeLapsed(T0.AddMinutes(11))); // answered: not a lapse
    }

    [Fact]
    public void AFarWizardWalksOverAndAsksFromTheCircleEdge() {
        Assert.Null(HelpManners.ApproachSpot(400, 0, 0, 0));
        var spot = HelpManners.ApproachSpot(1100, 0, 0, 0);
        Assert.NotNull(spot);
        Assert.Equal(HelpManners.AskFrom, spot.Value.X, 1f);
        Assert.Equal(0, spot.Value.Y, 1f);
    }

    // ---- sight ----------------------------------------------------------------------------------

    [Fact]
    public void ABuildingHidesADuelAndOpenGroundDoesNot() {
        NavObstacle[] house = [NavObstacle.Box(new(1000, 1000, 200), s_identity, new(400, 400, 400))];
        var sight = SightGrid.Build([.. Square(0, 0, 2000, 2000)], house);
        Assert.False(sight.CanSee(new(500, 1000, 0), new(1500, 1000, 0)));  // through the house
        Assert.True(sight.CanSee(new(500, 300, 0), new(1500, 300, 0)));    // along the street past it
        Assert.True(sight.CanSee(new(500, 1000, 0), new(700, 1000, 0)));   // this side of it
    }

    [Fact]
    public void APostOrALowFenceDoesNotHideADuel() {
        NavObstacle[] things = [
            NavObstacle.Cylinder(new(1000, 1000, 150), s_identity, 15, 300), // a lamp post
            NavObstacle.Box(new(1000, 600, 20), s_identity, new(20, 800, 40)), // a fence, knee high
        ];
        var sight = SightGrid.Build([.. Square(0, 0, 2000, 2000)], things);
        Assert.True(sight.CanSee(new(500, 1000, 0), new(1500, 1000, 0)));
        Assert.True(sight.CanSee(new(500, 600, 0), new(1500, 600, 0)));
    }

    [Fact]
    public void AHillBetweenHidesWaterDoesNot() {
        var floor = new List<NavTriangle>();
        floor.AddRange(Square(0, 0, 900, 2000));
        floor.AddRange(Square(900, 0, 1100, 2000, 300)); // a ridge, above eye height
        floor.AddRange(Square(1100, 0, 2000, 2000));
        var sight = SightGrid.Build(floor, []);
        Assert.False(sight.CanSee(new(500, 1000, 0), new(1500, 1000, 0)));

        // No floor (a pond) in between: seen across it.
        var pond = SightGrid.Build([.. Square(0, 0, 900, 2000), .. Square(1100, 0, 2000, 2000)], []);
        Assert.True(pond.CanSee(new(500, 1000, 0), new(1500, 1000, 0)));
    }

    [Fact]
    public void AWallTheWizardLeansOnDoesNotHideWhatIsInFront() {
        NavObstacle[] wall = [NavObstacle.Box(new(480, 1000, 200), s_identity, new(40, 600, 400))];
        var sight = SightGrid.Build([.. Square(0, 0, 2000, 2000)], wall);
        Assert.True(sight.CanSee(new(520, 1000, 0), new(1500, 1000, 0)));
        Assert.False(sight.CanSee(new(300, 1000, 0), new(1500, 1000, 0)));
    }

    // ---- the street -----------------------------------------------------------------------------

    [Fact]
    public void WizardsStandOffDoorwaysAndApart() {
        var door = new Vector2(1000, 1000);
        Assert.True(StreetManners.InDoorway(door + new Vector2(100, 0), [door]));
        var off = StreetManners.StandOff(door, 0.7, StreetManners.StandOffDistance);
        Assert.False(StreetManners.InDoorway(off, [door]));
        Assert.Equal(StreetManners.StandOffDistance, Vector2.Distance(off, door), 0.5f);

        Assert.True(StreetManners.Crowded(new(0, 0), [new(100, 0)]));
        Assert.False(StreetManners.Crowded(new(0, 0), [new(200, 0)]));
    }

    [Fact]
    public void ShoppersStandSideBySideAtAnNpc() {
        var front = new Vector2(500, 500);
        var spots = Enumerable.Range(0, 4).Select(i => StreetManners.BesideNpc(front, 0f, i)).ToList();
        Assert.Equal(front, spots[0]);
        for (var i = 0; i < spots.Count; i++) {
            for (var j = i + 1; j < spots.Count; j++) {
                Assert.True(Vector2.Distance(spots[i], spots[j]) >= StreetManners.PersonalSpace - 0.5f);
            }
        }

        Assert.All(spots, s => Assert.Equal(front.X, s.X, 0.5f)); // along the NPC's front, not behind it
    }

    [Fact]
    public void AfterADuelAWizardLingersAMomentThenWalksOn() {
        var pauses = Enumerable.Range(0, 50).Select(StreetManners.AfterDuelPause).ToList();
        Assert.All(pauses, p => Assert.InRange(p.TotalSeconds, 3, 8));
        Assert.True(StreetManners.LongestDuel >= TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void StandingWizardsTurnALittleNowAndThen() {
        Assert.Equal(0f, StreetManners.IdleTurn(0.1));
        var turns = Enumerable.Range(0, 100).Select(i => StreetManners.IdleTurn(i / 100.0)).ToList();
        Assert.All(turns, t => Assert.InRange(t, -0.61f, 0.61f));
        Assert.Contains(turns, t => t < -0.3f);
        Assert.Contains(turns, t => t > 0.3f);
    }

    // ---- dungeons -------------------------------------------------------------------------------

    [Fact]
    public void DungeonSettingsParseWithDefaults() {
        Assert.Equal(new DungeonSettings(true, DungeonSettings.DefaultChance, DungeonSettings.DefaultHelpers), DungeonSettings.Parse("", "", ""));
        Assert.Equal(DungeonSettings.Off, DungeonSettings.Parse("false", "", ""));
        Assert.Equal(new DungeonSettings(true, 1, 3), DungeonSettings.Parse("true", "5", "9"));
        Assert.Equal(DungeonSettings.Off, DungeonSettings.Parse("true", "0", "2"));
        Assert.Equal(DungeonSettings.Off, DungeonSettings.Parse("true", "0.5", "0"));
        Assert.Equal(new DungeonSettings(true, 0.5, DungeonSettings.DefaultHelpers), DungeonSettings.Parse(" true ", "0.5", "x"));
    }

    [Fact]
    public void HelpersFitTheLeadersLevel() {
        Assert.True(DungeonManners.Fits(10, 12));
        Assert.True(DungeonManners.Fits(7, 12));
        Assert.False(DungeonManners.Fits(6, 12));   // too low to help
        Assert.True(DungeonManners.Fits(24, 12));   // a friendly high level
        Assert.False(DungeonManners.Fits(25, 12));
        Assert.True(DungeonManners.Fits(1, 2));
    }

    [Fact]
    public void HelpersNeverTakeARealPlayersPlaceOrAOneWizardTower() {
        Assert.Equal(2, DungeonManners.OpenHelperSlots(realPlayers: 1, helpers: 0, zoneHardLimit: 0, maxHelpers: 2));
        Assert.Equal(1, DungeonManners.OpenHelperSlots(1, 1, 0, 2));
        Assert.Equal(0, DungeonManners.OpenHelperSlots(1, 2, 0, 2));
        Assert.Equal(1, DungeonManners.OpenHelperSlots(3, 0, 0, 3));  // four in all
        Assert.Equal(0, DungeonManners.OpenHelperSlots(4, 0, 0, 3));
        Assert.Equal(0, DungeonManners.OpenHelperSlots(1, 0, 1, 3));  // a tower for one
        Assert.Equal(1, DungeonManners.OpenHelperSlots(1, 0, 2, 3));  // a tower for two
        Assert.Equal(0, DungeonManners.OpenHelperSlots(0, 0, 0, 3));  // no real player: nobody to help
        Assert.Equal(3, DungeonManners.OpenHelperSlots(1, 0, 12, 3)); // a big hard limit is still four a run
    }

    [Fact]
    public void HelpersKeepBehindTheLeaderNeverAhead() {
        var leader = new Vector2(1000, 1000);
        const float heading = 0f; // facing +X
        for (var i = 0; i < 4; i++) {
            var spot = DungeonManners.FollowSpot(leader, heading, i);
            Assert.True(spot.X < leader.X, $"helper {i} ahead of the leader");
            Assert.InRange(Vector2.Distance(spot, leader), DungeonManners.FollowDistance - 1, DungeonManners.FollowDistance + 100);
        }

        Assert.NotEqual(DungeonManners.FollowSpot(leader, heading, 0), DungeonManners.FollowSpot(leader, heading, 1));
        Assert.False(DungeonManners.ShouldWalk(new(800, 1000), new(900, 1000)));
        Assert.True(DungeonManners.ShouldWalk(new(400, 1000), new(900, 1000)));
    }

    [Fact]
    public void AStuckOrLostHelperGivesUp() {
        Assert.Null(DungeonManners.GiveUpReason(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(5)));
        Assert.Equal("stuck", DungeonManners.GiveUpReason(DungeonManners.StuckLimit, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal("lost", DungeonManners.GiveUpReason(TimeSpan.Zero, DungeonManners.FarLimit, TimeSpan.Zero));
        Assert.Equal("too long", DungeonManners.GiveUpReason(TimeSpan.Zero, TimeSpan.Zero, DungeonManners.LongestRun));
    }

    [Fact]
    public void DungeonLinesAreShortLowerCaseAndPickedBySeed() {
        foreach (var lines in new[] { DungeonLines.Join, DungeonLines.Declined, DungeonLines.MakeRoom, DungeonLines.Thanks,
                     DungeonLines.Leave, DungeonLines.Defeated }) {
            Assert.NotEmpty(lines);
            Assert.All(lines, l => Assert.Equal(l.ToLowerInvariant(), l));
            Assert.All(lines, l => Assert.InRange(l.Length, 2, 40));
            Assert.Equal(DungeonLines.Pick(lines, 3), DungeonLines.Pick(lines, 3 + lines.Length));
            Assert.Contains(DungeonLines.Pick(lines, -5), lines);
        }

        Assert.Contains("ty for the group!", DungeonLines.Thanks);
        Assert.Contains("gtg sorry", DungeonLines.Leave);
    }

    // ---- decks ----------------------------------------------------------------------------------

    private static ClassicSpellRecord Spell(string name, string school, int pips, params SpellEffectValues[] effects)
        => new() {
            Id = name.ToLowerInvariant().Replace(' ', '-'), Name = name, School = school, Kind = "trained", Profiles = ["2009"], SourceFile = "test.yaml",
            Values = new SpellValues(SpellPips.Of(pips), 0.8, 1, 0, null, [.. effects]),
        };

    private static SpellEffectValues Hit(int min, int max, SpellTargets targets = SpellTargets.Single)
        => new(SpellEffectKind.Damage, "fire", min, max, null, null, targets, null);

    private static readonly ClassicSpellRecord[] s_fire = [
        Spell("Fire Cat", "fire", 1, Hit(80, 120)),
        Spell("Fire Elf", "fire", 2, new SpellEffectValues(SpellEffectKind.Dot, "fire", 300, 300, null, 3, SpellTargets.Single, null)),
        Spell("Sunbird", "fire", 3, Hit(240, 280)),
        Spell("Meteor Strike", "fire", 4, Hit(150, 190, SpellTargets.AllEnemies)),
        Spell("Phoenix", "fire", 5, Hit(565, 625)),
        Spell("Fireblade", "fire", 0, new SpellEffectValues(SpellEffectKind.Blade, "fire", null, null, 35, null, SpellTargets.Single, null)),
        Spell("Fire Trap", "fire", 0, new SpellEffectValues(SpellEffectKind.Trap, "fire", null, null, 25, null, SpellTargets.Single, null)),
        Spell("Fire Shield", "fire", 0, new SpellEffectValues(SpellEffectKind.Ward, "fire", null, null, -80, null, SpellTargets.Single, null)),
        Spell("Fairy", "life", 3, new SpellEffectValues(SpellEffectKind.Heal, "life", 400, 400, null, null, SpellTargets.Single, null)),
        new() {
            Id = "firezilla", Name = "Firezilla", School = "fire", Kind = "trained", Profiles = ["2009"], SourceFile = "test.yaml",
            Values = new SpellValues(SpellPips.X, 0.8, 1, 0, null, [Hit(100, 150)]),
        },
    ];

    [Fact]
    public void ADeckLooksLikeA2009PlayersDeck() {
        var deck = AmbientDeckPlanner.Plan(s_fire, "fire", 25);
        var total = deck.Sum(d => d.Copies);
        Assert.Equal(AmbientDeckPlanner.DeckSize(25), total);
        Assert.DoesNotContain(deck, d => d.Spell.Name is "Fire Shield" or "Firezilla"); // never played by the ally brain
        Assert.Contains(deck, d => d.Spell.Name == "Fireblade");
        Assert.Contains(deck, d => d.Spell.Name == "Fire Trap");
        Assert.Contains(deck, d => d.Spell.Name == "Fairy");
        Assert.All(deck, d => Assert.InRange(d.Copies, 1, d.Spell.School == "fire" ? AmbientDeckPlanner.SchoolCopies : AmbientDeckPlanner.OtherCopies));
        var phoenix = deck.First(d => d.Spell.Name == "Phoenix").Copies;
        Assert.True(phoenix >= 2);
        Assert.True(deck.Where(d => d.Spell.Name is "Fire Cat" or "Fire Elf").Sum(d => d.Copies) < total / 2);
    }

    [Fact]
    public void ANewWizardsDeckIsItsFewSpells() {
        var deck = AmbientDeckPlanner.Plan(s_fire.Take(1), "fire", 1);
        Assert.Single(deck);
        Assert.Equal(AmbientDeckPlanner.SchoolCopies, deck[0].Copies);
        Assert.Empty(AmbientDeckPlanner.Plan([s_fire[7]], "fire", 10)); // a shield alone: nothing it would play
    }

}
