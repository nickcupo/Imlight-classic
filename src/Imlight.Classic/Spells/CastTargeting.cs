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
 * CLASSIC SPELL VALUES
 * ========================================================================
 *
 * PURPOSE:
 * Picks the target a cast lands on when the client's choice does not fit the
 * card. The official client asks for a target when one of the card's
 * effects is single-target (kEnemySingle, kFriendlySingle), read from its
 * own copy of the template; without the classic overlay installed it still
 * holds the modern card, so it can cast a card the classic values made
 * single-target (Orthrus) with no target at all.
 *
 * USAGE EXAMPLE:
 * var side = CastTargeting.SideOf(effectTargetNames);
 * var slot = CastTargeting.Choose(side, caster, chosen, circles);
 *
 * NOTE:
 * The client sends a sigil slot, or an out-of-range value for a card that
 * takes no target; the server then treats the caster as the target.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Spells;

/// <summary>
/// Whom a card's single target must be.
/// </summary>
public enum CastTargetSide {

    /// <summary>
    /// The card takes no single target (area, self, global).
    /// </summary>
    None,

    Enemy,
    Friend,
}

/// <summary>
/// One sigil slot as target selection sees it.
/// </summary>
/// <param name="Slot">The slot index.</param>
/// <param name="Team">The team number.</param>
/// <param name="CanBeTargeted">True when the slot holds a living combatant that is in the duel.</param>
public readonly record struct CastCircle(int Slot, int Team, bool CanBeTargeted);

/// <summary>
/// Chooses a cast's target from the card's own effects.
/// </summary>
public static class CastTargeting {

    /// <summary>
    /// The side of the card's first single-target effect, in effect order (a roll's or per-pip list's children included).
    /// </summary>
    /// <param name="effectTargets">The kEffectTarget member names of the card's effects, in order.</param>
    /// <returns>The side, or <see cref="CastTargetSide.None"/> when no effect takes a single target.</returns>
    public static CastTargetSide SideOf(IEnumerable<string> effectTargets) {
        foreach (var target in effectTargets) {
            switch (target) {
                case "kEnemySingle":
                    return CastTargetSide.Enemy;
                case "kFriendlySingle":
                case "kFriendlySingleNotMe":
                    return CastTargetSide.Friend;
            }
        }

        return CastTargetSide.None;
    }

    /// <summary>
    /// The slot the cast lands on.
    /// </summary>
    /// <param name="side">The card's side (<see cref="SideOf"/>).</param>
    /// <param name="caster">The caster.</param>
    /// <param name="chosen">The client's choice; the caster itself when the client sent none.</param>
    /// <param name="circles">Every occupied slot.</param>
    /// <returns>
    /// <paramref name="chosen"/> when it fits the card; else the first targetable enemy by slot for an enemy card
    /// (<paramref name="chosen"/> when there is none), or the caster for a friendly card.
    /// </returns>
    public static int Choose(CastTargetSide side, CastCircle caster, CastCircle chosen, IReadOnlyList<CastCircle> circles) {
        switch (side) {
            case CastTargetSide.Enemy when chosen.Team == caster.Team || !chosen.CanBeTargeted:
                return circles.Where(circle => circle.Team != caster.Team && circle.CanBeTargeted)
                    .OrderBy(circle => circle.Slot)
                    .Select(circle => (int?) circle.Slot)
                    .FirstOrDefault() ?? chosen.Slot;
            case CastTargetSide.Friend when chosen.Team != caster.Team:
                return caster.Slot;
            default:
                return chosen.Slot;
        }
    }

}
