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
 * ALLY BRAIN
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: how an ambient wizard plays a round as a teammate. The monster
 * AI rolls aggressiveness, intelligence and selfishness every round; a
 * helper should look like a sensible 2009 player instead, so this is a
 * fixed rule list, checked in order (owner, 2026-10-01):
 *   1. Stunned: pass.
 *   2. Heal the most hurt ally below 40% health, if a heal is castable.
 *   3. With three or more enemies standing, an all-enemy spell whose
 *      expected total beats the best single hit.
 *   4. Finish a nearly dead enemy with the cheapest card that kills it
 *      (no overkill).
 *   5. Blade (self) or trap (target) once before a big hit, then hit.
 *   6. Save pips when a much stronger hit is one pip away and nothing is
 *      urgent.
 *   7. Hit the enemy the team is already hitting (then the weakest) with
 *      the best expected-damage card.
 *   8. Pass to build pips.
 * Expected damage is the average of min and max times accuracy; 2009 had
 * no critical hits or blocks. Before May 2010 an all-enemy spell still
 * took a target, so every cast names an enemy slot (AllyMove.TargetSlot).
 * Same view, same move: no randomness.
 *
 * USAGE EXAMPLE:
 * var move = AllyBrain.Choose(view);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Ambient;

/// <summary>What a card is for, as the ally brain sees it.</summary>
public enum AllyCardRole { Damage, Heal, Blade, Trap, Other }

/// <summary>A card in the ally's hand.</summary>
/// <param name="HandIndex">Its index in the hand (MSG_ACTORCOMBATMOVE's SpellSelection).</param>
/// <param name="Name">For logs.</param>
/// <param name="Pips">Pips it costs (a power pip counts two for the ally's own school).</param>
/// <param name="Accuracy">0..1.</param>
/// <param name="Role">Damage, heal, blade, trap or other.</param>
/// <param name="MinDamage">Damage cards: the low end (per target).</param>
/// <param name="MaxDamage">Damage cards: the high end (per target).</param>
/// <param name="Heal">Heal cards: health restored.</param>
/// <param name="AllEnemies">Hits every enemy.</param>
/// <param name="HealsTeam">Heals every ally.</param>
public sealed record AllyCard(int HandIndex, string Name, int Pips, double Accuracy, AllyCardRole Role,
                              int MinDamage = 0, int MaxDamage = 0, int Heal = 0, bool AllEnemies = false,
                              bool HealsTeam = false) {

    /// <summary>Expected damage to one target.</summary>
    public double Expected => (MinDamage + MaxDamage) / 2.0 * Accuracy;

}

/// <summary>A duel participant as the ally sees it.</summary>
/// <param name="Slot">The sub-circle slot (the move's target index).</param>
/// <param name="Ally">On the ally's team.</param>
/// <param name="Health">Current health.</param>
/// <param name="MaxHealth">Maximum health.</param>
/// <param name="TeamHits">How many of the ally's teammates hit this enemy last round.</param>
/// <param name="Blades">Blades on it (allies only matter for the ally itself).</param>
/// <param name="Traps">Traps on it (enemies).</param>
public sealed record AllyCombatant(int Slot, bool Ally, int Health, int MaxHealth, int TeamHits = 0, int Blades = 0,
                                   int Traps = 0) {

    public bool Alive => Health > 0;

    public double HealthFraction => MaxHealth <= 0 ? 1 : (double) Health / MaxHealth;

}

/// <summary>Everything the ally brain looks at in a planning phase.</summary>
public sealed record AllyView(int SelfSlot, int Pips, bool Stunned, IReadOnlyList<AllyCard> Hand,
                              IReadOnlyList<AllyCombatant> Combatants);

/// <summary>The ally's choice.</summary>
public enum AllyMoveKind { Pass, Cast }

/// <summary>A move: pass, or cast <see cref="HandIndex"/> at <see cref="TargetSlot"/>.</summary>
public sealed record AllyMove(AllyMoveKind Kind, int HandIndex, int TargetSlot, string Reason) {

    public static AllyMove Pass(string reason) => new(AllyMoveKind.Pass, -1, -1, reason);

}

/// <summary>
/// The ally rule set (see the file header).
/// </summary>
public static class AllyBrain {

    /// <summary>An ally below this share of its health gets healed.</summary>
    public const double HealBelow = 0.40;

    /// <summary>An enemy at or below this share of its health counts as nearly dead.</summary>
    public const double NearlyDead = 0.25;

    /// <summary>
    /// A hit is "big" when its expected damage is at least this many times the cheapest damage card's, or it costs 4+ pips.
    /// </summary>
    public const double BigHitFactor = 1.6;

    /// <summary>Chooses the move for <paramref name="view"/>.</summary>
    public static AllyMove Choose(AllyView view) {
        ArgumentNullException.ThrowIfNull(view);
        if (view.Stunned) {
            return AllyMove.Pass("stunned");
        }

        var self = view.Combatants.FirstOrDefault(c => c.Slot == view.SelfSlot);
        var enemies = view.Combatants.Where(c => !c.Ally && c.Alive).ToList();
        var allies = view.Combatants.Where(c => c.Ally && c.Alive).ToList();
        if (enemies.Count == 0) {
            return AllyMove.Pass("no enemy standing");
        }

        var castable = view.Hand.Where(card => card.Pips <= view.Pips).ToList();
        var damage = castable.Where(card => card.Role == AllyCardRole.Damage && card.Expected > 0).ToList();

        // 1. Heal the most hurt ally under the line.
        var hurt = allies.Where(a => a.HealthFraction < HealBelow).OrderBy(a => a.HealthFraction).ThenBy(a => a.Slot).FirstOrDefault();
        if (hurt is not null) {
            var heal = castable.Where(card => card.Role == AllyCardRole.Heal && card.Heal > 0)
                .OrderByDescending(card => Math.Min(card.Heal, hurt.MaxHealth - hurt.Health) * card.Accuracy)
                .ThenBy(card => card.Pips).ThenBy(card => card.HandIndex).FirstOrDefault();
            if (heal is not null) {
                return Cast(heal, heal.HealsTeam ? view.SelfSlot : hurt.Slot, $"heal slot {hurt.Slot} at {hurt.HealthFraction:P0}");
            }
        }

        var target = PickTarget(enemies);

        // 3. Many enemies: an all-enemy spell when it beats the best single hit.
        if (enemies.Count >= 3) {
            var area = damage.Where(card => card.AllEnemies)
                .OrderByDescending(card => card.Expected * enemies.Count).ThenBy(card => card.HandIndex).FirstOrDefault();
            var single = damage.Where(card => !card.AllEnemies).Select(card => card.Expected).DefaultIfEmpty(0).Max();
            if (area is not null && area.Expected * enemies.Count > single) {
                return Cast(area, target.Slot, $"{enemies.Count} enemies: all-enemy spell");
            }
        }

        // 4. Finish a nearly dead enemy without overkill.
        var finishable = enemies.Where(e => e.HealthFraction <= NearlyDead)
            .OrderByDescending(e => e.TeamHits).ThenBy(e => e.Health).ThenBy(e => e.Slot).ToList();
        foreach (var weak in finishable) {
            // A card kills when even its low roll does, or on average; the cheapest, then the least overkill, wins.
            var finisher = damage.Where(card => !card.AllEnemies && (card.MinDamage >= weak.Health || card.Expected >= weak.Health))
                .OrderBy(card => card.Pips).ThenBy(card => card.MaxDamage).ThenBy(card => card.HandIndex).FirstOrDefault();
            if (finisher is not null) {
                return Cast(finisher, weak.Slot, $"finish slot {weak.Slot} ({weak.Health} health)");
            }
        }

        var allDamage = view.Hand.Where(card => card.Role == AllyCardRole.Damage && card.Expected > 0).ToList();
        var cheapest = allDamage.Select(card => card.Expected).DefaultIfEmpty(0).Min();
        bool IsBig(AllyCard card) => card.Pips >= 4 || (cheapest > 0 && card.Expected >= cheapest * BigHitFactor);
        var bestNow = damage.Where(card => !card.AllEnemies || enemies.Count >= 2)
            .OrderByDescending(card => card.Expected).ThenBy(card => card.Pips).ThenBy(card => card.HandIndex).FirstOrDefault();

        // 5. Blade or trap once before a big hit (castable now or next round), then hit.
        var bigSoon = allDamage.Where(card => IsBig(card) && card.Pips <= view.Pips + 1)
            .OrderByDescending(card => card.Expected).FirstOrDefault();
        if (bigSoon is not null) {
            // Only when the big hit is still affordable next round (a round adds a pip), so the buff is used at once.
            bool StillAffordable(AllyCard buff) => bigSoon.Pips <= view.Pips - buff.Pips + 1;
            var blade = castable.Where(card => card.Role == AllyCardRole.Blade).OrderBy(card => card.Pips)
                .ThenBy(card => card.HandIndex).FirstOrDefault();
            if (blade is not null && (self?.Blades ?? 0) == 0 && StillAffordable(blade)) {
                return Cast(blade, view.SelfSlot, $"blade before {bigSoon.Name}");
            }

            var trap = castable.Where(card => card.Role == AllyCardRole.Trap).OrderBy(card => card.Pips)
                .ThenBy(card => card.HandIndex).FirstOrDefault();
            if (trap is not null && target.Traps == 0 && StillAffordable(trap)) {
                return Cast(trap, target.Slot, $"trap slot {target.Slot} before {bigSoon.Name}");
            }
        }

        // 6. Save pips for a much stronger hit one pip away, when nothing is urgent.
        var strongerSoon = allDamage.Where(card => card.Pips == view.Pips + 1)
            .OrderByDescending(card => card.Expected).FirstOrDefault();
        var anyAllyLow = allies.Any(a => a.HealthFraction < 0.6);
        if (strongerSoon is not null && !anyAllyLow && finishable.Count == 0
            && (bestNow is null || strongerSoon.Expected >= bestNow.Expected * 1.5)) {
            return AllyMove.Pass($"saving pips for {strongerSoon.Name}");
        }

        // 7. Hit the team's target with the best expected damage.
        if (bestNow is not null) {
            return Cast(bestNow, target.Slot, $"hit slot {target.Slot}");
        }

        // 8. Nothing useful: build pips.
        return AllyMove.Pass("nothing useful castable");
    }

    /// <summary>The enemy the team is already hitting, else the weakest; slot order breaks ties.</summary>
    public static AllyCombatant PickTarget(IReadOnlyList<AllyCombatant> enemies)
        => enemies.OrderByDescending(e => e.TeamHits).ThenBy(e => e.Health).ThenBy(e => e.Slot).First();

    private static AllyMove Cast(AllyCard card, int targetSlot, string reason)
        => new(AllyMoveKind.Cast, card.HandIndex, targetSlot, $"{reason}: {card.Name}");

}
