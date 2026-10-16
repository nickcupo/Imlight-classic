using System.IO;
using System.Linq;
using Imlight.CoreLib.Classic.Housing;
using Xunit;

namespace Imlight.Classic.Tests;

// CLASSIC: the owner's pending housing rulings (resale value, attic capacity, attic ownership) sit behind one data
// switch, houses-october-2010.yaml pending_rulings.enabled. Golden checks: with the switch off (as shipped) today's
// live behaviour is unchanged; with it on, the recommended values apply to the October family only.
[Collection(nameof(ClassicRuntimeCollection))]
public sealed class HousePendingRulingsTests {
    private const string October = "october-2010-arc1", Celestia = "celestia-2010";
    private static string RealPath => Path.Combine(ClassicDataFixture.Root, "housing", "houses-october-2010.yaml");

    private static string SwitchedOn(TempClassicData data, string owner = "wizard") {
        var source = File.ReadAllText(RealPath);
        Assert.Contains("pending_rulings:\n  enabled: false", source);
        var path = Path.Combine(data.Root, "houses-october-2010.yaml");
        File.WriteAllText(path, source.Replace("pending_rulings:\n  enabled: false", "pending_rulings:\n  enabled: true")
            .Replace("attic_owner: wizard", $"attic_owner: {owner}"));
        return path;
    }

    [Fact]
    public void AsShippedTheSwitchIsOffAndNothingChanges() {
        var catalog = HouseCatalog.Load(RealPath, October, out var rulings);
        Assert.False(rulings.Enabled);
        Assert.Equal(0, rulings.AtticCapacity);
        Assert.Equal(17, catalog.Count);
        Assert.All(catalog.Values, house => {
            Assert.True(house.Complete, house.Name);
            Assert.Equal(15, house.MinimumLevel); // purchase stays open at the dated level 15
            Assert.False(house.ResaleAllowed, house.Name); // resale stays closed
        });
    }

    [Fact]
    public void TheRuntimeAtticStaysClosedWithoutTheSwitch() {
        HouseCatalog.TestRulings.Value = null;
        Assert.Equal(0, HousingRules.ApprovedAtticCapacity);
        // Outside an initialized October runtime (the test host) the attic and resale remain closed.
        Assert.Equal(0, HousingRules.AtticCapacity);
        HouseCatalog.TestRulings.Value = HousePendingRulings.Off;
        Assert.Equal(0, HousingRules.AtticCapacity);
    }

    [Fact]
    public void SwitchedOnTheRecommendedValuesApply() {
        using var data = new TempClassicData();
        var catalog = HouseCatalog.Load(SwitchedOn(data), October, out var rulings);
        Assert.True(rulings.Enabled);
        Assert.Equal(100, rulings.AtticCapacity); // dated 2009-05-22 and 2010-09-23
        Assert.Equal("wizard", rulings.AtticOwner);
        Assert.All(catalog.Values, house => Assert.True(house.ResaleAllowed, house.Name));
        Assert.Equal(2001, catalog[160943].ResaleGold); // Imperial Palace: dated 2,001 gold (oldid110495, 2010-10-05)
        Assert.Equal(401, catalog[160431].ResaleGold); // Wooded Cottage 8,000 -> 401 by the same formula
        Assert.All(catalog.Values.Where(h => h.Crowns > 0), h => Assert.Equal(5001, h.ResaleGold)); // school houses
        Assert.All(catalog.Values, h => Assert.InRange(h.ResaleGold, 1, h.Gold / 10)); // "a considerable loss"
        // Prices, capacities and the level are untouched by the switch.
        var off = HouseCatalog.Load(RealPath, October);
        Assert.All(catalog.Values, h => Assert.Equal(off[h.TemplateId] with { ResaleAllowed = true }, h));
    }

    [Fact]
    public void TheSwitchReachesEveryOctoberFamilyProfileButNoOlderOne() {
        _ = ClassicDataFixture.LoadProfile(Celestia);
        _ = ClassicDataFixture.LoadProfile(October);
        using var data = new TempClassicData();
        var path = SwitchedOn(data);
        Assert.Equal(HouseCatalog.Load(path, October).Values.OrderBy(h => h.TemplateId),
            HouseCatalog.Load(path, Celestia).Values.OrderBy(h => h.TemplateId));
        foreach (var older in new[] { "late-2009", "arc1-2009h1", "dev-unrestricted" }) {
            Assert.Empty(HouseCatalog.Load(path, older, out var rulings));
            Assert.False(rulings.Enabled);
        }
    }

    [Fact]
    public void AnAccountWideAtticCannotBeSwitchedOn() {
        using var data = new TempClassicData();
        Assert.Throws<InvalidDataException>(() => HouseCatalog.Load(SwitchedOn(data, "account"), October));
    }

    [Fact]
    public void AResaleValueAboveThePriceIsRefused() {
        using var data = new TempClassicData();
        var path = Path.Combine(data.Root, "houses-october-2010.yaml");
        File.WriteAllText(path, File.ReadAllText(RealPath).Replace("resale_gold: 401", "resale_gold: 9000"));
        Assert.Throws<InvalidDataException>(() => HouseCatalog.Load(path, October));
    }
}
