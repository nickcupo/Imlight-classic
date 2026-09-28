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
 * CLASSIC CRAFTING ASSET EVIDENCE
 * ========================================================================
 *
 * PURPOSE:
 * Pins the decoded private r806919 recipe assets before enabling classic crafting.
 *
 * USAGE EXAMPLE:
 * W101C_CRAFTING_GAME_DATA points at the local directory containing Recipes-WorldData.wad.
 *
 * NOTE:
 * These are asset decoder checks, not acceptance tests for a crafting transaction.
 * The decoded economics do not establish historical 2009 values.
 *
 * TODO:
 * Add service transaction regressions when the audited contracts are established.
 *
 * Created by: Nick with Codex (GPT-6)
 * Version: KALI 1.0
 * Last Updated: 09/28/2026
 */

using System;
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Wad;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class CraftingProtocolEvidenceTests {
    [Fact]
    public void StagedDaggerRecipeDecodesConcreteOutputMaterialsAndEconomics() {
        var recipe = Load("Recipe-Athame-Craft-T1-002");
        Assert.Equal("Recipe-Athame-Craft-T1-002", recipe.m_recipeName);
        Assert.Equal("Recipes_00000199", recipe.m_displayKey);
        Assert.Equal(180048UL, recipe.m_itemID.Full);
        Assert.Equal(64, recipe.m_goldCost);
        Assert.Equal(60, recipe.m_cookTime);
        Assert.Equal(new[] { (106947UL, 2), (106953UL, 4), (106931UL, 2), (106939UL, 2) },
            recipe.m_ingredients.Select(i => (i.m_itemID.Full, i.m_quantity)).ToArray());
        Assert.All(recipe.m_ingredients, ingredient => {
            Assert.Equal(0, (int)ingredient.m_ingredientType);
            Assert.True(string.IsNullOrEmpty(ingredient.m_adjective));
            Assert.True(string.IsNullOrEmpty(ingredient.m_spellTemplate));
        });
        Assert.Contains("CraftGeneric", recipe.m_adjectives);
        Assert.Equal(0, recipe.m_crownsCost);
        Assert.Equal(0, recipe.m_arenaPointCost);
        Assert.Equal(0, recipe.m_pvpCurrencyCost);
        Assert.Equal(0, recipe.m_pvpTourneyCurrencyCost);
        Assert.Null(recipe.m_purchaseRequirements);
        Assert.Null(recipe.m_displayRequirements);
        Assert.Null(recipe.m_craftingResults);
        Assert.True(string.IsNullOrEmpty(recipe.m_itemLootTable));
        Assert.True(string.IsNullOrEmpty(recipe.m_spellName));
    }

    [Theory]
    [InlineData("Recipe-Athame-Craft-T1-001", 180047UL, 4500, 96)]
    [InlineData("Recipe-Athame-Craft-T1-003", 180049UL, 4500, 96)]
    [InlineData("Recipe-Athame-Craft-T1-004", 180050UL, 3000, 64)]
    public void NeighboringRecipesRetainTheirOwnCooldownAndPurchaseRequirements(
        string name, ulong output, int seconds, int gold) {
        var recipe = Load(name);
        Assert.Equal(name, recipe.m_recipeName);
        Assert.Equal(output, recipe.m_itemID.Full);
        Assert.Equal(seconds, recipe.m_cookTime);
        Assert.Equal(gold, recipe.m_goldCost);
        Assert.NotNull(recipe.m_purchaseRequirements);
        Assert.NotEmpty(recipe.m_purchaseRequirements.m_requirements);
        Assert.All(recipe.m_ingredients, ingredient => Assert.True(ingredient.m_quantity > 0));
    }

    private static RecipeTemplate Load(string name) {
        var directory = Environment.GetEnvironmentVariable("W101C_CRAFTING_GAME_DATA")
            ?? "/Users/nick/w101c-private/aurorium/data/V_r806919.Wizard_1_610/Data/GameData";
        var path = Path.Combine(directory, "Recipes-WorldData.wad");
        if (!File.Exists(path)) {
            const string reason = "Set W101C_CRAFTING_GAME_DATA to staged r806919 GameData for crafting asset checks.";
            if (Environment.GetEnvironmentVariable("W101C_REQUIRE_CLASSIC_DATA") == "1") Assert.Fail(reason);
            Assert.Skip(reason);
        }
        using var stream = File.OpenRead(path);
        var archive = ArchiveParser.Parse(stream);
        Assert.NotNull(archive);
        var bytes = archive.OpenFile($"ObjectData/Equipment_Recipes/{name}.xml");
        Assert.True(bytes.HasValue);
        Assert.True(new BindSerializer().Deserialize<RecipeTemplate>(bytes.Value.ToArray(), 1, out var recipe));
        Assert.NotNull(recipe);
        return recipe;
    }
}
