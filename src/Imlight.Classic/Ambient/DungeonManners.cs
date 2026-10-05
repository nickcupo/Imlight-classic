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
 * DUNGEON MANNERS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-04): ambient wizards grouping up for a dungeon the way
 * 2009 players did (owner: "script them for dungeons too"). A real player
 * starts a dungeon sigil's countdown; ambient wizards that can see the
 * sigil and are of a fitting level may walk over and step on with them.
 * The rules here are the pure part:
 *   - Settings ([Classic] AmbientWizardDungeons, AmbientWizardDungeonChance,
 *     AmbientWizardDungeonHelpers): on/off, the chance that a fitting
 *     wizard comes over (0.35), at most how many come (2).
 *   - Who fits: within RecruitDistance with a clear line, level from
 *     five below the player to twelve above (a level 3 is no help in a
 *     level 20 dungeon; a level 40 walking a level 5 through is what a
 *     friendly high level did), and the player has not said no lately.
 *   - Slots: never more ambient wizards than the instance can hold after
 *     every real player (a one-wizard tower takes none), and a real player
 *     always takes an ambient wizard's place (InstanceGroup's bump).
 *   - Inside: each helper keeps its own spot a few steps behind the
 *     leader (FollowSpot), never in front; it walks only when the leader
 *     has moved off (FollowSlack), so it waits at doors and in rooms.
 *   - Giving up: a helper that cannot reach the leader for StuckLimit, or
 *     has been far from it for FarLimit, says bye and leaves; nobody is
 *     kept in a dungeon past LongestRun.
 *
 * USAGE EXAMPLE:
 * var settings = DungeonSettings.Parse("true", "0.35", "2");
 * if (DungeonManners.Fits(helperLevel, leaderLevel)) { ... }
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using System.Globalization;
using System.Numerics;

namespace Imlight.Classic.Ambient;

/// <summary>The dungeon switches for ambient wizards.</summary>
/// <param name="Enabled">[Classic] AmbientWizardDungeons (default true).</param>
/// <param name="JoinChance">[Classic] AmbientWizardDungeonChance, 0..1 (default 0.35): for each fitting wizard.</param>
/// <param name="MaxHelpers">[Classic] AmbientWizardDungeonHelpers, 0..3 (default 2): at most this many per group.</param>
public sealed record DungeonSettings(bool Enabled, double JoinChance, int MaxHelpers) {

    public const double DefaultChance = 0.35;
    public const int DefaultHelpers = 2;

    public static DungeonSettings Off { get; } = new(false, 0, 0);

    /// <summary>Parses the three settings; blank or bad values take their defaults.</summary>
    public static DungeonSettings Parse(string? enabled, string? chance, string? helpers) {
        var on = string.IsNullOrWhiteSpace(enabled) || !bool.TryParse(enabled.Trim(), out var flag) || flag;
        var p = double.TryParse(chance?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && double.IsFinite(c)
            ? Math.Clamp(c, 0, 1) : DefaultChance;
        var n = int.TryParse(helpers?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            ? Math.Clamp(h, 0, 3) : DefaultHelpers;
        return on && n > 0 && p > 0 ? new DungeonSettings(true, p, n) : Off;
    }

}

/// <summary>The dungeon rules (see the file header).</summary>
public static class DungeonManners {

    /// <summary>How far from a sigil a wizard notices a player starting its countdown (and can still make it).</summary>
    public const float RecruitDistance = 1200f;

    /// <summary>A helper walks after the leader once the leader is farther than this from its spot.</summary>
    public const float FollowSlack = 260f;

    /// <summary>How far behind the leader the helpers' spots are.</summary>
    public const float FollowDistance = 170f;

    /// <summary>A helper this far from the leader is "far" (lost, left behind).</summary>
    public const float FarDistance = 1500f;

    /// <summary>A helper that cannot route to the leader this long gives up.</summary>
    public static readonly TimeSpan StuckLimit = TimeSpan.FromSeconds(25);

    /// <summary>A helper far from the leader (in the same zone) this long gives up.</summary>
    public static readonly TimeSpan FarLimit = TimeSpan.FromSeconds(60);

    /// <summary>Nobody stays in a dungeon run longer than this.</summary>
    public static readonly TimeSpan LongestRun = TimeSpan.FromMinutes(75);

    /// <summary>After the leader goes through a door, a helper follows within this (walking to the door first).</summary>
    public static readonly TimeSpan DoorFollowLimit = TimeSpan.FromSeconds(8);

    /// <summary>How long a player who said no to dungeon helpers is left alone.</summary>
    public static readonly TimeSpan QuietAfterNo = TimeSpan.FromMinutes(30);

    /// <summary>True when a helper of <paramref name="helperLevel"/> fits a run led by a <paramref name="leaderLevel"/>.</summary>
    public static bool Fits(int helperLevel, int leaderLevel)
        => helperLevel >= Math.Max(1, leaderLevel - 5) && helperLevel <= leaderLevel + 12;

    /// <summary>The players an instance holds: four, or its hard limit when that is lower (a one-wizard tower).</summary>
    public static int Capacity(int zoneHardLimit) => zoneHardLimit is > 0 and < 4 ? zoneHardLimit : 4;

    /// <summary>
    /// How many more ambient wizards may join a group of <paramref name="realPlayers"/> real players and
    /// <paramref name="helpers"/> helpers: what the instance holds after the real players, never more than
    /// <paramref name="maxHelpers"/> helpers in all. A tower for one takes none; a full group none.
    /// </summary>
    public static int OpenHelperSlots(int realPlayers, int helpers, int zoneHardLimit, int maxHelpers) {
        var capacity = Capacity(zoneHardLimit);
        if (capacity <= 1 || realPlayers <= 0) {
            return 0;
        }

        return Math.Max(0, Math.Min(maxHelpers - helpers, capacity - realPlayers - helpers));
    }

    /// <summary>
    /// The <paramref name="index"/>-th helper's spot: <see cref="FollowDistance"/> behind the leader (who faces
    /// <paramref name="leaderHeading"/>, radians from +X), the first a little left, the second a little right, the
    /// third straight behind. Never ahead of the leader.
    /// </summary>
    public static Vector2 FollowSpot(Vector2 leader, float leaderHeading, int index) {
        var angle = leaderHeading + MathF.PI + (index % 3) switch { 0 => 0.55f, 1 => -0.55f, _ => 0f };
        var distance = FollowDistance + index / 3 * 90f;
        return leader + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance;
    }

    /// <summary>True when the leader has moved far enough from the helper's spot that the helper should walk.</summary>
    public static bool ShouldWalk(Vector2 helper, Vector2 spot) => Vector2.Distance(helper, spot) > FollowSlack;

    /// <summary>Why a helper leaves the run now, or null to stay.</summary>
    public static string? GiveUpReason(TimeSpan unreachableFor, TimeSpan farFor, TimeSpan inRunFor) {
        if (unreachableFor >= StuckLimit) {
            return "stuck";
        }

        if (farFor >= FarLimit) {
            return "lost";
        }

        return inRunFor >= LongestRun ? "too long" : null;
    }

}
