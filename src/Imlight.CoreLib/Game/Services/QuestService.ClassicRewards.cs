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

using System.Collections.Generic;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Resources;
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
        foreach (var card in rewards.CardsFor(questName, school)) {
            if (CoreObjectFactory.GetCoreTemplate(card.Template) is not SpellTemplate spell) {
                Logger.Warning("Quest {Quest} gives treasure card {Card} ({Template}), which is no client spell; it is not granted.",
                    Logger.Args(questName, card.Name, card.Template));
                continue;
            }

            var spellHash = StringHash.Compute(spell.m_name);
            for (var i = 0; i < card.Count; i++) {
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK { SpellID = (int) spellHash, EnchantmentID = 0 });
                wizard.SpellbookBehavior.AddTreasureCard(card.Template);
                WizardCollection.AddTreasureCard(wizard, card.Template);
            }

            loot.Add(new TreasureCardLootInfo {
                m_lootType = LOOT_TYPE.LOOT_TYPE_TREASURE_CARD,
                m_spellID = card.Template,
                m_numItems = card.Count,
            });
            Logger.Information("Quest {Quest} gave wizard {CharId} the treasure card {Card} x{Count}.",
                Logger.Args(questName, wizard.CharId, card.Name, card.Count));
        }
    }

}
