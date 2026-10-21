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
 * CLASSIC SCHOOL MASTERY (EXPERIMENT)
 * ========================================================================
 *
 * PURPOSE:
 * An optional twist on top of the historical game, off unless the profile
 * turns on features.experiments.mastery (no historical profile does): one
 * signature mechanic per school that fires from that school's own play.
 * Every effect is a free cast of a spell template the r806919 client already
 * has (the pet may-cast family), inserted right after the action that set it
 * off with the boss-cheat vehicle (BossCheatCast, kind Mastery): no card, no
 * pips, no accuracy roll, never uses up a Dispel. The client plays the
 * template it already knows, so what it shows is what the server applied.
 *
 * The seven mechanics (design: ~/w101c-private/build/playbot-reports/design-mastery.md), PvE only:
 *   - Fire, Kindling: a Fire burn lands on an enemy that now carries two or more
 *     Fire burns: a free Detonate ("Pet - Detonate") sets the newest off at once.
 *   - Ice, Permafrost: an enemy's spell uses up a shield on an Ice wizard who
 *     loses health and stays standing: a free Ice Armor (absorb 125). Once a round.
 *   - Storm, Chain Lightning: a Storm wizard's single-target Storm spell defeats
 *     its target: the same spell leaps, free, to the enemy with the most health.
 *     A leap can leap again, at most twice a round.
 *   - Myth, Evolution: an owned minion that has acted twice grows into the next
 *     monster (Golem -> Troll -> Cyclops -> Minotaur): a free Draw Power
 *     sacrifices it (+2 pips) and the next minion card is cast free. Needs the
 *     owner's level for that card and a free seat.
 *   - Life, Overgrowth: a Life heal that tops a friend up with 150 or more to
 *     spare grows the spare into a free Spirit Armor (absorb 400). Once a round.
 *   - Death, Puppeteer: a Death drain leaves a creature at half health or below:
 *     a free Beguile takes its next action for the wizard's side. Once a round,
 *     once per creature in a fight.
 *   - Balance, Equilibrium: a Balance attack that uses up both a blade on the
 *     wizard and a trap on the enemy tips the scales: a free Hydra (Fire, Ice and
 *     Storm, 190 each). Once a round.
 * Only wizards of the school, from the level of the keystone spell their
 * professor teaches (Fire Elf 5, Tower Shield 16, Storm Shark 10, Troll Minion 7,
 * Spirit Armor 16, Ghoul 5, Balanceblade 12).
 *
 * USAGE EXAMPLE:
 * duel.Mastery.NewRound(round);                       // CombatDuelComponent, each new round
 * mastery.BeforeAction(action); ... mastery.AfterAction(action)   // CombatResolver, around each resolved spell
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/21/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// CLASSIC: the School Mastery numbers of the prototype. Constants while the experiment waits for the owner; they move to
/// classic-data/rules with the owner's numbers before anything ships.
/// </summary>
internal static class MasteryRules {

    /// <summary>Fire Elf, the Fire professor's first burn (Dalia Falmea, level 5).</summary>
    internal const int KindlingLevel = 5;

    /// <summary>Kindling needs this many Fire burns on the enemy after the new one lands.</summary>
    internal const int KindlingBurns = 2;

    /// <summary>The client's free Detonate: one kDetonateOverTime, param 1, at an enemy.</summary>
    internal const string KindlingSpell = "Pet - Detonate";

    /// <summary>Tower Shield, the Ice professor's shield (Lydia Greyrose, level 16).</summary>
    internal const int PermafrostLevel = 16;

    /// <summary>The client's free Ice Armor: absorb 125 on the caster.</summary>
    internal const string PermafrostSpell = "PetPower-Combat-IceArmor-01";

    /// <summary>Storm Shark (Halston Balestrom, level 10).</summary>
    internal const int ChainLevel = 10;

    /// <summary>Leaps a Storm wizard's spells may make in one round.</summary>
    internal const int ChainLeapsPerRound = 2;

    /// <summary>Troll Minion (level 7): the first evolution, Golem to Troll.</summary>
    internal const int EvolutionLevel = 7;

    /// <summary>A minion evolves after this many actions of its own.</summary>
    internal const int EvolutionActions = 2;

    /// <summary>The creatures the classic Myth minion cards summon, by tier (Golem 0, Troll 1, Cyclops 2, Minotaur 3).</summary>
    internal static readonly IReadOnlyDictionary<ulong, int> MinionTier = new Dictionary<ulong, int> {
        [35972] = 0, [35680] = 1, [36059] = 1, [36060] = 1, [35681] = 2, [36061] = 2, [36062] = 2, [36063] = 2, [35682] = 3,
    };

    /// <summary>The next tier's card and the level its trainer teaches it at (Troll 7, Cyclops 18, Minotaur 38).</summary>
    internal static readonly (string Card, int Level)[] NextMinion = [("Minion Myth 001", 7), ("Minion Myth 002", 18), ("Minion Myth 003", 38)];

    /// <summary>The classic Myth minion cards: Golem, Troll, Cyclops, Minotaur.</summary>
    internal const string GolemMinion = "Minion Myth 000", TrollMinion = "Minion Myth 001",
        CyclopsMinion = "Minion Myth 002", MinotaurMinion = "Minion Myth 003";

    /// <summary>The client's Draw Power: sacrifices the caster's minion and gives 2 pips.</summary>
    internal const string EvolutionSacrifice = "PetPower-Combat-DrawPower-01";

    /// <summary>Spirit Armor (Life, level 16): Overgrowth's shield, and its level.</summary>
    internal const int OvergrowthLevel = 16;

    /// <summary>Spare healing (the card's heal past full health) that grows the shield.</summary>
    internal const int OvergrowthSpare = 150;

    /// <summary>The client's Spirit Armor: absorb 400 on a friend.</summary>
    internal const string OvergrowthSpell = "Spirit Armor";

    /// <summary>Ghoul (Dworgyn, level 5).</summary>
    internal const int PuppeteerLevel = 5;

    /// <summary>A creature at or below this share of its health can be beguiled.</summary>
    internal const float PuppeteerShare = 0.5f;

    /// <summary>The client's free Beguile: one mind control at an enemy.</summary>
    internal const string PuppeteerSpell = "Pet - Beguile";

    /// <summary>Balanceblade (level 12).</summary>
    internal const int EquilibriumLevel = 12;

    /// <summary>The client's free Hydra: 190 Fire, 190 Ice and 190 Storm at one enemy.</summary>
    internal const string EquilibriumSpell = "PetPower-Combat-Hydra-01";

}

/// <summary>
/// CLASSIC: one duel's School Mastery procs (experiments.mastery).
/// </summary>
internal sealed class MasteryDirector {

    /// <summary>Test hook: resolves a spell name without the client's spell files.</summary>
    internal static Func<string, (Spell Spell, SpellTemplate Template)?>? ResolveSpellForTests;

    private readonly CombatDuelComponent _duel;
    private readonly HashSet<CombatDuelSubCircle> _permafrostThisRound = [];
    private readonly HashSet<CombatDuelSubCircle> _overgrowthThisRound = [];
    private readonly HashSet<CombatDuelSubCircle> _puppeteerThisRound = [];
    private readonly HashSet<CombatDuelSubCircle> _equilibriumThisRound = [];
    private readonly Dictionary<CombatDuelSubCircle, int> _leapsThisRound = [];
    private readonly Dictionary<object, int> _minionActions = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _beguiledByMastery = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CombatDuelSubCircle, Snapshot> _before = [];

    private readonly record struct Snapshot(int Health, int Shields, int Blades, int Traps);

    internal MasteryDirector(CombatDuelComponent duel) => _duel = duel;

    /// <summary>True while the active profile turns the experiment on.</summary>
    internal static bool Active => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive && ClassicRuntime.Rules.MasteryEnabled;

    /// <summary>A new round: the once-a-round mechanics may fire again.</summary>
    internal void NewRound(int round) {
        _permafrostThisRound.Clear();
        _overgrowthThisRound.Clear();
        _puppeteerThisRound.Clear();
        _equilibriumThisRound.Clear();
        _leapsThisRound.Clear();
    }

    /// <summary>Before a spell resolves: every combatant's health and shields, to see what the spell did.</summary>
    internal void BeforeAction(QueuedCombatAction action) {
        _before.Clear();
        foreach (var circle in _duel.SubCircles.Where(c => c is { Occupied: true, AddedToDuel: true, ParticipantGameStats: not null })) {
            _before[circle] = new Snapshot(circle.ParticipantGameStats.m_currentHitpoints, Shields(circle), Blades(circle), Traps(circle));
        }
    }

    /// <summary>
    /// After a spell resolved: the procs it sets off, in the order they come. Only Chain Lightning may follow a proc.
    /// </summary>
    internal List<QueuedCombatAction> AfterAction(QueuedCombatAction action) {
        var procs = new List<QueuedCombatAction>();
        if (action?.SpellCaster is null || action.Spell is null || _duel.IsPvpDuel) {
            return procs;
        }

        var ownCast = action.Cheat is null;
        if (ownCast) {
            Add(procs, Kindling(action));
            Add(procs, Overgrowth(action));
            Add(procs, Puppeteer(action));
            Add(procs, Equilibrium(action));
            procs.AddRange(Evolution(action));
        }

        if (ownCast || action.Cheat?.IsMastery == true) {
            Add(procs, ChainLightning(action));
        }

        procs.AddRange(Permafrost(action));
        return procs;
    }

    private static void Add(List<QueuedCombatAction> procs, QueuedCombatAction? proc) {
        if (proc is not null) procs.Add(proc);
    }

    private int HealthBefore(CombatDuelSubCircle circle)
        => _before.TryGetValue(circle, out var before) ? before.Health : circle.ParticipantGameStats?.m_currentHitpoints ?? 0;

    private static bool IsSchool(QueuedCombatAction action, MagicSchool school) => action.Spell.m_magicSchoolID == (uint) school;

    private static bool Deals(QueuedCombatAction action, params kSpellEffects[] kinds)
        => Flatten(action.SpellTemplate?.m_effects).Any(effect => kinds.Contains(effect.m_effectType));

    private static bool IsEnemyOf(CombatDuelSubCircle? target, CombatDuelSubCircle caster)
        => target is { Occupied: true } && target.OccupiedTeam != caster.ActingTeam;

    // ------------------------------------------------------------ Fire: Kindling

    private QueuedCombatAction? Kindling(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        var target = action.SelectedTarget;
        if (!Masters(caster, MagicSchool.Fire, MasteryRules.KindlingLevel) || !IsSchool(action, MagicSchool.Fire)
            || !Flatten(action.SpellTemplate?.m_effects).Any(IsFireBurn)
            || target is not { IsAlive: true } || !IsEnemyOf(target, caster)
            || target._hangingEffects is not { } hanging || hanging.Count(IsFireBurn) < MasteryRules.KindlingBurns) {
            return null;
        }

        return Proc(caster, target, MasteryRules.KindlingSpell);
    }

    private static bool IsFireBurn(SpellEffect effect)
        => effect.m_effectType == kSpellEffects.kDamageOverTime
            && string.Equals(effect.m_sDamageType, "Fire", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------ Ice: Permafrost

    private IEnumerable<QueuedCombatAction> Permafrost(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        foreach (var (wizard, before) in _before) {
            if (!wizard.IsWizard || !wizard.IsAlive || wizard.OccupiedTeam == caster.ActingTeam || _permafrostThisRound.Contains(wizard)
                || !Masters(wizard, MagicSchool.Ice, MasteryRules.PermafrostLevel)
                || wizard.ParticipantGameStats.m_currentHitpoints >= before.Health || Shields(wizard) >= before.Shields) {
                continue;
            }

            if (Proc(wizard, wizard, MasteryRules.PermafrostSpell) is { } armor) {
                _permafrostThisRound.Add(wizard);
                yield return armor;
            }
        }
    }

    // ------------------------------------------------------------ Storm: Chain Lightning

    private QueuedCombatAction? ChainLightning(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        var target = action.SelectedTarget;
        if (!Masters(caster, MagicSchool.Storm, MasteryRules.ChainLevel) || !caster.IsAlive || !IsSchool(action, MagicSchool.Storm)
            || !IsSingleTargetAttack(action.SpellTemplate)
            || target is null || !IsEnemyOf(target, caster) || target.IsAlive || HealthBefore(target) <= 0
            || _leapsThisRound.GetValueOrDefault(caster) >= MasteryRules.ChainLeapsPerRound) {
            return null;
        }

        var next = _duel.SubCircles
            .Where(c => c is { Occupied: true, AddedToDuel: true, IsAlive: true } && c != target && IsEnemyOf(c, caster))
            .OrderByDescending(c => c.ParticipantGameStats.m_currentHitpoints).ThenBy(c => c.SlotIndex)
            .FirstOrDefault();
        if (next is null) {
            return null;
        }

        _leapsThisRound[caster] = _leapsThisRound.GetValueOrDefault(caster) + 1;
        return Cast(caster, next, action.Spell, action.SpellTemplate);
    }

    // A damaging card whose every enemy effect hits one enemy (an area card never leaps).
    private static bool IsSingleTargetAttack(SpellTemplate? template) {
        var effects = Flatten(template?.m_effects).ToList();
        return effects.Any(e => e.m_effectType is kSpellEffects.kDamage or kSpellEffects.kStealHealth or kSpellEffects.kDamageOverTime)
            && !effects.Any(e => e.m_effectTarget is kEffectTarget.kEnemyTeam or kEffectTarget.kEnemyTeamAllAtOnce);
    }

    // ------------------------------------------------------------ Myth: Evolution

    private IEnumerable<QueuedCombatAction> Evolution(QueuedCombatAction action) {
        var minion = action.SpellCaster;
        if (!minion.IsSummonedMinion || !minion.IsAlive || minion.ParticipantObject is not { } creature
            || !MasteryRules.MinionTier.TryGetValue(creature.m_templateID.Full, out var tier)) {
            yield break;
        }

        var owner = _duel.SubCircles.FirstOrDefault(c => c.IsWizard && minion.IsOwnedMinionOf(c));
        var acted = _minionActions[creature] = _minionActions.GetValueOrDefault(creature) + 1;
        if (owner is null || tier >= MasteryRules.NextMinion.Length || acted < MasteryRules.EvolutionActions
            || !Masters(owner, MagicSchool.Myth, MasteryRules.NextMinion[tier].Level) || !_duel.HasFreeSummonSeat(owner)) {
            yield break;
        }

        if (Proc(owner, minion, MasteryRules.EvolutionSacrifice) is not { } sacrifice
            || Proc(owner, owner, MasteryRules.NextMinion[tier].Card) is not { } summon) {
            yield break;
        }

        yield return sacrifice;
        yield return summon;
    }

    // ------------------------------------------------------------ Life: Overgrowth

    private QueuedCombatAction? Overgrowth(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        if (!Masters(caster, MagicSchool.Life, MasteryRules.OvergrowthLevel) || !IsSchool(action, MagicSchool.Life)
            || _overgrowthThisRound.Contains(caster)) {
            return null;
        }

        var heals = Flatten(action.SpellTemplate?.m_effects).Where(e => e.m_effectType == kSpellEffects.kHeal && e.m_effectParam > 0).ToList();
        if (heals.Count == 0) {
            return null;
        }

        var friend = heals.Any(e => e.m_effectTarget is kEffectTarget.kSelf) ? caster : action.SelectedTarget;
        if (friend is not { IsWizard: true, IsAlive: true } || friend.OccupiedTeam != caster.ActingTeam
            || HealthBefore(friend) <= 0 || friend.ParticipantGameStats.m_currentHitpoints < friend.ParticipantGameStats.m_baseHitpoints) {
            return null;
        }

        var spare = HealthBefore(friend) + heals.Max(e => e.m_effectParam) - friend.ParticipantGameStats.m_baseHitpoints;
        if (spare < MasteryRules.OvergrowthSpare) {
            return null;
        }

        var proc = Proc(caster, friend, MasteryRules.OvergrowthSpell);
        if (proc is not null) {
            _overgrowthThisRound.Add(caster);
        }

        return proc;
    }

    // ------------------------------------------------------------ Death: Puppeteer

    private QueuedCombatAction? Puppeteer(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        var target = action.SelectedTarget;
        if (!Masters(caster, MagicSchool.Death, MasteryRules.PuppeteerLevel) || !IsSchool(action, MagicSchool.Death)
            || !Deals(action, kSpellEffects.kStealHealth) || target is not { IsAlive: true, IsWizard: false }
            || !IsEnemyOf(target, caster) || target.BeguiledActions > 0 || _puppeteerThisRound.Contains(caster)
            || target.ParticipantObject is not { } creature || _beguiledByMastery.Contains(creature)
            || target.ParticipantGameStats.m_currentHitpoints > target.ParticipantGameStats.m_baseHitpoints * MasteryRules.PuppeteerShare) {
            return null;
        }

        var proc = Proc(caster, target, MasteryRules.PuppeteerSpell);
        if (proc is not null) {
            _puppeteerThisRound.Add(caster);
            _beguiledByMastery.Add(creature);
        }

        return proc;
    }

    // ------------------------------------------------------------ Balance: Equilibrium

    private QueuedCombatAction? Equilibrium(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        var target = action.SelectedTarget;
        if (!Masters(caster, MagicSchool.Balance, MasteryRules.EquilibriumLevel) || !IsSchool(action, MagicSchool.Balance)
            || !Deals(action, kSpellEffects.kDamage) || !IsEnemyOf(target, caster) || _equilibriumThisRound.Contains(caster)
            || !_before.TryGetValue(caster, out var mine) || !_before.TryGetValue(target!, out var theirs)
            || Blades(caster) >= mine.Blades || Traps(target!) >= theirs.Traps) {
            return null;
        }

        var at = target!.IsAlive ? target : _duel.SubCircles
            .Where(c => c is { Occupied: true, AddedToDuel: true, IsAlive: true } && IsEnemyOf(c, caster))
            .OrderByDescending(c => c.ParticipantGameStats.m_currentHitpoints).ThenBy(c => c.SlotIndex).FirstOrDefault();
        if (at is null) {
            return null;
        }

        var proc = Proc(caster, at, MasteryRules.EquilibriumSpell);
        if (proc is not null) {
            _equilibriumThisRound.Add(caster);
        }

        return proc;
    }

    // A blade on the circle (a positive outgoing-damage charm) and a trap on it (a positive incoming-damage ward).
    private static int Blades(CombatDuelSubCircle circle)
        => circle._hangingEffects?.Count(effect => effect.m_effectType == kSpellEffects.kModifyOutgoingDamage && effect.m_effectParam > 0) ?? 0;

    private static int Traps(CombatDuelSubCircle circle)
        => circle._hangingEffects?.Count(effect => effect.m_effectType == kSpellEffects.kModifyIncomingDamage && effect.m_effectParam > 0) ?? 0;

    // A protective ward on the circle: a negative incoming-damage ward (Tower Shield, Volcanic Shield, ...).
    private static int Shields(CombatDuelSubCircle circle)
        => circle._hangingEffects?.Count(effect => effect.m_effectType == kSpellEffects.kModifyIncomingDamage && effect.m_effectParam < 0) ?? 0;

    // ------------------------------------------------------------ shared

    /// <summary>True when <paramref name="circle"/> is a wizard of <paramref name="school"/> at <paramref name="level"/> or above.</summary>
    internal static bool Masters(CombatDuelSubCircle? circle, MagicSchool school, int level)
        => circle is { Occupied: true, IsWizard: true, ParticipantGameStats: { } stats }
            && stats.m_schoolID == (uint) school && stats.Level >= level;

    private static QueuedCombatAction? Proc(CombatDuelSubCircle caster, CombatDuelSubCircle target, string spellName) {
        if (Resolve(spellName) is not { } resolved) {
            Logger.Warning("School Mastery: the client spell {0} was not found; no proc.", Logger.Args(spellName));
            return null;
        }

        return Cast(caster, target, resolved.Spell, resolved.Template);
    }

    private static QueuedCombatAction Cast(CombatDuelSubCircle caster, CombatDuelSubCircle target, Spell spell, SpellTemplate template) => new() {
        SpellCaster = caster,
        SelectedTarget = target,
        Spell = spell,
        SpellTemplate = template,
        Cheat = new BossCheatCast(BossCheatKind.Mastery, Interrupt: true, Message: null),
    };

    private static IEnumerable<SpellEffect> Flatten(IEnumerable<SpellEffect>? effects) {
        foreach (var effect in effects ?? []) {
            if (effect is null) {
                continue;
            }

            var children = effect switch {
                RandomSpellEffect random => random.m_effectList,
                VariableSpellEffect variable => variable.m_effectList,
                EffectListSpellEffect list => list.m_effectList,
                _ => null,
            };
            if (children is null) {
                yield return effect;
                continue;
            }

            foreach (var child in Flatten(children)) {
                yield return child;
            }
        }
    }

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
