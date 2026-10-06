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
 * CLASSIC SPELL VALUES TESTS
 * ========================================================================
 *
 * PURPOSE:
 * AccuracyTableLoader reads a per-school accuracy table, resolves profile
 * overrides along the extends chain and reports invalid tables.
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
using System.Linq;
using System.Text;
using Imlight.Classic.Rules;
using Imlight.Classic.Spells;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class AccuracyTableTests : IDisposable {

    private readonly TempClassicData _data = new();

    public void Dispose() => _data.Dispose();

    private static string TableYaml(string fire = "accuracy: 0.75", string extraSchool = "") {
        var text = new StringBuilder();
        text.Append("id: accuracy-test\ntitle: Test\nprofiles: [late-2009, arc1-2009h1]\nschools:\n");
        foreach (var school in ClassicSpellSchema.PlayerSchools) {
            text.Append("  ").Append(school).Append(":\n");
            text.Append("    ").Append(school == "fire" ? fire.Replace("\n", "\n    ") : "accuracy: 0.8").Append('\n');
            text.Append("    provenance:\n    - {source: https://example.invalid, source_date: '2010-01-01', retrieved: '2026-09-27', covers: [accuracy], confidence: verified}\n");
            text.Append("    later_changes: []\n");
        }

        text.Append(extraSchool);
        text.Append("license_tag: own\n");

        return text.ToString();
    }

    [Fact]
    public void LoadsEverySchool() {
        var table = AccuracyTableLoader.Load(_data.WriteRule("accuracy-test.yaml", TableYaml()));

        Assert.Equal("accuracy-test", table.Id);
        Assert.Equal("Test", table.Title);
        Assert.Equal(new[] { "late-2009", "arc1-2009h1" }, table.Profiles.ToArray());
        Assert.Equal(7, table.Schools.Count);
        Assert.Equal(0.75, table.BaseAccuracy("fire", ["late-2009"]));
        Assert.Equal(0.8, table.BaseAccuracy("ice", ["late-2009"]));
        Assert.Null(table.BaseAccuracy("shadow", ["late-2009"]));
        Assert.Equal("classic-data/rules/accuracy-test.yaml", table.SourceFile);
    }

    [Fact]
    public void ProfileValuesApplyAlongTheChain() {
        var table = AccuracyTableLoader.Load(_data.WriteRule("accuracy-test.yaml", TableYaml("""
            accuracy: 0.75
            modern_accuracy: 0.7
            profile_values:
              arc1-2009h1: {accuracy: 0.65}
            """)));

        Assert.Equal(0.65, table.BaseAccuracy("fire", ["arc1-2009h1", "late-2009"]));
        Assert.Equal(0.65, table.BaseAccuracy("fire", ["arc1-strict", "arc1-2009h1", "late-2009"]));
        Assert.Equal(0.75, table.BaseAccuracy("fire", ["late-2009"]));
        Assert.Equal(0.7, table.Schools["fire"].ModernAccuracy);
    }

    [Fact]
    public void InvalidTablesReportEveryError() {
        var path = _data.WriteRule("accuracy-test.yaml", TableYaml("""
            accuracy: 75
            profile_values:
              late-2009: {accuracy: 0.7}
            """, extraSchool: "  shadow: {accuracy: 0.5}\n").Replace("id: accuracy-test", "id: accuracy-other"));

        var ex = Assert.Throws<ClassicDataException>(() => AccuracyTableLoader.Load(path));

        Assert.Contains(ex.Errors, error => error.KeyPath == "id");
        Assert.Contains(ex.Errors, error => error.KeyPath == "schools.fire.accuracy");
        Assert.Contains(ex.Errors, error => error.KeyPath == "schools.fire.profile_values.late-2009");
        Assert.Contains(ex.Errors, error => error.KeyPath == "schools.shadow" && error.Message == "unknown key 'shadow'");
    }

    [Fact]
    public void MissingSchoolIsAnError() {
        var yaml = TableYaml();
        var start = yaml.IndexOf("  balance:", StringComparison.Ordinal);
        var end = yaml.IndexOf("license_tag", StringComparison.Ordinal);
        var path = _data.WriteRule("accuracy-test.yaml", yaml[..start] + yaml[end..]);

        var ex = Assert.Throws<ClassicDataException>(() => AccuracyTableLoader.Load(path));

        Assert.Contains(ex.Errors, error => error.KeyPath == "schools.balance" && error.Message.Contains("missing"));
    }

}
