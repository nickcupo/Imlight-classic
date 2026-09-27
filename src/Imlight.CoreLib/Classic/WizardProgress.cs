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
 * CLASSIC WIZARD PROGRESS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC travel: a wizard's quests, registry and level as the world-unlock
 * rule (Imlight.Classic.Travel) reads them.
 *
 * USAGE EXAMPLE:
 * ClassicRuntime.Rules.IsWorldUnlocked(hubKey, new WizardProgress(wizard))
 *
 * NOTE:
 * A completed quest is read from its "<quest>_Complete" registry entry, as
 * ServerQuestBehavior.HasCompletedQuest stamps it.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

#nullable enable

using Imlight.Classic.Travel;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

internal sealed class WizardProgress(Wizard wizard) : IPlayerProgress {

    public bool HasActiveQuest(string questName)
        => wizard.QuestBehavior?.HasQuest(questName) == true;

    public bool HasCompletedQuest(string questName)
        => wizard.QuestBehavior?.HasCompletedQuest(questName) == true;

    public bool HasEntry(string entryName)
        => wizard.QuestBehavior is not null && wizard.HasRegistryValue(entryName);

    public int Level
        => wizard.MagicSchoolBehavior?.Level ?? 1;

}
