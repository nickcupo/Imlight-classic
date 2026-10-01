using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class ClassicZoneWadsTests {

    [Theory]
    [InlineData("MooShu/MS_Hub", "MooShu-MS_Hub.wad")]
    [InlineData("MooShu-MS_Hub", "MooShu-MS_Hub.wad")]
    [InlineData("Krokotopia/KT_Tomb/KT_WellOfSpirits", "Krokotopia-KT_Tomb-KT_WellOfSpirits.wad")]
    public void AZoneNamesItsPackageTheWayThePatchServerDoes(string zone, string file)
        => Assert.Equal(System.IO.Path.Combine("zones", file), ClassicZoneWads.PathOf("zones", zone));

}
