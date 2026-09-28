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
 * CLASSIC START
 * ========================================================================
 * 
 * PURPOSE:
 * Where a new character starts and what it carries when the profile sets
 * rules.tutorial: unicorn-way-classic: Ambrose's office, with only the
 * school's starter wand and the tutorial deck, the deck holding the school spell.
 * 
 * USAGE EXAMPLE:
 * var zone = ClassicStart.IsActive ? ClassicStart.StartingZone : ConfigurationManager.Settings["Character.StartingZone"];
 * 
 * NOTE:
 * Under the classic start these replace Character.StartingZone and
 * Character.DefaultItems, and the Black Cat pet is not granted. The office's
 * two exit triggers ("Trigger Teleport Outside" and "Trigger Teleport")
 * require the player registry entry GainedEnrollment.
 * 
 * Each school starts with its own Tier 1 wand, which gives one item card of
 * each other school's first spell (Wizard101 wiki: Antiquated_Wand oldid 9135,
 * 2009-03-05, "the Wand that all wizards start with", the missing card
 * depending on the school; Tutorial oldid 62614, 2010-02-17, "Wand according
 * to School"). The kit is given once: CL_StarterKitGiven marks it, because
 * the classic start equips the wand and deck and leaves the backpack empty.
 *
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

#nullable enable

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using Imlight.Classic;
using Imlight.CoreLib.Shared.Behaviors;

namespace Imlight.CoreLib.Classic;

internal static class ClassicStart {

    /// <summary>
    /// Merle Ambrose's House, where the tutorial ends and Unicorn's Folly is offered.
    /// </summary>
    internal const string StartingZone = "WizardCity/Interiors/WC_Headmistress_House";

    /// <summary>
    /// The player registry entry the office exits require.
    /// </summary>
    internal const string EnrollmentEntry = "GainedEnrollment";

    /// <summary>
    /// Set once the school spell and the starter wand and deck have been given.
    /// </summary>
    internal const string CompletedEntry = "CL_ClassicStartDone";

    /// <summary>
    /// Set once the starter kit has been given, so a wizard whose kit is all equipped is not given it again.
    /// </summary>
    internal const string StarterKitGivenEntry = "CL_StarterKitGiven";

    /// <summary>
    /// Deck-Tutorial-001: the starter deck every school gets.
    /// </summary>
    internal const ulong StarterDeckTemplateId = 126983;

    /// <summary>
    /// Each school's starting wand: the Tier 1 wand whose six item cards are the other schools' first spells.
    /// </summary>
    /// <remarks>
    /// Wizard101 wiki revisions before 2010-05-26: Symmetrical_Wand oldid 23422 (2009-06-26, reward of
    /// "Enrollment - Balance School"), Wand_of_Repose oldid 38843 (2009-08-08, "Starting a student for the
    /// School of Death"), Antiquated_Wand oldid 58707 (2010-01-23, "the Conjurer's (Myth wizards) starting
    /// wand"), Fairy's_Wand oldid 58719 (2010-01-24, "the Theurge's (Life wizard's) starting wand"), Tutorial
    /// oldid 62614 (2010-02-17, Storm: Charged Wand) and Thunder_Snake_Item_Card oldid 12231 (2009-04-04, lists
    /// Branded Wand among the wands giving that card). Fire (Branded Wand) and Ice (Insulated Wand) have no
    /// dated page of their own; they are the r806919 templates that follow the same pattern (the school's own
    /// first spell left out, <c>m_school</c> set to the school).
    /// </remarks>
    internal static FrozenDictionary<MagicSchool, ulong> StarterWandTemplateIds { get; } = new Dictionary<MagicSchool, ulong> {
        [MagicSchool.Balance] = 87241, // Wand-T1-001, Symmetrical Wand
        [MagicSchool.Death]   = 87244, // Wand-T1-004, Wand of Repose
        [MagicSchool.Fire]    = 87247, // Wand-T1-007, Branded Wand
        [MagicSchool.Ice]     = 87250, // Wand-T1-010, Insulated Wand
        [MagicSchool.Life]    = 87253, // Wand-T1-013, Fairy's Wand
        [MagicSchool.Myth]    = 87256, // Wand-T1-016, Antiquated Wand
        [MagicSchool.Storm]   = 87259, // Wand-T1-019, Charged Wand
    }.ToFrozenDictionary();

    /// <summary>
    /// The starter kit of a wizard of <paramref name="school"/>: its school's wand and the starter deck.
    /// </summary>
    /// <param name="school">The wizard's school.</param>
    /// <returns>The template ids; the Myth wand for a school without a starter wand.</returns>
    internal static ImmutableArray<ulong> StarterItemTemplateIds(MagicSchool school)
        => [StarterWandTemplateIds.GetValueOrDefault(school, StarterWandTemplateIds[MagicSchool.Myth]), StarterDeckTemplateId];

    /// <summary>
    /// Copies of the school spell the starter deck holds when the classic start ends.
    /// </summary>
    internal const int SchoolSpellDeckCopies = 3;

    /// <summary>
    /// True when the active profile gives new characters the classic start.
    /// </summary>
    internal static bool IsActive
        => ClassicRuntime.IsInitialized && ClassicRuntime.Rules.UsesClassicStart;

}
