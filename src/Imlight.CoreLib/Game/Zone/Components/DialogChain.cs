#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// CLASSIC: dialog chaining, r806919's way. After an NPC dialog closes the client re-asks for the NPC's options
/// (MSG_INTERACTNPC, Reinteract 2), and InteractionManager::NewNPCInteraction (0x14172ad00) talks to the NPC again at
/// once, without X, when the answer for that NPC holds an AvailableQuestEntry (0x141596500) or an AvailableCompleteEntry
/// (0x141596190). So in the answer to a Reinteract-2 re-ask only (elsewhere the client treats them as their base class,
/// ios-parity), an offer becomes an AvailableQuestEntry and a talk-back that turns a quest in becomes an
/// AvailableCompleteEntry: accepting "Get Some Bling" (KT-SPH2-C02-007) at General Khaba in KT_ChampHall, whose last goal
/// is to talk to him there, goes straight on to its turn-in, then to his next quest. Each keeps its option's service
/// name and index, so the click is routed as before.
/// </summary>
internal static class DialogChain {

    internal const string OfferService = "QuestOfferService";
    internal const string PersonaGoalService = "QuestPersonaGoalService";

    /// <summary>The options with offers and quest-completing talk-backs in their chaining classes.</summary>
    /// <param name="questById">The wizard's quest instance by id, or null.</param>
    /// <param name="questByTitle">The quest template whose title key is this, or null.</param>
    internal static List<ServiceOptionBase> Forms(IEnumerable<ServiceOptionBase> options, Func<ulong, QuestInstance?> questById,
                                                  Func<string, QuestTemplate?> questByTitle)
        => [.. options.Select(option => option switch {
            GoalEntry goal when goal is not AvailableCompleteEntry && goal.m_serviceName == PersonaGoalService
                && questById(goal.m_questID.Full) is { } quest && TurnsIn(quest, goal.m_goalID.Full) => Complete(goal),
            PrepEntry prep when prep.m_serviceName == OfferService && questByTitle(prep.m_displayKey) is { } template
                => Offer(prep, template),
            _ => option,
        })];

    /// <summary>
    /// CLASSIC: the position of the option a chained message chose in <paramref name="options"/> (the wizard's current
    /// list), or -1: for MSG_INTERACTCOMPLETEGOAL the persona goal option of that quest and goal; for
    /// MSG_INTERACTAVAILABLEQUEST the offer whose title key is <paramref name="questTitle"/> (offers are listed by title).
    /// </summary>
    internal static int FindChosen(IReadOnlyList<ServiceOptionBase> options, string serviceName, ulong questId, ulong goalId,
                                   string? questTitle) {
        for (var i = 0; i < options.Count; i++) {
            var option = options[i];
            if (option.m_serviceName != serviceName) continue;
            if (serviceName == PersonaGoalService && option is GoalEntry goal && questId != 0
                && goal.m_questID.Full == questId && goal.m_goalID.Full == goalId) return i;
            if (serviceName == OfferService && questTitle is { Length: > 0 }
                && (option is PrepEntry prep && prep.m_displayKey == questTitle
                    || option is AvailableQuestEntry offer && offer.m_questTitle == questTitle)) return i;
        }
        return -1;
    }

    /// <summary>True when finishing goal <paramref name="goalId"/> finishes the quest: every other goal is done.</summary>
    internal static bool TurnsIn(QuestInstance quest, ulong goalId)
        => quest.GoalProgress.Any(g => g.ID == goalId)
        && quest.GoalProgress.Where(g => g.ID != goalId).All(g => g.IsGoalCompleted());

    private static AvailableCompleteEntry Complete(GoalEntry goal) => new() {
        m_serviceName = goal.m_serviceName, m_iconKey = goal.m_iconKey, m_displayKey = goal.m_displayKey,
        m_serviceIndex = goal.m_serviceIndex, m_forceInteract = goal.m_forceInteract,
        m_questID = goal.m_questID, m_goalID = goal.m_goalID, m_goalTitle = goal.m_goalTitle,
        m_questTitle = goal.m_questTitle, m_goalNameID = goal.m_goalNameID,
    };

    private static AvailableQuestEntry Offer(PrepEntry prep, QuestTemplate quest) => new() {
        m_serviceName = prep.m_serviceName, m_iconKey = prep.m_iconKey, m_displayKey = prep.m_displayKey,
        m_serviceIndex = prep.m_serviceIndex, m_forceInteract = prep.m_forceInteract,
        m_questTitle = quest.m_questTitle, m_questName = quest.m_questName, m_isMainline = quest.m_mainline,
    };
}
