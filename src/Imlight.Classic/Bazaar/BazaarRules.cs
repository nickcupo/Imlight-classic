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
 * CLASSIC (2026-10-08): server_stock.shelf (bazaar-october-2010.yaml) gives
 * the October 2010 shelf: a few hundred lots, partial rotation, gear spread
 * over level bands, boss drops rare, every 2010 reagent
 * (BazaarStockPlanner.PlanShelf). Without it the 2009 restock is unchanged.
 *
 * Last Updated: 10/08/2026
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

/// <summary>
/// An item the server may stock, with its base cost. CLASSIC (2026-10-08): <see cref="Level"/> is the level the item
/// needs (0 when unknown or none) and <see cref="Rare"/> marks an item only bosses drop (few copies on the shelf).
/// </summary>
public sealed record BazaarCandidate(ulong Template, BazaarKind Kind, int BaseCost, int Level = 0, bool Rare = false);

/// <summary>
/// One lot of a restock: hold at least <see cref="Copies"/> server copies of the template. CLASSIC (2026-10-08): a
/// <see cref="Kept"/> lot is one already on the shelf that this restock leaves alone (its copies and price as they are).
/// </summary>
public sealed record BazaarLot(ulong Template, BazaarKind Kind, int Copies, double PriceFactor, bool Kept = false);

/// <summary>CLASSIC (2026-10-08): a share of the gear shelf for items up to <see cref="MaxLevel"/>.</summary>
public sealed record BazaarLevelBand(int MaxLevel, double Share);

/// <summary>CLASSIC (2026-10-08): one reagent the shelf carries: its copies range and the chance it is in stock at all.</summary>
public sealed record BazaarReagentStock(ulong Template, string Name, int Min, int Max, double Presence);

/// <summary>
/// CLASSIC (2026-10-08): the October 2010 shelf (server_stock.shelf). A busy Bazaar of the time held what thousands of
/// players sold: a few hundred items at once, common drops in many copies and boss drops in few, every reagent that
/// players harvested, and items coming and going all day rather than all at once. Null in the 2009 rules, which keep the
/// whole-shelf restock.
/// </summary>
public sealed record BazaarShelf(
    int Lots,
    double RotateShare,
    double RareWeight,
    FrozenDictionary<BazaarKind, (int Min, int Max)> CommonRanges,
    ImmutableArray<BazaarLevelBand> GearBands,
    ImmutableArray<ulong> TreasureCardVendors,
    ImmutableArray<BazaarReagentStock> Reagents);

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

    /// <summary>CLASSIC (2026-10-08): the October 2010 shelf, or null (the 2009 whole-shelf restock).</summary>
    public BazaarShelf? Shelf { get; init; }

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
    /// CLASSIC (2026-10-08): the October 2010 restock (<see cref="BazaarRules.Shelf"/>). <paramref name="serverHeld"/>
    /// is the server's copies on the shelf now, by template. About <see cref="BazaarShelf.RotateShare"/> of those lots
    /// rotate out and the rest are kept as they are (bought copies stay bought); new lots fill each kind back to its
    /// share of <paramref name="lots"/>, gear spread over the level bands, boss drops weighted by
    /// <see cref="BazaarShelf.RareWeight"/> and given the rare copies range, other items the common one. Every listed
    /// reagent is re-rolled each restock (in stock at its presence chance).
    /// </summary>
    public static IReadOnlyList<BazaarLot> PlanShelf(BazaarRules rules, IReadOnlyList<BazaarCandidate> pool, int lots,
                                                     IReadOnlyDictionary<ulong, int> serverHeld, Random random) {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(serverHeld);
        ArgumentNullException.ThrowIfNull(random);
        var shelf = rules.Shelf ?? throw new InvalidOperationException("the rules have no shelf");
        var plan = new List<BazaarLot>();
        if (lots <= 0) {
            return plan;
        }

        var byTemplate = new Dictionary<ulong, BazaarCandidate>();
        foreach (var candidate in pool) {
            byTemplate.TryAdd(candidate.Template, candidate);
        }

        var used = new HashSet<ulong>();

        // Keep most of what the server has on the shelf; rotate the rest out.
        var kept = new Dictionary<BazaarKind, int>();
        var onShelf = serverHeld.Where(pair => pair.Value > 0 && byTemplate.TryGetValue(pair.Key, out var c) && c.Kind != BazaarKind.Reagent)
            .OrderBy(pair => pair.Key).ToArray();
        random.Shuffle(onShelf);
        var keep = (int) Math.Round(onShelf.Length * (1 - shelf.RotateShare));
        foreach (var (template, copies) in onShelf.Take(keep)) {
            var kind = byTemplate[template].Kind;
            var target = (int) Math.Round(lots * rules.Shares.GetValueOrDefault(kind));
            if (kept.GetValueOrDefault(kind) >= target) {
                continue;
            }

            kept[kind] = kept.GetValueOrDefault(kind) + 1;
            used.Add(template);
            plan.Add(new BazaarLot(template, kind, copies, 1.0, Kept: true));
        }

        foreach (var kind in new[] { BazaarKind.Gear, BazaarKind.TreasureCard, BazaarKind.Housing }) {
            var wanted = (int) Math.Round(lots * rules.Shares.GetValueOrDefault(kind)) - kept.GetValueOrDefault(kind);
            if (wanted <= 0) {
                continue;
            }

            var options = pool.Where(c => c.Kind == kind && !used.Contains(c.Template)).ToList();
            if (kind == BazaarKind.Gear && shelf.GearBands.Length > 0) {
                var lower = 0;
                var picked = 0;
                var bandWanted = new List<(int Lower, int Upper, int Count)>();
                foreach (var band in shelf.GearBands) {
                    var count = (int) Math.Round(wanted * band.Share);
                    bandWanted.Add((lower, band.MaxLevel, count));
                    lower = band.MaxLevel;
                }

                foreach (var (low, high, count) in bandWanted) {
                    var inBand = options.Where(c => !used.Contains(c.Template) && Math.Max(1, c.Level) > low && Math.Max(1, c.Level) <= high).ToList();
                    foreach (var candidate in PickWeighted(inBand, count, shelf.RareWeight, random)) {
                        plan.Add(NewLot(rules, shelf, candidate, random));
                        used.Add(candidate.Template);
                        picked++;
                    }
                }

                // A band short of items gives its place to the others.
                var rest = options.Where(c => !used.Contains(c.Template)).ToList();
                foreach (var candidate in PickWeighted(rest, wanted - picked, shelf.RareWeight, random)) {
                    plan.Add(NewLot(rules, shelf, candidate, random));
                    used.Add(candidate.Template);
                }

                continue;
            }

            foreach (var candidate in PickWeighted(options, wanted, shelf.RareWeight, random)) {
                plan.Add(NewLot(rules, shelf, candidate, random));
                used.Add(candidate.Template);
            }
        }

        foreach (var reagent in shelf.Reagents) {
            if (used.Contains(reagent.Template) || random.NextDouble() >= reagent.Presence) {
                continue;
            }

            var copies = Math.Min(rules.CapFor(BazaarKind.Reagent), random.Next(reagent.Min, reagent.Max + 1));
            plan.Add(new BazaarLot(reagent.Template, BazaarKind.Reagent, copies, Jitter(rules, random)));
            used.Add(reagent.Template);
        }

        return plan;
    }

    private static BazaarLot NewLot(BazaarRules rules, BazaarShelf shelf, BazaarCandidate candidate, Random random) {
        var (min, max) = candidate.Rare
            ? rules.StockRanges.GetValueOrDefault(candidate.Kind, (1, 1))
            : shelf.CommonRanges.GetValueOrDefault(candidate.Kind, rules.StockRanges.GetValueOrDefault(candidate.Kind, (1, 1)));
        var copies = Math.Min(rules.CapFor(candidate.Kind), random.Next(min, max + 1));

        return new BazaarLot(candidate.Template, candidate.Kind, copies, Jitter(rules, random));
    }

    private static double Jitter(BazaarRules rules, Random random)
        => Math.Round(1.0 + ((random.NextDouble() * 2) - 1) * rules.PriceJitter, 3);

    // Up to count distinct candidates, each draw weighted (rare ones by rareWeight), without replacement.
    private static List<BazaarCandidate> PickWeighted(List<BazaarCandidate> options, int count, double rareWeight, Random random) {
        var picked = new List<BazaarCandidate>();
        if (count <= 0 || options.Count == 0) {
            return picked;
        }

        // Efraimidis-Spirakis: key = u^(1/w), take the largest keys.
        return options
            .Select(c => (Candidate: c, Key: Math.Pow(random.NextDouble(), 1.0 / (c.Rare ? Math.Max(0.01, rareWeight) : 1.0))))
            .OrderByDescending(x => x.Key).ThenBy(x => x.Candidate.Template)
            .Take(count).Select(x => x.Candidate).ToList();
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
        "gear", "treasure_card", "reagent", "housing", "shares", "price_jitter", "shelf");
    // CLASSIC (2026-10-08): the October 2010 shelf.
    private static readonly FrozenSet<string> s_shelfKeys = FrozenSet.Create(StringComparer.Ordinal,
        "lots", "rotate_share", "rare_weight", "common", "gear_level_bands", "treasure_card_vendors", "reagents");
    private static readonly FrozenSet<string> s_commonKeys = FrozenSet.Create(StringComparer.Ordinal, "gear", "treasure_card", "housing");
    private static readonly FrozenSet<string> s_bandKeys = FrozenSet.Create(StringComparer.Ordinal, "max_level", "share");
    private static readonly FrozenSet<string> s_reagentKeys = FrozenSet.Create(StringComparer.Ordinal,
        "template", "name", "type", "rank", "min", "max", "presence", "source");
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
        BazaarShelf? shelf = null;
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

            if (stock.Find("shelf") is { } shelfEntry) {
                shelf = ReadShelf(shelfEntry.Value, diagnostics);
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
            Shelf = shelf,
        };
    }

    // CLASSIC (2026-10-08): server_stock.shelf, the October 2010 shelf.
    private static BazaarShelf? ReadShelf(YNode node, YamlDiagnostics diagnostics) {
        const string path = "server_stock.shelf";
        if (diagnostics.ReadMap(node, path) is not { } shelf) {
            return null;
        }

        diagnostics.CheckKeys(shelf, path, s_shelfKeys, ["lots", "rotate_share", "rare_weight", "common", "gear_level_bands", "reagents"]);
        var lots = shelf.Find("lots") is { } l ? diagnostics.ReadInt(l.Value, YamlTree.Join(path, "lots"), 1, 5_000) : null;
        var rotate = shelf.Find("rotate_share") is { } r ? diagnostics.ReadFraction(r.Value, YamlTree.Join(path, "rotate_share")) : null;
        double? rareWeight = null;
        if (shelf.Find("rare_weight") is { } w) {
            rareWeight = ReadMultiplier(w.Value, YamlTree.Join(path, "rare_weight"), diagnostics);
            if (rareWeight is <= 0) {
                diagnostics.At(w.Value, YamlTree.Join(path, "rare_weight"), "must be above 0");
            }
        }

        var common = new Dictionary<BazaarKind, (int, int)>();
        if (shelf.Find("common") is { } commonEntry && diagnostics.ReadMap(commonEntry.Value, YamlTree.Join(path, "common")) is { } commonMap) {
            var commonPath = YamlTree.Join(path, "common");
            diagnostics.CheckKeys(commonMap, commonPath, s_commonKeys, ["gear", "treasure_card", "housing"]);
            foreach (var (key, kind) in s_kinds) {
                if (commonMap.Find(key) is { } entry && ReadRange(entry.Value, YamlTree.Join(commonPath, key), diagnostics) is { } range) {
                    common[kind] = range;
                }
            }
        }

        var bands = ImmutableArray.CreateBuilder<BazaarLevelBand>();
        var bandsPath = YamlTree.Join(path, "gear_level_bands");
        if (shelf.Find("gear_level_bands") is { } bandsEntry && diagnostics.ReadList(bandsEntry.Value, bandsPath) is { } bandList) {
            for (var i = 0; i < bandList.Items.Length; i++) {
                var bandPath = YamlTree.Index(bandsPath, i);
                if (diagnostics.ReadMap(bandList.Items[i], bandPath) is not { } band) {
                    continue;
                }

                diagnostics.CheckKeys(band, bandPath, s_bandKeys, ["max_level", "share"]);
                var max = band.Find("max_level") is { } m ? diagnostics.ReadInt(m.Value, YamlTree.Join(bandPath, "max_level"), 1, 200) : null;
                var share = band.Find("share") is { } sh ? diagnostics.ReadFraction(sh.Value, YamlTree.Join(bandPath, "share")) : null;
                if (max is null || share is null) {
                    continue;
                }

                if (bands.Count > 0 && max <= bands[^1].MaxLevel) {
                    diagnostics.At(band, bandPath, "max_level must rise");
                }

                bands.Add(new BazaarLevelBand(max.Value, share.Value));
            }

            if (bands.Count > 0 && Math.Abs(bands.Sum(b => b.Share) - 1.0) > 0.001) {
                diagnostics.At(bandsEntry.Value, bandsPath, "the band shares must add up to 1");
            }
        }

        var vendors = ImmutableArray.CreateBuilder<ulong>();
        var vendorsPath = YamlTree.Join(path, "treasure_card_vendors");
        if (shelf.Find("treasure_card_vendors") is { } vendorsEntry && diagnostics.ReadList(vendorsEntry.Value, vendorsPath) is { } vendorList) {
            for (var i = 0; i < vendorList.Items.Length; i++) {
                if (diagnostics.ReadInt(vendorList.Items[i], YamlTree.Index(vendorsPath, i), 1) is { } vendor) {
                    vendors.Add((ulong) vendor);
                }
            }
        }

        var reagents = ImmutableArray.CreateBuilder<BazaarReagentStock>();
        var reagentsPath = YamlTree.Join(path, "reagents");
        var seen = new HashSet<ulong>();
        if (shelf.Find("reagents") is { } reagentsEntry && diagnostics.ReadList(reagentsEntry.Value, reagentsPath) is { } reagentList) {
            for (var i = 0; i < reagentList.Items.Length; i++) {
                var itemPath = YamlTree.Index(reagentsPath, i);
                if (diagnostics.ReadMap(reagentList.Items[i], itemPath) is not { } item) {
                    continue;
                }

                diagnostics.CheckKeys(item, itemPath, s_reagentKeys, ["template", "name", "min", "max", "presence"]);
                var template = item.Find("template") is { } t ? diagnostics.ReadInt(t.Value, YamlTree.Join(itemPath, "template"), 1) : null;
                var name = item.Find("name") is { } n ? diagnostics.ReadString(n.Value, YamlTree.Join(itemPath, "name")) : null;
                var min = item.Find("min") is { } mn ? diagnostics.ReadInt(mn.Value, YamlTree.Join(itemPath, "min"), 1) : null;
                var max = item.Find("max") is { } mx ? diagnostics.ReadInt(mx.Value, YamlTree.Join(itemPath, "max"), 1) : null;
                var presence = item.Find("presence") is { } p ? diagnostics.ReadFraction(p.Value, YamlTree.Join(itemPath, "presence")) : null;
                if (template is null || name is null || min is null || max is null || presence is null) {
                    continue;
                }

                if (min > max) {
                    diagnostics.At(item, itemPath, "min is above max");
                }

                if (!seen.Add((ulong) template)) {
                    diagnostics.At(item, itemPath, $"template {template} is listed twice");
                }

                reagents.Add(new BazaarReagentStock((ulong) template, name, min.Value, max.Value, presence.Value));
            }
        }

        if (lots is null || rotate is null || rareWeight is null) {
            return null;
        }

        return new BazaarShelf(lots.Value, rotate.Value, rareWeight.Value, common.ToFrozenDictionary(), bands.ToImmutable(),
            vendors.ToImmutable(), reagents.ToImmutable());
    }

    private static (int Min, int Max)? ReadRange(YNode node, string path, YamlDiagnostics diagnostics) {
        if (diagnostics.ReadMap(node, path) is not { } range) {
            return null;
        }

        diagnostics.CheckKeys(range, path, s_rangeKeys, ["min", "max"]);
        var min = range.Find("min") is { } mn ? diagnostics.ReadInt(mn.Value, YamlTree.Join(path, "min"), 1) : null;
        var max = range.Find("max") is { } mx ? diagnostics.ReadInt(mx.Value, YamlTree.Join(path, "max"), 1) : null;
        if (min is null || max is null) {
            return null;
        }

        if (min > max) {
            diagnostics.At(range, path, "min is above max");
        }

        return (min.Value, max.Value);
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
