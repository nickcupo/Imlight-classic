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
 * CLASSIC CROWN SHOP CATALOG
 * ========================================================================
 *
 * PURPOSE:
 * What the Crown Shop sold at the profile's cutoff, with prices, category,
 * rental length, level floor and whether it is bought only during a duel.
 *
 * USAGE EXAMPLE:
 * var catalog = CrownShopCatalogLoader.Load(path);
 * var offered = catalog.Offered(rules.IsFeatureEnabled);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/28/2026
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Classic.Yaml;

namespace Imlight.Classic.Rules;

/// <summary>
/// The Crown Shop's categories, in the client's own terms.
/// </summary>
public static class CrownShopCategories {

    public const string PermanentMounts = "permanent_mounts";
    public const string RentalMounts = "rental_mounts";
    public const string Henchmen = "henchmen";
    public const string Elixirs = "elixirs";
    public const string Transformations = "transformations";
    public const string ClothingBundles = "clothing_bundles";
    public const string Boosters = "boosters";
    public const string Furniture = "furniture";
    public const string Houses = "houses"; // CLASSIC: independently approved deed catalog.
    public const string Gear = "gear";

    public static ImmutableArray<string> All { get; } =
        [PermanentMounts, RentalMounts, Henchmen, Elixirs, Transformations, ClothingBundles, Boosters, Furniture, Gear, Houses];

}

/// <summary>
/// One item the Crown Shop sold.
/// </summary>
public sealed record CrownShopEntry(
    string Name,
    ulong Template,
    string Category,
    int Crowns,
    int Gold,
    int? RentalDays,
    int MinLevel,
    bool CombatOnly,
    string? Feature);

/// <summary>
/// The Crown Shop at the cutoff.
/// </summary>
public sealed class CrownShopCatalog {

    public required string Id { get; init; }
    public required ImmutableArray<string> Profiles { get; init; }
    public required ImmutableArray<CrownShopEntry> Items { get; init; }
    public required string SourceFile { get; init; }

    /// <summary>
    /// The items whose feature switch is on, by template.
    /// </summary>
    public FrozenDictionary<ulong, CrownShopEntry> Offered(Func<string, bool> featureEnabled)
        => Items.Where(item => item.Feature is null || featureEnabled(item.Feature))
            .ToFrozenDictionary(item => item.Template);

}

/// <summary>
/// Loads and validates the Crown Shop catalog.
/// </summary>
public static class CrownShopCatalogLoader {

    internal static readonly FrozenSet<string> s_rootKeys = FrozenSet.Create(StringComparer.Ordinal,
        "id", "title", "profiles", "provenance", "license_tag", "notes", "items");
    internal static readonly FrozenSet<string> s_itemKeys = FrozenSet.Create(StringComparer.Ordinal,
        "name", "template", "category", "crowns", "gold", "rental_days", "min_level", "combat_only", "feature", "source",
        "confidence", "notes");
    private static readonly Regex s_id = new(@"^crown-shop-[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    /// <exception cref="ClassicDataException">The file is missing or invalid; every error is reported.</exception>
    public static CrownShopCatalog Load(string path) {
        var fullPath = Path.GetFullPath(path);
        var display = ClassicDataLocator.DisplayPath(fullPath);
        if (!File.Exists(fullPath)) {
            throw new ClassicDataException(new ClassicDataError(display, "", null, "the Crown Shop catalog does not exist"));
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

        diagnostics.CheckKeys(map, "", s_rootKeys, ["id", "profiles", "provenance", "license_tag", "items"]);
        var id = map.Find("id") is { } idEntry ? diagnostics.ReadString(idEntry.Value, "id") : null;
        var expectedId = Path.GetFileNameWithoutExtension(fullPath);
        if (id is not null && (!s_id.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal))) {
            diagnostics.At(map.Find("id")!.Value, "id", $"id '{id}' must be crown-shop-<name> and equal the file name ('{expectedId}')");
        }

        var profiles = ClassicRuleFiles.ReadProfiles(map, diagnostics);
        var items = ImmutableArray.CreateBuilder<CrownShopEntry>();
        var seen = new HashSet<ulong>();
        if (map.Find("items") is { } itemsEntry && diagnostics.ReadList(itemsEntry.Value, "items") is { } list) {
            for (var i = 0; i < list.Items.Length; i++) {
                var keyPath = YamlTree.Index("items", i);
                if (diagnostics.ReadMap(list.Items[i], keyPath) is not { } item) {
                    continue;
                }

                diagnostics.CheckKeys(item, keyPath, s_itemKeys, ["name", "template", "category", "source"]);
                string At(string key) => YamlTree.Join(keyPath, key);
                var name = item.Find("name") is { } n ? diagnostics.ReadString(n.Value, At("name")) : null;
                var template = item.Find("template") is { } t ? diagnostics.ReadInt(t.Value, At("template"), 1) : null;
                var category = item.Find("category") is { } c ? diagnostics.ReadEnum(c.Value, At("category"), CrownShopCategories.All) : null;
                var crowns = item.Find("crowns") is { } cr ? diagnostics.ReadInt(cr.Value, At("crowns"), 1) : null;
                var gold = item.Find("gold") is { } g ? diagnostics.ReadInt(g.Value, At("gold"), 1) : null;
                var days = item.Find("rental_days") is { } d ? diagnostics.ReadInt(d.Value, At("rental_days"), 1, 365) : null;
                var level = item.Find("min_level") is { } l ? diagnostics.ReadInt(l.Value, At("min_level"), 1, 50) : null;
                var combat = item.Find("combat_only") is { } co ? diagnostics.ReadBool(co.Value, At("combat_only")) : null;
                var feature = item.Find("feature") is { } f ? diagnostics.ReadString(f.Value, At("feature")) : null;
                if (feature is not null && !ClassicFeatures.IsKnown(feature)) {
                    diagnostics.At(item.Find("feature")!.Value, At("feature"), $"unknown feature '{feature}'");
                }

                if (crowns is null && gold is null) {
                    diagnostics.At(item, keyPath, "needs a crowns or gold price");
                }

                if (name is null || template is null || category is null) {
                    continue;
                }

                if (!seen.Add((ulong) template.Value)) {
                    diagnostics.At(item, keyPath, $"template {template} is listed twice");
                }

                items.Add(new CrownShopEntry(name, (ulong) template.Value, category, crowns ?? 0, gold ?? 0, days, level ?? 1,
                    combat ?? false, feature));
            }
        }

        if (diagnostics.HasErrors) {
            throw diagnostics.ToException();
        }

        return new CrownShopCatalog {
            Id = id!,
            Profiles = profiles,
            Items = items.ToImmutable(),
            SourceFile = display,
        };
    }

}
