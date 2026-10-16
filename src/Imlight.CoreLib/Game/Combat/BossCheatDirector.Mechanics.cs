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
 * CLASSIC BOSS CHEATS (RUNTIME): THE WATERWORKS PRIMITIVES
 * ========================================================================
 *
 * PURPOSE:
 * The runtime of the data-driven primitives of BossCheatMechanics: cycles
 * (and their hand-off), seat marks, round-start casts and reactions. See
 * the header of BossCheatDirector.cs for the order they play in.
 *
 * USAGE EXAMPLE:
 * queue.InsertRange(0, RoundStartCasts());       // AddExtraCasts, once a round
 * responses.AddRange(AfterActionMechanics(action));
 *
 * NOTE:
 * Every decision is logged as "BOSSCHEAT" (Events) like the 2009 cheats.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/16/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Combat;

internal sealed partial class BossCheatDirector {

    private static int MaxHealth(CombatDuelSubCircle circle) => Math.Max(1, circle.ParticipantGameStats?.m_baseHitpoints ?? 1);

    // Seat 1..4: the circle's place on its side (slot 4 is the wizards' first circle, the sun).
    internal static int SeatOf(CombatDuelSubCircle circle) => circle.SlotIndex % 4 + 1;

    private static bool HasTrap(CombatDuelSubCircle circle)
        => circle._hangingEffects?.Any(w => w.m_effectType == kSpellEffects.kModifyIncomingDamageType
            || w.m_effectType == kSpellEffects.kModifyIncomingDamage && w.m_effectParam > 0) ?? false;

    private static bool HasBlade(CombatDuelSubCircle circle)
        => circle?._hangingEffects?.Any(w => w.m_effectType == kSpellEffects.kModifyOutgoingDamage && w.m_effectParam > 0) ?? false;

    /// <summary>The seats <paramref name="boss"/> has marked so far.</summary>
    internal IReadOnlyCollection<int> MarkedSeats(CombatDuelSubCircle boss)
        => _marked.TryGetValue(boss.ParticipantObject, out var seats) ? seats : [];

    /// <summary>The current phase of <paramref name="cycle"/>, or null before it starts.</summary>
    internal CyclePhase? CurrentPhase(BossCycle cycle)
        => _cycles.TryGetValue(cycle.Id, out var state) && state.Holder is not null ? cycle.Phases[state.PhaseIndex] : null;

    private BossCycle? CycleOf(CombatDuelSubCircle boss) => ClassicProgression.BossCheats.CycleOf((uint) boss.ParticipantObject.m_templateID);

    private IEnumerable<CombatDuelSubCircle> LivingWizards(CombatDuelSubCircle boss)
        => _duel.SubCircles.Where(c => c is { Occupied: true, AddedToDuel: true } && c.IsAlive && c.IsWizard && c.OccupiedTeam != boss.OccupiedTeam)
            .OrderBy(c => c.SlotIndex);

    // ---------------------------------------------------------------- round start

    /// <summary>
    /// The casts that open the round, in play order: each cycle's, then each boss's marks and round-start casts.
    /// </summary>
    internal List<QueuedCombatAction> RoundStartCasts() {
        var casts = new List<QueuedCombatAction>();
        foreach (var cycle in ClassicProgression.BossCheats.Cycles) {
            casts.AddRange(CycleRound(cycle));
        }

        foreach (var (boss, cheat) in LivingBosses().OrderBy(x => x.Circle.SlotIndex).ToList()) {
            if (cheat.Marks is { } marks) {
                casts.AddRange(MarkCasts(boss, marks, roundStart: true));
            }

            foreach (var rule in cheat.RoundStart) {
                if (!rule.IsDue(_round) || !rule.Health.Contains(Health(boss), MaxHealth(boss))) {
                    continue;
                }

                if (rule.OnlyIfNotAttacked
                    && (_round <= 1 || _lastAttackedRound.GetValueOrDefault(boss.ParticipantObject, int.MinValue) == _round - 1)) {
                    continue;
                }

                var steps = Steps(boss, null, rule.Do, BossCheatKind.RoundStart, rule.Message, null);
                if (steps.Count > 0) {
                    Record(boss, $"round start: {Names(steps)}{(rule.OnlyIfNotAttacked ? " (not attacked last round)" : "")}");
                }

                casts.AddRange(steps);
            }
        }

        return casts;
    }

    // The cycle's round: it starts with its first living holder, moves to the next phase when the current one is over,
    // and starts again from the first phase when a new holder takes it over (the last one fell).
    private List<QueuedCombatAction> CycleRound(BossCycle cycle) {
        var holders = _duel.SubCircles.Where(c => c is { Occupied: true, AddedToDuel: true } && c.IsAlive && CheatOf(c) is not null).ToList();
        var holder = cycle.Holders.Select(h => (Holder: h, Circle: holders.FirstOrDefault(c => (uint) c.ParticipantObject.m_templateID == h.Template)))
            .FirstOrDefault(x => x.Circle is not null);
        if (holder.Circle is null) {
            return [];
        }

        if (!_cycles.TryGetValue(cycle.Id, out var state)) {
            _cycles[cycle.Id] = state = new CycleState();
        }

        if (state.LastRound == _round) {
            return [];
        }

        state.LastRound = _round;
        string? message;
        string why;
        if (state.Holder is null || state.Holder != holder.Holder.Template) {
            var takeover = state.Holder is not null;
            state.PhaseIndex = 0;
            state.RoundsLeft = cycle.Phases[0].FirstRounds;
            message = takeover ? holder.Holder.TakeoverMessage ?? cycle.Phases[0].Message : cycle.Phases[0].Message;
            why = takeover ? "takes the cycle over" : "starts the cycle";
        }
        else if (--state.RoundsLeft > 0) {
            return [];
        }
        else {
            state.PhaseIndex = (state.PhaseIndex + 1) % cycle.Phases.Length;
            state.RoundsLeft = cycle.Phases[state.PhaseIndex].Rounds;
            message = cycle.Phases[state.PhaseIndex].Message;
            why = "switches phase";
        }

        state.Holder = holder.Holder.Template;
        var phase = cycle.Phases[state.PhaseIndex];
        var boss = holder.Circle;
        var casts = new List<QueuedCombatAction>();
        if (Cast(boss, null, phase.Spell, new BossCheatCast(BossCheatKind.Phase, true, message)) is { } bubble) {
            casts.Add(bubble);
        }

        casts.AddRange(Steps(boss, null, phase.OnEnter, BossCheatKind.Phase, null, phase));
        Record(boss, $"{why}: phase {phase.Id} for {state.RoundsLeft} round(s): {Names(casts)}");

        return casts;
    }

    // ---------------------------------------------------------------- marks

    // At the start of a round: every marked seat gets the marked spell and every other wizard the other spell, each only
    // when the last one is gone. Mid-round (a wizard's spell took the boss below a seat's mark): only the new seats.
    private List<QueuedCombatAction> MarkCasts(CombatDuelSubCircle boss, BossMarks marks, bool roundStart) {
        if (!_marked.TryGetValue(boss.ParticipantObject, out var seats)) {
            _marked[boss.ParticipantObject] = seats = [];
        }

        var first = seats.Count == 0;
        var added = marks.SeatsAt(Health(boss), MaxHealth(boss)).Where(seats.Add).ToList();
        if (!roundStart && added.Count == 0) {
            return [];
        }

        var casts = new List<QueuedCombatAction>();
        foreach (var wizard in LivingWizards(boss)) {
            var marked = seats.Contains(SeatOf(wizard));
            if (!roundStart && !added.Contains(SeatOf(wizard))) {
                continue;
            }

            var spell = marked ? marks.MarkedSpell : marks.UnmarkedSpell;
            if (spell is null || HasEffectOf(wizard, spell)) {
                continue;
            }

            if (Cast(boss, wizard, spell, new BossCheatCast(BossCheatKind.Mark, true, null, marks.SureHit)) is { } cast) {
                casts.Add(cast);
            }
        }

        var message = first && added.Count > 0 ? marks.Message : added.Count > 0 ? marks.AddedMessage : null;
        if (casts.Count > 0 && message is not null) {
            casts[0].Cheat = casts[0].Cheat with { Message = message };
        }

        if (added.Count > 0 || casts.Count > 0) {
            Record(boss, $"marks seats [{string.Join(", ", seats)}]{(added.Count > 0 ? $" (new: {string.Join(", ", added)})" : "")}: {Names(casts)}");
        }

        return casts;
    }

    // Whether one of the spell's own hanging effects (same type, value and school) is on the circle.
    private static bool HasEffectOf(CombatDuelSubCircle circle, string spellName) {
        if (Resolve(spellName) is not { } resolved || circle._hangingEffects is not { } hanging) {
            return false;
        }

        var own = Flatten(resolved.Template.m_effects).Where(e => e.m_effectType != kSpellEffects.kInvalidSpellEffect).ToList();
        return hanging.Any(h => own.Any(e => e.m_effectType == h.m_effectType && e.m_effectParam == h.m_effectParam
            && string.Equals(e.m_sDamageType, h.m_sDamageType, StringComparison.OrdinalIgnoreCase)));
    }

    // ---------------------------------------------------------------- reactions

    // After a wizard's spell: reactions (the boss's own, then its cycle phase's while it holds the cycle), the marks a
    // health drop adds, and the attack record round_start's only_if_not_attacked reads.
    private List<QueuedCombatAction> AfterActionMechanics(QueuedCombatAction action) {
        var responses = new List<QueuedCombatAction>();
        var effects = Flatten(action.SpellTemplate?.m_effects).ToList();
        foreach (var (boss, cheat) in LivingBosses().ToList()) {
            var before = _healthBefore.GetValueOrDefault(boss, Health(boss));
            var now = Health(boss);
            if (now < before && Fires(CheatTrigger.Attack, effects, before, now)) {
                _lastAttackedRound[boss.ParticipantObject] = _round;
            }

            foreach (var reaction in cheat.Reactions) {
                responses.AddRange(React(boss, reaction, action, effects, before, now));
            }

            if (CycleOf(boss) is { } cycle && _cycles.TryGetValue(cycle.Id, out var state)
                && state.Holder == (uint) boss.ParticipantObject.m_templateID && CurrentPhase(cycle) is { } phase) {
                foreach (var reaction in phase.Reactions) {
                    responses.AddRange(React(boss, reaction, action, effects, before, now));
                }
            }

            // A seat marked by this spell's damage is marked from now on (its spell lands at once).
            if (cheat.Marks is { } marks && boss.IsAlive) {
                responses.AddRange(MarkCasts(boss, marks, roundStart: false));
            }
        }

        return responses;
    }

    private List<QueuedCombatAction> React(CombatDuelSubCircle boss, BossReaction reaction, QueuedCombatAction action,
        List<SpellEffect> effects, int before, int now) {
        var when = reaction.When;
        if (!when.Kinds.Any(k => Fires(k, effects, before, now))) {
            return [];
        }

        if (when.SingleTarget && !IsSingleTarget(effects)) {
            return [];
        }

        var target = action.SelectedTarget;
        var aimed = when.Target switch {
            CheatTargetFilter.Self => target == boss,
            CheatTargetFilter.Side => target is { Occupied: true } && target.OccupiedTeam == boss.OccupiedTeam,
            _ => true,
        };
        if (!aimed) {
            return [];
        }

        if (when.Caster != CheatCasterFilter.Any) {
            var marked = MarkedSeats(boss).Contains(SeatOf(action.SpellCaster));
            if (marked != (when.Caster == CheatCasterFilter.Marked)) {
                return [];
            }
        }

        if (when.Phase is { } phaseId && (CycleOf(boss) is not { } cycle || CurrentPhase(cycle)?.Id != phaseId)) {
            return [];
        }

        if (when.Missing == CheatMissing.TrapOnTarget && _targetHadTrap || when.Missing == CheatMissing.BladeOnCaster && _casterHadBlade) {
            return [];
        }

        var key = ((object) boss.ParticipantObject, reaction);
        if (reaction.PerRound is { } cap && _reactionsThisRound.GetValueOrDefault(key) >= cap) {
            return [];
        }

        var steps = Steps(boss, action.SpellCaster, reaction.Do, BossCheatKind.Reaction, reaction.Message, CycleOf(boss) is { } c ? CurrentPhase(c) : null);
        if (steps.Count > 0) {
            _reactionsThisRound[key] = _reactionsThisRound.GetValueOrDefault(key) + 1;
            Record(boss, $"reaction to slot {action.SpellCaster.SlotIndex}'s {action.SpellTemplate?.m_name} on slot {target?.SlotIndex}"
                + $"{(when.Phase is null ? "" : $" in phase {when.Phase}")}: {Names(steps)}");
        }

        return steps;
    }

    // A spell aimed at one combatant: no team, area or global effect, and at least one single-target one.
    private static bool IsSingleTarget(List<SpellEffect> effects)
        => effects.Any(e => e.m_effectTarget is kEffectTarget.kEnemySingle or kEffectTarget.kPreselectedEnemySingle
                or kEffectTarget.kFriendlySingle or kEffectTarget.kFriendlySingleNotMe or kEffectTarget.kSelf)
            && !effects.Any(e => e.m_effectTarget is kEffectTarget.kEnemyTeam or kEffectTarget.kEnemyTeamAllAtOnce
                or kEffectTarget.kMultiTargetEnemy or kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce
                or kEffectTarget.kAtLeastOneEnemy or kEffectTarget.kGlobal);

    // ---------------------------------------------------------------- steps

    // The steps' casts, in order; the message goes with the first one that is cast. A removal spell with nothing to
    // remove is skipped.
    private List<QueuedCombatAction> Steps(CombatDuelSubCircle boss, CombatDuelSubCircle? caster, IEnumerable<CheatStep> steps,
        BossCheatKind kind, string? message, CyclePhase? phase) {
        var casts = new List<QueuedCombatAction>();
        foreach (var step in steps) {
            var name = step.RecastPhase ? phase?.Spell : step.Spells.Length == 1 ? step.Spells[0] : step.Spells[Rng.Next(step.Spells.Length)];
            if (name is null) {
                continue;
            }

            var target = step.Target switch {
                CheatStepTarget.Self => boss,
                _ => caster,
            };
            if (Cast(boss, target, name, new BossCheatCast(kind, true, null, step.SureHit)) is not { } cast) {
                continue;
            }

            if (step.RemoveCount is not null || step.RemoveOnly != CheatRemoveOnly.Spell) {
                cast.SpellTemplate = Adjusted(cast.SpellTemplate, step);
            }

            if (IsRemovalOnly(cast.SpellTemplate) && !HasSomethingToRemove(boss, cast)) {
                Record(boss, $"skips {name}: nothing to remove");
                continue;
            }

            casts.Add(cast);
        }

        if (casts.Count > 0 && message is not null) {
            casts[0].Cheat = casts[0].Cheat with { Message = message };
        }

        return casts;
    }

    private static bool IsRemoval(SpellEffect effect) => effect.m_effectType is kSpellEffects.kRemoveWard or kSpellEffects.kRemoveCharm;

    private static bool IsRemovalOnly(SpellTemplate template) {
        var effects = Flatten(template.m_effects).Where(e => e.m_effectType != kSpellEffects.kInvalidSpellEffect).ToList();
        return effects.Count > 0 && effects.All(IsRemoval);
    }

    // A copy of the template whose removal effects take the step's count and disposition (the client still plays the
    // spell's own animation; the duel state it receives after the cast shows what was removed).
    private static SpellTemplate Adjusted(SpellTemplate template, CheatStep step) {
        var disposition = step.RemoveOnly switch {
            CheatRemoveOnly.Harmful => kHangingDisposition.kHarmful,
            CheatRemoveOnly.Beneficial => kHangingDisposition.kBeneficial,
            _ => (kHangingDisposition?) null,
        };

        return template with {
            m_effects = [.. template.m_effects.Select(e => e is not null && IsRemoval(e)
                ? e with { m_effectParam = step.RemoveCount ?? e.m_effectParam, m_disposition = disposition ?? e.m_disposition }
                : e)],
        };
    }

    // Whether any of the cast's removal effects finds a ward or charm on a combatant it reaches.
    private bool HasSomethingToRemove(CombatDuelSubCircle boss, QueuedCombatAction cast) {
        foreach (var effect in Flatten(cast.SpellTemplate.m_effects).Where(IsRemoval)) {
            IEnumerable<CombatDuelSubCircle> reached = effect.m_effectTarget switch {
                kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce
                    => _duel.SubCircles.Where(c => c is { Occupied: true } && c.IsAlive && c.OccupiedTeam == boss.OccupiedTeam),
                kEffectTarget.kEnemyTeam or kEffectTarget.kEnemyTeamAllAtOnce or kEffectTarget.kMultiTargetEnemy
                    => _duel.SubCircles.Where(c => c is { Occupied: true } && c.IsAlive && c.OccupiedTeam != boss.OccupiedTeam),
                kEffectTarget.kSelf => [boss],
                _ => cast.SelectedTarget is { } t ? [t] : [],
            };
            foreach (var circle in reached) {
                var found = effect.m_effectType == kSpellEffects.kRemoveWard
                    ? CombatWards.FindRemovableWards(circle, effect.m_disposition).Any(w => w.m_effectType != kSpellEffects.kStunBlock)
                    : CombatCharms.FindHangingCharms(circle, effect.m_disposition).Count > 0;
                if (found) {
                    return true;
                }
            }
        }

        return false;
    }

    private static string Names(IEnumerable<QueuedCombatAction> casts) {
        var names = casts.Select(c => $"{c.SpellTemplate?.m_name} on slot {c.SelectedTarget?.SlotIndex}").ToList();
        return names.Count == 0 ? "nothing to cast" : string.Join(", ", names);
    }

}
