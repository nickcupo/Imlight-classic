using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.CoreLib.Classic.Gardening;
using Imlight.CoreLib.Classic.Housing;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

// CLASSIC: December 2010 gardening, stage 1 (plant on prepared soil, grow over time, needs, harvest, persistence).
// The plant numbers below are a FIXTURE shaped like the client's Couch Potatoes chain, not historical data.
[Collection(nameof(ClassicRuntimeCollection))]
public sealed class GardenTests : IDisposable {
    private const uint SeedItem = 9001, Seedling = 9002, Young = 9003, Mature = 9004, Elder = 9005, Wilt = 9006, Dead = 9007;
    private const uint Soil = 8001, BigSeed = 9101, BigSeedling = 9102, SoilSpell = 7001, WaterSpell = 7002;
    private const uint H = 3600, D = 86400, T0 = 1_300_000_000;
    private readonly IGardenCatalog _previous = GardenCatalog.Test.Value;

    public GardenTests() { EquipmentAttachConcurrencyTests.Configure(); GardenCatalog.Test.Value = Catalog(); }
    public void Dispose() => GardenCatalog.Test.Value = _previous;

    private static uint[] Need(uint water) => [water, 0, 0, 0, 0];
    private static SeedInfo Info(uint id, GardenStage stage, uint next, uint nextSeconds, uint water = 0, uint wilt = 0,
        uint harvest = 0, int xp = 0, GardenSize size = GardenSize.Small, uint maxHarvests = 0)
        => new(id, stage, next, nextSeconds, Need(water), Need(water), Need(wilt), harvest, harvest, Wilt, Dead, Young, Mature,
            maxHarvests, xp, size, false, false, 0);

    internal static FixedGardenCatalog Catalog() {
        var c = new FixedGardenCatalog();
        foreach (var s in new[] {
            Info(SeedItem, GardenStage.Seed, Seedling, 0),
            Info(Seedling, GardenStage.Seedling, Young, 13 * H),
            Info(Young, GardenStage.Young, Mature, 2 * D, water: 4 * H, wilt: 12 * H),
            Info(Mature, GardenStage.Mature, Elder, 9 * D, water: 8 * H, wilt: 12 * H, harvest: 12 * H, xp: 10, maxHarvests: 3),
            Info(Elder, GardenStage.Elder, 0, 0, xp: 30),
            Info(Wilt, GardenStage.Wilting, Dead, 1 * D),
            Info(Dead, GardenStage.Dead, 0, 0),
            Info(BigSeed, GardenStage.Seed, BigSeedling, 0, size: GardenSize.Large),
            Info(BigSeedling, GardenStage.Seedling, 0, 0, size: GardenSize.Large),
        }) c.Seeds[s.TemplateId] = s;
        c.Soils[Soil] = new SoilInfo(Soil, GardenSize.Small, false, false);
        c.Spells[SoilSpell] = new GardenSpellInfo(SoilSpell, GardenSpellKind.SoilPreparation, Soil, [false, false, false, false, false], 0, 2, 0);
        c.Spells[WaterSpell] = new GardenSpellInfo(WaterSpell, GardenSpellKind.Growing, 0, [true, false, false, false, false], 5, 1, 0);
        return c;
    }

    private static GardenEngine Engine() => new(GardenCatalog.Current, new Random(7));
    private static GardenLedger Ledger() => new() { OwnerId = 1, DeedId = 2, Zone = "Housing/WizardCity/WC_Tier1_Exterior", PackageNumber = 40 };
    private static GardenPlantState PlantAt(GardenLedger l) => l.Plants.Single(p => !p.IsSoil);

    // ---- the gate (golden: off everywhere today) ----------------------------------------------------------------

    [Fact]
    public void GardeningIsOffInEveryProfileThatExistsToday() {
        Assert.False(GardenRules.Enabled); // test host: no initialized October runtime
        foreach (var id in new[] { "late-2009", "arc1-2009h1", "dev-unrestricted", "october-2010-arc1", "celestia-2010" }) {
            var profile = ClassicDataFixture.LoadProfile(id);
            Assert.False(ClassicProfiles.IsOrExtends(profile.Id, GardenRules.Wintertusk2011), id);
        }
        Assert.False(ClassicDataFixture.LoadProfile("late-2009").Features.IsEnabled(ClassicFeatures.Gardening));
    }

    [Theory]
    [InlineData("9d0h0m", 9 * 86400)]
    [InlineData("0d13h0m", 13 * 3600)]
    [InlineData("1d10h30m", 86400 + 10 * 3600 + 30 * 60)]
    [InlineData("0m", 0)]
    [InlineData("", 0)]
    public void ClientRateStringsBecomeSeconds(string rate, int seconds) => Assert.Equal((uint)seconds, GardenRules.Seconds(rate));

    [Fact]
    public void AnUnknownRateIsRefused() => Assert.Throws<FormatException>(() => GardenRules.Seconds("soon"));

    // ---- the life of a plant -------------------------------------------------------------------------------------

    [Fact]
    public void APlantNeedsPreparedSoilOfTheRightSize() {
        var l = Ledger(); var e = Engine();
        Assert.Equal("Prepare the soil first.", e.Plant(l, SeedItem, 10, 20, 5, T0, 1));
        Assert.Null(e.PrepareSoil(l, Catalog().Spells[SoilSpell], 10, 20, 5, T0));
        Assert.Equal("Something is already growing there.", e.PrepareSoil(l, Catalog().Spells[SoilSpell], 10.4f, 20, 5, T0));
        Assert.Equal("That seed needs a larger plot.", e.Plant(l, BigSeed, 10, 20, 5, T0, 1));
        Assert.Equal("That is not a seed.", e.Plant(l, Young, 10, 20, 5, T0, 1));
        Assert.Null(e.Plant(l, SeedItem, 10.3f, 19.8f, 5, T0, 1));
        var p = PlantAt(l);
        Assert.Equal(Seedling, p.TemplateId);
        Assert.Equal((10f, 20f, 5f), (p.X, p.Y, p.Z)); // exactly on the soil pile the client targeted
        Assert.Equal("Something is already growing there.", e.Plant(l, SeedItem, 10, 20, 5, T0, 1));
        Assert.Equal(new byte[] { GardenRules.PatchAddSoil, GardenRules.PatchAddPlant }, e.Patches.Select(x => x.Command));
        Assert.Equal(Soil, e.Patches[0].TemplateOrFlags);
        Assert.Equal(Seedling, e.Patches[1].TemplateOrFlags);
    }

    [Fact]
    public void ItGrowsThroughEveryStageWhenCaredFor() {
        var l = Ledger(); var e = Engine(); var water = Catalog().Spells[WaterSpell];
        e.PrepareSoil(l, Catalog().Spells[SoilSpell], 0, 0, 0, T0);
        e.Plant(l, SeedItem, 0, 0, 0, T0, 1);
        e.Advance(l, T0 + 13 * H - 1);
        Assert.Equal(Seedling, PlantAt(l).TemplateId);
        e.Advance(l, T0 + 13 * H);
        Assert.Equal(Young, PlantAt(l).TemplateId);
        // Water every 4 hours: the young plant never wilts and matures two days later.
        var t = T0 + 13 * H;
        for (var i = 0; i < 12; i++) { t += 4 * H; e.Advance(l, t); e.CastGrowing(l, water, 0, 0, 0, t); }
        Assert.Equal(Mature, PlantAt(l).TemplateId);
        Assert.Equal(0u, PlantAt(l).Flags & GardenRules.NeedMask);
    }

    [Fact]
    public void AnUnmetNeedPausesGrowthThenWiltsAndKillsThePlant() {
        var l = Ledger(); var e = Engine();
        e.PrepareSoil(l, Catalog().Spells[SoilSpell], 0, 0, 0, T0);
        e.Plant(l, SeedItem, 0, 0, 0, T0, 1);
        var young = T0 + 13 * H;
        e.Advance(l, young + 4 * H);
        Assert.Equal(GardenRules.FlagNeedWater, PlantAt(l).Flags & GardenRules.NeedMask);
        e.Advance(l, young + 16 * H - 1);
        Assert.Equal(Young, PlantAt(l).TemplateId);
        e.Advance(l, young + 16 * H);                // 12 h of thirst
        Assert.Equal(Wilt, PlantAt(l).TemplateId);
        e.Advance(l, young + 16 * H + D);            // "it will die before too long"
        Assert.Equal(Dead, PlantAt(l).TemplateId);
        Assert.Equal("That plant is not ready to harvest.", e.Harvest(l, 0, 0, 0, young + 20 * D).Error);
    }

    [Fact]
    public void WateringAWiltingPlantRevivesItToItsYoungStage() {
        var l = Ledger(); var e = Engine();
        e.PrepareSoil(l, Catalog().Spells[SoilSpell], 0, 0, 0, T0);
        e.Plant(l, SeedItem, 0, 0, 0, T0, 1);
        var wilted = T0 + 13 * H + 16 * H;
        e.Advance(l, wilted);
        Assert.Equal(Wilt, PlantAt(l).TemplateId);
        Assert.Equal(1, e.CastGrowing(l, Catalog().Spells[WaterSpell], 1, 1, 0, wilted + H));
        Assert.Equal(Young, PlantAt(l).TemplateId);
        Assert.Equal(0u, PlantAt(l).Flags & GardenRules.NeedMask);
    }

    [Fact]
    public void ThirstDoesNotCountTowardGrowth() {
        var l = Ledger(); var e = Engine();
        e.PrepareSoil(l, Catalog().Spells[SoilSpell], 0, 0, 0, T0);
        e.Plant(l, SeedItem, 0, 0, 0, T0, 1);
        var young = T0 + 13 * H;
        e.Advance(l, young);
        var due = PlantAt(l).NextStageAt;
        e.Advance(l, young + 10 * H);                            // thirsty since young + 4 h
        e.CastGrowing(l, Catalog().Spells[WaterSpell], 0, 0, 0, young + 10 * H);
        Assert.Equal(due + 6 * H, PlantAt(l).NextStageAt);         // six thirsty hours pushed maturity back
    }

    [Fact]
    public void AMaturePlantHarvestsOnItsTimerUpToItsLimitThenTheElderOnce() {
        var l = Ledger(); var e = Engine(); var water = Catalog().Spells[WaterSpell];
        e.PrepareSoil(l, Catalog().Spells[SoilSpell], 0, 0, 0, T0);
        e.Plant(l, SeedItem, 0, 0, 0, T0, 1);
        uint t = T0;
        void Live(uint until) { while (t < until) { t = Math.Min(until, t + 2 * H); e.Advance(l, t); e.CastGrowing(l, water, 0, 0, 0, t); } }
        Live(T0 + 13 * H + 2 * D);
        Assert.Equal(Mature, PlantAt(l).TemplateId);
        Assert.Equal("That plant is not ready to harvest.", e.Harvest(l, 0, 0, 0, t).Error);
        Live(t + 12 * H);
        Assert.NotEqual(0u, PlantAt(l).Flags & GardenRules.FlagHarvestReady); // the client's glow (patch 10)
        var first = e.Harvest(l, 0, 0, 0, t);
        Assert.True(first.Done, first.Error);
        Assert.Equal(10, first.Xp);
        Assert.False(first.Final);
        Assert.Equal(GardenRules.PatchHarvested, e.Patches.Last().Command);
        Assert.Equal(t + 12 * H, e.Patches.Last().Time);
        Assert.Equal("That plant is not ready to harvest.", e.Harvest(l, 0, 0, 0, t + 1).Error);
        for (var i = 0; i < 2; i++) { Live(t + 12 * H); Assert.True(e.Harvest(l, 0, 0, 0, t).Done); }
        Live(t + 12 * H);
        Assert.Equal("That plant has nothing more to give until it grows.", e.Harvest(l, 0, 0, 0, t).Error);
        Live(T0 + 13 * H + 2 * D + 9 * D);
        Assert.Equal(Elder, PlantAt(l).TemplateId);
        Assert.Contains(e.Patches, p => p.Command == GardenRules.PatchAdvanceMature && p.TemplateOrFlags == Elder);
        var last = e.Harvest(l, 0, 0, 0, t);
        Assert.True(last.Done, last.Error);
        Assert.True(last.Final);
        Assert.Equal(30, last.Xp);
        Assert.DoesNotContain(l.Plants, p => !p.IsSoil);          // "it disappears"
        Assert.Single(l.Plants);                                   // the soil pile stays until plowed
    }

    [Fact]
    public void ANeedyPlantCannotBeHarvested() {
        var l = Ledger(); var e = Engine(); var water = Catalog().Spells[WaterSpell];
        e.PrepareSoil(l, Catalog().Spells[SoilSpell], 0, 0, 0, T0);
        e.Plant(l, SeedItem, 0, 0, 0, T0, 1);
        uint t = T0;
        while (t < T0 + 13 * H + 2 * D + 12 * H) { t += 2 * H; e.Advance(l, t); e.CastGrowing(l, water, 0, 0, 0, t); }
        e.Advance(l, t + 8 * H);
        Assert.NotEqual(0u, PlantAt(l).Flags & GardenRules.NeedMask);
        Assert.Equal("Take care of the plant's needs first.", e.Harvest(l, 0, 0, 0, t + 8 * H).Error);
    }

    [Fact]
    public void EveryPatchAdvancesThePackageVersionByExactlyOne() {
        var l = Ledger(); var e = Engine();
        e.PrepareSoil(l, Catalog().Spells[SoilSpell], 0, 0, 0, T0);
        e.Plant(l, SeedItem, 0, 0, 0, T0, 1);
        e.Advance(l, T0 + 40 * D);
        e.Plow(l, 0, 0, 0, T0 + 40 * D);
        e.Plow(l, 0, 0, 0, T0 + 40 * D);
        Assert.Equal(Enumerable.Range(2, e.Patches.Count).Select(v => (uint)v), e.Patches.Select(p => p.Version));
        Assert.Equal(1u + (uint)e.Patches.Count, l.Version);
        Assert.Empty(l.Plants);
    }

    [Fact]
    public void AreaSpellsReachEveryPlantInTheirRadiusOnly() {
        var l = Ledger(); var e = Engine();
        foreach (var x in new[] { 0f, 4f, 30f }) { e.PrepareSoil(l, Catalog().Spells[SoilSpell], x, 0, 0, T0); e.Plant(l, SeedItem, x, 0, 0, T0, 1); }
        e.Advance(l, T0 + 13 * H + 5 * H);
        Assert.Equal(2, e.CastGrowing(l, Catalog().Spells[WaterSpell], 1, 0, 0, T0 + 18 * H));
        Assert.Equal(1, l.Plants.Count(p => !p.IsSoil && (p.Flags & GardenRules.FlagNeedWater) != 0));
    }

    // ---- the native blob -----------------------------------------------------------------------------------------

    [Fact]
    public void TheGardenBlobIsTheClientsShortForm() {
        var l = Ledger(); var e = Engine();
        e.PrepareSoil(l, Catalog().Spells[SoilSpell], -12.4f, 300.6f, 7.25f, T0);
        e.Plant(l, SeedItem, -12.4f, 300.6f, 7.25f, T0, 1);
        var bytes = GardenCodec.Compress(l);
        Assert.Equal(GardenRules.ShortMagic, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal((ushort)4, BitConverter.ToUInt16(bytes, 4));
        Assert.Equal(8 + 16 + 36, bytes.Length); // soil pile: 16 bytes; plant: 36 bytes
        var read = GardenCodec.Decompress(bytes);
        Assert.Equal(2, read.Count);
        Assert.Equal((-12f, 301f, 7.25f, Soil, GardenRules.FlagSoil), (read[0].X, read[0].Y, read[0].Z, read[0].Template, read[0].Flags));
        Assert.Equal(Seedling, read[1].Template);
        Assert.Equal(T0 + 13 * H, read[1].NextStage);
        Assert.Equal(T0, read[1].StageStart);
    }

    [Fact]
    public void TheGardenBlobCarriesTheBinaryDataUnchanged() {
        var l = Ledger(); var e = Engine();
        e.PrepareSoil(l, Catalog().Spells[SoilSpell], -1, -1, -1, T0); // negative axes put 0x80+ bytes in the data
        e.Plant(l, SeedItem, -1, -1, -1, T0, 1);
        byte[] blob = GardenCodec.Blob(l, 0x1234567890);
        var data = GardenCodec.Compress(l);
        var at = Enumerable.Range(0, blob.Length - data.Length + 1).Single(i => blob.Skip(i).Take(data.Length).SequenceEqual(data));
        Assert.Equal(data.Length, BitConverter.ToUInt16(blob, at - 2)); // the std::string length prefix
        Assert.True(data.Any(b => b >= 0x80));
    }

    [Fact]
    public void OnlyARequestForThisGardensPackageIsAccepted() {
        var l = Ledger();
        var request = HousingCodec.Encode(GardenCodec.Manifest(l, 77));
        Assert.True(GardenCodec.AcceptRequest(request, 77, l));
        Assert.False(GardenCodec.AcceptRequest(request, 78, l));
        var other = Ledger(); other.PackageNumber = 41;
        Assert.False(GardenCodec.AcceptRequest(request, 77, other));
        // A furniture (Proxy) request is the housing service's, never the garden's.
        var room = new HousingLedger { OwnerId = 1, PackageNumber = 40 };
        Assert.False(GardenCodec.AcceptRequest(HousingCodec.Encode(HousingCodec.Manifest(room, 77)), 77, l));
        Assert.Equal(GardenRules.PackageUserData | (9UL << 40) | (0x55UL << 48), GardenCodec.ProxyId(0x55));
    }

    // ---- the real client's templates (private owned extraction; skipped where it is absent) ----------------------

    [Fact]
    public void TheClientsCouchPotatoesChainReadsAsSeedStages() {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "w101c-private/extract/r806919/root/ObjectData/Housing/Gardening/Plants");
        if (!Directory.Exists(dir)) Assert.Skip("Requires the owned r806919 template extraction; no game payload is committed.");
        SeedInfo Read(string name, uint id) {
            Assert.True(new Imcodec.ObjectProperty.BindSerializer().Deserialize<WizItemTemplate>(
                File.ReadAllBytes(Path.Combine(dir, name + ".xml")), 1, out var item), name);
            var seed = item.m_behaviors.OfType<SeedBehaviorTemplate>().Single();
            return GardenCatalog.FromTemplate(id, seed, item.m_adjectiveList.Any(a => a.ToString() == "Seed"));
        }
        var seed = Read("GDN_LG_CouchPotatoes_Seed", 473092);
        var seedling = Read("GDN_LG_CouchPotatoes_Seedling", 473093);
        var young = Read("GDN_LG_CouchPotatoes_Adolescent", 473094);
        var mature = Read("GDN_LG_CouchPotatoes", 473095);
        Assert.Equal(GardenStage.Seed, seed.Stage);
        Assert.Equal(473093u, seed.NextStageTemplateId);
        Assert.Equal((GardenStage.Seedling, 473094u, 13u * 3600), (seedling.Stage, seedling.NextStageTemplateId, seedling.NextStageSeconds));
        Assert.Equal((GardenStage.Young, 473095u), (young.Stage, young.NextStageTemplateId));
        Assert.Equal((GardenStage.Mature, 473096u, 9u * 86400), (mature.Stage, mature.NextStageTemplateId, mature.NextStageSeconds));
        Assert.True(mature.HasNeeds);
        Assert.True(mature.HarvestMax >= mature.HarvestMin && mature.HarvestMin > 0);
        Assert.Equal(473097u, mature.WiltTemplateId);
        Assert.Equal(GardenSize.Large, mature.Size);
    }

    [Fact]
    public void TheClientsSoilAndWaterSpellsReadAsGardenSpells() {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "w101c-private/extract/r806919/root/Spells/Gardening Spells");
        if (!Directory.Exists(dir)) Assert.Skip("Requires the owned r806919 spell extraction; no game payload is committed.");
        GardenSpellInfo Read(string name) {
            Assert.True(new Imcodec.ObjectProperty.BindSerializer().Deserialize<SpellTemplate>(
                File.ReadAllBytes(Path.Combine(dir, name + ".xml")), 1, out var spell), name);
            return GardenCatalog.FromSpell(1, Assert.IsType<GardenSpellTemplate>(spell));
        }
        var soil = Read("Small Soil");
        Assert.Equal(GardenSpellKind.SoilPreparation, soil.Kind);
        Assert.Equal(346113u, soil.SoilTemplateId); // Prepared Soil - Small
        var mist = Read("Mist");
        Assert.Equal(GardenSpellKind.Growing, mist.Kind);
        Assert.True(mist.ProvidesAny);
        Assert.True(mist.EnergyCost > 0);
    }

    // ---- persistence ---------------------------------------------------------------------------------------------

    [Fact]
    public void PlantingSavesTheGardenAndTakesTheSeedTogether() {
        var store = new GardenStore(); using var scope = store.Scope(T0);
        var live = store.Login();
        Assert.Equal("Prepare the soil first.", GardenCollection.Plant(live, store.Room, SeedItem, 5001, 0, 0, 0).Error);
        Assert.Contains(5001ul, store.Wizard.InventoryBehavior.InventoryItemIds); // refused: nothing taken
        store.SeedSoil(0, 0, 0);
        var planted = GardenCollection.Plant(live, store.Room, SeedItem, 5001, 0, 0, 0);
        Assert.True(planted.Saved, planted.Error);
        Assert.Equal(5001ul, planted.RemovedItemId);
        Assert.DoesNotContain(5001ul, store.Wizard.InventoryBehavior.InventoryItemIds);
        Assert.DoesNotContain(5001ul, live.InventoryBehavior.InventoryItemIds);
        Assert.True(store.Documents.ContainsKey("item/5001")); // the original seed item document is retained
        var saved = store.Garden;
        Assert.Equal(Seedling, saved.Plants.Single(p => !p.IsSoil).TemplateId);
        Assert.Equal(5001ul, saved.Plants.Single(p => !p.IsSoil).SeedItemId);
        Assert.Equal(saved.Version, planted.Patches.Last().Version);
    }

    [Fact]
    public void AFailedSaveChangesNothing() {
        var store = new GardenStore(); using var scope = store.Scope(T0);
        store.SeedSoil(0, 0, 0);
        var live = store.Login();
        var before = store.Garden.Version;
        store.FailSave = true;
        Assert.False(GardenCollection.Plant(live, store.Room, SeedItem, 5001, 0, 0, 0).Saved);
        Assert.Equal(before, store.Garden.Version);
        Assert.Contains(5001ul, store.Wizard.InventoryBehavior.InventoryItemIds);
    }

    [Fact]
    public void TheGardenGrowsWhileNobodyIsHomeAndHarvestXpIsSaved() {
        var store = new GardenStore(); using var scope = store.Scope(T0);
        store.SeedSoil(0, 0, 0);
        var live = store.Login();
        Assert.True(GardenCollection.Plant(live, store.Room, SeedItem, 5001, 0, 0, 0).Saved);
        // Owner logs out for a day: the next visit catches the garden up from saved times.
        store.Clock = T0 + 13 * H + 3 * H;
        var caught = GardenCollection.Advance(store.Login(), store.Room);
        Assert.True(caught.Saved, caught.Error);
        Assert.Equal(Young, store.Garden.Plants.Single(p => !p.IsSoil).TemplateId);
        Assert.Contains(caught.Patches, p => p.Command == GardenRules.PatchAddPlant && p.TemplateOrFlags == Young);
        // A mature plant ready to harvest: XP is saved with the garden.
        var plant = store.Garden.Plants.Single(p => !p.IsSoil);
        plant.TemplateId = Mature; plant.Flags = 0; Array.Clear(plant.NeedAt); plant.NextStageAt = 0; plant.HarvestAt = store.Clock;
        var harvested = GardenCollection.Harvest(store.Login(), store.Room, 0, 0, 0);
        Assert.True(harvested.Saved, harvested.Error);
        Assert.Equal(10, store.Wizard.GameStats.m_gardeningXP);
        Assert.Equal(10, harvested.GardeningXp);
    }

    [Fact]
    public void AVisitorCannotGardenSomeoneElsesLot() {
        var store = new GardenStore(); using var scope = store.Scope(T0);
        store.SeedSoil(0, 0, 0);
        var visitor = store.Login(); visitor.CharId = GardenStore.Owner + 1;
        Assert.Equal("Only the owner can garden here.", GardenCollection.Plant(visitor, store.Room, SeedItem, 5001, 0, 0, 0).Error);
        Assert.Equal("Only the owner can garden here.", GardenCollection.Plow(visitor, store.Room, 0, 0, 0).Error);
        Assert.Equal("Only the owner can garden here.", GardenCollection.Harvest(visitor, store.Room, 0, 0, 0).Error);
    }

    private sealed class GardenStore {
        internal const ulong Owner = 724656;
        internal readonly Dictionary<string, object> Documents = new();
        internal bool FailSave;
        internal uint Clock;
        internal HousingRoomIdentity Room { get; } = new(Owner, 2, "Housing/WizardCity/WC_Tier1_Exterior");
        internal Wizard Wizard => (Wizard)Documents["wizard/1"];
        internal GardenLedger Garden => (GardenLedger)Documents[GardenLedger.DocumentId(Owner, 2, Room.Zone)];
        internal GardenStore() {
            Documents["wizard/1"] = new Wizard { CharId = Owner, AccountId = 1, Zone = Room.Zone,
                GameStats = new ServerWizGameStats(MagicSchool.Fire, 50) { m_currentGold = 100, m_baseGoldPouch = 1000 },
                MagicSchoolBehavior = new() { Level = 50 },
                InventoryBehavior = new() { InventoryItemIds = [5001], Items = new() },
                EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = new(), SlotList = [] },
                StorageBehavior = new() { BankItemIds = [], Items = new() } };
            Documents["item/5001"] = new WizClientObjectItem { m_globalID = 5001, m_templateID = SeedItem, m_characterId = Owner };
            Documents[GardenLedger.DocumentId(Owner, 2, Room.Zone)] = new GardenLedger { OwnerId = Owner, DeedId = 2, Zone = Room.Zone, PackageNumber = 40 };
        }
        internal void SeedSoil(float x, float y, float z) {
            var g = Garden; var e = new GardenEngine(GardenCatalog.Current, new Random(1));
            Assert.Null(e.PrepareSoil(g, Catalog().Spells[SoilSpell], x, y, z, Clock));
        }
        internal Wizard Login() {
            var live = Clone(Wizard);
            foreach (var id in live.InventoryBehavior.InventoryItemIds) live.InventoryBehavior.Items.Add((WizClientObjectItem)Documents[$"item/{id}"] with { });
            return live;
        }
        internal static Wizard Clone(Wizard w) => new() { CharId = w.CharId, AccountId = w.AccountId, Zone = w.Zone,
            GameStats = CloneStats(w.GameStats), MagicSchoolBehavior = new() { Level = w.MagicSchoolBehavior.Level },
            InventoryBehavior = new() { InventoryItemIds = [.. w.InventoryBehavior.InventoryItemIds], Items = new() },
            EquipmentBehavior = new() { EquippedItemIds = [], EquippedItems = new(), SlotList = [] },
            StorageBehavior = new() { BankItemIds = [], Items = new() } };
        private static ServerWizGameStats CloneStats(ServerWizGameStats s) {
            var c = s.CloneSnapshotWithGold(s.m_currentGold);
            c.m_gardeningXP = s.m_gardeningXP; c.m_gardeningLevel = s.m_gardeningLevel;
            return c;
        }
        internal IDisposable Scope(uint now) {
            Clock = now;
            var old = WizardCollection.TestStoreScope.Value; var oldRows = WizardInventoryTransactions.TestRowsScope.Value;
            var oldClock = GardenCollection.TestClock.Value;
            WizardCollection.TestStoreScope.Value = new(Open, (s, id) => id == Owner ? s.Load<Wizard>("wizard/1") : null!);
            WizardInventoryTransactions.TestRowsScope.Value = session => Documents.Where(p => p.Value is WizClientObjectItem)
                .Select(p => session.Load<WizClientObjectItem>(p.Key)).ToList();
            GardenCollection.TestClock.Value = () => Clock;
            return new Restore(() => {
                WizardCollection.TestStoreScope.Value = old; WizardInventoryTransactions.TestRowsScope.Value = oldRows;
                GardenCollection.TestClock.Value = oldClock;
            });
        }
        private IDocumentSession Open() {
            var s = DispatchProxy.Create<IDocumentSession, HouseTests.HouseSessionProxy>();
            var proxy = (HouseTests.HouseSessionProxy)(object)s;
            proxy.Saved = Documents; proxy.Fail = () => FailSave;
            return s;
        }
    }

    private sealed class Restore(System.Action undo) : IDisposable { public void Dispose() => undo(); }
}
