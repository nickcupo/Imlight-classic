// CLASSIC: wintertusk-2011 (May 2011, work package A'): the profile, its family checks, the stun-shield count, the Stun Block
// move to Diego, the October Cloak stand-in staying out of the Celestia family, the closed Wintertusk world, and the
// permanent Wild Bolt lock (owner decision D4) in every profile.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imlight.Classic.Pvp;
using Imlight.Classic.Spells;
using Imlight.Classic.Travel;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.WizardData.Models.World;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class WintertuskProfileTests {

    private const string Wintertusk = ClassicProfiles.Wintertusk2011;
    private const string Celestia = ClassicProfiles.Celestia2010;
    private const string October = ClassicProfiles.October2010;

    private static ClassicSpellBook Book() => ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "spells"));

    private static void Use(string profile) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(profile));
    }

    public static IEnumerable<object[]> EveryProfile()
        => ClassicProfileLoader.LoadAll(ClassicDataFixture.ProfilesPath).Select(p => new object[] { p.Id });

    [Fact]
    public void WintertuskExtendsCelestiaWithinTheMay2011CutoffAndIsNotCanonical() {
        var profile = ClassicDataFixture.LoadProfile(Wintertusk);

        Assert.Equal(ProfileStatus.Optional, profile.Status);
        Assert.Equal(Celestia, profile.Extends);
        Assert.Equal(new[] { Wintertusk, Celestia, October, "late-2009" }, profile.Lineage);
        Assert.True(profile.InheritsMembership);
        Assert.Equal(new[] { Wintertusk, Celestia, October }, profile.MembershipIds);
        Assert.Equal(new DateOnly(2011, 5, 31), profile.Cutoff);
        Assert.Equal(new DateOnly(2011, 5, 11), profile.ZoneContentCutoff);
        Assert.Equal(60, profile.LevelCap);
        Assert.Equal(new[] { "wizard_city", "krokotopia", "marleybone", "mooshu", "dragonspyre", "grizzleheim", "celestia", "wintertusk" },
            profile.Worlds!.Value);
        // Inherited from celestia-2010 unchanged.
        var celestia = ClassicDataFixture.LoadProfile(Celestia);
        Assert.Equal(celestia.Rules.XpTable, profile.Rules.XpTable);
        Assert.Equal(celestia.Rules.PlayerHealth, profile.Rules.PlayerHealth);
        Assert.Equal(celestia.Rules.LevelStats, profile.Rules.LevelStats);
        Assert.Equal(celestia.Rules.Potions, profile.Rules.Potions);
        Assert.Equal(celestia.DisabledQuests, profile.DisabledQuests);
        Assert.True(profile.Features.IsEnabled(ClassicFeatures.CriticalAndBlock)); // inherited from celestia-2010 (claude/celestia-b)
        // Crafting stays off (owner); gardening is on (stage K1 built, claude/house-garden).
        Assert.False(profile.Features.IsEnabled(ClassicFeatures.Crafting));
        Assert.True(profile.Features.IsEnabled(ClassicFeatures.Gardening));
        // Package I: Wintertusk is not a Spiral Door world, so it has no world_unlocks rule (the quest chain is the way in).
        Assert.False(profile.WorldUnlocks.ContainsKey("wintertusk"));
        // The Celestia entry rule is inherited.
        Assert.Equal("WC-MAIN-C01-013", Assert.Single(profile.WorldUnlocks["celestia"].AnyOf).Name);
    }

    [Fact]
    public void FamilyChecksTreatWintertuskAsOctoberAndCelestiaOrLater() {
        _ = ClassicDataFixture.LoadProfile(Wintertusk);
        _ = ClassicDataFixture.LoadProfile(Celestia);
        _ = ClassicDataFixture.LoadProfile(October);

        Assert.True(ClassicProfiles.IsOctoberFamily(Wintertusk));
        Assert.True(ClassicProfiles.IsCelestiaOrLater(Wintertusk));
        Assert.True(ClassicProfiles.IsCelestiaOrLater(Celestia));
        Assert.False(ClassicProfiles.IsCelestiaOrLater(October));
        Assert.False(ClassicProfiles.IsCelestiaOrLater("late-2009"));
        Assert.True(ClassicProfiles.IsWintertuskFamily(Wintertusk));
        Assert.False(ClassicProfiles.IsWintertuskFamily(Celestia));
        Assert.False(ClassicProfiles.IsWintertuskFamily((string?) null));
        Assert.Equal(60, ClassicProfiles.MaxLevelOf(Wintertusk));
        // Data that lists a parent applies to the child; data listing only the child never reaches the parents.
        Assert.True(ClassicProfiles.Lists([October], Wintertusk));
        Assert.True(ClassicProfiles.Lists([Celestia], Wintertusk));
        Assert.False(ClassicProfiles.Lists(["late-2009"], Wintertusk));
        Assert.False(ClassicProfiles.Lists([Wintertusk], Celestia));
    }

    [Fact]
    public void WintertuskZonesOpenOnlyInWintertuskWithoutASpiralDoorEntry() {
        var rules = ClassicDataFixture.RealRules(Wintertusk);

        // Package I: no Wintertusk hub key in either client; Hrundle Fjord is a Grizzleheim hub zone.
        Assert.False(rules.IsHubKeyAllowed("Wintertusk"));
        var zone = rules.IsZoneAllowed("Grizzleheim/GH_HFjord/GH_Nordrilund");
        Assert.True(zone.Allowed);
        Assert.True(rules.IsZoneAllowed("Grizzleheim/GH_HFjord/GH_HFjord").Allowed);
        // Grizzleheim itself is unaffected; the older profiles do not list the world at all.
        Assert.True(rules.IsZoneAllowed("Grizzleheim/GH_MainHub").Allowed);
        Assert.False(ClassicDataFixture.RealRules(Celestia).IsHubKeyAllowed("Wintertusk"));
        Assert.False(ClassicDataFixture.RealRules(Celestia).IsZoneAllowed("Grizzleheim/GH_HFjord/GH_Nordrilund").Allowed);
        Assert.False(ClassicDataFixture.RealRules(October).IsZoneAllowed("Grizzleheim/GH_HFjord/GH_Nordrilund").Allowed);
        Assert.Equal(60, rules.EffectiveMaxLevel(180));
    }

    [Fact]
    public void StunShieldsAreOnePerStunInPveAndTheOpposingTeamSizeInPvpOnlyFromWintertusk() {
        try {
            Use(Wintertusk);
            Assert.Equal(1, ClassicOctoberRules.AutomaticStunBlocks(pvp: false, opposingPlayers: 4));
            Assert.Equal(1, ClassicOctoberRules.AutomaticStunBlocks(pvp: true, opposingPlayers: 1));
            Assert.Equal(2, ClassicOctoberRules.AutomaticStunBlocks(pvp: true, opposingPlayers: 2));
            Assert.Equal(3, ClassicOctoberRules.AutomaticStunBlocks(pvp: true, opposingPlayers: 3));
            Assert.Equal(4, ClassicOctoberRules.AutomaticStunBlocks(pvp: true, opposingPlayers: 4));
            Assert.Equal(1, ClassicOctoberRules.AutomaticStunBlocks(pvp: true, opposingPlayers: 0)); // never fewer than one

            Use(Celestia);
            Assert.Equal(4, ClassicOctoberRules.AutomaticStunBlocks(pvp: true, opposingPlayers: 1));
            Assert.Equal(1, ClassicOctoberRules.AutomaticStunBlocks(pvp: false, opposingPlayers: 4));
            Use(October);
            Assert.Equal(4, ClassicOctoberRules.AutomaticStunBlocks(pvp: true, opposingPlayers: 2));
            Use("late-2009");
            Assert.Equal(1, ClassicOctoberRules.AutomaticStunBlocks(pvp: true, opposingPlayers: 4));
        }
        finally {
            ClassicRuntime.ResetForTests();
        }
    }

    [Fact]
    public void StunBlockIsDiegosWithTwoShieldsOnlyInWintertusk() {
        var book = Book();
        var record = book.FindByTemplate("Spells/Stun Block.xml")!;
        foreach (var id in new[] { "late-2009", "arc1-2009h1", October, Celestia }) {
            if (!record.IsInProfile(id)) {
                continue;
            }

            var values = record.ValuesFor(ClassicDataFixture.LoadProfile(id).Lineage);
            Assert.Equal("Sabrina Greenstar", values.Trainer);
            Assert.Single(values.Effects);
        }

        var may = record.ValuesFor(ClassicDataFixture.LoadProfile(Wintertusk).Lineage);
        Assert.True(record.IsInProfile(Wintertusk));
        Assert.Equal("Diego the Duelmaster", may.Trainer);
        Assert.Equal(2, may.Effects.Length);
        Assert.Equal(SpellPips.Of(0), may.Pips);
        Assert.Equal(1, may.TrainingPoints);
    }

    [Fact]
    public void StunBlockStockMovesFromSabrinaToDiegoIdempotently() {
        var record = Book().FindByTemplate("Spells/Stun Block.xml")!;
        var values = record.ValuesFor(ClassicDataFixture.LoadProfile(Wintertusk).Lineage);
        var sabrina = new NPCSpellInventory { TemplateID = 38210, Spells = [new NPCSpellEntry { TemplateID = 777 }, new NPCSpellEntry { TemplateID = 5 }] };
        var diego = new NPCSpellInventory { TemplateID = 38226, Spells = [new NPCSpellEntry { TemplateID = 9 }] };

        for (var pass = 0; pass < 2; pass++) {
            var (newSabrina, newDiego) = ClassicTrainerInventories.StunBlockStock(sabrina, diego, 777, values);
            Assert.DoesNotContain(newSabrina!.Spells, s => s.TemplateID == 777);
            Assert.Contains(newSabrina.Spells, s => s.TemplateID == 5);
            Assert.Equal(1, newDiego.Spells.Count(s => s.TemplateID == 777));
            Assert.Contains(newDiego.Spells, s => s.TemplateID == 9);
            sabrina = newSabrina;
            diego = newDiego;
        }
    }

    [Fact]
    public void DiegoRequiresPrivateForStunBlockInWintertuskAndTheOctoberCloakStandInIsOutOfTheCelestiaFamily() {
        var arena = ArenaLoader.Load(Path.Combine(ClassicDataFixture.Root, "pvp", "arena-2009.yaml"));
        var private_ = ArenaRules.MinRatingOf("Private", arena.Ranks);
        const ulong stunBlock = 424242;
        try {
            Use(Wintertusk);
            ClassicOctoberTraining.StunBlock = stunBlock;
            Assert.True(ClassicOctoberTraining.Applies(ClassicOctoberTraining.Diego, stunBlock));
            Assert.False(ClassicOctoberTraining.CanTrain(ClassicOctoberTraining.Diego, stunBlock, 1, private_ - 1, arena));
            Assert.True(ClassicOctoberTraining.CanTrain(ClassicOctoberTraining.Diego, stunBlock, 1, private_, arena));
            Assert.True(ClassicOctoberTraining.CanTrain(38210, stunBlock, 1, 0, arena)); // other trainers are not gated
            // Owner ruling 2026-10-16: no Private-rank Cloak at Diego from celestia-2010 on.
            Assert.False(ClassicOctoberTraining.Applies(ClassicOctoberTraining.Diego, ClassicOctoberTraining.Cloak));

            Use(Celestia);
            Assert.False(ClassicOctoberTraining.Applies(ClassicOctoberTraining.Diego, stunBlock));
            Assert.False(ClassicOctoberTraining.Applies(ClassicOctoberTraining.Diego, ClassicOctoberTraining.Cloak));

            Use(October);
            Assert.True(ClassicOctoberTraining.Applies(ClassicOctoberTraining.Diego, ClassicOctoberTraining.Cloak));
        }
        finally {
            ClassicOctoberTraining.StunBlock = 0;
            ClassicRuntime.ResetForTests();
        }
    }

    [Fact]
    public void NoSpellValueChangesExceptStunBlockComparedWithCelestia() {
        // Owner ruling 2026-10-16: the May 2011 spell-value changes are not taken. The one exception (owner, same day: "new
        // dark pact and old insane bolt") is Insane Bolt, a scoped wintertusk-2011 record that celestia-2010 never applies.
        var book = Book();
        var celestia = new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile(Celestia));
        var may = new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile(Wintertusk));
        var insaneBolt = book.Records.Single(r => r.Id == "spell.storm.insane_bolt");
        Assert.True(may.AppliesHere(insaneBolt));
        Assert.False(celestia.AppliesHere(insaneBolt));
        Assert.Equal(["spell.storm.insane_bolt"], book.Records.Where(r => r.Scoped).Select(r => r.Id));
        foreach (var record in book.Records.Where(r => r.Id is not ("spell.ice.stun_block" or "spell.storm.insane_bolt"))) {
            Assert.Equal(record.IsInProfile(Celestia), record.IsInProfile(Wintertusk));
            Assert.Equal(celestia.ValuesOf(record), may.ValuesOf(record));
            Assert.DoesNotContain(Wintertusk, record.ProfileValues.Keys);
        }
    }

    [Theory]
    [MemberData(nameof(EveryProfile))]
    public void WildBoltIsTenPercentAndFlatThousandInEveryProfileForever(string profileId) {
        var profile = ClassicDataFixture.LoadProfile(profileId);
        var book = Book();
        var overrides = new ClassicSpellOverrides(book, profile);
        var records = book.Records.Where(r => r.Id == "spell.storm.wild_bolt").ToList();
        var record = Assert.Single(records);
        // No profile may carry a Wild Bolt override at all: the owner's lock lives in the base values.
        Assert.Empty(record.ProfileValues);
        var values = overrides.ValuesOf(record);
        Assert.Equal(SpellPips.Of(2), values.Pips);
        Assert.Equal(0.1, values.Accuracy);
        var effect = Assert.Single(values.Effects);
        Assert.Equal(1000, effect.Min);
        Assert.Equal(1000, effect.Max);
        Assert.Empty(effect.Outcomes);
    }

}
