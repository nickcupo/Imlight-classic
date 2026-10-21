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
 * SCHOOL PRIDE (experiments.schoolflavor)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-20, owner: "find ways to further solidify school
 * identity ... a fun twist"; design-flavor report): the world side of
 * school identity, done entirely with our own ambient wizards, so the
 * unmodified r806919 client needs nothing new.
 *   - A wizard that greets a player who stopped near it knows the player's
 *     school: a same-school wizard says so ("another pyromancer!"), one of
 *     another school teases now and then ("{you}? ew jk"). The school
 *     titles are the game's own (MagicSchools_*Title) and every word is in
 *     the client's chat dictionary (checked 2026-10-20).
 *   - In Ravenwood an ambient wizard prefers the spot in front of its own
 *     school's tree (Bernie, Kelvin, Torrence, Blossom, Ivan; Death and
 *     Balance have no tree there, so their wizards keep to Malorn Ashthorn
 *     and Arthur Wethersfield), so each tree has its own little crowd.
 * Off unless the profile sets experiments.schoolflavor or the dashboard's
 * SchoolFlavor switch is on; nothing here is historical.
 *
 * USAGE EXAMPLE:
 * var pool = SchoolPride.Greetings(greeter: AmbientSchool.Fire, player: AmbientSchool.Ice, rng);
 *
 * Created by: Nick with Claude Code (claude-fable-5-1)
 * Version: KALI 1.0
 * Last Updated: 10/20/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Ambient;

/// <summary>What ambient wizards do about a player's school when <c>experiments.schoolflavor</c> is on.</summary>
public static class SchoolPride {

    /// <summary>Ravenwood, where the school trees stand.</summary>
    public static readonly string RavenwoodZone = "WizardCity/WC_Ravenwood";

    /// <summary>How often a wizard of another school teases rather than greeting plainly.</summary>
    public const double TeaseChance = 0.5;

    /// <summary>How often a Ravenwood wizard heads for its own school's tree rather than anywhere.</summary>
    public const double OwnTreeChance = 0.7;

    /// <summary>The slot for the player's school name in a template.</summary>
    public const string YouSlot = "{you}";

    /// <summary>The slot for the player's school title (pyromancer...) in a template.</summary>
    public const string TitleSlot = "{title}";

    /// <summary>The school's title as the game's MagicSchools_*Title key reads it, lower case for chat.</summary>
    public static string Title(AmbientSchool school) => school switch {
        AmbientSchool.Fire => "pyromancer", AmbientSchool.Ice => "thaumaturge", AmbientSchool.Storm => "diviner",
        AmbientSchool.Myth => "conjurer", AmbientSchool.Life => "theurgist", AmbientSchool.Death => "necromancer",
        _ => "sorcerer",
    };

    /// <summary>
    /// The Ravenwood NPC a school's wizards gather at: the school tree, or the teacher for the two schools without one.
    /// </summary>
    public static ulong TreeTemplate(AmbientSchool school) => school switch {
        AmbientSchool.Fire => 44207,    // WC_RVW_FireTree, Bernie
        AmbientSchool.Ice => 82294,     // WC_RVW_FrostTree, Kelvin
        AmbientSchool.Storm => 39178,   // WC_RVW_StormTree, Torrence
        AmbientSchool.Life => 39158,    // WC_RVW_LifeTree, Blossom
        AmbientSchool.Myth => 38275,    // WC-RVW_MythTree, Ivan
        AmbientSchool.Death => 81102,   // WC-RAV-NPC08, Malorn Ashthorn (the Death tree is in Nightside)
        _ => 81103,                     // WC-RAV-NPC09, Arthur Wethersfield (Balance has no tree)
    };

    /// <summary>Same-school greetings, by the greeter's (and the player's) school. {name} is the player.</summary>
    public static readonly Dictionary<AmbientSchool, string[]> SameSchool = new() {
        [AmbientSchool.Fire] = [
            "another pyromancer!", "fire buddies", "yes fire", "fire is the best school", "hi {name} fire rules", "a pyromancer finally",
        ],
        [AmbientSchool.Ice] = [
            "another thaumaturge!", "ice buddies", "yay ice", "ice is the best school", "hi {name} ice rules", "a thaumaturge finally",
        ],
        [AmbientSchool.Storm] = [
            "another diviner!", "storm buddies", "yes storm", "storm is the best school", "hi {name} storm rules", "a diviner finally",
        ],
        [AmbientSchool.Myth] = [
            "another conjurer!", "myth buddies", "yes myth", "myth is the best school", "hi {name} myth rules", "a conjurer finally",
        ],
        [AmbientSchool.Life] = [
            "another theurgist!", "life buddies", "yay life", "life is the best school", "hi {name} life rules", "a theurgist finally",
        ],
        [AmbientSchool.Death] = [
            "another necromancer!", "death buddies", "yes death", "death is the best school", "hi {name} death rules", "a necromancer finally",
        ],
        [AmbientSchool.Balance] = [
            "another sorcerer!", "balance buddies", "yes balance", "balance is the best school", "hi {name} balance rules", "a sorcerer finally",
        ],
    };

    /// <summary>Teasing from another school. {you} is the player's school, {title} the player's title, {school} the speaker's.</summary>
    public static readonly string[] Tease = [
        "{you}? ew jk", "{school} beats {you} any day", "{you} is ok i guess", "you should have picked {school}", "a {title} lol hi",
        "{you} lol", "hi {title}", "{school} is better than {you}", "{you}? cool i guess",
    ];

    /// <summary>Fills {you} and {title} for the player's school; the planner fills the rest ({school}, {name}).</summary>
    public static string Fill(string template, AmbientSchool player) {
        ArgumentNullException.ThrowIfNull(template);
        return template.Replace(YouSlot, AmbientChatBrain.SchoolName(player).ToLowerInvariant(), StringComparison.Ordinal)
            .Replace(TitleSlot, Title(player), StringComparison.Ordinal);
    }

    /// <summary>
    /// The greeting pool for <paramref name="greeter"/> meeting a <paramref name="player"/>: the same school's lines, or a
    /// tease with <see cref="TeaseChance"/>; null when the plain greeting should be used instead.
    /// </summary>
    public static IReadOnlyList<string>? Greetings(AmbientSchool greeter, AmbientSchool player, Random rng) {
        ArgumentNullException.ThrowIfNull(rng);
        if (greeter == player) {
            return SameSchool[greeter];
        }

        return rng.NextDouble() < TeaseChance ? Tease.Select(t => Fill(t, player)).ToArray() : null;
    }

    /// <summary>Of the free wizards near a player, the one to greet: a same-school one when there is one, else the nearest.</summary>
    public static T? PickGreeter<T>(IEnumerable<(T Wizard, AmbientSchool School, float Distance)> near, AmbientSchool player) where T : class {
        ArgumentNullException.ThrowIfNull(near);
        var sorted = near.OrderBy(n => n.Distance).ToList();
        return sorted.FirstOrDefault(n => n.School == player).Wizard ?? sorted.FirstOrDefault().Wizard;
    }

}
