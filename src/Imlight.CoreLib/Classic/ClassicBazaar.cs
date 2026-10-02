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
 * CLASSIC BAZAAR (RUNTIME)
 * ========================================================================
 *
 * PURPOSE:
 * The 2009 Bazaar's prices (classic-data/rules/bazaar-*.yaml) for every
 * sale and purchase, and the server's own stock: on a timer ([Classic]
 * BazaarRestockMinutes) it puts 2009 items on the shelves (gear, treasure
 * cards, reagents and housing items that 2009 mobs dropped, from
 * classic-data's mob rewards) and rotates its old ones out. What players
 * sold stays and sells as before.
 *
 * NOTE:
 * [Classic] BazaarStocked (on by default, owner request) and
 * BazaarStockPerRestock change at the next restock. The ledger of the
 * server's own copies is a database document (BazaarServerStock), so a
 * restart keeps telling them from players' copies.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Classic;
using Imlight.Classic.Bazaar;
using Imlight.Common;
using Imlight.CoreLib.Classic.Admin;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// The Bazaar's 2009 prices and the server's stock.
/// </summary>
public static class ClassicBazaar {

    private static BazaarRules? s_rules;
    private static Timer? s_timer;
    private static IReadOnlyList<BazaarCandidate> s_pool = [];
    private static readonly Dictionary<ulong, double> s_priceFactors = [];
    private static DateTime s_lastRestockUtc;
    private static DateTime s_nextRestockUtc;
    private static string s_lastResult = "not yet";

    /// <summary>The loaded rules, or null (stock Imlight prices).</summary>
    public static BazaarRules? Rules => s_rules;

    /// <summary>Loads the rules and the item pool, and starts the restock timer.</summary>
    public static void Initialize(string? classicDataRoot, string profileId) {
        if (s_rules is not null || classicDataRoot is null) {
            return;
        }

        var directory = Path.Combine(classicDataRoot, "rules");
        foreach (var path in Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "bazaar-*.yaml").Order() : Enumerable.Empty<string>()) {
            try {
                var rules = BazaarRulesLoader.Load(path);
                if (rules.Profiles.Contains(profileId, StringComparer.Ordinal)) {
                    s_rules = rules;
                    break;
                }
            }
            catch (ClassicDataException ex) {
                Logger.Error("Classic Bazaar: {Path} is invalid; the Bazaar keeps stock Imlight prices: {Error}",
                    Logger.Args(path, ex.Message));

                return;
            }
        }

        if (s_rules is null) {
            Logger.Information("Classic Bazaar: no Bazaar rules for profile {Profile}.", Logger.Args(profileId));

            return;
        }

        s_pool = BuildPool();
        Logger.Information("Classic Bazaar: {File}; {Count} 2009 items may be stocked ({Gear} gear, {Cards} treasure cards, "
            + "{Reagents} reagents, {Housing} housing).",
            Logger.Args(s_rules.SourceFile, s_pool.Count, s_pool.Count(c => c.Kind == BazaarKind.Gear),
                s_pool.Count(c => c.Kind == BazaarKind.TreasureCard), s_pool.Count(c => c.Kind == BazaarKind.Reagent),
                s_pool.Count(c => c.Kind == BazaarKind.Housing)));

        // The first restock waits for the database to be up; the timer re-reads the interval each time.
        s_nextRestockUtc = DateTime.UtcNow.AddSeconds(30);
        s_timer = new Timer(_ => TickRestock(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
        AdminDashboard.AddSection("Bazaar", DashboardState);
    }

    /// <summary>The kind of a template, or null when the Bazaar does not take it (not for auction, Crowns-only).</summary>
    public static BazaarKind? KindOf(CoreTemplate? template) {
        switch (template) {
            case SpellTemplate:
                return BazaarKind.TreasureCard;
            case ReagentItemTemplate:
                return BazaarKind.Reagent;
            case WizItemTemplate item:
                var adjectives = item.m_adjectiveList ?? [];
                if (adjectives.Any(a => a is "FLAG_NoAuction" or "FLAG_CrownsOnly" or "FLAG_NoTrade" or "FLAG_NoSell")) {
                    return null;
                }

                return adjectives.Any(a => a.StartsWith("Housing", StringComparison.OrdinalIgnoreCase)
                        || a is "Furniture" or "Decoration" or "WallHanging" or "OutdoorItem" or "Tile" or "Wallpaper")
                    ? BazaarKind.Housing
                    : BazaarKind.Gear;
            default:
                return null;
        }
    }

    /// <summary>The base cost the tiers multiply.</summary>
    public static int BaseCostOf(CoreTemplate? template) => template switch {
        ReagentItemTemplate reagent => (int) Math.Max(0, reagent.m_baseCost),
        WizItemTemplate item => (int) Math.Max(0, item.m_baseCost),
        SpellTemplate spell => (int) Math.Max(0, spell.m_baseCost),
        _ => 0,
    };

    /// <summary>
    /// The prices an entry should carry while the Bazaar holds <paramref name="copies"/> of it (a server lot keeps its
    /// price variation).
    /// </summary>
    public static (int Buy, int Sell) PricesFor(ulong templateId, int copies) {
        var template = CoreObjectFactory.GetCoreTemplate(templateId);
        var kind = KindOf(template) ?? BazaarKind.Gear;
        var baseCost = BaseCostOf(template);
        double factor;
        lock (s_priceFactors) {
            factor = s_priceFactors.GetValueOrDefault(templateId, 1.0);
        }

        return (s_rules!.BuyPrice(baseCost, copies, kind, factor), s_rules.SellPrice(baseCost, copies, kind));
    }

    /// <summary>Restocks now (the dashboard and tests); normally the timer does it.</summary>
    public static string RestockNow() {
        if (s_rules is null) {
            return "no Bazaar rules";
        }

        try {
            var result = Restock(s_rules, ClassicSettings.BazaarStocked ? ClassicSettings.BazaarStockPerRestock : 0, Random.Shared);
            s_lastRestockUtc = DateTime.UtcNow;
            s_lastResult = result;
            Logger.Information("Classic Bazaar: restocked: {Result}.", Logger.Args(result));

            return result;
        }
        catch (Exception ex) {
            s_lastResult = "failed: " + ex.Message;
            Logger.Error("Classic Bazaar: restock failed: {Error}", Logger.Args(ex));

            return s_lastResult;
        }
    }

    private static void TickRestock() {
        // The interval is read each time, so a change on the dashboard applies to the restock already scheduled.
        if (s_lastRestockUtc != default) {
            s_nextRestockUtc = s_lastRestockUtc.AddMinutes(Math.Max(1, ClassicSettings.BazaarRestockMinutes));
        }

        if (DateTime.UtcNow < s_nextRestockUtc) {
            return;
        }

        RestockNow();
    }

    private static string Restock(BazaarRules rules, int lots, Random random) {
        var plan = BazaarStockPlanner.Plan(rules, s_pool, lots, random).ToDictionary(lot => lot.Template);
        var ledger = BazaarServerStockCollection.Load().Copies
            .Select(pair => (Ok: ulong.TryParse(pair.Key, out var id), Id: id, pair.Value))
            .Where(entry => entry.Ok).ToDictionary(entry => entry.Id, entry => entry.Value);

        var upserts = new List<AuctionHouseEntry>();
        var removals = new List<ulong>();
        var newLedger = new Dictionary<ulong, int>();
        int added = 0, removed = 0;
        lock (AuctionHouseCollection.Lock) {
            var held = AuctionHouseCollection.GetAllAuctionHouseEntries().GroupBy(entry => entry.m_templateID.Full)
                .ToDictionary(group => group.Key, group => group.Sum(entry => entry.m_numForSale));
            lock (s_priceFactors) {
                foreach (var lot in plan.Values) {
                    s_priceFactors[lot.Template] = lot.PriceFactor;
                }
            }

            foreach (var template in plan.Keys.Union(ledger.Keys)) {
                var heldNow = held.GetValueOrDefault(template);
                var wanted = plan.TryGetValue(template, out var lot) ? lot.Copies : 0;
                var kind = lot?.Kind ?? KindOf(CoreObjectFactory.GetCoreTemplate(template)) ?? BazaarKind.Gear;
                var (change, server) = BazaarStockPlanner.Reconcile(heldNow, ledger.GetValueOrDefault(template), wanted,
                    rules.CapFor(kind));
                if (server > 0) {
                    newLedger[template] = server;
                }

                if (change == 0) {
                    continue;
                }

                var copies = heldNow + change;
                added += Math.Max(0, change);
                removed += Math.Max(0, -change);
                if (copies <= 0) {
                    removals.Add(template);
                    continue;
                }

                var (buy, sell) = PricesFor(template, copies);
                upserts.Add(new AuctionHouseEntry {
                    m_templateID = (GID) template,
                    m_numForSale = copies,
                    m_buyPrice = buy,
                    m_sellPrice = sell,
                });
            }

            AuctionHouseCollection.ApplyStockChanges(upserts, removals);
        }

        BazaarServerStockCollection.Save(newLedger, DateTime.UtcNow);

        return $"{plan.Count} server lots, {added} copies added, {removed} rotated out, {newLedger.Count} templates of server stock";
    }

    // Every template a 2009 mob dropped (classic-data mob rewards, with the client loot-table fallbacks), which the
    // Bazaar takes and which has a price.
    private static IReadOnlyList<BazaarCandidate> BuildPool() {
        var rewards = ClassicProgression.MobRewards;
        if (rewards is null) {
            return [];
        }

        var templates = new HashSet<ulong>();
        foreach (var mob in rewards.Mobs) {
            templates.UnionWith(mob.Items.Select(entry => entry.Template));
            templates.UnionWith(mob.TreasureCards.Select(entry => entry.Template));
            templates.UnionWith(mob.Reagents.Select(entry => entry.Template));
        }

        foreach (var list in rewards.TreasureCardFallback.Values.Concat(rewards.ReagentFallback.Values)) {
            templates.UnionWith(list.Select(entry => entry.Template));
        }

        var pool = new List<BazaarCandidate>();
        foreach (var id in templates) {
            var template = CoreObjectFactory.GetCoreTemplate(id);
            if (KindOf(template) is { } kind && BaseCostOf(template) > 0) {
                pool.Add(new BazaarCandidate(id, kind, BaseCostOf(template)));
            }
        }

        return pool;
    }

    private static object DashboardState() => new {
        stocked = ClassicSettings.BazaarStocked ? "on" : "off",
        rules = s_rules?.SourceFile,
        pool = s_pool.Count,
        lastRestock = s_lastRestockUtc == default ? "never" : s_lastRestockUtc.ToString("u"),
        lastResult = s_lastResult,
        nextRestock = s_nextRestockUtc.ToString("u"),
        entries = AuctionHouseCollection.GetAllAuctionHouseEntries().Count,
    };

}
