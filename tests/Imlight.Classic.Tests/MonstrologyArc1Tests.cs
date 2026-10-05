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

    // CLASSIC (owner request 2026-10-05): only 2009 creatures, even inside an Arc 1 folder.
    [Theory]
    [InlineData(35085u, "WC", true)]      // Ghost-Blue-L01 (Wizard City; the rig's Monstrology creature)
    [InlineData(346694u, "GH", false)]    // GH2-Bear-Valkyrie-2-BOSS-R10: Wintertusk (Hrundle Fjord), Nov 2010
    [InlineData(1668223u, "KT", false)]   // KT_SE_Beetle_Scarab_C_01: Selenopolis
    [InlineData(1302672u, "MB", false)]   // G14-BP-Golem_Steel_Boss: 2014 Marleybone dungeon
    [InlineData(1466263u, "WC", false)]   // Wooden Skeleton Key boss room
    [InlineData(1340305u, "WC", false)]   // Heroic_WC_Rattlebones
    [InlineData(35085u, "MR", false)]     // a 2009 id is still refused outside an Arc 1 folder
    public void OnlyCreaturesOfThe2009WorldCount(uint creature, string folder, bool counts)
        => Assert.Equal(counts, MonstrologyCardCatalog.Is2009Creature(creature, folder));

    [Fact]
    public void The2009SetHasTheArc1CreaturesAndNoLaterOnes() {
        Assert.Equal(375, MonstrologyEra2009.Creatures.Count);
        Assert.DoesNotContain(346694u, MonstrologyEra2009.Creatures); // the owner's 2026-10-05 minion (Wintertusk)
    }
}
