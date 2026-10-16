using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Xunit;

namespace Imlight.Classic.Tests;

// Work package B: the 2010-2014 critical and block model of celestia-2010 (rules/critical-2010.yaml, the 2014
// client's percent model; docs/research/crit-block-2010.md). Older profiles keep crits off.
[Collection(nameof(ClassicRuntimeCollection))]
public sealed class CriticalModel2010Tests : IDisposable {
    private const string Celestia = "celestia-2010";
    private const string October = "october-2010-arc1";
    private const int FireIndex = 2;
    private const int StormIndex = 3;
    private readonly Dictionary<int, MagicSchoolTemplate> _schools;
    private readonly List<int> _added = [];
    private readonly Func<CombatDuelSubCircle, int> _critHook = CombatCriticals.AuraCritBoostPoints;
    private readonly Func<CombatDuelSubCircle, int> _blockHook = CombatCriticals.AuraBlockBoostPoints;

    public CriticalModel2010Tests() {
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-crit2010-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }

        _schools = (Dictionary<int, MagicSchoolTemplate>) typeof(MagicSchools)
            .GetField("s_magicSchools", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        AddSchool(FireIndex, "Fire", 2);
        AddSchool(StormIndex, "Storm", 3);
        UseProfile(Celestia);
    }

    public void Dispose() {
        foreach (var key in _added) {
            _schools.Remove(key);
        }

        CombatCriticals.AuraCritBoostPoints = _critHook;
        CombatCriticals.AuraBlockBoostPoints = _blockHook;
        ClassicProgression.UseCriticalModelForTests(null);
        ClassicRuntime.ResetForTests();
    }

    private void AddSchool(int key, string name, int index) {
        if (_schools.Values.Any(s => s.m_schoolName == name) || _schools.ContainsKey(key)) {
            return;
        }

        _schools.Add(key, new MagicSchoolTemplate { m_schoolName = name, m_schoolIndex = index });
        _added.Add(key);
    }

    private static CriticalModel RealModel()
        => CriticalModelLoader.Load(Path.Combine(ClassicDataFixture.Root, "rules", "critical-2010.yaml"));

    private static void UseProfile(string id) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(id));
        var profile = ClassicDataFixture.LoadProfile(id);
        ClassicProgression.UseCriticalModelForTests(profile.Rules.Critical is { } path
            ? CriticalModelLoader.Load(Path.Combine(ClassicDataFixture.Root, path)) : null);
    }

    // ---------------------------------------------------------------- the file and the formula

    [Fact]
    public void TheRealFileHoldsThe2014ClientValues() {
        var model = RealModel();
        Assert.Equal("critical-2010", model.Id);
        Assert.Equal(new[] { Celestia }, model.Profiles);
        Assert.Equal("unverified", model.Status);
        Assert.Equal(0.00225, model.CritPerRating, 9);
        Assert.Equal(0.00275, model.BlockPerRating, 9);
        Assert.Equal(0.001, model.CritLevelPenalty, 9);
        Assert.Equal(0.001, model.BlockLevelPenalty, 9);
        Assert.Equal(50, model.CritPenaltyFromLevel);
        Assert.Equal(50, model.BlockPenaltyFromLevel);
        Assert.Equal(0.01, model.MinPieceChance, 9);
        Assert.Equal(2, model.CritMultiplier);
        Assert.Equal(99, model.RollMax);
    }

    [Theory]
    [InlineData(30, 50, 0.0675)]   // 30 x 0.00225, no penalty at 50
    [InlineData(30, 40, 0.0675)]   // below 50 no penalty and no bonus
    [InlineData(30, 55, 0.0625)]   // minus 5 x 0.001
    [InlineData(30, 60, 0.0575)]
    [InlineData(2, 60, 0.01)]      // 0.0045 - 0.01 is below 0: the 1% floor of a piece with a rating
    [InlineData(5, 50, 0.01125)]
    [InlineData(4, 50, 0.01)]      // 0.009: floored to 1%
    [InlineData(0, 60, 0.0)]       // no rating, no floor
    public void ACriticalPieceConvertsWithTheWearersLevel(double rating, int level, double expected)
        => Assert.Equal(expected, RealModel().CritPieceChance(rating, level), 9);

    [Theory]
    [InlineData(14, 50, 0.0385)]   // the notes' "+14 Block Rating": 14 x 0.00275
    [InlineData(14, 55, 0.0335)]
    [InlineData(3, 60, 0.01)]
    public void ABlockPieceConvertsWithTheWearersLevel(double rating, int level, double expected)
        => Assert.Equal(expected, RealModel().BlockPieceChance(rating, level), 9);

    [Fact]
    public void TheRollRunsToNinetyNineOrTheChanceAndTheBlockUsesTheSameRoll() {
        var model = RealModel();
        Assert.Equal(49.5, model.Roll(30, 0.5), 9);
        Assert.Equal(60, model.Roll(120, 0.5), 9); // a chance above 99 widens the roll; it still always crits
        Assert.True(CriticalModel.Crits(30, 29.9));
        Assert.False(CriticalModel.Crits(30, 30));
        Assert.True(CriticalModel.Blocks(20, 10));
        Assert.False(CriticalModel.Blocks(20, 25));
    }

    [Fact]
    public void TheCritRateIsTheChanceOverNinetyNine() {
        var (caster, _) = Pair(Celestia);
        Equip(caster, "CanonicalAllCriticalHit", crit: 100, level: 50); // 22.5%
        typeof(CombatDuelComponent).GetField("_rng", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(caster._duelActor, new Random(20101027));
        var model = RealModel();
        var crits = 0;
        const int n = 40_000;
        for (var i = 0; i < n; i++) {
            if (CombatCriticals.ClassicRollsCritical(caster, model, "Fire", out _)) {
                crits++;
            }
        }

        Assert.InRange(crits / (double) n, 22.5 / 99 - 0.01, 22.5 / 99 + 0.01);
    }

    // ---------------------------------------------------------------- gear flow and the stats sent to the client

    [Fact]
    public void GearPiecesConvertOneByOneAndTheClientIsSentTheSum() {
        var stats = Stats(60);
        Add(stats, "CanonicalAllCriticalHit", crit: 10);
        Add(stats, "CanonicalAllCriticalHit", crit: 10);
        Add(stats, "CanonicalFireCriticalHit", crit: 30);
        Add(stats, "CanonicalAllBlock", block: 14);
        Assert.Equal(20f, stats.m_criticalHitRatingAll);

        var sent = stats.GetCombatGameStats();
        // Two pieces of 10 at 60: 2 x (0.0225 - 0.01) = 0.025, not one piece of 20 (0.035).
        Assert.Equal(0.025f, sent.m_criticalHitPercentAll, 5);
        Assert.Equal(0.0285f, sent.m_blockPercentAll, 5); // 14 x 0.00275 - 0.01
        Assert.Equal(0.0575f, sent.m_criticalHitPercentBySchool[2], 5); // Fire: 30 x 0.00225 - 0.01
        Assert.Equal(0f, sent.m_criticalHitPercentBySchool[3]);
        Assert.Equal(20f, sent.m_criticalHitRatingAll);

        // Taking a piece off takes its chance off.
        CharacterEffectHelper.RemoveStatisticEffectFromStats(stats, "CanonicalAllCriticalHit", new WizStatisticEffect { m_criticalHitRating = 10 });
        Assert.Equal(0.0125f, stats.GetCombatGameStats().m_criticalHitPercentAll, 5);

        // The level is read when the stats are sent, so levelling up lowers each piece's chance.
        stats.Level = 50;
        Assert.Equal(0.0225f, stats.GetCombatGameStats().m_criticalHitPercentAll, 5);

        CharacterEffectHelper.ResetRebuiltEquipmentEffects(stats);
        Assert.Null(stats.ClassicCritPieces);
        Assert.Equal(0f, stats.GetCombatGameStats().m_criticalHitPercentAll);
    }

    [Theory]
    [InlineData(October)]
    [InlineData("late-2009")]
    public void OlderProfilesSendNoCriticalOrBlock(string profile) {
        UseProfile(profile);
        Assert.Null(CombatCriticals.ActiveClassicModel);
        var stats = Stats(50);
        Add(stats, "CanonicalAllCriticalHit", crit: 50);
        Add(stats, "CanonicalAllBlock", block: 50);
        var sent = stats.GetCombatGameStats();
        Assert.Equal(0f, sent.m_criticalHitPercentAll);
        Assert.Equal(0f, sent.m_blockPercentAll);
        Assert.Equal(0f, sent.m_criticalHitRatingAll);
    }

    // ---------------------------------------------------------------- casts

    [Fact]
    public void ALandedCriticalDoublesDamageAndIsReported() {
        var (caster, target) = Pair(Celestia);
        Equip(caster, "CanonicalAllCriticalHit", crit: 400, level: 50); // 90%
        FixRoll(caster, 0.5); // roll 49.5
        var result = Cast(caster, target, Damage(100));
        Assert.Equal(800, target.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal((char) 2, result.m_spellHits);
        var hit = Assert.Single(result.m_CritHitList);
        Assert.Equal(2f, hit.m_mult);
        Assert.False(hit.m_blocked);
    }

    [Fact]
    public void ABlockAboveTheRollCancelsTheCriticalForThatTarget() {
        var (caster, target) = Pair(Celestia);
        Equip(caster, "CanonicalAllCriticalHit", crit: 400, level: 50); // 90 points
        Equip(target, "CanonicalAllBlock", block: 200, level: 50);      // 55 points, above the 49.5 roll
        FixRoll(caster, 0.5);
        var result = Cast(caster, target, Damage(100));
        Assert.Equal(900, target.ParticipantGameStats.m_currentHitpoints);
        var hit = Assert.Single(result.m_CritHitList);
        Assert.True(hit.m_blocked);
        Assert.Equal(0f, hit.m_mult);
    }

    [Fact]
    public void ABlockBelowTheRollDoesNotStopTheCritical() {
        var (caster, target) = Pair(Celestia);
        Equip(caster, "CanonicalAllCriticalHit", crit: 400, level: 50);
        Equip(target, "CanonicalAllBlock", block: 100, level: 50); // 27.5 points, below the 49.5 roll
        FixRoll(caster, 0.5);
        Cast(caster, target, Damage(100));
        Assert.Equal(800, target.ParticipantGameStats.m_currentHitpoints);
    }

    [Fact]
    public void HealsCriticalAndAreNeverBlocked() {
        var (caster, target) = Pair(Celestia);
        Equip(caster, "CanonicalAllCriticalHit", crit: 400, level: 50);
        Equip(caster, "CanonicalAllBlock", block: 400, level: 50);
        caster.ParticipantGameStats.m_currentHitpoints = 100;
        FixRoll(caster, 0.5);
        var result = Cast(caster, caster, new SpellEffect { m_effectType = kSpellEffects.kHeal, m_effectTarget = kEffectTarget.kSelf, m_effectParam = 100 });
        Assert.Equal(300, caster.ParticipantGameStats.m_currentHitpoints);
        Assert.False(Assert.Single(result.m_CritHitList).m_blocked);
    }

    [Fact]
    public void CharmsAndWardsNeverCritical() {
        var (caster, target) = Pair(Celestia);
        Equip(caster, "CanonicalAllCriticalHit", crit: 400, level: 50);
        FixRoll(caster, 0.0);
        var result = Cast(caster, target, new SpellEffect { m_effectType = kSpellEffects.kModifyIncomingDamage, m_effectTarget = kEffectTarget.kEnemySingle,
            m_sDamageType = "All", m_effectParam = 30 });
        Assert.True(result.m_CritHitList is null || result.m_CritHitList.Count == 0);
        Assert.NotEqual((char) 2, result.m_spellHits);
    }

    [Fact]
    public void DamageOverTimeCriticalsUnderThe2010Model() {
        var (caster, target) = Pair(Celestia);
        Equip(caster, "CanonicalAllCriticalHit", crit: 400, level: 50);
        FixRoll(caster, 0.5);
        Cast(caster, target, new SpellEffect { m_effectType = kSpellEffects.kDamageOverTime, m_effectTarget = kEffectTarget.kEnemySingle,
            m_sDamageType = "Fire", m_effectParam = 300, m_numRounds = 3 });
        Assert.Equal(200, Assert.Single(target._hangingEffects).m_paramPerRound); // 600 over 3 rounds
    }

    [Fact]
    public void TheSchoolPiecesCountOnlyForThatSchool() {
        var (caster, target) = Pair(Celestia);
        Equip(caster, "CanonicalStormCriticalHit", crit: 400, level: 50);
        FixRoll(caster, 0.5);
        Cast(caster, target, Damage(100)); // a Fire spell: the Storm piece does not apply
        Assert.Equal(900, target.ParticipantGameStats.m_currentHitpoints);
    }

    [Fact]
    public void AuraHooksAddPercentPointsToEachSide() {
        var (caster, target) = Pair(Celestia);
        CombatCriticals.AuraCritBoostPoints = sc => sc == caster ? 60 : 0;   // Vengeance-style
        FixRoll(caster, 0.5);
        Cast(caster, target, Damage(100));
        Assert.Equal(800, target.ParticipantGameStats.m_currentHitpoints);

        target.ParticipantGameStats.m_currentHitpoints = 1000;
        CombatCriticals.AuraBlockBoostPoints = sc => sc == target ? 55 : 0;  // Conviction-style
        var result = Cast(caster, target, Damage(100));
        Assert.Equal(900, target.ParticipantGameStats.m_currentHitpoints);
        Assert.True(Assert.Single(result.m_CritHitList).m_blocked);
    }

    [Theory]
    [InlineData(October)]
    [InlineData("late-2009")]
    public void OlderProfilesNeverCritical(string profile) {
        UseProfile(profile);
        var (caster, target) = Pair(profile);
        Equip(caster, "CanonicalAllCriticalHit", crit: 400, level: 50);
        FixRoll(caster, 0.0);
        var result = Cast(caster, target, Damage(100));
        Assert.Equal(900, target.ParticipantGameStats.m_currentHitpoints);
        Assert.NotEqual((char) 2, result.m_spellHits);
    }

    // ---------------------------------------------------------------- helpers

    private static SpellEffect Damage(int amount)
        => new() { m_effectType = kSpellEffects.kDamage, m_effectTarget = kEffectTarget.kEnemySingle, m_sDamageType = "Fire", m_effectParam = amount };

    private static ServerWizGameStats Stats(int level) {
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = stats.m_currentHitpoints = 1000;
        stats.Level = level;
        return stats;
    }

    private static void Add(ServerWizGameStats stats, string name, float crit = 0, float block = 0)
        => CharacterEffectHelper.AddStatisticEffectToStats(stats, name, new WizStatisticEffect { m_criticalHitRating = crit, m_blockRating = block });

    private static void Equip(CombatDuelSubCircle sc, string name, float crit = 0, float block = 0, int level = 50) {
        sc.ParticipantGameStats.Level = level;
        Add(sc.ParticipantGameStats, name, crit, block);
    }

    private static void FixRoll(CombatDuelSubCircle caster, double unit)
        => typeof(CombatDuelComponent).GetField("_rng", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(caster._duelActor, new FixedRandom(unit));

    private sealed class FixedRandom(double unit) : Random {
        public override double NextDouble() => unit;
        protected override double Sample() => unit;
    }

    private static (CombatDuelSubCircle Caster, CombatDuelSubCircle Target) Pair(string _) {
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
        return (Occupy(duel, 4, player: true), Occupy(duel, 0, player: false));
    }

    private static CombatDuelSubCircle Occupy(CombatDuelComponent duel, int slot, bool player) {
        var circle = duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject { m_templateID = player ? 1UL : 2UL });
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", ActorRefs.Nobody);
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = stats.m_currentHitpoints = 1000;
        stats.Level = 50;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant { m_hangingEffects = [] });
        circle.AddedToDuel = true;
        return circle;
    }

    private static CombatAction Cast(CombatDuelSubCircle caster, CombatDuelSubCircle target, SpellEffect effect) {
        const uint tid = uint.MaxValue - 2010;
        var template = new SpellTemplate { m_name = "Crit 2010 regression", m_spellRank = new SpellRank(), m_effects = [effect] };
        var cache = (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory).GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        cache[tid] = template;
        try {
            var spell = new Spell { m_templateID = tid, m_pipCost = new SpellRank(), m_magicSchoolID = StringHash.Compute("Fire") };
            var action = new QueuedCombatAction { SpellCaster = caster, SelectedTarget = target, Spell = spell, SpellTemplate = template };
            var result = new CombatAction { m_targetSubcircleList = [] };
            float seconds = 0;
            CombatActionResolver.ProcessedQueuedCombatAction(action, ref result, ref seconds);
            return result;
        } finally {
            cache.Remove(tid);
        }
    }
}
