using System;
using System.Collections.Generic;
using Imlight.Classic.Inventory;
using Xunit;

namespace Imlight.Classic.Tests;

public class BackpackQuickSellTests {
    [Fact]
    public void Unsellable_flag_used_by_both_shop_paths_blocks_removal() {
        Assert.False(BackpackQuickSell.IsSellable(["Equipment", "FLAG_NoSell"]));
        Assert.True(BackpackQuickSell.IsSellable(["Equipment"]));
        Assert.True(BackpackQuickSell.IsSellable(null));
        var sales = BackpackQuickSell.Execute([new(1, 1)],
            _ => BackpackQuickSell.IsSellable(["FLAG_NoSell"]) ? 10 : null,
            _ => throw new Exception("Must not remove unsellable item"));
        Assert.Empty(sales);
    }

    [Fact]
    public void Forged_quantities_duplicates_and_missing_items_never_create_gold() {
        var owned = new HashSet<ulong> { 1, 2, 3 };
        var sales = BackpackQuickSell.Execute([
            new(1, int.MaxValue), new(2, -1), new(3, 0), new(99, 1),
            new(1, 1), new(1, 1), new(2, 1)
        ], id => owned.Contains(id) ? 10 : null, owned.Remove);
        Assert.Equal(new[] { new BackpackQuickSell.Sale(1, 10), new BackpackQuickSell.Sale(2, 10) }, sales);
        Assert.Equal(new ulong[] { 3 }, owned);
    }
    [Fact]
    public void Removal_lost_to_another_service_cannot_be_paid() {
        Assert.Empty(BackpackQuickSell.Execute([new(1, 1)], _ => 50, _ => false));
    }
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(2147483648d)]
    public void Invalid_prices_never_remove_items(double value) {
        Assert.Empty(BackpackQuickSell.Execute([new(1, 1)], _ => value, _ => throw new Exception("Must not remove")));
    }
    [Fact]
    public void Gold_is_capped_without_integer_overflow() {
        var sales = new[] { new BackpackQuickSell.Sale(1, int.MaxValue), new BackpackQuickSell.Sale(2, int.MaxValue) };
        Assert.Equal(25, BackpackQuickSell.GoldToApply(sales, 975, 1000));
        Assert.Equal(0, BackpackQuickSell.GoldToApply(sales, 1001, 1000));
    }
}
