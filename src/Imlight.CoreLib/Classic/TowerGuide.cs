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
 * CLASSIC BRISKBREEZE TOWER GUIDE
 * ========================================================================
 *
 * PURPOSE:
 * The spellbook guide to Briskbreeze Tower's boss cheats. Once a wizard has
 * unlocked the tower (BriskbreezeTower.IsUnlocked), the Quests tab lists an
 * entry titled by the guide (one card per boss: floor and name), and its ?
 * button, "all dialogue associated with this quest", opens the guide's pages
 * (MSG_REQUESTQUESTDIALOG / MSG_QUESTDIALOG). The text is the guide block of
 * the profile's boss-cheat file, held to the cheats by BossCheatGuideCheck.
 *
 * USAGE EXAMPLE:
 * if (TowerGuide.ShouldShow(wizard, ClassicProgression.BossCheats)) { send TowerGuide.Quest(...) }
 *
 * NOTE:
 * It is not a quest instance: nothing is saved, it never completes, and the
 * quest engine (goals, combat credit, door lights, counts, badges) never
 * sees it. It is sent with every login and zone load while the condition
 * holds, so it survives relogs and follows the unlock. No quest arrow: the
 * entry and its cards carry NoQuestHelper and no destination zone, and the
 * entry skips the helper's auto-select. Off when the profile has no boss
 * cheats (arc1-2009h1, dev-unrestricted) or the file has no guide.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Rules;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic;

internal static class TowerGuide {

    /// <summary>
    /// The entry's internal name; no quest template has it.
    /// </summary>
    internal const string QuestName = "W101C-GUIDE-GNT-01";

    /// <summary>
    /// The entry's quest id: a fixed id outside the random 64-bit ids of quest instances, the same each login.
    /// </summary>
    internal const ulong QuestId = 0x5743_4755_4944_4500; // "WCGUIDE\0"

    /// <summary>
    /// The level the entry shows (the tower is for level 50).
    /// </summary>
    internal const int Level = 50;

    /// <summary>
    /// The id the client sends back when the ? button asks for the entry's dialogue.
    /// </summary>
    internal static uint QuestNameId => StringHash.Compute(QuestName);

    /// <summary>
    /// The goal id of the card for the <paramref name="index"/>th boss.
    /// </summary>
    internal static ulong GoalId(int index) => QuestId + 1 + (ulong) index;

    /// <summary>
    /// True when <paramref name="wizard"/> should see the guide under <paramref name="cheats"/>.
    /// </summary>
    internal static bool ShouldShow(Wizard? wizard, BossCheats cheats)
        => cheats.Count > 0 && cheats.Guide is not null && BriskbreezeTower.IsUnlocked(wizard);

    /// <summary>
    /// The quest-log entry.
    /// </summary>
    internal static QUEST_MESSAGES_52_PROTOCOL.MSG_SENDQUEST Quest(BossCheatGuide guide, bool isNew) {
        var serializer = new ObjectSerializer(false);
        serializer.Serialize(new MadlibBlock {
            m_madlibs = [
                new MadlibArgT_ByteString { m_madlibToken = "NAME", m_madlibArgument = guide.Title },
                new MadlibArgT_ByteString { m_madlibToken = "LEVEL", m_madlibArgument = Level.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            ],
            m_blockToken = "QUEST",
        }, 1, out var madlibs);
        serializer.Serialize(new LootInfoList(), 1, out var rewards);
        serializer.Serialize(new AssociatedWorldsList { m_associatedWorlds = ["WizardCity"] }, 1, out var worlds);

        return new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDQUEST {
            QuestID = QuestId,
            QuestNameID = QuestNameId,
            QuestType = 0,
            QuestLevel = Level,
            QuestTitle = guide.Title,
            QuestInfo = "",
            New = isNew ? (byte) 1 : (byte) 0,
            QuestMadlibs = madlibs,
            GoalData = "",
            Rewards = rewards,
            ClientTags = "",
            AssociatedWorlds = worlds,
            NoQuestHelper = 1,
            Mainline = 0,
            ReadyToTurnIn = 0,
            SkipQHAutoSelect = 1,
            PetOnlyQuest = 0,
            ActivityType = 0,
        };
    }

    /// <summary>
    /// The entry's cards: one per boss, in floor order ("Floor 10: Orrick Nightglider").
    /// </summary>
    internal static List<QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL> Goals(BossCheats cheats) {
        var guide = cheats.Guide!;
        var serializer = new ObjectSerializer(false);
        var goals = new List<QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL>();
        var bosses = guide.Bosses
            .Select(b => (Page: b, Floor: cheats.TryGet(b.Template, out var cheat) ? cheat.Floor : 0))
            .OrderBy(b => b.Floor)
            .ToList();
        for (var i = 0; i < bosses.Count; i++) {
            var title = $"Floor {bosses[i].Floor}: {bosses[i].Page.ShownAs}";
            serializer.Serialize(new MadlibBlock {
                m_madlibs = [
                    new MadlibArgT_ByteString { m_madlibToken = "NAME", m_madlibArgument = title },
                    new MadlibArgT_ByteString { m_madlibToken = "LOCATION", m_madlibArgument = guide.Location },
                ],
                m_blockToken = "GOAL",
            }, 1, out var madlibs);
            goals.Add(new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDGOAL {
                QuestID = QuestId,
                GoalID = GoalId(i),
                GoalNameID = StringHash.Compute($"{QuestName}-{bosses[i].Page.Template}"),
                GoalTitle = title,
                GoalLocation = guide.Location,
                GoalDestinationZone = "",
                GoalImage1 = "",
                GoalImage2 = "",
                PersonaName = "",
                PatronIcon = "",
                GoalType = (byte) GOAL_TYPE.GOAL_TYPE_WAYPOINT,
                GoalStatus = 0,
                GoalCount = 0,
                UseTally = 0,
                GoalTotal = 0,
                TallyText = "",
                SubscriberGoalTotal = 0,
                SendType = 0,
                GoalMadlibs = madlibs,
                ClientTags = "",
                NoQuestHelper = 1,
            });
        }

        return goals;
    }

    /// <summary>
    /// The entry's dialogue: one page per guide page, in reading order.
    /// </summary>
    internal static ActorDialog Dialog(BossCheats cheats)
        => new() {
            m_dialogTag = "Start",
            m_dialogEntries = [.. cheats.Guide!.PagesInOrder(cheats.Bosses).Select(page => new ActorDialogEntry {
                m_dialog = page,
                m_meetsRequirements = true,
                m_bypassCameraOnReview = true,
                m_allowPlayerToMove = true,
            })],
            m_madlibs = [],
            m_dialogEvents = [],
        };

    /// <summary>
    /// The answer to the ? button.
    /// </summary>
    internal static WIZARD_12_PROTOCOL.MSG_QUESTDIALOG? DialogMessage(BossCheats cheats) {
        if (!new ObjectSerializer(Versionable: false).Serialize(Dialog(cheats), 16, out var data)) {
            return null;
        }

        return new WIZARD_12_PROTOCOL.MSG_QUESTDIALOG {
            QuestNameID = QuestNameId,
            ActorDialog = data,
        };
    }

}
