// CLASSIC: school-statistic vectors include the highest zero-based school and retain existing offsets.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using MagicSchoolTemplate = Imcodec.ObjectProperty.TypeCache.MagicSchoolTemplate;
using WizStatisticEffect = Imcodec.ObjectProperty.TypeCache.WizStatisticEffect;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class SchoolStatisticVectorBoundsTests {
    [Theory]
    [InlineData(false, "null")]
    [InlineData(false, "short")]
    [InlineData(false, "longer")]
    [InlineData(true, "null")]
    [InlineData(true, "short")]
    [InlineData(true, "longer")]
    public void HighestSchoolAddAndRemovePreserveAllOtherCells(bool sparse, string shape) {
        using var schools = new SchoolFixture(sparse);
        var original = InitialVector(shape); var before = original?.ToArray() ?? [];
        var unrelated = new List<float> { .13f, .17f };
        var stats = new ServerWizGameStats(default, 1) { m_dmgBonusPercent = original!, m_dmgReducePercent = unrelated,
            m_currentGold = 123, m_currentMana = 71, m_currentArenaPoints = 456 };
        var effect = new WizStatisticEffect { m_damageBonusPercent = .02f };

        CharacterEffectHelper.AddStatisticEffectToStats(stats, "CanonicalBalanceDamage", effect);
        Assert.Equal(Math.Max(7, before.Length), stats.m_dmgBonusPercent.Count);
        if (original is not null) Assert.Same(original, stats.m_dmgBonusPercent);
        for (var index = 0; index < stats.m_dmgBonusPercent.Count; index++)
            Assert.Equal((index < before.Length ? before[index] : 0) + (index == 6 ? .02f : 0), stats.m_dmgBonusPercent[index], 6);

        var applied = stats.m_dmgBonusPercent;
        CharacterEffectHelper.RemoveStatisticEffectFromStats(stats, "CanonicalBalanceDamage", effect);
        Assert.Same(applied, stats.m_dmgBonusPercent);
        Assert.Equal(Math.Max(7, before.Length), stats.m_dmgBonusPercent.Count);
        for (var index = 0; index < stats.m_dmgBonusPercent.Count; index++)
            Assert.Equal(index < before.Length ? before[index] : 0, stats.m_dmgBonusPercent[index], 6);
        Assert.Same(unrelated, stats.m_dmgReducePercent);
        Assert.Equal(new[] { .13f, .17f }, unrelated);
        Assert.Equal(123, stats.m_currentGold); Assert.Equal(71, stats.m_currentMana); Assert.Equal(456, stats.m_currentArenaPoints);
    }

    [Theory]
    [InlineData(false, "null")]
    [InlineData(false, "short")]
    [InlineData(false, "longer")]
    [InlineData(true, "null")]
    [InlineData(true, "short")]
    [InlineData(true, "longer")]
    public void RemovalGrowsPartialVectorsWithoutDiscardingOtherSchools(bool sparse, string shape) {
        using var schools = new SchoolFixture(sparse);
        var original = InitialVector(shape); var before = original?.ToArray() ?? [];
        var stats = new ServerWizGameStats(default, 1) { m_dmgReducePercent = original! };
        CharacterEffectHelper.RemoveStatisticEffectFromStats(stats, "CanonicalFireReduceDamage",
            new WizStatisticEffect { m_damageReducePercent = .05f });
        Assert.Equal(Math.Max(7, before.Length), stats.m_dmgReducePercent.Count);
        if (original is not null) Assert.Same(original, stats.m_dmgReducePercent);
        for (var index = 0; index < stats.m_dmgReducePercent.Count; index++)
            Assert.Equal((index < before.Length ? before[index] : 0) - (index == 0 ? .05f : 0), stats.m_dmgReducePercent[index], 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombatGetterIncludesHighestSchoolAcceptsLongerVectorsAndRejectsOneCellShort(bool sparse) {
        using var schools = new SchoolFixture(sparse);
        // This getter reads only its argument and the school resource. No duel, actors or game process is started.
        var circle = (CombatDuelSubCircle)RuntimeHelpers.GetUninitializedObject(typeof(CombatDuelSubCircle));
        var exact = Enumerable.Repeat(0f, 7).ToList(); exact[6] = .06f;
        Assert.Equal(.06f, circle.GetStatBySchool(exact, "Balance"));
        var longer = new List<float>(exact) { .17f, .23f };
        Assert.Equal(.06f, circle.GetStatBySchool(longer, "Balance"));
        Assert.Throws<ArgumentException>(() => circle.GetStatBySchool(exact.Take(6).ToList(), "Balance"));
        Assert.Equal(0f, circle.GetStatBySchool<float>(null!, "Balance"));
        Assert.Throws<ArgumentException>(() => circle.GetStatBySchool(Enumerable.Repeat("authored", 7).ToList(), "Balance"));
    }

    [Fact]
    public void OnlySchoolAtIndexZeroStillRequiresOneCell() {
        using var schools = new SchoolFixture(sparse: false, onlyFire: true);
        var stats = new ServerWizGameStats(default, 1);
        var effect = new WizStatisticEffect { m_accuracyBonusPercent = .05f };
        CharacterEffectHelper.AddStatisticEffectToStats(stats, "CanonicalFireAccuracy", effect);
        Assert.Equal(.05f, Assert.Single(stats.m_accBonusPercent));
        CharacterEffectHelper.RemoveStatisticEffectFromStats(stats, "CanonicalFireAccuracy", effect);
        Assert.Equal(0f, Assert.Single(stats.m_accBonusPercent));
    }

    private static List<float>? InitialVector(string shape) => shape switch {
        "null" => null,
        "short" => [.41f, .19f],
        "longer" => [.41f, .19f, .13f, .11f, .09f, .07f, .31f, .17f, .23f],
        _ => throw new ArgumentException("Unknown authored vector shape.", nameof(shape)),
    };

    private sealed class SchoolFixture : IDisposable {
        private readonly Dictionary<int, MagicSchoolTemplate> _schools;
        private readonly Dictionary<int, MagicSchoolTemplate> _previous;
        internal SchoolFixture(bool sparse, bool onlyFire = false) {
            var config = Path.Combine(Path.GetTempPath(), "w101c-school-vector-" + Guid.NewGuid().ToString("N") + ".ini");
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "w101c-school-vector.log")}\n[Character]\nBaseGoldPouch=1000\n");
            ConfigurationManager.Initialize(config); // Retain authored configuration; never delete fixture files.
            _schools = (Dictionary<int, MagicSchoolTemplate>)typeof(MagicSchools)
                .GetField("s_magicSchools", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            _previous = new(_schools); _schools.Clear();
            var names = new[] { "Fire", "Ice", "Storm", "Life", "Myth", "Death", "Balance" };
            IEnumerable<int> indices = onlyFire ? new[] { 0 } : sparse ? new[] { 0, 2, 6 } : Enumerable.Range(0, 7);
            foreach (var index in indices)
                _schools[index] = new MagicSchoolTemplate { m_schoolName = names[index], m_schoolIndex = index };
            Assert.Equal(onlyFire ? 0u : 6u, MagicSchools.GetMaxMagicSchoolIndex());
        }
        public void Dispose() {
            _schools.Clear(); foreach (var (index, school) in _previous) _schools[index] = school;
        }
    }
}
