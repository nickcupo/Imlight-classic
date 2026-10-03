using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Game.Zone.Components;
using Xunit;

namespace Imlight.Classic.Tests;

// CLASSIC: per-creature spell decks (classic-data/creatures/creature-decks-2009.yaml).
public sealed class CreatureDecksTests {

    private static CreatureDecks Real() => CreatureDecksLoader.Load(Path.Combine(ClassicDataFixture.Root, "creatures", "creature-decks-2009.yaml"));

    // A stand-in for SpellFactory: every name gets an id from its hash, except the names listed as unknown.
    private static Func<string, uint?> Resolver(params string[] unknown)
        => name => unknown.Contains(name) ? null : (uint) (name.GetHashCode() & 0x7fffffff) + 1;

    [Fact]
    public void TheCanonicalProfileNamesTheFile() {
        Assert.Equal("creatures/creature-decks-2009.yaml", ClassicDataFixture.LoadProfile("late-2009").Rules.CreatureDecks);
        Assert.Equal("creatures/creature-decks-2009.yaml", ClassicDataFixture.LoadProfile("arc1-2009h1").Rules.CreatureDecks);
        Assert.Null(ClassicDataFixture.LoadProfile("dev-unrestricted").Rules.CreatureDecks);
    }

    [Fact]
    public void EveryDeckHasSpellsASourceAndACountableDeck() {
        var decks = Real();
        Assert.True(decks.Count > 300);
        Assert.All(decks.Decks, d => {
            Assert.NotEmpty(d.Spells);
            Assert.StartsWith("https://wizard101.fandom.com/wiki/", d.Source);
            Assert.All(d.Spells, s => Assert.InRange(s.Count, 1, 12));
        });
        Assert.Contains(decks.Decks, d => d.PostCutoff);
        Assert.Contains(decks.Decks, d => !d.PostCutoff);
    }

    [Fact]
    public void MinionsAndMonstrologyCreaturesHaveTheirOwnDecksNotTheirSchoolsDeck() {
        var decks = Real();
        Assert.True(decks.TryGet(35677, out var fireElemental));   // Minion-Fire, the Fire Elemental minion
        Assert.True(decks.TryGet(35679, out var waterElemental));  // Minion-Storm
        Assert.True(decks.TryGet(35101, out var banshee));         // Banshee-WCBoss-L04, a Monstrology creature with its own natural attacks
        Assert.NotEqual(fireElemental.Spells.Select(s => s.Spell), waterElemental.Spells.Select(s => s.Spell));
        Assert.NotEqual(fireElemental.Spells.Select(s => s.Spell), banshee.Spells.Select(s => s.Spell));
        Assert.Contains(fireElemental.Spells, s => s.Spell == "Fire Cat");
        Assert.Contains(banshee.Spells, s => s.Spell.StartsWith("NA Banshee", StringComparison.Ordinal));
        Assert.Contains(banshee.Spells, s => s.Spell == "NA Banshee-Boss-01");
    }

    [Fact]
    public void TheDeckComponentUsesTheFileDeckInFileOrderAndSkipsUnknownSpells() {
        var decks = Real();
        Assert.True(decks.TryGet(35677, out var deck));
        var ids = CombatCreatureDeckComponent.ClassicDeckSpellIds(decks, 35677, Resolver("Fire Elf"));
        Assert.Equal(deck.Spells.Length - 1, ids.Count);
        Assert.Equal(Resolver()("Fire Cat"), ids[0]);
        Assert.DoesNotContain(Resolver()("Fire Elf")!.Value, ids);
    }

    [Fact]
    public void TwoMinionsGetDifferentHandsFromTheComponent() {
        var decks = Real();
        var fire = CombatCreatureDeckComponent.ClassicDeckSpellIds(decks, 35677, Resolver());
        var water = CombatCreatureDeckComponent.ClassicDeckSpellIds(decks, 35679, Resolver());
        Assert.NotEmpty(fire);
        Assert.NotEmpty(water);
        Assert.False(fire.SequenceEqual(water));
    }

    [Fact]
    public void ACreatureWithoutADeckGetsNoneSoItKeepsItsTemplateList() {
        Assert.Empty(CombatCreatureDeckComponent.ClassicDeckSpellIds(Real(), 4000000000u, Resolver()));
        Assert.Empty(CombatCreatureDeckComponent.ClassicDeckSpellIds(CreatureDecks.Empty, 35677, Resolver()));
        // A deck none of whose names resolve is empty too, so the template list is used.
        Assert.Empty(CombatCreatureDeckComponent.ClassicDeckSpellIds(Real(), 35677, Resolver(Real().Decks.First(d => d.Template == 35677).Spells.Select(s => s.Spell).ToArray())));
    }

    [Fact]
    public void ADuplicateTemplateOrSpellIsAnError() {
        var path = Write("""
            kind: creature-decks
            version: 1
            id: creature-decks-test
            profiles: [late-2009]
            license_tag: own
            creatures:
            - {template: 5, name: A, source: 'https://wizard101.fandom.com/wiki/A?oldid=1', source_date: '2010-01-01', post_cutoff: false, spells: [{spell: X, count: 2}, {spell: X, count: 1}]}
            - {template: 5, name: B, source: 'https://wizard101.fandom.com/wiki/B?oldid=2', source_date: '2010-01-01', post_cutoff: false, spells: [{spell: Y, count: 2}]}
            """);
        var ex = Assert.Throws<ClassicDataException>(() => CreatureDecksLoader.Load(path));
        Assert.Contains(ex.Errors, e => e.Message.Contains("listed twice"));
        Assert.True(ex.Errors.Length >= 2);
    }

    [Fact]
    public void ALoadedFileKeepsOrderCountsAndTheCutoffFlag() {
        var path = Write("""
            kind: creature-decks
            version: 1
            id: creature-decks-test
            profiles: [late-2009]
            license_tag: own
            creatures:
            - {template: 5, name: A, source: 'https://wizard101.fandom.com/wiki/A?oldid=1', source_date: '2010-08-01', post_cutoff: true, spells: [{spell: Z, count: 3}, {spell: Y, count: 1}]}
            """);
        var decks = CreatureDecksLoader.Load(path);
        Assert.True(decks.TryGet(5, out var deck));
        Assert.True(deck.PostCutoff);
        Assert.Equal(["Z", "Y"], deck.Spells.Select(s => s.Spell));
        Assert.Equal([3, 1], deck.Spells.Select(s => s.Count));
        Assert.False(decks.TryGet(6, out _));
    }

    [Fact]
    public void AMissingFileIsAnErrorButEmptyHasNoDecks() {
        Assert.Throws<ClassicDataException>(() => CreatureDecksLoader.Load(Path.Combine(Path.GetTempPath(), "w101c-no-such-creature-decks.yaml")));
        Assert.Equal(0, CreatureDecks.Empty.Count);
    }

    private static string Write(string yaml) {
        var dir = Directory.CreateTempSubdirectory("w101c-decks-");
        var path = Path.Combine(dir.FullName, "creature-decks-test.yaml");
        File.WriteAllText(path, yaml);

        return path;
    }

}
