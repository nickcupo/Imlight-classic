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
 * CLASSIC BOSS CHEAT MECHANICS (DATA)
 * ========================================================================
 *
 * PURPOSE:
 * The generic, data-driven cheat primitives added for the Waterworks (May
 * 2011) on top of Briskbreeze Tower's (October 2009) extra casts,
 * interrupts, trap breaks and summons:
 *   - steps: what a cheat does (a spell, or one of several, cast out of
 *     turn for no pips on a chosen target, optionally with its removal
 *     count or disposition changed, never fizzling, or the cycle's current
 *     bubble cast again);
 *   - marks: seats the boss marks each round (Luska's ink), more seats as
 *     its health drops, with a visible spell on the marked seats and
 *     another on everyone else, reapplied only when gone;
 *   - reactions: a cheat in answer to a wizard's spell, by the kind of
 *     spell (attack, trap, charm, blade, shield, heal, global, any), single
 *     target or not, who it hit (this boss, its side, anyone), who cast it
 *     (marked, unmarked) and what was missing (a trap on the target, a
 *     blade on the caster), optionally only in one phase of a cycle;
 *   - round_start: casts at the start of a round, before every other
 *     action, every N rounds, in a health window, optionally only when no
 *     wizard attacked the boss in the round before (Frostbranch's shape);
 *   - cycles: a group of bosses sharing a timed cycle of phases (Sylster's
 *     white and black Doom and Gloom), each phase a bubble and the steps
 *     played on entering it, held by the first living boss of its holders
 *     and handed to the next one when the holder falls.
 *
 * USAGE EXAMPLE:
 * boss.Reactions, boss.Marks, boss.RoundStart; table.Cycles; table.CycleOf(template)
 *
 * NOTE:
 * The runtime is BossCheatDirector (Imlight.CoreLib). Every number here is a
 * data value with its source in the boss-cheat file.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/16/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>
/// Whom a cheat step's spell goes on.
/// </summary>
public enum CheatStepTarget {
    /// <summary>The spell decides: a friendly spell on the boss, an enemy spell on the triggering wizard or a random one.</summary>
    Auto,
    /// <summary>The boss itself.</summary>
    Self,
    /// <summary>The wizard whose spell set the cheat off (reactions only).</summary>
    Caster,
}

/// <summary>
/// Which hanging effects a removal step takes, overriding the spell's own disposition.
/// </summary>
public enum CheatRemoveOnly {
    /// <summary>The spell's own disposition.</summary>
    Spell,
    /// <summary>Traps, hexes and prisms (harmful wards) or weaknesses (harmful charms).</summary>
    Harmful,
    /// <summary>Shields (beneficial wards) or blades (beneficial charms).</summary>
    Beneficial,
}

/// <summary>
/// One thing a cheat does: a spell cast out of turn for no pips.
/// </summary>
/// <param name="Spells">The spell, or several of which one is drawn at random.</param>
/// <param name="Target">Whom it goes on.</param>
/// <param name="RemoveCount">For a spell that removes wards or charms: how many a target, instead of the spell's own count.</param>
/// <param name="RemoveOnly">For such a spell: which ones, instead of the spell's own disposition.</param>
/// <param name="SureHit">It never misses on accuracy (a dispel on the boss still stops it).</param>
/// <param name="RecastPhase">Instead of a spell: the bubble of the cycle's current phase again.</param>
public sealed record CheatStep(ImmutableArray<string> Spells, CheatStepTarget Target, int? RemoveCount, CheatRemoveOnly RemoveOnly,
    bool SureHit, bool RecastPhase) {

    /// <summary>A step that casts <paramref name="spell"/> (test hook and the cycle's bubble).</summary>
    public static CheatStep Of(string spell, CheatStepTarget target = CheatStepTarget.Auto, int? removeCount = null,
        CheatRemoveOnly removeOnly = CheatRemoveOnly.Spell, bool sureHit = false)
        => new([spell], target, removeCount, removeOnly, sureHit, false);
}

/// <summary>
/// A health window in percent of the boss's maximum health: below <see cref="BelowPercent"/> and at least
/// <see cref="AtLeastPercent"/> (either may be absent).
/// </summary>
public readonly record struct PercentWindow(int? BelowPercent, int? AtLeastPercent) {

    /// <summary>True when <paramref name="health"/> of <paramref name="max"/> is inside the window.</summary>
    public bool Contains(int health, int max) {
        var hundredths = (long) health * 100;
        return (BelowPercent is not { } below || hundredths < (long) below * max)
            && (AtLeastPercent is not { } atLeast || hundredths >= (long) atLeast * max);
    }

}

/// <summary>
/// Who cast the spell a reaction answers.
/// </summary>
public enum CheatCasterFilter {
    Any,
    /// <summary>A wizard in a seat the boss has marked.</summary>
    Marked,
    /// <summary>A wizard in a seat the boss has not marked.</summary>
    Unmarked,
}

/// <summary>
/// Whom the spell a reaction answers must have been cast at.
/// </summary>
public enum CheatTargetFilter {
    /// <summary>This boss.</summary>
    Self,
    /// <summary>Any creature on this boss's side.</summary>
    Side,
    /// <summary>Anyone (a wizard's blade on a wizard counts).</summary>
    Any,
}

/// <summary>
/// A buff whose absence sets a reaction off (it only has to be present when the spell is cast, not used).
/// </summary>
public enum CheatMissing {
    None,
    /// <summary>The spell's target had no trap, hex or prism on it.</summary>
    TrapOnTarget,
    /// <summary>The caster had no blade on it.</summary>
    BladeOnCaster,
}

/// <summary>
/// What sets a reaction off.
/// </summary>
/// <param name="Kinds">The kinds of wizard spell it answers.</param>
/// <param name="SingleTarget">Only a spell that targets one combatant (no team, area or global effect).</param>
/// <param name="Target">Whom the spell must have been cast at.</param>
/// <param name="Caster">Who must have cast it.</param>
/// <param name="Phase">Only in this phase of the boss's cycle, or always.</param>
/// <param name="Missing">Only when this buff was missing.</param>
public sealed record CheatCondition(ImmutableArray<CheatTrigger> Kinds, bool SingleTarget, CheatTargetFilter Target,
    CheatCasterFilter Caster, string? Phase, CheatMissing Missing);

/// <summary>
/// A cheat in answer to a wizard's spell: its steps follow that spell at once, before the next caster.
/// </summary>
/// <param name="When">What sets it off.</param>
/// <param name="Do">What it does, in order.</param>
/// <param name="Message">The client string key shown with its first cast.</param>
/// <param name="PerRound">How many times a round at most (null: no limit).</param>
/// <param name="Source">Where the behaviour is described.</param>
public sealed record BossReaction(CheatCondition When, ImmutableArray<CheatStep> Do, string? Message, int? PerRound, string Source);

/// <summary>
/// A seat the boss marks, from a health mark on (seat 1 is the first wizard circle, the sun).
/// </summary>
public sealed record MarkSeat(int Seat, int? HealthBelowPercent);

/// <summary>
/// The seats a boss marks: at the start of each round, and at once when a wizard's spell takes it below a seat's
/// health mark. A marked seat gets <see cref="MarkedSpell"/> and every other wizard <see cref="UnmarkedSpell"/>, each
/// only when the last one is gone.
/// </summary>
public sealed record BossMarks(ImmutableArray<MarkSeat> Seats, string MarkedSpell, string? UnmarkedSpell, string? Message,
    string? AddedMessage, bool SureHit, string Source) {

    /// <summary>The seats marked at <paramref name="health"/> of <paramref name="max"/>.</summary>
    public IEnumerable<int> SeatsAt(int health, int max)
        => Seats.Where(s => s.HealthBelowPercent is not { } below || new PercentWindow(below, null).Contains(health, max)).Select(s => s.Seat);
}

/// <summary>
/// Casts at the start of a round, before every other action.
/// </summary>
/// <param name="Do">What it casts.</param>
/// <param name="FirstRound">The first round it comes in.</param>
/// <param name="Every">Then every this many rounds.</param>
/// <param name="Health">The health window it comes in.</param>
/// <param name="OnlyIfNotAttacked">Only when no wizard's attack took health off the boss in the round before.</param>
/// <param name="Message">The client string key shown with its first cast.</param>
/// <param name="Source">Where it is described.</param>
public sealed record BossRoundStart(ImmutableArray<CheatStep> Do, int FirstRound, int Every, PercentWindow Health,
    bool OnlyIfNotAttacked, string? Message, string Source) {

    /// <summary>True when it comes in round <paramref name="round"/>.</summary>
    public bool IsDue(int round) => round >= FirstRound && (round - FirstRound) % Every == 0;
}

/// <summary>
/// One phase of a cycle.
/// </summary>
/// <param name="Id">Its name, as reactions refer to it.</param>
/// <param name="Spell">The bubble cast as it starts (a global spell).</param>
/// <param name="FirstRounds">How long it lasts as the first phase after the cycle (re)starts.</param>
/// <param name="Rounds">How long it lasts otherwise.</param>
/// <param name="OnEnter">What the holder casts after the bubble as the phase starts (a step with nothing to remove is skipped).</param>
/// <param name="Message">The client string key shown with the bubble.</param>
/// <param name="Reactions">Reactions of the holder while the phase lasts.</param>
public sealed record CyclePhase(string Id, string Spell, int FirstRounds, int Rounds, ImmutableArray<CheatStep> OnEnter,
    string? Message, ImmutableArray<BossReaction> Reactions);

/// <summary>
/// A boss that may hold a cycle, in order; the message is what it says as it takes the cycle over.
/// </summary>
public sealed record CycleHolder(uint Template, string? TakeoverMessage);

/// <summary>
/// A timed cycle of phases shared by a group of bosses. The first living holder plays it from the first round; when it
/// falls, the next living holder takes over at the start of the next round and starts again from the first phase.
/// </summary>
public sealed record BossCycle(string Id, ImmutableArray<CycleHolder> Holders, ImmutableArray<CyclePhase> Phases, string Source) {

    /// <summary>The phase called <paramref name="id"/>, or null.</summary>
    public CyclePhase? Phase(string id) => Phases.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
}

public static partial class BossCheatsLoader {

    internal static readonly FrozenSet<string> s_stepKeys = FrozenSet.Create(StringComparer.Ordinal,
        "spell", "one_of", "target", "remove_count", "remove_only", "sure_hit", "recast_phase", "notes");
    internal static readonly FrozenSet<string> s_reactionKeys = FrozenSet.Create(StringComparer.Ordinal,
        "when", "do", "message", "per_round", "source", "notes");
    internal static readonly FrozenSet<string> s_conditionKeys = FrozenSet.Create(StringComparer.Ordinal,
        "kinds", "single_target", "target", "caster", "phase", "missing", "notes");
    internal static readonly FrozenSet<string> s_marksKeys = FrozenSet.Create(StringComparer.Ordinal,
        "seats", "marked_spell", "unmarked_spell", "message", "added_message", "sure_hit", "source", "notes");
    internal static readonly FrozenSet<string> s_seatKeys = FrozenSet.Create(StringComparer.Ordinal,
        "seat", "health_below_percent", "notes");
    internal static readonly FrozenSet<string> s_roundStartKeys = FrozenSet.Create(StringComparer.Ordinal,
        "do", "first_round", "every", "health_below_percent", "health_at_least_percent", "only_if_not_attacked", "message", "source", "notes");
    internal static readonly FrozenSet<string> s_cycleKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "holders", "phases", "source", "source_date", "notes");
    internal static readonly FrozenSet<string> s_holderKeys = FrozenSet.Create(StringComparer.Ordinal,
        "template", "name", "takeover_message", "notes");
    internal static readonly FrozenSet<string> s_phaseKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "spell", "first_rounds", "rounds", "on_enter", "message", "reactions", "notes");

    private static readonly string[] s_reactionKinds = ["any", "hit", "heal", "shield", "blade", "attack", "trap", "charm", "global"];
    private static readonly string[] s_stepTargets = ["auto", "self", "caster"];
    private static readonly string[] s_removeOnly = ["harmful", "beneficial"];
    private static readonly string[] s_targetFilters = ["self", "side", "any"];
    private static readonly string[] s_casterFilters = ["any", "marked", "unmarked"];
    private static readonly string[] s_missing = ["trap_on_target", "blade_on_caster"];

    private static string? ReadMessage(YMap map, string keyPath, string key, YamlDiagnostics diagnostics) {
        if (map.Find(key) is not { } entry || diagnostics.ReadString(entry.Value, YamlTree.Join(keyPath, key)) is not { } message) {
            return null;
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(message, @"^[A-Za-z0-9-]+_[0-9]+\z")) {
            diagnostics.At(entry.Value, YamlTree.Join(keyPath, key), $"'{message}' is not a client string key (Table_00000123)");
            return null;
        }

        return message;
    }

    private static ImmutableArray<CheatStep> ReadSteps(YNode node, string keyPath, bool allowCaster, bool allowRecast, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadList(node, keyPath) is not { } list) {
            return [];
        }

        if (list.Items.IsEmpty) {
            diagnostics.At(list, keyPath, "needs at least one step");
        }

        var steps = ImmutableArray.CreateBuilder<CheatStep>();
        for (var i = 0; i < list.Items.Length; i++) {
            var path = YamlTree.Index(keyPath, i);
            if (diagnostics.ReadMap(list.Items[i], path) is not { } map) {
                continue;
            }

            diagnostics.CheckKeys(map, path, s_stepKeys, []);
            var recast = map.Find("recast_phase") is { } rp && diagnostics.ReadBool(rp.Value, YamlTree.Join(path, "recast_phase")) == true;
            var spells = ImmutableArray.CreateBuilder<string>();
            if (map.Find("spell") is { } sp && diagnostics.ReadString(sp.Value, YamlTree.Join(path, "spell")) is { } spell) {
                spells.Add(spell);
            }

            if (map.Find("one_of") is { } oo) {
                spells.AddRange(ReadNames(oo.Value, YamlTree.Join(path, "one_of"), diagnostics));
            }

            if (recast) {
                if (!allowRecast) {
                    diagnostics.At(map, path, "recast_phase is only for a boss in a cycle");
                }

                if (spells.Count > 0) {
                    diagnostics.At(map, path, "a recast_phase step casts the phase's own bubble; give no spell");
                }
            }
            else if ((map.Find("spell") is null) == (map.Find("one_of") is null)) {
                diagnostics.At(map, path, "a step needs exactly one of spell, one_of and recast_phase");
                continue;
            }

            var target = CheatStepTarget.Auto;
            if (map.Find("target") is { } te && diagnostics.ReadEnum(te.Value, YamlTree.Join(path, "target"), s_stepTargets) is { } t) {
                target = Enum.Parse<CheatStepTarget>(t, ignoreCase: true);
                if (target == CheatStepTarget.Caster && !allowCaster) {
                    diagnostics.At(te.Value, YamlTree.Join(path, "target"), "target caster is only for a reaction (there is no triggering spell here)");
                }
            }

            var count = map.Find("remove_count") is { } rc ? diagnostics.ReadInt(rc.Value, YamlTree.Join(path, "remove_count"), 1, 20) : null;
            var only = CheatRemoveOnly.Spell;
            if (map.Find("remove_only") is { } ro && diagnostics.ReadEnum(ro.Value, YamlTree.Join(path, "remove_only"), s_removeOnly) is { } o) {
                only = Enum.Parse<CheatRemoveOnly>(o, ignoreCase: true);
            }

            var sure = map.Find("sure_hit") is { } sh && diagnostics.ReadBool(sh.Value, YamlTree.Join(path, "sure_hit")) == true;
            steps.Add(new CheatStep(spells.ToImmutable(), target, count, only, sure, recast));
        }

        return steps.ToImmutable();
    }

    private static ImmutableArray<BossReaction> ReadReactions(YNode node, string keyPath, bool inCycle, string? fixedPhase,
        YamlDiagnostics diagnostics) {
        if (diagnostics.ReadList(node, keyPath) is not { } list) {
            return [];
        }

        var reactions = ImmutableArray.CreateBuilder<BossReaction>();
        for (var i = 0; i < list.Items.Length; i++) {
            var path = YamlTree.Index(keyPath, i);
            if (diagnostics.ReadMap(list.Items[i], path) is not { } map) {
                continue;
            }

            diagnostics.CheckKeys(map, path, s_reactionKeys, ["when", "do", "source"]);
            CheatCondition? when = null;
            if (map.Find("when") is { } we && diagnostics.ReadMap(we.Value, YamlTree.Join(path, "when")) is { } wm) {
                when = ReadCondition(wm, YamlTree.Join(path, "when"), inCycle, fixedPhase, diagnostics);
            }

            var steps = map.Find("do") is { } de ? ReadSteps(de.Value, YamlTree.Join(path, "do"), true, inCycle, diagnostics) : [];
            var message = ReadMessage(map, path, "message", diagnostics);
            var perRound = map.Find("per_round") is { } pr ? diagnostics.ReadInt(pr.Value, YamlTree.Join(path, "per_round"), 1, 20) : null;
            var source = map.Find("source") is { } so ? diagnostics.ReadString(so.Value, YamlTree.Join(path, "source")) : null;
            if (when is not null && !steps.IsEmpty && source is not null) {
                reactions.Add(new BossReaction(when, steps, message, perRound, source));
            }
        }

        return reactions.ToImmutable();
    }

    private static CheatCondition? ReadCondition(YMap map, string keyPath, bool inCycle, string? fixedPhase, YamlDiagnostics diagnostics) {
        diagnostics.CheckKeys(map, keyPath, s_conditionKeys, ["kinds"]);
        var kinds = ImmutableArray.CreateBuilder<CheatTrigger>();
        if (map.Find("kinds") is { } ke && diagnostics.ReadList(ke.Value, YamlTree.Join(keyPath, "kinds")) is { } kl) {
            for (var j = 0; j < kl.Items.Length; j++) {
                if (diagnostics.ReadEnum(kl.Items[j], YamlTree.Index(YamlTree.Join(keyPath, "kinds"), j), s_reactionKinds) is { } kind) {
                    kinds.Add(Enum.Parse<CheatTrigger>(kind, ignoreCase: true));
                }
            }

            if (kl.Items.IsEmpty) {
                diagnostics.At(ke.Value, YamlTree.Join(keyPath, "kinds"), "a reaction needs at least one kind of spell");
            }
        }

        var single = map.Find("single_target") is { } st && diagnostics.ReadBool(st.Value, YamlTree.Join(keyPath, "single_target")) == true;
        var target = CheatTargetFilter.Self;
        if (map.Find("target") is { } te && diagnostics.ReadEnum(te.Value, YamlTree.Join(keyPath, "target"), s_targetFilters) is { } t) {
            target = Enum.Parse<CheatTargetFilter>(t, ignoreCase: true);
        }

        var caster = CheatCasterFilter.Any;
        if (map.Find("caster") is { } ce && diagnostics.ReadEnum(ce.Value, YamlTree.Join(keyPath, "caster"), s_casterFilters) is { } c) {
            caster = Enum.Parse<CheatCasterFilter>(c, ignoreCase: true);
        }

        var missing = CheatMissing.None;
        if (map.Find("missing") is { } me && diagnostics.ReadEnum(me.Value, YamlTree.Join(keyPath, "missing"), s_missing) is { } m) {
            missing = m == "trap_on_target" ? CheatMissing.TrapOnTarget : CheatMissing.BladeOnCaster;
        }

        var phase = fixedPhase;
        if (map.Find("phase") is { } pe && diagnostics.ReadString(pe.Value, YamlTree.Join(keyPath, "phase")) is { } p) {
            if (fixedPhase is not null) {
                diagnostics.At(pe.Value, YamlTree.Join(keyPath, "phase"), "a phase's own reaction already belongs to that phase");
            }
            else if (!inCycle) {
                diagnostics.At(pe.Value, YamlTree.Join(keyPath, "phase"), "phase is only for a boss that is a holder of a cycle");
            }

            phase = p;
        }

        return kinds.Count == 0 ? null : new CheatCondition(kinds.ToImmutable(), single, target, caster, phase, missing);
    }

    private static BossMarks? ReadMarks(YMap map, string keyPath, YamlDiagnostics diagnostics) {
        diagnostics.CheckKeys(map, keyPath, s_marksKeys, ["seats", "marked_spell", "source"]);
        var seats = ImmutableArray.CreateBuilder<MarkSeat>();
        if (map.Find("seats") is { } se && diagnostics.ReadList(se.Value, YamlTree.Join(keyPath, "seats")) is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var path = YamlTree.Index(YamlTree.Join(keyPath, "seats"), i);
                if (diagnostics.ReadMap(list.Items[i], path) is not { } sm) {
                    continue;
                }

                diagnostics.CheckKeys(sm, path, s_seatKeys, ["seat"]);
                var seat = sm.Find("seat") is { } s ? diagnostics.ReadInt(s.Value, YamlTree.Join(path, "seat"), 1, 4) : null;
                var below = sm.Find("health_below_percent") is { } b ? diagnostics.ReadInt(b.Value, YamlTree.Join(path, "health_below_percent"), 1, 100) : null;
                if (seat is null) {
                    continue;
                }

                if (seats.Any(x => x.Seat == seat)) {
                    diagnostics.At(sm, path, $"seat {seat} is listed twice");
                    continue;
                }

                seats.Add(new MarkSeat(seat.Value, below));
            }

            if (list.Items.IsEmpty) {
                diagnostics.At(list, YamlTree.Join(keyPath, "seats"), "marks need at least one seat");
            }
        }

        var marked = map.Find("marked_spell") is { } ms ? diagnostics.ReadString(ms.Value, YamlTree.Join(keyPath, "marked_spell")) : null;
        var unmarked = map.Find("unmarked_spell") is { } us ? diagnostics.ReadString(us.Value, YamlTree.Join(keyPath, "unmarked_spell")) : null;
        var message = ReadMessage(map, keyPath, "message", diagnostics);
        var added = ReadMessage(map, keyPath, "added_message", diagnostics);
        var sure = map.Find("sure_hit") is { } sh && diagnostics.ReadBool(sh.Value, YamlTree.Join(keyPath, "sure_hit")) == true;
        var source = map.Find("source") is { } so ? diagnostics.ReadString(so.Value, YamlTree.Join(keyPath, "source")) : null;

        return marked is null || source is null || seats.Count == 0
            ? null
            : new BossMarks(seats.ToImmutable(), marked, unmarked, message, added, sure, source);
    }

    private static ImmutableArray<BossRoundStart> ReadRoundStart(YNode node, string keyPath, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadList(node, keyPath) is not { } list) {
            return [];
        }

        var rules = ImmutableArray.CreateBuilder<BossRoundStart>();
        for (var i = 0; i < list.Items.Length; i++) {
            var path = YamlTree.Index(keyPath, i);
            if (diagnostics.ReadMap(list.Items[i], path) is not { } map) {
                continue;
            }

            diagnostics.CheckKeys(map, path, s_roundStartKeys, ["do", "source"]);
            var steps = map.Find("do") is { } de ? ReadSteps(de.Value, YamlTree.Join(path, "do"), false, false, diagnostics) : [];
            var first = map.Find("first_round") is { } fr ? diagnostics.ReadInt(fr.Value, YamlTree.Join(path, "first_round"), 1, 99) : 1;
            var every = map.Find("every") is { } ev ? diagnostics.ReadInt(ev.Value, YamlTree.Join(path, "every"), 1, 99) : 1;
            var window = ReadPercentWindow(map, path, diagnostics);
            var quiet = map.Find("only_if_not_attacked") is { } oa && diagnostics.ReadBool(oa.Value, YamlTree.Join(path, "only_if_not_attacked")) == true;
            var message = ReadMessage(map, path, "message", diagnostics);
            var source = map.Find("source") is { } so ? diagnostics.ReadString(so.Value, YamlTree.Join(path, "source")) : null;
            if (!steps.IsEmpty && first is not null && every is not null && source is not null) {
                rules.Add(new BossRoundStart(steps, first.Value, every.Value, window, quiet, message, source));
            }
        }

        return rules.ToImmutable();
    }

    private static PercentWindow ReadPercentWindow(YMap map, string keyPath, YamlDiagnostics diagnostics) {
        var below = map.Find("health_below_percent") is { } b ? diagnostics.ReadInt(b.Value, YamlTree.Join(keyPath, "health_below_percent"), 1, 100) : null;
        var atLeast = map.Find("health_at_least_percent") is { } a ? diagnostics.ReadInt(a.Value, YamlTree.Join(keyPath, "health_at_least_percent"), 0, 99) : null;
        if (below is not null && atLeast is not null && atLeast >= below) {
            diagnostics.At(map, keyPath, $"health_at_least_percent {atLeast} must be below health_below_percent {below}");
        }

        return new PercentWindow(below, atLeast);
    }

    private static ImmutableArray<BossCycle> ReadCycles(YNode node, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadList(node, "cycles") is not { } list) {
            return [];
        }

        var cycles = ImmutableArray.CreateBuilder<BossCycle>();
        for (var i = 0; i < list.Items.Length; i++) {
            var path = YamlTree.Index("cycles", i);
            if (diagnostics.ReadMap(list.Items[i], path) is not { } map) {
                continue;
            }

            diagnostics.CheckKeys(map, path, s_cycleKeys, ["id", "holders", "phases", "source", "source_date"]);
            var id = map.Find("id") is { } ie ? diagnostics.ReadString(ie.Value, YamlTree.Join(path, "id")) : null;
            var source = map.Find("source") is { } so ? diagnostics.ReadString(so.Value, YamlTree.Join(path, "source")) : null;
            _ = map.Find("source_date") is { } sd ? diagnostics.ReadDate(sd.Value, YamlTree.Join(path, "source_date"), false) : null;

            var holders = ImmutableArray.CreateBuilder<CycleHolder>();
            if (map.Find("holders") is { } he && diagnostics.ReadList(he.Value, YamlTree.Join(path, "holders")) is { } hl) {
                for (var j = 0; j < hl.Items.Length; j++) {
                    var hp = YamlTree.Index(YamlTree.Join(path, "holders"), j);
                    if (diagnostics.ReadMap(hl.Items[j], hp) is not { } hm) {
                        continue;
                    }

                    diagnostics.CheckKeys(hm, hp, s_holderKeys, ["template"]);
                    var template = hm.Find("template") is { } t ? diagnostics.ReadInt(t.Value, YamlTree.Join(hp, "template"), 1) : null;
                    var takeover = ReadMessage(hm, hp, "takeover_message", diagnostics);
                    if (template is null) {
                        continue;
                    }

                    if (holders.Any(h => h.Template == (uint) template)) {
                        diagnostics.At(hm, hp, $"holder {template} is listed twice");
                        continue;
                    }

                    holders.Add(new CycleHolder((uint) template.Value, takeover));
                }

                if (hl.Items.IsEmpty) {
                    diagnostics.At(hl, YamlTree.Join(path, "holders"), "a cycle needs at least one holder");
                }
            }

            var phases = ImmutableArray.CreateBuilder<CyclePhase>();
            if (map.Find("phases") is { } pe && diagnostics.ReadList(pe.Value, YamlTree.Join(path, "phases")) is { } pl) {
                for (var j = 0; j < pl.Items.Length; j++) {
                    var pp = YamlTree.Index(YamlTree.Join(path, "phases"), j);
                    if (diagnostics.ReadMap(pl.Items[j], pp) is not { } pm) {
                        continue;
                    }

                    diagnostics.CheckKeys(pm, pp, s_phaseKeys, ["id", "spell", "rounds"]);
                    var pid = pm.Find("id") is { } pi ? diagnostics.ReadString(pi.Value, YamlTree.Join(pp, "id")) : null;
                    var spell = pm.Find("spell") is { } ps ? diagnostics.ReadString(ps.Value, YamlTree.Join(pp, "spell")) : null;
                    var rounds = pm.Find("rounds") is { } pr ? diagnostics.ReadInt(pr.Value, YamlTree.Join(pp, "rounds"), 1, 20) : null;
                    var firstRounds = pm.Find("first_rounds") is { } fr ? diagnostics.ReadInt(fr.Value, YamlTree.Join(pp, "first_rounds"), 1, 20) : rounds;
                    var onEnter = pm.Find("on_enter") is { } oe ? ReadSteps(oe.Value, YamlTree.Join(pp, "on_enter"), false, false, diagnostics) : [];
                    var message = ReadMessage(pm, pp, "message", diagnostics);
                    var reactions = pm.Find("reactions") is { } re && pid is not null
                        ? ReadReactions(re.Value, YamlTree.Join(pp, "reactions"), true, pid, diagnostics) : [];
                    if (pid is null || spell is null || rounds is null || firstRounds is null) {
                        continue;
                    }

                    if (phases.Any(p => p.Id == pid)) {
                        diagnostics.At(pm, pp, $"phase '{pid}' is listed twice");
                        continue;
                    }

                    phases.Add(new CyclePhase(pid, spell, firstRounds.Value, rounds.Value, onEnter, message, reactions));
                }

                if (pl.Items.IsEmpty) {
                    diagnostics.At(pl, YamlTree.Join(path, "phases"), "a cycle needs at least one phase");
                }
            }

            if (id is not null && source is not null && holders.Count > 0 && phases.Count > 0) {
                if (cycles.Any(c => c.Id == id)) {
                    diagnostics.At(map, path, $"cycle '{id}' is listed twice");
                    continue;
                }

                cycles.Add(new BossCycle(id, holders.ToImmutable(), phases.ToImmutable(), source));
            }
        }

        return cycles.ToImmutable();
    }

    // Each holder is a boss of the file and holds one cycle; a boss's phase reaction names a phase of its own cycle.
    private static void CheckCycles(YMap root, ImmutableArray<BossCycle> cycles, IReadOnlyList<BossCheat> bosses, YamlDiagnostics diagnostics) {
        var byTemplate = bosses.ToDictionary(b => b.Template);
        var holderOf = new Dictionary<uint, BossCycle>();
        foreach (var cycle in cycles) {
            foreach (var holder in cycle.Holders) {
                if (!byTemplate.ContainsKey(holder.Template)) {
                    diagnostics.At(root, "cycles", $"cycle '{cycle.Id}': holder {holder.Template} is not a boss of this file");
                }

                if (!holderOf.TryAdd(holder.Template, cycle)) {
                    diagnostics.At(root, "cycles", $"boss {holder.Template} holds two cycles");
                }
            }
        }

        foreach (var boss in bosses) {
            foreach (var reaction in boss.Reactions.Where(r => r.When.Phase is not null)) {
                if (!holderOf.TryGetValue(boss.Template, out var cycle) || cycle.Phase(reaction.When.Phase!) is null) {
                    diagnostics.At(root, "bosses", $"{boss.Name}: a reaction names phase '{reaction.When.Phase}', which its cycle does not have");
                }
            }

            if (boss.Reactions.Any(r => r.When.Caster != CheatCasterFilter.Any) && boss.Marks is null) {
                diagnostics.At(root, "bosses", $"{boss.Name}: a reaction checks marked seats, but the boss marks none");
            }

            if (boss.Reactions.SelectMany(r => r.Do).Any(s => s.RecastPhase) && !holderOf.ContainsKey(boss.Template)) {
                diagnostics.At(root, "bosses", $"{boss.Name}: recast_phase needs the boss to hold a cycle");
            }
        }
    }

}
