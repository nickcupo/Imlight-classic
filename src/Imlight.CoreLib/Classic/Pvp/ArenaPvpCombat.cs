// CLASSIC: translate native, profile-overridden spells and public battle state into arena strategy inputs.
using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Ambient;
using Imlight.Classic.Pvp;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Classic.Pvp;

internal static class ArenaPvpCombat {
    private static readonly string[] Schools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance"];

    internal static COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE Choose(CombatDuelComponent duel, CombatDuelSubCircle me,
                                                                 out AllyMove move) {
        duel.CycleArenaAmbientHand(me);
        move = ArenaPvpBrain.Choose(ViewFor(duel, me), duel.ArenaAiRng(me.SlotIndex));
        return move.Kind == AllyMoveKind.Cast
            ? new() { Actor = me.ParticipantActor, MoveType = (byte) CombatMoveType.Attack,
                      SpellSelection = (byte) move.HandIndex, SpellTarget = (uint) move.TargetSlot }
            : new() { Actor = me.ParticipantActor, MoveType = (byte) CombatMoveType.Pass };
    }

    internal static ArenaPvpView ViewFor(CombatDuelComponent duel, CombatDuelSubCircle me) {
        var hand = me.GetCurrentHand()?.m_spellList ?? [];
        var cards = new List<ArenaPvpCard>();
        for (var index = 0; index < hand.Count; index++) {
            var spell = hand[index];
            if (spell is null) continue;
            var count = me.CombatParticipant?.m_pipCount;
            var pips = count is null ? 0 : count.m_genericPips + count.m_powerPips * (me.HasSchoolMastery(spell.m_magicSchoolID) ? 2 : 1);
            if (CardFor(index, spell, pips, me.HasPipsForSpell(spell)) is { } card) {
                var name = card.School;
                var stats = me.ParticipantGameStats;
                var gear = me.GetStatBySchool(stats?.m_accBonusPercent, name) + (stats?.m_accBonusPercentAll ?? 0)
                         - me.GetStatBySchool(stats?.m_accReducePercent, name) - (stats?.m_accReducePercentAll ?? 0);
                var charms = VisibleEffects(me._hangingEffects).Where(e => e.m_effectType == kSpellEffects.kModifyAccuracy
                    && (string.Equals(e.m_sDamageType, "All", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.m_sDamageType, name, StringComparison.OrdinalIgnoreCase)))
                    .Sum(e => e.m_effectParam);
                cards.Add(card with { Accuracy = Math.Clamp(card.Accuracy + (Math.Round(gear * 100) + charms) / 100.0, 0, 1) });
            }
        }
        var circles = duel.SubCircles.Where(c => c is { Occupied: true, AddedToDuel: true }).ToList();
        var focus = new Dictionary<int, int>();
        foreach (var ally in circles.Where(c => c != me && c.OccupiedTeam == me.OccupiedTeam)) {
            var queued = duel.CombatResolver?.GetQueuedAction(ally);
            if (queued?.Spell is not null && queued.SelectedTarget is { } target && target.OccupiedTeam != me.OccupiedTeam)
                focus[target.SlotIndex] = focus.GetValueOrDefault(target.SlotIndex) + 1;
        }
        var combatants = circles.Select(circle => {
            var stats = circle.ParticipantGameStats;
            var pips = circle.CombatParticipant?.m_pipCount;
            var school = Enum.GetName((MagicSchool) (stats?.m_schoolID ?? 0)) ?? "Balance";
            var damage = Schools.ToDictionary(s => s, s => (double) circle.GetStatBySchool(stats?.m_dmgBonusPercent, s)
                + (stats?.m_dmgBonusPercentAll ?? 0), StringComparer.OrdinalIgnoreCase);
            var resist = Schools.ToDictionary(s => s, s => (double) circle.GetStatBySchool(stats?.m_dmgReducePercent, s)
                + (stats?.m_dmgReducePercentAll ?? 0), StringComparer.OrdinalIgnoreCase);
            return new ArenaPvpCombatant(circle.SlotIndex, circle.OccupiedTeam == me.OccupiedTeam, school,
                Math.Max(0, stats?.m_currentHitpoints ?? 0), Math.Max(1, stats?.m_baseHitpoints ?? 1),
                pips is null ? 0 : pips.m_genericPips + pips.m_powerPips * 2,
                VisibleEffects(circle._hangingEffects).Select(e => ModifierFor(e, hanging: true)).OfType<ArenaModifier>().ToList(),
                focus.GetValueOrDefault(circle.SlotIndex), damage, resist,
                stats?.m_healBonusPercentAll ?? 0, stats?.m_healIncBonusPercentAll ?? 0,
                circle.IsWizard && !circle.IsAlive);
        }).ToList();
        return new ArenaPvpView(me.SlotIndex, (me.CombatParticipant?.m_stunned ?? 0) > 0, (int) me.AvailableSpells, cards, combatants);
    }

    // A hidden trap or weakness is unknown even when it hangs on the NPC or a teammate.
    internal static IEnumerable<SpellEffect> VisibleEffects(IEnumerable<SpellEffect> effects)
        => (effects ?? []).Where(e => e is not null && !e.m_cloaked);

    internal static ArenaPvpCard CardFor(int index, Spell spell, int availablePips, bool castable) {
        if (CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template) return null;
        return CardFor(index, spell, template, availablePips, castable);
    }

    // Separate overload lets integration tests use real generated types without loading private resources.
    internal static ArenaPvpCard CardFor(int index, Spell spell, SpellTemplate template, int availablePips, bool castable) {
        var effects = Expand(template.m_effects ?? [], availablePips).ToList();
        var branches = EffectBranches(template.m_effects ?? [], availablePips).Select(b =>
            new ArenaDamageBranch(b.Probability, b.Effects.Where(e => IsDamage(e) && e.m_effectTarget != kEffectTarget.kSelf)
                .Select(e => DamagePart(e, template, availablePips)).ToList())).ToList();
        var damage = branches.SelectMany(b => b.Parts.Select(p => p with { Amount = p.Amount * b.Probability })).ToList();
        var heals = effects.Where(e => e.Effect.m_effectType is kSpellEffects.kHeal or kSpellEffects.kHealOverTime).ToList();
        var modifiers = effects.Select(e => ModifierFor(e.Effect, spell.m_templateID)).OfType<ArenaModifier>().ToList();
        var role = damage.Count > 0 ? ArenaCardRole.Damage : heals.Count > 0 ? ArenaCardRole.Heal
            : modifiers.Any(m => m.Kind == ArenaModifierKind.OutgoingDamage && m.Amount > 0) ? ArenaCardRole.Blade
            : modifiers.Any(m => m.Kind == ArenaModifierKind.IncomingDamage && m.Amount > 0) ? ArenaCardRole.Trap
            : modifiers.Any(m => m.Kind == ArenaModifierKind.IncomingDamage && m.Amount < 0) ? ArenaCardRole.Shield
            : modifiers.Any(m => m.Kind == ArenaModifierKind.OutgoingDamage && m.Amount < 0) ? ArenaCardRole.Weakness
            : effects.Any(e => e.Effect.m_effectType == kSpellEffects.kRemoveWard) ? ArenaCardRole.RemoveWard
            : effects.Any(e => e.Effect.m_effectType == kSpellEffects.kRemoveCharm) ? ArenaCardRole.RemoveCharm
            : effects.Any(e => e.Effect.m_effectType == kSpellEffects.kReshuffle) ? ArenaCardRole.Reshuffle : ArenaCardRole.Other;
        var allEnemies = effects.Any(e => e.Effect.m_effectTarget is kEffectTarget.kEnemyTeam or kEffectTarget.kEnemyTeamAllAtOnce);
        var allAllies = effects.Any(e => e.Effect.m_effectTarget is kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce);
        return new ArenaPvpCard(index, spell.m_templateID, template.m_name ?? spell.m_templateID.ToString(),
            template.m_sMagicSchoolName ?? "", spell.m_pipCost?.m_xPipSpell == true ? availablePips : spell.m_pipCost?.m_spellRank ?? 0,
            availablePips, castable, Math.Clamp(spell.m_accuracy / 100.0, 0, 1), role, damage, modifiers,
            heals.Sum(e => Math.Max(0, e.Effect.m_effectParam) * e.Weight),
            heals.Any(e => e.Effect.m_effectType == kSpellEffects.kHealOverTime),
            effects.Count > 0 && effects.All(e => e.Effect.m_effectTarget == kEffectTarget.kSelf), allEnemies, allAllies,
            branches, !template.m_noDiscard && !spell.m_treasureCard,
            heals.Where(e => e.Effect.m_effectType == kSpellEffects.kHeal).Sum(e => Math.Max(0, e.Effect.m_effectParam) * e.Weight),
            effects.Where(e => IsDamage(e.Effect) && e.Effect.m_effectTarget == kEffectTarget.kSelf)
                .Sum(e => Math.Max(0, e.Effect.m_effectParam) * e.Weight));
    }

    private static bool IsDamage(SpellEffect effect) => effect.m_effectType is kSpellEffects.kDamage or kSpellEffects.kDamageNoCrit
        or kSpellEffects.kDamageOverTime or kSpellEffects.kStealHealth or kSpellEffects.kDamagePerTotalPipPower;
    private static ArenaDamagePart DamagePart(SpellEffect effect, SpellTemplate template, int pips)
        => new(effect.m_sDamageType ?? template.m_sMagicSchoolName,
            Math.Max(0, effect.m_effectParam) * (effect.m_effectType == kSpellEffects.kDamagePerTotalPipPower ? pips : 1),
            effect.m_effectType == kSpellEffects.kDamageOverTime,
            effect.m_effectType == kSpellEffects.kStealHealth ? effect.m_healModifier : 0);

    // Keep mutually exclusive outcomes separate from EffectList's consecutive hits when estimating shields.
    private static List<(double Probability, List<SpellEffect> Effects)> EffectBranches(IEnumerable<SpellEffect> effects, int pips) {
        var plans = new List<(double Probability, List<SpellEffect> Effects)> { (1, []) };
        foreach (var effect in effects.Where(e => e is not null)) {
            List<(double Probability, List<SpellEffect> Effects)> next;
            if (effect is RandomSpellEffect random && random.m_effectList is { Count: > 0 } choices)
                next = choices.SelectMany(c => EffectBranches([c], pips).Select(b => (b.Probability / choices.Count, b.Effects))).ToList();
            else if (effect is VariableSpellEffect variable && variable.m_effectList is { Count: > 0 } tiers)
                next = EffectBranches([VariableTier(tiers, pips)], pips);
            else if (effect is EffectListSpellEffect list && list.m_effectList is { } parts)
                next = EffectBranches(parts, pips);
            else if (effect is ConditionalSpellEffect) continue;
            else next = [(1, [effect])];
            plans = plans.SelectMany(a => next.Select(b => (a.Probability * b.Probability, a.Effects.Concat(b.Effects).ToList()))).ToList();
        }
        return plans;
    }

    private static SpellEffect VariableTier(IReadOnlyList<SpellEffect> tiers, int pips)
        => tiers.Where(e => e.m_pipNum > 0 && e.m_pipNum <= pips).OrderByDescending(e => e.m_pipNum).FirstOrDefault()
           ?? tiers[Math.Clamp(pips == 0 ? 0 : pips - 1, 0, tiers.Count - 1)];

    internal static IEnumerable<(SpellEffect Effect, double Weight)> Expand(IEnumerable<SpellEffect> effects, int pips, double weight = 1) {
        foreach (var effect in effects) {
            if (effect is null) continue;
            if (effect is RandomSpellEffect random && random.m_effectList is { Count: > 0 } choices) {
                foreach (var child in Expand(choices, pips, weight / choices.Count)) yield return child;
            }
            else if (effect is VariableSpellEffect variable && variable.m_effectList is { Count: > 0 } tiers) {
                var chosen = VariableTier(tiers, pips);
                foreach (var child in Expand([chosen], pips, weight)) yield return child;
            }
            else if (effect is EffectListSpellEffect list && list.m_effectList is { } parts) {
                foreach (var child in Expand(parts, pips, weight)) yield return child;
            }
            else if (effect is not ConditionalSpellEffect) yield return (effect, weight);
        }
    }

    private static ArenaModifier ModifierFor(SpellEffect effect, uint templateId = 0, bool hanging = false) {
        if (effect is null) return null;
        ArenaModifierKind? kind = effect.m_effectType switch {
            kSpellEffects.kModifyOutgoingDamage => ArenaModifierKind.OutgoingDamage,
            kSpellEffects.kModifyIncomingDamage => ArenaModifierKind.IncomingDamage,
            kSpellEffects.kAbsorbDamage => ArenaModifierKind.Absorb,
            kSpellEffects.kModifyOutgoingHeal => ArenaModifierKind.OutgoingHeal,
            kSpellEffects.kModifyIncomingHeal => ArenaModifierKind.IncomingHeal,
            _ => null,
        };
        return kind is { } value ? new ArenaModifier(templateId == 0 ? effect.m_spellTemplateID : templateId, value,
            effect.m_sDamageType ?? "All", value == ArenaModifierKind.Absorb && hanging ? effect.m_paramPerRound : effect.m_effectParam) : null;
    }
}
