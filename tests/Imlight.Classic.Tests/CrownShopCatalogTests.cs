using System.IO;
using System.Linq;
using Imlight.Classic.Rules;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class CrownShopCatalogTests {

    private static CrownShopCatalog Real() => CrownShopCatalogLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "crown-shop-2009.yaml"));

    [Fact]
    public void Late2009OffersThe2009MountsAndHenchmen() {
        var offered = Real().Offered(ClassicDataFixture.RealRules("late-2009").IsFeatureEnabled);

        var pony = offered[191237];   // Chestnut Pony (PERM), Mounts page oldid 68110
        Assert.Equal((5000, 50000, CrownShopCategories.PermanentMounts), (pony.Crowns, pony.Gold, pony.Category));
        var ponyDay = offered[191222];   // Chestnut Pony (1 Day): gold only
        Assert.Equal((0, 2000, 1), (ponyDay.Crowns, ponyDay.Gold, ponyDay.RentalDays));
        Assert.Contains(offered.Values, item => item.Name == "Seraph Wings");
        Assert.All(offered.Values.Where(item => item.Category == CrownShopCategories.Henchmen), item => Assert.True(item.CombatOnly));
    }

    [Fact]
    public void Arc1HasNoMountsOrHenchmen()
        => Assert.Empty(Real().Offered(ClassicDataFixture.RealRules("arc1-2009h1").IsFeatureEnabled));

    [Fact]
    public void TheCanonicalProfileNamesTheCrownShop()
        => Assert.Equal("rules/crown-shop-2009.yaml", ClassicDataFixture.LoadProfile("late-2009").Rules.CrownShop);

}
