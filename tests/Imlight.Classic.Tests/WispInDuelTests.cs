// CLASSIC: health and mana wisps never heal a wizard in a duel (live bug 25, qa-s90 in KT_ChampHall: a KT_WispHealth
// floating past a duel circle took a fighting wizard from 1807 to 2002 between rounds). Evidence level: rule inference
// (health and mana in a duel change only by the duel); no dated 2009-2010 source either way was found.

using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class WispInDuelTests {

    // Wizard.IsInDuel takes the character lock (WizardCollection), which needs the shared test configuration first.
    public WispInDuelTests() => EquipmentAttachConcurrencyTests.Configure();

    [Fact]
    public void AWizardInADuelPicksUpNoWisp() {
        var wizard = new Wizard { CharId = 0x5915_0001 };
        Assert.True(WispPickup.Allowed(wizard));
        wizard.IsInDuel = true;
        Assert.False(WispPickup.Allowed(wizard));
        wizard.IsInDuel = false;                                  // the duel is over: the wisp heals again
        Assert.True(WispPickup.Allowed(wizard));
        Assert.False(WispPickup.Allowed(null!));
    }
}
