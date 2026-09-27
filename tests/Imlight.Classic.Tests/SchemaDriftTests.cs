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
 * Pins the names the C# code knows (world ids, feature paths, rule enums)
 * to classic-data's JSON Schemas, so the two cannot drift apart.
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
 * Last Updated: 09/26/2026
 */

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        Assert.Equal(new[] { "accuracy_table", "xp_table", "power_pips_from_rank", "dragonspyre_difficulty", "tutorial" },
            rules.EnumerateObject().Select(rule => rule.Name).ToArray());
    }

    [Fact]
    public void ProfileIdPatternMatchesSchema() {
        var schema = ReadSchema("profile.schema.json");

        Assert.Equal(ClassicSchema.IdPattern, schema.GetProperty("properties").GetProperty("id").GetProperty("pattern").GetString());
    }

}
