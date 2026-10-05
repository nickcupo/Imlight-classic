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
 * AMBIENT DECK PLANNER
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): a 2009 player's deck for an ambient wizard's
 * school and level, composed the way the playbot's kit planner
 * (playbot-reports/bc/kit.py, used for the dungeon playthroughs) builds
 * one: a deck of DeckSize(level) cards; about a tenth blades, a tenth
 * traps and a tenth heals (Life a sixth), the rest attacks split cheap
 * (1-2 pips) 30%, middle (3-4) 45% and big (5+) 25%, strongest first; at
 * most six copies of one school spell and three of another school's (the
 * planner's limits for a deck without its own). Only
 * the cards AllyBrain plays (attacks, heals, blades, traps): a shield or
 * a minion the brain never casts would only clog the hand. Before, every
 * trained spell went in twice, so a level 30 drew its level 1 bolt as
 * often as its best hit.
 *
 * USAGE EXAMPLE:
 * var deck = AmbientDeckPlanner.Plan(records, "fire", 22);   // (record, copies)
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Spells;

namespace Imlight.Classic.Ambient;

/// <summary>Builds an ambient wizard's deck (see the file header).</summary>
public static class AmbientDeckPlanner {

    /// <summary>The most copies of one card of the wizard's own school.</summary>
    public const int SchoolCopies = 6;

    /// <summary>The most copies of one card of another school.</summary>
    public const int OtherCopies = 3;

    /// <summary>Cards in the deck at <paramref name="level"/>: the vendor deck a player of that level carried.</summary>
    public static int DeckSize(int level) => level switch {
        < 5 => 14,
        < 10 => 18,
        < 20 => 24,
        < 30 => 30,
        < 40 => 36,
        _ => 40,
    };

    private enum Role { Attack, Heal, Blade, Trap }

    private sealed record Card(ClassicSpellRecord Record, Role Role, int Pips, double Strength);

    /// <summary>
    /// The deck for a wizard of <paramref name="school"/> at <paramref name="level"/> from the spells it knows
    /// (<paramref name="known"/>: its school's trained spells up to its level, and any others it learned), as
    /// (spell, copies). Empty when it knows no card the ally brain plays.
    /// </summary>
    public static IReadOnlyList<(ClassicSpellRecord Spell, int Copies)> Plan(IEnumerable<ClassicSpellRecord> known, string school,
                                                                            int level) {
        var cards = known.Select(Classify).OfType<Card>().DistinctBy(c => c.Record.Id).ToList();
        var size = DeckSize(level);
        var counts = new Dictionary<string, int>();
        var order = new List<Card>();

        int Put(Card card, int wanted) {
            var cap = string.Equals(card.Record.School, school, StringComparison.OrdinalIgnoreCase) ? SchoolCopies : OtherCopies;
            var have = counts.GetValueOrDefault(card.Record.Id);
            var n = Math.Min(wanted, Math.Min(cap - have, size - counts.Values.Sum()));
            if (n <= 0) {
                return 0;
            }

            if (have == 0) {
                order.Add(card);
            }

            counts[card.Record.Id] = have + n;
            return n;
        }

        void Fill(IEnumerable<Card> pool, int wanted) {
            foreach (var card in pool) {
                if (wanted <= 0) {
                    return;
                }

                wanted -= Put(card, wanted);
            }
        }

        var life = string.Equals(school, "life", StringComparison.OrdinalIgnoreCase);
        var tenth = Math.Max(2, (int) Math.Round(size * 0.10));
        Fill(cards.Where(c => c.Role == Role.Blade).OrderByDescending(c => c.Strength), tenth);
        Fill(cards.Where(c => c.Role == Role.Trap).OrderByDescending(c => c.Strength), tenth);
        Fill(cards.Where(c => c.Role == Role.Heal).OrderByDescending(c => c.Strength),
            life ? (int) Math.Round(size / 6.0) : Math.Max(1, (int) Math.Round(size * 0.10)));

        var attacks = cards.Where(c => c.Role == Role.Attack).ToList();
        var free = size - counts.Values.Sum();
        var tiers = new (Func<Card, bool> In, double Share)[] {
            (c => c.Pips <= 2, attacks.Any(c => c.Pips >= 5) ? 0.30 : 0.35),
            (c => c.Pips is 3 or 4, attacks.Any(c => c.Pips >= 5) ? 0.45 : 0.65),
            (c => c.Pips >= 5, attacks.Any(c => c.Pips >= 5) ? 0.25 : 0),
        };
        foreach (var (inTier, share) in tiers) {
            Fill(attacks.Where(inTier).OrderByDescending(c => c.Strength), (int) Math.Round(free * share));
        }

        // Whatever is left: the strongest attacks, then anything playable.
        while (counts.Values.Sum() < size) {
            var before = counts.Values.Sum();
            Fill(attacks.OrderByDescending(c => c.Strength), size - before);
            Fill(cards.OrderByDescending(c => c.Strength), size - counts.Values.Sum());
            if (counts.Values.Sum() == before) {
                break;
            }
        }

        return [.. order.Select(c => (c.Record, counts[c.Record.Id]))];
    }

    private static Card? Classify(ClassicSpellRecord record) {
        if (record.Values.Pips.Fixed is not { } pips) {
            return null; // an X spell: the ally brain cannot cost it
        }

        var effects = record.Values.Effects;
        var hits = effects.Where(e => e.Kind is SpellEffectKind.Damage or SpellEffectKind.Dot or SpellEffectKind.Steal && e.HasAmount)
            .ToList();
        if (hits.Count > 0) {
            var each = hits.Sum(e => (e.Min!.Value + e.Max!.Value) / 2.0) * record.Values.Accuracy;
            var all = hits.Any(e => e.Targets == SpellTargets.AllEnemies);
            return new Card(record, Role.Attack, pips, all ? each * 1.5 : each);
        }

        if (effects.FirstOrDefault(e => e.Kind is SpellEffectKind.Heal or SpellEffectKind.Hot && e.HasAmount) is { } heal) {
            return new Card(record, Role.Heal, pips, (heal.Min!.Value + heal.Max!.Value) / 2.0);
        }

        if (effects.FirstOrDefault(e => e.Kind == SpellEffectKind.Blade && (e.Percent ?? 0) > 0) is { } blade) {
            return new Card(record, Role.Blade, pips, blade.Percent!.Value);
        }

        if (effects.FirstOrDefault(e => e.Kind == SpellEffectKind.Trap && (e.Percent ?? 0) > 0) is { } trap) {
            return new Card(record, Role.Trap, pips, trap.Percent!.Value);
        }

        return null;
    }

}
