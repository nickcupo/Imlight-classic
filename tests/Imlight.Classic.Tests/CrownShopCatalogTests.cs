using System.IO;
using System.Linq;
using Imlight.Classic.Rules;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class CrownShopCatalogTests {

    public CrownShopCatalogTests() {
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-crownshop-tests.log")}\n");
            Imlight.Common.ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
    }

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
        => Assert.DoesNotContain(Real().Offered(ClassicDataFixture.RealRules("arc1-2009h1").IsFeatureEnabled).Values,
            item => item.Category is CrownShopCategories.PermanentMounts or CrownShopCategories.RentalMounts
                or CrownShopCategories.Henchmen);

    [Fact]
    public void TheCatalogSerializesForTheClientAndReadsBack() {
        var offered = Real().Offered(ClassicDataFixture.RealRules("late-2009").IsFeatureEnabled);
        var data = Imlight.CoreLib.Game.Services.CrownShopService.SerializeCatalog(offered.Values);
        Assert.True(data.Length > 0);

        var serializer = new Imcodec.ObjectProperty.ObjectSerializer(Versionable: false,
            Behaviors: Imcodec.ObjectProperty.SerializerFlags.SerializeFlags | Imcodec.ObjectProperty.SerializerFlags.Compress);
        Assert.True(serializer.Deserialize<Imcodec.ObjectProperty.TypeCache.CrownShopData>(data,
            Imcodec.ObjectProperty.PropertyFlags.Prop_Save | Imcodec.ObjectProperty.PropertyFlags.Prop_Public, out var shop));
        Assert.Equal(offered.Count, shop.m_items.Count);
        Assert.Contains(shop.m_items, item => (ulong) item.m_itemTemplateId == 191237 && item.m_crownsCost == 5000);
    }

    [Fact]
    public void TheCanonicalProfileNamesTheCrownShop()
        => Assert.Equal("rules/crown-shop-2009.yaml", ClassicDataFixture.LoadProfile("late-2009").Rules.CrownShop);

}
