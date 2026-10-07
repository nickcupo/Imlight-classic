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

using System;
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

    // CLASSIC: both native exit callbacks must pass saved starter finalization before emitting a teleport.
    internal static bool CompleteStarterExit(Func<bool> finish, System.Action teleport) {
        if (!finish()) return false;
        teleport();
        return true;
    }

    private bool CompleteClassicStart(Wizard wizard) {
        if (!ClassicStart.IsActive || wizard.HasRegistryValue(ClassicStart.CompletedEntry)) {
            GrantClassicEnrollment(wizard);

            return true;
        }

        var introCompleted = CompleteTutorialIntro(wizard, GetActiveGameObject());
        EquipStarterWandAndDeck(wizard);
        if (!FinishClassicStart(wizard)) return false;
        Logger.Information("Classic start for {Wizard}: Tutorial_Intro (school spell) {IntroResult}; starter wand and deck equipped.",
            Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), introCompleted ? "completed" : "not completed"));
        return true;
    }

    private bool FinishClassicStart(Wizard wizard) {
        if (!ClassicStart.IsActive) {
            return true;
        }
        // CLASSIC: completion is permanent; never refill a deck the player has since edited.
        if (wizard.HasRegistryValue(ClassicStart.CompletedEntry)) {
            GrantClassicEnrollment(wizard);
            return true;
        }

        // Tutorial_Intro exists only to carry the school spell; left held, it would come back in the quest log.
        if (wizard.HasQuest(TUTORIAL_INTRO_QUEST_NAME)) {
            wizard.CompleteQuest(TUTORIAL_INTRO_QUEST_NAME);
        }

        return DeckSchoolSpell(wizard);
    }

    private bool DeckSchoolSpell(Wizard wizard) {
        var spell = FindSchoolSpell(wizard);
        if (spell is null) {
            Logger.Warning("Classic start for {Wizard}: Tutorial_Intro gives this school no spell; the deck stays empty.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName()));

            return false;
        }

        // The deck never holds a spell the book lacks; Tutorial_Intro's own ResLearnSpell may not have run yet.
        if (!wizard.SpellbookBehavior.LearnedSpellTemplateIds.Contains(spell.m_templateID) && wizard.LearnSpell(spell)) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK {
                SpellID = (int) spell.m_templateID,
            });
        }

        return CompleteSavedStarterDeck(wizard, spell.m_templateID);
    }

    // CLASSIC: native tutorial completion requires three saved school cards, not a successful live-only fill.
    // Read the saved count before and after the fill; failed writes leave the finale pending and never grant access.
    internal static bool CompleteSavedStarterDeck(Wizard wizard, uint spellTemplateId,
        Func<ulong, uint, int?> savedCount = null, Func<uint, ulong, bool> addSpell = null,
        Func<string, ulong, bool> setRegistry = null) {
        if (wizard.HasRegistryValue(ClassicStart.CompletedEntry)) return true;
        if (!wizard.SpellbookBehavior.LearnedSpellTemplateIds.Contains(spellTemplateId)) return false;

        var deck = wizard.EquipmentBehavior.GetItemInSlot(EquipmentSlotType.Deck);
        if (deck is null) {
            Logger.Warning("Classic start for {Wizard}: no deck is equipped, so the school spell is not put in one.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName()));

            return false;
        }

        savedCount ??= (id, spell) => WizardItemCollection.SavedDeckSpellCount(id, spell);
        addSpell ??= wizard.AddSpellToDeck;
        setRegistry ??= wizard.SetRegistryValue;
        var copies = savedCount(deck.m_globalID.Full, spellTemplateId);
        if (copies is null) {
            Logger.Error("Classic start for wizard {0}: saved starter deck {1} could not be loaded.",
                Logger.Args(wizard.CharId, deck.m_globalID.Full));
            return false;
        }

        while (copies < ClassicStart.SchoolSpellDeckCopies) {
            if (!addSpell(spellTemplateId, deck.m_globalID.Full)) {
                Logger.Warning("Classic start for wizard {0}: starter deck write refused; completion remains pending.",
                    Logger.Args(wizard.CharId));
                return false;
            }
            copies++;
        }

        if (savedCount(deck.m_globalID.Full, spellTemplateId) != ClassicStart.SchoolSpellDeckCopies) {
            Logger.Error("Classic start for wizard {0}: three saved starter cards were not confirmed; completion remains pending.",
                Logger.Args(wizard.CharId));
            return false;
        }

        if (!setRegistry(ClassicStart.CompletedEntry, 1) || !setRegistry(ClassicStart.EnrollmentEntry, 1)) return false;
        Logger.Information("Classic start for {Wizard}: the saved deck holds {Copies} of school spell {Spell}.",
            Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), ClassicStart.SchoolSpellDeckCopies, spellTemplateId));
        return true;
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

        // CLASSIC: legacy wizards retain the old enrollment repair; a newly granted kit must finish its saved deck.
        if (wizard.HasRegistryValue(ClassicStart.StarterKitGivenEntry) && !wizard.HasRegistryValue(ClassicStart.CompletedEntry)) {
            return;
        }

        wizard.SetRegistryValue(ClassicStart.EnrollmentEntry, 1);
        Logger.Information("Classic start: {Wizard} may now leave Ambrose's office ({Entry} set).",
            Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), ClassicStart.EnrollmentEntry));
    }

}
