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
 * CLASSIC CRAFTING SWITCH
 * ========================================================================
 *
 * PURPOSE:
 * One place that says whether crafting is available to players and which
 * templates lead to it, so the hook sites (vendors) agree. The server never
 * built crafting (the recipe ingredient data is not in the client; owner
 * 2026-10-15 "just turn crafting off"), so "on" only keeps the old behavior
 * of the profiles that list it.
 *
 * USAGE EXAMPLE:
 * if (!ClassicCrafting.Enabled && ClassicCrafting.IsRecipeVendorName(name)) { ... }
 *
 * NOTE:
 * Names are matched on template names (m_objectName), as Eloise's
 * CROWN-FURNITURE is: the crafting behaviors are not decoded by this server.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-sonnet-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/15/2026
 */

using System;

namespace Imlight.Classic;

/// <summary>
/// CLASSIC: the crafting feature switch and the template names that lead to crafting.
/// </summary>
public static class ClassicCrafting {

    /// <summary>
    /// True unless an active profile turns the <see cref="ClassicFeatures.Crafting"/> feature off.
    /// </summary>
    public static bool Enabled
        => !ClassicRuntime.IsInitialized || !ClassicRuntime.IsActive
        || ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.Crafting);

    /// <summary>
    /// True for the recipe shops of the worlds (WC-Shop-Recipe-NPC01, DS-Shop-Recipe-Fire01, KT-SHOP-PET-RECIPES, ...).
    /// </summary>
    /// <param name="templateName">The template's object name.</param>
    public static bool IsRecipeVendorName(string? templateName)
        => templateName is { Length: > 0 } && templateName.Contains("recipe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a vendor offers no shop: crafting is off, the template is a recipe shop and the data gives it no stock.
    /// A recipe-named vendor with a real stock (Felicia's furniture, the pet snack shops) keeps its shop.
    /// </summary>
    /// <param name="templateName">The template's object name.</param>
    /// <param name="hasStock">Whether the data has an inventory for the vendor.</param>
    public static bool ClosesRecipeShop(string? templateName, bool hasStock)
        => !Enabled && !hasStock && IsRecipeVendorName(templateName);

    /// <summary>
    /// True for placeable crafting stations (HOUSE_crafting_*, *_CraftingStation_*, Event_crafting_*).
    /// </summary>
    /// <param name="templateName">The template's object name.</param>
    public static bool IsCraftingStationName(string? templateName)
        => templateName is { Length: > 0 }
        && (templateName.StartsWith("HOUSE_crafting_", StringComparison.OrdinalIgnoreCase)
            || templateName.StartsWith("Event_crafting_", StringComparison.OrdinalIgnoreCase)
            || templateName.Contains("CraftingStation", StringComparison.OrdinalIgnoreCase));

}
