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
    public void OctoberOffersExactlyTheTenVerifiedElixirsAndMasteryUsesOriginal9950Price() {
        var rules = ClassicDataFixture.RealRules("october-2010-arc1");
        var offered = Imlight.CoreLib.Game.Services.CrownShopService.ForProfile(Real().Offered(rules.IsFeatureEnabled, rules.Profile.Id).Values, rules).ToArray();
        var elixirs = offered.Where(i => i.Category == CrownShopCategories.Elixirs).OrderBy(i => i.Template).ToArray();
        Assert.Equal(16, elixirs.Length); // CLASSIC: the ten generic products and six school Battle elixirs (2026-10-08)
        Assert.Equal(new[] { 200, 350, 200, 350, 225, 375, 150, 225, 100, 175, 300, 300, 300, 300, 300, 300 }, elixirs.Select(i => i.Crowns));
        Assert.Equal(Enumerable.Range(191099, 10).Concat(Enumerable.Range(191117, 6)).Select(i => (ulong)i), elixirs.Select(i => i.Template));
        Assert.All(elixirs, i => Assert.Equal(0, i.Gold));
        var mastery = offered.Where(i => i.Template >= 468162 && i.Template <= 468168).ToArray();
        Assert.Equal(7, mastery.Length);
        Assert.All(mastery, i => Assert.Equal(9950, i.Crowns));
        foreach (var profile in new[] { "late-2009", "arc1-2009h1", "dev-unrestricted" }) {
            var older = ClassicDataFixture.RealRules(profile);
            Assert.DoesNotContain(Imlight.CoreLib.Game.Services.CrownShopService.ForProfile(Real().Offered(older.IsFeatureEnabled, older.Profile.Id).Values, older),
                i => i.Category == CrownShopCategories.Elixirs);
        }
        Assert.Empty(Imlight.CoreLib.Game.Services.CrownShopService.ForProfile([
            elixirs[0] with { Crowns = 1 }, elixirs[0] with { Gold = 1 }, elixirs[0] with { Template = 191109 },
        ], rules));
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    [InlineData(2u, false)]
    [InlineData(uint.MaxValue, false)]
    public void ElixirPurchaseUsesOnlyTheConfirmedNativeUseNowFlag(uint flag, bool expected)
        => Assert.Equal(expected, Imlight.CoreLib.Game.Services.CrownShopService.ElixirUseNow(
            new Imcodec.MessageLayer.Generated.WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_REQUEST { PurchaseElixirEquipNow = flag }));

    [Fact]
    public void ElixirOffersRoundTripThroughConnectedNativeLocaleMenuAndSinglePurchaseCategory() {
        var rules = ClassicDataFixture.RealRules("october-2010-arc1");
        var offered = Imlight.CoreLib.Game.Services.CrownShopService.ForProfile(Real().Offered(rules.IsFeatureEnabled, rules.Profile.Id).Values, rules)
            .Where(i => i.Category == CrownShopCategories.Elixirs).ToArray();
        var serializer = new Imcodec.ObjectProperty.ObjectSerializer(Versionable: false,
            Behaviors: Imcodec.ObjectProperty.SerializerFlags.SerializeFlags | Imcodec.ObjectProperty.SerializerFlags.Compress);
        Assert.True(serializer.Deserialize<Imcodec.ObjectProperty.TypeCache.CrownShopData>(
            Imlight.CoreLib.Game.Services.CrownShopService.SerializeCatalog(offered),
            Imcodec.ObjectProperty.PropertyFlags.Prop_Save | Imcodec.ObjectProperty.PropertyFlags.Prop_Public, out var data));
        var category = Assert.Single(data.m_crownShopLayout.m_categories.Where(c => c.m_name == "CrownShopSWF_CategoryElixirs"));
        var tab = Assert.Single(data.m_crownShopLayout.m_tabs.Where(t => t.m_ID == category.m_parentTabID));
        Assert.Equal("CrownShopSWF_MenuElixirs", tab.m_name);
        Assert.Contains(category.m_ID, tab.m_categoryIDs);
        Assert.True(category.m_forceDisallowMultipleBuy);
        Assert.False(category.m_isGroupElixirsCategory);
        Assert.Equal(16, data.m_items.Count);
        Assert.All(data.m_items, item => Assert.StartsWith($"{category.m_ID}:", item.m_displayPriority));
    }

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

    [Fact]
    public void OctoberPermanentMountsUseDatedPricesAndSerializeTheOriginalTemplates() {
        var rules = ClassicDataFixture.RealRules("october-2010-arc1");
        var offered = Real().Offered(rules.IsFeatureEnabled, rules.Profile.Id);
        var dragon = offered[191275];
        var filly = offered[225825];
        Assert.Equal((10000, 85000, CrownShopCategories.PermanentMounts),
            (dragon.Crowns, dragon.Gold, dragon.Category));
        Assert.Equal((10000, 0, CrownShopCategories.PermanentMounts),
            (filly.Crowns, filly.Gold, filly.Category));
        Assert.Null(dragon.RentalDays);
        Assert.Null(filly.RentalDays);
        Assert.Equal("october-2010-arc1", Assert.Single(dragon.Profiles));
        Assert.Equal("october-2010-arc1", Assert.Single(filly.Profiles));

        var serializer = new Imcodec.ObjectProperty.ObjectSerializer(Versionable: false,
            Behaviors: Imcodec.ObjectProperty.SerializerFlags.SerializeFlags | Imcodec.ObjectProperty.SerializerFlags.Compress);
        Assert.True(serializer.Deserialize<Imcodec.ObjectProperty.TypeCache.CrownShopData>(
            Imlight.CoreLib.Game.Services.CrownShopService.SerializeCatalog(offered.Values),
            Imcodec.ObjectProperty.PropertyFlags.Prop_Save | Imcodec.ObjectProperty.PropertyFlags.Prop_Public, out var data));
        var shownDragon = Assert.Single(data.m_items.Where(item => (ulong)item.m_itemTemplateId == 191275));
        var shownFilly = Assert.Single(data.m_items.Where(item => (ulong)item.m_itemTemplateId == 225825));
        Assert.Equal((10000, 85000), (shownDragon.m_crownsCost, shownDragon.m_goldCost));
        Assert.Equal((10000, 0), (shownFilly.m_crownsCost, shownFilly.m_goldCost));
    }

    [Theory]
    [InlineData("late-2009")]
    [InlineData("arc1-2009h1")]
    [InlineData("dev-unrestricted")]
    [InlineData("unknown-profile")]
    [InlineData("OCTOBER-2010-ARC1")]
    [InlineData(null)]
    public void OctoberMountsAreAbsentFromEveryOtherOrUnspecifiedProfile(string? profileId) {
        // CLASSIC: force every feature on to test the independent historical membership guard.
        var offered = Real().Offered(_ => true, profileId);
        Assert.False(offered.ContainsKey(191275));
        Assert.False(offered.ContainsKey(225825));
        Assert.Contains((ulong)191237, offered.Keys);
        Assert.Contains((ulong)220071, offered.Keys); // Existing owner-approved wing policy is preserved.
    }

    [Fact]
    public void OctoberMembershipDoesNotBypassTheMountFeatureSwitch() {
        var offered = Real().Offered(_ => false, "october-2010-arc1");
        Assert.False(offered.ContainsKey(191275));
        Assert.False(offered.ContainsKey(225825));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[bad_profile]")]
    [InlineData("[october-2010-arc1, october-2010-arc1]")]
    [InlineData("[dev-unrestricted]")]
    [InlineData("october-2010-arc1")]
    public void ExplicitItemProfilesMustBeNonemptyValidUniqueCatalogMembers(string itemProfiles) {
        using var data = new TempClassicData();
        var path = data.WriteRule("crown-shop-test.yaml", $$"""
            id: crown-shop-test
            profiles: [late-2009, october-2010-arc1]
            provenance: []
            license_tag: own
            items:
            - {name: Test mount, template: 191275, category: permanent_mounts, crowns: 10000, source: 0, profiles: {{itemProfiles}}}
            """);
        var error = Assert.Throws<ClassicDataException>(() => CrownShopCatalogLoader.Load(path));
        Assert.Contains(error.Errors, problem => problem.KeyPath.StartsWith("items[0].profiles"));
    }

    [Fact]
    public void HouseOffersUseTheNativeHouseUiAndAConnectedLayoutWhileEmptyHouseCatalogHasNoTab() {
        var house = new CrownShopEntry("Test house", 160431, CrownShopCategories.Houses, 10000, 100000, null, 2, false, null);
        var serializer = new Imcodec.ObjectProperty.ObjectSerializer(Versionable: false,
            Behaviors: Imcodec.ObjectProperty.SerializerFlags.SerializeFlags | Imcodec.ObjectProperty.SerializerFlags.Compress);
        Assert.True(serializer.Deserialize<Imcodec.ObjectProperty.TypeCache.CrownShopData>(
            Imlight.CoreLib.Game.Services.CrownShopService.SerializeCatalog([house]),
            Imcodec.ObjectProperty.PropertyFlags.Prop_Save | Imcodec.ObjectProperty.PropertyFlags.Prop_Public, out var data));
        var category = Assert.Single(data.m_crownShopLayout.m_categories.Where(c => c.m_isHousesCategory));
        Assert.Equal("CrownShopSWF_CategoryHouses", category.m_name);
        var tab = Assert.Single(data.m_crownShopLayout.m_tabs.Where(t => t.m_ID == category.m_parentTabID));
        Assert.Equal("CrownShopSWF_MenuHousing", tab.m_name);
        Assert.Contains(category.m_ID, tab.m_categoryIDs);
        Assert.Contains($"{category.m_ID}:", Assert.Single(data.m_items).m_displayPriority);
        Assert.True(serializer.Deserialize<Imcodec.ObjectProperty.TypeCache.CrownShopData>(
            Imlight.CoreLib.Game.Services.CrownShopService.SerializeCatalog([]),
            Imcodec.ObjectProperty.PropertyFlags.Prop_Save | Imcodec.ObjectProperty.PropertyFlags.Prop_Public, out var empty));
        Assert.DoesNotContain(empty.m_crownShopLayout.m_categories, c => c.m_isHousesCategory);
    }

}
