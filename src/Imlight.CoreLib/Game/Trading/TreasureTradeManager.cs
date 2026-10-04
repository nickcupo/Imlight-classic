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
 * TREASURE TRADE MANAGER
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: treasure card trading between two friends standing close together, as in 2009. One lock holds every
 * trade's state; the swap itself is one save of both books (WizardCollection.CommitTreasureCardTrade).
 *
 * The protocol is the r806919 client's (WizardGraphicalClient.exe, read with capstone; see the session report
 * playbot-reports/trade-potions.md):
 * - Friends list "Trade": the client opens its TradeWindow at once and sends MSG_TRADE_CREATE{TargetGID}.
 * - The target gets MSG_TRADE_REQUEST{TargetGID = requester} (TradeRequestWindow). Accept: the target opens its
 *   TradeWindow and sends MSG_TRADE_JOIN_STATUS{TargetGID = requester, PlayerStatus 1}; decline, or an ignored
 *   request (trades off in the options), sends PlayerStatus 0.
 * - Server to client, every status message is from the receiver's side: TargetGID = the partner, PlayerStatus = the
 *   receiver's own status, TargetStatus = the partner's. A JOIN_STATUS with either status 0 makes the client show
 *   "$PLAYERNAME$ has cancelled the trade." (TradeWindow), and TargetStatus 0 closes the request window.
 * - MSG_TRADE_CHANGE_ITEM{TargetGID, ItemTemplate (the card's spell id, the name hash), ItemEnchant, Action 1 add /
 *   0 remove}; relayed to the partner with ChangeGID = TargetGID = the sender. The window holds 4 different cards.
 * - MSG_TRADE_READY_STATUS PlayerStatus: 1 = the Ready box ticked, 0 = unticked, 2 = "Trade" pressed in the
 *   confirmation (only offered once both are ready).
 * - MSG_TRADE_RESULT{TargetGID, Status, ItemsGained, ItemsLost}: Status 0 = done; the client then moves the cards
 *   in its own book (removes what it offered, adds what it got), so the server sends no book messages. Status N > 0
 *   shows GUI TradeErrorN.
 *
 * 2009 rules (Fandom Trading oldid 6297, 2009-01-29; oldid 44230, 2009-09-28; oldid 51252, 2009-11-07): trade with a
 * friend from the Friends List, "standing very close"; "you may only trade treasure cards". Gold cannot be traded
 * (Gold oldid 63649), so MSG_TRADE_CHANGE_MONEY is refused.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.Math;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Trading;

/// <summary>Where a player is, as the trade rules see it.</summary>
internal sealed record TradeParty(ulong CharId, Wizard Wizard, string Zone, ulong Instance, Vector3? Location);

/// <summary>What the trade manager needs from the server (fakes in tests).</summary>
internal interface ITradeWorld {

    /// <summary>The online player, or null when offline.</summary>
    TradeParty Party(ulong charId);

    bool AreFriends(ulong charId, ulong otherCharId);

    /// <summary>The profile's treasure card switch.</summary>
    bool TradingEnabled { get; }

    /// <summary>The treasure card template of a client spell id (template id or spell-name hash); 0 when none.</summary>
    uint TreasureTemplateOf(uint spellId);

    void Send(ulong charId, IMessage message);

    /// <summary>Saves the swap of both books at once; false when a card is gone or the save fails.</summary>
    bool Commit(Wizard first, IReadOnlyList<uint> firstGives, Wizard second, IReadOnlyList<uint> secondGives);

}

/// <summary>Result codes of MSG_TRADE_RESULT (the client's GUI TradeErrorN texts).</summary>
internal static class TradeStatus {

    public const uint Done = 0;
    public const uint NotInZone = 4;          // "Target player is not in the same zone."
    public const uint AlreadyTrading = 7;     // "Target player is already in a trade."
    public const uint Failed = 8;             // "Trade failed."
    public const uint CannotHold = 13;        // "One of the players ... cannot hold the items"
    public const uint CannotTrade = 14;       // "Target player cannot trade at this time."
    public const uint NotAccepting = 15;      // "Target player is not accepting trades at this time."

}

/// <summary>CLASSIC: every treasure card trade on the server.</summary>
internal sealed class TreasureTradeManager {

    /// <summary>Different cards one side may offer: the TradeWindow takes no fifth (client r806919).</summary>
    public const int MaxCardsPerSide = 4;

    /// <summary>
    /// How close the two must stand, in world units. 2009 says only "standing very close" (Trading oldid 6297); the
    /// number is ours (about a few steps; a wizard runs ~230 units a second).
    /// </summary>
    public const float MaxDistance = 400f;

    private enum Phase { Requested, Open }

    private sealed class Side {

        public ulong CharId;
        public int Ready;
        public readonly List<uint> Offer = []; // spell ids as the client sends them, one entry per card

    }

    private sealed class Trade {

        public Phase Phase;
        public readonly Side Requester = new();
        public readonly Side Target = new();

        public Side Of(ulong charId) => Requester.CharId == charId ? Requester : Target;
        public Side PartnerOf(ulong charId) => Requester.CharId == charId ? Target : Requester;

    }

    private readonly object _gate = new();
    private readonly Dictionary<ulong, Trade> _byChar = [];
    private ITradeWorld _world;

    public TreasureTradeManager(ITradeWorld world) => _world = world;

    /// <summary>The server's trade manager.</summary>
    public static TreasureTradeManager Instance { get; } = new(null);

    /// <summary>Sets the server's world once (TradeService does it on first use).</summary>
    public void UseWorld(ITradeWorld world) {
        lock (_gate) {
            _world ??= world;
        }
    }

    /// <summary>True while <paramref name="charId"/> is in a trade or has asked for one (for tests and logs).</summary>
    public bool IsTrading(ulong charId) {
        lock (_gate) {
            return _byChar.ContainsKey(charId);
        }
    }

    private static ulong Gid(ulong charId) => Wizard.GetGameObjectId(charId);

    // MSG_TRADE_CREATE from the requester.
    public void Create(ulong requester, ulong targetGid) {
        lock (_gate) {
            if (!Wizard.TryGetCharacterId(targetGid, out var target) || target == requester) {
                return;
            }

            // A new window replaces whatever this player had open.
            LeaveLocked(requester);

            var refusal = CheckPair(requester, target, requireLocation: false);
            if (refusal is null && _byChar.ContainsKey(target)) {
                refusal = TradeStatus.AlreadyTrading;
            }

            if (refusal is { } status) {
                Logger.Information("Trade: {0} -> {1} refused (TradeError{2}).", Logger.Args(requester, target, status));
                SendResult(requester, target, status, 0, 0);

                return;
            }

            var trade = new Trade { Phase = Phase.Requested };
            trade.Requester.CharId = requester;
            trade.Target.CharId = target;
            _byChar[requester] = trade;
            _byChar[target] = trade;
            Logger.Information("Trade: {0} asks {1} to trade.", Logger.Args(requester, target));
            _world.Send(target, new WIZARD_12_PROTOCOL.MSG_TRADE_REQUEST { TargetGID = Gid(requester) });
        }
    }

    // MSG_TRADE_JOIN_STATUS from either player.
    public void Join(ulong charId, ulong targetGid, int playerStatus) {
        lock (_gate) {
            if (!_byChar.TryGetValue(charId, out var trade) || !Wizard.TryGetCharacterId(targetGid, out var partnerId)
                || trade.PartnerOf(charId).CharId != partnerId) {
                return;
            }

            if (playerStatus == 0) {
                Logger.Information("Trade: {0} leaves the trade with {1}.", Logger.Args(charId, partnerId));
                LeaveLocked(charId);

                return;
            }

            if (trade.Phase != Phase.Requested || trade.Target.CharId != charId) {
                return;
            }

            // The target accepts: both must still be together.
            if (CheckPair(trade.Requester.CharId, charId, requireLocation: true) is { } refusal) {
                Logger.Information("Trade: {0} accepted {1} but TradeError{2}.", Logger.Args(charId, partnerId, refusal));
                EndLocked(trade);
                SendResult(charId, partnerId, refusal, 0, 0);
                SendResult(partnerId, charId, refusal, 0, 0);

                return;
            }

            trade.Phase = Phase.Open;
            Logger.Information("Trade: {0} and {1} open a trade.", Logger.Args(trade.Requester.CharId, charId));
            SendJoin(trade.Requester.CharId, charId, 1, 1);
            SendJoin(charId, trade.Requester.CharId, 1, 1);
        }
    }

    // MSG_TRADE_CHANGE_ITEM from either player.
    public void ChangeItem(ulong charId, ulong targetGid, uint spellId, uint enchant, byte action) {
        lock (_gate) {
            if (!OpenTradeOf(charId, targetGid, out var trade)) {
                return;
            }

            var me = trade.Of(charId);
            var partner = trade.PartnerOf(charId);
            if (action != 0) {
                if (!CanOffer(me, spellId, enchant)) {
                    Logger.Warning("Trade: {0} offered spell {1} (enchant {2}) it cannot trade; the trade fails.",
                        Logger.Args(charId, spellId, enchant));
                    FailLocked(trade, TradeStatus.Failed);

                    return;
                }

                me.Offer.Add(spellId);
            }
            else if (!me.Offer.Remove(spellId)) {
                return;
            }

            _world.Send(partner.CharId, new WIZARD_12_PROTOCOL.MSG_TRADE_CHANGE_ITEM {
                TargetGID = Gid(charId), ChangeGID = Gid(charId), ItemTemplate = spellId, ItemEnchant = enchant, Action = action,
            });

            // Any change to the offer clears both Ready boxes, so nobody confirms a trade they have not seen.
            me.Ready = 0;
            partner.Ready = 0;
            SendReadyBoth(trade);
        }
    }

    // MSG_TRADE_READY_STATUS from either player.
    public void Ready(ulong charId, ulong targetGid, int playerStatus) {
        lock (_gate) {
            if (!OpenTradeOf(charId, targetGid, out var trade)) {
                return;
            }

            var me = trade.Of(charId);
            var partner = trade.PartnerOf(charId);
            var status = Math.Clamp(playerStatus, 0, 2);
            if (status == 2 && partner.Ready < 1) {
                status = 1; // the confirmation exists only once both boxes are ticked
            }

            me.Ready = status;
            if (me.Ready == 2 && partner.Ready == 2) {
                ExecuteLocked(trade);

                return;
            }

            SendReadyBoth(trade);
        }
    }

    // MSG_TRADE_CHANGE_MONEY: gold is never traded in 2009.
    public void ChangeMoney(ulong charId, ulong targetGid) {
        lock (_gate) {
            if (OpenTradeOf(charId, targetGid, out var trade)) {
                Logger.Warning("Trade: {0} tried to add gold; gold cannot be traded.", Logger.Args(charId));
                FailLocked(trade, TradeStatus.Failed);
            }
        }
    }

    /// <summary>The player logged out or left: the partner sees the trade cancelled.</summary>
    public void Leave(ulong charId) {
        lock (_gate) {
            LeaveLocked(charId);
        }
    }

    private bool OpenTradeOf(ulong charId, ulong targetGid, out Trade trade)
        => _byChar.TryGetValue(charId, out trade) && trade.Phase == Phase.Open
           && Wizard.TryGetCharacterId(targetGid, out var partnerId) && trade.PartnerOf(charId).CharId == partnerId;

    private bool CanOffer(Side side, uint spellId, uint enchant) {
        if (enchant != 0 || spellId == 0) {
            return false; // 2009 books held no enchanted cards, and the server keeps none
        }

        if (!side.Offer.Contains(spellId) && side.Offer.Distinct().Count() >= MaxCardsPerSide) {
            return false;
        }

        var template = _world.TreasureTemplateOf(spellId);
        var party = _world.Party(side.CharId);
        if (template == 0 || party?.Wizard is not { } wizard) {
            return false;
        }

        var offered = side.Offer.Count(id => _world.TreasureTemplateOf(id) == template);

        return wizard.SpellbookBehavior.TreasureCardCount(template) > offered;
    }

    private void ExecuteLocked(Trade trade) {
        var a = trade.Requester;
        var b = trade.Target;
        if (CheckPair(a.CharId, b.CharId, requireLocation: true) is { } refusal) {
            FailLocked(trade, refusal);

            return;
        }

        var partyA = _world.Party(a.CharId);
        var partyB = _world.Party(b.CharId);
        var aGives = a.Offer.Select(_world.TreasureTemplateOf).ToList();
        var bGives = b.Offer.Select(_world.TreasureTemplateOf).ToList();
        if (aGives.Contains(0u) || bGives.Contains(0u)) {
            FailLocked(trade, TradeStatus.Failed);

            return;
        }

        bool saved;
        try {
            saved = _world.Commit(partyA.Wizard, aGives, partyB.Wizard, bGives);
        }
        catch (Exception ex) {
            Logger.Error("Trade: saving the trade of {0} and {1} failed: {2}", Logger.Args(a.CharId, b.CharId, ex.Message));
            saved = false;
        }

        if (!saved) {
            FailLocked(trade, TradeStatus.Failed);

            return;
        }

        EndLocked(trade);
        Logger.Information("Trade: {0} gave {1} [{2}], {3} gave {4} [{5}].",
            Logger.Args(a.CharId, aGives.Count, string.Join(",", aGives), b.CharId, bGives.Count, string.Join(",", bGives)));
        SendResult(a.CharId, b.CharId, TradeStatus.Done, (uint) bGives.Count, (uint) aGives.Count);
        SendResult(b.CharId, a.CharId, TradeStatus.Done, (uint) aGives.Count, (uint) bGives.Count);
    }

    // The two may trade: online, both trading allowed, friends, same zone and instance, and (once the target has
    // answered) standing close. Returns the client's error code, or null.
    private uint? CheckPair(ulong requester, ulong target, bool requireLocation) {
        if (_world is null || !_world.TradingEnabled) {
            return TradeStatus.CannotTrade;
        }

        var a = _world.Party(requester);
        var b = _world.Party(target);
        if (a is null || b is null) {
            return TradeStatus.CannotTrade;
        }

        if (!_world.AreFriends(requester, target)) {
            return TradeStatus.CannotTrade;
        }

        if (!string.Equals(a.Zone, b.Zone, StringComparison.OrdinalIgnoreCase) || a.Instance != b.Instance) {
            return TradeStatus.NotInZone;
        }

        if (requireLocation && (a.Wizard is null || b.Wizard is null)) {
            return TradeStatus.CannotTrade;
        }

        if (a.Location is { } la && b.Location is { } lb && Distance(la, lb) > MaxDistance) {
            return TradeStatus.CannotTrade;
        }

        return null;
    }

    private static float Distance(Vector3 a, Vector3 b) {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;

        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private void LeaveLocked(ulong charId) {
        if (!_byChar.TryGetValue(charId, out var trade)) {
            return;
        }

        var partner = trade.PartnerOf(charId).CharId;
        EndLocked(trade);
        if (trade.Phase == Phase.Requested && trade.Requester.CharId == charId) {
            // The requester gave up before an answer: the target's request window closes.
            SendJoin(partner, charId, 0, 0);
        }
        else {
            // "$PLAYERNAME$ has cancelled the trade."
            SendJoin(partner, charId, 1, 0);
        }
    }

    private void FailLocked(Trade trade, uint status) {
        EndLocked(trade);
        SendResult(trade.Requester.CharId, trade.Target.CharId, status, 0, 0);
        SendResult(trade.Target.CharId, trade.Requester.CharId, status, 0, 0);
    }

    private void EndLocked(Trade trade) {
        if (_byChar.TryGetValue(trade.Requester.CharId, out var a) && ReferenceEquals(a, trade)) {
            _byChar.Remove(trade.Requester.CharId);
        }

        if (_byChar.TryGetValue(trade.Target.CharId, out var b) && ReferenceEquals(b, trade)) {
            _byChar.Remove(trade.Target.CharId);
        }
    }

    private void SendReadyBoth(Trade trade) {
        var a = trade.Requester;
        var b = trade.Target;
        _world.Send(a.CharId, new WIZARD_12_PROTOCOL.MSG_TRADE_READY_STATUS { TargetGID = Gid(b.CharId), PlayerStatus = a.Ready, TargetStatus = b.Ready });
        _world.Send(b.CharId, new WIZARD_12_PROTOCOL.MSG_TRADE_READY_STATUS { TargetGID = Gid(a.CharId), PlayerStatus = b.Ready, TargetStatus = a.Ready });
    }

    private void SendJoin(ulong to, ulong partner, int playerStatus, int targetStatus)
        => _world.Send(to, new WIZARD_12_PROTOCOL.MSG_TRADE_JOIN_STATUS {
            TargetGID = Gid(partner), PlayerStatus = playerStatus, TargetStatus = targetStatus,
        });

    private void SendResult(ulong to, ulong partner, uint status, uint gained, uint lost)
        => _world?.Send(to, new WIZARD_12_PROTOCOL.MSG_TRADE_RESULT {
            TargetGID = Gid(partner), Status = status, ItemsGained = gained, ItemsLost = lost,
        });

}
