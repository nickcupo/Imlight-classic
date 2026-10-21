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
 * CLASSIC: the duel's School Mastery director (experiments.mastery). Off unless the profile turns it on.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/21/2026
 */

using Imlight.CoreLib.Game.Combat;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class CombatDuelComponent {

    private MasteryDirector _mastery;

    /// <summary>CLASSIC: this fight's School Mastery procs.</summary>
    internal MasteryDirector Mastery => _mastery ??= new MasteryDirector(this);

    /// <summary>
    /// CLASSIC: a new round of the fight; the first round starts a new director, since the sigil's component outlives
    /// each fight.
    /// </summary>
    private void BeginMasteryRound(int round) {
        if (!MasteryDirector.Active) {
            return;
        }

        if (round <= 1) {
            _mastery = null;
        }

        Mastery.NewRound(round);
    }

}
