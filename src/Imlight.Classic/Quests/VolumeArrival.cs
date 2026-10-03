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
 * CLASSIC: a wizard who arrives inside a trigger volume. Stock Imlight never
 * posted its enter events, so a volume around a landing spot never fired:
 * the Jade Palace's Jade Oni (An Imperial Cure) and Sesshu in MS_Plague2_T5
 * (Air Apparent). The events now post on the wizard's first move inside the
 * volume, and a trigger that teleports does not fire on them, so a landing
 * spot inside an exit volume never bounces the wizard back out.
 *
 * USAGE EXAMPLE:
 * listens = ... && VolumeArrival.Fires(message.ArrivedInside, triggerTeleports);
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

namespace Imlight.Classic.Quests;

/// <summary>Which triggers an arrival-inside volume event fires.</summary>
public static class VolumeArrival {

    /// <summary>False for a trigger that teleports when the event is an arrival inside its volume.</summary>
    public static bool Fires(bool arrivedInside, bool triggerTeleports)
        => !(arrivedInside && triggerTeleports);

}
