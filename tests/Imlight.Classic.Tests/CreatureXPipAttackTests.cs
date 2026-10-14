using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;
using Xunit;

namespace Imlight.Classic.Tests;

// CLASSIC: a creature's "NA ...-Boss-01" natural attack (and Tempest) is an X-pip spell: a VariableSpellEffect whose own
// type is kInvalidSpellEffect with the damage in per-pip-tier children. The creature AI's damage filter ignored it, so a
// boss with only that attack (or Katsumori, whose other card needs 4 pips) passed round after round (MooShu playthrough,
// duel 5264260778913716953, "No castable damage spells").
public sealed class CreatureXPipAttackTests {
    private static uint s_next = uint.MaxValue - 4000;

    private static Spell Register(params SpellEffect[] effects) {
        var tid = s_next--;
        var cache = (IDictionary<ulong, CoreTemplate>) typeof(CoreObjectFactory)
            .GetField("s_templateCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        cache[tid] = new SpellTemplate { m_name = "x-pip filter " + tid, m_spellRank = new SpellRank(), m_effects = effects.ToList() };
        return new Spell { m_templateID = tid, m_pipCost = new SpellRank(), m_magicSchoolID = 0 };
    }

    private static SpellEffect Damage(kEffectTarget target = kEffectTarget.kEnemySingle, int pip = 1)
        => new() { m_effectType = kSpellEffects.kDamage, m_effectTarget = target, m_effectParam = 100 * pip, m_pipNum = pip };

    private static VariableSpellEffect XPip(kEffectTarget wrapperTarget, kEffectTarget childTarget)
        => new() {
            m_effectType = kSpellEffects.kInvalidSpellEffect, m_effectTarget = wrapperTarget,
            m_effectList = [Damage(childTarget, 1), Damage(childTarget, 2), Damage(childTarget, 3)],
        };

    [Fact]
    public void AnXPipNaturalAttackCountsAsADamageSpell() {
        var natural = Register(XPip(kEffectTarget.kEnemySingle, kEffectTarget.kEnemySingle));
        var result = SpellEffectFilter.FilterSpellsByOutgoingDamage([natural]);
        Assert.Single(result);
        Assert.Same(natural, result[0]);
    }

    [Fact]
    public void AnXPipAttackCountsWhenOnlyTheWrapperNamesTheEnemy() {
        var natural = Register(XPip(kEffectTarget.kEnemySingle, kEffectTarget.kFriendlySingle));
        Assert.Single(SpellEffectFilter.FilterSpellsByOutgoingDamage([natural]));
    }

    [Fact]
    public void AnXPipHealOrFriendlyEffectIsNotADamageSpell() {
        var heal = Register(new VariableSpellEffect {
            m_effectType = kSpellEffects.kInvalidSpellEffect, m_effectTarget = kEffectTarget.kFriendlySingle,
            m_effectList = [new SpellEffect { m_effectType = kSpellEffects.kHeal, m_effectTarget = kEffectTarget.kFriendlySingle, m_pipNum = 1 }],
        });
        var selfDamage = Register(XPip(kEffectTarget.kFriendlySingle, kEffectTarget.kFriendlySingle));
        Assert.Empty(SpellEffectFilter.FilterSpellsByOutgoingDamage([heal, selfDamage]));
    }

    [Fact]
    public void PlainDamageStillCountsAndASpellIsListedOnceHoweverManyDamageEffectsItHas() {
        var plain = Register(Damage());
        var twice = Register(Damage(), Damage(kEffectTarget.kEnemyTeam));
        var result = SpellEffectFilter.FilterSpellsByOutgoingDamage([plain, twice]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void AnEnemyBuffOrFriendlyDamageIsNotDamage() {
        var friendly = Register(Damage(kEffectTarget.kFriendlySingle));
        var curse = Register(new SpellEffect { m_effectType = kSpellEffects.kModifyIncomingDamage, m_effectTarget = kEffectTarget.kEnemySingle, m_effectParam = 25 });
        Assert.Empty(SpellEffectFilter.FilterSpellsByOutgoingDamage([friendly, curse]));
    }
}
