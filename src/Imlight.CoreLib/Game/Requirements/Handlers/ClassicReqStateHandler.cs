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
 * CLASSIC: the ReqState trigger check. True when the zone's object with the
 * named tag is in the named state (the Temple of Storms mind puzzle).
 *
 * USAGE EXAMPLE:
 * Picked by RequirementDispatcher for a ClassicReqState, which only
 * ClassicZoneTypeRegistry decodes.
 *
 * NOTE:
 * The states live in ZoneObjectStates under the context's zone. An object
 * the zone does not track passes (Imlight.Classic.Quests.ObjectStateRules).
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */

using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic;

namespace Imlight.CoreLib.Game.Requirements.Handlers;

internal sealed class ClassicReqStateHandler : BaseRequirementHandler<ClassicReqState> {

    public override bool Evaluate(IRequirementContext context)
        => ObjectStateRules.Met(ZoneObjectStates.Find(context?.GetZoneRef()), Requirement?.m_triggerObjName,
            Requirement?.m_triggerObjState);

}
