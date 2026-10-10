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
 * CLASSIC QUEST ENGINE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: a boss that fights only when a zone script sends it. Prince Gobblestone (WC_Colossus_ThroneRoom, template
 * 35120) has an aggro radius of 1, so walking up to him never starts the duel; the room's MakeWar and Trigger Aggro
 * triggers (result ResInitiateCombat) fire when the wizard enters, long before the Prince is near, and found no
 * creature in range. Stock Imlight disarmed both triggers on that first empty try, the gate never opened
 * ("Trigger Mob Death" needs the Prince dead) and the quest chain behind it stopped.
 *
 * USAGE EXAMPLE:
 * if (ScriptedAggro.Engages(isMonster, proximity, armed, inReach)) { ...start the duel... }
 *
 * NOTE:
 * A ResInitiateCombat that finds nothing now arms every scripted-only creature in the zone for that wizard; the
 * creature then fights the wizard when he comes within Reach of it. The Prince is not started from across the room.
 * The reach (600, a little over the 350 of an ordinary gobbler) is ours.
 *
 * TODO:
 * - KingsIsle's real ResInitiateCombat target rule is not documented; no dated source for the room's fight start was
 *   found (unverified).
 *
 * Created by: Nick with Claude Code (claude-sonnet-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

namespace Imlight.Classic.Quests;

/// <summary>When a scripted-only boss starts its duel.</summary>
public static class ScriptedAggro {

    /// <summary>A creature whose own aggro radius is at most this fights only when a script arms it.</summary>
    public const float ScriptedOnlyRadius = 1f;

    /// <summary>How near an armed wizard must come before the boss attacks.</summary>
    public const float Reach = 600f;

    /// <summary>True for a dueling creature that never aggroes by walking up to it.</summary>
    public static bool IsScriptedOnly(bool isMonster, float proximity) => isMonster && proximity <= ScriptedOnlyRadius;

    /// <summary>True when the armed wizard is within reach of the scripted-only boss and it should attack now.</summary>
    public static bool Engages(bool isMonster, float proximity, bool armed, bool inReach)
        => armed && inReach && IsScriptedOnly(isMonster, proximity);
}
