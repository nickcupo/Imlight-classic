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
 * AMBIENT BAZAAR TRADER
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): what an ambient wizard sells to and buys from the
 * Bazaar on one visit, so the shelves change the way a busy 2009 Bazaar's
 * did ("players can sell their items, then other players can buy those
 * same items", Bazaar oldid 31084, 2009-07-08; Elik Silverfist has "no
 * default items ... the items that are there are the items the players
 * sold to him", oldid 29049, 2009-07-05). Pure: the caller passes the
 * shelf, the 2009 drop pool and the random source.
 *
 * Rates (our own; 2009's are unknown) - see the report for the reasoning:
 *  - One visit is 1 to 3 trades, never two on the same item.
 *  - Selling: loot a 2009 wizard picked up (the restock pool: 2009 mob
 *    drops), by the Bazaar rules' kind shares; gear and housing 1 copy,
 *    treasure cards 1-3, reagents 3-12. Never past a soft ceiling a kind
 *    (gear 5, cards 12, housing 3, reagents 60 copies held) or the cap.
 *  - Buying: anything on the shelf, gear and housing three times as
 *    likely as reagents and cards twice; gear and housing 1 copy, cards
 *    1-2, reagents 2-8. It may buy the last copy (items do disappear),
 *    but never one a real player sold in the last 30 minutes.
 *  - Balance: the chance a trade is a sale falls as players' copies
 *    (everything but the server's own restock) fill up: 75% on an empty
 *    shelf, 50% at half the target, 15% at or past 1.2x the target
 *    (60 item copies; a reagent counts a tenth). Ambient wizards add at
 *    most a few dozen copies and then buy as much as they sell.
 *  - No gold changes hands with real players: ambient wizards' purses are
 *    not tracked, so the economy only sees supply (and the tiered prices
 *    that follow the copies held).
 *
 * USAGE EXAMPLE:
 * var trades = AmbientBazaarTrader.Plan(rules, shelf, pool, random);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic.Bazaar;

namespace Imlight.Classic.Ambient;

/// <summary>An item on the Bazaar's shelf as the trader sees it.</summary>
/// <param name="Template">The template.</param>
/// <param name="Kind">Gear, treasure card, reagent or housing.</param>
/// <param name="Copies">Copies held now.</param>
/// <param name="ServerCopies">Of those, the server's own restock copies (the ledger).</param>
/// <param name="Protected">A real player sold it lately: ambient wizards leave it for players.</param>
public sealed record BazaarShelfLot(ulong Template, BazaarKind Kind, int Copies, int ServerCopies, bool Protected);

/// <summary>One trade: <see cref="Change"/> copies added (a sale to the Bazaar) or taken (a purchase, negative).</summary>
public sealed record AmbientBazaarTrade(ulong Template, BazaarKind Kind, int Change);

/// <summary>Plans an ambient wizard's Bazaar visit (see the file header).</summary>
public static class AmbientBazaarTrader {

    /// <summary>Players' item copies (gear, cards, housing; a reagent counts a tenth) the trader aims around.</summary>
    public const int PlayerCopyTarget = 60;

    /// <summary>The most trades the server's ambient wizards make in an hour, whatever the number of visits.</summary>
    public const int MaxTradesPerHour = 40;

    /// <summary>How long a real player's sale is left for players.</summary>
    public static readonly TimeSpan PlayerSaleShield = TimeSpan.FromMinutes(30);

    /// <summary>An ambient wizard in the Bazaar room visits the counter about this often (minutes, inclusive range).</summary>
    public const int VisitMinMinutes = 3, VisitMaxMinutes = 8;

    /// <summary>The soft ceiling of copies held up to which ambient wizards still sell an item of this kind.</summary>
    public static int SellCeiling(BazaarKind kind) => kind switch {
        BazaarKind.Gear => 5, BazaarKind.TreasureCard => 12, BazaarKind.Housing => 3, _ => 60,
    };

    /// <summary>Players' copies on the shelf, as the balance counts them.</summary>
    public static double PlayerCopies(IEnumerable<BazaarShelfLot> shelf)
        => shelf.Sum(lot => Math.Max(0, lot.Copies - Math.Max(0, lot.ServerCopies)) * (lot.Kind == BazaarKind.Reagent ? 0.1 : 1.0));

    /// <summary>The chance one trade is a sale, given players' copies held.</summary>
    public static double SellChance(double playerCopies)
        => Math.Clamp(0.75 - 0.5 * (playerCopies / PlayerCopyTarget), 0.15, 0.75);

    /// <summary>One visit's trades (1 to 3), or none when nothing fits.</summary>
    public static IReadOnlyList<AmbientBazaarTrade> Plan(BazaarRules rules, IReadOnlyList<BazaarShelfLot> shelf,
                                                         IReadOnlyList<BazaarCandidate> pool, Random random) {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(shelf);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(random);

        var held = shelf.GroupBy(l => l.Template).ToDictionary(g => g.Key, g => g.First());
        var trades = new List<AmbientBazaarTrade>();
        var touched = new HashSet<ulong>();
        var playerCopies = PlayerCopies(shelf);
        var count = 1 + random.Next(3);
        for (var i = 0; i < count; i++) {
            var sell = random.NextDouble() < SellChance(playerCopies);
            var trade = sell ? PickSale(rules, held, pool, touched, random) : PickPurchase(held, touched, random);
            trade ??= sell ? PickPurchase(held, touched, random) : PickSale(rules, held, pool, touched, random);
            if (trade is null) {
                continue;
            }

            trades.Add(trade);
            touched.Add(trade.Template);
            var weight = trade.Kind == BazaarKind.Reagent ? 0.1 : 1.0;
            playerCopies = Math.Max(0, playerCopies + trade.Change * weight);
        }

        return trades;
    }

    private static AmbientBazaarTrade? PickSale(BazaarRules rules, Dictionary<ulong, BazaarShelfLot> held,
                                               IReadOnlyList<BazaarCandidate> pool, HashSet<ulong> touched, Random random) {
        if (pool.Count == 0) {
            return null;
        }

        // A kind by the rules' shares, then an item of it (a few tries: some are at their ceiling).
        var kinds = pool.Select(c => c.Kind).Distinct().ToList();
        for (var attempt = 0; attempt < 6; attempt++) {
            var kind = PickKind(rules, kinds, random);
            var options = pool.Where(c => c.Kind == kind).ToList();
            if (options.Count == 0) {
                continue;
            }

            var candidate = options[random.Next(options.Count)];
            var now = held.TryGetValue(candidate.Template, out var lot) ? lot.Copies : 0;
            var room = Math.Min(SellCeiling(kind), rules.CapFor(kind)) - now;
            if (room <= 0 || touched.Contains(candidate.Template)) {
                continue;
            }

            var copies = kind switch {
                BazaarKind.TreasureCard => random.Next(1, 4),
                BazaarKind.Reagent => random.Next(3, 13),
                _ => 1,
            };

            return new AmbientBazaarTrade(candidate.Template, kind, Math.Min(copies, room));
        }

        return null;
    }

    private static BazaarKind PickKind(BazaarRules rules, List<BazaarKind> kinds, Random random) {
        var total = kinds.Sum(k => rules.Shares.GetValueOrDefault(k, 0.0));
        if (total <= 0) {
            return kinds[random.Next(kinds.Count)];
        }

        var roll = random.NextDouble() * total;
        foreach (var kind in kinds) {
            roll -= rules.Shares.GetValueOrDefault(kind, 0.0);
            if (roll < 0) {
                return kind;
            }
        }

        return kinds[^1];
    }

    private static AmbientBazaarTrade? PickPurchase(Dictionary<ulong, BazaarShelfLot> held, HashSet<ulong> touched, Random random) {
        var options = held.Values.Where(l => l.Copies > 0 && !l.Protected && !touched.Contains(l.Template)).ToList();
        if (options.Count == 0) {
            return null;
        }

        static int Weight(BazaarKind kind) => kind switch {
            BazaarKind.Gear or BazaarKind.Housing => 3, BazaarKind.TreasureCard => 2, _ => 1,
        };

        var roll = random.Next(options.Sum(l => Weight(l.Kind)));
        var pick = options[^1];
        foreach (var lot in options) {
            roll -= Weight(lot.Kind);
            if (roll < 0) {
                pick = lot;
                break;
            }
        }

        var copies = pick.Kind switch {
            BazaarKind.TreasureCard => random.Next(1, 3),
            BazaarKind.Reagent => random.Next(2, 9),
            _ => 1,
        };

        return new AmbientBazaarTrade(pick.Template, pick.Kind, -Math.Min(copies, pick.Copies));
    }

}

/// <summary>
/// The hourly trade budget shared by every ambient wizard (<see cref="AmbientBazaarTrader.MaxTradesPerHour"/>), as a
/// sliding window. Thread-safe.
/// </summary>
public sealed class AmbientTradeBudget(int perHour) {

    private readonly Queue<DateTime> _recent = new();

    /// <summary>Takes up to <paramref name="wanted"/> trades from the budget; returns how many were granted.</summary>
    public int Take(int wanted, DateTime now) {
        lock (_recent) {
            while (_recent.Count > 0 && now - _recent.Peek() >= TimeSpan.FromHours(1)) {
                _recent.Dequeue();
            }

            var granted = Math.Clamp(perHour - _recent.Count, 0, Math.Max(0, wanted));
            for (var i = 0; i < granted; i++) {
                _recent.Enqueue(now);
            }

            return granted;
        }
    }

}
