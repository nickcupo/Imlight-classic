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
 * ARENA RULES TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The 2009 arena's pure rules (ids, join checks, Quick Join, rating, ranks,
 * tickets, flee = loss), the real arena file, and the open circles' new
 * side-by-arrival rule (the housing dueling sigil).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imlight.Classic.Pvp;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ArenaRulesTests {

    private static ArenaConfig Real() => ArenaLoader.Load(Path.Combine(ClassicDataFixture.Root, "pvp", "arena-2009.yaml"));

    [Fact]
    public void IdsAreTheClientsStringHashes() {
        // client r806919: the PvPWindow compares MSG_PREPVPKIOSK.TournamentNameID with the hash of "PvPPractice".
        Assert.Equal(1176822080u, ArenaRules.Hash("PvPPractice"));
        Assert.Equal(1001972036u, ArenaRules.Hash("PvPSanctioned"));
        Assert.Equal(0u, ArenaRules.Hash(""));
        Assert.Equal("PvPPractice3v3Match", ArenaRules.MatchName("PvPPractice", 3));
        for (var size = 1; size <= 4; size++) {
            Assert.Equal(size, ArenaRules.TeamSizeOf("PvPSanctioned", ArenaRules.Hash($"PvPSanctioned{size}v{size}Match")));
        }

        Assert.Equal(0, ArenaRules.TeamSizeOf("PvPPractice", ArenaRules.Hash("PvPPracticeFFAMatch")));
    }

    [Theory]
    [InlineData(true, 0, 2, 10, 0, 0, false, false, ArenaErrors.MatchStarted)]
    [InlineData(false, 2, 2, 10, 0, 0, false, false, ArenaErrors.SideIsFull)]
    [InlineData(false, 1, 2, 9, 10, 0, false, false, ArenaErrors.SideNotAllowed)]     // below the minimum level
    [InlineData(false, 1, 2, 21, 0, 20, false, false, ArenaErrors.SideNotAllowed)]    // above the maximum level
    [InlineData(false, 1, 2, 15, 10, 20, true, false, ArenaErrors.SideNotAllowed)]    // friends only, not a friend
    [InlineData(false, 1, 2, 15, 10, 20, true, true, null)]
    [InlineData(false, 0, 1, 1, 0, 0, false, false, null)]
    public void JoinChecks(bool started, int side, int size, int level, int min, int max, bool friendsOnly, bool friend, string? error)
        => Assert.Equal(error, ArenaRules.JoinError(started, side, size, level, min, max, friendsOnly, friend));

    [Theory]
    [InlineData(0, 0, 2, 0)]
    [InlineData(1, 0, 2, 1)]
    [InlineData(2, 1, 2, 1)]
    [InlineData(1, 2, 2, 0)]
    [InlineData(2, 2, 2, -1)]
    [InlineData(1, 1, 1, -1)]
    public void QuickJoinTakesAnyOpenSpot(int side0, int side1, int size, int expected)
        => Assert.Equal(expected, ArenaRules.QuickJoinSide(side0, side1, size));

    [Fact]
    public void RatingIsEloAndNeverStandsStill() {
        Assert.Equal(16, ArenaRules.RatingChange(500, 500, won: true, kFactor: 32));
        Assert.Equal(-16, ArenaRules.RatingChange(500, 500, won: false, kFactor: 32));
        Assert.True(ArenaRules.RatingChange(900, 500, won: true, kFactor: 32) is >= 1 and < 5);   // beating a far lower side
        Assert.True(ArenaRules.RatingChange(500, 900, won: true, kFactor: 32) > 28);             // an upset
        Assert.Equal(1, ArenaRules.RatingChange(2000, 0, won: true, kFactor: 32));               // a win always gains
        var after = ArenaRules.After(new ArenaStanding(10, 0, 0), -16, won: false, minRating: 0);
        Assert.Equal(new ArenaStanding(0, 0, 1), after);
    }

    [Fact]
    public void RanksTicketsAndFleeing() {
        var config = Real();
        Assert.Equal("Private", ArenaRules.RankOf(500, config.Ranks));
        Assert.Equal("Sergeant", ArenaRules.RankOf(550, config.Ranks));
        Assert.Equal("Warlord", ArenaRules.RankOf(950, config.Ranks));
        Assert.Equal(10, ArenaRules.Tickets(ArenaKind.Ranked, won: true, config));   // youngwizard 2009-08-31
        Assert.Equal(3, ArenaRules.Tickets(ArenaKind.Ranked, won: false, config));
        Assert.Equal(0, ArenaRules.Tickets(ArenaKind.Practice, won: true, config));
        Assert.False(ArenaRules.CountsAsWin(sideWon: true, fled: true));             // Duels oldid 4913: flee = lose
        Assert.True(ArenaRules.CountsAsWin(sideWon: true, fled: false));
    }

    [Fact]
    public void RealArenaFileLoads() {
        var config = Real();
        Assert.Contains("late-2009", config.Profiles);
        Assert.Equal("WizardCity/WC_Duel_Arena", config.HallZone);
        Assert.Equal((100587u, 100586u), (config.PracticeKiosk, config.RankedKiosk));
        Assert.Equal(("PvPPractice", "PvPSanctioned"), (config.PracticeTournament, config.RankedTournament));
        Assert.Equal(500, config.StartRating);
        Assert.Contains(config.Arenas, a => a.Zone == "WizardCity/DS_Arena");   // October 2009 notes, oldid 48297
        Assert.Contains(config.Arenas, a => a.Zone == "WizardCity/GH_Arena");
        Assert.Equal(config.Arenas.Length, config.Arenas.Select(a => a.Zone).Distinct().Count());
        Assert.True(config.Ranks.Select(r => r.MinRating).SequenceEqual(config.Ranks.Select(r => r.MinRating).Order()));
    }

    [Fact]
    public void TicketVendorsSellThe2009Stock() {
        var config = Real();
        var diego = config.TicketVendors.Single(v => v.Npc == 38226);
        Assert.Equal("WizardShops_00000018", diego.Title);              // "Diego's Deluxe Raiments"
        Assert.Equal(105, diego.Items.Count(i => i.Rank is null && i.Template is >= 100477 and <= 100582));   // 5 tiers x 7 x 3
        Assert.Equal("Warlord", diego.Items.Single(i => i.Template == 164174).Rank);   // Magma Colossus
        var roland = config.TicketVendors.Single(v => v.Npc == 164327);
        Assert.Equal(10, roland.Items.Length);
        Assert.Equal("Sergeant", roland.Items.Single(i => i.Template == 164213).Rank);  // Ribbon Stand
        // Diego's templates carry the 2009 prices: charged as the template says (the shop window shows the template's).
        Assert.All(diego.Items, i => Assert.Null(i.Price));
        // Roland's housing templates carry later prices: the 2009 ones are charged (wiki oldid 36841, 2009-07-25), and the
        // client step "tickets" (tools/mac/classic_tickets.py) writes the same numbers into the client's templates.
        var expected = new Dictionary<uint, int> {
            [160592] = 625, [160333] = 500, [82862] = 1200, [160274] = 1500, [164202] = 200, [164211] = 200, [164213] = 200,
            [160655] = 485, [160238] = 750, [160823] = 1500,
        };
        Assert.Equal(expected, roland.Items.ToDictionary(i => i.Template, i => i.Price!.Value));
    }

    [Fact]
    public void AnExplicitPriceWinsOverThe2009Price() {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"arena-price-{Guid.NewGuid():N}")).FullName;
        var path = Path.Combine(dir, "arena-2009.yaml");   // the loader wants the id to match the file name
        var text = File.ReadAllText(Path.Combine(ClassicDataFixture.Root, "pvp", "arena-2009.yaml"))
            .Replace("{template: 160592, name: 'Ninja Sword Rack', price_2009: 625}",
                "{template: 160592, name: 'Ninja Sword Rack', price: 7, price_2009: 625}");
        File.WriteAllText(path, text);
        try {
            var roland = ArenaLoader.Load(path).TicketVendors.Single(v => v.Npc == 164327);
            Assert.Equal(7, roland.Items.Single(i => i.Template == 160592).Price);
            Assert.Equal(500, roland.Items.Single(i => i.Template == 160333).Price);
        }
        finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(500, 100, 500, null, null)]
    [InlineData(99, 100, 500, null, "tickets")]
    [InlineData(1000, 200, 549, "Sergeant", "rank")]
    [InlineData(1000, 200, 550, "Sergeant", null)]
    [InlineData(1500, 1500, 950, "Warlord", null)]
    public void TicketPurchases(int tickets, int price, int rating, string? rank, string? error)
        => Assert.Equal(error, ArenaRules.TicketPurchaseError(tickets, price, rating, ArenaRules.MinRatingOf(rank, Real().Ranks)));

    [Fact]
    public void OpenCirclesAreNoLongerTheClassicArena() {
        // Owner ruling 2026-10-05: the walk-in circles were the housing dueling sigil; the classic profiles use matches.
        var open = OpenPvpLoader.Load(Path.Combine(ClassicDataFixture.Root, "pvp", "open-pvp-2009.yaml"));
        Assert.DoesNotContain("late-2009", open.Profiles);
        Assert.DoesNotContain("arc1-2009h1", open.Profiles);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(2, 1, 1)]
    [InlineData(4, 3, 1)]
    [InlineData(4, 4, -1)]
    [InlineData(3, 4, 0)]
    public void HousingSigilSidesAlternateByArrival(int seated0, int seated1, int expected)
        => Assert.Equal(expected, OpenPvpRules.ChooseSideByArrival(seated0, seated1));

}
