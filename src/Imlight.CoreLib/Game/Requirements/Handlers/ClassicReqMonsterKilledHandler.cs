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
 * CLASSIC: the Monster_Killed trigger check. True when a monster the
 * event reports is one the requirement names.
 *
 * USAGE EXAMPLE:
 * Picked by RequirementDispatcher for a ClassicReqMonsterKilled, which only
 * ClassicZoneTypeRegistry decodes (zone triggers on "Monster_Killed").
 *
 * NOTE:
 * The defeated monsters come from KilledMonsterScope, which the trigger
 * supervisor and the trigger set while they evaluate a Monster_Killed
 * event; outside one the check is false. The match itself is
 * Imlight.Classic.Quests.KilledMonster.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Requirements.Handlers;

internal sealed class ClassicReqMonsterKilledHandler : BaseRequirementHandler<ClassicReqMonsterKilled> {

    public override bool Evaluate(IRequirementContext context) {
        if (!ClassicQuestEngine.IsActive || KilledMonsterScope.Current is not { Length: > 0 } killed) {
            return false;
        }

        return killed.Any(templateId => CoreObjectFactory.GetCoreTemplate(templateId) is GameObjectTemplate template
            && KilledMonster.Matches(Requirement.m_adjectiveList, template.m_objectName,
                template.m_adjectiveList?.Select(adjective => (string) adjective)));
    }

}
