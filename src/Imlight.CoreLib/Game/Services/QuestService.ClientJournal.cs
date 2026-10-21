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

// CLASSIC: the client's hidden quests, kept per character (Classic/ClientJournal.cs). Tutorial tips: TutorialTipService.
internal partial class QuestService {

    [MessageHandler(typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_UPDATEUSERHIDDENQUESTS))]
    private void ReceiveUpdateUserHiddenQuests(QUEST_MESSAGES_52_PROTOCOL.MSG_UPDATEUSERHIDDENQUESTS message) {
        var wizard = GetActiveWizard();
        var data = ClientJournalStore.Accept((byte[]) message.Data); // the bytes as sent: binary, never a string
        if (wizard is null || data is null) return;
        if (ClientJournalStore.Save(wizard.CharId, journal => journal.HiddenQuestsData = data)) {
            (wizard.Journal ??= new ClientJournal { CharId = wizard.CharId }).HiddenQuestsData = data;
        }
    }

}
