// CLASSIC: Prospector Zeke's October 2010 1 Day mounts (price and +20% speed), October profile only.
using System;
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class MountTemplateTests : IDisposable {

    public MountTemplateTests() {
        var config = Path.Combine(Path.GetTempPath(), "w101c-mounts-" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "w101c-mounts.log")}\n");
        ConfigurationManager.Initialize(config);
    }

    public void Dispose() {
        ClassicMountTemplates.Initialize(null, "late-2009");
        ClassicRuntime.ResetForTests();
    }

    private static void Use(string profile) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(profile));
        ClassicMountTemplates.Initialize(ClassicDataFixture.Root, profile);
    }

    private static WizItemTemplate Native(uint id, float cost)
        => new() { m_templateID = id, m_baseCost = cost, m_equipEffects = [new SpeedEffectInfo { m_effectName = "SpeedBuff", m_speedMultiplier = 40 }] };

    [Fact]
    public void TheDataListsTheSixHorseAndBroomRentals() {
        var mounts = ClassicMountTemplates.Load(Path.Combine(ClassicDataFixture.Root, ClassicMountTemplates.RelativePath));
        Assert.Equal([191154u, 191221u, 191222u, 191225u, 191232u, 191233u], mounts.Keys.Order());
        Assert.All(mounts.Values, m => Assert.Equal(20, m.Speed));
        Assert.All(mounts.Values, m => Assert.Equal(40, m.NativeSpeed));
        Assert.Equal(1429f, mounts[191154].BaseCost);
        Assert.All(mounts.Values.Where(m => m.Id != 191154), m => Assert.Equal(2000f, m.BaseCost));
    }

    [Theory]
    [InlineData(191222u, "ObjectData/Mounts/Mount-HorseBasic-001.xml", 1500f, 2000f)]
    [InlineData(191221u, "ObjectData/Mounts/Mount-Broom-Basic-002.xml", 1000f, 2000f)]
    [InlineData(191154u, "ObjectData/Mounts/Mount-Broom-Basic-001.xml", 1429f, 1429f)]
    public void OctoberRentalsCostTheDatedPriceAndGiveTwentyPercent(uint id, string path, float native, float dated) {
        Use("october-2010-arc1");
        var item = Native(id, native);
        ClassicMountTemplates.Apply(item, path);
        Assert.Equal(dated, item.m_baseCost);
        Assert.Equal(20, Assert.IsType<SpeedEffectInfo>(Assert.Single(item.m_equipEffects)).m_speedMultiplier);
        ClassicMountTemplates.Apply(item, path); // idempotent
        Assert.Equal(dated, item.m_baseCost);
        Assert.Equal(20, ((SpeedEffectInfo) item.m_equipEffects[0]).m_speedMultiplier);
    }

    [Fact]
    public void PermanentMountsAndUnexpectedTemplatesAreLeftAlone() {
        Use("october-2010-arc1");
        var permanent = Native(191237, 50000);
        ClassicMountTemplates.Apply(permanent, "ObjectData/Mounts/Mount-HorseBasic-007.xml");
        Assert.Equal(40, ((SpeedEffectInfo) permanent.m_equipEffects[0]).m_speedMultiplier);
        Assert.Equal(50000f, permanent.m_baseCost);

        var odd = Native(191222, 1234);
        ClassicMountTemplates.Apply(odd, "ObjectData/Mounts/Mount-HorseBasic-001.xml");
        Assert.Equal(1234f, odd.m_baseCost);
        Assert.Equal(40, ((SpeedEffectInfo) odd.m_equipEffects[0]).m_speedMultiplier);

        var moved = Native(191222, 1500);
        ClassicMountTemplates.Apply(moved, "ObjectData/Mounts/Elsewhere.xml");
        Assert.Equal(1500f, moved.m_baseCost);
    }

    [Theory]
    [InlineData("late-2009")]
    [InlineData("arc1-2009h1")]
    [InlineData("dev-unrestricted")]
    public void OlderProfilesKeepTheNativeTemplate(string profile) {
        Use(profile);
        Assert.Null(ClassicMountTemplates.Mounts);
        var item = Native(191222, 1500);
        ClassicMountTemplates.Apply(item, "ObjectData/Mounts/Mount-HorseBasic-001.xml");
        Assert.Equal(1500f, item.m_baseCost);
        Assert.Equal(40, ((SpeedEffectInfo) item.m_equipEffects[0]).m_speedMultiplier);
    }

    [Fact]
    public void ZekeSellsOnlyTheSixRentalsInEveryOpenWorld() {
        var dir = Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "NpcInventory");
        var zekes = Directory.GetFiles(dir, "*-ProspectorZeke.json");
        Assert.Equal(6, zekes.Length);
        var mounts = ClassicMountTemplates.Load(Path.Combine(ClassicDataFixture.Root, ClassicMountTemplates.RelativePath));
        foreach (var file in zekes) {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var stock = doc.RootElement.GetProperty("Inventory").EnumerateArray().Select(e => e.GetUInt32()).Order();
            Assert.Equal(mounts.Keys.Order(), stock);
        }
    }

}
