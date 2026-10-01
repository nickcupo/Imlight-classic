using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ClassicLaterObjectsTests {

    [Fact]
    public void NothingIsHiddenWithoutAList() {
        Assert.False(ClassicLaterObjects.IsLater(1451483, "WizardCity/WC_Streets/WC_Unicorn"));
        Assert.False(ClassicProgression.LaterObjects.Hides(1546036, "WizardCity/WC_Hub"));
    }

}
