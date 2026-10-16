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
 * XP BANKED AT AN OLD LEVEL CAP
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the decision behind the XP cap migration (CoreLib
 * XpCapMigration). Under the FillBar ceiling a wizard at the cap keeps
 * earning XP up to one point short of the next level (681,129 at level 50).
 * When a profile raises the cap (october-2010-arc1's 50 to celestia-2010's
 * 60, or any later raise), that banked XP would level them at once. The
 * archived October 2010 notes say it is not paid: "You will not receive
 * experience from prior quests and duels that you completed when you were
 * level 50" (owner ruling 2026-10-16: "reset it"). So a wizard exactly at
 * the old cap with more XP than that level's total goes back to the total,
 * once, when the database first runs under the higher cap.
 *
 * NOTE:
 * The database records the cap its XP was settled under (Meta/XpCapSettlement,
 * a number, not a flag), so every later raise settles again and a lowered cap
 * is recorded too. A database without the record was played under a cap of 50
 * (every profile before celestia-2010). A profile without a cap
 * (dev-unrestricted) changes nothing and leaves the record as it is.
 * Wizards below the old cap, above it, or with XP within their level are never
 * touched.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/16/2026
 */

namespace Imlight.Classic.Admin;

/// <summary>CLASSIC: what the XP cap migration does at start-up.</summary>
/// <param name="SweepFromCap">The old cap whose banked XP is reset, or null when no wizard is touched.</param>
/// <param name="CapToRecord">The cap to record afterwards, or null to leave the record as it is.</param>
public sealed record XpCapPlan(int? SweepFromCap, int? CapToRecord) {

    /// <summary>True when the migration has nothing to do.</summary>
    public bool NothingToDo => SweepFromCap is null && CapToRecord is null;

}

/// <summary>CLASSIC: the XP-at-an-old-cap rules (October 2010 notes; owner ruling 2026-10-16).</summary>
public static class XpCapSettlement {

    /// <summary>The marker document's id.</summary>
    public const string DocumentId = "Meta/XpCapSettlement";

    /// <summary>The cap a database without a record was played under (every profile before celestia-2010).</summary>
    public const int UnrecordedCap = ClassicProfiles.Arc1LevelCap;

    /// <summary>
    /// The plan for a database whose XP was settled under <paramref name="recordedCap"/> (null: no record) when the
    /// server now runs under <paramref name="currentCap"/> (null: no cap).
    /// </summary>
    public static XpCapPlan Plan(int? recordedCap, int? currentCap) {
        if (currentCap is not int cap || cap < 1) {
            return new XpCapPlan(null, null);
        }

        var previous = recordedCap ?? UnrecordedCap;
        if (cap > previous) {
            return new XpCapPlan(previous, cap);
        }

        return recordedCap == cap ? new XpCapPlan(null, null) : new XpCapPlan(null, cap);
    }

    /// <summary>
    /// The XP a saved wizard keeps when the cap rises from <paramref name="oldCap"/>: the total for their level when
    /// they are exactly at the old cap and hold more than it; otherwise null (unchanged).
    /// </summary>
    /// <param name="level">The saved level.</param>
    /// <param name="xp">The saved XP total.</param>
    /// <param name="oldCap">The cap the XP was earned under.</param>
    /// <param name="xpToReachLevel">The XP total on reaching <paramref name="level"/> (0 when unknown: unchanged).</param>
    public static int? Settled(int level, int xp, int oldCap, int xpToReachLevel)
        => level == oldCap && xpToReachLevel > 0 && xp > xpToReachLevel ? xpToReachLevel : null;

}
