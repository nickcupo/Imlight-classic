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
 *   - Myth, Monster Mash: a Myth wizard with no minion lands a Myth attack: the
 *     monster stays to fight (a free Golem/Troll/Cyclops/Minotaur Minion). Once a round.
 *   - Life, Lifeline: the first time in a fight that a friend (or the Life wizard)
 *     falls to a quarter of their health or below, a free Fairy (420; Satyr 860
 *     from level 26) answers. A defeated friend comes back. Once a fight.
 *   - Death, Puppeteer: a Death drain leaves a creature at half health or below:
 *     a free Beguile takes its next action for the wizard's side. Once a round,
 *     once per creature in a fight.
 *   - Balance, Echo: a Balance wizard's trap on an enemy spreads to the whole
 *     enemy side (free Mass Hex), a blade on a friend to the whole team (free
 *     Bladestorm). Once a round.
 * Only wizards of the school, from the level of the keystone spell their
 * professor teaches (Fire Elf 5, Tower Shield 16, Storm Shark 10, Troll Minion 7,
 * Sprite 10, Ghoul 5, Balanceblade 12).
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

    /// <summary>Troll Minion (level 7).</summary>
    internal const int MonsterMashLevel = 7;

    /// <summary>The classic Myth minion cards: Golem, Troll, Cyclops, Minotaur.</summary>
    internal const string GolemMinion = "Minion Myth 000", TrollMinion = "Minion Myth 001",
        CyclopsMinion = "Minion Myth 002", MinotaurMinion = "Minion Myth 003";

    /// <summary>Sprite (Moolinda Wu, level 10).</summary>
    internal const int LifelineLevel = 10;

    /// <summary>Satyr's level: from here the Lifeline is a Satyr.</summary>
    internal const int LifelineSatyrLevel = 26;

    /// <summary>A friend at or below this share of their health calls the Lifeline.</summary>
    internal const float LifelineShare = 0.25f;

    /// <summary>The client's Fairy (420, a friend) and Satyr (860, a friend).</summary>
    internal const string LifelineFairy = "Pixie Life", LifelineSatyr = "Satyr";

    /// <summary>Ghoul (Dworgyn, level 5).</summary>
    internal const int PuppeteerLevel = 5;

    /// <summary>A creature at or below this share of its health can be beguiled.</summary>
    internal const float PuppeteerShare = 0.5f;

    /// <summary>The client's free Beguile: one mind control at an enemy.</summary>
    internal const string PuppeteerSpell = "Pet - Beguile";

    /// <summary>Balanceblade (level 12).</summary>
    internal const int EchoLevel = 12;

    /// <summary>The client's Mass Hex (+30% trap on every enemy) and Bladestorm (+20% blade on every friend).</summary>
    internal const string EchoTrap = "Mass Hex", EchoBlade = "Bladestorm";

}

/// <summary>
/// CLASSIC: one duel's School Mastery procs (experiments.mastery).
/// </summary>
internal sealed class MasteryDirector {

    /// <summary>Test hook: resolves a spell name without the client's spell files.</summary>
    internal static Func<string, (Spell Spell, SpellTemplate Template)?>? ResolveSpellForTests;

    private readonly CombatDuelComponent _duel;
    private readonly HashSet<CombatDuelSubCircle> _permafrostThisRound = [];
    private readonly HashSet<CombatDuelSubCircle> _mashThisRound = [];
    private readonly HashSet<CombatDuelSubCircle> _puppeteerThisRound = [];
    private readonly HashSet<CombatDuelSubCircle> _echoThisRound = [];
    private readonly Dictionary<CombatDuelSubCircle, int> _leapsThisRound = [];
    private readonly HashSet<CombatDuelSubCircle> _lifelineUsed = [];
    private readonly HashSet<object> _beguiledByMastery = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CombatDuelSubCircle, (int Health, int Shields)> _before = [];

    internal MasteryDirector(CombatDuelComponent duel) => _duel = duel;

    /// <summary>True while the active profile turns the experiment on.</summary>
    internal static bool Active => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive && ClassicRuntime.Rules.MasteryEnabled;

    /// <summary>A new round: the once-a-round mechanics may fire again.</summary>
    internal void NewRound(int round) {
        _permafrostThisRound.Clear();
        _mashThisRound.Clear();
        _puppeteerThisRound.Clear();
        _echoThisRound.Clear();
        _leapsThisRound.Clear();
    }

    /// <summary>Before a spell resolves: every combatant's health and shields, to see what the spell did.</summary>
    internal void BeforeAction(QueuedCombatAction action) {
        _before.Clear();
        foreach (var circle in _duel.SubCircles.Where(c => c is { Occupied: true, AddedToDuel: true, ParticipantGameStats: not null })) {
            _before[circle] = (circle.ParticipantGameStats.m_currentHitpoints, Shields(circle));
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
            Add(procs, MonsterMash(action));
            Add(procs, Puppeteer(action));
            Add(procs, Echo(action));
        }

        if (ownCast || action.Cheat?.IsMastery == true) {
            Add(procs, ChainLightning(action));
        }

        procs.AddRange(Permafrost(action));
        Add(procs, Lifeline());
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

    // ------------------------------------------------------------ Myth: Monster Mash

    private QueuedCombatAction? MonsterMash(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        if (!Masters(caster, MagicSchool.Myth, MasteryRules.MonsterMashLevel) || !IsSchool(action, MagicSchool.Myth)
            || !Deals(action, kSpellEffects.kDamage) || !IsEnemyOf(action.SelectedTarget, caster)
            || _mashThisRound.Contains(caster) || !caster.IsAlive
            || _duel.SubCircles.Any(c => c.IsOwnedMinionOf(caster)) || !_duel.HasFreeSummonSeat(caster)) {
            return null;
        }

        var proc = Proc(caster, caster, MinionFor(action.SpellTemplate));
        if (proc is not null) {
            _mashThisRound.Add(caster);
        }

        return proc;
    }

    /// <summary>The minion that stays after a Myth attack: the monster's own where the classic deck has one.</summary>
    internal static string MinionFor(SpellTemplate? attack) {
        var name = $"{attack?.m_name} {attack?.m_spellBase}";
        return name.Contains("Troll", StringComparison.OrdinalIgnoreCase) ? MasteryRules.TrollMinion
            : name.Contains("Cyclops", StringComparison.OrdinalIgnoreCase) ? MasteryRules.CyclopsMinion
            : name.Contains("Minotaur", StringComparison.OrdinalIgnoreCase) || name.Contains("Orthrus", StringComparison.OrdinalIgnoreCase)
                ? MasteryRules.MinotaurMinion
            : MasteryRules.GolemMinion;
    }

    // ------------------------------------------------------------ Life: Lifeline

    private QueuedCombatAction? Lifeline() {
        foreach (var life in _duel.SubCircles.Where(c => !_lifelineUsed.Contains(c) && c.IsAlive
                     && Masters(c, MagicSchool.Life, MasteryRules.LifelineLevel))) {
            var friend = _duel.SubCircles
                .Where(c => c is { Occupied: true, AddedToDuel: true, IsWizard: true } && c.OccupiedTeam == life.OccupiedTeam
                    && FellToLifeline(c))
                .OrderBy(c => c.ParticipantGameStats.m_currentHitpoints).ThenBy(c => c.SlotIndex)
                .FirstOrDefault();
            if (friend is null) {
                continue;
            }

            var spell = life.ParticipantGameStats.Level >= MasteryRules.LifelineSatyrLevel ? MasteryRules.LifelineSatyr : MasteryRules.LifelineFairy;
            if (Proc(life, friend, spell) is { } heal) {
                _lifelineUsed.Add(life);
                return heal;
            }
        }

        return null;
    }

    private bool FellToLifeline(CombatDuelSubCircle circle) {
        var max = circle.ParticipantGameStats.m_baseHitpoints;
        var line = (int) Math.Floor(max * MasteryRules.LifelineShare);
        return max > 0 && HealthBefore(circle) > line && circle.ParticipantGameStats.m_currentHitpoints <= line;
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

    // ------------------------------------------------------------ Balance: Echo

    private QueuedCombatAction? Echo(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        var target = action.SelectedTarget;
        if (!Masters(caster, MagicSchool.Balance, MasteryRules.EchoLevel) || !IsSchool(action, MagicSchool.Balance)
            || _echoThisRound.Contains(caster) || target is not { Occupied: true }) {
            return null;
        }

        var effects = Flatten(action.SpellTemplate?.m_effects).ToList();
        var trap = IsEnemyOf(target, caster) && effects.Any(e => e.m_effectType == kSpellEffects.kModifyIncomingDamage && e.m_effectParam > 0
            && e.m_effectTarget == kEffectTarget.kEnemySingle);
        var blade = !IsEnemyOf(target, caster) && effects.Any(e => e.m_effectType == kSpellEffects.kModifyOutgoingDamage && e.m_effectParam > 0
            && e.m_effectTarget == kEffectTarget.kFriendlySingle);
        if (!trap && !blade) {
            return null;
        }

        var proc = Proc(caster, target, trap ? MasteryRules.EchoTrap : MasteryRules.EchoBlade);
        if (proc is not null) {
            _echoThisRound.Add(caster);
        }

        return proc;
    }

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
