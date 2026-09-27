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
 * Path resolution for the [Classic] ini settings.
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
using System.IO;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ClassicDataLocatorTests : IDisposable {

    private readonly string _top = Path.Combine(Path.GetTempPath(), "imlight-locator-" + Guid.NewGuid().ToString("N"));

    public ClassicDataLocatorTests() {
        Directory.CreateDirectory(_top);
    }

    public void Dispose() {
        try {
            Directory.Delete(_top, recursive: true);
        }
        catch (IOException) {
            // A leftover temp directory is harmless.
        }
    }

    [Fact]
    public void AbsolutePathIsUsedAsIs() {
        var absolute = Path.Combine(_top, "somewhere", "profiles");

        Assert.Equal(absolute, ClassicDataLocator.ResolveProfilesPath(absolute, "/unused"));
    }

    [Fact]
    public void RelativePathResolvesAgainstTheBaseDirectory() {
        var bin = Path.Combine(_top, "bin");

        Assert.Equal(Path.Combine(_top, "data", "profiles"),
            ClassicDataLocator.ResolveProfilesPath(Path.Combine("..", "data", "profiles"), bin));
    }

    [Fact]
    public void EmptyWalksUpAndReturnsTheNearestMatch() {
        var far = Path.Combine(_top, "classic-data", "profiles");
        var near = Path.Combine(_top, "repo", "classic-data", "profiles");
        var bin = Path.Combine(_top, "repo", "server", "bin", "Release");
        Directory.CreateDirectory(far);
        Directory.CreateDirectory(near);
        Directory.CreateDirectory(bin);

        Assert.Equal(near, ClassicDataLocator.ResolveProfilesPath("", bin));
        Assert.Equal(near, ClassicDataLocator.ResolveProfilesPath("   ", bin));
        Assert.Equal(Path.Combine(_top, "repo", "classic-data"), ClassicDataLocator.FindClassicDataRoot(bin));
    }

    [Fact]
    public void NotFoundListsEveryProbedPath() {
        var bin = Path.Combine(_top, "lonely", "bin");
        Directory.CreateDirectory(bin);
        var probed = ClassicDataLocator.ProbeProfilesPaths(bin);

        var ex = Assert.Throws<DirectoryNotFoundException>(() => ClassicDataLocator.ResolveProfilesPath(null, bin));

        Assert.Equal(Path.Combine(bin, "classic-data", "profiles"), probed[0]);
        Assert.EndsWith(Path.Combine("classic-data", "profiles"), probed[^1]);
        foreach (var path in probed) {
            Assert.Contains(path, ex.Message);
        }
    }

    [Fact]
    public void ZoneWorldsPathDefaultsNextToTheProfiles() {
        var profiles = Path.Combine(_top, "classic-data", "profiles");

        Assert.Equal(Path.Combine(_top, "classic-data", "zones", "worlds.yaml"),
            ClassicDataLocator.ResolveZoneWorldsPath("", profiles, "/unused"));
    }

    [Fact]
    public void ZoneWorldsPathCanBeSetExplicitly() {
        var bin = Path.Combine(_top, "bin");
        var absolute = Path.Combine(_top, "maps", "custom.yaml");

        Assert.Equal(absolute, ClassicDataLocator.ResolveZoneWorldsPath(absolute, "/unused", bin));
        Assert.Equal(Path.Combine(bin, "maps", "custom.yaml"),
            ClassicDataLocator.ResolveZoneWorldsPath(Path.Combine("maps", "custom.yaml"), "/unused", bin));
    }

    [Fact]
    public void DisplayPathIsRelativeToTheClassicDataParent() {
        var file = Path.Combine(_top, "classic-data", "profiles", "late-2009.yaml");

        Assert.Equal("classic-data/profiles/late-2009.yaml", ClassicDataLocator.DisplayPath(file));
        Assert.Equal(Path.Combine(_top, "elsewhere.yaml"), ClassicDataLocator.DisplayPath(Path.Combine(_top, "elsewhere.yaml")));
    }

    [Fact]
    public void SpellsPathDefaultsNextToTheProfiles() {
        var profiles = Path.Combine(_top, "classic-data", "profiles");
        var bin = Path.Combine(_top, "bin");

        Assert.Equal(Path.Combine(_top, "classic-data", "spells"), ClassicDataLocator.ResolveSpellsPath(" ", profiles, "/unused"));
        Assert.Equal(Path.Combine(bin, "data", "spells"), ClassicDataLocator.ResolveSpellsPath(Path.Combine("data", "spells"), profiles, bin));
    }

    [Fact]
    public void LineageFallsBackToTheIdWithoutSourceFiles() {
        Assert.Equal(new[] { "test" }, ZoneFixture.Profile().Lineage);
    }

}
