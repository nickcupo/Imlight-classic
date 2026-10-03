using Imlight.CoreLib.Game.Monstrology;
using Xunit;

namespace Imlight.Classic.Tests;

// CLASSIC (owner ruling 2026-10-02): Monstrology covers the Arc 1 worlds only, judged by the creature template's
// world folder in the client's TemplateManifest.
public sealed class MonstrologyArc1Tests {

    [Theory]
    [InlineData("ObjectData/WC/WC_Ghost_LostSoul.xml", true)]
    [InlineData("ObjectData/KT/KT_Krokotomb_Mummy.xml", true)]
    [InlineData("ObjectData/GH/GH_Wolf_Warrior.xml", true)]
    [InlineData("ObjectData/MooShu/MS_Oni.xml", true)]
    [InlineData("ObjectData/MR/MR_Durvish_Captain_A_Boss_02.xml", false)]
    [InlineData("ObjectData/Community Event/CE_Roach.xml", false)]
    [InlineData("ObjectData/Gauntlet/G_Boss.xml", false)]
    [InlineData("Spells/MonsterMagic/Summon.xml", false)]
    [InlineData("", false)]
    public void OnlyArc1WorldFoldersCount(string path, bool arc1)
        => Assert.Equal(arc1, MonstrologyCardCatalog.IsArc1Folder(MonstrologyCardCatalog.FolderOf(path)));
}
