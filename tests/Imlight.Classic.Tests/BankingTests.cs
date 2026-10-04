using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Imlight.Classic.Inventory;
using Xunit;

namespace Imlight.Classic.Tests;

public class BankingTests {

    private static readonly string[] Plain = ["Equipment", "Hat"];
    private static readonly string[] NoTrade = ["Equipment", Banking.NoTradeFlag];
    private static readonly string[] NoAuction = ["Equipment", "FLAG_NoAuction", "FLAG_CrownsOnly"];

    private static BankLists Lists(IEnumerable<ulong>? backpack = null, IEnumerable<ulong>? bank = null,
        List<ulong>? shared = null, IEnumerable<ulong>? equipped = null)
        => new([.. backpack ?? []], [.. bank ?? []], shared ?? [], equipped?.ToHashSet());

    private static List<ulong> Ids(int from, int count) => [.. Enumerable.Range(from, count).Select(i => (ulong) i)];

    [Fact]
    public void Classic_limits_are_the_owner_backpack_and_the_2009_banks() {
        Assert.Equal(new BankLimits(150, 100, 100), BankLimits.Classic);
        Assert.Equal(150, BankLimits.Classic.Of(BankPlace.Backpack));
        Assert.Equal(100, BankLimits.Classic.Of(BankPlace.Bank));
        Assert.Equal(100, BankLimits.Classic.Of(BankPlace.SharedBank));
    }

    [Fact]
    public void Deposit_and_withdraw_move_the_id_between_backpack_bank_and_shared_bank() {
        var lists = Lists(backpack: [1, 2]);
        Assert.Equal(BankRefusal.None, Banking.Move(lists, 1, BankPlace.Backpack, BankPlace.Bank, BankLimits.Classic, Plain));
        Assert.Equal([2UL], lists.Backpack);
        Assert.Equal([1UL], lists.Bank);
        Assert.Equal(BankPlace.Bank, lists.Find(1));

        Assert.Equal(BankRefusal.None, Banking.Move(lists, 1, BankPlace.Bank, BankPlace.SharedBank, BankLimits.Classic, Plain));
        Assert.Empty(lists.Bank);
        Assert.Equal([1UL], lists.Shared);

        Assert.Equal(BankRefusal.None, Banking.Move(lists, 1, BankPlace.SharedBank, BankPlace.Backpack, BankLimits.Classic, Plain));
        Assert.Equal([2UL, 1UL], lists.Backpack);
        Assert.Empty(lists.Shared);
        Assert.Null(lists.Find(99));
    }

    [Fact]
    public void A_full_destination_refuses_and_changes_nothing() {
        var lists = Lists(backpack: [500], bank: Ids(1, 100));
        Assert.Equal(BankRefusal.Full, Banking.Move(lists, 500, BankPlace.Backpack, BankPlace.Bank, BankLimits.Classic, Plain));
        Assert.Equal([500UL], lists.Backpack);
        Assert.Equal(100, lists.Bank.Count);

        var shared = Lists(backpack: [500], shared: Ids(1, 100));
        Assert.Equal(BankRefusal.Full, Banking.Move(shared, 500, BankPlace.Backpack, BankPlace.SharedBank, BankLimits.Classic, Plain));

        var backpack = Lists(backpack: Ids(1, 150), bank: [900]);
        Assert.Equal(BankRefusal.Full, Banking.Move(backpack, 900, BankPlace.Bank, BankPlace.Backpack, BankLimits.Classic, Plain));
        var roomy = Lists(backpack: Ids(1, 149), bank: [900]);
        Assert.Equal(BankRefusal.None, Banking.Move(roomy, 900, BankPlace.Bank, BankPlace.Backpack, BankLimits.Classic, Plain));
        Assert.Equal(150, roomy.Backpack.Count);
        Assert.Equal(BankRefusal.Full, Banking.Check(roomy, 1, BankPlace.Backpack, BankPlace.Bank, new BankLimits(150, 0, 100), Plain));
    }

    [Fact]
    public void No_trade_items_stay_out_of_the_shared_bank_but_bank_normally() {
        Assert.False(Banking.CanShare(NoTrade));
        Assert.True(Banking.CanShare(NoAuction));
        Assert.True(Banking.CanShare(null));

        var lists = Lists(backpack: [7], bank: [8]);
        Assert.Equal(BankRefusal.NoTrade, Banking.Move(lists, 7, BankPlace.Backpack, BankPlace.SharedBank, BankLimits.Classic, NoTrade));
        Assert.Equal(BankRefusal.NoTrade, Banking.Move(lists, 8, BankPlace.Bank, BankPlace.SharedBank, BankLimits.Classic, NoTrade));
        Assert.Empty(lists.Shared);
        Assert.Equal(BankRefusal.None, Banking.Move(lists, 7, BankPlace.Backpack, BankPlace.Bank, BankLimits.Classic, NoTrade));
        Assert.Equal(BankRefusal.None, Banking.Move(lists, 8, BankPlace.Bank, BankPlace.SharedBank, BankLimits.Classic, NoAuction));
    }

    [Fact]
    public void Worn_missing_and_same_place_moves_are_refused() {
        var lists = Lists(backpack: [1], equipped: [2]);
        Assert.Equal(BankRefusal.Equipped, Banking.Move(lists, 2, BankPlace.Backpack, BankPlace.Bank, BankLimits.Classic, Plain));
        Assert.Equal(BankRefusal.NotFound, Banking.Move(lists, 3, BankPlace.Backpack, BankPlace.Bank, BankLimits.Classic, Plain));
        Assert.Equal(BankRefusal.NotFound, Banking.Move(lists, 1, BankPlace.Bank, BankPlace.Backpack, BankLimits.Classic, Plain));
        Assert.Equal(BankRefusal.SamePlace, Banking.Move(lists, 1, BankPlace.Backpack, BankPlace.Backpack, BankLimits.Classic, Plain));
        Assert.Equal([1UL], lists.Backpack);
    }

    [Fact]
    public void Two_wizards_of_one_account_pass_an_item_through_the_shared_bank() {
        var shared = new List<ulong>();
        var first = Lists(backpack: [11], shared: shared);
        var second = Lists(backpack: [21], shared: shared);

        Assert.Equal(BankRefusal.None, Banking.Move(first, 11, BankPlace.Backpack, BankPlace.SharedBank, BankLimits.Classic, Plain));
        Assert.Equal(BankPlace.SharedBank, second.Find(11));
        Assert.Equal(BankRefusal.None, Banking.Move(second, 11, BankPlace.SharedBank, BankPlace.Bank, BankLimits.Classic, Plain));
        Assert.Empty(shared);
        Assert.Equal([11UL], second.Bank);
        Assert.Null(first.Find(11));
        // The first wizard cannot take it back now.
        Assert.Equal(BankRefusal.NotFound, Banking.Move(first, 11, BankPlace.SharedBank, BankPlace.Backpack, BankLimits.Classic, Plain));
    }

    [Theory]
    [InlineData(5UL, 0UL, 5UL, BankPlace.Bank, BankPlace.SharedBank)]
    [InlineData(0UL, 6UL, 6UL, BankPlace.SharedBank, BankPlace.Bank)]
    public void Bank_to_bank_direction_follows_the_filled_id(ulong bankId, ulong sharedId, ulong id, BankPlace from, BankPlace to) {
        Assert.Equal((id, from, to), Banking.BankToBank(bankId, sharedId));
    }

    [Fact]
    public void Bank_to_bank_with_both_or_neither_id_is_ignored() {
        Assert.Null(Banking.BankToBank(0, 0));
        Assert.Null(Banking.BankToBank(1, 2));
        Assert.Equal(BankPlace.SharedBank, Banking.BankOf(true));
        Assert.Equal(BankPlace.Bank, Banking.BankOf(false));
    }

    [Fact]
    public void Refusals_have_player_texts() {
        Assert.Equal("Your shared bank is full.", Banking.Describe(BankRefusal.Full, BankPlace.SharedBank));
        Assert.Equal("Your bank is full.", Banking.Describe(BankRefusal.Full, BankPlace.Bank));
        Assert.Equal("Your backpack is full.", Banking.Describe(BankRefusal.Full, BankPlace.Backpack));
        Assert.Contains("No Trade", Banking.Describe(BankRefusal.NoTrade, BankPlace.SharedBank));
    }

    [Fact]
    public void Shared_ledger_keeps_entries_and_survives_a_save_round_trip() {
        var ledger = new SharedBankLedger { AccountId = 42 };
        ledger.Reconcile([1, 2], id => new SharedBankEntry { ItemId = id, DocumentId = $"items/{id}", DepositedBy = 7 });
        Assert.Equal("SharedBanks/42", SharedBankLedger.DocumentId(42));

        var saved = JsonSerializer.Deserialize<SharedBankLedger>(JsonSerializer.Serialize(ledger))!;
        Assert.Equal(42UL, saved.AccountId);
        Assert.Equal([1UL, 2UL], saved.ItemIds());
        Assert.Equal("items/2", saved.Find(2)!.DocumentId);

        // A move out keeps the other entry (with its document) and a move in adds one.
        saved.Reconcile([2, 3], id => new SharedBankEntry { ItemId = id, DocumentId = "new", DepositedBy = 8 });
        Assert.Equal([2UL, 3UL], saved.ItemIds());
        Assert.Equal("items/2", saved.Find(2)!.DocumentId);
        Assert.Equal(8UL, saved.Find(3)!.DepositedBy);
        Assert.Null(saved.Find(1));
    }
}
