using System.Collections.Generic;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.World;
using Xunit;

namespace Imlight.Classic.Tests;

// CLASSIC: a creature with no deck of its own (summoned minions, Monstrology creatures) takes the client's generic
// deck for its school and rank instead of one starter card from every school.
public sealed class GenericCreatureDeckTests {

    private static Dictionary<string, CreatureSpellbook> Books(params string[] names) {
        var books = new Dictionary<string, CreatureSpellbook>();
        uint id = 1;
        foreach (var name in names) {
            books[name] = new CreatureSpellbook(name, [id++]);
        }
        return books;
    }

    private static readonly Dictionary<string, CreatureSpellbook> s_books =
        Books("Mdeck-D-R2", "Mdeck-D-R4", "Mdeck-D-R7", "Mdeck-D-R3-1", "Mdeck-D-R16-DM-1", "Mdeck-F-R3", "Mdeck-S-R2-AV");

    [Theory]
    [InlineData("Death", 4, "Mdeck-D-R4")]
    [InlineData("Death", 5, "Mdeck-D-R4")]
    [InlineData("Death", 16, "Mdeck-D-R7")]      // special decks (-DM) are never picked
    [InlineData("Death", 1, "Mdeck-D-R2")]       // below every rank: the lowest
    [InlineData("death", 3, "Mdeck-D-R3-1")]
    [InlineData("Fire", 9, "Mdeck-F-R3")]
    public void PicksTheHighestPlainRankAtOrBelow(string school, int rank, string expected)
        => Assert.Equal(expected, CreatureSpellbookCollection.GenericFor(s_books, school, rank)!.DeckName);

    [Theory]
    [InlineData("Storm")]   // only a special deck
    [InlineData("Shadow")]
    [InlineData("")]
    [InlineData(null)]
    public void NoGenericDeckGivesNull(string? school)
        => Assert.Null(CreatureSpellbookCollection.GenericFor(s_books, school!, 5));
}
