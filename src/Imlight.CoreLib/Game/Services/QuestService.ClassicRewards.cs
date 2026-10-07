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
 * CLASSIC QUEST REWARDS
 * ========================================================================
 *
 * PURPOSE:
 * Gives the treasure cards a quest gave in 2009 (ClassicProgression.
 * QuestCards, classic-data/quests/cards-*.yaml) when it completes: into the
 * treasure book, and into the quest's reward popup.
 *
 * USAGE EXAMPLE:
 * GrantClassicQuestCards(wizard, questName, spellRewards.m_loot);   // CompleteQuest
 *
 * NOTE:
 * The book add is the treasure shop's: MSG_ADDTREASURESPELLTOBOOK with the
 * spell's name hash, and the template id in SpellbookBehavior's treasure
 * cards, which TreasureShopService resends on every attach. The popup entry
 * is a TreasureCardLootInfo in the same MSG_QUESTREWARDS as the quest's new
 * spells. Off when the profile turns treasure cards off.
 *
 * TODO:
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class QuestService {

    private void GrantClassicQuestCards(Wizard wizard, string questName, List<LootInfo> loot) {
        if (ClassicProgression.QuestCards is not { } rewards
            || !ClassicRuntime.Rules.IsFeatureEnabled(ClassicFeatures.TreasureCards)) {
            return;
        }

        var school = wizard.MagicSchoolBehavior?.MagicSchool.ToString();
        var rolled = rewards.CardsFor(questName, school).SelectMany(card => Enumerable.Repeat(card.Template, card.Count)).ToArray();
        try {
            GrantClassicQuestCardBatch(wizard, rolled, loot, SendToSocket);
        }
        catch {
            if (WizardCollection.IsInventorySnapshotUncertain(wizard)) CloseSession();
            throw; // CLASSIC: never repeat a reward whose saved acknowledgement may have been lost
        }
    }

    // CLASSIC: use the same fresh saved capacity and ACK-only publication as combat/Second Chance rewards.
    // Keep this quest window's existing template-ID convention; its counts describe only accepted copies.
    internal static bool GrantClassicQuestCardBatch(Wizard wizard, IReadOnlyList<uint> rolled,
        List<LootInfo> loot, Action<IMessage> send) {
        if (!ClassicStackRewards.TryGrant(wizard, rolled, [], out var receipt)) return false;
        foreach (var card in receipt.Cards)
            send(new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK { SpellID = (int) card.SpellHash, EnchantmentID = 0 });
        foreach (var group in receipt.Cards.GroupBy(card => card.TemplateId)) loot.Add(new TreasureCardLootInfo {
            m_lootType = LOOT_TYPE.LOOT_TYPE_TREASURE_CARD,
            m_spellID = group.Key,
            m_numItems = group.Count(),
        });
        return true;
    }
}
