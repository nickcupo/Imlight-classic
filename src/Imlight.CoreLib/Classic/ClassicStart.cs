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
 * tutorial's wand and deck, the deck holding the school spell.
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
 * TODO:
 * - Which starter wand did a 2009 wizard get? The Jan 2009 notes describe a 1-pip wand this client may not have.
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

#nullable enable

using System.Collections.Immutable;
using Imlight.Classic;

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
    /// Wand-T1-016 (87256) and Deck-Tutorial-001 (126983): the wand and deck the tutorial equips.
    /// </summary>
    internal static ImmutableArray<ulong> StarterItemTemplateIds { get; } = [87256, 126983];

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
