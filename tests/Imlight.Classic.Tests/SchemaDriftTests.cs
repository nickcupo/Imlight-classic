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
 * CLASSIC RULES TESTS
 * ========================================================================
 * 
 * PURPOSE:
 * Pins the names the C# code knows (world ids, feature paths, rule enums,
 * spell and accuracy table fields) to classic-data's JSON Schemas, so the
 * two cannot drift apart.
 * 
 * USAGE EXAMPLE:
 * dotnet test server/tests/Imlight.Classic.Tests
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Classic.Rules;
using Imlight.Classic.Spells;
using Imlight.Classic.Zones;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class SchemaDriftTests {

    private static JsonElement ReadSchema(string name) {
        using var document = JsonDocument.Parse(File.ReadAllText(ClassicDataFixture.SchemaPath(name)));

        return document.RootElement.Clone();
    }

    private static string?[] EnumValues(JsonElement element)
        => [.. element.GetProperty("enum").EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Null ? null : value.GetString())];

    [Fact]
    public void WorldIdsMatchProfileSchema() {
        var schema = ReadSchema("profile.schema.json");
        var worlds = EnumValues(schema.GetProperty("$defs").GetProperty("world"));

        Assert.Equal(worlds, ClassicSchema.WorldIds.ToArray());
    }

    [Fact]
    public void FeaturePathsMatchProfileSchema() {
        var schema = ReadSchema("profile.schema.json");
        var paths = new List<string>();
        foreach (var feature in schema.GetProperty("properties").GetProperty("features").GetProperty("properties").EnumerateObject()) {
            if (feature.Value.TryGetProperty("type", out var type) && type.GetString() == "object") {
                paths.AddRange(feature.Value.GetProperty("properties").EnumerateObject().Select(member => $"{feature.Name}.{member.Name}"));
            }
            else {
                paths.Add(feature.Name);
            }
        }

        Assert.Equal(paths, ClassicFeatures.All.ToArray());
    }

    [Fact]
    public void FeaturePathsMatchZonesSchema() {
        var schema = ReadSchema("zones.schema.json");
        var paths = EnumValues(schema.GetProperty("$defs").GetProperty("featurePath"));

        Assert.Equal(paths, ClassicFeatures.All.ToArray());
    }

    [Fact]
    public void RuleEnumsMatchProfileSchema() {
        var schema = ReadSchema("profile.schema.json");
        var rules = schema.GetProperty("properties").GetProperty("rules").GetProperty("properties");
        var statuses = EnumValues(schema.GetProperty("properties").GetProperty("status"));

        Assert.Equal(statuses, ClassicSchema.ProfileStatuses.ToArray());
        Assert.Equal(EnumValues(rules.GetProperty("power_pips_from_rank")), ClassicSchema.PowerPipRanks.Cast<string?>().Append(null).ToArray());
        Assert.Equal(EnumValues(rules.GetProperty("dragonspyre_difficulty")), ClassicSchema.DragonspyreDifficulties.ToArray());
        Assert.Equal(EnumValues(rules.GetProperty("tutorial")), ClassicSchema.Tutorials.ToArray());
        Assert.Equal(EnumValues(rules.GetProperty("teleport_stones")), ClassicSchema.TeleportStoneRules.ToArray());
        Assert.Equal(new[] { "accuracy_table", "xp_table", "player_health", "mob_rewards", "badges", "quest_cards", "treasure_prices", "mob_stats", "crown_shop", "later_objects", "creature_decks", "power_pips_from_rank",
                             "dragonspyre_difficulty", "tutorial", "teleport_stones", "potions", "second_chance", "boss_cheats", "instance_resets" },
            rules.EnumerateObject().Select(rule => rule.Name).ToArray());
    }

    [Fact]
    public void PotionAndSecondChanceLoaderKeysMatchTheirSchemas() { // CLASSIC
        string[] Keys(System.Text.Json.JsonElement node) => [.. node.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
        string[] Sorted(IEnumerable<string> keys) => [.. keys.Order(StringComparer.Ordinal)];

        Assert.Equal(Keys(ReadSchema("potions.schema.json")), Sorted(PotionRulesLoader.s_rootKeys));
        Assert.Equal(Keys(ReadSchema("second-chance.schema.json")), Sorted(SecondChanceRulesLoader.s_rootKeys));
    }

    [Fact]
    public void InstanceResetLoaderKeysMatchTheirSchema() { // CLASSIC
        string[] Keys(System.Text.Json.JsonElement node) => [.. node.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

        Assert.Equal(Keys(ReadSchema("instance-resets.schema.json")), InstanceResetRulesLoader.s_rootKeys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void BossCheatLoaderKeysMatchTheirSchema() { // CLASSIC
        string[] Keys(System.Text.Json.JsonElement node) => [.. node.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
        string[] Sorted(IEnumerable<string> keys) => [.. keys.Order(StringComparer.Ordinal)];
        var schema = ReadSchema("boss-cheats.schema.json");
        var defs = schema.GetProperty("$defs");
        var boss = defs.GetProperty("boss");

        Assert.Equal(Keys(schema), Sorted(BossCheatsLoader.s_rootKeys));
        Assert.Equal(Keys(schema.GetProperty("properties").GetProperty("dungeon")), Sorted(BossCheatsLoader.s_dungeonKeys));
        Assert.Equal(Keys(boss), Sorted(BossCheatsLoader.s_bossKeys));
        Assert.Equal(Keys(boss.GetProperty("properties").GetProperty("interrupt")), Sorted(BossCheatsLoader.s_interruptKeys));
        Assert.Equal(Keys(boss.GetProperty("properties").GetProperty("destroy_traps")), Sorted(BossCheatsLoader.s_trapKeys));
        Assert.Equal(Keys(boss.GetProperty("properties").GetProperty("summons").GetProperty("items")), Sorted(BossCheatsLoader.s_summonKeys));
        Assert.Equal(Keys(boss.GetProperty("properties").GetProperty("free_spells").GetProperty("items")), Sorted(BossCheatsLoader.s_freeKeys));
        var guide = schema.GetProperty("properties").GetProperty("guide");
        Assert.Equal(Keys(guide), Sorted(BossCheatGuideLoader.s_guideKeys));
        Assert.Equal(Keys(guide.GetProperty("properties").GetProperty("spells").GetProperty("items")), Sorted(BossCheatGuideLoader.s_spellKeys));
        Assert.Equal(Keys(guide.GetProperty("properties").GetProperty("bosses").GetProperty("items")), Sorted(BossCheatGuideLoader.s_bossKeys));
    }

    [Fact]
    public void CreatureDeckLoaderKeysMatchTheirSchema() { // CLASSIC
        string[] Keys(System.Text.Json.JsonElement node) => [.. node.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
        string[] Sorted(IEnumerable<string> keys) => [.. keys.Order(StringComparer.Ordinal)];
        var decks = ReadSchema("creature-decks.schema.json");

        Assert.Equal(Keys(decks), Sorted(CreatureDecksLoader.s_rootKeys));
        Assert.Equal(Keys(decks.GetProperty("$defs").GetProperty("creature")), Sorted(CreatureDecksLoader.s_creatureKeys));
        Assert.Equal(Keys(decks.GetProperty("$defs").GetProperty("spell")), Sorted(CreatureDecksLoader.s_spellKeys));
    }

    [Fact]
    public void BadgeAndQuestCardLoaderKeysMatchTheirSchemas() {
        string[] Keys(System.Text.Json.JsonElement node) => [.. node.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
        string[] Sorted(IEnumerable<string> keys) => [.. keys.Order(StringComparer.Ordinal)];
        var badges = ReadSchema("badges.schema.json");
        var cards = ReadSchema("quest-cards.schema.json");

        Assert.Equal(Keys(badges), Sorted(BadgeRulesLoader.s_rootKeys));
        Assert.Equal(Keys(badges.GetProperty("$defs").GetProperty("badge")), Sorted(BadgeRulesLoader.s_badgeKeys));
        Assert.Equal(Keys(badges.GetProperty("$defs").GetProperty("badge").GetProperty("properties").GetProperty("award")),
            Sorted(BadgeRulesLoader.s_awardKeys));
        Assert.Equal(Keys(cards), Sorted(QuestCardRewardsLoader.s_rootKeys));
        Assert.Equal(Keys(cards.GetProperty("$defs").GetProperty("quest")), Sorted(QuestCardRewardsLoader.s_questKeys));
    }

    [Fact]
    public void TreasurePriceLoaderKeysMatchTheirSchema() {
        string[] Keys(System.Text.Json.JsonElement node) => [.. node.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
        var prices = ReadSchema("treasure-prices.schema.json");

        Assert.Equal(Keys(prices), Sorted(TreasurePricesLoader.s_rootKeys));
        Assert.Equal(Keys(prices.GetProperty("$defs").GetProperty("card")), Sorted(TreasurePricesLoader.s_cardKeys));
    }

    [Theory]
    [InlineData("worldsFile")]
    [InlineData("overridesFile")]
    [InlineData("world")]
    [InlineData("area")]
    [InlineData("override")]
    public void ZoneLoaderKeysMatchZonesSchema(string definition) {
        var schema = ReadSchema("zones.schema.json");
        var properties = schema.GetProperty("$defs").GetProperty(definition).GetProperty("properties")
            .EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        var keys = definition switch {
            "worldsFile" => ZoneWorldMapLoader.s_worldsFileKeys,
            "overridesFile" => ZoneWorldMapLoader.s_overridesFileKeys,
            "world" => ZoneWorldMapLoader.s_worldKeys,
            "area" => ZoneWorldMapLoader.s_areaKeys,
            _ => ZoneWorldMapLoader.s_overrideKeys,
        };

        Assert.Equal(properties, keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ProfileLoaderKeysMatchProfileSchema() {
        var schema = ReadSchema("profile.schema.json");
        var unlock = schema.GetProperty("properties").GetProperty("world_unlocks").GetProperty("additionalProperties");
        var check = schema.GetProperty("$defs").GetProperty("unlockCheck");

        Assert.Equal(PropertyNames(schema), Sorted(ClassicProfileLoader.s_rootKeys));
        Assert.Equal(PropertyNames(unlock), Sorted(ClassicProfileLoader.s_worldUnlockKeys));
        Assert.Equal(PropertyNames(check), Sorted(Imlight.Classic.Travel.UnlockCheck.Keys));
    }

    [Fact]
    public void ProfileIdPatternMatchesSchema() {
        var schema = ReadSchema("profile.schema.json");

        Assert.Equal(ClassicSchema.IdPattern, schema.GetProperty("properties").GetProperty("id").GetProperty("pattern").GetString());
    }

    private static string[] PropertyNames(JsonElement element)
        => [.. element.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

    private static string[] Sorted(IEnumerable<string> keys) => [.. keys.Order(StringComparer.Ordinal)];

    [Fact]
    public void SpellEnumsMatchSpellSchema() {
        var schema = ReadSchema("spell.schema.json");
        var defs = schema.GetProperty("$defs");
        var effect = defs.GetProperty("effect").GetProperty("properties");

        Assert.Equal(EnumValues(defs.GetProperty("school")), ClassicSpellSchema.Schools.ToArray());
        Assert.Equal(EnumValues(defs.GetProperty("effectSchool")), ClassicSpellSchema.EffectSchools.ToArray());
        Assert.Equal(EnumValues(schema.GetProperty("properties").GetProperty("kind")), ClassicSpellSchema.Kinds.ToArray());
        Assert.Equal(EnumValues(effect.GetProperty("type")), ClassicSpellSchema.EffectTypes.ToArray());
        Assert.Equal(EnumValues(effect.GetProperty("targets")), ClassicSpellSchema.Targets.ToArray());
        Assert.Equal(EnumValues(defs.GetProperty("licenseTag")), ClassicSpellSchema.LicenseTags.ToArray());
        Assert.Equal(14, defs.GetProperty("pips").GetProperty("oneOf")[0].GetProperty("maximum").GetInt32());
    }

    [Fact]
    public void SpellEnumTypesFollowTheSchemaNames() {
        Assert.Equal(ClassicSpellSchema.EffectTypes.Select(name => name.Replace("_", "")),
            Enum.GetValues<SpellEffectKind>().Select(kind => kind.ToString().ToLowerInvariant()));
        Assert.Equal(ClassicSpellSchema.Targets.Select(name => name.Replace("_", "")),
            Enum.GetValues<SpellTargets>().Select(target => target.ToString().ToLowerInvariant()));
    }

    [Fact]
    public void SpellLoaderKeysMatchSpellSchema() {
        var schema = ReadSchema("spell.schema.json");
        var defs = schema.GetProperty("$defs");

        Assert.Equal(PropertyNames(schema), Sorted(ClassicSpellLoader.s_recordKeys));
        Assert.Equal(PropertyNames(defs.GetProperty("values")), Sorted(ClassicSpellLoader.s_valuesKeys));
        Assert.Equal(PropertyNames(defs.GetProperty("valuesOverride")), Sorted(ClassicSpellLoader.s_valuesKeys));
        Assert.Equal(PropertyNames(defs.GetProperty("effect")), Sorted(ClassicSpellLoader.s_effectKeys));
        Assert.Equal(PropertyNames(schema.GetProperty("properties").GetProperty("modern_values")), Sorted(ClassicSpellLoader.s_modernValuesKeys));
        Assert.Equal(PropertyNames(defs.GetProperty("provenance")), Sorted(ClassicSpellLoader.s_provenanceKeys));
        Assert.Equal(PropertyNames(defs.GetProperty("laterChange")), Sorted(ClassicSpellLoader.s_laterChangeKeys));
    }

    [Fact]
    public void AccuracyTableKeysMatchAccuracySchema() {
        var schema = ReadSchema("accuracy.schema.json");

        Assert.Equal(PropertyNames(schema), Sorted(AccuracyTableLoader.s_rootKeys));
        Assert.Equal(PropertyNames(schema.GetProperty("$defs").GetProperty("school")), Sorted(AccuracyTableLoader.s_schoolKeys));
        Assert.Equal(PropertyNames(schema.GetProperty("properties").GetProperty("schools")), Sorted(ClassicSpellSchema.Schools));
    }


    [Fact]
    public void XpTableKeysMatchXpSchema() {
        var schema = ReadSchema("xp-table.schema.json");

        Assert.Equal(PropertyNames(schema), Sorted(XpTableLoader.s_rootKeys));
        Assert.Equal(PropertyNames(schema.GetProperty("$defs").GetProperty("level")), Sorted(XpTableLoader.s_levelKeys));
    }

    [Fact]
    public void MobRewardKeysMatchMobRewardSchema() {
        var schema = ReadSchema("mob-rewards.schema.json");
        var properties = schema.GetProperty("properties");
        var defs = schema.GetProperty("$defs");

        Assert.Equal(PropertyNames(schema), Sorted(MobRewardRulesLoader.s_rootKeys));
        Assert.Equal(PropertyNames(properties.GetProperty("combat_xp")), Sorted(MobRewardRulesLoader.s_combatKeys));
        Assert.Equal(PropertyNames(properties.GetProperty("gold")), Sorted(MobRewardRulesLoader.s_goldKeys));
        Assert.Equal(PropertyNames(properties.GetProperty("gold").GetProperty("properties").GetProperty("by_rank").GetProperty("items")),
            Sorted(MobRewardRulesLoader.s_rankKeys));
        // drops, treasure_cards and reagents share one shape; the loader accepts tallies only under drops and
        // quantity/client_tables only under the other two.
        var dropRule = PropertyNames(defs.GetProperty("dropRule"));
        Assert.Equal(dropRule, Sorted(MobRewardRulesLoader.s_dropKeys.Union(MobRewardRulesLoader.s_extraDropKeys)));
        Assert.Equal(PropertyNames(defs.GetProperty("tally")), Sorted(MobRewardRulesLoader.s_tallyKeys));
        Assert.Equal(PropertyNames(defs.GetProperty("clientTable")), Sorted(MobRewardRulesLoader.s_clientTableKeys));
        Assert.Equal(PropertyNames(defs.GetProperty("mob")), Sorted(MobRewardRulesLoader.s_mobKeys));
        Assert.Equal(PropertyNames(defs.GetProperty("dropEntry")), Sorted(MobRewardRulesLoader.s_itemKeys));
    }

}
