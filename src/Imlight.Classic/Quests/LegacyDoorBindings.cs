using System;
using System.Collections.Generic;
using System.Linq;
namespace Imlight.Classic.Quests;

// Explicit reviewed physical entrance bindings. No nearest or lamp-number inference.
public static class LegacyDoorBindings {
    public sealed record Binding(string Zone, string Event, string Tag, string Destination, int Index) {
        public IReadOnlyList<string> AlternateDestinations { get; init; } = Array.Empty<string>();
        public IEnumerable<string> Destinations { get { yield return Destination; foreach (var destination in AlternateDestinations) yield return destination; } }
    }
    private static readonly Binding[] Bindings = [
        new("DragonSpire/DS_A1_Knowledge/DS_A1Z1_WizardTower", "Enter_TeleportVol_ToTower3FromDS_WizardTower (1)", "DS_A1Z1_Tower03_Win", "DragonSpire/DS_A1_Knowledge/Interiors/DS_ToK_T3", 0x51000016),
        new("DragonSpire/DS_A1_Knowledge/DS_A1Z3_GrandChasm", "Enter_TeleportVol_ToGuantlet3Room", "DS_A1Z3_Vault20_Crystal", "DragonSpire/DS_A1_Knowledge/Interiors/DS_Chasm_Gauntlet_3Room", 0x5100001f),
        new("DragonSpire/DS_A2_Battle/DS_A2Hub_Necropolis", "Enter_Activator Volume (1)", "DS_Necropolis_Crypt10_Win", "DragonSpire/DS_A2_Battle/Interiors/DS_Necropolis_Crypt3", 0x51000022),
        new("DragonSpire/DS_A2_Battle/DS_A2Hub_Necropolis", "Enter_Activator Volume", "DS_Necropolis_Crypt11_Win", "DragonSpire/DS_A2_Battle/Interiors/DS_Necropolis_Gauntlet_3Room01", 0x51000025),
        new("DragonSpire/DS_A2_Battle/DS_A2Z1_Hatchery", "Enter_TeleportVol_ToTower1FromDS_Hatchery", "DS_A2Z1_Tower01_Win", "DragonSpire/DS_A2_Battle/Interiors/DS_Hatchery_T1", 0x51000019),
        new("DragonSpire/DS_A2_Battle/DS_A2Z3_Detention", "Enter_TeleportToTower1Vol22", "DS_A2Z3_Building06_Win", "DragonSpire/DS_A2_Battle/Interiors/DS_Detention_T1", 0x51000028),
        new("DragonSpire/DS_A2_Battle/DS_A2Z3_Detention", "Enter_TeleportVol_ToHouse1FromDetention", "DS_A2Z3_Building07_Win", "DragonSpire/DS_A2_Battle/Interiors/DS_Detention_H1", 0x5100002b),
        new("DragonSpire/DS_A2_Battle/DS_A2Z3_Detention", "Enter_TeleportVol_TeleportToTower3FromDetention", "DS_A2Z3_Tower03_Win", "DragonSpire/DS_A2_Battle/Interiors/DS_Detention_T3", 0x5100002e),
        new("DragonSpire/DS_A2_Battle/DS_A2Z3_Detention", "Enter_TeleportToTower2FromDetentionVol", "DS_A2Z3_Tower04_Win", "DragonSpire/DS_A2_Battle/Interiors/DS_Detention_T2", 0x51000031),
        new("DragonSpire/DS_A2_Battle/DS_A2Z3_Detention", "Enter_Activator Volume", "DS_A2Z3_Tower06_Win", "DragonSpire/DS_A2_Battle/Interiors/DS_Detention_Gauntlet_3Room01_Sub/3Room01_3", 0x51000034),
        new("Krokotopia/KT_Krokosphinx/KT_ChampHall", "Enter_Activator Volume ICE-C03-001", "Kroc_Sphinx_02_Window_01", "Krokotopia/KT_Krokosphinx/Interiors/KT_ChampHall_T4", 0x51000037),
        new("Krokotopia/KT_Pyramid/KT_Hall", "Enter_Activator Volume", "Kroc_Pyramid_01_Window_01", "Krokotopia/KT_Pyramid/Interiors/KT_Hall_T3", 0x51000013),
        new("Krokotopia/KT_Pyramid/KT_Hall", "Enter_Activator Volume BAL-C03-004", "Kroc_Pyramid_01_Window_02", "Krokotopia/KT_Pyramid/Interiors/KT_Hall_T2", 0x5100003a),
        new("Marleybone/MB_Hub", "Enter_Instance Bones", "MB_BonesHouse_Door", "Marleybone/Interiors/MB_SherlockHolmesHouse_Instance", 0x5100003d) { AlternateDestinations = ["Marleybone/Interiors/MB_SherlockHolmes_House"] },
        new("Marleybone/MB_Hub", "Enter_Instance Bones", "MB_BonesHouse_Win", "Marleybone/Interiors/MB_SherlockHolmesHouse_Instance", 0x51000040) { AlternateDestinations = ["Marleybone/Interiors/MB_SherlockHolmes_House"] },
        new("MooShu/MS_Death/MS_Death_Zone3_AncientTree", "Enter_TeleportVol_To Tower 4", "MS_AZ_Death03_Window_Pagoda_02", "MooShu/MS_Death/Interiors/MS_Death3_T4", 0x51000043),
        new("WizardCity/WC_Hub", "Enter_TeleportVol_Teleport location (WC_Hub WC_Headmistress_House Entrance)", "WC_Hub_H17", "WizardCity/Interiors/WC_Headmistress_House", 0x51000046),
        new("WizardCity/WC_Hub", "Enter_Activator Volume (5)", "WC_Hub_H18", "WizardCity/Interiors/WC_Library", 0x51000049),
        new("WizardCity/WC_Hub", "Enter_Volume-TeleportToTower", "WC_Hub_H19", "WizardCity/Interiors/WC_Headmaster_Tower", 0x5100004c),
        new("WizardCity/WC_Shop_Area", "Enter_TeleportVol_Teleport location (WC_Shop NPCShop Decks)", "WC_Shop_H05", "WizardCity/Interiors/WC_Shop_Decks", 0x5100004f),
        new("WizardCity/WC_Shop_Area", "Enter_TeleportVol_Teleport location (WC_Shop NPCShop Pets)", "WC_Shop_H06", "WizardCity/Interiors/WC_Shop_Pets", 0x51000052),
        new("WizardCity/WC_Shop_Area", "Enter_TeleportVol_Teleport location (WC_Shop NPCShop Dye)", "WC_Shop_H07", "WizardCity/Interiors/WC_ShopDye", 0x51000055),
        new("WizardCity/WC_Shop_Area", "Enter_TeleportVol_Teleport location (WC_Shop NPCShop Athames)", "WC_Shop_H08", "WizardCity/Interiors/WC_Shop_Athames", 0x51000058),
        new("WizardCity/WC_Shop_Area", "Enter_TeleportVol_Teleport location (WC_Shop NPCShop Amulets)", "WC_Shop_H10", "WizardCity/Interiors/WC_Shop_Amulets", 0x5100005b),
        new("WizardCity/WC_Shop_Area", "Enter_TeleportVol_Teleport location (WC_Shop Jewelry)", "WC_Shop_H12", "WizardCity/Interiors/WC_Shop_Jeweler", 0x5100005e),
        new("WizardCity/WC_Shop_Area", "Enter_TeleportVol_Teleport location (WC_Shop NPCShop Rings)", "WC_Shop_H14", "WizardCity/Interiors/WC_Shop_Rings", 0x51000061),
        new("WizardCity/WC_Shop_Area", "Enter_TeleportVol_Teleport location (WC_Shop NPCShop Shoe)", "WC_Shop_H15", "WizardCity/Interiors/WC_Shop_Boots", 0x51000064),
        new("WizardCity/WC_Shop_Area", "Enter_TeleportVol_Teleport location (WC_Shop NPCShop Hat)", "WC_Shop_H16", "WizardCity/Interiors/WC_Shop_Hat", 0x51000067),
        new("WizardCity/WC_Shop_Area", "Enter_TeleportVol_Teleport location (WC_Shop NPCShop Robes)", "WC_Shop_H17", "WizardCity/Interiors/WC_Shop_Robes", 0x5100006a),
        new("WizardCity/WC_Shop_Area", "Enter_TeleportVol_Teleport location (WC_Shop NPCShop Wands)", "WC_Shop_H18", "WizardCity/Interiors/WC_Shop_Wands", 0x5100006d),
        new("WizardCity/WC_Streets/WC_Firecat", "Enter_TeleportVol_Teleport location (Street 5 House 6 Entrance)", "WC_Firecat_H06", "WizardCity/WC_Streets/Interiors/WC_Firecat_H2", 0x5100001c),
        new("WizardCity/WC_Streets/WC_Firecat", "Enter_TeleportVol_Teleport to Flamea's House", "WC_Firecat_H16", "WizardCity/WC_Streets/Interiors/WC_Firecat_H1", 0x51000070),
        new("WizardCity/WC_Streets/WC_OldeTown", "Enter_TeleportVol_ToBazaar", "WC_Olde_Town_H02", "WizardCity/WC_Streets/Interiors/WC_OldeTown_AuctionHouse", 0x51000073),
        new("WizardCity/WC_Streets/WC_Triton", "Enter_TeleportVol_Teleport location-ThiefHouse", "WC_Triton_H18", "WizardCity/WC_Streets/Interiors/WC_Triton_H3", 0x51000076),
        new("WizardCity/WC_Streets/WC_Unicorn", "Enter_Teleport location (Street1 Hedgemaze Entrance) Activator", "WC_Unicorn_Boss01", "WizardCity/WC_Streets/Interiors/WC_Unicorn_HedgeMaze", 0x5100000a),
        new("WizardCity/WC_Streets/WC_Unicorn", "Enter_Activator Volume Totos House", "WC_Unicorn_H01", "WizardCity/WC_Streets/Interiors/WC_Unicorn_H3", 0x51000007),
        new("WizardCity/WC_Streets/WC_Unicorn", "Enter_Street 1 House 1 Entry", "WC_Unicorn_H02", "WizardCity/WC_Streets/Interiors/WC_Unicorn_H1", 0x51000001),
        new("WizardCity/WC_Streets/WC_Unicorn", "Enter_Activator Volume Dorothy's House", "WC_Unicorn_H07", "WizardCity/WC_Streets/Interiors/WC_Unicorn_H2", 0x51000004),
        new("WizardCity/WC_Streets/WC_Unicorn", "Enter_Activator Volume (2)", "WC_Unicorn_T1", "WizardCity/WC_Streets/Interiors/WC_Unicorn_T1", 0x51000010),
        new("WizardCity/WC_Streets/WC_Unicorn", "Enter_Activator Volume", "WC_Unicorn_T2", "WizardCity/WC_Streets/Interiors/WC_Unicorn_T2", 0x5100000d),
        new("Grizzleheim/GH_Raven", "Enter_TeleportVol_ToRavenFortressT8", "GH_Raven_PLamp_Glass_10", "Grizzleheim/Interiors/GH_RavenFortress_T8", 0x51000079),
        new("Krokotopia/KT_Krokosphinx/KT_Arena", "Enter_Volume-Fire-C03-002", "HD Kroc_Sphinx_03_Window_03", "Krokotopia/KT_Krokosphinx/Interiors/KT_Arena_T2", 0x5100007c),
        new("Krokotopia/KT_Krokosphinx/KT_Retreat", "Enter_Activator Volume (3)", "Sphinx_05_Window_01", "Krokotopia/KT_Krokosphinx/Interiors/KT_Retreat_T2", 0x5100007f),
        new("Krokotopia/KT_Krokosphinx/KT_Retreat", "Enter_Activator Volume (4)", "Sphinx_05_Window_02", "Krokotopia/KT_Krokosphinx/Interiors/KT_Retreat_T1", 0x51000082),
        new("Krokotopia/KT_Krokosphinx/KT_Retreat", "Enter_KeymasterTeleporter", "Sphinx_05_Window_03", "Krokotopia/KT_Krokosphinx/Interiors/KT_Retreat_T4", 0x51000085),
        new("Marleybone/MB_ScotlandYard/MB_KatzLab", "Enter_Activator Volume (2)", "MB_SY2_ClockTower_10_Win", "Marleybone/MB_ScotlandYard/Interiors/MB_KatzLab_T6", 0x51000088),
    ];
    public static IReadOnlyList<Binding> All { get; } = Array.AsReadOnly(Bindings);
    public static bool IsAuthoritativeAlias(string tag) => Bindings.Any(b => tag == b.Tag + "_d" || tag == b.Tag + "_b" || tag == b.Tag + "_y");
    public static IReadOnlyList<Binding> ForZone(string zone) => Bindings.Where(b => string.Equals(b.Zone, zone, StringComparison.OrdinalIgnoreCase)).ToArray();
}
