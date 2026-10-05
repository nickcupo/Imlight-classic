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
 * CLASSIC BRISKBREEZE TOWER UNLOCK
 * ========================================================================
 *
 * PURPOSE:
 * The one "tower unlocked" check. Briskbreeze Tower (Gauntlet of Woe) opens
 * when a wizard has registry entry QT-WC-GNT-C01-001: the client's own
 * sigil ToGauntlet01 on Colossus Boulevard requires it, and in 2009 taking
 * the level-50 quest "Lost Lieutenant" (WC-GNT-C01-001, Sergeant Muldoon,
 * Olde Town) set it (playbot-reports/gauntlet-entry.md).
 *
 * USAGE EXAMPLE:
 * if (BriskbreezeTower.IsUnlocked(wizard)) { ... }
 *
 * NOTE:
 * The sigil and everything that follows the unlock (the spellbook guide,
 * TowerGuide) read the entry the same way a client requirement does
 * (ReqHasEntryHandler.HasEntry): the plain registry entry, or the quest
 * WC-GNT-C01-001 active or done once its template exists. So the quest that
 * grants access needs only to set the entry (or be that quest); nothing here
 * changes. To gate the tower some other way, change IsUnlocked and the sigil
 * together, and only here.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

#nullable enable

using Imlight.CoreLib.Game.Requirements.Handlers;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

internal static class BriskbreezeTower {

    /// <summary>
    /// The registry entry the tower's sigil (ToGauntlet01) requires: "quest WC-GNT-C01-001 (Lost Lieutenant) taken".
    /// </summary>
    internal const string UnlockEntry = "QT-WC-GNT-C01-001";

    /// <summary>
    /// True when <paramref name="wizard"/> may enter Briskbreeze Tower, exactly as the sigil reads it.
    /// </summary>
    internal static bool IsUnlocked(Wizard? wizard)
        => wizard?.QuestBehavior is not null && ReqHasEntryHandler.HasEntry(wizard, UnlockEntry);

}
