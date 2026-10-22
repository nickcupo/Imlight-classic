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
 *   - Ice, Frost Retort (Round 3, the owner's "damage reflection"): when an enemy's
 *     spell hits an Ice wizard, half of what the wizard's shields and armour
 *     stopped lashes back at that enemy (at most a quarter of the Ice wizard's
 *     health), shown as a free Ice attack card landing that number (the classic
 *     Frost Beetle .. Colossus values, the server naming the value). Once a
 *     round; never on a tick; a retort is never reflected.
 *   - Storm, Static Discharge (Round 3: the owner's overkill split, Wild Bolt look and
 *     backfire): a Storm wizard's Storm spell defeats enemies with damage to spare:
 *     the spare is split equally over every other living enemy, one free bolt
 *     each (the Wild Bolt animation; mastery-demo's card overlay gives the unused
 *     "Pet - Wild Bolt" template a list of values and the server names the one at
 *     or under the share). Each bolt has one chance in five to jump back into the
 *     Storm wizard instead ("Wild Bolt - Amulet", the same values, aimed at its
 *     caster); a discharge never puts more than 40% of the Storm wizard's health
 *     back into them. No accuracy or health: a fizzle still wastes the turn.
 *   - Myth, Shatterstrike (Round 3, the owner's "shield breaking", independent of
 *     minions): when a Myth wizard's own Myth attack lands on an enemy that still
 *     holds a shield or absorb, the wizard casts a free Pierce at it (the client's
 *     card: one shield, never a trap), so the next hitter lands clean. Once a round.
 *   - Life, Overheal (Round 3, the owner's "some damage for solo"): half of the
 *     healing a Life wizard's own heal puts past full health (capped at a quarter
 *     of their health) becomes a free Life attack card at the weakest enemy, at
 *     the listed value at or under it (Imp .. Centaur). Once a round.
 *   - Death, Hungry Dead (Round 3): a Death drain that lands while the wizard is
 *     at half health or less drains again, free (Ghoul, 180). Once a round.
 *   - Balance, Spectrum (Round 3, the owner's "reward balance for using different
 *     schools"): every school a Balance wizard casts for the first time in a fight
 *     (other than Balance) gives a free blade of that school (+35%); the fourth
 *     distinct school, Balance included, gives the team a free Bladestorm (+20%).
 *     A cast counts the one school its landed effects name, else its card's school.
 * Only wizards of the school, from the level of the keystone spell their
 * professor teaches (Fire Elf 5, Tower Shield 16, Storm Shark 10, Troll 5,
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
    internal const int RetortLevel = 16;

    /// <summary>The share of the stopped damage that lashes back, and the cap as a share of the Ice wizard's health.</summary>
    internal const float RetortShare = 0.5f, RetortCapShare = 0.25f;

    /// <summary>The classic Ice attack cards whose damage values show the retort (each a list of fixed values).</summary>
    internal static readonly string[] RetortCards = ["Frost Beetle", "Snow Serpent", "Evil Snowman", "Ice Wyvern", "Colossus"];

    /// <summary>Storm Shark (Halston Balestrom, level 10).</summary>
    internal const int DischargeLevel = 10;

    /// <summary>The bolt at an enemy and the bolt back into the caster (Wild Bolt animation; values from mastery-demo's records).</summary>
    internal const string DischargeBolt = "Pet - Wild Bolt", DischargeBackfire = "Wild Bolt - Amulet";

    /// <summary>One bolt in this many jumps back into the Storm wizard.</summary>
    internal const int DischargeBackfireOdds = 5;

    /// <summary>The most a discharge puts back into the Storm wizard, as a share of their health: never a kill from full.</summary>
    internal const float DischargeSelfCap = 0.4f;

    /// <summary>Troll (level 5): the Myth professor's second attack, and Shatterstrike's level.</summary>
    internal const int ShatterLevel = 5;

    /// <summary>The client's Pierce: removes one shield or absorb from an enemy.</summary>
    internal const string ShatterSpell = "Pierce";

    /// <summary>Sprite (Moolinda Wu, level 10).</summary>
    internal const int OverhealLevel = 10;

    /// <summary>The share of the overflow that becomes damage, and the cap as a share of the Life wizard's health.</summary>
    internal const float OverhealShare = 0.5f, OverhealCapShare = 0.25f;

    /// <summary>The classic Life attack cards whose listed values show the hit.</summary>
    internal static readonly string[] OverhealCards = ["Imp", "Leprechaun", "Nature's Wrath", "Seraph", "Centaur"];

    /// <summary>Ghoul (Dworgyn, level 5).</summary>
    internal const int HungryDeadLevel = 5;

    /// <summary>A drain while the wizard is at or below this share of their health drains again.</summary>
    internal const float HungryDeadShare = 0.5f;

    /// <summary>The client's free Ghoul: a 180 Death drain.</summary>
    internal const string HungryDeadSpell = "Pet - Ghoul";

    /// <summary>Balanceblade (level 12).</summary>
    internal const int SpectrumLevel = 12;

    /// <summary>Distinct schools (Balance included) that bring the team's Bladestorm.</summary>
    internal const int SpectrumSchools = 4;

    /// <summary>The school blades (+35%, one friend) and Bladestorm (+20%, every friend).</summary>
    internal static string BladeOf(string school) => $"{school}blade";

    internal const string SpectrumTeam = "Bladestorm";

}

/// <summary>
/// CLASSIC: one duel's School Mastery procs (experiments.mastery).
/// </summary>
internal sealed class MasteryDirector {

    /// <summary>Test hook: resolves a spell name without the client's spell files.</summary>
    internal static Func<string, (Spell Spell, SpellTemplate Template)?>? ResolveSpellForTests;

    private readonly CombatDuelComponent _duel;
    private readonly HashSet<CombatDuelSubCircle> _retortThisRound = [];
    private readonly Dictionary<CombatDuelSubCircle, int> _prevented = [];
    private readonly HashSet<CombatDuelSubCircle> _overhealThisRound = [];
    private readonly HashSet<CombatDuelSubCircle> _shatterThisRound = [];
    private readonly Dictionary<CombatDuelSubCircle, int> _overheal = [];
    private readonly HashSet<CombatDuelSubCircle> _hungryThisRound = [];
    private readonly Dictionary<CombatDuelSubCircle, HashSet<string>> _schoolsThisFight = [];
    private readonly HashSet<CombatDuelSubCircle> _spectrumTeamGiven = [];
    private readonly Dictionary<CombatDuelSubCircle, int> _overkill = [];
    private readonly HashSet<string> _landedSchools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CombatDuelSubCircle, Snapshot> _before = [];

    private readonly record struct Snapshot(int Health, int Shields, int Blades, int Traps);

    internal MasteryDirector(CombatDuelComponent duel) => _duel = duel;

    /// <summary>True while the active profile turns the experiment on.</summary>
    internal static bool Active => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive && ClassicRuntime.Rules.MasteryEnabled;

    /// <summary>A new round: the once-a-round mechanics may fire again.</summary>
    internal void NewRound(int round) {
        _retortThisRound.Clear();
        _overhealThisRound.Clear();
        _shatterThisRound.Clear();
        _hungryThisRound.Clear();
    }

    /// <summary>Before a spell resolves: every combatant's health and shields, to see what the spell did.</summary>
    internal void BeforeAction(QueuedCombatAction action) {
        _before.Clear();
        _overkill.Clear();
        _prevented.Clear();
        _overheal.Clear();
        _landedSchools.Clear();
        foreach (var circle in _duel.SubCircles.Where(c => c is { Occupied: true, AddedToDuel: true, ParticipantGameStats: not null })) {
            _before[circle] = new Snapshot(circle.ParticipantGameStats.m_currentHitpoints, Shields(circle), Blades(circle), Traps(circle));
        }
    }

    /// <summary>Damage past <paramref name="target"/>'s remaining health in the spell now resolving (CombatEffectApplicator).</summary>
    internal void RecordOverkill(CombatDuelSubCircle target, int overkill)
        => _overkill[target] = _overkill.GetValueOrDefault(target) + Math.Max(overkill, 0);

    /// <summary>Damage <paramref name="target"/>'s shields and armour stopped in the spell now resolving (not a tick).</summary>
    internal void RecordPrevented(CombatDuelSubCircle target, int prevented)
        => _prevented[target] = _prevented.GetValueOrDefault(target) + Math.Max(prevented, 0);

    /// <summary>Healing past <paramref name="target"/>'s full health in the spell now resolving (not a tick).</summary>
    internal void RecordOverheal(CombatDuelSubCircle target, int overflow)
        => _overheal[target] = _overheal.GetValueOrDefault(target) + Math.Max(overflow, 0);

    /// <summary>The school a landed effect of the spell now resolving names ("All" and none are not schools).</summary>
    internal void RecordEffect(SpellEffect effect) {
        if (effect?.m_sDamageType is { Length: > 0 } school && !string.Equals(school, "All", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<MagicSchool>(school, ignoreCase: true, out var known) && known != MagicSchool.None) {
            _landedSchools.Add(known.ToString());
        }
    }

    /// <summary>
    /// After a spell resolved: the procs it sets off, in the order they come. A proc never sets off another proc.
    /// </summary>
    internal List<QueuedCombatAction> AfterAction(QueuedCombatAction action) {
        var procs = new List<QueuedCombatAction>();
        if (action?.SpellCaster is null || action.Spell is null || _duel.IsPvpDuel) {
            return procs;
        }

        if (action.Cheat is null) {
            Add(procs, Kindling(action));
            procs.AddRange(StaticDischarge(action));
            Add(procs, Overheal(action));
            Add(procs, Shatterstrike(action));
            Add(procs, HungryDead(action));
            procs.AddRange(Spectrum(action));
        }

        if (action.Cheat?.IsMastery != true) {
            procs.AddRange(FrostRetort(action));
        }

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

    // ------------------------------------------------------------ Ice: Frost Retort

    private IEnumerable<QueuedCombatAction> FrostRetort(QueuedCombatAction action) {
        var attacker = action.SpellCaster;
        foreach (var (ice, stopped) in _prevented.ToList()) {
            if (stopped <= 0 || !ice.IsWizard || !ice.IsAlive || _retortThisRound.Contains(ice)
                || ice.OccupiedTeam == attacker.ActingTeam || ice.OccupiedTeam == attacker.OccupiedTeam
                || !attacker.IsAlive || !attacker.Occupied || !Masters(ice, MagicSchool.Ice, MasteryRules.RetortLevel)) {
                continue;
            }

            var amount = Math.Min((int) Math.Floor(stopped * MasteryRules.RetortShare),
                (int) Math.Floor(ice.ParticipantGameStats.m_baseHitpoints * MasteryRules.RetortCapShare));
            if (ListedValueFor(MasteryRules.RetortCards, amount, ice, "Ice") is not { } pick || Proc(ice, attacker, pick.Card) is not { } retort) {
                continue;
            }

            retort.ForcedRandomIndex = pick.Index;
            _retortThisRound.Add(ice);
            yield return retort;
        }
    }

    /// <summary>
    /// The card of <paramref name="cards"/> and its listed value (a RandomSpellEffect of fixed damages) that best show
    /// <paramref name="amount"/>: the largest at or under it once the caster's own <paramref name="school"/> damage bonus
    /// (applied again by the cast) is taken out; null under the smallest value.
    /// </summary>
    internal static (string Card, int Index, int Value)? ListedValueFor(IEnumerable<string> cards, int amount, CombatDuelSubCircle caster, string school) {
        var boost = Math.Max(1 + CombatEffectApplicator.GetPercentDamageIncrease(caster, school), 1f);
        var budget = amount / boost;
        (string Card, int Index, int Value)? best = null;
        foreach (var card in cards) {
            if (Resolve(card) is not { } resolved
                || resolved.Template.m_effects?.OfType<RandomSpellEffect>().FirstOrDefault() is not { m_effectList: { } values }) {
                continue;
            }

            for (var i = 0; i < values.Count; i++) {
                var value = values[i]?.m_effectType == kSpellEffects.kDamage ? values[i].m_effectParam : 0;
                if (value > 0 && value <= budget && (best is null || value > best.Value.Value)) {
                    best = (card, i, value);
                }
            }
        }

        return best;
    }

    // ------------------------------------------------------------ Storm: Static Discharge

    private IEnumerable<QueuedCombatAction> StaticDischarge(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        if (!Masters(caster, MagicSchool.Storm, MasteryRules.DischargeLevel) || !caster.IsAlive || !IsSchool(action, MagicSchool.Storm)) {
            yield break;
        }

        var spare = _overkill.Where(kv => !kv.Key.IsAlive && IsEnemyOf(kv.Key, caster)).Sum(kv => kv.Value);
        var survivors = _duel.SubCircles
            .Where(c => c is { Occupied: true, AddedToDuel: true, IsAlive: true } && IsEnemyOf(c, caster)).ToList();
        if (spare <= 0 || survivors.Count == 0) {
            yield break;
        }

        var share = spare / (double) survivors.Count;
        var selfRoom = (int) Math.Floor(caster.ParticipantGameStats.m_baseHitpoints * MasteryRules.DischargeSelfCap);
        foreach (var survivor in survivors) {
            // Rolled once here, by the server, from the duel's own stream; nothing re-rolls it.
            var backfire = _duel.Rng.Next(0, MasteryRules.DischargeBackfireOdds) == 0;
            var card = backfire ? MasteryRules.DischargeBackfire : MasteryRules.DischargeBolt;
            var budget = backfire ? Math.Min(share, selfRoom) : share;
            if (BoltFor(card, budget, caster) is not { } pick || Proc(caster, backfire ? caster : survivor, card) is not { } bolt) {
                continue;
            }

            if (backfire) {
                selfRoom -= pick.Shown;
            }

            bolt.ForcedRandomIndex = pick.Index;
            yield return bolt;
        }
    }

    /// <summary>
    /// The listed value of <paramref name="card"/> at or under <paramref name="amount"/> once the caster's own Storm bonus (which
    /// the cast adds back) is taken out; null under the smallest. Shown is the boosted number the bolt will land.
    /// </summary>
    internal static (int Index, int Value, int Shown)? BoltFor(string card, double amount, CombatDuelSubCircle caster) {
        var boost = Math.Max(1 + CombatEffectApplicator.GetPercentDamageIncrease(caster, "Storm"), 1f);
        if (Resolve(card)?.Template.m_effects?.OfType<RandomSpellEffect>().FirstOrDefault() is not { m_effectList: { } values }) {
            return null;
        }

        (int Index, int Value, int Shown)? best = null;
        for (var i = 0; i < values.Count; i++) {
            var value = values[i]?.m_effectType == kSpellEffects.kDamage ? values[i].m_effectParam : 0;
            if (value > 0 && value * boost <= amount + 0.5 && (best is null || value > best.Value.Value)) {
                best = (i, value, (int) Math.Floor(value * boost));
            }
        }

        return best;
    }

    // ------------------------------------------------------------ Myth: Shatterstrike

    private QueuedCombatAction? Shatterstrike(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        var target = action.SelectedTarget;
        if (!Masters(caster, MagicSchool.Myth, MasteryRules.ShatterLevel) || !caster.IsAlive || !IsSchool(action, MagicSchool.Myth)
            || !Deals(action, kSpellEffects.kDamage, kSpellEffects.kDamageOverTime, kSpellEffects.kStealHealth)
            || target is not { IsAlive: true } || !IsEnemyOf(target, caster) || Shields(target) + Absorbs(target) == 0
            || !_shatterThisRound.Add(caster)) {
            return null;
        }

        return Proc(caster, target, MasteryRules.ShatterSpell);
    }

    private static int Absorbs(CombatDuelSubCircle circle)
        => circle._hangingEffects?.Count(effect => effect.m_effectType == kSpellEffects.kAbsorbDamage && effect.m_paramPerRound > 0) ?? 0;

    // ------------------------------------------------------------ Life: Overheal

    private QueuedCombatAction? Overheal(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        if (!Masters(caster, MagicSchool.Life, MasteryRules.OverhealLevel) || !caster.IsAlive || !IsSchool(action, MagicSchool.Life)
            || _overhealThisRound.Contains(caster)) {
            return null;
        }

        var overflow = _overheal.Where(kv => kv.Key.OccupiedTeam == caster.ActingTeam).Sum(kv => kv.Value);
        var amount = Math.Min((int) Math.Floor(overflow * MasteryRules.OverhealShare),
            (int) Math.Floor(caster.ParticipantGameStats.m_baseHitpoints * MasteryRules.OverhealCapShare));
        var weakest = _duel.SubCircles
            .Where(c => c is { Occupied: true, AddedToDuel: true, IsAlive: true } && IsEnemyOf(c, caster))
            .OrderBy(c => c.ParticipantGameStats.m_currentHitpoints).ThenBy(c => c.SlotIndex).FirstOrDefault();
        if (amount <= 0 || weakest is null
            || ListedValueFor(MasteryRules.OverhealCards, amount, caster, "Life") is not { } pick
            || Proc(caster, weakest, pick.Card) is not { } hit) {
            return null;
        }

        hit.ForcedRandomIndex = pick.Index;
        _overhealThisRound.Add(caster);
        return hit;
    }

    // ------------------------------------------------------------ Death: Hungry Dead

    private QueuedCombatAction? HungryDead(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        if (!Masters(caster, MagicSchool.Death, MasteryRules.HungryDeadLevel) || !caster.IsAlive || !IsSchool(action, MagicSchool.Death)
            || !Deals(action, kSpellEffects.kStealHealth) || _hungryThisRound.Contains(caster)
            || HealthBefore(caster) > caster.ParticipantGameStats.m_baseHitpoints * MasteryRules.HungryDeadShare) {
            return null;
        }

        var at = action.SelectedTarget is { IsAlive: true } target && IsEnemyOf(target, caster) ? target : _duel.SubCircles
            .Where(c => c is { Occupied: true, AddedToDuel: true, IsAlive: true } && IsEnemyOf(c, caster))
            .OrderByDescending(c => c.ParticipantGameStats.m_currentHitpoints).ThenBy(c => c.SlotIndex).FirstOrDefault();
        if (at is null || Proc(caster, at, MasteryRules.HungryDeadSpell) is not { } ghoul) {
            return null;
        }

        _hungryThisRound.Add(caster);
        return ghoul;
    }

    // ------------------------------------------------------------ Balance: Spectrum

    private IEnumerable<QueuedCombatAction> Spectrum(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        if (!Masters(caster, MagicSchool.Balance, MasteryRules.SpectrumLevel) || !caster.IsAlive) {
            yield break;
        }

        var school = SchoolOfCast(action);
        if (school is null) {
            yield break;
        }

        if (!_schoolsThisFight.TryGetValue(caster, out var schools)) {
            _schoolsThisFight[caster] = schools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        if (!schools.Add(school)) {
            yield break;   // a school already counted this fight gives nothing more
        }

        if (!string.Equals(school, nameof(MagicSchool.Balance), StringComparison.OrdinalIgnoreCase)
            && Proc(caster, caster, MasteryRules.BladeOf(school)) is { } blade) {
            yield return blade;
        }

        if (schools.Count >= MasteryRules.SpectrumSchools && _spectrumTeamGiven.Add(caster)
            && Proc(caster, caster, MasteryRules.SpectrumTeam) is { } team) {
            yield return team;
        }
    }

    /// <summary>The one school a cast counts for: the only school its landed effects name, else its card's school.</summary>
    internal string? SchoolOfCast(QueuedCombatAction action) {
        if (_landedSchools.Count == 1) {
            return _landedSchools.First();
        }

        var school = (MagicSchool) action.Spell.m_magicSchoolID;
        return school is MagicSchool.Fire or MagicSchool.Ice or MagicSchool.Storm or MagicSchool.Myth or MagicSchool.Life
            or MagicSchool.Death or MagicSchool.Balance ? school.ToString() : null;
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
