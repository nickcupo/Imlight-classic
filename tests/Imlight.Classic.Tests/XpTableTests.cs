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
 * CLASSIC XP TABLE TESTS
 * ========================================================================
 *
 * PURPOSE:
 * The XP table loader's checks, and the real xp-2009 table.
 *
 * USAGE EXAMPLE:
 * dotnet test tests/Imlight.Classic.Tests --filter XpTableTests
 *
 * NOTE:
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.IO;
using System.Linq;
using System.Text;
using Imlight.Classic.Rules;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class XpTableTests : System.IDisposable {

    private readonly TempClassicData _data = new();

    public void Dispose() => _data.Dispose();

    private string Write(string yaml, string name = "xp-test.yaml") {
        var folder = Path.Combine(_data.Root, "progression");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, yaml.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));

        return path;
    }

    private static string TableYaml(string levels, int maxLevel = 3, string id = "xp-test") {
        var text = new StringBuilder();
        text.Append("id: ").Append(id).Append("\nprofiles: [late-2009]\nmax_level: ").Append(maxLevel).Append('\n');
        text.Append("provenance:\n- {source: https://example.invalid, source_date: '2009-06-15', retrieved: '2026-09-27', covers: [levels], confidence: verified}\n");
        text.Append("license_tag: own\nlevels:\n").Append(levels);

        return text.ToString();
    }

    private const string ThreeLevels = """
        - {level: 1, xp_to_next: 45, total_xp: 0, confidence: verified}
        - {level: 2, xp_to_next: 115, total_xp: 45, confidence: verified}
        - {level: 3, xp_to_next: 205, total_xp: 160, confidence: verified}
        """;

    [Fact]
    public void LoadsAndComputesTotals() {
        var table = XpTableLoader.Load(Write(TableYaml(ThreeLevels)));

        Assert.Equal("xp-test", table.Id);
        Assert.Equal(3, table.MaxLevel);
        Assert.Equal(45, table.XpToLeave(1));
        Assert.Equal(160, table.XpToLeave(2));
        Assert.Equal(365, table.XpToLeave(3));
        Assert.Equal(0, table.XpToReach(1));
        Assert.Equal(160, table.XpToReach(3));
        Assert.Null(table.XpToLeave(4));
        Assert.Equal(1, table.LevelAt(44));
        Assert.Equal(2, table.LevelAt(45));
        Assert.Equal(3, table.LevelAt(10_000));
    }

    [Fact]
    public void ReportsLevelsThatDifferFromTheClient() {
        var table = XpTableLoader.Load(Write(TableYaml(ThreeLevels)));

        Assert.Empty(table.DiffersFrom([0, 45, 160, 365, 705]));
        Assert.Equal(new[] { (2, 170, 160), (3, 375, 365) }, table.DiffersFrom([0, 45, 170, 375]).ToArray());
    }

    [Fact]
    public void RejectsAWrongTotal() {
        var yaml = TableYaml(ThreeLevels.Replace("total_xp: 160", "total_xp: 161"));

        var ex = Assert.Throws<ClassicDataException>(() => XpTableLoader.Load(Write(yaml)));
        Assert.Contains(ex.Errors, error => error.Message.Contains("should be 160"));
    }

    [Fact]
    public void RejectsAGapAndACountMismatch() {
        var yaml = TableYaml(ThreeLevels.Replace("level: 2,", "level: 5,"));

        var ex = Assert.Throws<ClassicDataException>(() => XpTableLoader.Load(Write(yaml)));
        Assert.Contains(ex.Errors, error => error.Message.Contains("out of order"));

        var short2 = TableYaml(ThreeLevels, maxLevel: 4);
        ex = Assert.Throws<ClassicDataException>(() => XpTableLoader.Load(Write(short2)));
        Assert.Contains(ex.Errors, error => error.Message.Contains("max_level is 4"));
    }

    [Fact]
    public void RejectsAWikiValueWithoutNotesAndABadId() {
        var yaml = TableYaml(ThreeLevels.Replace("xp_to_next: 115, total_xp: 45,", "xp_to_next: 115, total_xp: 45, wiki_value: 151,"));

        var ex = Assert.Throws<ClassicDataException>(() => XpTableLoader.Load(Write(yaml)));
        Assert.Contains(ex.Errors, error => error.Message.Contains("needs notes"));

        ex = Assert.Throws<ClassicDataException>(() => XpTableLoader.Load(Write(TableYaml(ThreeLevels, id: "xp-other"))));
        Assert.Contains(ex.Errors, error => error.Message.Contains("match the file name"));
    }

    [Fact]
    public void RealXp2009TableCoversTheLevelCap() {
        var profile = ClassicDataFixture.LoadProfile("late-2009");
        var table = XpTableLoader.Load(Path.Combine(ClassicDataFixture.Root, profile.Rules.XpTable!));

        Assert.Equal("xp-2009", table.Id);
        Assert.Equal(profile.LevelCap, table.MaxLevel);
        Assert.Equal(45, table.XpToLeave(1));
        Assert.Equal(6970, table.XpToReach(11));        // entering Krokotopia at level 10-11
        Assert.Equal(636530, table.XpToReach(50));       // the level cap
        Assert.Equal(681130, table.XpToLeave(50));       // the XP ceiling at the cap
        Assert.Contains("late-2009", table.Profiles);
        Assert.Contains("arc1-2009h1", table.Profiles);
    }

}
