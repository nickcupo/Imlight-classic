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
 * CLASSIC BOSS CHEATS (RUNTIME)
 * ========================================================================
 *
 * PURPOSE:
 * Plays the scripted cheats of the bosses the profile's boss-cheat table
 * lists (Briskbreeze Tower, October 2009) inside one duel:
 *   - extra casts: the boss casts CastsPerRound spells in its turn; the
 *     extra ones are its free spells (no pips) for its current health;
 *   - interrupts: right after a wizard's spell that sets one off (Orrik:
 *     a hit, between 4000 and 2000 health), the boss casts out of turn.
 *     The wizard's spell is not cancelled. The client gets the cast as a
 *     CombatAction with m_interrupt set and, when the table names one,
 *     m_stringKeyMessage (WC-ActorDialog_00000771, "Interrupt!", a string
 *     the r806919 client ships next to Orrick's own lines);
 *   - trap breaking: right after a wizard's trap lands on the boss's side,
 *     the boss casts the table's spell (Cleanse Ward: the newest trap) on
 *     the trapped creature, out of turn and for no pips; optionally also
 *     once in its own turn while a trap is on it;
 *   - free casts: every cheat cast costs no pips and uses no card;
 *   - summons: creatures join the boss's side at a round or when a cast
 *     takes the boss below a health mark, and again a round after the last
 *     of them fell when the table says so.
 * A creature the table does not list never cheats.
 *
 * USAGE EXAMPLE:
 * duel.BossCheats.NewRound(round);                     // CombatDuelComponent, each new round
 * cheats.AddExtraCasts(queue);                         // CombatResolver, after sorting the round
 * cheats.BeforeAction(action); ... cheats.AfterAction(action)  // around each resolved wizard spell
 *
 * NOTE:
 * Every decision is logged as "BOSSCHEAT" so a server log shows each extra
 * cast, interrupt, trap break and summon by round.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.Classic.Spells;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// What kind of cheat cast a queued action is.
/// </summary>
internal enum BossCheatKind {
    /// <summary>A second (or third) cast in the boss's own turn.</summary>
    ExtraCast,
    /// <summary>An out-of-turn cast in answer to a wizard's spell.</summary>
    Interrupt,
    /// <summary>An out-of-turn cast that breaks a trap a wizard just placed.</summary>
    TrapBreak,
    /// <summary>A trap break in the boss's own turn.</summary>
    RoundTrapBreak,
}

/// <summary>
/// The cheat a queued action carries: it costs no pips, uses no card and may be shown out of turn.
/// </summary>
internal sealed record BossCheatCast(BossCheatKind Kind, bool Interrupt, string? Message);

/// <summary>
/// One duel's boss cheats.
/// </summary>
internal sealed class BossCheatDirector(CombatDuelComponent duel) {

    // A stream of its own, so a fight without a cheating boss rolls exactly as before.
    private const int CHEAT_STREAM = 0x0B055;

    /// <summary>Test hook: resolves a spell name without the client's spell files.</summary>
    internal static Func<string, (Spell Spell, SpellTemplate Template)?>? ResolveSpellForTests;

    /// <summary>Test hook: replaces the minion spawn.</summary>
    internal Action<CombatDuelSubCircle, BossSummon>? SummonForTests;

    private readonly CombatDuelComponent _duel = duel;
    private Random? _rng;
    private int _round;
    private readonly Dictionary<object, int> _interruptsThisRound = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(object Boss, int Index), SummonState> _summons = [];
    private readonly Dictionary<CombatDuelSubCircle, int> _healthBefore = [];
    private readonly Dictionary<CombatDuelSubCircle, int> _trapsBefore = [];

    private sealed class SummonState {
        internal bool Done;
        internal bool SeenAlive;
    }

    /// <summary>Every cheat this duel played, in order ("round N | ..."): the log the tests and the playbot read.</summary>
    internal List<string> Events { get; } = [];

    private Random Rng => _rng ??= _duel.StreamFor(CHEAT_STREAM);

    /// <summary>
    /// The cheats of <paramref name="circle"/>, or null when it is not a listed boss of the active profile.
    /// </summary>
    internal static BossCheat? CheatOf(CombatDuelSubCircle? circle) {
        if (circle is not { Occupied: true } || circle.IsSummonedMinion || circle.IsWizard
            || circle.OccupiedTeam != CombatTeam.Monster || !ClassicRuntime.IsInitialized || !ClassicRuntime.IsActive) {
            return null;
        }

        return ClassicProgression.BossCheats.TryGet((uint) circle.ParticipantObject.m_templateID, out var boss) ? boss : null;
    }

    private IEnumerable<(CombatDuelSubCircle Circle, BossCheat Cheat)> LivingBosses()
        => _duel.SubCircles.Where(c => c is not null && c.IsAlive && c.AddedToDuel)
            .Select(c => (Circle: c, Cheat: CheatOf(c)))
            .Where(x => x.Cheat is not null)
            .Select(x => (x.Circle, x.Cheat!));

    private static int Health(CombatDuelSubCircle circle) => circle.ParticipantGameStats?.m_currentHitpoints ?? 0;

    private void Record(CombatDuelSubCircle boss, string text) {
        var line = $"round {_round} | {CheatOf(boss)?.Name ?? "?"} (slot {boss.SlotIndex}, {Health(boss)} hp) | {text}";
        Events.Add(line);
        Logger.Information("Duel {0} | BOSSCHEAT {1}", Logger.Args(_duel.Duel?.m_duelID.Full ?? 0UL, line));
    }

    /// <summary>
    /// A new round: interrupt counts start again, and summons due this round come.
    /// </summary>
    internal void NewRound(int round) {
        _round = round;
        _interruptsThisRound.Clear();
        foreach (var (boss, cheat) in LivingBosses().ToList()) {
            for (var i = 0; i < cheat.Summons.Length; i++) {
                var summon = cheat.Summons[i];
                var state = State(boss, i);
                if (summon.Round is { } due && !state.Done && round >= due) {
                    Summon(boss, summon, state, $"round {due}");
                    continue;
                }

                if (!summon.Resummon || !state.Done) {
                    continue;
                }

                var alive = OwnedMinions(boss, summon.Creature);
                if (alive > 0) {
                    state.SeenAlive = true;
                }
                else if (state.SeenAlive) {
                    Summon(boss, summon, state, "the last one fell");
                }
            }
        }
    }

    private SummonState State(CombatDuelSubCircle boss, int index) {
        var key = ((object) boss.ParticipantObject, index);
        if (!_summons.TryGetValue(key, out var state)) {
            _summons[key] = state = new SummonState();
        }

        return state;
    }

    private int OwnedMinions(CombatDuelSubCircle boss, uint creature)
        => _duel.SubCircles.Count(c => c is { Occupied: true, IsSummonedMinion: true } && c.IsAlive
            && (uint) c.ParticipantObject.m_templateID == creature && c.IsOwnedMinionOf(boss));

    private void Summon(CombatDuelSubCircle boss, BossSummon summon, SummonState state, string why) {
        state.Done = true;
        state.SeenAlive = false;
        Record(boss, $"summons {summon.Count} x {summon.Name} ({summon.Creature}) because {why}");
        if (SummonForTests is { } hook) {
            hook(boss, summon);

            return;
        }

        _duel.SummonBossMinions(boss, summon);
    }

    /// <summary>
    /// Puts each boss's extra casts (and its own-turn trap break) right after its card in the sorted round.
    /// </summary>
    internal void AddExtraCasts(List<QueuedCombatAction> queue) {
        for (var i = 0; i < queue.Count; i++) {
            var action = queue[i];
            if (action.Cheat is not null || CheatOf(action.SpellCaster) is not { } cheat
                || !action.SpellCaster.IsAlive || action.SpellCaster.CombatParticipant?.m_stunned > 0) {
                continue;
            }

            var boss = action.SpellCaster;
            var extras = new List<QueuedCombatAction>();
            if (cheat.DestroyTraps is { EveryRound: { } window } traps && window.Contains(Health(boss))
                && TrappedAlly(boss) is { } trapped
                && Cast(boss, trapped, traps.Spell, new BossCheatCast(BossCheatKind.RoundTrapBreak, false, traps.Message)) is { } roundBreak) {
                extras.Add(roundBreak);
                Record(boss, $"own turn: breaks a trap on slot {trapped.SlotIndex} with {traps.Spell}");
            }

            for (var n = 1; n < cheat.CastsPerRound; n++) {
                var spells = cheat.FreeSpellsAt(Health(boss)).ToList();
                if (spells.Count == 0) {
                    break;
                }

                var name = spells[Rng.Next(spells.Count)];
                if (Cast(boss, null, name, new BossCheatCast(BossCheatKind.ExtraCast, false, null)) is { } extra) {
                    extras.Add(extra);
                    Record(boss, $"extra cast {n + 1} of {cheat.CastsPerRound}: {name} for no pips");
                }
            }

            queue.InsertRange(i + 1, extras);
            i += extras.Count;
        }
    }

    private CombatDuelSubCircle? TrappedAlly(CombatDuelSubCircle boss)
        => _duel.SubCircles.FirstOrDefault(c => c is { Occupied: true } && c.IsAlive && c.OccupiedTeam == boss.OccupiedTeam
            && HarmfulWards(c) > 0);

    private static int HarmfulWards(CombatDuelSubCircle circle)
        => circle._hangingEffects?.Count(w => w.m_effectType == kSpellEffects.kModifyIncomingDamage && w.m_effectParam > 0) ?? 0;

    /// <summary>
    /// Before a wizard's spell resolves: what the bosses' side looks like, to see afterwards what the spell did.
    /// </summary>
    internal void BeforeAction(QueuedCombatAction action) {
        _healthBefore.Clear();
        _trapsBefore.Clear();
        if (!IsWizardSpell(action)) {
            return;
        }

        foreach (var circle in _duel.SubCircles.Where(c => c is { Occupied: true } && c.OccupiedTeam == CombatTeam.Monster)) {
            _healthBefore[circle] = Health(circle);
            _trapsBefore[circle] = HarmfulWards(circle);
        }
    }

    private static bool IsWizardSpell(QueuedCombatAction action)
        => action.Cheat is null && action.Spell is not null && action.SpellCaster is { } caster
            && caster.ActingTeam == CombatTeam.Player && caster.OccupiedTeam == CombatTeam.Player;

    /// <summary>
    /// After a wizard's spell resolved: health-mark summons, and the out-of-turn casts it sets off, in the order they
    /// follow it.
    /// </summary>
    internal List<QueuedCombatAction> AfterAction(QueuedCombatAction action) {
        var responses = new List<QueuedCombatAction>();
        if (!IsWizardSpell(action) || _healthBefore.Count == 0) {
            return responses;
        }

        foreach (var (boss, cheat) in LivingBosses().ToList()) {
            var before = _healthBefore.GetValueOrDefault(boss, Health(boss));
            var now = Health(boss);

            // Summons at a health mark come with the hit that crosses it.
            for (var i = 0; i < cheat.Summons.Length; i++) {
                if (cheat.Summons[i].HealthBelow is { } mark && now < mark && State(boss, i) is { Done: false } state) {
                    Summon(boss, cheat.Summons[i], state, $"a hit took it below {mark}");
                }
            }

            // Traps that just landed on its side are broken at once.
            if (cheat.DestroyTraps is { OnPlaced: true } traps) {
                foreach (var trapped in _duel.SubCircles.Where(c => c is { Occupied: true } && c.IsAlive
                             && c.OccupiedTeam == boss.OccupiedTeam && HarmfulWards(c) > _trapsBefore.GetValueOrDefault(c, 0))) {
                    if (Cast(boss, trapped, traps.Spell, new BossCheatCast(BossCheatKind.TrapBreak, true, traps.Message)) is { } breaker) {
                        responses.Add(breaker);
                        _trapsBefore[trapped] = HarmfulWards(trapped);
                        Record(boss, $"interrupt: breaks the trap slot {action.SpellCaster.SlotIndex} put on slot {trapped.SlotIndex} with {traps.Spell}");
                    }
                }
            }

            if (cheat.Interrupt is { } interrupt && interrupt.Health.Contains(now)
                && _interruptsThisRound.GetValueOrDefault(boss.ParticipantObject) < interrupt.PerRound
                && Triggers(interrupt, action, boss, before, now) is { } trigger) {
                var name = interrupt.Spells[Rng.Next(interrupt.Spells.Length)];
                if (Cast(boss, action.SpellCaster, name, new BossCheatCast(BossCheatKind.Interrupt, true, interrupt.Message)) is { } answer) {
                    responses.Add(answer);
                    _interruptsThisRound[boss.ParticipantObject] = _interruptsThisRound.GetValueOrDefault(boss.ParticipantObject) + 1;
                    Record(boss, $"interrupt ({trigger} by slot {action.SpellCaster.SlotIndex}): {name} for no pips");
                }
            }
        }

        return responses;
    }

    private static CheatTrigger? Triggers(BossInterrupt interrupt, QueuedCombatAction action, CombatDuelSubCircle boss, int before, int now) {
        var effects = Flatten(action.SpellTemplate?.m_effects).ToList();
        foreach (var trigger in interrupt.On) {
            var fires = trigger switch {
                CheatTrigger.Any => true,
                // A hit: the spell's direct damage took health off this boss. Drains (Life Steal: Wraith, Vampire, ...) do
                // not count (Wizard101 Central guide, in-text update dated 2010-03-18).
                CheatTrigger.Hit => now < before && effects.Any(e => e.m_effectType is kSpellEffects.kDamage or kSpellEffects.kDamageNoCrit
                    or kSpellEffects.kDamagePerTotalPipPower or kSpellEffects.kMaxHealthDamage),
                CheatTrigger.Heal => effects.Any(e => e.m_effectType is kSpellEffects.kHeal or kSpellEffects.kHealPercent or kSpellEffects.kHealOverTime),
                CheatTrigger.Shield => effects.Any(e => e.m_effectType == kSpellEffects.kAbsorbDamage
                    || e.m_effectType == kSpellEffects.kModifyIncomingDamage && e.m_effectParam < 0),
                CheatTrigger.Blade => effects.Any(e => e.m_effectType == kSpellEffects.kModifyOutgoingDamage && e.m_effectParam > 0),
                _ => false,
            };
            if (fires) {
                return trigger;
            }
        }

        return null;
    }

    private static IEnumerable<SpellEffect> Flatten(IEnumerable<SpellEffect>? effects) {
        foreach (var effect in effects ?? []) {
            if (effect is null) {
                continue;
            }

            yield return effect;
            var children = effect switch {
                RandomSpellEffect r => r.m_effectList,
                VariableSpellEffect v => v.m_effectList,
                EffectListSpellEffect l => l.m_effectList,
                _ => null,
            };
            foreach (var child in Flatten(children)) {
                yield return child;
            }
        }
    }

    // A cheat cast of the boss: a friendly card goes on `friendly` (or the boss), an enemy card on `enemy` when that is a
    // living wizard, else on a random living one; an area card still needs a living target to be cast.
    private QueuedCombatAction? Cast(CombatDuelSubCircle boss, CombatDuelSubCircle? target, string spellName, BossCheatCast cheat) {
        if (Resolve(spellName) is not { } resolved) {
            Logger.Warning("Duel {0} | BOSSCHEAT {1}: no client spell '{2}'; the cheat is skipped.",
                Logger.Args(_duel.Duel?.m_duelID.Full ?? 0UL, CheatOf(boss)?.Name, spellName));

            return null;
        }

        var side = CombatActionResolver.CardSide(resolved.Template);
        CombatDuelSubCircle? chosen;
        if (side == CastTargetSide.Friend || Flatten(resolved.Template.m_effects).All(e => !IsEnemyTarget(e))) {
            chosen = target is { Occupied: true } && target.OccupiedTeam == boss.OccupiedTeam ? target : boss;
        }
        else {
            var wizards = _duel.SubCircles.Where(c => c is { Occupied: true, AddedToDuel: true } && c.IsAlive
                && c.OccupiedTeam != boss.OccupiedTeam).ToList();
            if (wizards.Count == 0) {
                return null;
            }

            chosen = target is not null && wizards.Contains(target) ? target : wizards[Rng.Next(wizards.Count)];
        }

        return new QueuedCombatAction {
            SpellCaster = boss,
            SelectedTarget = chosen,
            Spell = resolved.Spell,
            SpellTemplate = resolved.Template,
            Cheat = cheat,
        };
    }

    private static bool IsEnemyTarget(SpellEffect effect)
        => effect.m_effectTarget is kEffectTarget.kAtLeastOneEnemy or kEffectTarget.kEnemyTeam or kEffectTarget.kEnemyTeamAllAtOnce
            or kEffectTarget.kEnemySingle or kEffectTarget.kMultiTargetEnemy or kEffectTarget.kPreselectedEnemySingle;

    private static (Spell Spell, SpellTemplate Template)? Resolve(string name) {
        if (ResolveSpellForTests is { } hook) {
            return hook(name);
        }

        var spell = SpellFactory.GetSpell(name);
        if (spell is null || CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template) {
            return null;
        }

        return (spell, template);
    }

}
