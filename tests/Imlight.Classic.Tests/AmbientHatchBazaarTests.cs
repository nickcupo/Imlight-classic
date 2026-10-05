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
 * AMBIENT HATCHING AND BAZAAR TESTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): ambient wizards at the Pet Pavilion and the
 * Bazaar: the switches and default rooms, the pets they own (2009 shop
 * pets, mostly Adult, 24-hour cooldown), the hatch partner reservation
 * (one player at a time, promises, never a busy or young pet), and the
 * Bazaar trader (1-3 trades a visit, ceilings, the real-sale shield, the
 * sell/buy balance and the hourly budget).
 *
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests --filter AmbientHatchBazaarTests
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Akka.Actor;
using Imlight.Classic.Ambient;
using Imlight.Classic.Bazaar;
using Imlight.Classic.Pets;
using Imlight.Common;
using Imlight.CoreLib.Classic.Ambient;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class AmbientHatchBazaarTests {

    public AmbientHatchBazaarTests() {
        // The hatch reservation logs; the logger needs a configuration (a quiet one).
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-ambient-hatch-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        }
        finally {
            File.Delete(config);
        }
    }

    // ---- settings ------------------------------------------------------------------------------

    [Fact]
    public void DefaultZonesAddThePetPavilionAndASmallBazaarCrowd() {
        var settings = AmbientSettings.Parse("4", "", "", "", "");

        Assert.True(settings.Hatching && settings.Bazaar);
        Assert.Contains((AmbientSettings.PetPavilionZone, 4), settings.Zones);
        Assert.Contains((AmbientSettings.BazaarZone, AmbientSettings.BazaarDefaultCount), settings.Zones);
        Assert.Contains((AmbientSettings.BazaarZone, 2), AmbientSettings.Parse("2", "", "", "", "").Zones);
    }

    [Fact]
    public void SwitchesOffLeaveTheirRoomsOut() {
        var settings = AmbientSettings.Parse("4", "", "", "", "", hatching: "false", bazaar: "false");

        Assert.False(settings.Hatching || settings.Bazaar);
        Assert.DoesNotContain(settings.Zones, z => z.Zone == AmbientSettings.PetPavilionZone || z.Zone == AmbientSettings.BazaarZone);
        Assert.False(AmbientSettings.Off.Hatching || AmbientSettings.Off.Bazaar);
    }

    [Fact]
    public void AnExplicitZoneListIsKeptAsWritten() {
        var settings = AmbientSettings.Parse("4", "WizardCity/WC_Hub", "", "", "");

        Assert.Equal([("WizardCity/WC_Hub", 4)], settings.Zones.ToArray());
        Assert.True(settings.Hatching); // the switch is on; the Pavilion just has no wizards unless listed
    }

    [Fact]
    public void PavilionAndBazaarWizardsAreOfEveryLevel() {
        Assert.Equal(((byte) 10, (byte) 45), AmbientIdentity.LevelsFor(AmbientSettings.PetPavilionZone, 50));
        Assert.Equal(((byte) 5, (byte) 35), AmbientIdentity.LevelsFor(AmbientSettings.BazaarZone, 50));
        Assert.Equal(((byte) 10, (byte) 30), AmbientIdentity.LevelsFor(AmbientSettings.PetPavilionZone, 30));
    }

    // ---- pets ----------------------------------------------------------------------------------

    [Fact]
    public void PetsAreTheShopsEightMostlyAdultAndTheSameForASeed() {
        var pets = Enumerable.Range(0, 2000).Select(AmbientPets.For).ToList();

        Assert.All(pets, p => Assert.Contains(p.TemplateId, AmbientPets.ClassicPetTemplates));
        Assert.All(pets, p => Assert.InRange(p.Level, PetRules.Teen, PetRules.Epic));
        Assert.All(pets, p => Assert.InRange(p.TrainedShare, 0.05, 0.95));
        Assert.Equal(AmbientPets.ClassicPetTemplates.Length, pets.Select(p => p.TemplateId).Distinct().Count());
        var adults = pets.Count(p => p.Level == PetRules.Adult);
        Assert.InRange(adults, 1000, 1400);                               // ~60%
        Assert.InRange(pets.Count(p => p.Level == PetRules.Teen), 200, 400); // ~15%, cannot hatch
        Assert.Equal(AmbientPets.For(77), AmbientPets.For(77));
    }

    [Fact]
    public void OnlyAdultPetsOffTheirDailyCooldownHatch() {
        const long now = 1_800_000_000;
        Assert.False(AmbientPets.MayHatch(PetRules.Teen, 0, now));
        Assert.True(AmbientPets.MayHatch(PetRules.Adult, 0, now));
        Assert.True(AmbientPets.MayHatch(PetRules.Epic, 0, now));
        Assert.False(AmbientPets.MayHatch(PetRules.Adult, now - 23 * 3600, now));
        Assert.True(AmbientPets.MayHatch(PetRules.Adult, now - 24 * 3600, now));
    }

    [Fact]
    public void TrainedStatsSitBetweenStartAndMax() {
        Assert.Equal(1, AmbientPets.TrainedStat(1, 240, 0));
        Assert.Equal(240, AmbientPets.TrainedStat(1, 240, 1));
        Assert.Equal(121, AmbientPets.TrainedStat(1, 240, 0.5));
        Assert.Equal(240, AmbientPets.TrainedStat(1, 240, 3));
    }

    [Theory]
    [InlineData("anyone want to hatch?", true)]
    [InlineData("lf hatching partner", true)]
    [InlineData("breed?", true)]
    [InlineData("the hatchery is cool", false)]
    [InlineData("hello", false)]
    public void HatchRequestsAreRecognised(string text, bool asks) => Assert.Equal(asks, AmbientPets.AsksForHatch(text));

    [Fact]
    public void HatchLinesNameThePetAndStage() {
        var line = AmbientPets.Line(AmbientPets.Talk.Offer, 0, "Fire Elf", PetRules.Adult);
        Assert.Equal("anyone want to hatch? my fire elf is adult", line);
        Assert.DoesNotContain("{", AmbientPets.Line(AmbientPets.Talk.Answer, 3, "Imp", PetRules.Ancient));
        Assert.Equal("Fire Elf", AmbientPets.NameOf(100408));
    }

    [Fact]
    public void WaitsAreWithinTheirRanges() {
        var random = new Random(3);
        for (var i = 0; i < 200; i++) {
            Assert.InRange(AmbientPets.Wait(random, AmbientPets.JoinMinSeconds, AmbientPets.JoinMaxSeconds).TotalSeconds, 4, 10);
            Assert.InRange(AmbientPets.Wait(random, AmbientPets.ConfirmMinSeconds, AmbientPets.ConfirmMaxSeconds).TotalSeconds, 3, 8);
        }

        Assert.True(AmbientPets.HoldLimit <= TimeSpan.FromMinutes(2)); // never holds a player's hatch spot for long
    }

    // ---- the hatch partner reservation -----------------------------------------------------------

    private static readonly NameTableSizes s_tables = new(100, 100, 50, 50);

    private sealed class Sink : ReceiveActor {
        public Sink() => ReceiveAny(_ => { });
    }

    /// <summary>A present ambient wizard in the Pavilion whose pet is at <paramref name="level"/>.</summary>
    private static AmbientWizard PavilionWizard(ActorSystem system, ulong charId, int level) {
        var seed = Enumerable.Range(1, 5000).First(s => AmbientPets.For(s).Level == level && s % 97 == (int) (charId % 97));
        var record = AmbientWizardRecord.From(AmbientIdentity.Generate(seed, AmbientSettings.PetPavilionZone, s_tables, (10, 45)), charId);
        var wizard = new AmbientWizard(record, new Wizard { CharId = charId }) {
            Endpoint = system.ActorOf(Props.Create(() => new Sink())),
            Zone = AmbientSettings.PetPavilionZone,
            Present = true,
            Activity = AmbientActivity.Idle,
        };
        AmbientWizards.Register(wizard);
        return wizard;
    }

    private static void WithPavilion(Action<ActorSystem> test) {
        var before = AmbientWizards.Settings;
        var profile = AmbientHatching.ProfileHatches;
        using var system = ActorSystem.Create("ambient-hatch", "akka.actor.provider = local");
        try {
            AmbientWizards.Settings = AmbientSettings.Parse("4", "", "", "", "");
            AmbientHatching.Reset();
            AmbientHatching.ProfileHatches = () => true;
            test(system);
        }
        finally {
            foreach (var wizard in AmbientWizards.All.Where(w => w.Zone == AmbientSettings.PetPavilionZone).ToList()) {
                AmbientWizards.Unregister(wizard);
            }

            AmbientHatching.Reset();
            AmbientHatching.ProfileHatches = profile;
            AmbientWizards.Settings = before;
        }
    }

    [Fact]
    public void APartnerIsReservedForOnePlayerAndFreedWhenThePlayerLeaves() => WithPavilion(system => {
        var adult = PavilionWizard(system, 0xA3B1E00000000201, PetRules.Adult);
        var now = DateTime.UtcNow;

        var first = AmbientHatching.Claim(AmbientSettings.PetPavilionZone, 1001, now);
        Assert.Same(adult, first);
        Assert.True(AmbientHatching.IsBusy(adult.CharId));
        Assert.Null(AmbientHatching.Claim(AmbientSettings.PetPavilionZone, 1002, now)); // never two players at once

        AmbientHatching.Release(adult, 1002, AmbientHatchEnd.PlayerLeft); // not its player: nothing
        Assert.True(AmbientHatching.IsBusy(adult.CharId));
        AmbientHatching.Release(adult, 1001, AmbientHatchEnd.PlayerLeft);
        Assert.False(AmbientHatching.IsBusy(adult.CharId));
        Assert.Same(adult, AmbientHatching.Claim(AmbientSettings.PetPavilionZone, 1002, now));
    });

    [Fact]
    public void YoungPetsCooldownsBusyWizardsAndOtherRoomsAreNeverClaimed() => WithPavilion(system => {
        var teen = PavilionWizard(system, 0xA3B1E00000000202, PetRules.Teen);
        var resting = PavilionWizard(system, 0xA3B1E00000000203, PetRules.Adult);
        resting.Record.PetLastHatchUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;
        var fighting = PavilionWizard(system, 0xA3B1E00000000204, PetRules.Ancient);
        fighting.Activity = AmbientActivity.Fighting;
        var now = DateTime.UtcNow;

        Assert.Null(AmbientHatching.Claim(AmbientSettings.PetPavilionZone, 1001, now));
        Assert.Null(AmbientHatching.Claim("WizardCity/WC_Hub", 1001, now));
        Assert.Null(AmbientHatching.Claim(AmbientSettings.PetPavilionZone, teen.CharId, now)); // an ambient wizard never asks

        fighting.Activity = AmbientActivity.Idle;
        Assert.Same(fighting, AmbientHatching.Claim(AmbientSettings.PetPavilionZone, 1001, now));
    });

    [Fact]
    public void APromisedWizardWaitsForItsPlayer() => WithPavilion(system => {
        var a = PavilionWizard(system, 0xA3B1E00000000205, PetRules.Adult);
        var b = PavilionWizard(system, 0xA3B1E00000000206, PetRules.Adult);
        var now = DateTime.UtcNow;
        AmbientHatching.Promise(2001, b, now);

        Assert.Same(a, AmbientHatching.Claim(AmbientSettings.PetPavilionZone, 2002, now)); // b is held for 2001
        Assert.Same(b, AmbientHatching.Claim(AmbientSettings.PetPavilionZone, 2001, now));
    });

    [Fact]
    public void AnOpenOfferNeverDisplacesAYesAlreadyGiven() => WithPavilion(system => {
        var yes = PavilionWizard(system, 0xA3B1E00000000208, PetRules.Adult);
        var open = PavilionWizard(system, 0xA3B1E00000000209, PetRules.Adult);
        var now = DateTime.UtcNow;
        AmbientHatching.Promise(3001, yes, now);                  // answered the player's "hatch?"
        AmbientHatching.Promise(3001, open, now, replace: false);  // a room-wide offer a moment later

        Assert.Same(yes, AmbientHatching.Claim(AmbientSettings.PetPavilionZone, 3001, now));
    });

    [Fact]
    public void HatchingOffMeansNoPartner() => WithPavilion(system => {
        PavilionWizard(system, 0xA3B1E00000000207, PetRules.Adult);
        AmbientWizards.Settings = AmbientSettings.Parse("4", "", "", "", "", hatching: "false");
        Assert.Null(AmbientHatching.Claim(AmbientSettings.PetPavilionZone, 1001, DateTime.UtcNow));
    });

    // ---- the Bazaar trader -------------------------------------------------------------------------

    private static BazaarRules Rules() => new() {
        Id = "bazaar-test",
        Profiles = ["late-2009"],
        MaxCopies = 100,
        MaxReagentCopies = 500,
        ItemTiers = [new BazaarTier(0, 0.5, 0), new BazaarTier(1, 0.5, 1.5)],
        ReagentTiers = [new BazaarTier(0, 0.9, 0), new BazaarTier(1, 0.9, 5)],
        StockRanges = new Dictionary<BazaarKind, (int, int)> {
            [BazaarKind.Gear] = (1, 4), [BazaarKind.TreasureCard] = (2, 12), [BazaarKind.Reagent] = (10, 60), [BazaarKind.Housing] = (1, 3),
        }.ToFrozenDictionary(),
        Shares = new Dictionary<BazaarKind, double> {
            [BazaarKind.Gear] = 0.5, [BazaarKind.TreasureCard] = 0.2, [BazaarKind.Reagent] = 0.2, [BazaarKind.Housing] = 0.1,
        }.ToFrozenDictionary(),
        PriceJitter = 0.1,
        SourceFile = "test",
    };

    private static List<BazaarCandidate> Pool() {
        var pool = new List<BazaarCandidate>();
        for (ulong i = 0; i < 40; i++) {
            var kind = (BazaarKind) (int) (i % 4);
            pool.Add(new BazaarCandidate(1000 + i, kind, 100));
        }

        return pool;
    }

    [Fact]
    public void AVisitIsOneToThreeTradesOnDifferentItems() {
        var rules = Rules();
        var random = new Random(5);
        for (var i = 0; i < 500; i++) {
            var shelf = new List<BazaarShelfLot> { new(1000, BazaarKind.Gear, 2, 0, false), new(1002, BazaarKind.Reagent, 30, 10, false) };
            var trades = AmbientBazaarTrader.Plan(rules, shelf, Pool(), random);
            Assert.InRange(trades.Count, 1, 3);
            Assert.Equal(trades.Count, trades.Select(t => t.Template).Distinct().Count());
            Assert.All(trades, t => Assert.NotEqual(0, t.Change));
        }
    }

    [Fact]
    public void SalesStopAtTheCeilingAndPurchasesNeverOverdrawOrTakeAShieldedSale() {
        var rules = Rules();
        var random = new Random(9);
        var pool = new List<BazaarCandidate> { new(1, BazaarKind.Gear, 100) };
        for (var i = 0; i < 300; i++) {
            var shelf = new List<BazaarShelfLot> {
                new(1, BazaarKind.Gear, AmbientBazaarTrader.SellCeiling(BazaarKind.Gear), 0, false), // full: no more sales of it
                new(2, BazaarKind.Gear, 1, 0, true),                                                  // a player's fresh sale
                new(3, BazaarKind.Reagent, 4, 0, false),
            };
            foreach (var trade in AmbientBazaarTrader.Plan(rules, shelf, pool, random)) {
                Assert.True(trade.Change < 0, "nothing is left to sell");
                Assert.NotEqual(2UL, trade.Template);
                Assert.True(-trade.Change <= shelf.First(l => l.Template == trade.Template).Copies);
            }
        }
    }

    [Fact]
    public void SellChanceFallsAsPlayersCopiesFillUp() {
        Assert.Equal(0.75, AmbientBazaarTrader.SellChance(0), 3);
        Assert.Equal(0.5, AmbientBazaarTrader.SellChance(AmbientBazaarTrader.PlayerCopyTarget / 2.0), 3);
        Assert.Equal(0.15, AmbientBazaarTrader.SellChance(AmbientBazaarTrader.PlayerCopyTarget * 2.0), 3);
        Assert.Equal(1.0, AmbientBazaarTrader.PlayerCopies([new(1, BazaarKind.Gear, 3, 2, false), new(2, BazaarKind.Reagent, 10, 20, false)]), 3);
    }

    [Fact]
    public void ManyVisitsKeepTheShelfChangingAndNearTheTarget() {
        // A thousand visits (a week at the hourly budget) from an empty shelf: copies come and go, and players' copies
        // settle around the target instead of growing without end or emptying.
        var rules = Rules();
        var random = new Random(11);
        var pool = Pool();
        var held = new Dictionary<ulong, (BazaarKind Kind, int Copies)>();
        var appeared = new HashSet<ulong>();
        var disappeared = new HashSet<ulong>();
        var samples = new List<double>();
        for (var visit = 0; visit < 1000; visit++) {
            var shelf = held.Where(p => p.Value.Copies > 0).Select(p => new BazaarShelfLot(p.Key, p.Value.Kind, p.Value.Copies, 0, false)).ToList();
            foreach (var trade in AmbientBazaarTrader.Plan(rules, shelf, pool, random)) {
                var now = (held.TryGetValue(trade.Template, out var lot) ? lot.Copies : 0) + trade.Change;
                Assert.True(now >= 0);
                held[trade.Template] = (trade.Kind, now);
                (now > 0 ? appeared : disappeared).Add(trade.Template);
            }

            if (visit >= 300) {
                samples.Add(AmbientBazaarTrader.PlayerCopies(held.Select(p => new BazaarShelfLot(p.Key, p.Value.Kind, p.Value.Copies, 0, false))));
            }
        }

        Assert.True(appeared.Count > 20);
        Assert.True(disappeared.Count > 5);
        Assert.InRange(samples.Average(), AmbientBazaarTrader.PlayerCopyTarget * 0.5, AmbientBazaarTrader.PlayerCopyTarget * 1.5);
        Assert.True(samples.Max() < AmbientBazaarTrader.PlayerCopyTarget * 2);
    }

    [Fact]
    public void TheHourlyBudgetCapsTradesAndRefills() {
        var budget = new AmbientTradeBudget(5);
        var start = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(3, budget.Take(3, start));
        Assert.Equal(2, budget.Take(3, start.AddMinutes(10)));
        Assert.Equal(0, budget.Take(1, start.AddMinutes(59)));
        Assert.Equal(3, budget.Take(3, start.AddMinutes(61)));
    }

}
