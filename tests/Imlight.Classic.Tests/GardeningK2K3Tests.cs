using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Classic.Gardening;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Networking;
using Xunit;
using static Imlight.Classic.Tests.GardenTests;

namespace Imlight.Classic.Tests;

// CLASSIC: gardening stages K2 (pests and pest spells, protection, likes and dislikes, revive, planters indoors) and K3
// (ranks, harvest rewards). The plants here are the GardenTests fixture (not historical data) plus a fixture pest.
[Collection(nameof(ClassicRuntimeCollection))]
public sealed class GardeningK2K3Tests : IDisposable {
    private const uint Bug = 6001, BigBug = 6002, Pixie = 6003, Shield = 6101, Zap1 = 7101, Zap3 = 7103, Guard = 7201;
    private const uint Pot = 5101, BigPot = 5102, Gnome = 5201, Stink = 5202;
    private readonly IGardenCatalog _catalog = GardenCatalog.Test.Value;
    private readonly GardeningTables? _tables = ClassicGardening.Test.Value;

    public GardeningK2K3Tests() { EquipmentAttachConcurrencyTests.Configure(); GardenCatalog.Test.Value = Catalog2(); }
    public void Dispose() { GardenCatalog.Test.Value = _catalog; ClassicGardening.Test.Value = _tables; }

    private static FixedGardenCatalog Catalog2(float like = 0.75f, float dislike = 1.25f) {
        var c = Catalog();
        // Every growing stage can get the fixture bug; the mature stage likes gnomes and dislikes stinkweed.
        foreach (var id in new[] { Seedling, Young, Mature }) {
            c.Seeds[id] = c.Seeds[id] with { Pests = [(100f, Bug)], PestCheckSeconds = 4 * H };
        }
        c.Seeds[Seedling] = c.Seeds[Seedling] with { Likes = [("GDN_GardenGnome", like), ("GDN_Stinkweed", dislike)] };
        c.Pests[Bug] = new PestInfo(Bug, 1, 2 * D, 2 * D, false, new bool[5]);
        c.Pests[BigBug] = new PestInfo(BigBug, 4, 2 * D, 2 * D, false, new bool[5]);
        c.Pests[Pixie] = new PestInfo(Pixie, 9, 8 * D, 8 * D, true, new bool[5]);
        c.Pests[Shield] = new PestInfo(Shield, 2, 2 * D, 2 * D, false, new bool[5]);
        c.Spells[Zap1] = new GardenSpellInfo(Zap1, GardenSpellKind.InsectFighting, 0, new bool[5], 500, 5, 0) { BugZapLevel = 1 };
        c.Spells[Zap3] = new GardenSpellInfo(Zap3, GardenSpellKind.InsectFighting, 0, new bool[5], 500, 15, 0) { BugZapLevel = 3 };
        c.Spells[Guard] = new GardenSpellInfo(Guard, GardenSpellKind.PlantProtection, 0, new bool[5], 500, 15, 0) { ProtectionTemplateId = Shield };
        c.Soils[Pot] = new SoilInfo(Pot, GardenSize.Small, false, false) { Pot = true };
        c.Soils[BigPot] = new SoilInfo(BigPot, GardenSize.Medium, false, false) { Pot = true };
        c.Adjectives[Gnome] = ["Housing", "GDN_GardenGnome"];
        c.Adjectives[Stink] = ["Housing", "GDN_Stinkweed"];
        return c;
    }

    private static GardenEngine Engine(bool pests = false) => new(GardenCatalog.Current, new Random(7)) { PestsEnabled = pests };
    private static GardenLedger Ledger() => new() { OwnerId = 1, DeedId = 2, Zone = "Housing/WizardCity/WC_Tier1_Exterior", PackageNumber = 40 };
    private static GardenPlantState PlantAt(GardenLedger l) => l.Plants.Single(p => !p.IsSoil);
    private static GardenSpellInfo SoilSpellInfo => ((FixedGardenCatalog)GardenCatalog.Current).Spells[SoilSpell];

    private static (GardenLedger, GardenEngine) Planted(bool pests = true) {
        var l = Ledger(); var e = Engine(pests);
        e.PrepareSoil(l, SoilSpellInfo, 0, 0, 0, T0);
        Assert.Null(e.Plant(l, SeedItem, 0, 0, 0, T0, 1));
        return (l, e);
    }

    // ---- pests -----------------------------------------------------------------------------------------------------

    [Fact]
    public void APestArrivesOnACheckStopsGrowthAndHarvestAndWiltsThePlant() {
        var (l, e) = Planted();
        var due = PlantAt(l).NextStageAt;
        e.Advance(l, T0 + 4 * H);                                  // the first check: 100% an hour makes it certain
        var p = PlantAt(l);
        Assert.Equal(Bug, p.PestTemplateId);
        Assert.NotEqual(0u, p.Flags & GardenRules.FlagPest);
        Assert.True(e.NeedsRefresh);                               // no patch adds a pest: the client reloads the blob
        e.Advance(l, due + H);
        Assert.Equal(Seedling, PlantAt(l).TemplateId);             // "Pests ... hinder its growth"
        e.Advance(l, T0 + 4 * H + 2 * D);                          // the pest's wilt time
        Assert.Equal(Wilt, PlantAt(l).TemplateId);
    }

    [Fact]
    public void APestSpellRemovesPestsOfItsRankOrLowerOnly() {
        var (l, e) = Planted();
        e.Advance(l, T0 + 4 * H);
        PlantAt(l).PestTemplateId = BigBug;                        // a rank 4 pest
        Assert.Equal(1, e.Zap(l, ((FixedGardenCatalog)GardenCatalog.Current).Spells[Zap3], 0, 0, 0, T0 + 5 * H));
        Assert.Equal(BigBug, PlantAt(l).PestTemplateId);          // "the spell must be of high enough rank"
        PlantAt(l).PestTemplateId = Bug;
        var before = e.Patches.Count;
        e.Zap(l, ((FixedGardenCatalog)GardenCatalog.Current).Spells[Zap1], 0, 0, 0, T0 + 6 * H);
        Assert.Equal(0u, PlantAt(l).PestTemplateId);
        Assert.Equal(0u, PlantAt(l).Flags & (GardenRules.FlagPest | GardenRules.FlagGoodPest));
        var patch = e.Patches.Skip(before).First();
        Assert.Equal(GardenRules.PatchPestRemoved, patch.Command);  // client case 6 needs a nonzero value
        Assert.Equal(Zap1, patch.TemplateOrFlags);
    }

    [Fact]
    public void ZappingTheLastTroubleOfAWiltingPlantMakesItYoungAgainAsTheClientDoes() {
        var (l, e) = Planted();
        e.Advance(l, T0 + 4 * H + 2 * D);
        Assert.Equal(Wilt, PlantAt(l).TemplateId);
        var p = PlantAt(l); Array.Clear(p.NeedSince); p.Flags &= ~GardenRules.NeedMask;
        e.Zap(l, ((FixedGardenCatalog)GardenCatalog.Current).Spells[Zap1], 0, 0, 0, T0 + 4 * H + 2 * D + 1);
        Assert.Equal(Young, PlantAt(l).TemplateId);
        Assert.DoesNotContain(e.Patches, x => x.Command == GardenRules.PatchAddPlant && x.TemplateOrFlags == Young); // case 6 did it
    }

    [Fact]
    public void AProtectionKeepsLowerPestsAwayRemovesOneAndExpires() {
        var (l, e) = Planted();
        var guard = ((FixedGardenCatalog)GardenCatalog.Current).Spells[Guard];
        Assert.Equal(1, e.Protect(l, guard, 0, 0, 0, T0 + H));
        var p = PlantAt(l);
        Assert.Equal(Shield, p.PestProtectionTemplateId);
        Assert.NotEqual(0u, p.Flags & GardenRules.FlagProtected);
        Assert.Equal(GardenRules.PatchProtection, e.Patches.Last().Command);
        e.Advance(l, T0 + 4 * H);
        Assert.Equal(0u, PlantAt(l).PestTemplateId);               // rank 2 protection keeps the rank 1 bug away
        e.Advance(l, T0 + H + 2 * D);
        Assert.Equal(0u, PlantAt(l).Flags & GardenRules.FlagProtected); // worn off: a flags patch without 0x100
    }

    [Fact]
    public void AGoodPestDoesNoHarm() {
        var (l, e) = Planted(pests: false);
        var p = PlantAt(l);
        p.PestTemplateId = Pixie; p.Flags |= GardenRules.FlagPest | GardenRules.FlagGoodPest;
        Assert.False(GardenEngine.Blocked(p));
        Assert.False(GardenEngine.BadPest(p));
    }

    // ---- revive ------------------------------------------------------------------------------------------------------

    [Fact]
    public void ReviveTurnsADeadPlantIntoAYoungOne() {
        var (l, e) = Planted(pests: false);
        Assert.Equal("Only a dead plant can be revived.", e.Revive(l, 0, 0, 0, T0 + H));
        e.Advance(l, T0 + 40 * D);
        Assert.Equal(Dead, PlantAt(l).TemplateId);
        Assert.Null(e.Revive(l, 0, 0, 0, T0 + 40 * D));
        Assert.Equal(Young, PlantAt(l).TemplateId);
        Assert.Contains(e.Patches, x => x.Command == GardenRules.PatchRevive);
    }

    // ---- likes and dislikes --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Gnome, 0.75)]
    [InlineData(Stink, 1.25)]
    public void NearbyThingsAPlantLikesOrDislikesChangeHowLongItGrows(uint furniture, double factor) {
        var l = Ledger(); var e = Engine();
        e.Neighbours = [new GardenNeighbour(furniture, 300, 0, 0)];
        e.PrepareSoil(l, SoilSpellInfo, 0, 0, 0, T0);
        e.Plant(l, SeedItem, 0, 0, 0, T0, 1);
        Assert.Equal((float)factor, PlantAt(l).Modifier, 3);
        Assert.Equal(T0 + (uint)Math.Round(13 * H * factor), PlantAt(l).NextStageAt);
        // Out of the client's 1000-unit reach the furniture does nothing.
        var far = Ledger(); var e2 = Engine(); e2.Neighbours = [new GardenNeighbour(furniture, 1500, 0, 0)];
        e2.PrepareSoil(far, SoilSpellInfo, 0, 0, 0, T0); e2.Plant(far, SeedItem, 0, 0, 0, T0, 1);
        Assert.Equal(T0 + 13 * H, PlantAt(far).NextStageAt);
    }

    [Fact]
    public void LikesMultiplyAndAreClampedAsTheClientClamps() {
        GardenCatalog.Test.Value = Catalog2(like: 0.05f, dislike: 1f);
        var l = Ledger(); var e = Engine(); e.Neighbours = [new GardenNeighbour(Gnome, 0, 0, 0)];
        e.PrepareSoil(l, SoilSpellInfo, 0, 0, 0, T0); e.Plant(l, SeedItem, 0, 0, 0, T0, 1);
        Assert.Equal(0.1f, PlantAt(l).Modifier, 3);               // 0x141d95da0 clamps to 0.1..5
    }

    // ---- planters ----------------------------------------------------------------------------------------------------

    [Fact]
    public void IndoorsSeedsGrowOnlyInPlantersOnePerPlanter() {
        var l = Ledger(); var e = Engine();
        e.Indoors = true;
        e.Pots = [new GardenPot(77, Pot, 100, 100, 0, ((FixedGardenCatalog)GardenCatalog.Current).Soils[Pot])];
        Assert.Equal("Indoors, plants grow in planters.", e.PrepareSoil(l, SoilSpellInfo, 0, 0, 0, T0));
        Assert.Equal("Plant the seed in a planter.", e.Plant(l, SeedItem, 0, 0, 0, T0, 1));
        Assert.Null(e.Plant(l, SeedItem, 110, 95, 20, T0, 1));     // the pot's plant node, near the pot
        Assert.Equal(77ul, PlantAt(l).PotItemId);
        Assert.Equal((110f, 95f, 20f), (PlantAt(l).X, PlantAt(l).Y, PlantAt(l).Z));
        Assert.Equal("Something is already growing there.", e.Plant(l, SeedItem, 100, 100, 20, T0, 1));
        Assert.Equal("That seed needs a larger plot.", e.Plant(Ledger(), BigSeed, 100, 100, 20, T0, 1));
        Assert.Null(e.Plow(l, 110, 95, 20, T0 + H));
        Assert.Null(e.Plant(l, SeedItem, 100, 100, 20, T0 + H, 1)); // plowed: the pot is free again
    }

    [Fact]
    public void TheDormAndACastleRoomAreGardensTheDormIndoors() {
        Assert.True(GardeningService.GardenRoom(new HousingAttachContext(1, 1, HousingRules.DormZone, 0, 9), out var indoors));
        Assert.True(indoors);
        Assert.False(GardeningService.GardenRoom(new HousingAttachContext(1, 1, "WizardCity/WC_Hub", 0, 9), out _));
    }

    // ---- ranks and harvest rewards -----------------------------------------------------------------------------------

    private static GardeningTables Tables(params GardenReward[] mature) {
        var real = GardeningTablesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "gardening-2011.yaml"));
        return real with {
            Harvests = [new GardenHarvestTable("fixture", [Mature], [Elder], [.. mature], []) { PickOne = true }],
            PlantSeedEnergy = 0,
        };
    }

    [Fact]
    public void AHarvestPaysItsGoldAndRaisesTheGardeningRank() {
        ClassicGardening.Test.Value = Tables(new GardenReward(GardenRewardKind.Gold, 0, "", 1, 24, 24));
        var store = new GardenStore(); using var scope = store.Scope(T0);
        store.SeedSoil(0, 0, 0);
        Assert.True(GardenCollection.Plant(store.Login(), store.Room, SeedItem, 5001, 0, 0, 0).Saved);
        var plant = store.Garden.Plants.Single(p => !p.IsSoil);
        plant.TemplateId = Mature; plant.Flags = 0; Array.Clear(plant.NeedAt); plant.NextStageAt = 0; plant.HarvestAt = store.Clock;
        store.Wizard.GameStats.m_gardeningXP = 15;                  // 15 + 10 crosses 20: Novice -> Neophyte
        var harvested = GardenCollection.Harvest(store.Login(), store.Room, 0, 0, 0);
        Assert.True(harvested.Saved, harvested.Error);
        Assert.Equal(124, store.Wizard.GameStats.m_currentGold);
        Assert.Equal(25, store.Wizard.GameStats.m_gardeningXP);
        Assert.Equal(2, store.Wizard.GameStats.m_gardeningLevel);
        Assert.True(harvested.RankUp);
        Assert.Contains(harvested.Rewards, m => m is Imcodec.MessageLayer.Generated.WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold: 124 });
    }

    [Fact]
    public void PlantAllSendsThePlantSeedSpellAndTheSeedItem() {
        var c = (FixedGardenCatalog)GardenCatalog.Current;
        c.Spells[982594924] = new GardenSpellInfo(982594924, GardenSpellKind.SoilPreparation, 0, new bool[5], 0, 2, 0);
        var store = new GardenStore(); using var scope = store.Scope(T0);
        store.SeedSoil(0, 0, 0);
        var planted = GardenCollection.Plant(store.Login(), store.Room, 982594924, 5001, 0, 0, 0);
        Assert.True(planted.Saved, planted.Error);
        Assert.Equal(Seedling, store.Garden.Plants.Single(p => !p.IsSoil).TemplateId);
    }

    [Fact]
    public void PlantingCostsEnergyWhenTheTablesSaySo() {
        ClassicGardening.Test.Value = Tables() with { PlantSeedEnergy = 2 };
        var store = new GardenStore(); using var scope = store.Scope(T0);
        store.SeedSoil(0, 0, 0);
        Assert.Equal("You don't have enough Energy.", GardenCollection.Plant(store.Login(), store.Room, SeedItem, 5001, 0, 0, 0).Error);
    }

    [Fact]
    public void ThePickOneTablesOfTheMay2011FileNameRealPlantStages() {
        var t = GardeningTablesLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "gardening-2011.yaml"));
        Assert.True(t.Harvests.Length >= 10);
        Assert.All(t.Harvests, h => { Assert.True(h.PickOne); Assert.NotEmpty(h.Mature); Assert.NotEmpty(h.Elder); });
        var dandelion = t.HarvestFor(355153)!.Value;               // GDN_SM_Dandelion (mature)
        Assert.False(dandelion.Elder);
        Assert.Contains(dandelion.Table.MatureRewards, r => r.Kind == GardenRewardKind.Gold && r.Min == 24);
        Assert.True(t.HarvestFor(355154)!.Value.Elder);
        Assert.Equal(new[] { 990984473u, 1639123435u, 2799024u, 1891250030u }, t.StarterSpells.Select(s => s.Template));
    }

    // ---- the real client's templates (private owned extraction; skipped where it is absent) ----------------------

    [Fact]
    public void EveryClientSeedAndPestRateParses() {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "w101c-private/extract/r806919/root/ObjectData/Housing/Gardening");
        if (!Directory.Exists(root)) Assert.Skip("Requires the owned r806919 template extraction; no game payload is committed.");
        var serializer = new Imcodec.ObjectProperty.BindSerializer();
        var seeds = 0; var pests = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories)) {
            if (!serializer.Deserialize<WizItemTemplate>(File.ReadAllBytes(file), 1, out var item) || item is null) continue;
            if (item.m_behaviors?.OfType<SeedBehaviorTemplate>().FirstOrDefault() is { } seed) {
                var info = GardenCatalog.FromTemplate(1, seed, false); seeds++;
                Assert.All(info.Likes, like => Assert.InRange(like.Modifier, 0.01f, 10f));
            }
            if (item.m_behaviors?.OfType<PestBehaviorTemplate>().FirstOrDefault() is { } pest) { GardenCatalog.FromPest(1, pest); pests++; }
        }
        Assert.True(seeds > 100, $"{seeds} seed templates");
        Assert.True(pests > 10, $"{pests} pest templates");
    }

    [Theory]
    [InlineData("HOUSE_GDN_SM_Pot_Normal_TerraCotta_A01", 0, false)]
    [InlineData("HOUSE_GDN_Med_Pot_Enchant_TerraCotta_A01", 1, true)]
    [InlineData("HOUSE_GDN_SM_Pot_Enchant_Krok_A01", 0, true)]
    public void APlantersSizeAndSoilComeFromItsName(string name, int size, bool enchanted) {
        var soil = GardenCatalog.SoilFromName(1, name, pot: true);
        Assert.True(soil.Pot);
        Assert.Equal(((GardenSize)size, enchanted), (soil.Size, soil.Enchanted));
    }
}
