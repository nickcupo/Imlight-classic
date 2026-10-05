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
 */

using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Quests;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Requirements.Handlers;

internal sealed class ReqHasEntryHandler : BaseRequirementHandler<ReqHasEntry> {

    public override bool Evaluate(IRequirementContext context) {
        var wizard = context.GetWizard();
        if (wizard == null) {
            return false;
        }

        var questName = Requirement.m_questName;
        if (Requirement.m_isQuestRegistry && string.IsNullOrEmpty(questName)) {
            return false;
        }

        var entryName = Requirement.m_entryName;
        if (string.IsNullOrEmpty(entryName)) {
            return false;
        }

        return GetEntryValue(wizard, questName, entryName);
    }

    private bool GetEntryValue(Wizard wizard, string questName, string entryName) {
        if (Requirement.m_isQuestRegistry) {
            return wizard.HasQuestRegistryValue(questName, entryName);
        }
        else {
            return HasEntry(wizard, entryName); // CLASSIC: shared with BriskbreezeTower.IsUnlocked.
        }
    }

    // CLASSIC: a plain (non-quest) registry entry as a client requirement reads it, including KingsIsle's
    // "QT-<quest>" entries. Also the one check of Briskbreeze Tower's unlock (Imlight.CoreLib.Classic.BriskbreezeTower).
    internal static bool HasEntry(Wizard wizard, string entryName)
        => wizard.HasRegistryValue(entryName)
            || HasTakenQuest(wizard, entryName); // CLASSIC: KingsIsle's "QT-<quest>" entries.

    // CLASSIC: KingsIsle's server set "QT-<quest>" when a quest was taken; nothing in SpiralDB writes it,
    // so the entry reads as "has the quest, active or done" (Imlight.Classic.Quests.QuestTakenEntry).
    private static bool HasTakenQuest(Wizard wizard, string entryName) {
        if (!ClassicQuestEngine.IsActive || QuestTakenEntry.QuestNameOf(entryName) is not { } questName
            || !QuestTemplateCollection.DoesQuestExist(questName)) {
            return false;
        }

        return wizard.QuestBehavior?.CurrentQuestInstances?.Any(q => q.QuestName == questName) == true
            || wizard.HasQuestRegistryValue(questName, QUEST_COMPLETED_ENTRY);
    }

    private const string QUEST_COMPLETED_ENTRY = "Complete"; // CLASSIC: as ReqHasQuestHandler reads completion.

}