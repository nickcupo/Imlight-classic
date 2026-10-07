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
 * CLASSIC BADGES
 * ========================================================================
 *
 * PURPOSE:
 * Sends a wizard's badges when it attaches, and sets its title when the
 * client picks one of its badges (MSG_SELECT_BADGE).
 *
 * USAGE EXAMPLE:
 * Registered in GameServiceFactory; runs only when the profile names badges.
 *
 * NOTE:
 * The title is the badge's title key, stored in PlayerNameBehavior's
 * BadgeTitle (sent to every client with the wizard) and announced to the
 * zone with WIZARD MSG_NEWTITLE. Badge id 0 clears the title (No Title).
 * A badge the wizard has not earned is refused with Error 1.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Services;

internal sealed class BadgeService(SessionActor sessionActor) : MessageService(sessionActor) { // CLASSIC

    private static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new BadgeService(parentActor));

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        if (ClassicProgression.Badges is not { } rules || GetActiveWizard() is not { } wizard) {
            return;
        }

        if (WizardCollection.IsInventorySnapshotUncertain(wizard)) { CloseSession(); return; }
        var earned = ClassicBadges.Earned(rules, wizard);
        foreach (var badgeMessage in ClassicBadges.ListMessages(earned)) {
            SendToSocket(badgeMessage);
        }

        ClassicBadges.ZoneEntered(wizard, wizard.Zone, SendToSocket);
        if (WizardCollection.IsInventorySnapshotUncertain(wizard)) CloseSession();
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_SELECT_BADGE))]
    private void ReceiveSelectBadge(GAME_5_PROTOCOL.MSG_SELECT_BADGE message) {
        if (ClassicProgression.Badges is not { } rules || GetActiveWizard() is not { } wizard) {
            return;
        }

        var title = "";
        if (message.BadgeNameID != 0) {
            var badge = ClassicBadges.Earned(rules, wizard).FirstOrDefault(b => ClassicBadges.NameId(b) == message.BadgeNameID);
            if (badge is null) {
                SendToSocket(new GAME_5_PROTOCOL.MSG_SELECT_BADGE { BadgeNameID = message.BadgeNameID, Error = 1 });

                return;
            }

            title = badge.TitleKey;
        }

        wizard.SetBadgeOverride(title);
        SendToSocket(new GAME_5_PROTOCOL.MSG_SELECT_BADGE { BadgeNameID = message.BadgeNameID, Error = 0 });
        SessionActor.GetZoneActor()?.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = new WIZARD_12_PROTOCOL.MSG_NEWTITLE { GlobalID = wizard.GameObjectID, PvPIconID = 0, Title = title },
            Targets = ZoneBroadcastTarget.Players,
        });
        Logger.Debug("Wizard {CharId} chose the title {Title}.", Logger.Args(wizard.CharId, title));
    }

}
