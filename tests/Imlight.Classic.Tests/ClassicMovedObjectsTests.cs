using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ClassicMovedObjectsTests {

    [Fact]
    public void MrLincolnLeavesRavenwoodForGolemCourt() {
        Assert.True(ClassicMovedObjects.IsMovedAway(39088, "WizardCity/WC_Ravenwood"));
        Assert.False(ClassicMovedObjects.IsMovedAway(39088, "WizardCity/WC_Golem_Tower"));
        Assert.False(ClassicMovedObjects.IsMovedAway(39088, "WizardCity/WC_Ravenwood_Teleporter"));
        Assert.False(ClassicMovedObjects.IsMovedAway(1451483, "WizardCity/WC_Ravenwood"));

        var placed = Assert.Single(ClassicMovedObjects.PlacementsFor("WizardCity/WC_Golem_Tower"));
        Assert.Equal(39088UL, (ulong) placed.m_templateID);
        Assert.Equal(324.0142f, placed.m_location.X);
        Assert.Equal(515.9117f, placed.m_location.Y);
        Assert.Equal(1.661999f, placed.m_orientation.Z);
        Assert.Equal("WC-GTW-Registrar instance", (string) placed.m_zoneTag);
        Assert.Equal(1.0f, placed.m_fScale);
        Assert.Empty(ClassicMovedObjects.PlacementsFor("WizardCity/WC_Ravenwood"));
    }

    [Fact]
    public void RolandSilverheartLeavesTheArenaForUnicornWay() {
        Assert.True(ClassicMovedObjects.IsMovedAway(164327, "WizardCity/WC_Duel_Arena"));
        Assert.False(ClassicMovedObjects.IsMovedAway(164327, "WizardCity/WC_Streets/WC_Unicorn"));

        var placed = Assert.Single(ClassicMovedObjects.PlacementsFor("WizardCity/WC_Streets/WC_Unicorn"));
        Assert.Equal(164327UL, (ulong) placed.m_templateID);
        Assert.Equal("WC-ARENA-FURNITURE instance", placed.m_zoneTag);
        Assert.Equal(2687.48f, placed.m_location.X);
        Assert.Equal(2.224037f, placed.m_orientation.Z);
        Assert.Equal(Imcodec.ObjectProperty.TypeCache.LoadingType.DYNAMIC_SERVER, placed.m_loadingType);
        Assert.Empty(ClassicMovedObjects.PlacementsFor("WizardCity/WC_Duel_Arena"));
    }

    [Fact]
    public void NothingMovesWhenTheClassicQuestEngineIsOff() {
        if (ClassicQuestEngine.IsActive) {
            return;
        }

        Assert.Empty(ClassicMovedObjects.MovedInto("WizardCity/WC_Golem_Tower"));
    }

}
