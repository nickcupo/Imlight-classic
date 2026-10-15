// CLASSIC: crafting off in october-2010-arc1 (owner 2026-10-15, "just turn crafting off").
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class CraftingOffTests : IDisposable {

    private const uint Station = (1u << 28) - 6001, Sofa = (1u << 28) - 6002;
    private readonly IDictionary<ulong, CoreTemplate> _cache = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory)
        .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    public CraftingOffTests() {
        ClassicRuntime.ResetForTests();
        _cache[Station] = new WizItemTemplate { m_templateID = Station, m_objectName = "HOUSE_crafting_generic_01" };
        _cache[Sofa] = new WizItemTemplate { m_templateID = Sofa, m_objectName = "HOUSE_WC_sofa_01" };
    }

    public void Dispose() {
        _cache.Remove(Station);
        _cache.Remove(Sofa);
        ClassicRuntime.ResetForTests();
    }

    private static void Use(string profile) => ClassicRuntime.Initialize(ClassicDataFixture.RealRules(profile));

    [Fact]
    public void OctoberProfileTurnsCraftingOffAndLate2009KeepsIt() {
        Assert.False(ClassicDataFixture.LoadProfile("october-2010-arc1").Features.IsEnabled(ClassicFeatures.Crafting));
        Assert.True(ClassicDataFixture.LoadProfile("late-2009").Features.IsEnabled(ClassicFeatures.Crafting));
        Assert.Contains(ClassicDataFixture.LoadProfile("october-2010-arc1").Notes, n => n.Contains("just turn crafting off", StringComparison.Ordinal));
    }

    [Fact]
    public void OctoberProfileDisablesTheCraftingQuestsAndTheyExistInTheOverlay() {
        var disabled = ClassicDataFixture.LoadProfile("october-2010-arc1").DisabledQuests;
        Assert.Contains("WC-CRAFT-C01-001", disabled);
        Assert.Contains("WC-CLASSIC-SIDE-063", disabled);
        Assert.Empty(ClassicDataFixture.LoadProfile("late-2009").DisabledQuests);
        foreach (var quest in disabled) {
            Assert.True(File.Exists(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates", quest + ".json")), quest);
        }
    }

    [Fact]
    public void EnabledFollowsTheProfile() {
        Assert.True(ClassicCrafting.Enabled); // no runtime: nothing restricted
        ClassicRuntime.ResetForTests();
        Use("october-2010-arc1");
        Assert.False(ClassicCrafting.Enabled);
        ClassicRuntime.ResetForTests();
        Use("late-2009");
        Assert.True(ClassicCrafting.Enabled);
        ClassicRuntime.ResetForTests();
        Use("dev-unrestricted");
        Assert.True(ClassicCrafting.Enabled);
    }

    [Theory]
    [InlineData("DS-Shop-Recipe-Fire01", true)]
    [InlineData("WC-Shop-Recipe-NPC01", true)]
    [InlineData("KT-SHOP-PET-RECIPES", true)]
    [InlineData("WC-CROWN-FURNITURE", false)]
    [InlineData("WC-HUB-NPC01", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RecipeVendorNames(string? name, bool expected) => Assert.Equal(expected, ClassicCrafting.IsRecipeVendorName(name));

    [Theory]
    [InlineData("HOUSE_crafting_spells_01", true)]
    [InlineData("BG_CraftingStation_03", true)]
    [InlineData("Event_crafting_equipable_02", true)]
    [InlineData("HOUSE_WC_sofa_01", false)]
    [InlineData(null, false)]
    public void CraftingStationNames(string? name, bool expected) => Assert.Equal(expected, ClassicCrafting.IsCraftingStationName(name));

    [Fact]
    public void RecipeShopWithoutStockIsClosedOnlyWhileCraftingIsOff() {
        Use("late-2009");
        Assert.False(ClassicCrafting.ClosesRecipeShop("DS-Shop-Recipe-Fire01", hasStock: false));
        ClassicRuntime.ResetForTests();
        Use("october-2010-arc1");
        Assert.True(ClassicCrafting.ClosesRecipeShop("DS-Shop-Recipe-Fire01", hasStock: false));
        Assert.False(ClassicCrafting.ClosesRecipeShop("MB-Shop-Recipe-NPC01", hasStock: true)); // a real stock stays
        Assert.False(ClassicCrafting.ClosesRecipeShop("WC-Hub-Vendor-01", hasStock: false)); // other vendors untouched
    }

    [Fact]
    public void CraftingStationsAreWithheldFromShopsOnlyWhileCraftingIsOff() {
        Use("late-2009");
        Assert.False(InteractVendorComponent.IsCraftingStationItem(Station));
        ClassicRuntime.ResetForTests();
        Use("october-2010-arc1");
        Assert.True(InteractVendorComponent.IsCraftingStationItem(Station));
        Assert.False(InteractVendorComponent.IsCraftingStationItem(Sofa));
    }

    [Fact]
    public void BadgesDoNotWaitForADisabledQuest() {
        var rules = new BadgeRules {
            Id = "badges-test", Profiles = [], SourceFile = "t",
            Badges = [
                new Badge("all", "All", "A_1", null, "A_1", "F_1", "general", new AllQuestsBadgeAward(["A", "B"])),
                new Badge("area", "Area", "A_2", null, "A_2", "F_1", "general", new AreaQuestsBadgeAward(["A", "B"],
                    ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("fire", ["B", "C"]))),
                new Badge("any", "Any", "A_3", null, "A_3", "F_1", "general", new QuestBadgeAward(["B", "C"])),
            ],
        };
        var stripped = rules.WithoutQuests(["B"]);
        Assert.Equal(["A"], ((AllQuestsBadgeAward) stripped.Find("all")!.Award).Quests);
        var area = (AreaQuestsBadgeAward) stripped.Find("area")!.Award;
        Assert.Equal(["A"], area.Quests);
        Assert.Equal(["C"], area.BySchool["fire"]);
        Assert.Equal(["B", "C"], ((QuestBadgeAward) stripped.Find("any")!.Award).AnyOf);
        Assert.Same(rules, rules.WithoutQuests([]));
    }

    [Fact]
    public void SavingTheCityNoLongerNeedsKrokWatcherInTheOctoberProfile() {
        var profile = ClassicDataFixture.LoadProfile("october-2010-arc1");
        var rules = BadgeRulesLoader.Load(Path.Combine(ClassicDataFixture.Root, profile.Rules.Badges!));
        Assert.Contains("WC-CLASSIC-SIDE-063", ((AreaQuestsBadgeAward) rules.Find("savior-of-wizard-city")!.Award).Quests);
        var stripped = rules.WithoutQuests(profile.DisabledQuests);
        Assert.DoesNotContain("WC-CLASSIC-SIDE-063", ((AreaQuestsBadgeAward) stripped.Find("savior-of-wizard-city")!.Award).Quests);
    }

}
