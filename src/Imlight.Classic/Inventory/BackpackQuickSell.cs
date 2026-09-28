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
 */

using System;
using System.Collections.Generic;

namespace Imlight.Classic.Inventory;

// CLASSIC: backpack objects have unique IDs, not client-controlled stack quantities.
public static class BackpackQuickSell {
    public static bool IsSellable(IEnumerable<string>? adjectives) {
        if (adjectives is null) return true;
        foreach (var adjective in adjectives)
            if (string.Equals(adjective, "FLAG_NoSell", StringComparison.Ordinal)) return false;
        return true;
    }

    public readonly record struct Request(ulong Id, long Quantity);
    public readonly record struct Sale(ulong Id, int Gold);

    public static IReadOnlyList<Sale> Execute(IEnumerable<Request> requests,
        Func<ulong, double?> price, Func<ulong, bool> remove) {
        var seen = new HashSet<ulong>();
        var sales = new List<Sale>();
        foreach (var request in requests) {
            if (request.Quantity != 1 || !seen.Add(request.Id)) continue;
            var value = price(request.Id);
            if (value is null || !double.IsFinite(value.Value) || value < 0 || value > int.MaxValue) continue;
            // Another service may have equipped, sold or removed the item since the lookup.
            if (!remove(request.Id)) continue;
            sales.Add(new(request.Id, (int)Math.Ceiling(value.Value)));
        }
        return sales;
    }

    public static int GoldToApply(IEnumerable<Sale> sales, int currentGold, int maxGold) {
        long total = 0;
        foreach (var sale in sales) total += sale.Gold;
        return (int)Math.Min(total, Math.Max(0L, (long)maxGold - currentGold));
    }
}
