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
 * CLASSIC MOB INFO
 * ========================================================================
 *
 * PURPOSE:
 * Reads a defeated mob's rank and kind from its client template for the
 * classic mob reward rules.
 *
 * USAGE EXAMPLE:
 * if (ClassicMobInfo.Of(templateId) is { } mob) { var loot = rules.Roll(mob, random); }
 *
 * NOTE:
 * The rank is the NPC behavior's m_nLevel (the 2009 wiki "Rank"); the kind
 * is boss when m_bossMob is set or m_mobTitle says Boss, elite when the
 * title says Elite, otherwise normal.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

#nullable enable

using System;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// Mob rank and kind from client templates.
/// </summary>
public static class ClassicMobInfo {

    /// <summary>
    /// The reward view of a mob template, or null when it is not an NPC.
    /// </summary>
    /// <param name="templateId">The mob's template id.</param>
    public static MobInfo? Of(ulong templateId) {
        if (CoreObjectFactory.GetCoreTemplate(templateId) is not GameObjectTemplate template) {
            return null;
        }

        var npc = template.m_behaviors?.OfType<NPCBehaviorTemplate>().FirstOrDefault();
        if (npc is null) {
            return null;
        }

        return new MobInfo(templateId, npc.m_nLevel, KindOf(npc.m_bossMob, npc.m_mobTitle.ToString()));
    }

    /// <summary>
    /// The reward kind for a boss flag and mob title.
    /// </summary>
    public static MobKind KindOf(bool bossFlag, string? mobTitle) {
        if (bossFlag || (mobTitle?.Contains("Boss", StringComparison.OrdinalIgnoreCase) ?? false)) {
            return MobKind.Boss;
        }

        return mobTitle?.Contains("Elite", StringComparison.OrdinalIgnoreCase) ?? false ? MobKind.Elite : MobKind.Normal;
    }

}
