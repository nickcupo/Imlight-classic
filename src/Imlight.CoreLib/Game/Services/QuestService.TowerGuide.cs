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
 * CLASSIC: the Briskbreeze Tower guide in the spellbook's Quests tab
 * (Imlight.CoreLib.Classic.TowerGuide). Sent after the held quests at each
 * login and zone load, added or removed when the unlock changes (a quest
 * starts, a goal or quest completes, a zone change), and its dialogue sent
 * when the ? button asks for it.
 *
 * USAGE EXAMPLE:
 * RefreshTowerGuide(); // after anything that may set QT-WC-GNT-C01-001
 *
 * NOTE:
 * The client's ? button sends MSG_REQUESTQUESTDIALOG for any quest; only the
 * guide answers. Other quests' dialogue review is not served (as before).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Services;

internal partial class QuestService {

    private bool _towerGuideShown;

    // A login or zone load: the client's quest list was just rebuilt from the held quests.
    private void SendTowerGuideAfterHeldQuests() {
        _towerGuideShown = false;
        RefreshTowerGuide(isNew: false);
    }

    private void RefreshTowerGuide(bool isNew = true) {
        var cheats = ClassicProgression.BossCheats;
        var show = TowerGuide.ShouldShow(GetActiveWizard(), cheats);
        if (show == _towerGuideShown) {
            return;
        }

        _towerGuideShown = show;
        if (!show) {
            SendToSocket(new QUEST_MESSAGES_52_PROTOCOL.MSG_REMOVEQUEST { QuestID = TowerGuide.QuestId });

            return;
        }

        SendToSocket(TowerGuide.Quest(cheats.Guide!, isNew));
        foreach (var goal in TowerGuide.Goals(cheats)) {
            SendToSocket(goal);
        }
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_REQUESTQUESTDIALOG))]
    private void ReceiveRequestQuestDialog(WIZARD_12_PROTOCOL.MSG_REQUESTQUESTDIALOG message) {
        if (message.QuestNameID != TowerGuide.QuestNameId || !_towerGuideShown
            || TowerGuide.DialogMessage(ClassicProgression.BossCheats) is not { } dialog) {
            return;
        }

        SendToSocket(dialog);
    }

}
