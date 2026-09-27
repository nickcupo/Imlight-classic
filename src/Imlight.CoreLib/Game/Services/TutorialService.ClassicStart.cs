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
 * TUTORIAL SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * CLASSIC: the classic start. A new character leaves the Golem Court
 * tutorial (finale or Skip), or starts without it, in Ambrose's office
 * with the school spell learned, the starter wand and deck equipped, and
 * GainedEnrollment set so the office doors let it out.
 * 
 * USAGE EXAMPLE:
 * CompleteClassicStart(wizard) before the tutorial's final teleport.
 * 
 * NOTE:
 * The client asks for Tutorial_Intro's OnlyGoal only after its final
 * teleport lands, outside the tutorial zones, where tutorial commands are
 * not taken; so the finale gives the school spell here. The office's exit
 * triggers require GainedEnrollment, which only the 2019 first quest set.
 * Every step is gated by ClassicStart.IsActive and runs once per character.
 * 
 * TODO:
 * - KingsIsle's tutorial script also put three copies of the school spell in the deck; the deck starts empty here.
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal sealed partial class TutorialService {

    private static string TutorialExitZone()
        => ClassicStart.IsActive ? ClassicStart.StartingZone : ConfigurationManager.Settings["Character.StartingZone"];

    private void CompleteClassicStart(Wizard wizard) {
        if (!ClassicStart.IsActive || wizard.HasRegistryValue(ClassicStart.CompletedEntry)) {
            GrantClassicEnrollment(wizard);

            return;
        }

        var introCompleted = CompleteTutorialIntro(wizard, GetActiveGameObject());
        EquipStarterWandAndDeck(wizard);
        FinishClassicStart(wizard);
        Logger.Information("Classic start for {Wizard}: Tutorial_Intro (school spell) {IntroResult}; starter wand and deck equipped.",
            Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), introCompleted ? "completed" : "not completed"));
    }

    private void FinishClassicStart(Wizard wizard) {
        if (!ClassicStart.IsActive) {
            return;
        }

        // Tutorial_Intro exists only to carry the school spell; left held, it would come back in the quest log.
        if (wizard.HasQuest(TUTORIAL_INTRO_QUEST_NAME)) {
            wizard.CompleteQuest(TUTORIAL_INTRO_QUEST_NAME);
        }

        wizard.SetRegistryValue(ClassicStart.CompletedEntry, 1);
        GrantClassicEnrollment(wizard);
    }

    private static void GrantClassicEnrollment(Wizard wizard) {
        if (!ClassicStart.IsActive || wizard.HasRegistryValue(ClassicStart.EnrollmentEntry)) {
            return;
        }

        wizard.SetRegistryValue(ClassicStart.EnrollmentEntry, 1);
        Logger.Information("Classic start: {Wizard} may now leave Ambrose's office ({Entry} set).",
            Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), ClassicStart.EnrollmentEntry));
    }

}
