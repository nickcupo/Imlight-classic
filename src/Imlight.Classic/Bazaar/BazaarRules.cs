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
 * CLASSIC BAZAAR RULES
 * ========================================================================
 *
 * PURPOSE:
 * The 2009 Bazaar's prices (classic-data/rules/bazaar-*.yaml): what it
 * pays and asks for an item by how many copies it holds, and the plan for
 * the server's own stock (which items, how many, at what price) so a small
 * server's Bazaar is never bare.
 *
 * USAGE EXAMPLE:
 * var rules = BazaarRulesLoader.Load(path);
 * var ask = rules.BuyPrice(baseCost, copiesHeld, BazaarKind.Gear);
 * var plan = BazaarStockPlanner.Plan(rules, pool, lots, previousServerStock, random);
 *
 * NOTE:
 * Pure: templates, base costs and the random source come from the caller.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Rules;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Bazaar;

/// <summary>What kind of thing a Bazaar lot is.</summary>
public enum BazaarKind {

    Gear,
    TreasureCard,
    Reagent,
    Housing

}

/// <summary>One price tier: from this many copies held, the Bazaar pays and asks these multiples of base cost.</summary>
public sealed record BazaarTier(int Copies, double Sell, double Buy);

/// <summary>An item the server may stock, with its base cost.</summary>
public sealed record BazaarCandidate(ulong Template, BazaarKind Kind, int BaseCost);

/// <summary>One lot of a restock: hold at least <see cref="Copies"/> server copies of the template.</summary>
public sealed record BazaarLot(ulong Template, BazaarKind Kind, int Copies, double PriceFactor);

/// <summary>
/// The Bazaar's price tiers and server stock settings.
/// </summary>
public sealed class BazaarRules {

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }
    public required int MaxCopies { get; init; }
    public required int MaxReagentCopies { get; init; }
    public required ImmutableArray<BazaarTier> ItemTiers { get; init; }
    public required ImmutableArray<BazaarTier> ReagentTiers { get; init; }
    public required FrozenDictionary<BazaarKind, (int Min, int Max)> StockRanges { get; init; }
    public required FrozenDictionary<BazaarKind, double> Shares { get; init; }
    public required double PriceJitter { get; init; }
    public required string SourceFile { get; init; }

    /// <summary>The most copies the Bazaar holds of one template.</summary>
    public int CapFor(BazaarKind kind) => kind == BazaarKind.Reagent ? MaxReagentCopies : MaxCopies;

    /// <summary>The tier for <paramref name="copies"/> held: the one with the most copies not above the count.</summary>
    public BazaarTier TierFor(int copies, BazaarKind kind) {
        var tiers = kind == BazaarKind.Reagent ? ReagentTiers : ItemTiers;
        var tier = tiers[0];
        foreach (var candidate in tiers) {
            if (candidate.Copies <= copies) {
                tier = candidate;
            }
        }

        return tier;
    }

    /// <summary>What the Bazaar asks for one copy while it holds <paramref name="copies"/> (at least 1 gold).</summary>
    public int BuyPrice(int baseCost, int copies, BazaarKind kind, double factor = 1.0)
        => Math.Max(1, (int) Math.Round(Math.Max(0, baseCost) * TierFor(Math.Max(1, copies), kind).Buy * factor));

    /// <summary>What the Bazaar pays a wizard for one copy while it holds <paramref name="copies"/> before the sale.</summary>
    public int SellPrice(int baseCost, int copies, BazaarKind kind)
        => Math.Max(1, (int) Math.Ceiling(Math.Max(0, baseCost) * TierFor(Math.Max(0, copies), kind).Sell));

}

/// <summary>
/// Plans a restock of the server's shelf space.
/// </summary>
public static class BazaarStockPlanner {

    /// <summary>
    /// Picks about <paramref name="lots"/> candidates, split by the rules' shares per kind, each with a random number of
    /// copies in its kind's range and a price factor within the jitter.
    /// </summary>
    public static IReadOnlyList<BazaarLot> Plan(BazaarRules rules, IReadOnlyList<BazaarCandidate> pool, int lots, Random random) {
        var plan = new List<BazaarLot>();
        if (lots <= 0 || pool.Count == 0) {
            return plan;
        }

        foreach (var group in pool.GroupBy(candidate => candidate.Kind)) {
            var share = rules.Shares.GetValueOrDefault(group.Key);
            var wanted = (int) Math.Round(lots * share);
            if (wanted <= 0) {
                continue;
            }

            var candidates = group.ToArray();
            random.Shuffle(candidates);
            var (min, max) = rules.StockRanges.GetValueOrDefault(group.Key, (1, 1));
            foreach (var candidate in candidates.Take(wanted)) {
                var copies = Math.Min(rules.CapFor(candidate.Kind), random.Next(min, max + 1));
                var factor = 1.0 + ((random.NextDouble() * 2) - 1) * rules.PriceJitter;
                plan.Add(new BazaarLot(candidate.Template, candidate.Kind, copies, Math.Round(factor, 3)));
            }
        }

        return plan;
    }

    /// <summary>
    /// The change to one template's shelf: given the copies held now (players' and the server's), the server copies
    /// counted last time, and the server copies wanted now (0 when the template rotates out), how many copies to add
    /// (negative: remove) and how many server copies to remember. Players' copies are never removed, and copies
    /// bought since the last restock count as the server's first.
    /// </summary>
    public static (int Change, int ServerCopies) Reconcile(int held, int serverBefore, int serverWanted, int cap) {
        var server = Math.Clamp(serverBefore, 0, Math.Max(0, held));
        var players = held - server;
        var target = Math.Clamp(serverWanted, 0, Math.Max(0, cap - players));

        return (target - server, target);
    }

}

/// <summary>
/// Loads and validates Bazaar rules.
/// </summary>
public static class BazaarRulesLoader {

    private static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "provenance", "license_tag", "notes", "max_copies", "max_reagent_copies", "item_tiers",
        "reagent_tiers", "server_stock");
    private static readonly FrozenSet<string> s_tierKeys = FrozenSet.Create(StringComparer.Ordinal, "copies", "sell", "buy");
    private static readonly FrozenSet<string> s_stockKeys = FrozenSet.Create(StringComparer.Ordinal,
        "gear", "treasure_card", "reagent", "housing", "shares", "price_jitter");
    private static readonly FrozenSet<string> s_rangeKeys = FrozenSet.Create(StringComparer.Ordinal, "min", "max");
    private static readonly FrozenSet<string> s_shareKeys = FrozenSet.Create(StringComparer.Ordinal, "gear", "treasure_card", "reagent", "housing");
    private static readonly (string Key, BazaarKind Kind)[] s_kinds = [
        ("gear", BazaarKind.Gear), ("treasure_card", BazaarKind.TreasureCard), ("reagent", BazaarKind.Reagent),
        ("housing", BazaarKind.Housing),
    ];
    private static readonly Regex s_id = new(@"^bazaar-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <summary>Loads the rules at <paramref name="path"/>.</summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static BazaarRules Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the Bazaar rules do not exist"));
        }

        var diagnostics = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, diagnostics);
        if (root is not YMap map) {
            if (root is not null) {
                diagnostics.At(root, "", $"the root must be a mapping, got {root.Describe()}");
            }

            throw diagnostics.ToException();
        }

        diagnostics.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "provenance", "license_tag", "max_copies",
            "max_reagent_copies", "item_tiers", "reagent_tiers", "server_stock"]);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be bazaar-<name> and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        var maxCopies = map.Find("max_copies") is { } mc ? diagnostics.ReadInt(mc.Value, "max_copies", 1, 10_000) : null;
        var maxReagents = map.Find("max_reagent_copies") is { } mr ? diagnostics.ReadInt(mr.Value, "max_reagent_copies", 1, 10_000) : null;
        var itemTiers = ReadTiers(map, "item_tiers", diagnostics);
        var reagentTiers = ReadTiers(map, "reagent_tiers", diagnostics);

        var ranges = new Dictionary<BazaarKind, (int, int)>();
        var shares = new Dictionary<BazaarKind, double>();
        double? jitter = null;
        if (map.Find("server_stock") is { } stockEntry && diagnostics.ReadMap(stockEntry.Value, "server_stock") is { } stock) {
            diagnostics.CheckKeys(stock, "server_stock", s_stockKeys, ["gear", "treasure_card", "reagent", "housing", "shares", "price_jitter"]);
            foreach (var (key, kind) in s_kinds) {
                var rangePath = YamlTree.Join("server_stock", key);
                if (stock.Find(key) is not { } rangeEntry || diagnostics.ReadMap(rangeEntry.Value, rangePath) is not { } range) {
                    continue;
                }

                diagnostics.CheckKeys(range, rangePath, s_rangeKeys, ["min", "max"]);
                var min = range.Find("min") is { } mn ? diagnostics.ReadInt(mn.Value, YamlTree.Join(rangePath, "min"), 1) : null;
                var max = range.Find("max") is { } mx ? diagnostics.ReadInt(mx.Value, YamlTree.Join(rangePath, "max"), 1) : null;
                if (min is not null && max is not null) {
                    if (min > max) {
                        diagnostics.At(range, rangePath, "min is above max");
                    }

                    ranges[kind] = (min.Value, max.Value);
                }
            }

            if (stock.Find("shares") is { } sharesEntry && diagnostics.ReadMap(sharesEntry.Value, "server_stock.shares") is { } shareMap) {
                diagnostics.CheckKeys(shareMap, "server_stock.shares", s_shareKeys, ["gear", "treasure_card", "reagent", "housing"]);
                foreach (var (key, kind) in s_kinds) {
                    if (shareMap.Find(key) is { } value && diagnostics.ReadFraction(value.Value, YamlTree.Join("server_stock.shares", key)) is { } share) {
                        shares[kind] = share;
                    }
                }

                if (shares.Count == 4 && Math.Abs(shares.Values.Sum() - 1.0) > 0.001) {
                    diagnostics.At(shareMap, "server_stock.shares", "the shares must add up to 1");
                }
            }

            if (stock.Find("price_jitter") is { } jitterEntry) {
                jitter = diagnostics.ReadFraction(jitterEntry.Value, "server_stock.price_jitter");
                if (jitter > 0.5) {
                    diagnostics.At(jitterEntry.Value, "server_stock.price_jitter", "at most 0.5");
                }
            }
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new BazaarRules {
            Id = id!,
            Profiles = profiles,
            MaxCopies = maxCopies!.Value,
            MaxReagentCopies = maxReagents!.Value,
            ItemTiers = itemTiers,
            ReagentTiers = reagentTiers,
            StockRanges = ranges.ToFrozenDictionary(),
            Shares = shares.ToFrozenDictionary(),
            PriceJitter = jitter ?? 0,
            SourceFile = display,
        };
    }

    private static ImmutableArray<BazaarTier> ReadTiers(YMap map, string key, YamlDiagnostics diagnostics) {
        var tiers = ImmutableArray.CreateBuilder<BazaarTier>();
        if (map.Find(key) is not { } entry || diagnostics.ReadList(entry.Value, key) is not { } list) {
            return tiers.ToImmutable();
        }

        for (var i = 0; i < list.Items.Length; i++) {
            var path = YamlTree.Index(key, i);
            if (diagnostics.ReadMap(list.Items[i], path) is not { } tier) {
                continue;
            }

            diagnostics.CheckKeys(tier, path, s_tierKeys, ["copies", "sell", "buy"]);
            var copies = tier.Find("copies") is { } c ? diagnostics.ReadInt(c.Value, YamlTree.Join(path, "copies"), 0) : null;
            var sell = tier.Find("sell") is { } s ? ReadMultiplier(s.Value, YamlTree.Join(path, "sell"), diagnostics) : null;
            var buy = tier.Find("buy") is { } b ? ReadMultiplier(b.Value, YamlTree.Join(path, "buy"), diagnostics) : null;
            if (copies is null || sell is null || buy is null) {
                continue;
            }

            if (tiers.Count == 0 && copies != 0) {
                diagnostics.At(tier, path, "the first tier must be 0 copies");
            }

            if (tiers.Count > 0 && copies <= tiers[^1].Copies) {
                diagnostics.At(tier, path, "copies must rise");
            }

            tiers.Add(new BazaarTier(copies.Value, sell.Value, buy.Value));
        }

        if (tiers.Count < 2) {
            diagnostics.At(entry.Value, key, "needs at least two tiers");
        }

        return tiers.ToImmutable();
    }

    private static double? ReadMultiplier(YNode node, string path, YamlDiagnostics diagnostics) {
        if (node is YScalar scalar && double.TryParse(scalar.Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 10) {
            return value;
        }

        diagnostics.At(node, path, "must be a number from 0 to 10");

        return null;
    }

}
