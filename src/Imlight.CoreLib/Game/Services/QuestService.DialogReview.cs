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
 * QUEST SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the ? ("More Info") button on a quest card in the spellbook's
 * Quests tab. The client sends MSG_REQUESTQUESTDIALOG with the quest's name
 * id; the answer (MSG_QUESTDIALOG) is the dialogue the wizard has had for
 * that quest (Imlight.CoreLib.Classic.QuestDialogReview). The Briskbreeze
 * Tower guide answers for its own entry (QuestService.TowerGuide.cs).
 *
 * USAGE EXAMPLE:
 * Client-triggered; no server code calls it.
 *
 * NOTE:
 * Only quests the wizard holds are answered; any other id (a quest they do
 * not have, finished, or unknown) gets no answer, so the request reveals
 * nothing. Read only. A per-session token bucket drops floods.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Services;

internal partial class QuestService {

    // A player clicks ? a few times in a row at most; a flood beyond that is dropped.
    private readonly Imlight.Classic.Security.TokenBucket _questDialogRequests = QuestDialogReview.NewRequestLimiter();

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_REQUESTQUESTDIALOG))]
    private void ReceiveRequestQuestDialog(WIZARD_12_PROTOCOL.MSG_REQUESTQUESTDIALOG message) {
        if (message is null || !_questDialogRequests.TryTake(DateTimeOffset.UtcNow)) {
            return;
        }

        if (TryAnswerTowerGuideDialog(message.QuestNameID)) {
            return;
        }

        var wizard = GetActiveWizard();
        if (QuestDialogReview.HeldQuest(wizard, message.QuestNameID) is not { } instance
            || QuestTemplateCollection.GetQuestByName(instance.QuestName) is not { } template) {
            return;
        }

        var playerObj = GetActiveGameObject();
        var answer = QuestDialogReview.Message(template, instance, requirements => RequirementDispatcher.EvaluateRequirements(
            requirements, new QuestRequirementContext(requirements, null, playerObj, wizard, instance.QuestName)));
        if (answer is null) {
            Logger.Error("Quest dialogue review of '{0}' for {1} did not serialize.", Logger.Args(instance.QuestName, wizard.CharId));

            return;
        }

        SendToSocket(answer);
    }

}
