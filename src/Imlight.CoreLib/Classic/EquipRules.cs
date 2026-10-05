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
 * EQUIP RULES
 * ========================================================================
 *
 * PURPOSE:
 * Whether a wizard may put on an item the client asked to equip: the item has a slot, the wizard is not in a duel
 * (the 2009 character screen could not change gear mid-battle; the server's own mount stow/restore around a duel
 * does not come through here), and the item template's m_equipRequirements are met.
 *
 * USAGE EXAMPLE:
 * var refusal = EquipRules.Check(template, level: 5, school: "Fire", inDuel: false);
 *
 * NOTE:
 * Only the requirement kinds gear uses for who may wear it are judged here: the wizard's level (ReqMagicLevel) and
 * school (ReqSchoolOfFocus, ReqIsSchool). Any other kind counts as met, so an unfamiliar requirement never locks a
 * wizard out of their own gear. Lists fold left to right as KingsIsle's do (RequirementFold).
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/04/2026
 */

using System;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

internal enum EquipRefusal : byte {
    None,
    NotEquippable,
    InDuel,
    RequirementsNotMet,
}

internal static class EquipRules {

    /// <summary>The slot an item template goes in, or null when it has none (not equippable).</summary>
    public static EquipmentSlot SlotOf(WizItemTemplate template)
        => template?.m_adjectiveList is null ? null : ItemHelper.GetItemSlot(template);

    /// <summary>Whether a wizard may equip an item of this template.</summary>
    public static EquipRefusal Check(WizItemTemplate template, int level, string school, bool inDuel) {
        if (SlotOf(template) is null) {
            return EquipRefusal.NotEquippable;
        }

        if (inDuel) {
            return EquipRefusal.InDuel;
        }

        return MeetsRequirements(template.m_equipRequirements, level, school)
            ? EquipRefusal.None
            : EquipRefusal.RequirementsNotMet;
    }

    /// <summary>True when the list is empty or met for a wizard of this level and school.</summary>
    public static bool MeetsRequirements(RequirementList requirements, int level, string school)
        => requirements?.m_requirements is not { Count: > 0 } list
        || RequirementFold.Evaluate(
            list,
            requirement => Meets(requirement, level, school),
            requirement => requirement.m_operator == Operator.ROP_OR,
            requirement => requirement.m_applyNOT);

    private static bool Meets(Requirement requirement, int level, string school) => requirement switch {
        RequirementList nested => MeetsRequirements(nested, level, school),
        ReqMagicLevel magicLevel => NumericRequirement.Meets(level, magicLevel.m_operatorType.ToString(), magicLevel.m_numericValue),
        ReqSchoolOfFocus focus => string.IsNullOrEmpty(focus.m_magicSchool)
            || string.Equals(focus.m_magicSchool, school, StringComparison.OrdinalIgnoreCase),
        ReqIsSchool isSchool => string.IsNullOrEmpty(isSchool.m_magicSchoolName)
            || string.Equals(isSchool.m_magicSchoolName, school, StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    /// <summary>The refusal for a wizard equipping an item of this template.</summary>
    public static EquipRefusal Check(Wizard wizard, WizItemTemplate template)
        => Check(template, wizard.MagicSchoolBehavior.Level, wizard.MagicSchoolBehavior.MagicSchool.ToString(), wizard.IsInDuel);

}
