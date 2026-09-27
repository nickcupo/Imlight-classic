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
 * with the school spell learned and three copies of it in the starter deck,
 * the starter wand and deck equipped, and GainedEnrollment set so the office
 * doors let it out.
 * 
 * USAGE EXAMPLE:
 * CompleteClassicStart(wizard) before the tutorial's final teleport.
 * 
 * NOTE:
 * The client asks for Tutorial_Intro's OnlyGoal only after its final
 * teleport lands, outside the tutorial zones, where tutorial commands are
 * not taken. The school spell is Tutorial_Intro's ResLearnSpell whose
 * requirements the wizard meets. The office's exit triggers require
 * GainedEnrollment, which only the 2019 first quest set. Every step is gated
 * by ClassicStart.IsActive and runs once per character. The office attach
 * carries the filled deck; without the tutorial the deck shows its cards
 * from the next zone change.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Linq;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.WizardData.Collections;
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

        if (!wizard.HasRegistryValue(ClassicStart.CompletedEntry)) {
            DeckSchoolSpell(wizard);
        }

        wizard.SetRegistryValue(ClassicStart.CompletedEntry, 1);
        GrantClassicEnrollment(wizard);
    }

    private void DeckSchoolSpell(Wizard wizard) {
        var spell = FindSchoolSpell(wizard);
        if (spell is null) {
            Logger.Warning("Classic start for {Wizard}: Tutorial_Intro gives this school no spell; the deck stays empty.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName()));

            return;
        }

        // The deck never holds a spell the book lacks; Tutorial_Intro's own ResLearnSpell may not have run yet.
        if (!wizard.SpellbookBehavior.LearnedSpellTemplateIds.Contains(spell.m_templateID) && wizard.LearnSpell(spell)) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK {
                SpellID = (int) spell.m_templateID,
            });
        }

        var deck = wizard.EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Deck);
        if (deck is null) {
            Logger.Warning("Classic start for {Wizard}: no deck is equipped, so the school spell is not put in one.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName()));

            return;
        }

        var copies = wizard.SpellbookBehavior.SpellList?.FirstOrDefault(card => card.m_templateID == spell.m_templateID)?.m_quantity ?? 0;
        while (copies < ClassicStart.SchoolSpellDeckCopies && wizard.AddSpellToDeck(spell.m_templateID, deck.m_globalID.Full)) {
            copies++;
        }

        Logger.Information("Classic start for {Wizard}: the deck holds {Copies} of school spell {Spell}.",
            Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), copies, spell.m_templateID));
    }

    private Spell FindSchoolSpell(Wizard wizard) {
        var goal = QuestTemplateCollection.GetQuestByName(TUTORIAL_INTRO_QUEST_NAME)?.m_goals?
            .FirstOrDefault(template => template.m_goalName == TUTORIAL_INTRO_GOAL_NAME);
        var playerObj = GetActiveGameObject();
        var learn = goal?.m_completeResults?.m_results?
            .OfType<ResLearnSpell>()
            .FirstOrDefault(result => RequirementDispatcher.EvaluateRequirements(result.m_requirements,
                new GenericRequirementContext(result.m_requirements, SessionActor.ActorRef, playerObj, wizard)));

        return learn is null ? null : SpellFactory.GetSpell(learn.m_templateID);
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
