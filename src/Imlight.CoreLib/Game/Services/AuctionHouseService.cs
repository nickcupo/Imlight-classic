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
 * AUCTION HOUSE SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages player interactions with the in-game auction house, handling 
 * item buying, selling, and inventory management.
 * 
 * USAGE EXAMPLE:
 * Internal service used within the game server's session management system.
 * Handles various auction house commands through message routing.
 * 
 * NOTE:
 * 
 * TODO:
 * - Implement proper error handling for auction house transactions
 * - Complete implementation of unhandled command cases
 * - Review and refine gold calculation logic
 * 
 * Created by: Joji
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System;
using System.Linq;
using Akka.Actor;
using Imlight.Classic;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Services;

internal class AuctionHouseService(SessionActor sessionActor) : MessageService(sessionActor) {
    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new AuctionHouseService(parentActor));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEREQUEST))]
    private void ReceiveAuctionHouseRequest(WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEREQUEST message) {
        var wizard = GetActiveWizard();
        // CLASSIC: quotes and confirmations have the same feature, attached-character, native-kind and
        // proximity checks. The native lock-sell quote (command 2) never sells anything; command 4 does.
        ClassicBazaarProtocol.Dispatch(message,
            () => Authorize(wizard, message),
            id => CoreObjectFactory.GetCoreTemplate(id),
            (BazaarNativeRequest request, out BazaarTransactionQuote quote) => ClassicBazaarTransactions.Quote(
                wizard, request.IsSell, request.Kind, request.TemplateId, request.OwnedGlobalId, request.Quantity, out quote),
            (BazaarNativeRequest request, out BazaarTransactionReceipt receipt) => request.IsSell
                ? ClassicBazaarTransactions.Sell(wizard, request.Kind, request.TemplateId, request.OwnedGlobalId, request.Quantity, out receipt)
                : ClassicBazaarTransactions.Buy(wizard, request.Kind, request.TemplateId, request.Quantity, request.Texture, request.Decal, out receipt),
            () => SendAuctionHouseContents(message.npcGlobalID, message.category),
            (request, quote) => SendToSocket(ClassicBazaarProtocol.QuoteResponse(request, quote)),
            (request, receipt) => PublishReceipt(wizard, request, receipt),
            () => SendFailure(message),
            () => ClassicBazaarTransactions.IsQuarantined(wizard),
            CloseSession);
    }

    private bool Authorize(Wizard wizard, WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEREQUEST message) {
        if (!ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.Bazaar)) {
            ClassicGate.RefuseFeature(ClassicFeatures.Bazaar, wizard?.CharId, InformGameClient);
            return false;
        }
        if (wizard is null || wizard.CharId == 0 || wizard.AccountId == 0) return false;
        if (ServiceProximity.FindNear<Zone.Components.InteractAuctionHouseComponent>(wizard, message.npcGlobalID, GetZoneObject) is null) {
            Logger.Warning("{0} sent Bazaar command {1} away from the Bazaar.", Logger.Args(wizard.CharId, message.Command));
            return false;
        }
        return !ClassicBazaarTransactions.IsQuarantined(wizard);
    }

    private void SendAuctionHouseContents(ulong npcId, uint category) {
        // CLASSIC: retain the native category/blob shape, but never advertise a forged template or a later
        // item kind. The same actual-template classification is checked again for every quote/commit.
        var entries = AuctionHouseCollection.GetAllAuctionHouseEntries()
            .Where(entry => entry.m_templateID.Full is > 0 and <= uint.MaxValue
                && entry.m_numForSale > 0 && entry.m_buyPrice > 0 && entry.m_sellPrice >= 0
                && CanListTemplate(entry.m_templateID.Full))
            .ToList();
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSECONTENTS {
            Contents = ClassicBazaarProtocol.WriteAuctionBlob(category, entries), GlobalID = npcId, LastSegment = 0,
        });
    }

    private static bool CanListTemplate(ulong templateId) {
        var template = CoreObjectFactory.GetCoreTemplate(templateId);
        return ClassicBazaar.KindOf(template) is not null
            && (template is not SpellTemplate spell || !string.IsNullOrEmpty(spell.m_name)
                && CoreObjectFactory.GetTemplatePath(templateId)?.StartsWith("Spells/TreasureCards/", StringComparison.OrdinalIgnoreCase) == true);
    }

    private void PublishReceipt(Wizard wizard, BazaarNativeRequest request, BazaarTransactionReceipt receipt) {
        // CLASSIC: this receipt is produced only after one atomic player/stock/items save was acknowledged.
        // Its serialized delivery bytes, native row identities, quantity and gold are authoritative.
        foreach (var packet in ClassicBazaarProtocol.ReceiptMessages(request, receipt, wizard.GameObjectID, wizard.CharId, request.TreasureNameHash)) {
            SendToSocket(packet);
        }
    }

    private void SendFailure(WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEREQUEST message) {
        var failure = ClassicBazaarProtocol.FailureMessage(message);
        if (failure is not null) SendToSocket(failure);
    }
}
