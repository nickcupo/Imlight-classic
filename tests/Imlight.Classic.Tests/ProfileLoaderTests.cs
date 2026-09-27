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
 * The profile loader on inline fixtures: extends merging, null overrides,
 * metadata that is never inherited, and every validation error with its
 * file, key path and line.
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

using System;
using System.Linq;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ProfileLoaderTests : IDisposable {

    private const string BaseProfile = """
        id: base
        title: Base
        description: Parent description
        status: canonical
        cutoff: 2010-05-25
        level_cap: 50
        worlds: [wizard_city, krokotopia]
        features:
          bazaar: true
          pets:
            energy: false
            leveling: false
        rules:
          xp_table: progression/xp.yaml
          power_pips_from_rank: magus
        notes:
          - "parent note"
        """;

    private readonly TempClassicData _data = new();

    public ProfileLoaderTests() {
        _data.WriteProfile("base", BaseProfile);
    }

    public void Dispose() => _data.Dispose();

    [Fact]
    public void ChildNullLevelCapRemovesParentCap() {
        _data.WriteProfile("child", """
            id: child
            title: Child
            status: optional
            extends: base
            cutoff: 2010-05-25
            level_cap: null
            """);

        var profile = ClassicProfileLoader.Load(_data.ProfilesPath, "child");

        Assert.Null(profile.LevelCap);
        Assert.Equal(new DateOnly(2010, 5, 25), profile.Cutoff);
    }

    [Fact]
    public void NestedFeatureMapsMerge() {
        _data.WriteProfile("child", """
            id: child
            title: Child
            status: optional
            extends: base
            cutoff: 2010-05-25
            features:
              pets:
                leveling: true
            """);

        var profile = ClassicProfileLoader.Load(_data.ProfilesPath, "child");

        Assert.False(profile.Features.IsEnabled(ClassicFeatures.PetsEnergy));
        Assert.True(profile.Features.IsEnabled(ClassicFeatures.PetsLeveling));
        Assert.True(profile.Features.Explicit[ClassicFeatures.Bazaar]);
        Assert.Equal(50, profile.LevelCap);
        Assert.Equal("magus", profile.Rules.PowerPipsFromRank);
    }

    [Fact]
    public void ChildListReplacesParentList() {
        _data.WriteProfile("child", """
            id: child
            title: Child
            status: optional
            extends: base
            cutoff: 2009-06-30
            worlds: [mooshu]
            """);

        var profile = ClassicProfileLoader.Load(_data.ProfilesPath, "child");

        Assert.Equal(new[] { "mooshu" }, profile.Worlds!.Value);
        Assert.Equal(new DateOnly(2009, 6, 30), profile.Cutoff);
    }

    [Fact]
    public void ThreeDeepChainMergesFromTheRoot() {
        _data.WriteProfile("middle", """
            id: middle
            title: Middle
            status: optional
            extends: base
            cutoff: 2010-01-01
            features:
              bazaar: false
            """);
        _data.WriteProfile("leaf", """
            id: leaf
            title: Leaf
            status: debug
            extends: middle
            cutoff: 2009-12-01
            features:
              pets:
                energy: true
            """);

        var profile = ClassicProfileLoader.Load(_data.ProfilesPath, "leaf");

        Assert.Equal(new DateOnly(2009, 12, 1), profile.Cutoff);
        Assert.Equal(50, profile.LevelCap);
        Assert.Equal(new[] { "wizard_city", "krokotopia" }, profile.Worlds!.Value);
        Assert.False(profile.Features.IsEnabled(ClassicFeatures.Bazaar));
        Assert.True(profile.Features.IsEnabled(ClassicFeatures.PetsEnergy));
        Assert.False(profile.Features.IsEnabled(ClassicFeatures.PetsLeveling));
        Assert.Equal(new[] { "leaf", "middle", "base" },
            profile.SourceFiles.Select(System.IO.Path.GetFileNameWithoutExtension).ToArray());
    }

    [Fact]
    public void MetadataIsNotInherited() {
        _data.WriteProfile("child", """
            id: child
            title: Child
            status: optional
            extends: base
            cutoff: 2010-05-25
            """);

        var profile = ClassicProfileLoader.Load(_data.ProfilesPath, "child");

        Assert.Equal("child", profile.Id);
        Assert.Equal("Child", profile.Title);
        Assert.Equal(ProfileStatus.Optional, profile.Status);
        Assert.Equal("base", profile.Extends);
        Assert.Null(profile.Description);
        Assert.Empty(profile.Notes);
        Assert.Equal("progression/xp.yaml", profile.Rules.XpTable);
    }

    [Fact]
    public void UnknownParentIsAnErrorOnExtends() {
        _data.WriteProfile("child", """
            id: child
            title: Child
            status: optional
            extends: late-2008
            cutoff: 2010-05-25
            """);

        var error = ZoneFixture.SingleError(() => ClassicProfileLoader.Load(_data.ProfilesPath, "child"), "extends");

        Assert.Equal("classic-data/profiles/child.yaml", error.File);
        Assert.Equal(4, error.Line);
        Assert.Equal("extends names 'late-2008', but profiles/late-2008.yaml does not exist", error.Message);
    }

    [Fact]
    public void ExtendsCycleShowsTheChain() {
        _data.WriteProfile("a", """
            id: a
            title: A
            status: optional
            extends: b
            cutoff: null
            """);
        _data.WriteProfile("b", """
            id: b
            title: B
            status: optional
            cutoff: null
            extends: a
            """);

        var error = ZoneFixture.SingleError(() => ClassicProfileLoader.Load(_data.ProfilesPath, "a"), "extends");

        Assert.Equal("classic-data/profiles/b.yaml", error.File);
        Assert.Equal(5, error.Line);
        Assert.Equal("extends cycle: a -> b -> a", error.Message);
    }

    [Fact]
    public void SelfExtendsIsACycle() {
        _data.WriteProfile("self", """
            id: self
            title: Self
            extends: self
            status: optional
            cutoff: null
            """);

        var error = ZoneFixture.SingleError(() => ClassicProfileLoader.Load(_data.ProfilesPath, "self"), "extends");

        Assert.Equal(3, error.Line);
        Assert.Equal("extends cycle: self -> self", error.Message);
    }

    [Fact]
    public void IdMustMatchFileName() {
        _data.WriteProfile("named", """
            title: Named
            id: other
            status: optional
            cutoff: null
            """);

        var error = ZoneFixture.SingleError(() => ClassicProfileLoader.Load(_data.ProfilesPath, "named"), "id");

        Assert.Equal("classic-data/profiles/named.yaml", error.File);
        Assert.Equal(2, error.Line);
        Assert.Equal("id 'other' does not match the file name 'named'", error.Message);
    }

    [Fact]
    public void UnknownTopLevelKey() {
        _data.WriteProfile("p", """
            id: p
            title: P
            status: optional
            cutoff: null
            colour: red
            """);

        var error = ZoneFixture.SingleError(() => ClassicProfileLoader.Load(_data.ProfilesPath, "p"), "colour");

        Assert.Equal(5, error.Line);
        Assert.Equal("unknown key 'colour'", error.Message);
    }

    [Fact]
    public void UnknownFeature() {
        _data.WriteProfile("p", """
            id: p
            title: P
            status: optional
            cutoff: null
            features:
              bazaar: true
              teleportation: false
            """);

        var error = ZoneFixture.SingleError(() => ClassicProfileLoader.Load(_data.ProfilesPath, "p"),
            "features.teleportation");

        Assert.Equal(7, error.Line);
        Assert.Equal("unknown feature 'teleportation'", error.Message);
    }

    [Fact]
    public void UnknownPetsSubkey() {
        _data.WriteProfile("p", """
            id: p
            title: P
            status: optional
            cutoff: null
            features:
              pets:
                energy: false
                hatchery: true
            """);

        var error = ZoneFixture.SingleError(() => ClassicProfileLoader.Load(_data.ProfilesPath, "p"),
            "features.pets.hatchery");

        Assert.Equal(8, error.Line);
        Assert.Equal("unknown feature 'pets.hatchery'", error.Message);
    }

    [Fact]
    public void WrongTypesAreAllReportedTogether() {
        _data.WriteProfile("p", """
            id: p
            title: P
            status: optional
            cutoff: 2010-13-01
            level_cap: fifty
            features:
              bazaar: maybe
            """);

        var ex = Assert.Throws<ClassicDataException>(() => ClassicProfileLoader.Load(_data.ProfilesPath, "p"));

        Assert.Equal(3, ex.Errors.Length);
        Assert.Contains(ex.Errors, error => error is { KeyPath: "cutoff", Line: 4 }
            && error.Message == "expected a yyyy-MM-dd date or null, got '2010-13-01'");
        Assert.Contains(ex.Errors, error => error is { KeyPath: "level_cap", Line: 5 }
            && error.Message == "expected an integer >= 1 or null, got 'fifty'");
        Assert.Contains(ex.Errors, error => error is { KeyPath: "features.bazaar", Line: 7 }
            && error.Message == "expected true or false, got 'maybe'");
        Assert.All(ex.Errors, error => Assert.Equal("classic-data/profiles/p.yaml", error.File));
        Assert.Contains("classic-data/profiles/p.yaml:7 features.bazaar: expected true or false, got 'maybe'", ex.Message);
    }

    [Fact]
    public void QuotedNullIsAStringNotNull() {
        _data.WriteProfile("p", """
            id: p
            title: P
            status: optional
            cutoff: "null"
            """);

        var error = ZoneFixture.SingleError(() => ClassicProfileLoader.Load(_data.ProfilesPath, "p"), "cutoff");

        Assert.Equal(4, error.Line);
        Assert.Equal("expected a yyyy-MM-dd date or null, got 'null'", error.Message);
    }

    [Fact]
    public void MissingStatus() {
        _data.WriteProfile("p", """
            # comment first
            id: p
            title: P
            cutoff: null
            """);

        var error = ZoneFixture.SingleError(() => ClassicProfileLoader.Load(_data.ProfilesPath, "p"), "status");

        Assert.Equal(2, error.Line);
        Assert.Equal("required key 'status' is missing", error.Message);
    }

    [Fact]
    public void InvalidWorldId() {
        _data.WriteProfile("p", """
            id: p
            title: P
            status: optional
            cutoff: null
            worlds:
              - wizard_city
              - krokotopia
              - narnia
            """);

        var error = ZoneFixture.SingleError(() => ClassicProfileLoader.Load(_data.ProfilesPath, "p"), "worlds[2]");

        Assert.Equal(8, error.Line);
        Assert.Equal("unknown world id 'narnia'", error.Message);
    }

    [Fact]
    public void DuplicateKey() {
        _data.WriteProfile("p", """
            id: p
            title: P
            status: optional
            cutoff: null
            features:
              bazaar: true
              bazaar: false
            """);

        var error = ZoneFixture.SingleError(() => ClassicProfileLoader.Load(_data.ProfilesPath, "p"), "features.bazaar");

        Assert.Equal("classic-data/profiles/p.yaml", error.File);
        Assert.Equal(7, error.Line);
        Assert.Equal("duplicate key", error.Message);
    }

    [Theory]
    [InlineData("worlds: [wizard_city, krokotopia", "worlds")]
    [InlineData("features: {bazaar: true", "features")]
    public void UnclosedFlowCollectionBeforeAnotherKey(string line, string keyPath) {
        _data.WriteProfile("p", $"""
            id: p
            title: P
            status: optional
            cutoff: null
            {line}
            level_cap: 50
            """);

        var error = ZoneFixture.SingleError(() => ClassicProfileLoader.Load(_data.ProfilesPath, "p"), keyPath);

        Assert.Equal("classic-data/profiles/p.yaml", error.File);
        Assert.Equal(5, error.Line);
        Assert.StartsWith("not valid YAML", error.Message);
    }

    [Fact]
    public void BadPowerPipRank() {
        _data.WriteProfile("p", """
            id: p
            title: P
            status: optional
            cutoff: null
            rules:
              power_pips_from_rank: wizard
            """);

        var error = ZoneFixture.SingleError(() => ClassicProfileLoader.Load(_data.ProfilesPath, "p"),
            "rules.power_pips_from_rank");

        Assert.Equal(6, error.Line);
        Assert.StartsWith("expected one of novice, apprentice", error.Message);
    }

    [Fact]
    public void NullPowerPipRankIsValid() {
        _data.WriteProfile("p", """
            id: p
            title: P
            status: optional
            cutoff: null
            rules:
              power_pips_from_rank: null
            """);

        var profile = ClassicProfileLoader.Load(_data.ProfilesPath, "p");

        Assert.Null(profile.Rules.PowerPipsFromRank);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("Late-2009")]
    [InlineData("late-2009\n")]
    [InlineData("")]
    public void InvalidIdIsRejectedBeforeAnyFileIsRead(string id) {
        var ex = Assert.Throws<ClassicDataException>(() => ClassicProfileLoader.Load(_data.ProfilesPath, id));

        Assert.Contains("is not a valid profile id", ex.Message);
    }

    [Fact]
    public void MissingProfileFile() {
        var ex = Assert.Throws<ClassicDataException>(() => ClassicProfileLoader.Load(_data.ProfilesPath, "nothing"));

        Assert.Equal("profile 'nothing' does not exist", Assert.Single(ex.Errors).Message);
    }

    [Fact]
    public void LoadAllReportsEveryBrokenFile() {
        _data.WriteProfile("bad-one", """
            id: bad-one
            title: Bad
            status: optional
            cutoff: yesterday
            """);
        _data.WriteProfile("bad-two", """
            id: bad-two
            title: Bad
            status: nope
            cutoff: null
            """);

        var ex = Assert.Throws<ClassicDataException>(() => ClassicProfileLoader.LoadAll(_data.ProfilesPath));

        Assert.Contains(ex.Errors, error => error.File.EndsWith("bad-one.yaml", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, error => error.File.EndsWith("bad-two.yaml", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadAllGoesPastAFileTheParserCannotRead() {
        _data.WriteProfile("bad-one", """
            id: bad-one
            title: Bad
            status: optional
            worlds: [wizard_city
            cutoff: null
            """);
        _data.WriteProfile("bad-two", """
            id: bad-two
            title: Bad
            status: nope
            cutoff: null
            """);

        var ex = Assert.Throws<ClassicDataException>(() => ClassicProfileLoader.LoadAll(_data.ProfilesPath));

        Assert.Contains(ex.Errors, error => error.File.EndsWith("bad-one.yaml", StringComparison.Ordinal) && error.KeyPath == "worlds");
        Assert.Contains(ex.Errors, error => error.File.EndsWith("bad-two.yaml", StringComparison.Ordinal));
    }

}
