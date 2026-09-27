/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * COMBAT GOAL TARGET INDEX
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: fills in the tally data SpiralDB's packet-captured combat goals
 * are missing, and says which defeated mobs count for each of them.
 *
 * USAGE EXAMPLE:
 * CombatGoalTargetIndex.ApplyTo(questTemplate);   // SpiralDB load
 * var credited = CombatGoalTargetIndex.RollCredits(questName, goalName,
 *     message.MobTemplateIds, chance, remaining);  // combat win
 *
 * NOTE:
 * Two kinds of captured goal need it. "Defeat and Collect" goals
 * (GOAL_TYPE_SCAVENGE) came without m_itemAdjectives, m_itemTotal or a
 * tally counter; the client has no item templates for their quest items
 * (Spider Eyes, Tower Key, ...), so, like BOUNTYCOLLECT, the item is only
 * the goal tally. "Defeat" goals outside Wizard City, captured as
 * GOAL_TYPE_BOUNTYCOLLECT, came without m_npcAdjectives or a tally counter,
 * so no defeated mob ever matched them. Bounty goals that carry adjectives
 * (all of Wizard City's own) are left to QuestService's adjective match.
 *
 * A target matches a defeated mob by its display-name key or by one of its
 * adjectives; each was checked against the client ObjectData and the spawns
 * of the goal's zones. None matches a mob that spawns in another world, but
 * for the Tower Archives' Loathsome Creeper, whose template Mordecai's Tower
 * (WC_Triton_T3) reuses.
 * Item and mob text are the goals' own locale keys (quest-local WizQst keys
 * or the mob's WizardMobs key), as on the WC KillCollect tallies.
 *
 * Scavenge counts come from the quest's dialog ("text"), a single named
 * item ("one"), the Wizard101 wiki ("wiki", unverified) or, where nothing
 * states it, a guess of 3 ("guess"). Bounty counts are the captured
 * m_bountyTotal; "wiki says N" marks a wiki page that disagrees. Bounty
 * targets cite the goal's Ddl_ client tag ("tag"), its portrait, the quest
 * text, the wiki quest page, or the only boss in the goal's destination
 * zone ("dest"). The chance is 1.0, as on the captured WC tallies.
 *
 * TODO:
 * - Replace the "wiki"/"guess" scavenge counts if a capture or SpiralDB
 *   update ever carries the real m_itemTotal.
 * - Captured "Defeat" goals are BOUNTYCOLLECT, whose client popups read
 *   "Collect <TALLYTEXT>"; does retail send the mob name there too?
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.WizardData.Collections;

/// <summary>
/// CLASSIC: restores the tally data and defeat targets that SpiralDB's captured scavenge
/// ("Defeat and Collect") and adjective-less bounty ("Defeat") goals lack.
/// </summary>
internal static class CombatGoalTargetIndex {

    private const float TallyChance = 1.0f;

    private sealed record Fill(int Count, string Item, string Mob, string[] Targets);

    private static readonly Dictionary<(string Quest, string Goal), Fill> s_scavengeFills = new() {
        // Krokotopia.
        [("KT-PYMHub-C01-002", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst9920_00000019", "WizQst9920_00000008", ["Pyramid_Nirini"]),                                      // one: Key, any Nirini
        [("KT-PYMHub-C01-003", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst9921_00000042", "WizardMobs_00000045", ["Biti_Nirini"]),                                        // one: Tablet
        [("KT-PYM3-C01-003", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst992F_00000029", "WizardMobs_00000051", ["WizardMobs_00000051"]),                                  // one: Key Piece, Nirini Quartermaster
        [("KT-SPH2-C01-002", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst9989_00000002", "WizardMobs_00000076", ["Nekhbet"]),                                              // one: Seal of the Fang
        [("KT-SPH2-C02-002", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst9960_00000033", "WizQst9960_00000022", ["Soul_Scavenger"]),                                       // one: Coin of Destiny
        [("KT-SPH2-C02-005", "1_WizardQuestGoals_KillCollect")] = new(6, "WizQst1EC84_00000000", "WizQst1EC84_00000003", ["WizardMobs_00000351"]),                                // wiki: Ice Shards, Glacial Avenger
        [("KT-SPH2-C02-006", "1_WizardQuestGoals_KillCollect")] = new(4, "WizQst1EC85_00000000", "WizQst1EC85_00000001", ["Ice_Weaver"]),                                         // text: "win four" Carapaces
        [("KT-SPH2-C02-008", "1_WizardQuestGoals_KillCollect")] = new(3, "WizQst1EC87_00000000", "WizQst1EC87_00000003", ["Hall_Servant"]),                                       // guess: Leather Straps
        [("KT-SPH2-C02-009", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst1EC88_00000000", "WizQst1EC88_00000003", ["Sokkwi_Crusher"]),                                     // one: Key
        [("KT-SPH3-C01-001", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst9964_00000002", "WizardMobs_00000073", ["Sokkwi_Brawler", "Sokkwi_Ripper", "Sokkwi_Soldier"]),  // one: Gladiator Mark
        [("KT-SPH3-C02-007", "1_WizardQuestGoals_KillCollect")] = new(2, "WizQst13DB5_00000014", "WizQst13DB5_00000015", ["Ice_Weaver"]),                                        // wiki: Spider Eyes
        [("KT-CRYHub-C01-001", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst99C8_00000001", "WizQst99C8_00000014", ["King_Uro_Ahnic"]),                                    // one: Cipher Part 1
        [("KT-CRYHub-C01-002", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst99C9_00000003", "WizQst99C9_00000013", ["WizardMobs_00000109"]),                                // one: Cipher Part 2
        [("KT-CRYHub-C01-003", "4_WizardQuestGoals_KillCollect")] = new(1, "WizQst99CA_00000003", "WizardMobs_00000101", ["Prince_Aka_Karanahn"]),                                // one: Temple Key

        // Marleybone.
        [("MB-AIR1-C01-004", "1_WizardQuestGoals_KillCollect")] = new(3, "WizQst9B47_00000020", "WizardMobs_00000177", ["Hooligan"]),                                             // wiki: Lockpicks

        // MooShu.
        [("MS-WAR1-C01-004", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst12DF0_00000010", "WizardMobs_00000201", ["Otomo_Courier"]),                                       // one: Attack Orders
        [("MS-WAR2-C01-004", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst12E00_00000008", "WizardMobs_00000208", ["Ronin_Keyholder"]),                                     // one: Rusty Key
        [("MS-PLAG1-C02-001", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst9C39_00000003", "WizardMobs_00000234", ["Ronin_Mutineer"]),                                      // one: Jade Handle
        [("MS-PLAG1-C02-002", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst9C3A_00000001", "WizardMobs_00000231", ["Koto"]),                                                // one: Jade Shaft
        [("MS-PLAG1-C02-005", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst9C3D_00000001", "WizardMobs_00000229", ["Kanago"]),                                              // one: Headpiece
        [("MS-PLAG2-C04-002", "2_WizardQuestGoals_KillCollect")] = new(6, "WizQst9C53_00000000", "WizardMobs_00000237", ["WizardMobs_00000237"]),                                 // wiki: Stolen Food, Infected Villager
        [("MS-DTH1-C01-006", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst12E32_00000008", "WizardMobs_00000255", ["WizardMobs_00000255"]),                                 // one: Spirit Cage, Tomugawa the Evil
        [("MS-DTH2-C01-002", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst12E3B_00000003", "WizardMobs_00000254", ["WizardMobs_00000254"]),                                 // one: Spectral Key, Sorrow Whisperer
        [("MS-DTH2-C02-001", "1_WizardQuestGoals_KillCollect")] = new(10, "WizQst12E40_00000009", "WizardMobs_00000269", ["WizardMobs_00000269"]),                                // wiki: Dark Souls, Walking Dead

        // Dragonspyre.
        [("DS-LIB1-C01-001", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst1ECF5_00000000", "WizardMobs_00000409", ["Lothesome_Creeper"]),                                   // one: Tower Key
        [("DS-LIB3-C01-003", "2_WizardQuestGoals_KillCollect")] = new(1, "WizQst1ED16_00000000", "WizardMobs_00000507", ["WizardMobs_00000507"]),                                 // one: Portal Stone, Vasek Ashweaver
        [("DS-LIB3-C02-002", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst1ED19_00000000", "WizardMobs_00000434", ["Crystal_Crawler"]),                                     // one: Pristine Crystal
        [("DS-NEC1-C01-001", "1_WizardQuestGoals_KillCollect")] = new(3, "WizQst1ED27_00000013", "WizQst1ED27_00000001", ["Terrorwing_Warlock"]),                                 // wiki: Textbooks
        [("DS-NEC1-C01-005", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst1ED2B_00000000", "WizardMobs_00000483", ["WizardMobs_00000483"]),                                 // one: Reins, The Collector
        [("DS-NEC1-C05-003", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst1ED84_00000000", "WizardMobs_00000490", ["WizardMobs_00000490"]),                                 // one: Crystal Hammer, Gallium Juggernaut
        [("DS-NEC2-C01-001", "1_WizardQuestGoals_KillCollect")] = new(3, "WizQst1ED39_00000000", "WizardMobs_00000463", ["Manascale_Sorcerer"]),                                  // wiki: Knowledge Crystals
        [("DS-ACAD1-C04-001", "1_WizardQuestGoals_KillCollect")] = new(3, "WizQst1ED97_00000002", "WizQst1ED97_00000001",
            ["WizardMobs_00000433", "WizardMobs_00000434", "WizardMobs_00000435", "WizardMobs_00000436", "WizardMobs_00000438"]),     // wiki: Crystal Spinnerets, the Crystal Grove spiders
        [("DS-ACAD2-C01-004", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst1EDA3_00000009", "WizardMobs_00000468", ["Kraysys"]),                                            // one: Knowledge Crystal
        [("DS-ACAD2-C01-006", "1_WizardQuestGoals_KillCollect")] = new(3, "WizQst1EDA5_00000000", "WizardMobs_00000440", ["WizardMobs_00000440"]),                                // wiki: Coal Hearts, Fangtooth Lavaspinner
        [("DS-ACAD2-C01-006", "2_WizardQuestGoals_KillCollect")] = new(3, "WizQst1EDA5_00000001", "WizardMobs_00000456", ["Burning_Flamewing"]),                                  // guess: Flame Sacs
        [("DS-ACAD2-C01-008", "1_WizardQuestGoals_KillCollect")] = new(8, "WizQst1EDA7_00000000", "WizardMobs_00000470", ["Terrorwing_Talonmaster"]),                             // wiki: Armor Pieces
    };

    private static readonly Dictionary<(string Quest, string Goal), Fill> s_bountyFills = new() {
        // Krokotopia.
        [("KT-CRYHub-C01-003", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000114", ["WizardMobs_00000114"]), // Prince Mkhai Karanahn; tag, wiki
        [("KT-CRYHub-C01-003", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000105", ["WizardMobs_00000105"]), // Prince Meti Karanahn; tag, wiki
        [("KT-CRYHub-C01-003", "3_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000108", ["WizardMobs_00000108"]), // Prince Ati Karanahn; tag, wiki
        [("KT-CRYHub-C01-004", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000091", ["WizardMobs_00000091"]), // Krokopatra; tag, wiki
        [("KT-PYM2-C01-002", "1_WizardQuestGoals_Kill")] = new(3, null, "WizardMobs_00000042", ["WizardMobs_00000042"]), // Nirini Warrior; tag, wiki; wiki says 4
        [("KT-PYM2-C01-002", "2_WizardQuestGoals_Kill")] = new(3, null, "WizardMobs_00000309", ["WizardMobs_00000309"]), // Flame Guardian; tag, wiki; wiki says 4
        [("KT-PYM2-C01-003", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000054", ["WizardMobs_00000054"]), // Nebit Nirini; tag
        [("KT-PYM2-C01-003", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000039", ["WizardMobs_00000039"]), // Akori Nirini; tag
        [("KT-PYM2-C01-003", "3_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000048", ["WizardMobs_00000048"]), // Shai Nirini; tag
        [("KT-PYM2-C01-003", "4_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000084", ["WizardMobs_00000084"]), // Edo Nirini; tag
        [("KT-PYMHub-C01-005", "2_WizardQuestGoals_00000201")] = new(1, null, "WizardMobs_00000035", ["WizardMobs_00000035"]), // Krokenkahmen; quest text, portrait
        [("KT-SPH2-C01-001", "1_WizardQuestGoals_Kill")] = new(4, null, "WizardMobs_00000069", ["WizardMobs_00000069", "WizardMobs_00000072"]), // Sokkwi Crusher, Sokkwi Protector; tags, wiki; wiki says 8
        [("KT-SPH2-C02-001", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000079", ["WizardMobs_00000079"]), // Odji Sokkwi; tag
        [("KT-SPH3-C02-001", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000081", ["WizardMobs_00000081"]), // Sokkwi Ripper; tag, wiki
        [("KT-SPH3-C02-002", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000058", ["WizardMobs_00000058"]), // Krag Stonechin; tag
        [("KT-SPH3-C02-003", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000059", ["WizardMobs_00000059"]), // Bort Malletmane; tag, wiki
        [("KT-SPH3-C02-004", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000061", ["WizardMobs_00000061"]), // Itennu Sokkwi; tag, wiki
        [("KT-SPH3-C02-005", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000071", ["WizardMobs_00000071"]), // Khai Amahte the Great; tag, wiki
        [("KT-SPH3-C02-006", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000060", ["WizardMobs_00000060"]), // Wild Sunbird; tag, wiki
        [("KT-SPH3-C02-008", "1_WizardQuestGoals_Kill")] = new(2, null, "WizardMobs_00000305", ["WizardMobs_00000305"]), // Sand Stalker; tag, wiki
        [("KT-SPH3-C02-009", "1_WizardQuestGoals_Kill")] = new(2, null, "WizardMobs_00000075", ["WizardMobs_00000075"]), // Sokkwi Gouger; tag, wiki
        [("KT-SPHub-C01-003", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000057", ["WizardMobs_00000057"]), // Keeper of the Fang; tag, wiki
        [("KT-SPHub-C01-004", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000063", ["WizardMobs_00000063"]), // Krokhotep; tag, wiki

        // Marleybone.
        [("MB-AIR1-C01-005", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000146", ["Gearhead_Destroyer"]), // Gearhead Destroyer; tag, wiki; adjective, its minions share the name
        [("MB-AIR2-C02-003", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000148", ["WizardMobs_00000148"]), // Potbelly; tag, wiki
        [("MB-AIR2-C03-001", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000173", ["WizardMobs_00000173"]), // Shakes O'Leary; tag, wiki
        [("MB-AIR2-C03-002", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000186", ["WizardMobs_00000186"]), // Timmy Icepick; tag, wiki
        [("MB-AIRHub-C02-004", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000130", ["WizardMobs_00000130"]), // Pops O'Leary; tag, wiki
        [("MB-MUSEHub-C02-001", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000144", ["WizardMobs_00000144"]), // Sprockets; wiki
        [("MB-MUSEHub-C02-002", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000150", ["WizardMobs_00000150"]), // Bellows; wiki
        [("MB-MUSEHub-C03-001", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000168", ["WizardMobs_00000168"]), // Meowiarty; portrait, wiki
        [("MB-YARD1-C01-001", "10_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000176", ["WizardMobs_00000176"]), // Gibson O'Leary; tag
        [("MB-YARD1-C01-001", "14_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000162", ["WizardMobs_00000162"]), // Agony Wraith; tag
        [("MB-YARD2-C01-001", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000169", ["WizardMobs_00000169"]), // Jacques the Scratcher; tag, wiki
        [("MB-YARD2-C01-002", "1_WizardQuestGoals_Kill")] = new(3, null, "WizardMobs_00000125", ["WizardMobs_00000125"]), // Deadly Scratcher; tag, wiki; wiki says 4
        [("MB-YARD2-C01-005", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000125", ["WizardMobs_00000125"]), // Deadly Scratcher; portrait, wiki
        [("MB-YARD2-C01-007", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000169", ["WizardMobs_00000169"]), // Jacques the Scratcher; tag, wiki
        [("MB-YARDHub-C01-003", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000167", ["WizardMobs_00000167"]), // Dr. Von Katzenstein; tag, wiki

        // MooShu.
        [("MS-DTH1-C01-003", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000256", ["WizardMobs_00000256"]), // Tamauji; tag, wiki
        [("MS-DTH1-C01-004", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000320", ["WizardMobs_00000320"]), // Ember Everburn; tag, wiki
        [("MS-DTH2-C01-001", "1_WizardQuestGoals_Kill")] = new(4, null, "WizardMobs_00000326", ["WizardMobs_00000326"]), // Cursed Ronin; tag, wiki
        [("MS-DTH2-C01-003", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000260", ["WizardMobs_00000260"]), // Usunoki; tag, wiki
        [("MS-DTH2-C01-005", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000266", ["WizardMobs_00000266"]), // Oyotomi the Defiler; tag, wiki
        [("MS-DTHHub-C01-003", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000278", ["WizardMobs_00000278"]), // Death Oni; portrait, wiki
        [("MS-MAIN-C02-002", "3_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000198", ["WizardMobs_00000198"]), // Yochimo; tag, wiki
        [("MS-MAIN-C06-004", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000200", ["WizardMobs_00000200"]), // Jade Oni; portrait, wiki
        [("MS-PLAG2-C01-003", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000238", ["WizardMobs_00000238"]), // Aiuchi; tag, wiki
        [("MS-PLAG2-C04-001", "1_WizardQuestGoals_Kill")] = new(4, null, "WizardMobs_00000240", ["WizardMobs_00000240"]), // Imitsu Defouler; tag, wiki
        [("MS-PLAG2-C04-004", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000239", ["WizardMobs_00000239"]), // Kyuto; tag, wiki
        [("MS-PLAGHub-C01-003", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000247", ["WizardMobs_00000247"]), // Plague Oni; tag, wiki
        [("MS-WAR1-C01-003", "1_WizardQuestGoals_Kill")] = new(3, null, "WizardMobs_00000203", ["WizardMobs_00000203"]), // Sanzoku Bandit; tag, wiki
        [("MS-WAR1-C01-003", "2_WizardQuestGoals_Kill")] = new(2, null, "WizardMobs_00000204", ["WizardMobs_00000204"]), // Sanzoku Outlaw; tag, wiki; wiki says 3
        [("MS-WAR1-C01-005", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000202", ["WizardMobs_00000202"]), // Do-Daga; tag, wiki
        [("MS-WAR1-C01-007", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000223", ["WizardMobs_00000223"]), // War Oni; tag, wiki
        [("MS-WAR2-C01-005", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000214", ["WizardMobs_00000214"]), // Maito; tag
        [("MS-WAR2-C02-002", "1_WizardQuestGoals_Kill")] = new(3, null, "WizardMobs_00000210", ["WizardMobs_00000210"]), // Otomo Supply Runner; tag, wiki; wiki says 4
        [("MS-WAR2-C02-003", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000211", ["WizardMobs_00000211"]), // Otomo Quartermaster; tag, wiki

        // Dragonspyre.
        [("DS-ACAD-C01-005", "3_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000418", ["WizardMobs_00000418"]), // Malistaire Drake; portrait, quest text
        [("DS-ACAD1-C02-002", "1_WizardQuestGoals_Kill")] = new(2, null, "WizardMobs_00000434", ["WizardMobs_00000434", "WizardMobs_00000435", "WizardMobs_00000436", "WizardMobs_00000438"]), // Crystal Crawler, Shimmer Widow, Ancient Crystalweaver, Faceted Spikespinner; quest text; wiki says 8; unverified set
        [("DS-ACAD1-C04-003", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000466", ["WizardMobs_00000466"]), // Chronius; tag, wiki
        [("DS-LIB-C02-001", "2_WizardQuestGoals_00000186")] = new(1, "WizQst1ECF3_00000024", "WizardMobs_00000526", ["WizardMobs_00000526"]), // Vladimir Darkflame: Portal Stone; quest text
        [("DS-LIB1-C01-003", "2_WizardQuestGoals_Kill")] = new(4, null, "WizardMobs_00000481", ["WizardMobs_00000481", "WizardMobs_00000506"]), // Restless Soldier, Wandering Wizard; tag, wiki; wiki says 8; unverified set
        [("DS-LIB1-C01-005", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000508", ["WizardMobs_00000508"]), // Petrov Gloomstrider; dest, wiki
        [("DS-LIB1-C01-008", "4_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000461", ["WizardMobs_00000461"]), // Zarathax; wiki
        [("DS-LIB2-C02-004", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000383", ["WizardMobs_00000383"]), // Earth Elemental; tag, wiki
        [("DS-LIB2-C02-006", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000417", ["WizardMobs_00000417"]), // Sea Lord; tag, wiki
        [("DS-LIB2-C03-001", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000469", ["WizardMobs_00000469"]), // Sysiphan; tag, wiki
        [("DS-LIB2-C05-001", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000494", ["WizardMobs_00000494"]), // Sandor Spearcaller; dest, wiki
        [("DS-LIB2-C06-001", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000486", ["WizardMobs_00000486"]), // Mavra Flamewing; dest, wiki
        [("DS-LIB3-C02-005", "2_WizardQuestGoals_00000195")] = new(1, "WizQst1ED1C_00000010", "WizardMobs_00000415", ["WizardMobs_00000415"]), // General Firetusk: Vault Keeper Key; wiki; unverified
        [("DS-MAIN-C01-002", "2_WizardQuestGoals_00000022")] = new(1, "WizQst1ECEA_00000020", "WizardMobs_00000452", ["WizardMobs_00000452"]), // Ghost of Sylvia Drake: Crystal on Tomb; quest text; item text unverified
        [("DS-MAIN-C01-004", "2_WizardQuestGoals_00000145")] = new(1, "WizQst23BC3_00000016", "WizardMobs_00000533", ["WizardMobs_00000533"]), // Drusilla Morningbane: Medallion of Fire; quest text, wiki
        [("DS-NEC1-C05-001", "2_WizardQuestGoals_00000009")] = new(1, "WizQst1ED82_00000014", "WizardMobs_00000500", ["WizardMobs_00000500"]), // Valeska Redwind: Drake Egg; wiki; unverified
        [("DS-NEC1-C05-004", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000493", ["WizardMobs_00000493"]), // Viktor Snowcrusher; tag, wiki
        [("DS-NEC2-C01-003", "3_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000482", ["WizardMobs_00000482"]), // Iona Pyrelance; tag, wiki
        [("DS-NEC2-C01-004", "1_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000448", ["WizardMobs_00000448"]), // Orin Grimcaster; tag, wiki
        [("DS-NEC2-C01-005", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000502", ["WizardMobs_00000502"]), // Katia Firewinter; tag, wiki
        [("DS-NEC2-C01-006", "3_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000489", ["WizardMobs_00000489"]), // Valerik Brightsword; tag, wiki
        [("DS-NEC2-C01-007", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000509", ["WizardMobs_00000509"]), // Rurik Flamesoul; tag, wiki
        [("DS-NEC2-C01-010", "2_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000387", ["WizardMobs_00000387"]), // Boris Blackrock; tag, wiki

        // Wizard City.
        [("WC-MAIN-C01-012", "3_WizardQuestGoals_Kill")] = new(1, null, "WizardMobs_00000536", ["WizardMobs_00000536"]), // Cyrus Drake; wiki
    };

    /// <summary>
    /// Fills a loaded quest's captured combat goals with their tally counter, and a scavenge
    /// goal's item total. Data the template already carries is kept; a scavenge goal, or a bounty
    /// goal without adjectives, that has no entry is logged, since nothing can complete it.
    /// </summary>
    /// <param name="quest">The quest template, just deserialized.</param>
    internal static void ApplyTo(QuestTemplate quest) {
        if (quest?.m_goals is null) {
            return;
        }

        foreach (var goal in quest.m_goals) {
            if (goal is ScavengeGoalTemplate scavengeGoal) {
                FillScavengeGoal(quest, scavengeGoal);
            } else if (goal is BountyGoalTemplate bountyGoal && !HasAdjectives(bountyGoal)) {
                FillBountyGoal(quest, bountyGoal);
            }
        }
    }

    private static void FillScavengeGoal(QuestTemplate quest, ScavengeGoalTemplate goal) {
        if (!TryGetEntry(s_scavengeFills, quest, goal, out var fill)) {
            return;
        }

        goal.m_tallyCounter ??= CreateTally(fill, fill.Count);
        if (goal.m_itemTotal <= 0) {
            goal.m_itemTotal = goal.m_tallyCounter.m_count;
        }
    }

    private static void FillBountyGoal(QuestTemplate quest, BountyGoalTemplate goal) {
        if (!TryGetEntry(s_bountyFills, quest, goal, out var fill)) {
            return;
        }

        goal.m_tallyCounter ??= CreateTally(fill, goal.m_bountyTotal > 0 ? goal.m_bountyTotal : fill.Count);
    }

    private static bool TryGetEntry(Dictionary<(string Quest, string Goal), Fill> fills, QuestTemplate quest,
                                    GoalTemplate goal, out Fill fill) {
        if (fills.TryGetValue((quest.m_questName, goal.m_goalName), out fill)) {
            return true;
        }

        Logger.Warning("Combat goal '{0}' in quest '{1}' has no CombatGoalTargetIndex entry and cannot be completed.",
            Logger.Args(goal.m_goalName, quest.m_questName));

        return false;
    }

    private static TallyCounterTemplate CreateTally(Fill fill, int count)
        => new() {
            m_percentChance = TallyChance,
            m_descriptor = fill.Item ?? fill.Mob,
            m_descriptor2 = fill.Mob,
            m_count = count,
            m_tallyResults = new ResultList { m_results = [] },
        };

    private static bool HasAdjectives(BountyGoalTemplate goal)
        => goal.m_npcAdjectives is { Count: > 0 };

    /// <summary>
    /// Whether a combat win credits the goal through this index: a scavenge goal, or a bounty goal
    /// without adjectives, that has an entry.
    /// </summary>
    /// <param name="questName">The quest the goal belongs to.</param>
    /// <param name="goal">The goal template.</param>
    internal static bool IsIndexedGoal(string questName, GoalTemplate goal)
        => goal switch {
            ScavengeGoalTemplate => s_scavengeFills.ContainsKey((questName, goal.m_goalName)),
            BountyGoalTemplate bountyGoal => !HasAdjectives(bountyGoal)
                && s_bountyFills.ContainsKey((questName, goal.m_goalName)),
            _ => false,
        };

    /// <summary>
    /// Rolls the goal's chance once per defeated mob that is one of its targets, and returns how
    /// many count toward the goal, never more than <paramref name="remaining"/>.
    /// </summary>
    /// <param name="questName">The quest the goal belongs to.</param>
    /// <param name="goalName">The indexed goal.</param>
    /// <param name="mobTemplateIds">The defeated mobs' template IDs (from MSG_COMBATWIN).</param>
    /// <param name="chance">The chance per matching mob, 0 to 1.</param>
    /// <param name="remaining">How many more the goal needs.</param>
    internal static int RollCredits(string questName, string goalName, IEnumerable<ulong> mobTemplateIds,
                                    double chance, int remaining) {
        var credits = 0;
        foreach (var mobTemplateId in mobTemplateIds ?? []) {
            if (credits >= remaining) {
                break;
            }

            if (IsTargetMob(questName, goalName, mobTemplateId) && Random.Shared.NextDouble() <= chance) {
                credits++;
            }
        }

        return credits;
    }

    /// <summary>
    /// Whether defeating the mob with this template counts toward the given indexed goal.
    /// </summary>
    /// <param name="questName">The quest the goal belongs to.</param>
    /// <param name="goalName">The indexed goal.</param>
    /// <param name="mobTemplateId">The defeated mob's template ID (from MSG_COMBATWIN).</param>
    internal static bool IsTargetMob(string questName, string goalName, ulong mobTemplateId) {
        var key = (questName, goalName);
        if (!s_scavengeFills.TryGetValue(key, out var fill) && !s_bountyFills.TryGetValue(key, out fill)) {
            return false;
        }

        if (CoreObjectFactory.GetCoreTemplate(mobTemplateId) is not GameObjectTemplate mob) {
            return false;
        }

        return fill.Targets.Contains(mob.m_displayName)
            || mob.m_adjectiveList?.Any(fill.Targets.Contains) == true;
    }

}
