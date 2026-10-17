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
 */

using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Game.Services;

// CLASSIC: the client's hidden quests and finished tutorial tips, kept per character (Classic/ClientJournal.cs).
internal partial class QuestService {

    [MessageHandler(typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_UPDATEUSERHIDDENQUESTS))]
    private void ReceiveUpdateUserHiddenQuests(QUEST_MESSAGES_52_PROTOCOL.MSG_UPDATEUSERHIDDENQUESTS message) {
        var wizard = GetActiveWizard();
        var data = ClientJournalStore.Accept(message.Data);
        if (wizard is null || data is null) return;
        if (ClientJournalStore.Save(wizard.CharId, journal => journal.HiddenQuests = data)) {
            (wizard.Journal ??= new ClientJournal { CharId = wizard.CharId }).HiddenQuests = data;
        }
    }

    // A tip was finished: ask the client's TutorialLogBehavior for its whole log (it answers MSG_UPDATETUTORIALTIPLOG).
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_READTUTORIALTIP))]
    private void ReceiveReadTutorialTip(WIZARD2_53_PROTOCOL.MSG_READTUTORIALTIP message)
        => SendToSocket(new WIZARD2_53_PROTOCOL.MSG_REQUESTTUTORIALTIPLOG { GlobalID = message.GlobalID, TutorialTipCount = 0 });

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_UPDATETUTORIALTIPLOG))]
    private void ReceiveUpdateTutorialTipLog(WIZARD2_53_PROTOCOL.MSG_UPDATETUTORIALTIPLOG message) {
        var wizard = GetActiveWizard();
        var data = ClientJournalStore.Accept(message.TutorialTipData);
        if (wizard is null || data is null) return;
        if (ClientJournalStore.Save(wizard.CharId, journal => journal.TutorialTips = data)) {
            (wizard.Journal ??= new ClientJournal { CharId = wizard.CharId }).TutorialTips = data;
        }
    }

    // At login the client asks for the saved log; nothing saved yet means no answer (as before).
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTTUTORIALTIPLOG))]
    private void ReceiveRequestTutorialTipLog(WIZARD2_53_PROTOCOL.MSG_REQUESTTUTORIALTIPLOG message) {
        var wizard = GetActiveWizard();
        if (wizard is null) return;
        var tips = (wizard.Journal ??= ClientJournalStore.Load(wizard.CharId)).TutorialTips;
        if (!string.IsNullOrEmpty(tips)) {
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UPDATETUTORIALTIPLOG { GlobalID = message.GlobalID, TutorialTipData = tips });
        }
    }

}
