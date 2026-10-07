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
 * CLASSIC (2026-10-04): ambient wizards in the Bazaar room also trade
 * (AmbientVisit, planned by AmbientBazaarTrader): their sales are
 * players' copies (kept by the restock, never rotated out) and their
 * purchases take copies off the shelf, so the stock changes between
 * restocks as a populated 2009 Bazaar's did. A real player's sale is left
 * alone for AmbientBazaarTrader.PlayerSaleShield (NoteRealSale).
 *
 * NOTE:
 * [Classic] BazaarStocked (on by default, owner request) and
 * BazaarStockPerRestock change at the next restock. The ledger of the
 * server's own copies is a database document (BazaarServerStock), so a
 * restart keeps telling them from players' copies.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
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
using Imlight.Classic.Ambient;
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

    private static volatile BazaarRules? s_rules;
    private static readonly object s_initializeGate = new(); // CLASSIC: a later initialization cannot bypass unfinished quote correction.
    private static Timer? s_timer;
    private static IReadOnlyList<BazaarCandidate> s_pool = [];
    private static readonly Dictionary<ulong, double> s_priceFactors = [];
    private static DateTime s_lastRestockUtc;
    private static DateTime s_nextRestockUtc;
    private static string s_lastResult = "not yet";

    // CLASSIC (2026-10-04): ambient wizards' trading (AmbientVisit).
    private static readonly AmbientTradeBudget s_ambientBudget = new(AmbientBazaarTrader.MaxTradesPerHour);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, DateTime> s_realSales = new();
    private static readonly object s_ambientStatsGate = new();
    private static int s_ambientVisits, s_ambientSold, s_ambientBought;
    private static readonly Queue<string> s_ambientLast = new();

    /// <summary>The loaded rules, or null (stock Imlight prices).</summary>
    public static BazaarRules? Rules => s_rules;

    /// <summary>Loads the rules and the item pool, and starts the restock timer.</summary>
    public static void Initialize(string? classicDataRoot, string profileId) {
        lock (s_initializeGate) {
            InitializeUnlocked(classicDataRoot, profileId);
        }
    }

    private static void InitializeUnlocked(string? classicDataRoot, string profileId) {
        if (s_rules is not null || classicDataRoot is null) {
            return;
        }

        BazaarRules? selectedRules = null;
        var directory = Path.Combine(classicDataRoot, "rules");
        foreach (var path in Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "bazaar-*.yaml").Order() : Enumerable.Empty<string>()) {
            try {
                var rules = BazaarRulesLoader.Load(path);
                if (rules.Profiles.Contains(profileId, StringComparer.Ordinal)) {
                    selectedRules = rules;
                    break;
                }
            }
            catch (ClassicDataException ex) {
                Logger.Error("Classic Bazaar: {Path} is invalid; the Bazaar keeps stock Imlight prices: {Error}",
                    Logger.Args(path, ex.Message));

                return;
            }
        }

        if (selectedRules is null) {
            Logger.Information("Classic Bazaar: no Bazaar rules for profile {Profile}.", Logger.Args(profileId));

            return;
        }

        // CLASSIC: refresh the one historical base correction before any requests can quote persisted stock.
        // This does not restock: copies, current price factors and the server/player ownership ledger stay untouched.
        RepriceGlacialTreasure(profileId, selectedRules);

        s_pool = BuildPool();
        // CLASSIC: only acknowledged correction publishes a ready Bazaar. A later initialization rereads
        // persisted quotes after failure, rather than seeing rules and skipping the unfinished correction.
        s_rules = selectedRules;
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

    private static void RepriceGlacialTreasure(string profileId, BazaarRules rules) {
        if (!ClassicRuntime.IsInitialized || !ClassicRuntime.IsActive || ClassicRuntime.Rules.Profile.Id != profileId
                || !ClassicSpellTemplates.HasHistoricalTreasureBase(profileId, ClassicProgression.TreasurePrices)) {
            return;
        }

        var id = ClassicSpellTemplates.GlacialTreasureId;
        var template = CoreObjectFactory.GetCoreTemplate(id) as SpellTemplate;
        var path = CoreObjectFactory.GetTemplatePath(id);
        lock (AuctionHouseCollection.Lock) {
            var entries = AuctionHouseCollection.GetAllAuctionHouseEntries().Where(entry => entry.m_templateID.Full == id).ToList();
            var held = entries.Sum(entry => entry.m_numForSale);
            var (factor, defaulted) = CurrentGlacialTreasureFactor();

            if (entries.Count == 0 || !TryGlacialTreasureQuote(profileId, ClassicProgression.TreasurePrices,
                    id, template, path, held, rules, factor, out var quote)) {
                Logger.Information("Classic Bazaar: historical Glacial Shield base correction changed 0 templates.");
                return;
            }

            try {
                var changed = AuctionHouseCollection.ApplyPriceChanges(id, held, quote.Buy, quote.Sell);
                Logger.Information("Classic Bazaar: historical Glacial Shield base correction changed {Count} templates; {Copies} copies retained; current factor {Factor} ({Policy}).",
                    Logger.Args(changed ? 1 : 0, held, factor, defaulted ? "existing startup default; prior-process factor was not persisted" : "retained in-memory factor"));
            }
            catch (Exception ex) {
                // Cache prices change only after an acknowledged save. A failed/ambiguous save aborts startup;
                // the next initialization rereads the persisted quote instead of serving a stale price.
                Logger.Error("Classic Bazaar: Glacial Shield quote correction failed; startup stopped: {Error}", Logger.Args(ex));
                throw;
            }
        }
    }

    internal static (double Factor, bool Defaulted) CurrentGlacialTreasureFactor() {
        lock (s_priceFactors) {
            // CLASSIC: this existing dictionary is not persisted. Retain its current factor when present;
            // after restart, use the existing 1.0 policy. Integer quotes cannot recover the prior factor
            // exactly: inverse reconstruction can change the corrected Math.Round result by one gold.
            return s_priceFactors.TryGetValue(ClassicSpellTemplates.GlacialTreasureId, out var factor)
                ? (factor, false) : (1.0, true);
        }
    }

    // CLASSIC: dated evidence corrects the card's base only. The accepted 2014 fallback resale tiers and
    // current in-memory lot factor (or existing startup default) remain policy; this is not new historical
    // proof for a Bazaar buy/sell multiplier. No prior-process jitter is inferred from rounded saved quotes.
    internal static bool TryGlacialTreasureQuote(string profileId, Imlight.Classic.Rules.TreasurePrices? prices,
            ulong templateId, SpellTemplate? template, string? path, int copies, BazaarRules rules, double factor,
            out (int Buy, int Sell) quote) {
        quote = default;
        if (!ClassicSpellTemplates.HasHistoricalTreasureBase(profileId, prices)
                || !rules.Profiles.Contains(profileId, StringComparer.Ordinal)
                || templateId != ClassicSpellTemplates.GlacialTreasureId || path != ClassicSpellTemplates.GlacialTreasurePath
                || template is null || template.GetType() != typeof(SpellTemplate)
                || template.m_name != ClassicSpellTemplates.GlacialTreasureName
                || template.m_baseCost != ClassicSpellTemplates.GlacialHistoricalBase || copies <= 0
                || !double.IsFinite(factor) || factor <= 0) {
            return false;
        }

        quote = (rules.BuyPrice(BaseCostOf(template), copies, BazaarKind.TreasureCard, factor),
            rules.SellPrice(BaseCostOf(template), copies, BazaarKind.TreasureCard));
        return true;
    }

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

    /// <summary>CLASSIC (2026-10-04): a real player sold <paramref name="templateId"/>: ambient wizards leave it for players a while.</summary>
    public static void NoteRealSale(ulong templateId) {
        if (s_rules is null) {
            return;
        }

        var now = DateTime.UtcNow;
        s_realSales[templateId] = now;
        foreach (var (template, at) in s_realSales) {
            if (now - at > AmbientBazaarTrader.PlayerSaleShield) {
                s_realSales.TryRemove(template, out _);
            }
        }
    }

    /// <summary>
    /// CLASSIC (2026-10-04): one ambient wizard's visit to the counter: 1 to 3 sales or purchases (AmbientBazaarTrader),
    /// within the server-wide hourly budget. Blocking (database); call off the actors. Returns what it did.
    /// </summary>
    public static string AmbientVisit(string who, Random random) {
        if (s_rules is not { } rules || !ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.Bazaar)) {
            return "no Bazaar";
        }

        var ledger = BazaarServerStockCollection.Load().Copies
            .Select(pair => (Ok: ulong.TryParse(pair.Key, out var id), Id: id, pair.Value))
            .Where(entry => entry.Ok).ToDictionary(entry => entry.Id, entry => entry.Value);
        var now = DateTime.UtcNow;
        var done = new List<string>();
        int sold = 0, bought = 0;
        lock (AuctionHouseCollection.Lock) {
            var entries = AuctionHouseCollection.GetAllAuctionHouseEntries();
            var shelf = entries.GroupBy(entry => entry.m_templateID.Full).Select(group => {
                var template = group.Key;
                var kind = KindOf(CoreObjectFactory.GetCoreTemplate(template)) ?? BazaarKind.Gear;
                var shielded = s_realSales.TryGetValue(template, out var at) && now - at < AmbientBazaarTrader.PlayerSaleShield;
                return new BazaarShelfLot(template, kind, group.Sum(entry => entry.m_numForSale), ledger.GetValueOrDefault(template), shielded);
            }).ToList();
            var plan = AmbientBazaarTrader.Plan(rules, shelf, s_pool, random);
            var granted = s_ambientBudget.Take(plan.Count, now);
            if (granted == 0) {
                return plan.Count == 0 ? "nothing to trade" : "hourly budget spent";
            }

            var held = shelf.ToDictionary(lot => lot.Template, lot => lot.Copies);
            var upserts = new List<AuctionHouseEntry>();
            var removals = new List<ulong>();
            foreach (var trade in plan.Take(granted)) {
                var copies = Math.Max(0, held.GetValueOrDefault(trade.Template) + trade.Change);
                held[trade.Template] = copies;
                if (copies == 0) {
                    removals.Add(trade.Template);
                }
                else {
                    var (buy, sell) = PricesFor(trade.Template, copies);
                    upserts.Add(new AuctionHouseEntry {
                        m_templateID = (GID) trade.Template, m_numForSale = copies, m_buyPrice = buy, m_sellPrice = sell,
                    });
                }

                if (trade.Change > 0) {
                    sold += trade.Change;
                }
                else {
                    bought -= trade.Change;
                }

                done.Add($"{(trade.Change > 0 ? "sold" : "bought")} {Math.Abs(trade.Change)} x {trade.Template} ({trade.Kind}, now {copies})");
            }

            AuctionHouseCollection.ApplyStockChanges(upserts, removals);
        }

        var result = $"{who}: {string.Join("; ", done)}";
        lock (s_ambientStatsGate) {
            s_ambientVisits++;
            s_ambientSold += sold;
            s_ambientBought += bought;
            s_ambientLast.Enqueue($"{now:u} {result}");
            while (s_ambientLast.Count > 10) {
                s_ambientLast.Dequeue();
            }
        }

        Logger.Information("Ambient Bazaar: {Result}.", Logger.Args(result));
        return result;
    }

    private static object DashboardState() => new {
        stocked = ClassicSettings.BazaarStocked ? "on" : "off",
        rules = s_rules?.SourceFile,
        pool = s_pool.Count,
        lastRestock = s_lastRestockUtc == default ? "never" : s_lastRestockUtc.ToString("u"),
        lastResult = s_lastResult,
        nextRestock = s_nextRestockUtc.ToString("u"),
        entries = AuctionHouseCollection.GetAllAuctionHouseEntries().Count,
        ambient = AmbientDashboard(),
    };

    private static object AmbientDashboard() {
        lock (s_ambientStatsGate) {
            return new {
                visits = s_ambientVisits, copiesSold = s_ambientSold, copiesBought = s_ambientBought,
                tradesPerHourCap = AmbientBazaarTrader.MaxTradesPerHour, last = s_ambientLast.ToArray(),
            };
        }
    }

}
