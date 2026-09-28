using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ClassicMinionOwnershipTests : IDisposable {
    public ClassicMinionOwnershipTests() {
        Profile(true);
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-minion-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally { File.Delete(config); }
    }
    public void Dispose() => ClassicRuntime.ResetForTests();
    private static void Profile(bool classic) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(classic ? "late-2009" : "dev-unrestricted"));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    public void ResolutionRejectsAlliedMinionBeforeSacrificeAndSelfReward(bool classic, bool own, bool expected) {
        Profile(classic);
        var duel = Duel(); var caster = Occupy(duel, 4, true); var ally = Occupy(duel, 5, true);
        var minion = Occupy(duel, 6, false, own ? caster : ally);
        caster.ParticipantGameStats.m_currentHitpoints = 100;
        Assert.Equal(expected, Resolve(caster, minion, kEffectTarget.kMinion, sacrifice: true));
        Assert.Equal(expected ? 0 : 1000, minion.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal(expected ? 450 : 100, caster.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal(1000, ally.ParticipantGameStats.m_currentHitpoints);
    }

    [Theory]
    [InlineData(kEffectTarget.kCasterMinion, false)]
    [InlineData(kEffectTarget.kFriendlyMinion, true)]
    [InlineData(kEffectTarget.kEnemyMinion, false)]
    [InlineData(kEffectTarget.kTargetMinion, true)]
    public void ExplicitMinionTargetVariantsRetainTheirMeaning(kEffectTarget target, bool expected) {
        var duel = Duel(); var caster = Occupy(duel, 4, true); var ally = Occupy(duel, 5, true);
        var minion = Occupy(duel, 6, false, ally);
        Assert.Equal(expected, Resolve(caster, minion, target));
        Assert.Equal(expected ? 1 : 0, minion._hangingEffects.Count);
    }

    [Fact]
    public void DeadMinionAndReplacementOwnerCannotProduceSacrificeReward() {
        var duel = Duel(); var caster = Occupy(duel, 4, true); var minion = Occupy(duel, 6, false, caster);
        caster.ParticipantGameStats.m_currentHitpoints = 100;
        minion.ParticipantGameStats.m_currentHitpoints = 0;
        Assert.False(Resolve(caster, minion, kEffectTarget.kMinion, true));
        Assert.Equal(100, caster.ParticipantGameStats.m_currentHitpoints);
        minion.ParticipantGameStats.m_currentHitpoints = 1000;
        caster.ParticipantGameStats.m_currentHitpoints = 0;
        Assert.False(minion.IsOwnedMinionOf(caster));
        caster.RemoveParticipant(); caster = Occupy(duel, 4, true);
        Assert.False(Resolve(caster, minion, kEffectTarget.kMinion, true));
        Assert.Equal(1000, minion.ParticipantGameStats.m_currentHitpoints);
        minion.RemoveParticipant(); Assert.False(minion.IsSummonedMinion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SummonedCreatureInheritsOwnerTeamOnlyInClassic(bool classic) {
        Profile(classic); var duel = Duel(); var caster = Occupy(duel, 0, false);
        var minion = Occupy(duel, 1, false, caster);
        Assert.Equal(classic ? CombatTeam.Monster : CombatTeam.Player, minion.OccupiedTeam);
        if (classic) Assert.True(Resolve(caster, minion, kEffectTarget.kCasterMinion));
    }

    private static bool Resolve(CombatDuelSubCircle caster, CombatDuelSubCircle target, kEffectTarget targetKind, bool sacrifice = false) {
        const uint tid = uint.MaxValue - 119;
        var template = new SpellTemplate { m_name = "Minion regression", m_spellRank = new SpellRank(), m_effects = [
            new SpellEffect { m_effectType = sacrifice ? kSpellEffects.kInstantKill : kSpellEffects.kModifyIncomingDamage,
                m_effectTarget = targetKind, m_sDamageType = "All", m_effectParam = -70 },
        ] };
        if (sacrifice) template.m_effects.Add(new SpellEffect { m_effectType = kSpellEffects.kHeal, m_effectTarget = kEffectTarget.kSelf, m_effectParam = 350 });
        var cache = (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory).GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        cache.Add(tid, template);
        try {
            var spell = new Spell { m_templateID = tid, m_pipCost = new SpellRank() };
            var action = new QueuedCombatAction { SpellCaster = caster, SelectedTarget = target, Spell = spell, SpellTemplate = template };
            var result = new CombatAction { m_targetSubcircleList = [] }; float seconds = 0;
            return CombatActionResolver.ProcessedQueuedCombatAction(action, ref result, ref seconds);
        } finally { cache.Remove(tid); }
    }
    private static CombatDuelComponent Duel() {
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
        return duel;
    }
    private static CombatDuelSubCircle Occupy(CombatDuelComponent duel, int slot, bool player, CombatDuelSubCircle? owner = null) {
        var circle = duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject { m_templateID = player ? 1UL : 2UL });
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", ActorRefs.Nobody);
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = stats.m_currentHitpoints = 1000;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant { m_hangingEffects = [] });
        CombatRegressionTests.SetProperty(circle, "IsSummonedMinion", owner is not null);
        if (owner is not null) circle.CaptureMinionOwner(owner.SlotIndex);
        circle.AddedToDuel = true; return circle;
    }
}
