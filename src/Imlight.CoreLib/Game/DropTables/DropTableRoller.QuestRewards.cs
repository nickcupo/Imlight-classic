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
 * DROP TABLES
 * ========================================================================
 * 
 * PURPOSE:
 * CLASSIC: rolls a quest's reward tables. Each table grants every item it
 * lists whose requirements pass, once each, in listed order, and its
 * potion slot.
 * 
 * USAGE EXAMPLE:
 * DropTableRoller.RollQuestReward(tableNames, playerRef, playerObj, wizard);
 * 
 * NOTE:
 * SpiralDB's quest tables are reward lists: a school item carries a
 * ReqSchoolOfFocus, so one school still gets one of them, and an item every
 * school gets (a housing item, a treasure card) comes on top. ResDropTable's
 * m_maxRolls is 1 on 871 of SpiralDB's 874 references and 0 on 3, so it is
 * not an item count. Off the classic quest engine this is Roll; mob loot
 * always goes through Roll and picks one item at random.
 * 
 * TODO:
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.DropTables;

public static partial class DropTableRoller {

    private static readonly ConcurrentDictionary<(string Table, string Item), bool> s_reportedUnknownItems = new();

    /// <summary>
    /// Rolls the drop tables of a quest's reward. On the classic quest engine every table grants all of its
    /// items that pass their requirements, and its potion slot; otherwise this is <see cref="Roll"/>.
    /// </summary>
    /// <param name="dropTableNames">The quest's reward table names.</param>
    /// <param name="playerRef">Player actor reference for requirements context.</param>
    /// <param name="playerObj">Player core object for requirements context.</param>
    /// <param name="wizard">Player wizard data for requirements context.</param>
    /// <returns>The combined reward of every table.</returns>
    public static DropTableResult RollQuestReward(string[] dropTableNames,
                                                  IActorRef playerRef,
                                                  CoreObject playerObj,
                                                  Wizard wizard) {
        if (!ClassicQuestEngine.IsActive) {
            return Roll(dropTableNames, playerRef, playerObj, wizard);
        }

        var random = new Random();
        var reward = CreateEmptyResult(string.Join(",", dropTableNames ?? []));
        foreach (var tableName in dropTableNames ?? []) {
            if (string.IsNullOrWhiteSpace(tableName)) {
                continue;
            }

            var dropTable = DropTableCollection.GetDropTable(tableName);
            if (dropTable is null) {
                Logger.Warning("Quest reward table {Table} is not in the database.", Logger.Args(tableName));

                continue;
            }

            if (random.NextDouble() > dropTable.RollChance) {
                continue;
            }

            reward.GoldAmount += RollGoldAmount(dropTable, random);
            reward.ExperienceAmount += dropTable.ExperienceAmount;
            reward.TrainingPoints += dropTable.TrainingPoints;
            reward.GrantsPotionSlot |= dropTable.GrantsPotionSlot;
            reward.Items.AddRange(EveryRewardItem(dropTable, playerRef, playerObj, wizard));
        }

        return reward;
    }

    private static List<DropItemResult> EveryRewardItem(DropTable dropTable,
                                                        IActorRef playerRef,
                                                        CoreObject playerObj,
                                                        Wizard wizard) {
        var results = new List<DropItemResult>();
        foreach (var item in dropTable.Items ?? []) {
            if (item is null || string.IsNullOrEmpty(item.ItemId)) {
                continue;
            }

            if (item.Requirements is not null) {
                var context = new GenericRequirementContext(
                    requirements: item.Requirements,
                    playerRef: playerRef,
                    playerObj: playerObj,
                    wizard: wizard
                );
                if (!RequirementDispatcher.EvaluateRequirements(item.Requirements, context)) {
                    continue;
                }
            }

            // Only client item templates are granted.
            if (!ulong.TryParse(item.ItemId, out var templateId) || CoreObjectFactory.GetCoreTemplate(templateId) is null) {
                if (s_reportedUnknownItems.TryAdd((dropTable.Name, item.ItemId), true)) {
                    Logger.Warning("Quest reward table {Table} lists {ItemId} ({ItemName}), which is no client template; it is not granted.",
                        Logger.Args(dropTable.Name, item.ItemId, item.ItemName));
                }

                continue;
            }

            results.Add(new DropItemResult {
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                Quantity = 1
            });
        }

        return results;
    }

}
