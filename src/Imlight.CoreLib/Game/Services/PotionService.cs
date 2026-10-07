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
 * POTION SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * This service handles the potion usage mechanics in the game.
 * 
 * USAGE EXAMPLE:
 * Internal service handling various cantrip-related messages and actions 
 * within the game server's session management system.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: valiantmeraki
 * Version: KALI 1.0
 * Last Updated: 05/04/2025
 */

using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Collections;
using Imcodec.MessageLayer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Imlight.CoreLib.Game.Services;

internal class PotionService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new PotionService(parentActor));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_USEPOTION))]
    private void UsePotion(WIZARD_12_PROTOCOL.MSG_USEPOTION message)
        => ApplyDrink(GetActiveWizard(), DateTime.UtcNow, SendToSocket, CloseSession);

    internal static bool ApplyDrink(Wizard wizard, DateTime nowUtc, Action<IMessage> send, Action close) {
        try {
            if (!WizardPotionTransactions.TryDrink(wizard, nowUtc, out var receipt)) return false;
            foreach (var packet in receipt.Messages) send(packet);
            return true;
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) close();
            throw;
        }
    }

    /// <summary>CLASSIC: false while the wizard is in a duel or a seat is held for them in one.</summary>
    internal static bool MayDrinkNow(Wizard wizard, DateTime nowUtc)
        => wizard is not null && !wizard.IsInDuel && Classic.ActiveDuels.HeldFor(wizard.CharId, nowUtc) is null;

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_POTIONBUYREQUEST))]
    private void ReceivePotionBuyRequest(WIZARD_12_PROTOCOL.MSG_POTIONBUYREQUEST message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        // CLASSIC: only by the potion vendor (the request's NPC, or the shop last opened).
        if (ServiceProximity.FindNear<Zone.Components.InteractPotionShopComponent>(wizard, message.npcGlobalID, GetZoneObject) is null) {
            Logger.Warning("Wizard {0} asked for potions away from the potion shop.", Logger.Args(wizard.CharId));
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_POTIONBUYCONFIRM { Failure = 1 });

            return;
        }

        // CLASSIC: retain the existing AmountEnum interpretation until the native mapping is verified.
        ApplyBuy(wizard, message.AmountEnum != 0, SendToSocket, CloseSession);
    }

    internal static bool ApplyBuy(Wizard wizard, bool fillAll, Action<IMessage> send, Action close,
        Func<int, int> price = null) {
        try {
            if (!WizardPotionTransactions.TryBuy(wizard, fillAll, out var receipt, price)) {
                send(new WIZARD_12_PROTOCOL.MSG_POTIONBUYCONFIRM { Failure = 1 });
                return false;
            }
            foreach (var packet in receipt.Messages) send(packet);
            return true;
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) close();
            throw;
        }
    }

}
