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
 * BANK SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the dorm bank and the account's shared bank (100 slots each, 2009). The client's BankingWindow
 * (r806919) speaks this protocol, read from the client:
 * - open: MSG_OPENBANK(GlobalID of the chest) builds the window over the player's ClientWizStorageBehavior. Its
 *   sizes come with the player object; its bank list (m_itemList) is AuthorityTransmit only and its shared list is
 *   runtime-only, so both are filled here with MSG_STORAGECLIENTADD (SharedBank 0/1) before the window opens. An
 *   add replaces an item with the same id, so sending them at every opening is safe.
 * - backpack -> bank: MSG_MOVEINVTOBANK(ClientRequestID, GlobalID, UseShared, Quantity);
 *   bank -> backpack: MSG_MOVEBANKTOINV(ClientRequestID, GlobalID, UseShared, Quantity);
 *   bank <-> shared bank: MSG_MOVEBANKTOBANK, BankID set for bank -> shared, BankSharedID for shared -> bank.
 * - the confirms (MSG_INVTOBANKCONFIRM, MSG_BANKTOINVCONFIRM, MSG_BANKTOBANKCONFIRM) only redraw the window: it
 *   looks the item up by GlobalID in the destination list, so the server first moves it there with
 *   MSG_STORAGECLIENTADD / MSG_INVENTORYBEHAVIOR_ADDITEM and out of the source with MSG_STORAGECLIENTREMOVE /
 *   MSG_INVENTORYBEHAVIOR_REMOVEITEM. A nonzero Failure / Error shows the client's "transaction was not
 *   successful" box; a chat line says why.
 * - closing sends MSG_DONESHOPPING (ShopService lets the wizard walk again).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System.Collections.Concurrent;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Inventory;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class BankService(SessionActor sessionActor) : MessageService(sessionActor) {

    // The client reads these items with flags 0x18 (ClientWizStorageBehavior::MSG_StorageClientAdd), as the backpack's.
    private const uint ITEM_SERIALIZATION_FLAGS = 24;

    // The chest each wizard has open, and the zone it is in; a move needs an open bank in the wizard's zone.
    private static readonly ConcurrentDictionary<ulong, string> s_openBanks = new();

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new BankService(parentActor));

    private ulong _openedFor;

    // The session ends (logout, disconnect): the chest closes with it.
    protected override void OnDispose() {
        if (_openedFor != 0) {
            s_openBanks.TryRemove(_openedFor, out _);
        }

        base.OnDispose();
    }

    /// <summary>True when the wizard opened the bank in the zone they are in.</summary>
    internal static bool IsOpen(Wizard wizard)
        => wizard is not null && s_openBanks.TryGetValue(wizard.CharId, out var zone) && zone == wizard.Zone;

    [MessageHandler(typeof(CLASSIC_FEATURES_PROTOCOL.MSG_BANKOPEN))]
    private void ReceiveBankOpen(CLASSIC_FEATURES_PROTOCOL.MSG_BANKOPEN message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        s_openBanks[wizard.CharId] = message.Zone ?? wizard.Zone;
        _openedFor = wizard.CharId;
        wizard.StorageBehavior ??= new();

        // The bank came with the player object; it is sent again in case the object predates a change.
        foreach (var item in wizard.StorageBehavior.Items) {
            SendStorageAdd(wizard, item, BankPlace.Bank);
        }

        var shared = BankCollection.LoadShared(wizard.AccountId);
        foreach (var item in shared) {
            SendStorageAdd(wizard, item, BankPlace.SharedBank);
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UPDATEBANKLIMIT { BankLimit = Shared.Behaviors.ServerWizStorageBehavior.BankLimit });
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_OPENBANK { GlobalID = message.BankObjectId });
        Logger.Debug("Bank opened for {0}: {1} in the bank, {2} in the shared bank.",
            Logger.Args(wizard.CharId, wizard.StorageBehavior.Items.Count, shared.Count));
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_DONESHOPPING))]
    private void ReceiveDoneShopping(WIZARD_12_PROTOCOL.MSG_DONESHOPPING message) {
        if (GetActiveWizard() is { } wizard) {
            s_openBanks.TryRemove(wizard.CharId, out _);
        }
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_MOVEINVTOBANK))]
    private void ReceiveMoveInvToBank(WIZARD_12_PROTOCOL.MSG_MOVEINVTOBANK message) {
        var to = Banking.BankOf(message.UseShared != 0);
        var result = Move(message.GlobalID, BankPlace.Backpack, to);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_INVTOBANKCONFIRM {
            ClientRequestID = message.ClientRequestID,
            OriginalGlobalID = message.GlobalID,
            GlobalID = message.GlobalID,
            UseShared = message.UseShared,
            Failure = (sbyte) (result.Moved ? 0 : 1),
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_MOVEBANKTOINV))]
    private void ReceiveMoveBankToInv(WIZARD_12_PROTOCOL.MSG_MOVEBANKTOINV message) {
        var from = Banking.BankOf(message.UseShared != 0);
        var result = Move(message.GlobalID, from, BankPlace.Backpack);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_BANKTOINVCONFIRM {
            ClientRequestID = message.ClientRequestID,
            OriginalGlobalID = message.GlobalID,
            GlobalID = message.GlobalID,
            Failure = (sbyte) (result.Moved ? 0 : 1),
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_MOVEBANKTOBANK))]
    private void ReceiveMoveBankToBank(WIZARD_12_PROTOCOL.MSG_MOVEBANKTOBANK message) {
        var plan = Banking.BankToBank(message.BankID, message.BankSharedID);
        var result = plan is { } p ? Move(p.Id, p.From, p.To) : new BankMoveResult(BankRefusal.NotFound, null);
        var id = plan?.Id ?? 0UL;
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_BANKTOBANKCONFIRM {
            ClientRequestID = message.ClientRequestID,
            OriginalGlobalID = id,
            GlobalID = id,
            UseShared = (sbyte) (plan?.To == BankPlace.SharedBank ? 1 : 0),
            Error = result.Moved ? 0u : 1u,
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_BANKDELETE))]
    private void ReceiveBankDelete(WIZARD_12_PROTOCOL.MSG_BANKDELETE message) {
        var wizard = GetActiveWizard();
        var from = message.FromInventory != 0 ? BankPlace.Backpack : Banking.BankOf(message.UseShared != 0);
        var result = IsOpen(wizard) ? BankCollection.Delete(wizard, message.GlobalID, from) : default;
        if (result.Moved) {
            SendRemove(wizard, message.GlobalID, from);
            Logger.Information("{0} deleted item {1} ({2}) from the {3}.",
                Logger.Args(wizard.CharId, (ulong) message.GlobalID, result.Item.m_templateID.Full, from));
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_BANKDELETECONFIRM {
            ClientRequestID = message.ClientRequestID,
            GlobalID = message.GlobalID,
            UseShared = message.UseShared,
            Failure = (sbyte) (result.Moved ? 0 : 1),
        });
    }

    private BankMoveResult Move(ulong itemId, BankPlace from, BankPlace to) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return default;
        }

        if (!IsOpen(wizard)) {
            Logger.Warning("{0} asked to move item {1} ({2} -> {3}) without an open bank.",
                Logger.Args(wizard.CharId, itemId, from, to));
            return default;
        }

        // CLASSIC: and still standing by the chest it was opened at (InteractService only opens it in range).
        if (ServiceProximity.FindNear<Zone.Components.InteractBankComponent>(wizard, 0, GetZoneObject) is null) {
            Logger.Warning("{0} asked to move item {1} ({2} -> {3}) away from the bank chest.",
                Logger.Args(wizard.CharId, itemId, from, to));
            return default;
        }

        var result = BankCollection.Move(wizard, itemId, from, to);
        if (!result.Moved) {
            Logger.Debug("Bank move of item {0} for {1} ({2} -> {3}) refused: {4}.",
                Logger.Args(itemId, wizard.CharId, from, to, result.Refusal));
            SendToSocket(Classic.ClassicChat.Line(Banking.Describe(result.Refusal, to)));
            return result;
        }

        // Destination first: the confirm that follows looks the item up there.
        if (to == BankPlace.Backpack) {
            SendInventoryAdd(wizard, result.Item);
        }
        else {
            SendStorageAdd(wizard, result.Item, to);
        }

        SendRemove(wizard, itemId, from);
        Logger.Debug("Bank: {0} moved item {1} ({2}) {3} -> {4}.",
            Logger.Args(wizard.CharId, itemId, result.Item.m_templateID.Full, from, to));
        return result;
    }

    private void SendRemove(Wizard wizard, ulong itemId, BankPlace from) {
        if (from == BankPlace.Backpack) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM { GlobalID = wizard.GameObjectID, ItemID = itemId });
        }
        else {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_STORAGECLIENTREMOVE {
                GlobalID = wizard.GameObjectID,
                SharedBank = (sbyte) (from == BankPlace.SharedBank ? 1 : 0),
                ItemID = itemId,
            });
        }
    }

    private void SendStorageAdd(Wizard wizard, WizClientObjectItem item, BankPlace place) {
        if (!Serialize(item, out var data)) {
            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_STORAGECLIENTADD {
            GlobalID = wizard.GameObjectID,
            SerializedItem = data,
            SharedBank = (sbyte) (place == BankPlace.SharedBank ? 1 : 0),
        });
    }

    private void SendInventoryAdd(Wizard wizard, WizClientObjectItem item) {
        if (Serialize(item, out var data)) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM { GlobalID = wizard.GameObjectID, SerializedItem = data });
        }
    }

    private static bool Serialize(WizClientObjectItem item, out Imcodec.IO.ByteString data) {
        var serializer = new CoreObjectSerializer(behaviors: SerializerFlags.None);
        if (serializer.Serialize(item, ITEM_SERIALIZATION_FLAGS, out data)) {
            return true;
        }

        Logger.Error("Bank: item {0} did not serialize.", Logger.Args(item.m_globalID.Full));
        return false;
    }

}
