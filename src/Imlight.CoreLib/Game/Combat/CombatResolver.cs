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
 * COMBAT ACTION RESOLUTION SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Serves as the entry point for resolving all combat actions, coordinating
 * the sequence of spells cast during a round and implementing the core
 * mathematical resolution of combat effects.
 * 
 * USAGE EXAMPLE:
 * var resolver = new CombatResolver(duel, subCircles);
 * resolver.Reset();
 * resolver.AddCombatMove(CombatMoveType.Attack, caster, target, spell);
 * float cinematicTime = resolver.ApplyQueuedCombatActions(out combatActionListObj);
 * 
 * NOTE:
 * This system works in conjunction with several specialized combat classes:
 * 
 * - CombatDuelComponent:    Orchestrates the overall duel, manages participants and phases
 * - CombatResolver:         Processes and resolves combat actions during the execution phase
 * - CombatDuelSubCircle:    Handles individual participant state and position
 * - CombatActionResolver:   Processes queued actions and resolves target selection
 * - CombatEffectApplicator: Applies spell effects with proper modifications
 * - CombatCharms:           Manages offensive modifiers that affect outgoing damage/healing
 * - CombatWards:            Handles defensive modifiers that affect incoming damage
 * - CombatDeck:             Controls spell deck management, drawing and discarding
 * - CombatEffectStack:      Tracks random/variable effect selection using bit-packing
 * 
 * The resolver determines hit/fizzle mechanics, processes spell accuracy,
 * manages the execution order, and coordinates timing of animations.
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Game.Spells;
using Imlight.Classic;
using Imlight.CoreLib.Classic;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Cryptography;

namespace Imlight.CoreLib.Game.Combat;

public class QueuedCombatAction {
    
    public CombatDuelSubCircle SpellCaster;
    public CombatDuelSubCircle SelectedTarget;
    public Spell Spell;
    public SpellTemplate SpellTemplate;
    // CLASSIC: a boss cheat cast (BossCheatDirector): no card, no pips, and out of turn when Interrupt is set.
    internal BossCheatCast Cheat;
    
}

/// <summary>
/// Processes and resolves combat actions during the execution phase of a duel round.
/// </summary>
/// <remarks>
/// Responsible for determining the order of combat actions, handling fizzles, and coordinating 
/// the effects of spells on targets. Manages the flow of a combat turn by processing queued 
/// actions and calculating cinematic timing for visual feedback.
/// </remarks>
public class CombatResolver(Duel duel, CombatDuelSubCircle[] actorSubCircles) {
    
    private const int SPELL_FIZZLE_TIME = 4;
    private const float SPELL_FIZZLE_FAIL_TIME = 3.0f; // the failed casting sign and smoke after the summon
    private const int SPELL_PASS_TIME = 1;
    private const float SPELL_CAST_TIME = 5.0f;
    private const float HANGING_EFFECT_CONSUME_TIME = 1.0f;
    private const float OVER_TIME_ACTIVATION_TIME = 2.0f;
    private const float DEATH_ANIMATION_TIME = 2.0f;

    private readonly Duel _duel = duel;

    private readonly CombatDuelSubCircle[] _subCircles = actorSubCircles;
    private CombatDuelSubCircle[] ActiveSubCircles => [.. _subCircles.Where(x => x.Occupied)];
    private List<QueuedCombatAction> _queuedCombatActions;

    public void Reset() =>
        _queuedCombatActions = [];

    /// <summary>
    /// Applies queued combat actions to the duel, processing each action in order.
    /// </summary>
    /// <param name="combatActionListObj">The object that will hold the list of combat actions.</param>
    /// <returns>The total cinematic time for the actions applied.</returns>
    public float ApplyQueuedCombatActions(out CombatActionListObj combatActionListObj) {
        Logger.Debug("Duel {0} | Applying combat actions..", 
            Logger.Args(_duel.m_duelID.Full, _duel.m_roundNum));

        combatActionListObj = new CombatActionListObj { m_actionList = [] };

        // Some subcircles may not have queued actions. Ensure they do by adding a pass action.
        AddCasterPassActionIfNeeded();
        SortQueuedActions();
        // CLASSIC: a listed boss's extra casts follow its own card (Briskbreeze Tower, October 2009).
        if (ClassicRuntime.IsInitialized && ClassicRuntime.IsActive) {
            BossCheats?.AddExtraCasts(_queuedCombatActions);
        }

        var cinematicTime = ProcessQueuedActions(combatActionListObj);

        return cinematicTime;
    }

    /// <summary>
    /// Adds a combat move to the queue for processing.
    /// </summary>
    /// <param name="type">The type of combat move (e.g., Attack, ChangeMind).</param>
    /// <param name="caster">The caster of the spell.</param>
    /// <param name="target">The target of the spell.</param>
    /// <param name="spell">The spell being cast.</param>
    public void AddCombatMove(CombatMoveType type,
                              CombatDuelSubCircle caster,
                              CombatDuelSubCircle target,
                              Spell spell) {
        // If this spell is already queued by the same caster, remove all of their queued actions.
        _queuedCombatActions.RemoveAll(x => x.SpellCaster == caster);

        if (type == CombatMoveType.ChangeMind) {
            // We can immediately return here. Anytime a caster doesn't have a queued action, they will pass their turn.
            return;
        }

        // Get the spell template.
        SpellTemplate spellTemplate = null;
        if (spell is not null) {
            spellTemplate = (SpellTemplate) CoreObjectFactory.GetCoreTemplate(spell.m_templateID);

            if (spellTemplate is null) {
                Logger.Error("Duel {0} | Slot {1} | Spell template {2} not found",
                    Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, spell.m_templateID));
                return;
            }
        }

        if (spell != null && !caster._duelActor.AllowsMonstrologyCast(caster, spell, spellTemplate)) {
            type = CombatMoveType.Pass; spell = null; spellTemplate = null; target = null;
        }

        var queuedAction = new QueuedCombatAction {
            SpellCaster = caster,
            Spell = type == CombatMoveType.Attack ? spell : null,
            SpellTemplate = spellTemplate,
            SelectedTarget = target
        };
        _queuedCombatActions.Add(queuedAction);

        LogQueuedCombatAction(type, caster, target, spell);
    }

    /// <summary>
    /// Checks if all participants have enqueued actions.
    /// </summary>
    /// <returns>True if all participants have enqueued actions, false otherwise.</returns>
    public bool HaveAllParticipantsEnqueuedActions() {
        var enqueuedPlayers = _subCircles.Where(circle => circle.AddedToDuel && circle.IsAlive);

        return enqueuedPlayers.Count() == _queuedCombatActions.Count;
    }

    /// <summary>The move a given caster has queued this round, or null if none. Used to re-broadcast a
    /// summoned minion's AI-chosen move at planning-phase start so the client telegraphs it.</summary>
    public QueuedCombatAction GetQueuedAction(CombatDuelSubCircle caster)
        => _queuedCombatActions.FirstOrDefault(a => a.SpellCaster == caster);

    private void AddCasterPassActionIfNeeded() {
        var castersWithoutActions = ActiveSubCircles
            .Where(subCircle => subCircle.AddedToDuel && subCircle.IsAlive)
            .Except(_queuedCombatActions.Select(action => action.SpellCaster))
            .ToList();

        foreach (var subCircle in castersWithoutActions) {
            var queuedAction = new QueuedCombatAction {
                SpellCaster = subCircle,
                SelectedTarget = null,
                Spell = null
            };
            _queuedCombatActions.Add(queuedAction);
        }
    }

    private void SortQueuedActions() => _queuedCombatActions.Sort((a, b) => {
        var aSlot = a.SpellCaster.SlotIndex;
        var bSlot = b.SpellCaster.SlotIndex;

        var aTeam = (int) a.SpellCaster.OccupiedTeam;
        var bTeam = (int) b.SpellCaster.OccupiedTeam;

        // Check if both actions belong to the same team
        if (aTeam == bTeam) {
            // Within the same team, sort by slot index (ascending)
            return aSlot.CompareTo(bSlot);
        }
        else {
            // Teams are different, prioritize team who acts first
            if (aTeam == _duel.m_firstTeamToAct) {
                return -1; // Team a acts first
            }
            else if (bTeam == _duel.m_firstTeamToAct) {
                return 1; // Team b acts first
            }
            else {
                return 0; // This should not happen.
            }
        }
    });

    // CLASSIC: the duel's boss cheats; null for a duel with no duel actor.
    private BossCheatDirector BossCheats => _subCircles.Length > 0 ? _subCircles[0]._duelActor?.BossCheats : null;

    private float ProcessQueuedActions(CombatActionListObj combatActionList) {
        var cinematicTime = 0.0f;
        var instantCinematics = _subCircles[0]._duelActor.CheatInstantCinematics;
        var bossCheats = ClassicRuntime.IsInitialized && ClassicRuntime.IsActive ? BossCheats : null; // CLASSIC

        // CLASSIC: an index loop, because a boss's out-of-turn casts go in right after the spell that set them off.
        for (var actionIndex = 0; actionIndex < _queuedCombatActions.Count; actionIndex++) {
            var action = _queuedCombatActions[actionIndex];
            // A caster who left the duel mid-round (fled, logged out, minion removed) has no CombatParticipant any
            // more. Their queued action is void; resolving it would throw and hang the whole duel (audit 2026-10-04).
            if (action.SpellCaster is null || action.SpellCaster.CombatParticipant is null) {
                Logger.Debug("Duel {0} | Slot {1} | Caster left the duel. Skipping action.",
                    Logger.Args(_duel.m_duelID.Full, action.SpellCaster?.SlotIndex ?? -1));

                continue;
            }

            // If the caster is dead, skip this action.
            if (!action.SpellCaster.IsAlive) {
                Logger.Debug("Duel {0} | Slot {1} | Caster is dead. Skipping action.",
                    Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex));

                continue;
            }

            // We want to invoke the overtime effects after we check for death.
            // This is because the overtime effects can kill a participant, and we want to see the animation.
            cinematicTime += InvokeOverTimeEffects(action.SpellCaster);

            // CLASSIC: a lethal tick voids the pending action just as an earlier opponent's hit does.
            // Keep the tick/death animation above, but do not cast or consume stun/Beguile afterward.
            if (ClassicRuntime.IsActive && !action.SpellCaster.IsAlive) {
                continue;
            }

            // Beguile: a combatant beguiled before this action takes it for the other side (like a stun, it applies
            // at the next action, this round's if the combatant has not acted yet), and the action uses it up.
            var beguiled = action.SpellCaster.BeguiledActions > 0;
            try {
                if (beguiled && action.Spell is not null) {
                    action.SelectedTarget = BeguiledTarget(action);
                }

                // A stun is used up at the stunned wizard's next action: this round's if it landed before they
                // acted (owner ruling 2026-09-28: it applies immediately), else next round's, when a stunned
                // wizard can only pass. This is the only place a stun is used up.
                if (action.SpellCaster.CombatParticipant.m_stunned > 0) {
                    action.SpellCaster.CombatParticipant.m_stunned--;

                    cinematicTime += HandlePassAction(action, combatActionList);

                    Logger.Debug("Duel {0} | Slot {1} | Caster is stunned. Passing.",
                        Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex));

                    continue;
                }

                // A null spell indicates the caster is passing their turn.
                if (action.Spell is null || action.SelectedTarget is null) {
                    Logger.Debug("Duel {0} | Slot {1} | Caster is passing their turn.",
                        Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex));

                    cinematicTime += HandlePassAction(action, combatActionList);

                    continue;
                }

                // If our target is gone, pass the turn.
                if (!CanResolveTarget(action)) {
                    cinematicTime += HandlePassAction(action, combatActionList);

                    Logger.Debug("Duel {0} | Slot {1} | Spell cannot occur because target is dead.",
                        Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex));

                    continue;
                }

                bossCheats?.BeforeAction(action); // CLASSIC
                List<QueuedCombatAction> cheatResponses = null;
                // Recheck under the session lock through effects AND costs. Strict Hello may have arrived after queuing.
                if (!action.SpellCaster._duelActor.RunMonstrologyCast(action.SpellCaster, action.Spell, action.SpellTemplate, () => {
                    // Determine if this spell hits or fizzles.
                    var spellHits = SpellHits(action.SpellCaster, action.Spell);
                    if (!spellHits) {
                        cinematicTime += HandleFizzleAction(action, combatActionList);

                        // Increase pips used counter by 1, even if the spell fizzled.
                        // CLASSIC: under the profile's mob reward rules a fizzled card counts as the rules say.
                        action.SpellCaster._usedPipsForExperienceGain += ClassicProgression.MobRewards is { } rewards
                            ? rewards.CombatXp.PipsForFizzle(action.Spell.m_pipCost.m_spellRank, CombatActionResolver.IsXPipSpell(action.Spell))
                            : 1;
                    }
                    else {
                        // Record when this caster's cinematic begins so a summoned minion appears with its cast.
                        action.SpellCaster._duelActor.CurrentActionCinematicOffsetSeconds = cinematicTime;
                        cinematicTime += HandleSuccessfulAction(action, combatActionList);
                        cheatResponses = bossCheats?.AfterAction(action); // CLASSIC
                    }
                })) cinematicTime += HandlePassAction(action, combatActionList);
                if (cheatResponses is { Count: > 0 }) {
                    _queuedCombatActions.InsertRange(actionIndex + 1, cheatResponses);
                }
            }
            finally {
                if (beguiled) {
                    action.SpellCaster.BeguiledActions--;
                }
            }
        }

        return instantCinematics ? 0 : cinematicTime;
    }

    // CLASSIC: a direct friendly heal revives a defeated wizard still in the duel. HoTs do not.
    // Firsthand dated evidence: MMORPG.com, "PvP Overview", Matt Plourde, 2009-03-09.
    // https://www.mmorpg.com/wizard101/system-focus/pvp-overview-2000116592
    internal static bool CanResolveTarget(QueuedCombatAction action) {
        var target = action?.SelectedTarget;
        if (target is null || !target.AddedToDuel || !target.Occupied) return false;
        if (target.IsAlive) return true;
        return ClassicRuntime.IsActive && target.IsWizard && action.SpellCaster is { } caster
            && target.OccupiedTeam == caster.ActingTeam
            && HasDirectFriendlyHeal(action.SpellTemplate?.m_effects);
    }

    private static bool HasDirectFriendlyHeal(IEnumerable<SpellEffect> effects) {
        if (effects is null) return false;
        return effects.Any(effect => effect is not null &&
            (effect.m_effectType == kSpellEffects.kHeal && effect.m_effectParam > 0
                && effect.m_effectTarget is kEffectTarget.kFriendlySingle or kEffectTarget.kFriendlyTeam
                    or kEffectTarget.kFriendlyTeamAllAtOnce
             || effect switch {
                 RandomSpellEffect random => HasDirectFriendlyHeal(random.m_effectList),
                 VariableSpellEffect variable => HasDirectFriendlyHeal(variable.m_effectList),
                 EffectListSpellEffect list => HasDirectFriendlyHeal(list.m_effectList),
                 _ => false,
             }));
    }

    // A beguiled caster's single target: a random living combatant on the side it now acts against (its own
    // teammates, itself only when alone) for a harmful card, or on the side it now acts for for a friendly card.
    private CombatDuelSubCircle BeguiledTarget(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        var side = CombatActionResolver.CardSide(action.SpellTemplate);
        if (side == Imlight.Classic.Spells.CastTargetSide.None) {
            return action.SelectedTarget;
        }

        var living = ActiveSubCircles.Where(circle => circle.IsAlive && circle.AddedToDuel).ToList();
        var candidates = side == Imlight.Classic.Spells.CastTargetSide.Enemy
            ? living.Where(circle => circle.OccupiedTeam != caster.ActingTeam && circle != caster).ToList()
            : living.Where(circle => circle.OccupiedTeam == caster.ActingTeam).ToList();
        if (candidates.Count == 0) {
            return side == Imlight.Classic.Spells.CastTargetSide.Enemy ? caster : action.SelectedTarget;
        }

        var chosen = candidates[caster._duelActor.Rng.Next(candidates.Count)];
        Logger.Debug("Duel {0} | Slot {1} | Beguiled: casts at slot {2}.",
            Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, chosen.SlotIndex));

        return chosen;
    }

    private float HandleFizzleAction(QueuedCombatAction action, CombatActionListObj combatActionList) {
        var fizzleAction = InitializeCombatAction(action);
        fizzleAction.m_spellHits = (char) 0;
        fizzleAction.m_targetSubcircleList.Add(action.SelectedTarget.SlotIndex);
        combatActionList.m_actionList.Add(fizzleAction);

        Logger.Debug("Duel {0} | Slot {1} | Spell fizzled.",
            Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex));

        return GetFizzleCinematicTime(action);
    }

    /// <summary>
    /// CLASSIC: a fizzle plays the spell's Summon stage, the cast and the failed casting sign with its smoke. A flat 4 s
    /// was shorter than that for creature spells (NA Wraith: Summon 2 s), so when a minion fizzled last in the round
    /// the next phase cut its cinematic off (owner client log 2026-10-03 11:13:33: the smoke camera and
    /// kPhase_Resolution in the same second) and the owner saw nothing happen.
    /// </summary>
    private static float GetFizzleCinematicTime(QueuedCombatAction action) {
        if (action.Spell is null) return SPELL_FIZZLE_TIME;
        var name = SpellFactory.GetBaseSpellName(action.Spell.m_templateID);
        if (string.IsNullOrEmpty(name)) return SPELL_FIZZLE_TIME;
        var summon = SpellCinematics.GetSpellSummonTime(name);
        return Math.Max(SPELL_FIZZLE_TIME, summon + SPELL_FIZZLE_FAIL_TIME);
    }

    private float HandleSuccessfulAction(QueuedCombatAction action, CombatActionListObj combatActionList) {
        var cinematicTime = 0.0f;
        var combatAction = InitializeCombatAction(action);
        var extractionBefore = action.SpellCaster._duelActor.BeginMonstrologyCast(action);
        var spellWorthCasting = CombatActionResolver.ProcessedQueuedCombatAction(action, ref combatAction, ref cinematicTime);
        action.SpellCaster._duelActor.ObserveMonstrologyCast(action, extractionBefore);

        LogCombatAction(action, combatAction, spellWorthCasting);

        combatActionList.m_actionList.Add(combatAction);

        if (action.Spell is null) {
            return SPELL_PASS_TIME;
        }

        // CLASSIC: a boss cheat cast uses no card and costs no pips.
        if (action.Cheat is null) {
            DoSpellCastConsequences(action.SpellCaster, combatAction);
        }

        return GetActionCinematicTime(action) + cinematicTime;
    }

    private float HandlePassAction(QueuedCombatAction action, CombatActionListObj combatActionList) {
        var passCombatAction = InitializeCombatAction(action);
        passCombatAction.m_spell = null;
        passCombatAction.m_spellHits = (char) 0;
        combatActionList.m_actionList.Add(passCombatAction);

        return SPELL_PASS_TIME;
    }

    private float InvokeOverTimeEffects(CombatDuelSubCircle caster) {
        // No participant (left mid-round): nothing hangs on them, and _hangingEffects is null.
        var hanging = caster._hangingEffects;
        if (hanging is null) {
            return 0;
        }

        // Get all DoT and HoT effects. Clone the list to avoid concurrent modification.
        var dotEffects = hanging
            .Where(x => x.m_effectType == kSpellEffects.kDamageOverTime)
            .ToList();
        var hotEffects = hanging
            .Where(x => x.m_effectType == kSpellEffects.kHealOverTime)
            .ToList();
        var cinematicTime = (dotEffects.Count + hotEffects.Count) * OVER_TIME_ACTIVATION_TIME;

        foreach (var effect in dotEffects) {
            var initialDamage = effect.m_paramPerRound;
            var wards = CombatWards.FindAppliedWards(caster, effect).ToList();
            if (ClassicRuntime.IsActive) {
                wards = CombatWards.GetWardsBySchool([.. wards], effect.m_sDamageType, out _);
            }
            var damage = CombatWards.GetIncomingDamageFromWards(wards, initialDamage);

            // We don't need to calculate stats from gear because the initial application already did that.

            cinematicTime += HANGING_EFFECT_CONSUME_TIME * wards.Count;
            var healthBeforeTick = caster.ParticipantGameStats.m_currentHitpoints;
            caster.DamageParticipant(damage);
            caster._duelActor.ObserveMonstrologyDot(caster, effect, healthBeforeTick);
            if (ClassicRuntime.IsActive) {
                foreach (var ward in wards.Where(w => w.m_paramPerRound <= 0)) caster._hangingEffects.Remove(ward);
            }
            effect.m_numRounds--;

            // Remove the effect if it's out of rounds.
            if (effect.m_numRounds <= 0) {
                caster._hangingEffects.Remove(effect);
            }
        }

        foreach (var effect in hotEffects) {
            // Todo: are there wards that increase incoming healing?
            // We don't need to calculate stats from gear because the initial application already did that.
            CombatEffectApplicator.HealParticipantBounded(caster, effect.m_paramPerRound);
            effect.m_numRounds--;

            // Remove the effect if it's out of rounds.
            if (effect.m_numRounds <= 0) {
                caster._hangingEffects.Remove(effect);
            }
        }

        // Is the participant dead after the effects?
        // If so, add a death animation time.
        if (!caster.IsAlive) {
            cinematicTime += DEATH_ANIMATION_TIME;
        }

        return cinematicTime;
    }

    private void LogQueuedCombatAction(CombatMoveType type, CombatDuelSubCircle caster, CombatDuelSubCircle target, Spell spell) {
        if (type == CombatMoveType.ChangeMind) {
            Logger.Debug("Duel {0} | Slot {1} | Caster changed their mind and is not casting a spell",
                Logger.Args(_duel.m_duelID.Full, caster.SlotIndex));

            return;
        }

        var targetOrSelf = target is null
            ? "null" : (target.SlotIndex == caster.SlotIndex ? "self" : target.SlotIndex.ToString());
        var spellOrPass = spell is null ? "pass" : spell.m_templateID.ToString();
        Logger.Debug("Duel {0} | Slot {1} | Caster is casting spell {2} against target {3}",
            Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, spellOrPass, targetOrSelf));
    }

    private void LogCombatAction(QueuedCombatAction action, CombatAction combatAction, bool spellWorthCasting) {
        if (spellWorthCasting) {
            var targetsStringForLog = string.Join(", ", combatAction.m_targetSubcircleList);
            var critSuffix = combatAction.m_CritHitList?.Any(x => x.m_mult > 0f) == true ? " (Crits!)" : "";
            Logger.Debug("Duel {0} | Slot {1} | Spell {2} hits targets [{3}]{4}",
                Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex, action.Spell.m_templateID, targetsStringForLog, critSuffix));
        }
        else {
            Logger.Debug("Duel {0} | Slot {1} | Spell {3} not worth casting. Passing turn.",
                Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex, action.Spell.m_templateID));
            combatAction.m_spell = null;
        }
    }

    private static CombatAction InitializeCombatAction(QueuedCombatAction action) => new() {
        m_spellCaster = action.SpellCaster.SlotIndex,
        m_targetSubcircleList = [],
        m_showCast = true,
        m_spellHits = (char) 1,
        m_spell = action.Spell,
        m_serializedBlocks = "\u0000\u0000",
        m_shadowPactTarget = -1,
        m_petCastTarget = -1,
        m_CritHitList = [],
        // CLASSIC: a boss's out-of-turn cast, as the client shows a cheating boss's interrupt.
        m_interrupt = action.Cheat?.Interrupt ?? false,
        m_stringKeyMessage = action.Cheat?.Message,
    };

    private static float GetActionCinematicTime(QueuedCombatAction action) {
        if (action.Spell is null) {
            return SPELL_PASS_TIME;
        }

        var spellName = SpellFactory.GetBaseSpellName(action.Spell.m_templateID);
        var cinematicFactory = SpellCinematics.Instance;

        // All spells will always have a summon time.
        var count = SpellCinematics.GetSpellSummonTime(spellName);

        // Check if this spell has a special casting time. If not, just add the default casting time.
        var castTime = SpellCinematics.GetSpellCastingTime(spellName);
        count += castTime > 0.1f ? castTime : SPELL_CAST_TIME;

        // Check to see if the spell has an act time. If it does, add it to the total time.
        // Otherwise, return the total time.
        var actTime = SpellCinematics.GetSpellActTime(spellName);
        if (actTime <= 0.1f) {
            return count + SpellCinematics.GetSpellTotalTime(spellName);
        }

        count += actTime;

        return count;
    }

    private static bool SpellHits(CombatDuelSubCircle caster, Spell spell) {
        if (caster is null || spell is null) {
            return false;
        }

        if (ConsumeDispell(caster, spell.m_magicSchoolID)) {
            return false;
        }

        if (caster.CheatNoFizzle) {
            return true;
        }

        var spellAccuracy = (int) spell.m_accuracy;
        var stats = caster.CombatParticipant.m_pGameStats;
        var school = MagicSchools.GetMagicSchool(spell.m_magicSchoolID);
        var schoolName = school.m_schoolName;

        var percentIncrease = caster.GetStatBySchool(stats.m_accBonusPercent, schoolName);
        var percentIncreaseAll = stats.m_accBonusPercentAll;
        var percentDecrease = caster.GetStatBySchool(stats.m_accReducePercent, schoolName);
        var percentDecreaseAll = stats.m_accReducePercentAll;

        // Convert to percentages for calculation
        var totalIncrease = (percentIncrease + percentIncreaseAll) * 100;
        var totalDecrease = (percentDecrease + percentDecreaseAll) * 100;

        // Apply percentages to the spell accuracy
        if (ClassicRuntime.IsActive) {
            // CLASSIC: gear accuracy adds percentage points to the card's own accuracy (a 90% Imp with
            // +5% Life accuracy hits 95% of the time), capped to 0..100.
            spellAccuracy = Math.Clamp(spellAccuracy + (int) Math.Round(totalIncrease) - (int) Math.Round(totalDecrease), 0, 100);
        }
        else {
            spellAccuracy *= (int) Math.Floor((1 + totalIncrease / 100.0) * (1 - totalDecrease / 100.0));
        }

        // Apply any hanging accuracy effects
        spellAccuracy = ClassicRuntime.IsActive
            ? ConsumeAccuracyCharms(spellAccuracy, caster, spell.m_magicSchoolID)
            : ConsumeHangingAccuracyEffects(spellAccuracy, caster, spell.m_magicSchoolID);

        var hitChance = caster._duelActor.Rng.Next(0, 100);
        if (ClassicRuntime.IsActive) {
            Logger.Information("[CLASSIC-ACC] template {0}: card {1}%, gear +{2}/-{3}, final {4}%, roll {5} -> {6}",
                Logger.Args(spell.m_templateID, (int) spell.m_accuracy, totalIncrease, totalDecrease, spellAccuracy,
                    hitChance, hitChance < spellAccuracy ? "hit" : "fizzle"));
        }

        // CLASSIC: the roll is 0 to 99, so a spell of accuracy A hits on A of the 100 rolls.
        return ClassicRuntime.IsActive ? hitChance < spellAccuracy : hitChance <= spellAccuracy;
    }

    private static void DoSpellCastConsequences(CombatDuelSubCircle caster, CombatAction action) {
        if (action.m_spell is null) {
            return;
        }

        // Treasure cards are permanently consumed on successful cast.
        // Regular spells are discarded and return to the deck on reshuffle.
        if (action.m_spell.m_treasureCard) {
            var consumedTemplateId = caster.ConsumeFromVault(action.m_spell);
            if (consumedTemplateId != 0 && caster._wizard != null) {
                // CLASSIC: the card came from the deck's Treasure Cards (the vault), which already left the book when the
                // wizard put it in the deck (Wizard.AddTreasureCardToDeck). Taking a book copy as well spent two cards.

                // Also remove from the equipped deck's spell list so it doesn't
                // reappear when the player re-opens their deck after combat.
                var deckSlot = caster._wizard.EquipmentBehavior.SlotList
                    .FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck);
                if (deckSlot?.ItemId != null) {
                    // CLASSIC: spent from the deck's Treasure Card ledger (Wizard.ConsumeDeckTreasureCard).
                    caster._wizard.ConsumeDeckTreasureCard(consumedTemplateId, deckSlot.ItemId.Value);

                    // Tell the client to remove the TC from the deck UI.
                    var spellTemplate = CoreObjectFactory.GetCoreTemplate(consumedTemplateId) as SpellTemplate;
                    if (spellTemplate != null) {
                        var spellHash = StringHash.Compute(spellTemplate.m_name);
                        caster.ParticipantActor.Tell(
                            new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMDECK {
                                SpellID = (int) spellHash,
                                EnchantmentID = 0,
                                DeckID = deckSlot.ItemId.Value,
                                Success = 1,
                                // CLASSIC: Destroy=1. With 0 the client puts the spent card back in its book (stock
                                // WizardClientModules::MSG_RemoveTreasureSpellFromDeck), a copy the server no longer has.
                                Destroy = 1
                            }, ActorRefs.NoSender);
                    }
                }
            }
        }
        else {
            caster.DiscardCard(action.m_spell);
        }

        // Deduce the players mana by the rank of the spell.
        // CLASSIC: not in a PvP duel; the 2009 arena cost no mana (OpenPvpRules.CastingCostsMana).
        if (Imlight.Classic.Pvp.OpenPvpRules.CastingCostsMana(caster._duelActor.Duel?.m_bPVP == true, ClassicRuntime.IsActive)) {
            caster.DeductMana(action.m_spell.m_pipCost.m_spellRank);
        }

        // X-pip spells spend what they used (same GetXPipCost that chose the tier).
        if (CombatActionResolver.IsXPipSpell(action.m_spell)) {
            var xCost = CombatActionResolver.GetXPipCost(action.m_spell, caster);
            // CLASSIC: the 2010 rule counts an X-pip card as one pip, whatever it spent.
            caster._usedPipsForExperienceGain += ClassicProgression.MobRewards?.CombatXp.PipsForCast(0, true, xCost) ?? xCost;

            caster.DeductPips((MagicSchool) action.m_spell.m_magicSchoolID, xCost);
        }
        else {
            // Increase the used pips for experience gain by the rank of the spell.
            // Even 0-rank spells will give the caster 1 pip for experience gain.
            caster._usedPipsForExperienceGain += ClassicProgression.MobRewards?.CombatXp.PipsForCast(action.m_spell.m_pipCost.m_spellRank, false, 0)
                ?? Math.Max((byte) 1, action.m_spell.m_pipCost.m_spellRank);

            caster.DeductPips((MagicSchool) action.m_spell.m_magicSchoolID, action.m_spell.m_pipCost.m_spellRank);
        }
    }

    private static bool ConsumeDispell(CombatDuelSubCircle caster, uint magicSchoolId) {
        var dispellHangingEffect = caster._hangingEffects
            .FirstOrDefault(x => x.m_effectType == kSpellEffects.kDispel
                     && StringHash.Compute(x.m_sDamageType) == magicSchoolId);

        if (dispellHangingEffect is not null) {
            caster._hangingEffects.Remove(dispellHangingEffect);
            return true;
        }
        else {
            return false;
        }
    }

    private static int ConsumeAccuracyCharms(int accuracy, CombatDuelSubCircle caster, uint magicSchoolId) {
        // CLASSIC: an accuracy charm names its school in m_sDamageType (All for any spell) and adds its percentage points.
        var charms = caster._hangingEffects
            .Where(x => x.m_effectType == kSpellEffects.kModifyAccuracy && x.m_sDamageType is { } school
                     && (string.Equals(school, "All", StringComparison.OrdinalIgnoreCase) || StringHash.Compute(school) == magicSchoolId))
            .ToList();

        foreach (var charm in charms) {
            accuracy += charm.m_effectParam;
            caster._hangingEffects.Remove(charm);
        }

        return accuracy;
    }

    private static int ConsumeHangingAccuracyEffects(int startingAccuracy, CombatDuelSubCircle caster, uint magicSchoolId) {
        var accuracyHangingEffects = caster._hangingEffects
            .Where(x => x.m_effectType == kSpellEffects.kModifyAccuracy)
            .Where(x => x.m_damageType == magicSchoolId);

        foreach (var effect in accuracyHangingEffects) {
            startingAccuracy += (int) Math.Floor(1 + effect.m_effectParam / 100.0);
            caster._hangingEffects.Remove(effect);
        }

        return startingAccuracy;
    }

}
