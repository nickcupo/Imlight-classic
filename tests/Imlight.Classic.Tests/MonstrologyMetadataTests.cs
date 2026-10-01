using System;
using System.IO;
using System.Linq;
using Imlight.CoreLib.Game.Monstrology;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.ObjectProperty;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class MonstrologyMetadataTests(ITestOutputHelper output) {
    [Fact] public void AllOwnedExtractSpellsResolveExactCreatureAdjectiveFamilies() {
        var root = "/private/tmp/w101c-minion-metadata";
        if (!Directory.Exists(root)) Assert.Skip("Owned extraction fixtures unavailable.");
        var files = Directory.GetFiles(root,"CollectEssence*.xml");
        Assert.True(files.Length >= 30);
        var families = new System.Collections.Generic.HashSet<string>();
        foreach (var file in files) {
            Assert.True(new BindSerializer().Deserialize<SpellTemplate>(File.ReadAllBytes(file),1,out var template));
            Assert.True(MonstrologyPendingExtraction.TryFamily(template,out var family));
            Assert.Contains("Collect_"+family,template.m_adjectives);
            families.Add(family);
        }
        Assert.Equal(18,families.Count);
    }

    [Fact] public void OwnedXPConfigUsesClientRunningSumsRatherThanLiteralCostsAsThresholds() {
        var path = Environment.GetEnvironmentVariable("W101C_MONSTROLOGY_XP_CONFIG")
            ?? "/Users/nick/w101c-private/extract/r806919/ch1/root/MonsterMagicXPConfig.xml";
        if (!File.Exists(path)) Assert.Skip("Owned binary XP config unavailable.");
        var thresholds = MonstrologyProgression.Thresholds(System.IO.File.ReadAllBytes(path));
        Assert.Equal(new[] { 0, 0, 10, 30, 60, 110, 210 }, thresholds.Take(7));
        Assert.True(MonstrologyRules.ValidThresholds(thresholds));
        var state = new MonstrologyLedger { OwnerId = 42 };
        var award = new ExtractionAward("verified-progression-test",42,35085,1,10,true,true,true);
        Assert.Equal(MonstrologyResult.Applied, MonstrologyRules.Award(state,award,thresholds));
        Assert.Equal(2,state.Level);
    }

    [Fact] public void ActualOwnedXPConfigDecodesWithoutInventedThresholds() {
        var path = Environment.GetEnvironmentVariable("W101C_MONSTROLOGY_XP_CONFIG")
            ?? "/Users/nick/w101c-private/extract/r806919/ch1/root/MonsterMagicXPConfig.xml";
        if (!File.Exists(path)) Assert.Skip("Set W101C_MONSTROLOGY_XP_CONFIG to the owned binary XP config.");
        var levels = MonstrologyMetadata.ReadLevels(File.ReadAllBytes(path), out var max);
        Assert.NotEmpty(levels); Assert.True(max > 0);
        output.WriteLine("Monstrology config max=" + max + " levels=" + levels.Length);
        output.WriteLine(string.Join(";", levels.Select(x => x.Level + ":" + x.ExperienceValue)));
    }
    [Theory]
    [InlineData("SummonFirstGuardianTC1402223", kSpellEffects.kSummonCreature)]
    [InlineData("KillFirstGuardianTC1402223", kSpellEffects.kKillCreature)]
    public void OwnedMonstrologyCardsDecodeActualEffectTemplates(string file, kSpellEffects required) {
        var path = "/Users/nick/w101c-private/extract/r806919/spells_probe/Spells/MonsterMagicTC/" + file + ".xml";
        if (!File.Exists(path)) Assert.Skip("Owned Monstrology card resource unavailable.");
        Assert.True(new BindSerializer().Deserialize<SpellTemplate>(File.ReadAllBytes(path), 1, out var spell));
        Assert.Contains(spell.m_effects, effect => effect.m_effectType == required);
        output.WriteLine(spell.m_name + " type=" + spell.m_sTypeName + " front=" + spell.m_cardFront + " treasure=" + spell.m_Treasure + " adjectives=" + string.Join(",",spell.m_adjectives ?? new()) + " effects=" + string.Join(";", spell.m_effects.Select(e =>
            e.m_effectType + ":param=" + e.m_effectParam + ":template=" + e.m_spellTemplateID + ":type=" + e.m_sDamageType)));
    }
    [Fact] public void ActualManifestAndLostSoulCardsResolveByPathAndCreatureEffect() {
        const string root = "/private/tmp/w101c-minion-metadata/";
        if (!File.Exists(root + "TemplateManifest.xml")) Assert.Skip("Owned manifest unavailable.");
        var serializer = new BindSerializer();
        Assert.True(serializer.Deserialize<TemplateManifest>(File.ReadAllBytes(root + "TemplateManifest.xml"), 1, out var manifest));
        var inputs = new System.Collections.Generic.List<(uint, string, SpellTemplate)>();
        foreach (var name in new[] { "SummonLostSoulTC", "KillLostSoulTC" }) {
            Assert.True(serializer.Deserialize<SpellTemplate>(File.ReadAllBytes(root + name + ".xml"), 1, out var spell));
            var entry = Assert.Single(manifest.m_serializedTemplates.Where(x => x.m_filename.EndsWith(name + ".xml", StringComparison.OrdinalIgnoreCase)));
            output.WriteLine("Card manifest " + entry.m_id + " " + entry.m_filename);
            inputs.Add((entry.m_id,entry.m_filename,spell));
        }
        var cards = MonstrologyCardCatalog.Build(inputs);
        Assert.Equal(2, cards.Count);
        Assert.True(cards.ContainsKey((35085,MonstrologyCreationKind.SummonCard)));
        Assert.True(cards.ContainsKey((35085,MonstrologyCreationKind.KillCard)));
        Assert.Empty(MonstrologyCardCatalog.Build(inputs.Concat(inputs)));
    }
    [Theory]
    [InlineData("CollectEssenceUndead.xml")]
    [InlineData("CollectEssenceUndead 50.xml")]
    [InlineData("CollectEssenceWyrm.xml")]
    public void OwnedExtractionCardsExposeActualEffectParameters(string file) {
        var path = "/private/tmp/w101c-minion-metadata/" + file;
        if (!File.Exists(path)) Assert.Skip("Owned extraction resource unavailable.");
        Assert.True(new BindSerializer().Deserialize<SpellTemplate>(File.ReadAllBytes(path), 1, out var spell));
        Assert.Contains(spell.m_effects, effect => effect.m_effectType == kSpellEffects.kCollectEssence);
        output.WriteLine(spell.m_name + " school=" + spell.m_sMagicSchoolName + " type=" + spell.m_sTypeName
            + " effects=" + string.Join(";", spell.m_effects.Select(e =>
            e.m_effectType + ":param=" + e.m_effectParam + ":template=" + e.m_spellTemplateID + ":type=" + e.m_sDamageType)));
    }
    [Fact] public void ObservedCreationRequestKindsRejectZeroAndUseCorrectMetadataCosts() {
        Assert.False(MonstrologyCreation.TryKind(0, out _));
        Assert.False(MonstrologyCreation.TryKind(4, out _));
        var mob = new MonstrologyMob("fixture", 4, false, 10, 20, 30, 40, 50, 60, 7, 8, 9);
        foreach (var value in new[] { 1, 2, 3 }) Assert.True(MonstrologyCreation.TryKind(value, out _));
        Assert.Equal(new MonstrologyCreationCost(30,40,7), MonstrologyCreation.Cost(mob, MonstrologyCreationKind.HouseGuest));
        Assert.Equal(new MonstrologyCreationCost(50,60,0), MonstrologyCreation.Cost(mob, MonstrologyCreationKind.KillCard));
        Assert.Equal(new MonstrologyCreationCost(10,20,0), MonstrologyCreation.Cost(mob, MonstrologyCreationKind.SummonCard));
    }
    [Fact] public void ActualGeneratedMobFieldsArePreservedAndNegativeCostsRefused() {
        var source = new MobMonsterMagicBehaviorTemplate {
            m_worldName = "fixture", m_collectionResistance = 4, m_isBoss = true,
            m_essencesPerSummonTC = 10, m_goldPerSummonTC = 20,
            m_essencesPerHouseGuest = 30, m_goldPerHouseGuest = 40,
            m_essencesPerKillTC = 50, m_goldPerKillTC = 60,
            m_houseGuestTemplateID = 7, m_alternateMobTemplateID = 8, m_collectedAsTemplateID = 9
        };
        var result = MonstrologyMetadata.ReadMob(source);
        Assert.Equal(4, result.CollectionResistance); Assert.True(result.Boss);
        Assert.Equal(10, result.SummonAnimus); Assert.Equal(20, result.SummonGold);
        Assert.Equal(30, result.GuestAnimus); Assert.Equal(40, result.GuestGold);
        Assert.Equal(50, result.KillAnimus); Assert.Equal(60, result.KillGold);
        Assert.Equal(7U, result.GuestTemplate); Assert.Equal(8U, result.AlternateTemplate); Assert.Equal(9U, result.CollectedTemplate);
        source.m_goldPerSummonTC = -1;
        Assert.Throws<ArgumentException>(() => MonstrologyMetadata.ReadMob(source));
    }
}
