// CLASSIC: Clockwork it Out (MB-YARD3-C01-001) was our own helper for KatzLab's Gate 1; r806919's collision and nav
// have no ground within ~1130 units of its "Gate 1 Open" volume, so it was retired (coordinator-approved, 2026-10-29).
// The template file stays, so the retirement can be undone.

using System.IO;
using System.Linq;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ClockworkItOutRetiredTests {

    private const string Quest = "MB-YARD3-C01-001";

    [Theory]
    [InlineData("late-2009")] [InlineData("arc1-2009h1")] [InlineData("october-2010-arc1")]
    [InlineData("celestia-2010")] [InlineData("wintertusk-2011")]
    public void EveryDatedProfileDisablesIt(string profile)
        => Assert.Contains(Quest, ClassicDataFixture.LoadProfile(profile).DisabledQuests);

    [Fact]
    public void ItsTemplateStaysAndNoOtherQuestNeedsIt() {
        var folder = Path.Combine(ClassicDataFixture.Root, "spiraldb-overlay", "QuestTemplates");
        Assert.True(File.Exists(Path.Combine(folder, Quest + ".json")));
        var needing = Directory.EnumerateFiles(folder, "*.json")
            .Where(file => Path.GetFileNameWithoutExtension(file) != Quest && File.ReadAllText(file).Contains(Quest))
            .Select(Path.GetFileName);
        Assert.Empty(needing);
    }

    [Theory]
    [InlineData("badges/badges-2009.yaml")] [InlineData("badges/badges-2010.yaml")]
    public void NoBadgeCountsIt(string file)
        => Assert.DoesNotContain(Quest, File.ReadAllText(Path.Combine(ClassicDataFixture.Root, file)));
}
