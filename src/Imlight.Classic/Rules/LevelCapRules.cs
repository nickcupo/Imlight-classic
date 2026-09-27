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
 * CLASSIC LEVEL CAP
 * ========================================================================
 * 
 * PURPOSE:
 * The pure level and XP arithmetic behind the profile's level cap: the
 * effective max level, the level clamp, the XP ceiling at the cap and how
 * much of an XP grant still fits under it.
 * 
 * USAGE EXAMPLE:
 * var ceiling = LevelCapRules.XpCeiling(xpToLeaveCap, xpToReachCap);
 * var applied = LevelCapRules.XpToApply(currentXp, grant, ceiling);
 * 
 * NOTE:
 * XP table indexing (MagicLevelsConfig): row[L].m_xpToLevel is the total XP
 * at which a wizard leaves level L, and GetExperiencePointsAtLevel(L) is
 * row[L - 1], the XP needed to reach L (0 past the end of the table).
 * 
 * TODO:
 * - Which ceiling matches the 2009 client at level 50: a full bar or an empty one?
 * 
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;

namespace Imlight.Classic.Rules;

/// <summary>
/// Where XP stops once a wizard is at the level cap.
/// </summary>
public enum XpCapPolicy {

    /// <summary>
    /// One XP short of leaving the cap level: the bar fills and the level never ticks over.
    /// </summary>
    FillBar,

    /// <summary>
    /// Exactly the XP needed to reach the cap level: XP stops on arrival.
    /// </summary>
    StopOnArrival,

}

/// <summary>
/// Level and XP arithmetic for the level cap.
/// </summary>
public static class LevelCapRules {

    /// <summary>
    /// The lower of the stock max level and the profile's cap.
    /// </summary>
    /// <param name="stockMaxLevel">min(Root.wad max level, ini MaxLevel).</param>
    /// <param name="levelCap">The profile's cap; null for none.</param>
    /// <returns>The effective max level.</returns>
    public static int EffectiveMaxLevel(int stockMaxLevel, int? levelCap)
        => levelCap is null ? stockMaxLevel : Math.Min(stockMaxLevel, levelCap.Value);

    /// <summary>
    /// Clamps a level into [1, <paramref name="maxLevel"/>]. A max level below 1 means "unknown" and passes through.
    /// </summary>
    /// <param name="level">The requested level.</param>
    /// <param name="maxLevel">The effective max level.</param>
    /// <returns>The clamped level.</returns>
    public static int ClampLevel(int level, int maxLevel)
        => maxLevel < 1 ? level : Math.Clamp(level, 1, maxLevel);

    /// <summary>
    /// The highest XP total a capped wizard may hold, or null when the table gives nothing to cap against.
    /// </summary>
    /// <param name="xpToLeave">The XP at which a wizard leaves the cap level; 0 when the table ends there.</param>
    /// <param name="xpToReach">The XP needed to reach the cap level.</param>
    /// <param name="policy">Where XP stops.</param>
    /// <returns>The ceiling; never 0, which would freeze all XP.</returns>
    public static int? XpCeiling(int xpToLeave, int xpToReach, XpCapPolicy policy = XpCapPolicy.FillBar) {
        if (policy == XpCapPolicy.FillBar && xpToLeave > 0 && xpToLeave - 1 >= xpToReach) {
            return xpToLeave - 1;
        }

        if (xpToReach > 0) {
            return xpToReach;
        }

        return null;
    }

    /// <summary>
    /// How much of an XP grant still fits under the ceiling.
    /// </summary>
    /// <param name="currentXp">The wizard's XP total.</param>
    /// <param name="requestedXp">The grant; zero or negative passes through.</param>
    /// <param name="xpCeiling">The ceiling; null passes the grant through.</param>
    /// <returns>The XP to apply.</returns>
    public static int XpToApply(int currentXp, int requestedXp, int? xpCeiling) {
        if (xpCeiling is null || requestedXp <= 0) {
            return requestedXp;
        }

        return Math.Min(requestedXp, Math.Max(0, xpCeiling.Value - currentXp));
    }

    /// <summary>
    /// Clamps an XP total to the ceiling.
    /// </summary>
    /// <param name="xp">The XP total.</param>
    /// <param name="xpCeiling">The ceiling; null passes through.</param>
    /// <returns>The clamped total.</returns>
    public static int ClampXp(int xp, int? xpCeiling)
        => xpCeiling is null ? xp : Math.Min(xp, xpCeiling.Value);

    /// <summary>
    /// True when the wizard can still gain XP.
    /// </summary>
    /// <param name="currentXp">The wizard's XP total.</param>
    /// <param name="xpCeiling">The ceiling; null always allows.</param>
    /// <returns>True when below the ceiling.</returns>
    public static bool CanGainXp(int currentXp, int? xpCeiling)
        => xpCeiling is null || currentXp < xpCeiling.Value;

}
