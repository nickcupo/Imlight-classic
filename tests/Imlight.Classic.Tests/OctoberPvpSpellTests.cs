// CLASSIC: defer live Cloak stock while retaining its researched temporary requirement for later use.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using Imlight.Classic.Pvp;
using Imlight.Classic.Spells;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.World;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class OctoberPvpSpellTests : IDisposable {
    public OctoberPvpSpellTests() {
        Profile("october-2010-arc1");
        var config = Path.Combine(Path.GetTempPath(), $"october-spell-tests-{Guid.NewGuid():N}.ini");
        File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}october-spell-tests.log\n");
        ConfigurationManager.Initialize(config);
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    private static void Profile(string profile) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(profile));
    }

    [Theory]
    [InlineData("october-2010-arc1", ClassicOctoberTraining.Diego, 0, true)]
    [InlineData("october-2010-arc1", ClassicOctoberTraining.Diego, -1, false)]
    [InlineData("october-2010-arc1", 38210ul, -1, true)]
    [InlineData("late-2009", ClassicOctoberTraining.Diego, -1, true)]
    [InlineData("arc1-2009h1", ClassicOctoberTraining.Diego, -1, true)]
    [InlineData("dev-unrestricted", ClassicOctoberTraining.Diego, -1, true)]
    public void TemporaryPrivateRequirementAppliesOnlyToOctoberDiegoCloak(string profile, ulong trainer, int offset, bool permitted) {
        Profile(profile);
        var arena = ArenaLoader.Load(Path.Combine(ClassicDataFixture.Root, "pvp", "arena-2009.yaml"));
        var rating = ArenaRules.MinRatingOf("Private", arena.Ranks) + offset;
        Assert.Equal(permitted, ClassicOctoberTraining.CanTrain(trainer, ClassicOctoberTraining.Cloak, 1, rating, arena));
        Assert.True(ClassicOctoberTraining.CanTrain(trainer, 1, 1, rating, arena)); // unrelated spells unchanged
    }

    [Fact]
    public void ExplicitResearchedRecordRestoresCloakWithoutChangingSabrinaOrDuplicatingStock() {
        var field = typeof(SpiralDB).GetField("s_npcSpellInventories", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        var records = ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "research-pending", "spells")).Records;
        try {
            for (var reload = 0; reload < 3; reload++) {
                field.SetValue(null, new ConcurrentDictionary<ulong, NPCSpellInventory>());
                var sabrina = new NPCSpellInventory { TemplateID = 38210, Spells = [] };
                var original = new NPCSpellInventory { TemplateID = ClassicOctoberTraining.Diego,
                    Spells = [new NPCSpellEntry { TemplateID = ClassicOctoberTraining.Cloak, Level = 50 }, new NPCSpellEntry { TemplateID = 1 }] };
                SpiralDB.RegisterNpcSpellInventory(sabrina);
                SpiralDB.RegisterNpcSpellInventory(original);
                ulong? Resolve(string path) => path == "Spells/Cloak.xml" ? ClassicOctoberTraining.Cloak : null;
                ClassicTrainerInventories.RefreshOctoberTraining(records, Resolve);
                ClassicTrainerInventories.RefreshOctoberTraining(records, Resolve);
                Assert.True(SpiralDB.TryGetNpcSpellInventory(ClassicOctoberTraining.Diego, out var diego));
                Assert.Equal(2, diego.Spells.Count);
                var cloak = Assert.Single(diego.Spells, entry => entry.TemplateID == ClassicOctoberTraining.Cloak);
                Assert.Equal(0, cloak.Level);
                Assert.Equal(0ul, cloak.RequiredSpellID);
                Assert.Equal(50, original.Spells[0].Level);
                Assert.Same(original.Spells[1], Assert.Single(diego.Spells, entry => entry.TemplateID == 1));
                Assert.True(SpiralDB.TryGetNpcSpellInventory(38210, out var unchanged));
                Assert.Same(sabrina, unchanged);
            }
        } finally { field.SetValue(null, previous); }
    }

    [Theory]
    [InlineData("late-2009")]
    [InlineData("arc1-2009h1")]
    [InlineData("dev-unrestricted")]
    public void CloakStockCorrectionLeavesOtherProfilesUnchanged(string profile) {
        Profile(profile);
        var field = typeof(SpiralDB).GetField("s_npcSpellInventories", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        var records = ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "spells")).Records;
        var original = new NPCSpellInventory { TemplateID = ClassicOctoberTraining.Diego, Spells = [] };
        try {
            field.SetValue(null, new ConcurrentDictionary<ulong, NPCSpellInventory>());
            SpiralDB.RegisterNpcSpellInventory(original);
            ClassicTrainerInventories.RefreshOctoberTraining(records, _ => ClassicOctoberTraining.Cloak);
            Assert.True(SpiralDB.TryGetNpcSpellInventory(ClassicOctoberTraining.Diego, out var after));
            Assert.Same(original, after);
        } finally { field.SetValue(null, previous); }
    }

    [Fact]
    public void OctoberTrainerWithholdsCloakAndConvictionUsingRealProfileAvailability() {
        const uint conviction = 2102149986;
        var book = ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "spells"));
        var overrides = new ClassicSpellOverrides(book, ClassicDataFixture.LoadProfile("october-2010-arc1"));
        var offered = InteractTrainerComponent.OfferedSpells(
            [new NPCSpellEntry { TemplateID = ClassicOctoberTraining.Cloak }, new NPCSpellEntry { TemplateID = conviction }],
            _ => true, id => overrides.IsTrainable(id == ClassicOctoberTraining.Cloak ? "Spells/Cloak.xml" : "Spells/Conviction.xml"));
        Assert.Empty(offered);
    }

    [Fact]
    public void EveryCurrentOctoberReloadRemovesNativeCloakWithoutChangingOtherStock() {
        var field = typeof(SpiralDB).GetField("s_npcSpellInventories", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        var records = ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "spells")).Records;
        Assert.DoesNotContain(records, record => record.Id == "spell.sun.cloak");
        var other = new NPCSpellEntry { TemplateID = 1 };
        var native = new NPCSpellInventory { TemplateID = ClassicOctoberTraining.Diego,
            Spells = [new NPCSpellEntry { TemplateID = ClassicOctoberTraining.Cloak }, other] };
        try {
            field.SetValue(null, new ConcurrentDictionary<ulong, NPCSpellInventory>());
            SpiralDB.RegisterNpcSpellInventory(native);
            for (var reload = 0; reload < 3; reload++) {
                ClassicTrainerInventories.RefreshOctoberTraining(records, _ => ClassicOctoberTraining.Cloak);
                Assert.True(SpiralDB.TryGetNpcSpellInventory(ClassicOctoberTraining.Diego, out var current));
                Assert.Same(other, Assert.Single(current.Spells));
            }
            Assert.Equal(2, native.Spells.Count);
        } finally { field.SetValue(null, previous); }
    }
}
