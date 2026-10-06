// CLASSIC: March 9, 2009 firsthand PvP play confirms direct heals revive defeated teammates.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ClassicRevivalTests : IDisposable {
    public ClassicRevivalTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("october-2010-arc1"));
    }
    public void Dispose() => ClassicRuntime.ResetForTests();

    [Theory]
    [InlineData(kEffectTarget.kFriendlySingle)]
    [InlineData(kEffectTarget.kFriendlyTeam)]
    [InlineData(kEffectTarget.kFriendlyTeamAllAtOnce)]
    public void ADirectPositiveHealCanResolveOnADefeatedPresentTeammate(kEffectTarget targetKind) {
        var (caster, target) = Pair();
        Assert.True(CombatResolver.CanResolveTarget(Action(caster, target, kSpellEffects.kHeal, 400, targetKind)));
    }

    [Theory]
    [InlineData(kSpellEffects.kHealOverTime, 400, kEffectTarget.kFriendlySingle)]
    [InlineData(kSpellEffects.kHeal, 0, kEffectTarget.kFriendlySingle)]
    [InlineData(kSpellEffects.kHeal, -400, kEffectTarget.kFriendlySingle)]
    [InlineData(kSpellEffects.kDamage, 400, kEffectTarget.kEnemySingle)]
    [InlineData(kSpellEffects.kModifyOutgoingDamage, 35, kEffectTarget.kFriendlySingle)]
    [InlineData(kSpellEffects.kHeal, 400, kEffectTarget.kEnemySingle)]
    public void HoTsDamageBuffsAndNonpositiveHealsCannotRevive(kSpellEffects effect, int amount, kEffectTarget targetKind) {
        var (caster, target) = Pair();
        Assert.False(CombatResolver.CanResolveTarget(Action(caster, target, effect, amount, targetKind)));
    }

    [Fact]
    public void ARemovedFledEnemyOrCreatureTargetCannotBeRevived() {
        var (caster, target) = Pair();
        var heal = Action(caster, target, kSpellEffects.kHeal, 400, kEffectTarget.kFriendlySingle);
        target.AddedToDuel = false;
        Assert.False(CombatResolver.CanResolveTarget(heal));
        target.AddedToDuel = true;
        target.PvpTeam = CombatTeam.Monster;
        Assert.False(CombatResolver.CanResolveTarget(heal));
        target.PvpTeam = CombatTeam.Player;
        Set(target, "ParticipantObject", new CoreObject { m_templateID = 2UL });
        Assert.False(CombatResolver.CanResolveTarget(heal));
        Set(target, "ParticipantObject", null!);
        Assert.False(CombatResolver.CanResolveTarget(heal));
    }

    [Fact]
    public void NestedDirectHealingIsRecognizedWithoutAllowingHoTOnlyLists() {
        var (caster, target) = Pair();
        var action = Action(caster, target, kSpellEffects.kHeal, 400, kEffectTarget.kFriendlySingle);
        var direct = action.SpellTemplate.m_effects[0];
        action.SpellTemplate.m_effects = [new VariableSpellEffect { m_effectList = [direct] }];
        Assert.True(CombatResolver.CanResolveTarget(action));
        direct.m_effectType = kSpellEffects.kHealOverTime;
        Assert.False(CombatResolver.CanResolveTarget(action));
    }

    [Fact]
    public void ActualHealingRevivesAndClearsPreviousBuffsPipsStunAndBeguile() {
        var (_, target) = Pair();
        target._hangingEffects.Add(new SpellEffect { m_effectType = kSpellEffects.kModifyOutgoingDamage, m_effectParam = 35 });
        target.BeguiledActions = 1;
        target.CombatParticipant.m_stunned = 1;
        target.CombatParticipant.m_pipCount = new PipCount { m_genericPips = 3, m_powerPips = 4 };
        CombatEffectApplicator.HealParticipantBounded(target, 400);
        Assert.True(target.IsAlive);
        Assert.Equal(400, target.ParticipantGameStats.m_currentHitpoints);
        Assert.Empty(target._hangingEffects);
        Assert.Equal(0, target.CombatParticipant.m_pipCount.m_genericPips);
        Assert.Equal(0, target.CombatParticipant.m_pipCount.m_powerPips);
        Assert.Equal(0, target.CombatParticipant.m_stunned);
        Assert.Equal(0, target.BeguiledActions);
        target._hangingEffects.Add(new SpellEffect { m_effectType = kSpellEffects.kModifyIncomingDamage });
        target.CombatParticipant.m_pipCount.m_genericPips = 2;
        CombatEffectApplicator.HealParticipantBounded(target, int.MaxValue);
        Assert.Equal(1000, target.ParticipantGameStats.m_currentHitpoints);
        Assert.Single(target._hangingEffects); // Normal healing does not strip a living teammate.
        Assert.Equal(2, target.CombatParticipant.m_pipCount.m_genericPips);
    }

    private static QueuedCombatAction Action(CombatDuelSubCircle caster, CombatDuelSubCircle target,
        kSpellEffects kind, int amount, kEffectTarget targetKind) => new() {
        SpellCaster = caster, SelectedTarget = target,
        SpellTemplate = new SpellTemplate { m_effects = [new SpellEffect {
            m_effectType = kind, m_effectParam = amount, m_effectTarget = targetKind, m_sDamageType = "Life",
        }] },
    };

    private static (CombatDuelSubCircle, CombatDuelSubCircle) Pair() {
        var duel = new CombatDuelComponent(null!);
        return (Circle(duel, 4, 1000), Circle(duel, 5, 0));
    }
    private static CombatDuelSubCircle Circle(CombatDuelComponent duel, int slot, int health) {
        var circle = new CombatDuelSubCircle(duel, 0, 0, default, slot) { AddedToDuel = true };
        Set(circle, "ParticipantObject", new CoreObject { m_templateID = 1UL });
        Set(circle, "ParticipantActor", ActorRefs.Nobody);
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = 1000;
        stats.m_currentHitpoints = health;
        Set(circle, "ParticipantGameStats", stats);
        Set(circle, "CombatParticipant", new CombatParticipant { m_hangingEffects = [], m_pipCount = new PipCount() });
        return circle;
    }
    private static void Set(object instance, string name, object value)
        => instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
}
