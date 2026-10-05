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
 * REQUIREMENT SYSTEM
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: ReqTriggerState, a zone trigger is active (TRIGGER_STATE_ACTIVE)
 * or not. The Grizzleheim library's OpenDoor waits for its four element
 * checks to be enabled; the Marleybone gauntlet's event triggers check
 * each other.
 *
 * USAGE EXAMPLE:
 * Evaluated by RequirementDispatcher for a zone trigger's requirements.
 *
 * NOTE:
 * Reads the zone instance's ZoneTriggerTable in the evaluating player's
 * scope. A trigger the zone does not have, or a context without a zone,
 * passes, as an untracked state object passes ReqState.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using Imlight.CoreLib.Classic;

namespace Imlight.CoreLib.Game.Requirements.Handlers;

internal sealed class ClassicReqTriggerStateHandler : BaseRequirementHandler<ClassicReqTriggerState> {

    public override bool Evaluate(IRequirementContext context) {
        if (Requirement is null) {
            return false;
        }

        var armed = ZoneTriggerTables.Find(context?.GetZoneRef())?.IsArmedNamed(Requirement.m_triggerName, context?.GetPlayerRef());

        return armed is null || armed == Requirement.WantsActive;
    }

}
