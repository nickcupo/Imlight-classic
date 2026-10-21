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
 * Prototype schools (design: ~/w101c-private/build/playbot-reports/design-mastery.md):
 *   - Fire, Kindling: a Fire wizard's Fire burn lands on an enemy that now
 *     carries two or more Fire burns: a free Detonate ("Pet - Detonate") sets
 *     the newest burn off at once. Once per action. No extra damage, tempo only.
 *   - Ice, Permafrost: an enemy's spell uses up a shield on an Ice wizard who
 *     loses health and stays standing: a free Ice Armor
 *     ("PetPower-Combat-IceArmor-01", absorb 125) on that wizard. Once a round.
 * Only wizards (never creatures, minions or henchmen) of the school, from the
 * level their professor teaches the keystone spell (Fire Elf 5, Tower Shield 16).
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

}

/// <summary>
/// CLASSIC: one duel's School Mastery procs (experiments.mastery).
/// </summary>
internal sealed class MasteryDirector {

    /// <summary>Test hook: resolves a spell name without the client's spell files.</summary>
    internal static Func<string, (Spell Spell, SpellTemplate Template)?>? ResolveSpellForTests;

    private readonly CombatDuelComponent _duel;
    private readonly HashSet<CombatDuelSubCircle> _permafrostThisRound = [];
    private readonly Dictionary<CombatDuelSubCircle, (int Health, int Shields)> _before = [];

    internal MasteryDirector(CombatDuelComponent duel) => _duel = duel;

    /// <summary>True while the active profile turns the experiment on.</summary>
    internal static bool Active => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive && ClassicRuntime.Rules.MasteryEnabled;

    /// <summary>A new round: each Ice wizard's Permafrost may form again.</summary>
    internal void NewRound(int round) => _permafrostThisRound.Clear();

    /// <summary>Before a spell resolves: each wizard's health and shields, to see what the spell took.</summary>
    internal void BeforeAction(QueuedCombatAction action) {
        _before.Clear();
        foreach (var circle in Wizards()) {
            _before[circle] = (circle.ParticipantGameStats.m_currentHitpoints, Shields(circle));
        }
    }

    /// <summary>
    /// After a spell resolved: the procs it sets off, in the order they come. A proc never sets off another Kindling.
    /// </summary>
    internal List<QueuedCombatAction> AfterAction(QueuedCombatAction action) {
        var procs = new List<QueuedCombatAction>();
        if (action?.SpellCaster is null) {
            return procs;
        }

        if (action.Cheat is null && Kindling(action) is { } detonate) {
            procs.Add(detonate);
        }

        procs.AddRange(Permafrost(action));
        return procs;
    }

    // ------------------------------------------------------------ Fire: Kindling

    private QueuedCombatAction? Kindling(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        var target = action.SelectedTarget;
        if (!Masters(caster, MagicSchool.Fire, MasteryRules.KindlingLevel) || action.Spell is null
            || action.Spell.m_magicSchoolID != (uint) MagicSchool.Fire
            || !Flatten(action.SpellTemplate?.m_effects).Any(IsFireBurn)
            || target is not { Occupied: true, IsAlive: true } || target.OccupiedTeam == caster.ActingTeam
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
            if (!wizard.IsAlive || wizard.OccupiedTeam == caster.ActingTeam || _permafrostThisRound.Contains(wizard)
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

    // A protective ward on the circle: a negative incoming-damage ward (Tower Shield, Volcanic Shield, ...).
    private static int Shields(CombatDuelSubCircle circle)
        => circle._hangingEffects?.Count(effect => effect.m_effectType == kSpellEffects.kModifyIncomingDamage && effect.m_effectParam < 0) ?? 0;

    // ------------------------------------------------------------ shared

    private IEnumerable<CombatDuelSubCircle> Wizards()
        => _duel.SubCircles.Where(circle => circle is { Occupied: true, AddedToDuel: true, IsAlive: true, IsWizard: true }
            && circle.ParticipantGameStats is not null);

    /// <summary>True when <paramref name="circle"/> is a wizard of <paramref name="school"/> at <paramref name="level"/> or above.</summary>
    internal static bool Masters(CombatDuelSubCircle? circle, MagicSchool school, int level)
        => circle is { Occupied: true, IsWizard: true, ParticipantGameStats: { } stats }
            && stats.m_schoolID == (uint) school && stats.Level >= level;

    private static QueuedCombatAction? Proc(CombatDuelSubCircle caster, CombatDuelSubCircle target, string spellName) {
        if (Resolve(spellName) is not { } resolved) {
            Logger.Warning("School Mastery: the client spell {0} was not found; no proc.", Logger.Args(spellName));
            return null;
        }

        return new QueuedCombatAction {
            SpellCaster = caster,
            SelectedTarget = target,
            Spell = resolved.Spell,
            SpellTemplate = resolved.Template,
            Cheat = new BossCheatCast(BossCheatKind.Mastery, Interrupt: true, Message: null),
        };
    }

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
