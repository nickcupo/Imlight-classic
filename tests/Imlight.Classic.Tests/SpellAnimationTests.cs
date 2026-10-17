// CLASSIC: the 2014 spell animations' stage lengths time the cast for the profiles the data names.
using System;
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Spells;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class SpellAnimationTests : IDisposable {

    public SpellAnimationTests() {
        var config = Path.Combine(Path.GetTempPath(), "w101c-anims-" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "w101c-anims.log")}\n");
        ConfigurationManager.Initialize(config);
    }

    public void Dispose() {
        ClassicSpellAnimations.Initialize(null, "late-2009");
        ClassicRuntime.ResetForTests();
    }

    private static void Use(string profile, string? root = null) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(profile));
        ClassicSpellAnimations.Initialize(root ?? ClassicDataFixture.Root, profile);
    }

    private static string RealPath => Path.Combine(ClassicDataFixture.Root, ClassicSpellAnimations.RelativePath);

    [Fact]
    public void TheDataListsTheRestoredCinematicsWithTheirStages() {
        var (profiles, templates) = ClassicSpellAnimations.Load(RealPath);
        Assert.Equal(["arc1-2009h1", "late-2009", "october-2010-arc1"], profiles.Order());
        var orthrus = templates["Orthrus"];
        Assert.Equal(["Summon", "Act1", "Act2"], orthrus.m_stages.Select(s => s.m_name));
        Assert.IsType<SummonCinematicStageTemplate>(orthrus.m_stages[0]);
        Assert.All(orthrus.m_stages.Skip(1), s => Assert.IsType<ActCinematicStageTemplate>(s));
        Assert.Equal(7.9f, orthrus.m_stages[2].m_duration, 3);
        Assert.Contains("Storm Lord", templates.Keys);
        Assert.Contains("Ra", templates.Keys);
        Assert.Contains("Bartleby", templates.Keys);       // Rebirth's cinematic
        Assert.DoesNotContain("Default", templates.Keys);
    }

    [Theory]
    [InlineData("october-2010-arc1")]
    [InlineData("late-2009")]
    [InlineData("arc1-2009h1")]
    public void ListedProfilesTimeTheCastFromThe2014Stages(string profile) {
        Use(profile);
        // Orthrus: 7.23 s summon, then both heads (4.53 s and 7.9 s); the longest act is what the server adds.
        Assert.Equal(7.233f, SpellCinematics.GetSpellSummonTime("Orthrus"), 3);
        Assert.Equal(7.9f, SpellCinematics.GetSpellActTime("Orthrus"), 3);
        // Storm Lord: the original 5.0 s attack, not r806919's AoE rebuild.
        Assert.Equal(5.0f, SpellCinematics.GetSpellActTime("Storm Lord"), 3);
        // Ra: 5.4 s summon and a 9.13 s act, the same Egyptian Ra; no weakness stage.
        var ra = SpellCinematics.GetCinematicTemplate("Ra");
        Assert.Equal(["Summon", "Act"], ra.m_stages.Select(s => s.m_name));
        Assert.Equal(9.133f, SpellCinematics.GetSpellActTime("Ra"), 3);
    }

    [Fact]
    public void UnrestrictedProfileAndUnlistedCinematicsKeepTheServersTemplates() {
        Use("dev-unrestricted");
        Assert.Null(ClassicSpellAnimations.Templates);
        Assert.False(ClassicSpellAnimations.TryGet("Orthrus", out _));

        Use("october-2010-arc1");
        Assert.False(ClassicSpellAnimations.TryGet("Kraken", out _));   // same in both clients: not listed
        Assert.False(ClassicSpellAnimations.TryGet(null, out _));
    }

    [Fact]
    public void AProfileTheDataDoesNotNameIsLeftAlone() {
        var root = Directory.CreateTempSubdirectory("w101c-anims-").FullName;
        try {
            Directory.CreateDirectory(Path.Combine(root, "rules"));
            File.WriteAllText(Path.Combine(root, ClassicSpellAnimations.RelativePath),
                File.ReadAllText(RealPath).Replace("\"profiles\": [\n    \"late-2009\",\n    \"arc1-2009h1\",\n    \"october-2010-arc1\"\n  ]",
                    "\"profiles\": [\"late-2009\"]"));
            Use("october-2010-arc1", root);
            Assert.Null(ClassicSpellAnimations.Templates);
            Use("late-2009", root);
            Assert.NotNull(ClassicSpellAnimations.Templates);
        }
        finally {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("\"type\": \"Summon\"", "\"type\": \"Bogus\"")]
    [InlineData("\"late-2009\",", "\"dev-unrestricted\",")]
    [InlineData("\"duration\": 7.233", "\"duration\": 700")]
    public void MalformedDataIsRefused(string from, string to) {
        var file = Path.Combine(Path.GetTempPath(), "w101c-anims-" + Guid.NewGuid().ToString("N") + ".json");
        try {
            var text = File.ReadAllText(RealPath);
            Assert.Contains(from, text);
            File.WriteAllText(file, text.Replace(from, to));
            Assert.Throws<InvalidDataException>(() => ClassicSpellAnimations.Load(file));
        }
        finally {
            File.Delete(file);
        }
    }

}
