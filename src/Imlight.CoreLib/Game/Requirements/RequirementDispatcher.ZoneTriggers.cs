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
 * CLASSIC: tells the zone trigger plan (ZoneTriggerPlans) whether a
 * requirement class can be evaluated at all on this server.
 *
 * USAGE EXAMPLE:
 * RequirementDispatcher.HasHandlerFor(typeof(ReqHasQuest));
 *
 * NOTE:
 * A class without a handler evaluates false (EvaluateIndividualRequirement).
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System.Linq;
using Type = System.Type;

namespace Imlight.CoreLib.Game.Requirements;

public static partial class RequirementDispatcher {

    /// <summary>
    /// CLASSIC: true when some handler evaluates requirements of <paramref name="requirementType"/>.
    /// </summary>
    internal static bool HasHandlerFor(Type requirementType)
        => requirementType is not null && s_requirementHandlers.Keys.Any(handler => handler.IsGenericTypeDefinition
            || (handler.BaseType?.IsGenericType == true && handler.BaseType.GetGenericArguments()[0] == requirementType));

}
