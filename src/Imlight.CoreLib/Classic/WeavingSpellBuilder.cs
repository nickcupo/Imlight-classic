// CLASSIC OWNER EXTRA: shared pure Weaving transformation. Compiled by the server and the card overlay,
// so client previews use exactly the combat calculation. Experiments remain off in historical profiles.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;

namespace Imlight.CoreLib.Classic;

public static class WeavingSpellBuilder {
    public static bool TryBuildCast(SpellTemplate original, WeaveRider rider, out SpellTemplate built, out string? refusal) {
        built = original;
        refusal = null;
        if (original.m_effects is not { Count: > 0 } effects) { refusal = "the card has no effects"; return false; }
        if (effects.Any(effect => effect is VariableSpellEffect || effect is null)) { refusal = "X-pip cards cannot be woven"; return false; }
        if (!effects.Any(IsDamageBearing)) { refusal = "only damage cards can be woven"; return false; }
        if (effects.Any(effect => Targets(effect).Any(target => target is not kEffectTarget.kEnemySingle))) {
            refusal = "only single-target cards can be woven"; return false;
        }
        var school = SchoolName(rider.School);
        var baseDamage = effects.Sum(AverageDamage);
        if (baseDamage <= 0) { refusal = "the card deals no damage"; return false; }

        var converted = new List<SpellEffect>();
        if (rider.FirstHitPercent > 0) converted.Add(Hit(kSpellEffects.kDamage, Scale(baseDamage, rider.FirstHitPercent), school, kEffectTarget.kEnemySingle));
        foreach (var effect in effects) converted.Add(Convert(effect, rider, school));
        if (rider.BurnPercent > 0 && rider.BurnRounds > 0) {
            var burn = Hit(kSpellEffects.kDamageOverTime, Scale(baseDamage, rider.BurnPercent), school, kEffectTarget.kEnemySingle);
            burn.m_numRounds = rider.BurnRounds;
            converted.Add(burn);
        }
        if (rider.AbsorbPercent > 0) converted.Add(Hit(kSpellEffects.kAbsorbDamage, Scale(baseDamage, rider.AbsorbPercent), "All", kEffectTarget.kSelf));
        if (rider.HealPercent > 0 && rider.HealRounds > 0) {
            var bloom = Hit(kSpellEffects.kHealOverTime, Scale(baseDamage, rider.HealPercent), school, kEffectTarget.kSelf);
            bloom.m_numRounds = rider.HealRounds;
            converted.Add(bloom);
        }
        if (rider.TrapPercent > 0) converted.Add(Hit(kSpellEffects.kModifyIncomingDamage, rider.TrapPercent, "All", kEffectTarget.kEnemySingle));

        built = original with { m_effects = converted, m_sMagicSchoolName = school, m_sTypeName = "Damage" };
        return true;
    }

    private static readonly kSpellEffects[] s_damageKinds = [kSpellEffects.kDamage, kSpellEffects.kDamageOverTime, kSpellEffects.kStealHealth];

    public static string SchoolName(string school) => school.Length == 0 ? school : char.ToUpperInvariant(school[0]) + school[1..].ToLowerInvariant();

    private static SpellEffect Hit(kSpellEffects kind, int amount, string school, kEffectTarget target) => new() {
        m_effectType = kind, m_effectParam = amount, m_sDamageType = school, m_damageType = StringHash.Compute(school),
        m_effectTarget = target, m_healModifier = 1.0f,
    };

    private static int Scale(double amount, int percent) => (int) Math.Round(amount * percent / 100.0, MidpointRounding.AwayFromZero);

    private static SpellEffect Convert(SpellEffect effect, WeaveRider rider, string school) {
        var copy = SpellTemplateEditor.Copy(effect);
        if (Children(copy) is { } children) {
            for (var i = 0; i < children.Count; i++) children[i] = Convert(children[i], rider, school);
            return copy;
        }
        if (!s_damageKinds.Contains(copy.m_effectType)) return copy; // a trap or other rider of the original stays as it was
        copy.m_effectParam = Scale(copy.m_effectParam, rider.HitPercent);
        copy.m_sDamageType = school;
        copy.m_damageType = StringHash.Compute(school);
        if (rider.DrainPercent > 0 && copy.m_effectType == kSpellEffects.kDamage) {
            copy.m_effectType = kSpellEffects.kStealHealth;
            copy.m_healModifier = rider.DrainPercent / 100f;
        } else if (copy.m_effectType == kSpellEffects.kStealHealth && rider.DrainPercent == 0) {
            copy.m_effectType = kSpellEffects.kDamage; // only Death keeps the drain
            copy.m_healModifier = 1.0f;
        }
        return copy;
    }

    private static double AverageDamage(SpellEffect effect) => effect switch {
        RandomSpellEffect random when random.m_effectList is { Count: > 0 } list => list.Average(AverageDamage),
        EffectListSpellEffect list when list.m_effectList is { Count: > 0 } items => items.Sum(AverageDamage),
        _ => effect is not null && s_damageKinds.Contains(effect.m_effectType) ? effect.m_effectParam : 0,
    };

    private static bool IsDamageBearing(SpellEffect effect) => effect switch {
        RandomSpellEffect or EffectListSpellEffect => Children(effect)?.Any(IsDamageBearing) == true,
        _ => effect is not null && s_damageKinds.Contains(effect.m_effectType),
    };

    private static IEnumerable<kEffectTarget> Targets(SpellEffect effect) => effect switch {
        RandomSpellEffect or EffectListSpellEffect => Children(effect)?.SelectMany(Targets) ?? [],
        _ => effect is not null && s_damageKinds.Contains(effect.m_effectType) ? [effect.m_effectTarget] : [],
    };

    private static List<SpellEffect>? Children(SpellEffect effect) => effect switch {
        RandomSpellEffect random => random.m_effectList,
        EffectListSpellEffect list => list.m_effectList,
        _ => null,
    };
}
