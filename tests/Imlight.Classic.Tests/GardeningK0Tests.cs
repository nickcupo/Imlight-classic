using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Classic.Gardening;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

// CLASSIC: gardening stage K0 (December 2010): Energy 40 + 1 per even level, Moolinda Wu's tutorial, the gardeners'
// stock and their gold-priced gardening spells. wintertusk-2011 only; the October and Celestia profiles are untouched.
[Collection(nameof(ClassicRuntimeCollection))]
public sealed class GardeningK0Tests : IDisposable {
    private readonly GardeningTables? _previous = ClassicGardening.Test.Value;
    private static GardeningTables Real => GardeningTablesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "gardening-2011.yaml"));

    public GardeningK0Tests() => EquipmentAttachConcurrencyTests.Configure();
    public void Dispose() => ClassicGardening.Test.Value = _previous;

    // ---- the profile wiring ------------------------------------------------------------------------------------

    [Fact]
    public void OnlyTheMay2011ProfileNamesTheGardeningTables() {
        Assert.Equal("rules/gardening-2011.yaml", ClassicDataFixture.LoadProfile("wintertusk-2011").Rules.Gardening);
        foreach (var id in new[] { "late-2009", "arc1-2009h1", "october-2010-arc1", "celestia-2010", "dev-unrestricted" }) {
            Assert.Null(ClassicDataFixture.LoadProfile(id).Rules.Gardening);
            Assert.False(Real.AppliesTo(id), id);
        }
        Assert.True(Real.AppliesTo("wintertusk-2011"));
    }

    [Fact]
    public void TheTutorialQuestsAreRestoredOnlyForTheMay2011Profile() {
        var quests = Real.TutorialQuests;
        Assert.Equal(new[] { "WC-GRDN-C01-001", "WC-GRDN-C02-001", "WC-GRDN-C02-002" }, quests);
        var wintertusk = ClassicDataFixture.LoadProfile("wintertusk-2011").RestoredQuests;
        foreach (var quest in quests) {
            Assert.Contains(quest, wintertusk);
            Assert.DoesNotContain(quest, ClassicDataFixture.LoadProfile("celestia-2010").RestoredQuests);
            Assert.DoesNotContain(quest, ClassicDataFixture.LoadProfile("october-2010-arc1").RestoredQuests);
            Assert.True(File.Exists(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates", quest + ".json")), quest);
        }
        var tombstones = File.ReadAllLines(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates", "_disabled.txt"))
            .Select(line => line.Split('#')[0].Trim()).ToHashSet();
        Assert.All(quests, quest => Assert.Contains(quest, tombstones));
    }

    [Fact]
    public void GreenAcresNeedsLevelTwelveAndGrowingPainsTeachesTheStarterSpells() {
        string Quest(string name) => File.ReadAllText(Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates", name + ".json"));
        Assert.Contains("\"m_numericValue\": 12.0", Quest("WC-GRDN-C01-001"));
        var pains = Quest("WC-GRDN-C02-002");
        foreach (var spell in Real.StarterSpells) Assert.Contains($"\"m_templateID\": {spell.Template}", pains);
        Assert.Contains("\"m_questName\": \"WC-GRDN-C02-001\"", pains);
    }

    // ---- Energy ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 40)]
    [InlineData(2, 41)]
    [InlineData(3, 41)]
    [InlineData(7, 43)]   // the client's table has +10 here (a later change)
    [InlineData(12, 46)]
    [InlineData(50, 65)]
    [InlineData(60, 70)]
    public void EnergyIsFortyAtLevelOnePlusOnePerEvenLevel(int level, int energy)
        => Assert.Equal(energy, Real.Energy!.MaxAt(level));

    [Fact]
    public void TheEnergyRuleReplacesEveryLevelOfTheClientTable() {
        ClassicGardening.Test.Value = Real;
        var levels = Enumerable.Range(0, 61).Select(i => new MagicLevelInfo { m_petEnergy = i == 0 ? 0 : i == 7 ? 53 : 40 + i / 2 }).ToList();
        MagicLevelsConfig.ApplyClassicEnergy(levels);
        Assert.Equal(40, levels[1].m_petEnergy);
        Assert.Equal(43, levels[7].m_petEnergy);
        Assert.Equal(70, levels[60].m_petEnergy);
        Assert.Equal(0, levels[0].m_petEnergy);
    }

    [Fact]
    public void WithoutTheGardeningRuleTheClientsEnergyStays() {
        ClassicGardening.Test.Value = null;
        ClassicRuntime.ResetForTests();
        try {
            ClassicRuntime.Initialize(ClassicDataFixture.RealRules("celestia-2010"));
            var levels = Enumerable.Range(0, 61).Select(i => new MagicLevelInfo { m_petEnergy = 99 }).ToList();
            MagicLevelsConfig.ApplyClassicEnergy(levels);
            Assert.All(levels, level => Assert.Equal(99, level.m_petEnergy));
        }
        finally { ClassicRuntime.ResetForTests(); }
    }

    // ---- ranks -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 1)]
    [InlineData(19, 1)]
    [InlineData(20, 2)]
    [InlineData(99, 2)]
    [InlineData(100, 3)]
    [InlineData(10999, 9)]
    [InlineData(11000, 10)]
    [InlineData(5_000_000, 10)]   // ten ranks in the window: Grandmaster is the last
    public void GardeningRanksFollowTheXpTable(int xp, int rank) {
        Assert.Equal(10, Real.MaxRank);
        Assert.Equal(rank, Real.RankFor(xp));
    }

    // ---- the gardeners ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheFourCousinsAreGardenersWithDatedStockWhereADatedPageExists() {
        var t = Real;
        Assert.Equal(new uint[] { 346179, 346176, 346177, 346178 }, t.Vendors.Select(v => v.Npc));
        Assert.Equal(new[] { "Farley", "Charley", "Marley", "Harley" }, t.Vendors.Select(v => v.Name));
        var charley = t.Vendor(346176)!;
        Assert.Contains(charley.Stock, s => s.Template == 355108); // the plantable Prickly Bear seed, not the seedling 356138
        Assert.DoesNotContain(charley.Stock, s => s.Template == 356138);
        Assert.Equal(8, charley.Spells.Length);
        Assert.Equal((3, 3000), charley.Spells.Where(s => s.Name == "Large Soil").Select(s => (s.Rank, s.Gold)).Single());
        var marley = t.Vendor(346177)!;
        Assert.Equal((6, 15000), marley.Spells.Where(s => s.Name == "Enchanted Lg. Soil").Select(s => (s.Rank, s.Gold)).Single());
        Assert.DoesNotContain(marley.Stock, s => s.Name.Contains("Snack", StringComparison.Ordinal));
        // May 2011: "Honeysickle seeds are no longer available from vendors".
        Assert.DoesNotContain(t.Vendors.SelectMany(v => v.Stock), s => s.Template == 355199);
        Assert.All(t.Vendors, v => Assert.Equal(6, v.ShopLayout));
    }

    [Fact]
    public void AGardenerSellsItsTableStockAndOnlyThat() {
        ClassicGardening.Test.Value = Real;
        var entity = (ZoneEntity)RuntimeHelpers.GetUninitializedObject(typeof(ZoneEntity));
        CombatRegressionTests.SetProperty(entity, nameof(ZoneEntity.ActiveGameObject), new CoreObject { m_templateID = 346177, m_globalID = 901888 });
        var shop = new InteractVendorComponent(entity);
        shop.OnStart();
        Assert.True(shop.HasItem(new GID(355080u)));   // Burning Snap Dragon
        Assert.True(shop.HasItem(new GID(342185u)));   // Medium Enchanted MooShu Pot
        Assert.False(shop.HasItem(new GID(220307u)));  // SpiralDB's later pet snack stock
    }

    [Fact]
    public void NoGardenerStockOutsideTheMay2011Profile() {
        ClassicGardening.Test.Value = null;
        Assert.Null(ClassicGardening.Vendor(346177)); // the test host runs no wintertusk-2011 runtime: gardening is off
    }

    [Fact]
    public void AGardeningSpellNeedsTheRankAndTheGold() {
        ClassicGardening.Test.Value = Real;
        var marley = Real.Vendor(346177)!;
        var swatter = marley.Spells.Single(s => s.Name == "Fly Swatter"); // rank 5, 25,000 gold
        var wizard = new Wizard { GameStats = new ServerWizGameStats(MagicSchool.Life, 30) { m_currentGold = 30000 },
            SpellbookBehavior = new() };
        wizard.GameStats.m_gardeningXP = 499; // rank 4
        Assert.Equal("Your gardening rank is too low for that spell.", InteractGardenTrainerComponent.Refusal(wizard, swatter));
        wizard.GameStats.m_gardeningXP = 500; // rank 5
        Assert.Null(InteractGardenTrainerComponent.Refusal(wizard, swatter));
        wizard.GameStats.m_currentGold = 24999;
        Assert.Equal("You don't have enough gold.", InteractGardenTrainerComponent.Refusal(wizard, swatter));
        var option = InteractGardenTrainerComponent.Option(new GardenSpellTemplate { m_name = "Fly Swatter", m_displayName = "k" }, swatter, 8, false,
            InteractGardenTrainerComponent.RankRequirement(5));
        Assert.Equal("GardenTrainingService", option.m_serviceName);
        Assert.Equal(25000, option.m_trainingCost);
        Assert.Equal(8, option.m_trainingIndex);
        Assert.Equal(5f, Assert.IsType<ReqGardeningLevel>(option.m_failedRequirement).m_numericValue);
    }

    [Fact]
    public void LearningAGardeningSpellPaysGoldAndLearnsInOneSave() {
        using var f = new SpellbookAcknowledgementTests.Fixture();
        var status = WizardSpellbookTransactions.TryLearnForGold(f.Live, SpellbookAcknowledgementTests.Learned, 60, _ => true,
            out var receipt, expectedAccountId: SpellbookAcknowledgementTests.Owner);
        Assert.Equal(SpellbookMutationStatus.Committed, status);
        Assert.Equal(1, f.Saves);
        Assert.Equal(40, f.Saved.GameStats.m_currentGold);
        Assert.Contains(SpellbookAcknowledgementTests.Learned, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds);
        Assert.Equal(40, receipt.GoldBalance);
        Assert.IsType<WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK>(receipt.Messages[0]);
        Assert.Equal(40, Assert.IsType<WIZARD_12_PROTOCOL.MSG_UPDATEGOLD>(receipt.Messages[1]).Gold);
        Assert.Equal(1, Assert.IsType<WIZARD_12_PROTOCOL.MSG_SPELLTRAINCOMPLETE>(receipt.Messages[2]).Success);
    }

    [Theory]
    [InlineData(101, true)]   // more than the saved wallet
    [InlineData(10, false)]   // the fresh rank check refuses
    public void ARefusedGardeningLessonChangesNothing(int gold, bool eligible) {
        using var f = new SpellbookAcknowledgementTests.Fixture();
        var status = WizardSpellbookTransactions.TryLearnForGold(f.Live, SpellbookAcknowledgementTests.Learned, gold, _ => eligible,
            out _, expectedAccountId: SpellbookAcknowledgementTests.Owner);
        Assert.Equal(SpellbookMutationStatus.Refused, status);
        Assert.Equal(0, f.Saves);
        Assert.Equal(100, f.Saved.GameStats.m_currentGold);
        Assert.DoesNotContain(SpellbookAcknowledgementTests.Learned, f.Saved.SpellbookBehavior.LearnedSpellTemplateIds);
    }

    [Fact]
    public void PlantingASeedCostsTheClientsPlantSeedEnergy() => Assert.Equal(2, Real.PlantSeedEnergy);
}
