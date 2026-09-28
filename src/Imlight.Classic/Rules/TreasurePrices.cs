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
 * TREASURE PRICES
 * ========================================================================
 *
 * PURPOSE:
 * What a library charged for each treasure card at the profile's cutoff
 * (classic-data/rules/treasure-prices-*.yaml, named by rules.treasure_prices).
 *
 * USAGE EXAMPLE:
 * var prices = TreasurePricesLoader.Load(path);
 * var gold = TreasurePrices.PriceOf(prices, "Fire Shield TC", templateBaseCost: 75, buyMultiplier: 2); // 150
 *
 * NOTE:
 * The client shows a library card at its template's m_baseCost times
 * PriceModifiers.xml's treasure buy multiplier. A card the table lists costs
 * the table's price; any other card costs what the client shows.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.RegularExpressions;
using Imlight.Classic.Spells;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>
/// A profile's library treasure-card prices.
/// </summary>
public sealed class TreasurePrices {

    /// <summary>
    /// The suffix of a treasure card's template name ("Fire Shield TC").
    /// </summary>
    public const string TreasureCardSuffix = " TC";

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }

    /// <summary>
    /// Gold for one card, by card name without the " TC" suffix, ignoring case.
    /// </summary>
    public required FrozenDictionary<string, int> ByName { get; init; }

    public required string SourceFile { get; init; }

    /// <summary>
    /// The table's price for a card, by its name or its template name.
    /// </summary>
    /// <param name="spellName">"Fire Shield" or "Fire Shield TC".</param>
    /// <returns>The price, or null when the table does not list the card.</returns>
    public int? Find(string? spellName) {
        if (string.IsNullOrEmpty(spellName)) {
            return null;
        }

        var name = spellName.EndsWith(TreasureCardSuffix, StringComparison.Ordinal) ? spellName[..^TreasureCardSuffix.Length] : spellName;

        return ByName.TryGetValue(name, out var price) ? price : null;
    }

    /// <summary>
    /// What a library charges for one card: the table's price when <paramref name="prices"/> lists it, else the price
    /// the client shows, the template's base cost times the client's treasure buy multiplier.
    /// </summary>
    /// <param name="prices">The profile's table, or null for none.</param>
    /// <param name="spellName">The card's name or template name.</param>
    /// <param name="templateBaseCost">The card template's m_baseCost.</param>
    /// <param name="buyMultiplier">PriceModifiers.xml's m_treasureMods.m_buyPriceMultiplier (1 when the file has none).</param>
    /// <returns>The gold for one card, at least 0.</returns>
    public static int PriceOf(TreasurePrices? prices, string? spellName, int templateBaseCost, int buyMultiplier)
        => prices?.Find(spellName) ?? ClientPrice(templateBaseCost, buyMultiplier);

    /// <summary>
    /// The price the client shows for a card: its base cost times the buy multiplier.
    /// </summary>
    public static int ClientPrice(int templateBaseCost, int buyMultiplier)
        => (int) Math.Clamp((long) Math.Max(0, templateBaseCost) * Math.Max(1, buyMultiplier), 0, int.MaxValue);

}

/// <summary>
/// Loads and validates library treasure-card prices.
/// </summary>
public static class TreasurePricesLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "provenance", "license_tag", "notes", "cards");
    internal static readonly FrozenSet<string> s_cardKeys = FrozenSet.Create(StringComparer.Ordinal, "name", "school", "price");
    private static readonly string[] s_schools = ["balance", "death", "fire", "ice", "life", "myth", "storm"];
    private static readonly Regex s_id = new(@"^treasure-prices-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the prices at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static TreasurePrices Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the treasure prices do not exist"));
        }

        var diagnostics = new YamlDiagnostics();
        var root = YamlTree.Parse(fullPath, display, diagnostics);
        if (root is null) {
            throw diagnostics.ToException();
        }

        if (root is not YMap map) {
            diagnostics.At(root, "", $"the root must be a mapping, got {root.Describe()}");

            throw diagnostics.ToException();
        }

        diagnostics.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "provenance", "license_tag", "cards"]);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be treasure-prices-<name> and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        if (map.Find("license_tag") is { } license) {
            _ = diagnostics.ReadEnum(license.Value, "license_tag", ClassicSpellSchema.LicenseTags);
        }

        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (map.Find("cards") is { } cardsEntry && diagnostics.ReadList(cardsEntry.Value, "cards") is { } list) {
            if (list.Items.IsEmpty) {
                diagnostics.At(list, "cards", "needs at least one card");
            }

            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("cards", i);
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } card) {
                    continue;
                }

                diagnostics.CheckKeys(card, keyPath, s_cardKeys, ["name", "price"]);
                var name = card.Find("name") is { } n ? diagnostics.ReadString(n.Value, YamlTree.Join(keyPath, "name")) : null;
                var price = card.Find("price") is { } p ? diagnostics.ReadInt(p.Value, YamlTree.Join(keyPath, "price"), 1, 1_000_000) : null;
                if (card.Find("school") is { } s) {
                    _ = diagnostics.ReadEnum(s.Value, YamlTree.Join(keyPath, "school"), s_schools);
                }

                if (name is null || price is null) {
                    continue;
                }

                if (!byName.TryAdd(name, price.Value)) {
                    diagnostics.At(card, keyPath, $"card '{name}' is listed twice");
                }
            }
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new TreasurePrices {
            Id = id!,
            Profiles = profiles,
            ByName = byName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            SourceFile = display,
        };
    }

}
