using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ClassicLaterObjectsTests {

    [Fact]
    public void The2019PrivateOryanIsLaterAndTheClassicOneIsNot() {
        Assert.True(ClassicLaterObjects.IsLater(1451483));    // WC-ST01-NPC05-B, next to Private Connelly
        Assert.False(ClassicLaterObjects.IsLater(38119));     // Private Connelly
    }

}
