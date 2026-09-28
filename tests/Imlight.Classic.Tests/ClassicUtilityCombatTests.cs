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
        Assert.Same(prism, Assert.Single(target._hangingEffects));
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
