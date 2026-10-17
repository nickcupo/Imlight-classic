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
 * TUTORIAL TIP SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-17): keeps the tutorial tips a wizard has read. The r806919
 * client asks for its log at every login (MSG_REQUESTTUTORIALTIPLOG, live:
 * every session) and reports each tip it shows (MSG_READTUTORIALTIP); both
 * went unanswered, so every read tip came back after a relog.
 *
 * NOTE:
 * The reply is MSG_UPDATETUTORIALTIPLOG with the log in the client's own
 * layout (Imlight.Classic.Net.TutorialTipLog); the client loads it into its
 * TutorialLogBehavior (handler 0x1413048d0). A log the client sends back
 * replaces ours.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/17/2026
 */

using System;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic.Net;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Services;

internal sealed class TutorialTipService(SessionActor sessionActor) : MessageService(sessionActor) { // CLASSIC

    private List<uint> _read;

    private static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new TutorialTipService(parentActor));

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTTUTORIALTIPLOG))]
    private void ReceiveRequestLog(WIZARD2_53_PROTOCOL.MSG_REQUESTTUTORIALTIPLOG message) {
        if (Read() is not { } read) {
            return;
        }

        SendLog(read);
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_READTUTORIALTIP))]
    private void ReceiveReadTip(WIZARD2_53_PROTOCOL.MSG_READTUTORIALTIP message) {
        if (message.TutorialTipID == 0 || Read() is not { } read || read.Contains(message.TutorialTipID)
                || read.Count >= TutorialTipLog.MaxTips) {
            return;
        }

        read.Add(message.TutorialTipID);
        Save(read);
        SendLog(read);
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_UPDATETUTORIALTIPLOG))]
    private void ReceiveUpdateLog(WIZARD2_53_PROTOCOL.MSG_UPDATETUTORIALTIPLOG message) {
        if (Read() is null || !TutorialTipLog.TryDecode((byte[]) message.TutorialTipData, out var tips)) {
            return;
        }

        _read = tips;
        Save(tips);
    }

    // The wizard's read tips, loaded once per session; null without a wizard.
    private List<uint> Read() {
        if (_read is not null) {
            return _read;
        }

        if (GetActiveWizard() is not { } wizard) {
            return null;
        }

        try {
            _read = TutorialTipCollection.Load(wizard.CharId);
        }
        catch (Exception ex) {
            Logger.Warning("Tutorial tips: could not load the log of {0}: {1}", Logger.Args(wizard.CharId, ex.Message));
            _read = [];
        }

        return _read;
    }

    private void Save(List<uint> read) {
        if (GetActiveWizard() is not { } wizard) {
            return;
        }

        try {
            TutorialTipCollection.Save(wizard.CharId, read);
        }
        catch (Exception ex) {
            Logger.Warning("Tutorial tips: could not save the log of {0}: {1}", Logger.Args(wizard.CharId, ex.Message));
        }
    }

    private void SendLog(List<uint> read) {
        if (GetActiveWizard() is not { } wizard) {
            return;
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UPDATETUTORIALTIPLOG {
            GlobalID = wizard.GameObjectID,
            TutorialTipData = TutorialTipLog.Encode(read),
        });
    }

}
