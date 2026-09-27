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
 * SCAVENGE GOAL INDEX
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: fills in the data SpiralDB's packet-captured "Defeat and Collect"
 * (GOAL_TYPE_SCAVENGE) goals are missing, and says which defeated mobs
 * drop each goal's quest item.
 *
 * USAGE EXAMPLE:
 * ScavengeGoalIndex.ApplyTo(questTemplate);   // SpiralDB load
 * var drops = ScavengeGoalIndex.RollDrops(questName, goalName,
 *     message.MobTemplateIds, chance, remaining); // combat win
 *
 * NOTE:
 * Every SpiralDB scavenge goal is a KillCollect goal that was built from a
 * capture without m_itemAdjectives, m_itemTotal or m_tallyCounter. The
 * client has no item templates for these quest items (Spider Eyes, Tower
 * Key, ...), so, like BOUNTYCOLLECT, the collected item is only the goal
 * tally: a matching mob defeat rolls m_percentChance and bumps the count.
 *
 * The table below restores that data. Item and mob text are the goals'
 * own locale keys (quest-local WizQst keys or the mob's WizardMobs key),
 * which is what the tally descriptors carry on the WC KillCollect goals.
 * A target matches a defeated mob by its display-name key or by one of its
 * adjectives; each was checked against the client ObjectData and the
 * destination zone's spawns. Counts come from the quest's own dialog when
 * it gives one ("text"), a single named item ("one"), the Wizard101 wiki
 * ("wiki", unverified) or, where nothing states it, a guess of 3 ("guess").
 * The drop chance is 1.0, as on the captured WC KillCollect tallies.
 *
 * TODO:
 * Replace the "wiki"/"guess" counts if a capture or SpiralDB update ever
 * carries the real m_itemTotal.
 *
 * Created by: Wizard101 Classic
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
/// CLASSIC: restores the tally data and drop targets that SpiralDB's captured scavenge
/// ("Defeat and Collect") goals lack.
/// </summary>
internal static class ScavengeGoalIndex {

    /// <summary>
    /// The drop chance written into the restored tallies, matching the captured WC KillCollect goals.
    /// </summary>
    private const float COLLECT_CHANCE = 1.0f;

    /// <summary>
    /// What one scavenge goal needs: the tally total, the item and mob locale keys for the tally
    /// text, and the mob display-name keys or adjectives that drop the item.
    /// </summary>
    private sealed record Fill(int Count, string Item, string Mob, string[] Targets);

    private static readonly Dictionary<(string Quest, string Goal), Fill> s_fills = new() {
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
        [("DS-ACAD1-C04-001", "1_WizardQuestGoals_KillCollect")] = new(3, "WizQst1ED97_00000002", "WizQst1ED97_00000001", ["Spider"]),                                            // wiki: Crystal Spinnerets, any spider
        [("DS-ACAD2-C01-004", "1_WizardQuestGoals_KillCollect")] = new(1, "WizQst1EDA3_00000009", "WizardMobs_00000468", ["Kraysys"]),                                            // one: Knowledge Crystal
        [("DS-ACAD2-C01-006", "1_WizardQuestGoals_KillCollect")] = new(3, "WizQst1EDA5_00000000", "WizardMobs_00000440", ["WizardMobs_00000440"]),                                // wiki: Coal Hearts, Fangtooth Lavaspinner
        [("DS-ACAD2-C01-006", "2_WizardQuestGoals_KillCollect")] = new(3, "WizQst1EDA5_00000001", "WizardMobs_00000456", ["Burning_Flamewing"]),                                  // guess: Flame Sacs
        [("DS-ACAD2-C01-008", "1_WizardQuestGoals_KillCollect")] = new(8, "WizQst1EDA7_00000000", "WizardMobs_00000470", ["Terrorwing_Talonmaster"]),                             // wiki: Armor Pieces
    };

    /// <summary>
    /// Fills a loaded quest's scavenge goals with their tally counter and item total. Data the
    /// template already carries is kept; a scavenge goal with no entry is logged, since nothing
    /// can complete it.
    /// </summary>
    /// <param name="quest">The quest template, just deserialized.</param>
    internal static void ApplyTo(QuestTemplate quest) {
        if (quest?.m_goals is null) {
            return;
        }

        foreach (var goal in quest.m_goals.OfType<ScavengeGoalTemplate>()) {
            if (!s_fills.TryGetValue((quest.m_questName, goal.m_goalName), out var fill)) {
                Logger.Warning("Scavenge goal '{0}' in quest '{1}' has no ScavengeGoalIndex entry and cannot be completed.",
                    Logger.Args(goal.m_goalName, quest.m_questName));

                continue;
            }

            goal.m_tallyCounter ??= new TallyCounterTemplate {
                m_percentChance = COLLECT_CHANCE,
                m_descriptor = fill.Item,
                m_descriptor2 = fill.Mob,
                m_count = fill.Count,
                m_tallyResults = new ResultList { m_results = [] },
            };

            if (goal.m_itemTotal <= 0) {
                goal.m_itemTotal = goal.m_tallyCounter.m_count;
            }
        }
    }

    /// <summary>
    /// Rolls one drop per defeated mob that is a target of the given scavenge goal, and returns
    /// how many items dropped, never more than <paramref name="remaining"/>.
    /// </summary>
    /// <param name="questName">The quest the goal belongs to.</param>
    /// <param name="goalName">The scavenge goal.</param>
    /// <param name="mobTemplateIds">The defeated mobs' template IDs (from MSG_COMBATWIN).</param>
    /// <param name="chance">The drop chance per matching mob, 0 to 1.</param>
    /// <param name="remaining">How many more items the goal needs.</param>
    internal static int RollDrops(string questName, string goalName, IEnumerable<ulong> mobTemplateIds,
                                  double chance, int remaining) {
        var drops = 0;
        foreach (var mobTemplateId in mobTemplateIds ?? []) {
            if (drops >= remaining) {
                break;
            }

            if (IsTargetMob(questName, goalName, mobTemplateId) && Random.Shared.NextDouble() <= chance) {
                drops++;
            }
        }

        return drops;
    }

    /// <summary>
    /// Whether defeating the mob with this template drops the item for the given scavenge goal.
    /// </summary>
    /// <param name="questName">The quest the goal belongs to.</param>
    /// <param name="goalName">The scavenge goal.</param>
    /// <param name="mobTemplateId">The defeated mob's template ID (from MSG_COMBATWIN).</param>
    internal static bool IsTargetMob(string questName, string goalName, ulong mobTemplateId) {
        if (!s_fills.TryGetValue((questName, goalName), out var fill)) {
            return false;
        }

        if (CoreObjectFactory.GetCoreTemplate(mobTemplateId) is not GameObjectTemplate mob) {
            return false;
        }

        return fill.Targets.Contains(mob.m_displayName)
            || mob.m_adjectiveList?.Any(fill.Targets.Contains) == true;
    }

}
