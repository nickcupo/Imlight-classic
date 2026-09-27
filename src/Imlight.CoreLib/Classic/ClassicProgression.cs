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
 * CLASSIC PROGRESSION
 * ========================================================================
 *
 * PURPOSE:
 * Holds the profile's XP table and mob reward rules, loaded at boot, for
 * MagicLevelsConfig, the combat resolver and CombatService.
 *
 * USAGE EXAMPLE:
 * ClassicProgression.Initialize(profile, classicDataRoot);   // ClassicStartup, restricted profiles only
 * if (ClassicProgression.MobRewards is { } rules) { ... }
 *
 * NOTE:
 * Null tables mean stock Imlight: the client's XP curve, 3 XP per pip
 * counted Imlight's way and SpiralDB mob loot only. A table the profile
 * names but that does not exist is a warning (ClassicStartup.CheckRuleTables);
 * one that exists but is invalid stops the boot.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

#nullable enable

using System.IO;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imlight.Common;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// The active profile's XP table and mob reward rules.
/// </summary>
public static class ClassicProgression {

    private static volatile XpTable? s_xpTable;
    private static volatile MobRewardRules? s_mobRewards;

    /// <summary>
    /// The profile's XP table, or null for the client's curve.
    /// </summary>
    public static XpTable? XpTable => s_xpTable;

    /// <summary>
    /// The profile's combat XP, gold and drop rules, or null for stock Imlight.
    /// </summary>
    public static MobRewardRules? MobRewards => s_mobRewards;

    /// <summary>
    /// Loads the tables a restricted profile names.
    /// </summary>
    /// <param name="profile">The active profile.</param>
    /// <param name="classicDataRoot">The classic-data directory.</param>
    /// <exception cref="ClassicDataException">A named table exists but is invalid.</exception>
    public static void Initialize(ClassicProfile profile, string classicDataRoot) {
        if (profile.Rules.XpTable is { } xp && File.Exists(Path.Combine(classicDataRoot, xp))) {
            s_xpTable = XpTableLoader.Load(Path.Combine(classicDataRoot, xp));
            Logger.Information("Classic XP table {Table}: levels 1-{MaxLevel}, {Total} XP to reach level {MaxLevel}.",
                Logger.Args(s_xpTable.Id, s_xpTable.MaxLevel, s_xpTable.XpToReach(s_xpTable.MaxLevel)!.Value, s_xpTable.MaxLevel));
        }

        if (profile.Rules.MobRewards is { } mob && File.Exists(Path.Combine(classicDataRoot, mob))) {
            s_mobRewards = MobRewardRulesLoader.Load(Path.Combine(classicDataRoot, mob));
            Logger.Information("Classic mob rewards {Table}: {XpPerPip} XP per pip, gold for {Ranks} ranks, {Mobs} documented mobs ({Templates} templates).",
                Logger.Args(s_mobRewards.Id, s_mobRewards.CombatXp.XpPerPip, s_mobRewards.GoldByRank.Count,
                    s_mobRewards.Mobs.Length, s_mobRewards.MobCount));
        }
    }

}
