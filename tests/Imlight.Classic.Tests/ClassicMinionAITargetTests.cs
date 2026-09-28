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
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ClassicMinionAITargetTests : IDisposable {
    public ClassicMinionAITargetTests() {
        var config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-ai-target-tests.log")}\n");
            ConfigurationManager.Initialize(config);
        } finally { File.Delete(config); }
    }
    public void Dispose() => ClassicRuntime.ResetForTests();

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 7)]
    [InlineData(true, false, 0)]
    [InlineData(true, false, 7)]
    [InlineData(false, true, 0)]
    [InlineData(false, true, 7)]
    [InlineData(false, false, 0)]
    [InlineData(false, false, 7)]
    public void ProductionAIAllyChoiceUsesZeroBasedSlotInClassic(bool classic, bool heal, int allySlot) {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules(classic ? "late-2009" : "dev-unrestricted"));
        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel { m_duelModifier = new DuelModifier { m_battlefieldEffects = [] } });
        // Stock treats all summoned minions as player-team. Slot zero remains legal independently of physical side.
        var ally = Occupy(duel, allySlot, 100);
        var caster = Occupy(duel, allySlot == 0 ? 1 : 6, 1000);
        CombatRegressionTests.SetProperty(caster, "IsSummonedMinion", true);
        caster.CaptureMinionOwner(allySlot);
        var ai = (CombatCreatureAIComponent) RuntimeHelpers.GetUninitializedObject(typeof(CombatCreatureAIComponent));
        var entity = (ZoneEntity) RuntimeHelpers.GetUninitializedObject(typeof(ZoneEntity));
        CombatRegressionTests.SetProperty(entity, "SelfRef", ActorRefs.Nobody);
        typeof(ZoneEntityComponent).GetProperty("Entity", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ai, entity);
        Set(ai, "_currentDuelComponent", duel); Set(ai, "_currentSubCircle", caster);
        Set(ai, "_random", new ChooseAllyRandom(allySlot == 0 ? 0 : 1));
        var stats = (StatsComponent) RuntimeHelpers.GetUninitializedObject(typeof(StatsComponent)); stats.Stats = caster.ParticipantGameStats;
        Set(ai, "_stats", stats);
        const uint tid = uint.MaxValue - 127;
        var template = new SpellTemplate { m_name = "friendly AI regression", m_spellRank = new SpellRank(), m_effects = [new SpellEffect {
            m_effectType = heal ? kSpellEffects.kHeal : kSpellEffects.kModifyOutgoingHeal,
            m_effectTarget = kEffectTarget.kFriendlySingle, m_effectParam = heal ? 100 : 30,
        }] };
        var spell = new Spell { m_templateID = tid, m_pipCost = new SpellRank(), m_magicSchoolID = 0 };
        Set(ai, "_roundHand", new Hand { m_spellList = [spell] });
        var cache = (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory).GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        cache.Add(tid, template);
        try {
            var move = (COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE) CombatRegressionTests.Invoke(ai, heal ? "DetermineHealingBehavior" : "DetermineDefensiveBehavior")!;
            Assert.Equal((uint)(classic ? allySlot : allySlot + 1), move.SpellTarget);
            Assert.Equal((byte)CombatMoveType.Attack, move.MoveType);
            Assert.Equal(0, move.SpellSelection);
            if (classic) {
                // Exercise the production resolution against the same zero-based index used by HandleAttackMove.
                var action = new QueuedCombatAction { SpellCaster = caster, SelectedTarget = duel.SubCircles[move.SpellTarget], Spell = spell, SpellTemplate = template };
                var result = new CombatAction { m_targetSubcircleList = [] }; float seconds = 0;
                Assert.True(CombatActionResolver.ProcessedQueuedCombatAction(action, ref result, ref seconds));
                Assert.Equal(new[] { allySlot }, result.m_targetSubcircleList);
                Assert.Equal(heal ? 200 : 100, ally.ParticipantGameStats.m_currentHitpoints);
                Assert.Equal(heal ? 0 : 1, ally._hangingEffects.Count);
                Assert.Equal(1000, caster.ParticipantGameStats.m_currentHitpoints);
                Assert.Empty(caster._hangingEffects);
            }
        } finally { cache.Remove(tid); }
    }

    private static void Set(object obj, string name, object value)
        => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, value);
    private static CombatDuelSubCircle Occupy(CombatDuelComponent duel, int slot, int hp) {
        var circle = duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject { m_templateID = 1 });
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", ActorRefs.Nobody);
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = 1000; stats.m_currentHitpoints = hp;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant { m_hangingEffects = [], m_pipCount = new PipCount() });
        circle.AddedToDuel = true; return circle;
    }
    private sealed class ChooseAllyRandom(int allyIndex) : Random {
        public override double NextDouble() => .25;
        public override int Next(int maxValue) => maxValue == 1 ? 0 : allyIndex;
    }
}
