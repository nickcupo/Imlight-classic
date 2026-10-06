using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class OctoberShopRuntimeTests {
    [Theory]
    [InlineData(468162u, MagicSchool.Balance)]
    [InlineData(468163u, MagicSchool.Death)]
    [InlineData(468164u, MagicSchool.Fire)]
    [InlineData(468165u, MagicSchool.Ice)]
    [InlineData(468166u, MagicSchool.Life)]
    [InlineData(468167u, MagicSchool.Myth)]
    [InlineData(468168u, MagicSchool.Storm)]
    public void OriginalMasteryBindingAllowsOffSchoolEquipAndMakesPowerPipsAffordableAndSpentForThatSchool(uint id, MagicSchool school) {
        EquipmentAttachConcurrencyTests.Configure();
        using var canonical = new ElixirTests.CanonicalFixture(); canonical.AddMasteryFixtures();
        var ownSchool = school == MagicSchool.Fire ? MagicSchool.Ice : MagicSchool.Fire;
        var info = new StatisticEffectInfo { m_effectName = "Canonical" + school + "Mastery", m_lookupIndex = 0 };
        // Exact original seven template identities and native NOT-school requirement.
        var amulet = new WizItemTemplate { m_templateID = id, m_adjectiveList = ["Amulet"], m_equipEffects = [info],
            m_equipRequirements = new RequirementList { m_operator = Operator.ROP_AND,
                m_requirements = [new ReqSchoolOfFocus { m_applyNOT = true, m_operator = Operator.ROP_AND, m_magicSchool = school.ToString() }] } };
        Assert.Equal(EquipRefusal.None, EquipRules.Check(amulet, 1, ownSchool.ToString(), false));
        Assert.Equal(EquipRefusal.RequirementsNotMet, EquipRules.Check(amulet, 1, school.ToString(), false));
        var stats = (ServerWizGameStats)RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats));
        stats.m_schoolID = (uint)ownSchool;
        var circle = (CombatDuelSubCircle)RuntimeHelpers.GetUninitializedObject(typeof(CombatDuelSubCircle));
        var participant = new CombatParticipant { m_pipCount = new PipCount { m_genericPips = 0, m_powerPips = 2 }, m_pGameStats = new WizGameStats() };
        Set(circle, "ParticipantGameStats", stats); Set(circle, "CombatParticipant", participant);
        const uint spellId = 4294966449;
        var cache = (IDictionary<ulong, CoreTemplate>)typeof(CoreObjectFactory).GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var hadPrevious = cache.TryGetValue(spellId, out var previous);
        var template = new SpellTemplate { m_name = "mastery fixture", m_sMagicSchoolName = "Fire", m_accuracy = 100,
            m_spellRank = new SpellRank { m_spellRank = 3 }, m_effects = [] };
        cache[spellId] = template;
        try {
            var spell = SpellFactory.GetSpell(template, spellId); spell.m_magicSchoolID = (uint)school;
            Assert.False(circle.HasPipsForSpell(spell));
            var applied = Assert.IsType<WizStatisticEffect>(CharacterEffectHelper.AddGameEffectToStats(stats, info));
            Assert.True(circle.HasSchoolMastery((uint)school));
            Assert.True(circle.HasPipsForSpell(spell));
            circle.DeductPips(school, 3);
            Assert.Equal(0, participant.m_pipCount.m_powerPips);
            Assert.Equal(0, participant.m_pipCount.m_genericPips);
            CharacterEffectHelper.RemoveStatisticEffectFromStats(stats, info.m_effectName, applied);
            participant.m_pipCount.m_powerPips = 2;
            Assert.False(circle.HasSchoolMastery((uint)school));
            Assert.False(circle.HasPipsForSpell(spell));
            Assert.Equal(0f, stats.m_powerPipBonusPercentAll); // mastery does not raise chance or other stats.
        }
        finally { if (hadPrevious) cache[spellId] = previous!; else cache.Remove(spellId); }
    }
    private static void Set(object instance, string property, object value)
        => instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
}
