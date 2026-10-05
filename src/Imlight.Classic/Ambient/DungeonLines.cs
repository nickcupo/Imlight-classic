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
 * DUNGEON LINES
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): what an ambient wizard says around a dungeon run,
 * in the short lower-case chat of 2009 (and only words the 2009 chat
 * filter let through): stepping on a sigil with a player, being told no,
 * giving a real player its place, leaving after the run, and leaving
 * because it got stuck or lost. Kept apart from AmbientLines (the street
 * lines) so the two can change on their own. The pick is by seed, so a
 * wizard does not repeat itself in one run.
 *
 * USAGE EXAMPLE:
 * AmbientChat.Say(wizard, DungeonLines.Pick(DungeonLines.Join, wizard.Turn++));
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System.Collections.Immutable;

namespace Imlight.Classic.Ambient;

/// <summary>Dungeon chat lines (see the file header).</summary>
public static class DungeonLines {

    /// <summary>Stepping on the sigil with a player (the player may still say no).</summary>
    public static readonly ImmutableArray<string> Join = [
        "mind if i come?", "can i join?", "ill help!", "need a hand in there?", "can i come with u?", "i'll come too",
    ];

    /// <summary>The player said no: steps off.</summary>
    public static readonly ImmutableArray<string> Declined = ["ok np", "oh ok, good luck!", "np, have fun", "kk gl"];

    /// <summary>A real player took its place on the sigil, or a friend of the group came in.</summary>
    public static readonly ImmutableArray<string> MakeRoom = ["oh ur full, np", "ill let ur friend come", "np, gl guys"];

    /// <summary>The run is over (the leader left the dungeon).</summary>
    public static readonly ImmutableArray<string> Thanks = ["ty for the group!", "thx for the group", "ty! that was fun", "gg ty"];

    /// <summary>Leaving early: stuck, lost, or out of time.</summary>
    public static readonly ImmutableArray<string> Leave = ["gtg sorry", "sorry gtg, gl!", "brb... actually gtg", "i have to go, gl!"];

    /// <summary>Defeated in the dungeon: off to the commons, like a player.</summary>
    public static readonly ImmutableArray<string> Defeated = ["oops lol", "aw i died", "sorry guys"];

    /// <summary>One of <paramref name="lines"/> by <paramref name="seed"/>.</summary>
    public static string Pick(ImmutableArray<string> lines, int seed) => lines[(int) ((uint) seed % (uint) lines.Length)];

}
