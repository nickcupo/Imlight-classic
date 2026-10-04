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
 * BANK AND SHARED BANK RULES
 * ========================================================================
 *
 * PURPOSE:
 * The 2009 dorm bank: a personal bank and an account-wide shared bank of 100 slots each, next to the backpack.
 * Items move backpack <-> bank <-> shared bank. These rules decide whether a move may happen and carry it out on
 * the id lists; the server's BankService persists the result and tells the client.
 *
 * Evidence (Wizard101 wiki revisions on or before 2010-05-25):
 * - Vendors and Banking oldid 56580 (2010-01-12): "your bank and shared bank has a total of 100 slots each ...
 *   when an item is deposited in the shared bank, any wizard on your account can access it."
 * - Vendors and Banking oldid 12380 (April 2009): the shared bank already existed.
 * - Dormitory oldid 4155 (January 2009): "Your bank is the chest at the end of your bed".
 * - Trading oldid 44230 (2009-09-28): only "Tradeable" items pass through the shared bank, so an item with
 *   FLAG_NoTrade stays with its wizard. The item infobox of the time lists No Trade and No Auction as two
 *   separate marks (Template:ClothingInfobox oldid 60547, Midsummer's Cowl oldid 60713, 2010-02-01); No Auction
 *   only kept an item out of the Bazaar, so it does not stop the shared bank.
 * - The backpack size is an owner ruling (2026-10-04): one 150-item backpack instead of 2009's eight per kind.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.Classic.Inventory;

/// <summary>Where an item sits: the backpack, the wizard's bank, or the account's shared bank.</summary>
public enum BankPlace : byte {
    Backpack,
    Bank,
    SharedBank,
}

/// <summary>Why a move was refused (<see cref="BankRefusal.None"/> when it happened).</summary>
public enum BankRefusal : byte {
    None,
    NotFound,
    SamePlace,
    Equipped,
    Full,
    NoTrade,
}

/// <summary>Slots in each place.</summary>
public readonly record struct BankLimits(int Backpack, int Bank, int SharedBank) {

    /// <summary>The classic profile: the owner's 150-item backpack, the 2009 bank and shared bank of 100 each.</summary>
    public static BankLimits Classic { get; } = new(Banking.ClassicBackpackSize, Banking.ClassicBankSize, Banking.ClassicSharedBankSize);

    public int Of(BankPlace place) => place switch {
        BankPlace.Backpack => Backpack,
        BankPlace.Bank => Bank,
        _ => SharedBank,
    };
}

/// <summary>
/// The id lists one move works on. Two wizards of one account share one <see cref="Shared"/> list.
/// </summary>
public sealed class BankLists(IList<ulong> backpack, IList<ulong> bank, IList<ulong> shared,
    IReadOnlyCollection<ulong>? equipped = null) {

    public IList<ulong> Backpack { get; } = backpack;
    public IList<ulong> Bank { get; } = bank;
    public IList<ulong> Shared { get; } = shared;
    public IReadOnlyCollection<ulong> Equipped { get; } = equipped ?? Array.Empty<ulong>();

    public IList<ulong> Of(BankPlace place) => place switch {
        BankPlace.Backpack => Backpack,
        BankPlace.Bank => Bank,
        _ => Shared,
    };

    /// <summary>Where the item is, or null.</summary>
    public BankPlace? Find(ulong id)
        => Backpack.Contains(id) ? BankPlace.Backpack : Bank.Contains(id) ? BankPlace.Bank
            : Shared.Contains(id) ? BankPlace.SharedBank : null;
}

public static class Banking {

    public const int ClassicBackpackSize = 150;
    public const int ClassicBankSize = 100;
    public const int ClassicSharedBankSize = 100;

    /// <summary>The template adjective of an item that stays with its wizard.</summary>
    public const string NoTradeFlag = "FLAG_NoTrade";

    /// <summary>True when an item with these template adjectives may go in the shared bank (no FLAG_NoTrade).</summary>
    public static bool CanShare(IEnumerable<string>? adjectives) {
        if (adjectives is null) {
            return true;
        }

        foreach (var adjective in adjectives) {
            if (string.Equals(adjective, NoTradeFlag, StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the item may move from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    /// <param name="lists">The wizard's lists; the item must be in the <paramref name="from"/> list.</param>
    /// <param name="id">The item's global id.</param>
    /// <param name="adjectives">The item template's adjectives (FLAG_NoTrade and so on).</param>
    public static BankRefusal Check(BankLists lists, ulong id, BankPlace from, BankPlace to, BankLimits limits,
        IEnumerable<string>? adjectives) {
        ArgumentNullException.ThrowIfNull(lists);
        if (from == to) {
            return BankRefusal.SamePlace;
        }

        // A worn item comes off in the backpack first (the client offers to move it; this server refuses).
        if (lists.Equipped.Contains(id)) {
            return BankRefusal.Equipped;
        }

        if (!lists.Of(from).Contains(id)) {
            return BankRefusal.NotFound;
        }

        if (to == BankPlace.SharedBank && !CanShare(adjectives)) {
            return BankRefusal.NoTrade;
        }

        var limit = limits.Of(to);
        return limit >= 0 && lists.Of(to).Count >= limit ? BankRefusal.Full : BankRefusal.None;
    }

    /// <summary>
    /// Moves the item between the lists when <see cref="Check"/> allows it. Nothing changes on a refusal.
    /// </summary>
    public static BankRefusal Move(BankLists lists, ulong id, BankPlace from, BankPlace to, BankLimits limits,
        IEnumerable<string>? adjectives) {
        var refusal = Check(lists, id, from, to, limits, adjectives);
        if (refusal != BankRefusal.None) {
            return refusal;
        }

        lists.Of(from).Remove(id);
        lists.Of(to).Add(id);
        return BankRefusal.None;
    }

    /// <summary>
    /// The place a client move names: MSG_MOVEINVTOBANK and MSG_MOVEBANKTOINV carry UseShared (0 bank, else shared).
    /// </summary>
    public static BankPlace BankOf(bool useShared) => useShared ? BankPlace.SharedBank : BankPlace.Bank;

    /// <summary>
    /// The direction of MSG_MOVEBANKTOBANK: the client fills BankID when it moves a bank item to the shared bank and
    /// BankSharedID when it moves a shared item to the bank (BankingWindow, r806919). Null when both or neither are set.
    /// </summary>
    public static (ulong Id, BankPlace From, BankPlace To)? BankToBank(ulong bankId, ulong bankSharedId)
        => (bankId, bankSharedId) switch {
            (not 0, 0) => (bankId, BankPlace.Bank, BankPlace.SharedBank),
            (0, not 0) => (bankSharedId, BankPlace.SharedBank, BankPlace.Bank),
            _ => null,
        };

    /// <summary>The player-facing text of a refusal.</summary>
    public static string Describe(BankRefusal refusal, BankPlace to) => refusal switch {
        BankRefusal.Full => to switch {
            BankPlace.Backpack => "Your backpack is full.",
            BankPlace.Bank => "Your bank is full.",
            _ => "Your shared bank is full.",
        },
        BankRefusal.NoTrade => "No Trade items can't go in the shared bank.",
        BankRefusal.Equipped => "Take that item off before you put it in the bank.",
        _ => "That item isn't there any more.",
    };
}

/// <summary>One item in an account's shared bank.</summary>
public sealed class SharedBankEntry {

    /// <summary>The item's global id.</summary>
    public ulong ItemId { get; set; }

    /// <summary>The item's document in the WizardItems collection (its owner id is 0 while it is shared).</summary>
    public string DocumentId { get; set; } = "";

    /// <summary>The wizard who put it in.</summary>
    public ulong DepositedBy { get; set; }
}

/// <summary>
/// An account's shared bank (document SharedBanks/{AccountId}). Every wizard of the account reads and writes it.
/// </summary>
public sealed class SharedBankLedger {

    public const string CollectionName = "SharedBanks";

    public static string DocumentId(ulong accountId) => $"{CollectionName}/{accountId}";

    public ulong AccountId { get; set; }

    public List<SharedBankEntry> Items { get; set; } = [];

    public List<ulong> ItemIds() => Items.ConvertAll(entry => entry.ItemId);

    public SharedBankEntry? Find(ulong itemId) => Items.Find(entry => entry.ItemId == itemId);

    /// <summary>Keeps the entries whose ids are in <paramref name="ids"/>, in that order, and adds new ones.</summary>
    public void Reconcile(IEnumerable<ulong> ids, Func<ulong, SharedBankEntry> added) {
        var next = new List<SharedBankEntry>();
        foreach (var id in ids) {
            next.Add(Find(id) ?? added(id));
        }

        Items = next;
    }
}

