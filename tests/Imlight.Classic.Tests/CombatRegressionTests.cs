/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * COMBAT REGRESSION TESTS
 * ========================================================================
 *
 * PURPOSE:
 * Exercise production duel entry, threat reset, and selected-target spell effects.
 *
 * USAGE EXAMPLE:
 * dotnet test --filter FullyQualifiedName~CombatRegressionTests
 *
 * NOTE:
 * Actor-owned state is supplied directly; no world server or database is started.
 *
 * TODO:
 *
 * Created by: Nick with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Spells;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Packets;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class CombatRegressionTests {
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void RepeatedDuelEntryResetsThreatAndUsesOnlyOpposingSlots(int slot) {
        // Bypass configuration and entity startup, then invoke the production message handler.
        var ai = (CombatCreatureAIComponent) RuntimeHelpers.GetUninitializedObject(typeof(CombatCreatureAIComponent));
        var hate = new Dictionary<int, int>();
        Field(ai, "_hateTable").SetValue(ai, hate);
        var duel = MakeDuel();
        Enter(ai, duel, slot);
        AssertThreat(hate, slot);

        for (var entry = 0; entry < 3; entry++) {
            foreach (var key in hate.Keys.ToArray()) {
                Invoke(ai, "UpdateHateTable", key, 100 + key);
            }
            duel = MakeDuel();
            Enter(ai, duel, slot);
            AssertThreat(hate, slot);
            Assert.Same(duel, Field(ai, "_currentDuelComponent").GetValue(ai));
            Assert.Same(duel.SubCircles[slot], Field(ai, "_currentSubCircle").GetValue(ai));
            Assert.True((bool) Field(ai, "_isInDuel").GetValue(ai)!);
        }

        var oppositeSlot = (slot + 4) % 8;
        Enter(ai, MakeDuel(), oppositeSlot);
        AssertThreat(hate, oppositeSlot);
    }

    private static void AssertThreat(Dictionary<int, int> hate, int slot) {
        var expectedSlots = Enumerable.Range(slot < 4 ? 4 : 0, 4).ToArray();
        Assert.Equal(expectedSlots, hate.Keys.OrderBy(key => key).ToArray());
        foreach (var key in expectedSlots) {
            Assert.Equal(key == (slot + 4) % 8 ? 1 : 0, hate[key]);
        }
    }

    private static void Enter(CombatCreatureAIComponent ai, CombatDuelComponent duel, int slot) {
        Invoke(ai, "ReceiveCombatAdded", new COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL {
            Duel = duel, SubCircle = duel.SubCircles[slot],
        });
    }

    internal static CombatDuelComponent MakeDuel() {
        var duel = new CombatDuelComponent(null!);
        SetProperty(duel, "SubCircles", Enumerable.Range(0, 8)
            .Select(slot => new CombatDuelSubCircle(duel, 0, 0, default, slot)).ToArray());
        return duel;
    }

    internal static void SetProperty(object instance, string name, object value)
        => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static FieldInfo Field(object instance, string name)
        => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static object? Invoke(object instance, string name, params object[] arguments)
        => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments);
}

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class OrthrusCombatRegressionTests : IDisposable {
    public OrthrusCombatRegressionTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        var configPath = Path.GetTempFileName();
        try {
            // Suppress combat logging, including the logger's network sink.
            File.WriteAllText(configPath, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.Combine(Path.GetTempPath(), "imlight-combat-tests.log")}\n");
            ConfigurationManager.Initialize(configPath);
        } finally {
            File.Delete(configPath);
        }
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    [Theory]
    [InlineData(false, 300)]
    [InlineData(true, 325)]
    public void BothHitsLandOnSelectedSecondEnemyAndLeaveOtherParticipantsUntouched(bool shielded, int expectedHealth) {
        var template = new SpellTemplate {
            m_name = "Orthrus", m_accuracy = 80, m_spellRank = new SpellRank { m_spellRank = 7 },
            m_effects = [new SpellEffect {
                m_effectType = kSpellEffects.kDamage, m_effectParam = 700,
                m_sDamageType = "Myth", m_effectTarget = kEffectTarget.kEnemyTeamAllAtOnce,
            }],
        };
        var record = ClassicSpellLoader.Load(System.IO.Path.Combine(ClassicDataFixture.Root, "spells")).FindByName("Orthrus")!;
        var overrides = new ClassicSpellOverrides(new ClassicSpellBook("spells", [record]), ClassicDataFixture.LoadProfile("late-2009"));
        var shape = SpellTemplateEditor.ShapeOf(template, "Spells/Tiered Spells/Orthrus.xml");
        SpellTemplateEditor.ApplyPlan(template, overrides.PlanFor(shape)!);
        Assert.Equal(new[] { 50, 650 }, template.m_effects.Select(effect => effect.m_effectParam).ToArray());

        var duel = CombatRegressionTests.MakeDuel();
        CombatRegressionTests.SetProperty(duel, "Duel", new Duel {
            m_duelModifier = new DuelModifier { m_battlefieldEffects = [] },
        });
        var firstEnemy = Occupy(duel, 0, false);
        var selectedEnemy = Occupy(duel, 1, false);
        var caster = Occupy(duel, 4, true);
        var ally = Occupy(duel, 5, true);
        var untouchedWard = Shield();
        firstEnemy._hangingEffects.Add(untouchedWard);
        if (shielded) {
            selectedEnemy._hangingEffects.Add(Shield());
        }

        // A private cache entry supplies the template without loading a world archive.
        const uint templateId = uint.MaxValue - 101;
        var cache = (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        cache.Add(templateId, template);
        try {
            var spell = new Spell { m_templateID = templateId, m_pipCost = new SpellRank { m_spellRank = 7 } };
            var target = (CombatDuelSubCircle) CombatRegressionTests.Invoke(duel, "ClassicCastTarget", spell, caster, selectedEnemy)!;
            Assert.Same(selectedEnemy, target);
            var action = new QueuedCombatAction {
                SpellCaster = caster, SelectedTarget = target, Spell = spell, SpellTemplate = template,
            };
            var combatAction = new CombatAction { m_spellCaster = caster.SlotIndex, m_targetSubcircleList = [] };
            var cinematicTime = 0f;

            Assert.True(CombatActionResolver.ProcessedQueuedCombatAction(action, ref combatAction, ref cinematicTime));

            Assert.Equal(expectedHealth, selectedEnemy.ParticipantGameStats.m_currentHitpoints);
            Assert.Empty(selectedEnemy._hangingEffects);
            Assert.Equal(new[] { selectedEnemy.SlotIndex }, combatAction.m_targetSubcircleList);
            Assert.Equal(selectedEnemy.SlotIndex, Assert.Single(combatAction.m_CritHitList).m_target);
            Assert.Equal(1000, firstEnemy.ParticipantGameStats.m_currentHitpoints);
            Assert.Same(untouchedWard, Assert.Single(firstEnemy._hangingEffects));
            Assert.Equal(1000, caster.ParticipantGameStats.m_currentHitpoints);
            Assert.Equal(1000, ally.ParticipantGameStats.m_currentHitpoints);
        } finally {
            cache.Remove(templateId);
        }
    }

    private static SpellEffect Shield() => new() {
        m_effectType = kSpellEffects.kModifyIncomingDamage, m_effectParam = -50, m_sDamageType = "Myth",
    };

    private static CombatDuelSubCircle Occupy(CombatDuelComponent duel, int slot, bool player) {
        var circle = duel.SubCircles[slot];
        CombatRegressionTests.SetProperty(circle, "ParticipantObject", new CoreObject { m_templateID = player ? 1UL : 2UL });
        CombatRegressionTests.SetProperty(circle, "ParticipantActor", ActorRefs.Nobody);
        var stats = (ServerWizGameStats) RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_baseHitpoints = 1000;
        stats.m_currentHitpoints = 1000;
        CombatRegressionTests.SetProperty(circle, "ParticipantGameStats", stats);
        CombatRegressionTests.SetProperty(circle, "CombatParticipant", new CombatParticipant { m_hangingEffects = [] });
        circle.AddedToDuel = true;
        return circle;
    }
}
