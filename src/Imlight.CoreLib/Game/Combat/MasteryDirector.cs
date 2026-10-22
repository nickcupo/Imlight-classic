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
 *   - Storm, Overkill (Round 3, the owner's idea): a Storm wizard's Storm spell
 *     defeats enemies with damage to spare: the damage past their remaining
 *     health is split equally among every other living enemy, shown as a free
 *     Tempest at the tier (80 a tier) nearest each enemy's share. It goes
 *     through wards, traps and resist like any Storm hit, never spills again,
 *     and gives no accuracy or health: a fizzle still wastes the turn.
 *   - Myth, Taming (Round 3, tied to Monstrology): a Myth spell leaves a creature
 *     the wizard has studied (Animus for it in their Monstrology ledger) at a
 *     quarter of its health or less: the client's own Monster Magic Defeat card
 *     for that creature takes it (free) and its Monster Magic Summon card raises
 *     it on the wizard's side (free). Once a fight; never a boss; needs no minion
 *     and a free seat. Extraction and the tome work as before for every school.
 *   - Life, Overgrowth: a Life heal that tops a friend up with 150 or more to
 *     spare grows the spare into a free Spirit Armor (absorb 400). Once a round.
 *   - Death, Hungry Dead (Round 3): a Death drain that lands while the wizard is
 *     at half health or less drains again, free (Ghoul, 180). Once a round.
 *   - Balance, Spectrum (Round 3, the owner's "reward balance for using different
 *     schools"): every school a Balance wizard casts for the first time in a fight
 *     (other than Balance) gives a free blade of that school (+35%); the fourth
 *     distinct school, Balance included, gives the team a free Bladestorm (+20%).
 *     A cast counts the one school its landed effects name, else its card's school.
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
using Imlight.CoreLib.Game.Monstrology;
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
    internal const int OverkillLevel = 10;

    /// <summary>The client's Tempest: an X-pip Storm card hitting every enemy for 80 a tier.</summary>
    internal const string OverkillSpell = "Tempest";

    /// <summary>Tempest's damage per tier.</summary>
    internal const int OverkillTier = 80;

    /// <summary>Troll Minion (level 7): Myth's first minion card, and Taming's level.</summary>
    internal const int TamingLevel = 7;

    /// <summary>A studied creature at or below this share of its health can be tamed.</summary>
    internal const float TamingShare = 0.25f;

    /// <summary>The client's Monstrology cards for a creature template: Monster Magic Defeat and Monster Magic Summon.</summary>
    internal static string DefeatCard(ulong creature) => $"Kill{creature}";

    internal static string SummonCard(ulong creature) => $"Summon{creature}";

    /// <summary>Spirit Armor (Life, level 16): Overgrowth's shield, and its level.</summary>
    internal const int OvergrowthLevel = 16;

    /// <summary>Spare healing (the card's heal past full health) that grows the shield.</summary>
    internal const int OvergrowthSpare = 150;

    /// <summary>The client's Spirit Armor: absorb 400 on a friend.</summary>
    internal const string OvergrowthSpell = "Spirit Armor";

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
    private readonly HashSet<CombatDuelSubCircle> _overgrowthThisRound = [];
    private readonly HashSet<CombatDuelSubCircle> _hungryThisRound = [];
    private readonly Dictionary<CombatDuelSubCircle, HashSet<string>> _schoolsThisFight = [];
    private readonly HashSet<CombatDuelSubCircle> _spectrumTeamGiven = [];
    private readonly Dictionary<CombatDuelSubCircle, int> _overkill = [];
    private readonly HashSet<string> _landedSchools = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<CombatDuelSubCircle> _tamedThisFight = [];
    private readonly Dictionary<ulong, MonstrologyLedger?> _ledgers = [];
    private readonly Dictionary<CombatDuelSubCircle, Snapshot> _before = [];

    private readonly record struct Snapshot(int Health, int Shields, int Blades, int Traps);

    internal MasteryDirector(CombatDuelComponent duel) => _duel = duel;

    /// <summary>True while the active profile turns the experiment on.</summary>
    internal static bool Active => ClassicRuntime.IsInitialized && ClassicRuntime.IsActive && ClassicRuntime.Rules.MasteryEnabled;

    /// <summary>A new round: the once-a-round mechanics may fire again.</summary>
    internal void NewRound(int round) {
        _retortThisRound.Clear();
        _overgrowthThisRound.Clear();
        _hungryThisRound.Clear();
    }

    /// <summary>Before a spell resolves: every combatant's health and shields, to see what the spell did.</summary>
    internal void BeforeAction(QueuedCombatAction action) {
        _before.Clear();
        _overkill.Clear();
        _prevented.Clear();
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
            Add(procs, Overkill(action));
            Add(procs, Overgrowth(action));
            Add(procs, HungryDead(action));
            procs.AddRange(Spectrum(action));
            procs.AddRange(Taming(action));
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
            if (RetortCardFor(amount, ice) is not { } pick || Proc(ice, attacker, pick.Card) is not { } retort) {
                continue;
            }

            retort.ForcedRandomIndex = pick.Index;
            _retortThisRound.Add(ice);
            yield return retort;
        }
    }

    /// <summary>
    /// The classic Ice attack card and value that best show <paramref name="amount"/>: the largest listed value at or under it
    /// once the wizard's own Ice damage bonus (applied again by the cast) is taken out; null under the smallest value.
    /// </summary>
    internal static (string Card, int Index, int Value)? RetortCardFor(int amount, CombatDuelSubCircle ice) {
        var boost = Math.Max(1 + CombatEffectApplicator.GetPercentDamageIncrease(ice, "Ice"), 1f);
        var budget = amount / boost;
        (string Card, int Index, int Value)? best = null;
        foreach (var card in MasteryRules.RetortCards) {
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

    // ------------------------------------------------------------ Storm: Overkill

    private QueuedCombatAction? Overkill(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        if (!Masters(caster, MagicSchool.Storm, MasteryRules.OverkillLevel) || !caster.IsAlive || !IsSchool(action, MagicSchool.Storm)) {
            return null;
        }

        var spare = _overkill.Where(kv => !kv.Key.IsAlive && IsEnemyOf(kv.Key, caster)).Sum(kv => kv.Value);
        var survivors = _duel.SubCircles
            .Where(c => c is { Occupied: true, AddedToDuel: true, IsAlive: true } && IsEnemyOf(c, caster)).ToList();
        if (spare <= 0 || survivors.Count == 0) {
            return null;
        }

        var tier = OverkillTierFor(spare / (double) survivors.Count, caster);
        if (tier < 1 || Proc(caster, survivors[0], MasteryRules.OverkillSpell) is not { } tempest) {
            return null;
        }

        tempest.ForcedXPips = tier;
        return tempest;
    }

    /// <summary>
    /// Tempest's tier nearest to <paramref name="share"/> once the wizard's own Storm damage bonus (applied again by the
    /// cast) is taken out; 0 when the share is under half a tier.
    /// </summary>
    internal static int OverkillTierFor(double share, CombatDuelSubCircle caster) {
        var boost = 1 + CombatEffectApplicator.GetPercentDamageIncrease(caster, "Storm");
        return (int) Math.Round(share / (MasteryRules.OverkillTier * Math.Max(boost, 1f)), MidpointRounding.AwayFromZero);
    }

    // ------------------------------------------------------------ Myth: Taming (Monstrology)

    /// <summary>Test hook: what Monstrology says about a creature template (its collected id and whether it is a boss).</summary>
    internal static Func<ulong, (uint Collected, bool Boss)?>? CreatureForTests;

    /// <summary>Test hook: whether a wizard (by circle) has studied a collected creature.</summary>
    internal static Func<CombatDuelSubCircle, uint, bool>? StudiedForTests;

    private IEnumerable<QueuedCombatAction> Taming(QueuedCombatAction action) {
        var caster = action.SpellCaster;
        var target = action.SelectedTarget;
        if (!Masters(caster, MagicSchool.Myth, MasteryRules.TamingLevel) || !IsSchool(action, MagicSchool.Myth)
            || !Deals(action, kSpellEffects.kDamage, kSpellEffects.kDamageOverTime, kSpellEffects.kStealHealth)
            || target is not { IsAlive: true, IsWizard: false, IsSummonedMinion: false, ParticipantObject: { } creature }
            || !IsEnemyOf(target, caster) || _tamedThisFight.Contains(caster) || !caster.IsAlive
            || target.ParticipantGameStats.m_currentHitpoints > target.ParticipantGameStats.m_baseHitpoints * MasteryRules.TamingShare
            || _duel.SubCircles.Any(c => c.IsOwnedMinionOf(caster)) || !_duel.HasFreeSummonSeat(caster)
            || Creature(creature.m_templateID.Full) is not { Boss: false } facts || !Studied(caster, facts.Collected)) {
            yield break;
        }

        var tid = creature.m_templateID.Full;
        if (Proc(caster, target, MasteryRules.DefeatCard(tid)) is not { } defeat
            || Proc(caster, caster, MasteryRules.SummonCard(tid)) is not { } summon) {
            yield break;
        }

        _tamedThisFight.Add(caster);
        yield return defeat;
        yield return summon;
    }

    private static (uint Collected, bool Boss)? Creature(ulong template) {
        if (CreatureForTests is { } hook) {
            return hook(template);
        }

        if (CoreObjectFactory.GetCoreTemplate(template) is not GameObjectTemplate creature
            || creature.m_behaviors?.OfType<MobMonsterMagicBehaviorTemplate>().FirstOrDefault() is not { } metadata) {
            return null;   // not a Monstrology creature
        }

        var boss = metadata.m_isBoss || creature.m_behaviors.OfType<NPCBehaviorTemplate>().FirstOrDefault()?.m_bossMob == true;
        return (metadata.m_collectedAsTemplateID != 0 ? metadata.m_collectedAsTemplateID : (uint) template, boss);
    }

    // Studied: the wizard's Monstrology ledger holds Animus for the creature (a successful extraction). Read once a fight.
    private bool Studied(CombatDuelSubCircle wizard, uint collected) {
        if (StudiedForTests is { } hook) {
            return hook(wizard, collected);
        }

        if (wizard._wizard is not { } owner || !MonstrologyService.Enabled
            || !MonstrologySessionPolicy.AllowsWizard(owner, MonstrologyService.Enabled)) {
            return false;
        }

        if (!_ledgers.TryGetValue(owner.CharId, out var ledger)) {
            try {
                ledger = MonstrologyRepository.ForPlayers().Read(owner.CharId);
            } catch (Exception ex) {
                Logger.Warning("School Mastery: Monstrology ledger of {0} unreadable ({1}); no Taming.", Logger.Args(owner.CharId.ToString(), ex.Message));
                ledger = null;
            }

            _ledgers[owner.CharId] = ledger;
        }

        return ledger?.Animus.TryGetValue(collected, out var animus) == true && animus > 0;
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
