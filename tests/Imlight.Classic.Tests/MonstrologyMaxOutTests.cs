using Imlight.CoreLib.Game.Monstrology;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class MonstrologyMaxOutTests {

    [Fact]
    public void MaxOutReachesTheTopLevelAndFillsAnimusWithoutLoweringAny() {
        var state = new MonstrologyLedger { OwnerId = 7 };
        state.Animus[11] = 60000;
        int[] thresholds = [0, 100, 300, 600];

        var result = MonstrologyRules.MaxOut(state, thresholds, [11u, 12u, 0u], 1000);

        Assert.Equal(MonstrologyResult.Applied, result);
        Assert.Equal((3, 600), (state.Level, state.Experience));
        Assert.Equal(60000, state.Animus[11]);
        Assert.Equal(1000, state.Animus[12]);
        Assert.False(state.Animus.ContainsKey(0));
    }

}
