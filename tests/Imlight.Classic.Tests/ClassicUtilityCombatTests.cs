using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Spells;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Xunit;
using CombatResolver = Imlight.CoreLib.Game.Combat.CombatResolver;

namespace Imlight.Classic.Tests;

// Production regressions for two engine gaps hidden by the 282/282 template audit.
// Does not assert full stun-immunity, profile-era stun rules, ward ordering or DoT-prism parity.
[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ClassicUtilityCombatTests : IDisposable {
    private const int IceIndex = 9001;
    private readonly Dictionary<int, MagicSchoolTemplate> _schools;

    public ClassicUtilityCombatTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-utility-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally {
            File.Delete(config);
        }
        _schools = (Dictionary<int, MagicSchoolTemplate>) typeof(MagicSchools)
            .GetField("s_magicSchools", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        _schools.Add(IceIndex, new MagicSchoolTemplate { m_schoolName = "Ice", m_schoolIndex = 1 });
    }

    public void Dispose() {
        _schools.Remove(IceIndex);
        ClassicRuntime.ResetForTests();
    }

    [Fact]
    public void RealClassicStunBlockCardActuallyBlocksExactlyOneStun() {
        var record = ClassicSpellLoader.Load(Path.Combine(ClassicDataFixture.Root, "spells")).FindByName("Stun Block")!;
        var overrides = new ClassicSpellOverrides(new ClassicSpellBook("spells", [record]), ClassicDataFixture.LoadProfile("late-2009"));
        var template = new SpellTemplate {
            m_name = "Stun Block", m_accuracy = 100, m_spellRank = new SpellRank(),
            m_effects = [Block(), Block()],
        };
        var plan = overrides.PlanFor(SpellTemplateEditor.ShapeOf(template, "Spells/Stun Block.xml"))!;
        SpellTemplateEditor.ApplyPlan(template, plan);
        var block = Assert.Single(template.m_effects);
        var target = Circle();
        Apply(block, target);
        Assert.Single(target._hangingEffects);
        Apply(Stun(), target);
        Assert.Equal(0, target.CombatParticipant.m_stunned);
        Assert.Empty(target._hangingEffects);
        Apply(Stun(), target);
        Assert.Equal(1, target.CombatParticipant.m_stunned);
        Assert.Equal(kSpellEffects.kStunBlock, Assert.Single(target._hangingEffects).m_effectType);
    }

    [Fact]
    public void AStunnedWizardWhoPassesUsesTheStunUp() {
        var target = Circle();
        Apply(Stun(), target);
        Assert.Equal(1, target.CombatParticipant.m_stunned);

        // The client lets a stunned wizard only pass; that pass must use the stun up.
        Resolve(target, new QueuedCombatAction { SpellCaster = target, Spell = null, SelectedTarget = null });
        Assert.Equal(0, target.CombatParticipant.m_stunned);

        // A pass by a wizard who is not stunned leaves the counter alone.
        Resolve(target, new QueuedCombatAction { SpellCaster = target, Spell = null, SelectedTarget = null });
        Assert.Equal(0, target.CombatParticipant.m_stunned);
    }

    [Fact]
    public void EachTargetOfAnAbsorbShieldHasItsOwnPoolAndTheCardIsUnchanged() {
        var a = Circle(); var b = Circle();
        var absorb = new SpellEffect { m_effectType = kSpellEffects.kAbsorbDamage, m_effectParam = 100, m_sDamageType = "Fire" };
        CombatEffectApplicator.ApplyEffect(absorb, [], a, [a, b]);

        Assert.NotSame(Assert.Single(a._hangingEffects), Assert.Single(b._hangingEffects));
        Assert.NotSame(absorb, a._hangingEffects[0]);
        a._hangingEffects[0].m_paramPerRound = 0;
        Assert.Equal(100, b._hangingEffects[0].m_paramPerRound);
        Assert.Equal(0, absorb.m_paramPerRound);
    }

    [Fact]
    public void RemoveAllCharmsDoesNotRewriteTheCard() {
        var target = Circle();
        var removeAll = new SpellEffect { m_effectType = kSpellEffects.kRemoveCharm, m_effectParam = -1 };
        Apply(removeAll, target);
        Assert.Equal(-1, removeAll.m_effectParam);
    }

    [Fact]
    public void BeforeJuly2009AStunLeavesNoStunBlock() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("arc1-2009h1"));
        var target = Circle();
        Apply(Stun(), target);
        Assert.Equal(1, target.CombatParticipant.m_stunned);
        Assert.Empty(target._hangingEffects);
    }

    [Fact]
    public void TwoCastStunBlocksAreConsumedOneAtATimeAndDoNotAbsorbDamage() {
        var target = Circle();
        Apply(Block(), target);
        Apply(Block(), target);
        Damage(target, "Fire", 100);
        Assert.Equal(900, target.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal(2, target._hangingEffects.Count);
        Apply(Stun(), target);
        Assert.Single(target._hangingEffects);
        Assert.Equal(0, target.CombatParticipant.m_stunned);
        Apply(Stun(), target);
        Assert.Empty(target._hangingEffects);
        Assert.Equal(0, target.CombatParticipant.m_stunned);
    }

    [Fact]
    public void MatchingPrismConvertsOnlyTheFirstHitAndIsConsumedWithoutScalingDamage() {
        var target = Circle();
        Apply(Prism(), target);
        var applied = CombatWards.GetWardsBySchool(target._hangingEffects.ToArray(), "Fire", out var school);
        Assert.Equal("Ice", school);
        Assert.Single(applied);
        Assert.Equal(100, CombatWards.GetIncomingDamageFromWards(applied, 100)); // 9001 is not a damage percentage.
        Damage(target, "Fire", 100);
        Assert.Equal(900, target.ParticipantGameStats.m_currentHitpoints);
        Assert.Empty(target._hangingEffects);
        Assert.Empty(CombatWards.GetWardsBySchool(target._hangingEffects.ToArray(), "Fire", out school));
        Assert.Equal("Fire", school);
        Damage(target, "Fire", 100);
        Assert.Equal(800, target.ParticipantGameStats.m_currentHitpoints);
    }

    [Theory]
    [InlineData("Ice")]
    [InlineData("Storm")]
    [InlineData("Myth")]
    public void WrongSchoolHitDoesNotTriggerOrConsumePrism(string school) {
        var target = Circle();
        var prism = Prism();
        Apply(prism, target);
        var applied = CombatWards.GetWardsBySchool([prism], school, out var finalSchool);
        Assert.Empty(applied);
        Assert.Equal(school, finalSchool);
        Damage(target, school, 100);
        Assert.Equal(900, target.ParticipantGameStats.m_currentHitpoints);
        var hanging = Assert.Single(target._hangingEffects);
        Assert.Equal(prism.m_effectType, hanging.m_effectType);
        Assert.Equal(prism.m_spellTemplateID, hanging.m_spellTemplateID);
        Damage(target, "Fire", 100);
        Assert.Empty(target._hangingEffects);
    }

    [Fact]
    public void MissingConversionSchoolDoesNotConsumePrismOrRevertAnEarlierConversion() {
        var valid = Prism();
        var unknown = Prism() with { m_effectParam = int.MaxValue, m_sDamageType = "Ice" };
        var applied = CombatWards.GetWardsBySchool([valid, unknown], "Fire", out var school);
        Assert.Equal("Ice", school);
        Assert.Same(valid, Assert.Single(applied));
    }

    [Fact]
    public void ConvertedHitUsesFollowingConvertedSchoolShieldAndConsumesBoth() {
        var target = Circle();
        var iceShield = new SpellEffect {
            m_effectType = kSpellEffects.kModifyIncomingDamage, m_sDamageType = "Ice", m_effectParam = -50,
            m_spellTemplateID = 1002,
        };
        Apply(iceShield, target);
        Apply(Prism(), target);
        Damage(target, "Fire", 100);
        Assert.Equal(950, target.ParticipantGameStats.m_currentHitpoints);
        Assert.Empty(target._hangingEffects);
    }

    [Theory]
    [InlineData(true, kSpellEffects.kRemoveWard)]
    [InlineData(true, kSpellEffects.kStealWard)]
    [InlineData(false, kSpellEffects.kRemoveWard)]
    [InlineData(false, kSpellEffects.kStealWard)]
    public void UtilityDispositionSelectsShieldInClassicButPreservesStockTrapSelection(bool classic, kSpellEffects kind) {
        Profile(classic);
        var target = Circle(); var caster = Circle();
        var trap = Ward(30, "Fire", 201); var shield = Ward(-50, "Fire", 202);
        target._hangingEffects.AddRange([trap, shield]);
        CombatEffectApplicator.ApplyEffect(new SpellEffect { m_effectType = kind, m_effectParam = 1,
            m_disposition = kHangingDisposition.kBeneficial }, [], caster, [target]);
        Assert.Same(classic ? trap : shield, Assert.Single(target._hangingEffects));
        if (kind == kSpellEffects.kStealWard) Assert.Same(classic ? shield : trap, Assert.Single(caster._hangingEffects));
    }

    [Theory]
    [InlineData(kHangingDisposition.kBeneficial, 2)]
    [InlineData(kHangingDisposition.kHarmful, 2)]
    [InlineData(kHangingDisposition.kBoth, 4)]
    public void RemoveAllHonorsDispositionAndKeepsStunBlocks(kHangingDisposition disposition, int removed) {
        var target = Circle();
        var shield = Ward(-50, "Fire", 201); var absorb = new SpellEffect { m_effectType = kSpellEffects.kAbsorbDamage };
        var trap = Ward(30, "Fire", 202); var prism = Prism(); var block = Block();
        target._hangingEffects.AddRange([shield, absorb, trap, prism, block]);
        Apply(new SpellEffect { m_effectType = kSpellEffects.kRemoveWard, m_effectParam = -1, m_disposition = disposition }, target);
        Assert.Equal(5 - removed, target._hangingEffects.Count);
        Assert.Contains(block, target._hangingEffects);
        if (disposition == kHangingDisposition.kBeneficial) Assert.Contains(trap, target._hangingEffects);
        if (disposition == kHangingDisposition.kHarmful) Assert.Contains(shield, target._hangingEffects);
    }

    [Fact]
    public void OneWardMeansOneEvenForIdenticalDuplicatesAndSelectionIsNewestFirst() {
        var target = Circle();
        target._hangingEffects.AddRange([Ward(-50, "Fire", 201), Ward(-50, "Fire", 201)]);
        Apply(new SpellEffect { m_effectType = kSpellEffects.kRemoveWard, m_effectParam = 1, m_disposition = kHangingDisposition.kBeneficial }, target);
        Assert.Single(target._hangingEffects);
        var latest = Ward(-80, "Ice", 202); target._hangingEffects.Add(latest);
        Assert.Same(latest, CombatWards.FindRemovableWards(target, kHangingDisposition.kBeneficial)[0]);
        Assert.All(CombatWards.FindAppliedWards(target, new SpellEffect(), kHangingDisposition.kBeneficial), w => Assert.True(w.m_effectParam < 0));
    }

    [Theory]
    [InlineData(true, "Ice", -50, 800)]
    [InlineData(false, "Ice", -50, 900)]
    [InlineData(true, "Fire", 30, 770)]
    [InlineData(false, "Fire", 30, 740)]
    public void ProductionDotTickFiltersAndConsumesOnlyInClassic(bool classic, string school, int modifier, int expected) {
        Profile(classic);
        var target = Circle(); var ward = Ward(modifier, school, 201);
        target._hangingEffects.AddRange([ward, Dot(100, 2)]);
        Tick(target); Tick(target);
        Assert.Equal(expected, target.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal(!classic || school != "Fire", target._hangingEffects.Contains(ward));
    }

    [Fact]
    public void DotPartialAbsorbSurvivesUntilExhaustedAndMatchingPrismIsConsumed() {
        var target = Circle();
        var absorb = new SpellEffect { m_effectType = kSpellEffects.kAbsorbDamage, m_sDamageType = "All",
            m_effectParam = 150, m_paramPerRound = 150, m_spellTemplateID = 202 };
        target._hangingEffects.AddRange([absorb, Prism(), Dot(100, 2)]);
        Tick(target);
        Assert.Equal(1000, target.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal(50, absorb.m_paramPerRound);
        Assert.DoesNotContain(target._hangingEffects, w => w.m_effectType == kSpellEffects.kModifyIncomingDamageType);
        Tick(target);
        Assert.Equal(950, target.ParticipantGameStats.m_currentHitpoints);
        Assert.Empty(target._hangingEffects);
    }

    [Theory]
    [InlineData(true, -100, 0)]
    [InlineData(false, -100, -90)]
    [InlineData(true, 100, 50)]
    public void ProductionHotUsesClassicHealingBounds(bool classic, int heal, int expected) {
        Profile(classic); var target = Circle();
        target.ParticipantGameStats.m_currentHitpoints = 10; target.ParticipantGameStats.m_baseHitpoints = 50;
        target._hangingEffects.Add(new SpellEffect { m_effectType = kSpellEffects.kHealOverTime, m_paramPerRound = heal, m_numRounds = 1 });
        Tick(target); Assert.Equal(expected, target.ParticipantGameStats.m_currentHitpoints);
    }

    [Fact]
    public void DirectCombatHealingCannotOverflowHealthAddition() {
        var target = Circle(); target.ParticipantGameStats.m_currentHitpoints = int.MaxValue - 10;
        target.ParticipantGameStats.m_baseHitpoints = int.MaxValue;
        Apply(new SpellEffect { m_effectType = kSpellEffects.kHeal, m_effectParam = 100 }, target);
        Assert.Equal(int.MaxValue, target.ParticipantGameStats.m_currentHitpoints);
        target.ParticipantGameStats.m_currentHitpoints = 10;
        Apply(new SpellEffect { m_effectType = kSpellEffects.kHeal, m_effectParam = -100 }, target);
        Assert.Equal(0, target.ParticipantGameStats.m_currentHitpoints);
    }

    [Fact]
    public void DotConsumesOnlyOneOfIdenticalDuplicateShieldsPerTick() {
        var target = Circle(); var shield = Ward(-50, "Fire", 201);
        target._hangingEffects.AddRange([shield, shield with { }, Dot(100, 2)]);
        Tick(target);
        Assert.Equal(950, target.ParticipantGameStats.m_currentHitpoints);
        Assert.Single(target._hangingEffects, w => w.m_effectType == kSpellEffects.kModifyIncomingDamage);
        Tick(target);
        Assert.Equal(900, target.ParticipantGameStats.m_currentHitpoints);
        Assert.Empty(target._hangingEffects);
    }

    [Fact]
    public void DrainAndHotCannotOverflowHealthAddition() {
        var caster = Circle(); var victim = Circle();
        caster.ParticipantGameStats.m_baseHitpoints = int.MaxValue;
        caster.ParticipantGameStats.m_currentHitpoints = int.MaxValue - 10;
        CombatEffectApplicator.ApplyEffect(new SpellEffect { m_effectType = kSpellEffects.kStealHealth,
            m_effectParam = 100, m_sDamageType = "Fire", m_healModifier = 1 }, [], caster, [victim]);
        Assert.Equal(int.MaxValue, caster.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal(900, victim.ParticipantGameStats.m_currentHitpoints);
        caster.ParticipantGameStats.m_currentHitpoints = int.MaxValue - 10;
        caster._hangingEffects.Add(new SpellEffect { m_effectType = kSpellEffects.kHealOverTime, m_paramPerRound = 100, m_numRounds = 1 });
        Tick(caster);
        Assert.Equal(int.MaxValue, caster.ParticipantGameStats.m_currentHitpoints);
    }

    private static void Profile(bool classic) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(classic ? "late-2009" : "dev-unrestricted"));
    }
    private static SpellEffect Ward(int amount, string school, uint id) => new() {
        m_effectType = kSpellEffects.kModifyIncomingDamage, m_effectParam = amount, m_sDamageType = school, m_spellTemplateID = id,
    };
    private static SpellEffect Dot(int amount, int rounds) => new() {
        m_effectType = kSpellEffects.kDamageOverTime, m_paramPerRound = amount, m_numRounds = rounds, m_sDamageType = "Fire",
    };
    private static void Tick(CombatDuelSubCircle target) {
        var resolver = new CombatResolver(target._duelActor.Duel, [target]);
        typeof(CombatResolver).GetMethod("InvokeOverTimeEffects", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(resolver, [target]);
    }

    private static void Resolve(CombatDuelSubCircle caster, QueuedCombatAction action) {
        var resolver = new CombatResolver(caster._duelActor.Duel, [caster]);
        resolver.Reset();
        var queue = (List<QueuedCombatAction>) typeof(CombatResolver)
            .GetField("_queuedCombatActions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resolver)!;
        queue.Add(action);
        typeof(CombatResolver).GetMethod("ProcessQueuedActions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(resolver, [new CombatActionListObj { m_actionList = [] }]);
    }

    private static SpellEffect Block() => new() {
        m_effectType = kSpellEffects.kStunBlock, m_effectTarget = kEffectTarget.kFriendlySingle,
    };
    private static SpellEffect Stun() => new() {
        m_effectType = kSpellEffects.kStun, m_effectTarget = kEffectTarget.kEnemySingle, m_numRounds = 1,
    };
    private static SpellEffect Prism() => new() {
        m_effectType = kSpellEffects.kModifyIncomingDamageType, m_sDamageType = "Fire",
        m_effectParam = IceIndex, m_effectTarget = kEffectTarget.kEnemySingle, m_spellTemplateID = 1001,
    };
    private static void Apply(SpellEffect effect, CombatDuelSubCircle target)
        => CombatEffectApplicator.ApplyEffect(effect, [], target, [target]);
    private static void Damage(CombatDuelSubCircle target, string school, int amount)
        => Apply(new SpellEffect { m_effectType = kSpellEffects.kDamage, m_sDamageType = school, m_effectParam = amount }, target);

    private static CombatDuelSubCircle Circle() {
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
        var circle = duel.SubCircles[0];
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = stats.m_currentHitpoints = 1000;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant { m_hangingEffects = [] });
        return circle;
    }
}
