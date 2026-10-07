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
 * TRADE SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the server side of the r806919 client's Treasure Trade window. Each player's messages go to the one
 * TreasureTradeManager (Game/Trading), which keeps both sides and makes the swap.
 *
 * NOTE:
 * A player who logs out or whose session closes leaves their trade: the partner sees "has cancelled the trade."
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Game.Trading;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal sealed class TradeService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new TradeService(parentActor));

    // The live wizard of every player who has used the trade window, for the partner's side of a trade.
    private static readonly ConcurrentDictionary<ulong, Wizard> s_live = new();

    private ulong _charId;

    private static TreasureTradeManager Trades => TreasureTradeManager.Instance;

    private ulong Register() {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return 0;
        }

        Trades.UseWorld(new ServerTradeWorld(Context.System));
        s_live[wizard.CharId] = wizard;
        _charId = wizard.CharId;

        return wizard.CharId;
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_TRADE_CREATE))]
    private void ReceiveTradeCreate(WIZARD_12_PROTOCOL.MSG_TRADE_CREATE message) {
        if (Register() is var me and not 0) {
            Trades.Create(me, message.TargetGID);
        }
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_TRADE_JOIN_STATUS))]
    private void ReceiveTradeJoinStatus(WIZARD_12_PROTOCOL.MSG_TRADE_JOIN_STATUS message) {
        if (Register() is var me and not 0) {
            Trades.Join(me, message.TargetGID, message.PlayerStatus);
        }
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_TRADE_CHANGE_ITEM))]
    private void ReceiveTradeChangeItem(WIZARD_12_PROTOCOL.MSG_TRADE_CHANGE_ITEM message) {
        if (Register() is var me and not 0) {
            Trades.ChangeItem(me, message.TargetGID, message.ItemTemplate, message.ItemEnchant, message.Action);
        }
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_TRADE_READY_STATUS))]
    private void ReceiveTradeReadyStatus(WIZARD_12_PROTOCOL.MSG_TRADE_READY_STATUS message) {
        if (Register() is var me and not 0) {
            Trades.Ready(me, message.TargetGID, message.PlayerStatus);
        }
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_TRADE_CHANGE_MONEY))]
    private void ReceiveTradeChangeMoney(WIZARD_12_PROTOCOL.MSG_TRADE_CHANGE_MONEY message) {
        if (Register() is var me and not 0) {
            Trades.ChangeMoney(me, message.TargetGID);
        }
    }

    protected override void OnPreDispose() {
        if (_charId != 0) {
            Trades.Leave(_charId);
            s_live.TryRemove(_charId, out _);
        }

        base.OnPreDispose();
    }

    /// <summary>The live server behind the trade manager.</summary>
    private sealed class ServerTradeWorld(ActorSystem system) : ITradeWorld {

        public bool TradingEnabled
            => Imlight.Classic.ClassicRuntime.Rules.IsFeatureEnabled(Imlight.Classic.ClassicFeatures.TreasureCards);

        public TradeParty Party(ulong charId) {
            var online = OnlinePlayerCollection.GetOnlinePlayer(charId);
            if (online is null) {
                return null;
            }

            s_live.TryGetValue(charId, out var wizard);

            return new TradeParty(charId, wizard, wizard?.Zone ?? online.CurrentZone, online.InstanceOwnerId, wizard?.Location,
                online.HousingDeedId); // CLASSIC
        }

        public bool AreFriends(ulong charId, ulong otherCharId)
            => BuddyRelationshipCollection.GetRelationshipsForPlayer(charId).Any(r =>
                (r.FirstPlayerId == otherCharId || r.SecondPlayerId == otherCharId) && !r.Blocked && !r.IsBrokenUp);

        public uint TreasureTemplateOf(uint spellId) => SpellbookService.TreasureTemplateOf(unchecked((int) spellId));

        public void Send(ulong charId, IMessage message) {
            if (OnlinePlayerCollection.GetOnlinePlayer(charId)?.ActorPath is { Length: > 0 } path) {
                system.ActorSelection(path).Tell(message);
            }
        }

        public bool Commit(Wizard first, IReadOnlyList<uint> firstGives, Wizard second, IReadOnlyList<uint> secondGives)
        {
            try {
                return WizardCollection.CommitTreasureCardTrade(first, firstGives, second, secondGives);
            }
            finally {
                // CLASSIC: a trade can be durable despite a lost acknowledgement. Both participants reload
                // rather than keeping stale books open after the manager reports an ordinary failed trade.
                foreach (var wizard in new[] { first, second }) {
                    if (WizardCollection.IsInventorySnapshotUncertain(wizard)
                        && OnlinePlayerCollection.GetOnlinePlayer(wizard.CharId)?.ActorPath is { Length: > 0 } path)
                        system.ActorSelection(path).Tell("Close");
                }
            }
        }

    }

}
