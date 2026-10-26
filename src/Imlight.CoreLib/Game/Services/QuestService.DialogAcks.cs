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
 * QUEST DIALOG ACKNOWLEDGEMENTS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (client-protocol audit 2026-10): the quest-dialog messages the client sends that the server never read.
 * The server drives every quest dialog itself from INTERACTNPC (talk goals complete there, in
 * InteractPersonaGoalComponent), so the client's acknowledgements change no goal state and no client window waits for
 * an answer to them:
 *  - DECLINEQUEST: the player closed an offer without accepting. The cached offer is cleared, so no stale offer stays
 *    acceptable (ACCEPTQUEST of a name that is no longer on offer is refused) and the next talk to the NPC offers it
 *    again.
 *  - COMPLETEPERSONA, COMPLETEDIALOG, INTERACTUNDERWAYQUEST, INTERACTCOMPLETEGOAL: debug-logged no-ops. They do not
 *    complete goals (INTERACTNPC already did) and are never trusted to.
 *
 * Created by: Nick with Claude Code (claude-sonnet-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/15/2026
 */

using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Game.Services;

internal partial class QuestService {

    private const int MaxDeclinedQuestNameLength = 256;

    [MessageHandler(typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_DECLINEQUEST))]
    private void ReceiveQuestDecline(QUEST_MESSAGES_52_PROTOCOL.MSG_DECLINEQUEST message) {
        string questName = message?.QuestName;
        var cleared = ClearDeclinedOffer(_cachedQuestOffers, questName);
        Logger.Debug("Player '{0}' declined quest '{1}' (offer {2}).",
            Logger.Args(GetActiveWizard()?.CharId, questName, cleared ? "cleared" : "was not cached"));
    }

    /// <summary>CLASSIC: drops a declined quest from the offer cache; true when it was cached.</summary>
    internal static bool ClearDeclinedOffer(Imlight.Classic.Security.BoundedOffers<QuestTemplate> offers, string questName) {
        if (string.IsNullOrEmpty(questName) || questName.Length > MaxDeclinedQuestNameLength || offers.Find(questName) is null) {
            return false;
        }

        offers.Remove(questName);

        return true;
    }

    [MessageHandler(typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEPERSONA))]
    private void ReceiveClientCompletePersona(QUEST_MESSAGES_52_PROTOCOL.MSG_COMPLETEPERSONA message)
        => Logger.Debug("Player '{0}' closed a persona dialog (quest {1}, goal {2}); goals complete on INTERACTNPC, nothing to do.",
            Logger.Args(GetActiveWizard()?.CharId, message?.QuestID, message?.GoalID));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_COMPLETEDIALOG))]
    private void ReceiveClientCompleteDialog(WIZARD_12_PROTOCOL.MSG_COMPLETEDIALOG message)
        => Logger.Debug("Player '{0}' finished a dialog (type '{1}', event '{2}'); nothing to do.",
            Logger.Args(GetActiveWizard()?.CharId, (string) message?.CompletionType, (string) message?.EntryEvent));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_INTERACTUNDERWAYQUEST))]
    private void ReceiveClientInteractUnderwayQuest(WIZARD_12_PROTOCOL.MSG_INTERACTUNDERWAYQUEST message)
        => Logger.Debug("Player '{0}' asked for the dialog of underway quest {1}; INTERACTNPC drives dialogs, nothing to do.",
            Logger.Args(GetActiveWizard()?.CharId, message?.QuestID));

    // CLASSIC: MSG_INTERACTCOMPLETEGOAL (r806919's AvailableCompleteEntry) is handled by InteractService, which routes it
    // to the NPC like the option's click (DialogChain).

}
