// CLASSIC: strict evidence policy and profile isolation, without any private client or database dependency.
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Imlight.Classic.Rules;
using System.Text.Json.Nodes;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class PuzzleHelpersTests {
    internal static string RealPath => Path.Combine(ClassicDataFixture.Root, PuzzleHelpers.RelativePath);
    internal static PuzzleHelpers Real() => PuzzleHelpersLoader.Load(RealPath);

    internal static ClassicProfile Profile(string id, string? reference = PuzzleHelpers.RelativePath) => new() {
        Id = id, Title = id, Status = ProfileStatus.Optional, Cutoff = new DateOnly(2010, 10, 31),
        Features = FeatureSwitches.None, Rules = new ProfileRules { PuzzleHelpers = reference }, SourceFiles = [],
    };

    private static string MutatedPolicy(string mutation) {
        var doc = JsonNode.Parse(File.ReadAllText(RealPath))!.AsObject();
        switch (mutation) {
            case "root_key": doc["typo"] = true; break;
            case "source_key": doc["provenance"]![0]!["typo"] = true; break;
            case "zone_key": doc["zones"]![0]!["typo"] = true; break;
            case "quest_key": doc["quest_helpers"]![0]!["typo"] = true; break;
            case "future": doc["provenance"]![0]!["source_date"] = "2010-11-01"; break;
            case "unverified": doc["provenance"]![0]!["confidence"] = "unverified"; break;
            case "source_link": doc["zones"]![0]!["source_id"] = "unknown"; break;
            case "duplicate_source": doc["provenance"]!.AsArray().Add(doc["provenance"]![0]!.DeepClone()); break;
            case "duplicate_trigger": doc["zones"]![0]!["remove_triggers"]!.AsArray().Add("Clue 02"); break;
            case "duplicate_goal": doc["quest_helpers"]![0]!["goals"]!.AsArray().Add("1_WizardQuestGoals_UseItem"); break;
            case "duplicate_zone": doc["zones"]!.AsArray().Add(doc["zones"]![0]!.DeepClone()); break;
            case "duplicate_quest": doc["quest_helpers"]!.AsArray().Add(doc["quest_helpers"]![0]!.DeepClone()); break;
            case "empty_targets": doc["zones"]![0]!["remove_triggers"] = new JsonArray(); break;
            case "traversal": doc["zones"]![0]!["zone"] = "Marleybone/../MB_KatzLab"; break;
            case "package": doc["zones"]![0]!["package"] = "another.wad"; break;
            case "member": doc["zones"]![0]!["member"] = "../triggers.xml"; break;
            case "profile": doc["profiles"] = new JsonArray("child-of-october"); break;
            case "id": doc["id"] = "puzzles-another"; break;
            case "enabled": doc["quest_helpers"]![0]!["no_quest_helper"] = false; break;
            case "missing": doc.Remove("provenance"); break;
            default: throw new ArgumentException(mutation);
        }
        var directory = Path.Combine(Path.GetTempPath(), "w101c-puzzle-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, PuzzleHelpers.PolicyId + ".yaml");
        File.WriteAllText(path, doc.ToJsonString());
        return path;
    }

    [Fact]
    public void RealPolicyNamesOnlyReviewedOctoberTargets() {
        var policy = Real();
        Assert.Equal(PuzzleHelpers.RelativePath, ClassicDataFixture.LoadProfile(PuzzleHelpers.OctoberProfile).Rules.PuzzleHelpers);
        Assert.Equal(PuzzleHelpers.PolicyId, policy.Id);
        Assert.Equal([PuzzleHelpers.OctoberProfile], policy.Profiles);
        Assert.Equal(new[] { "Marleybone/MB_BigBen/MB_BigBen", "Marleybone/MB_ScotlandYard/MB_KatzLab", "Marleybone/MB_Station/MB_Ironworks" },
            policy.Zones.Select(z => z.Zone).Order(StringComparer.Ordinal));
        Assert.Equal(19, policy.Zones.Sum(z => z.RemoveTriggers.Length));
        Assert.Equal(Enumerable.Range(1, 8).Select(i => $"Trigger ({i})").Concat(["Trigger Clue 01", "Trigger Clue 01 (1)", "Clue 02"]),
            policy.Zones.Single(z => z.Zone == "Marleybone/MB_ScotlandYard/MB_KatzLab").RemoveTriggers);
        Assert.Equal(new[] { "Trigger Icon Maker", "Trigger (6)", "Trigger (7)", "Trigger (8)" },
            policy.Zones.Single(z => z.Zone == "Marleybone/MB_Station/MB_Ironworks").RemoveTriggers);
        Assert.Equal(Enumerable.Range(7, 4).Select(i => $"Trigger ({i})"),
            policy.Zones.Single(z => z.Zone == "Marleybone/MB_BigBen/MB_BigBen").RemoveTriggers);
        var quest = Assert.Single(policy.QuestHelpers);
        Assert.Equal("GH-MAIN-C03-001", quest.Quest);
        Assert.Equal(Enumerable.Range(1, 7).Select(i => $"{i}_WizardQuestGoals_UseItem"), quest.Goals);
        Assert.True(quest.NoQuestHelper);
    }

    [Theory]
    [InlineData("late-2009")]
    [InlineData("arc1-2009h1")]
    [InlineData("dev-unrestricted")]
    [InlineData("child-of-october")]
    public void EvenAnInheritedReferenceNeverAppliesToOtherProfiles(string id) {
        Assert.Same(PuzzleHelpers.Empty, PuzzleHelpersLoader.LoadForProfile(Profile(id), "/does-not-exist"));
        Assert.False(Real().AppliesTo(id));
        if (id != "child-of-october") Assert.Null(ClassicDataFixture.LoadProfile(id).Rules.PuzzleHelpers);
    }

    [Theory]
    [InlineData("root_key")][InlineData("source_key")][InlineData("zone_key")][InlineData("quest_key")]
    [InlineData("future")][InlineData("unverified")][InlineData("source_link")][InlineData("duplicate_source")]
    [InlineData("duplicate_trigger")][InlineData("duplicate_goal")][InlineData("duplicate_zone")][InlineData("duplicate_quest")]
    [InlineData("empty_targets")][InlineData("traversal")][InlineData("package")][InlineData("member")]
    [InlineData("profile")][InlineData("id")][InlineData("enabled")][InlineData("missing")]
    public void InvalidPolicyIsRejected(string mutation)
        => Assert.Throws<ClassicDataException>(() => PuzzleHelpersLoader.Load(MutatedPolicy(mutation)));

    [Theory]
    [InlineData("../puzzles-october-2010.yaml")]
    [InlineData("/tmp/puzzles-october-2010.yaml")]
    [InlineData("rules/another.yaml")]
    public void ProfileReferenceCannotEscapeOrSubstitutePolicy(string reference)
        => Assert.Throws<ClassicDataException>(() => PuzzleHelpersLoader.LoadForProfile(Profile(PuzzleHelpers.OctoberProfile, reference), ClassicDataFixture.Root));

    [Fact]
    public void MissingConfiguredFileFailsInsteadOfSkipping()
        => Assert.Throws<ClassicDataException>(() => PuzzleHelpersLoader.LoadForProfile(Profile(PuzzleHelpers.OctoberProfile),
            Path.Combine(Path.GetTempPath(), "w101c-missing-puzzle-" + Guid.NewGuid().ToString("N"))));
}
