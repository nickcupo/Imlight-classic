// CLASSIC: admit the complete native tutorial command before any quest/refill/event/stage side effect.
using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal enum TutorialGoalKind { None, QuestGoal, Cinematic, NoOp, Skip, Finale }

internal sealed record TutorialGoalAdmission(TutorialGoalKind Kind, string QuestName, string GoalName,
    QuestInstance Quest, GoalInstance Goal, QuestTemplate QuestTemplate, GoalTemplate GoalTemplate, bool Prospective) {
    internal TutorialCommandVocabulary Vocabulary { get; init; }
    internal ulong QuestId { get; init; }
    internal ulong GoalId { get; init; }
    internal GOAL_TYPE GoalType { get; init; }
}

internal sealed record TutorialCommandDecision(string QuestToAdd, string QuestToRemove, string EventToPost,
    string Action, int Value, QuestTemplate AddTemplate, QuestInstance ExistingAddQuest,
    QuestInstance ExistingRemoveQuest, TutorialGoalAdmission Goal);

// The authored-test overload takes a policy directly; production always uses the native contract below.
internal sealed class TutorialCommandVocabulary {
    private readonly HashSet<string> _add, _remove, _events;
    private readonly HashSet<(string Quest, string Goal)> _goals;
    private readonly Dictionary<(string Quest, string Goal), TutorialGoalKind> _special;
    private readonly HashSet<int> _stages;
    internal TutorialCommandVocabulary(IEnumerable<string> add, IEnumerable<string> remove,
        IEnumerable<(string Quest, string Goal)> goals, IEnumerable<(string Quest, string Goal, TutorialGoalKind Kind)> special,
        IEnumerable<string> events, IEnumerable<int> stages) {
        _add = new(add, StringComparer.Ordinal); _remove = new(remove, StringComparer.Ordinal);
        _goals = new(goals); _special = special.ToDictionary(row => (row.Quest, row.Goal), row => row.Kind);
        _events = new(events, StringComparer.Ordinal); _stages = new(stages);
    }
    internal bool AllowsAdd(string name) => _add.Contains(name);
    internal bool AllowsRemove(string name) => _remove.Contains(name);
    internal bool AllowsGoal(string quest, string goal) => _goals.Contains((quest, goal));
    internal bool KnowsGoal(string goal) => _goals.Any(row => row.Goal == goal);
    internal bool IsSpecialGoal(string goal) => _special.Keys.Any(row => row.Goal == goal);
    internal bool TrySpecial(string quest, string goal, out string selectedQuest, out TutorialGoalKind kind) {
        selectedQuest = null; kind = TutorialGoalKind.None;
        var matches = _special.Where(row => row.Key.Goal == goal && (quest is null || row.Key.Quest == quest)).Take(2).ToArray();
        if (matches.Length != 1) return false;
        selectedQuest = matches[0].Key.Quest; kind = matches[0].Value; return true;
    }
    internal bool AllowsEvent(string name) => _events.Contains(name);
    internal bool AllowsAction(string action, int value) => action == "Stage" && _stages.Contains(value);
}

internal static class TutorialCommandAdmission {
    // Native TutorialQuestTemplate Tutorials/Tutorial_Intro.xml m_allowedQuests and the reachable Lua include
    // chain define these permissions. Installed/pristine proof is retained privately; no native assets are added.
    private static readonly string[] NativeQuests = ["WC-TUT-C03-001", "WC-TUT-C03-002", "WC-TUT-C05-001",
        "WC-TUT-C08-001", "WC-TUT-C09-014", "WC-TUT-C09-016", "Tutorial_Intro"];
    private const string CombatQuest = "WC-TUT-C03-001", CinematicQuest = "WC-TUT-C05-001", EquipmentQuest = "WC-TUT-C08-001";
    private static readonly (string Quest, string Goal)[] NativeGoals = new[] {
        "CloseDoor", "player damage 1", "Clear hand mob 0", "Clear hand mob 1", "mob damage 2", "mob damage 3",
        "mob damage 4", "player heal", "mob weakness", "player damage 2", "Give 3 pips to player", "Update pips",
        "mob damage 5", "mob damage 6", "player blade", "mob damage 7", "mob damage 8", "player damage 3", "give 4 pips to player",
    }.Select(goal => (CombatQuest, goal)).Concat(new[] {
        "StopRain", "Despawn Ambrose Outside", "Trigger Storm", "Transplant Player", "Trigger Rubble", "Trigger Silhouette",
        "Walk Ambrose", "Despawn Malistaire", "Trigger Wand Effect", "Despawn Ambrose Inside",
    }.Select(goal => (CinematicQuest, goal))).Concat(new[] {
        (EquipmentQuest, "Teleport"), (EquipmentQuest, "SkipTutorialGoal"), ("Tutorial_Intro", "OnlyGoal"),
    }).ToArray();
    private static readonly TutorialCommandVocabulary NativeVocabulary = new(NativeQuests, NativeQuests, NativeGoals,
        [(CinematicQuest, "StopRain", TutorialGoalKind.NoOp), (CinematicQuest, "Transplant Player", TutorialGoalKind.Cinematic),
            (CinematicQuest, "Despawn Malistaire", TutorialGoalKind.Cinematic), (CinematicQuest, "Trigger Wand Effect", TutorialGoalKind.Cinematic),
            (CinematicQuest, "Despawn Ambrose Inside", TutorialGoalKind.Cinematic), (EquipmentQuest, "SkipTutorialGoal", TutorialGoalKind.Skip),
            (EquipmentQuest, "Teleport", TutorialGoalKind.Finale)], [], [1, 2, 3, 4, 5, 6, 8]);

    internal static bool TryPreflight(Wizard wizard, GAME_5_PROTOCOL.MSG_SERVERTUTORIALCOMMAND message,
        out TutorialCommandDecision decision, Func<string, IEnumerable<QuestTemplate>> templates = null)
        => TryPreflightCore(wizard, message, NativeVocabulary, out decision, templates);

    internal static bool TryPreflightForTests(Wizard wizard, GAME_5_PROTOCOL.MSG_SERVERTUTORIALCOMMAND message,
        TutorialCommandVocabulary vocabulary, out TutorialCommandDecision decision,
        Func<string, IEnumerable<QuestTemplate>> templates)
        => TryPreflightCore(wizard, message, vocabulary, out decision, templates);

    private static bool TryPreflightCore(Wizard wizard, GAME_5_PROTOCOL.MSG_SERVERTUTORIALCOMMAND message,
        TutorialCommandVocabulary vocabulary, out TutorialCommandDecision decision,
        Func<string, IEnumerable<QuestTemplate>> templates) {
        decision = null;
        if (message is null || vocabulary is null || !TryJournal(wizard, out var held)) return false;
        string add = message.QuestToAdd, remove = message.QuestToRemove, goal = message.GoalToComplete;
        string eventName = message.EventToPost, action = message.Action;
        if (add is null || remove is null || goal is null || eventName is null || action is null
            || add.Length > 0 && !vocabulary.AllowsAdd(add) || remove.Length > 0 && !vocabulary.AllowsRemove(remove)
            || eventName.Length > 0 && !vocabulary.AllowsEvent(eventName)
            || action.Length > 0 && !vocabulary.AllowsAction(action, message.Value)) return false;
        QuestTemplate addTemplate = null; QuestInstance existingAdd = null, existingRemove = null;
        if (add.Length > 0) {
            if (!TryTemplate(add, templates, out addTemplate) || !TryHeld(held, add, out existingAdd)) return false;
        }
        if (remove.Length > 0 && !TryHeld(held, remove, out existingRemove)) return false;
        if (!TryGoal(wizard, held, vocabulary, goal, add.Length > 0 ? add : null,
            addTemplate, templates, out var selected)) return false;
        decision = new(add, remove, eventName, action, message.Value, addTemplate, existingAdd, existingRemove, selected);
        return true;
    }

    // Re-resolve after an admitted add's ACK. A prospective selection never allocates or adopts a quest in advance.
    // Existing held selection must retain its exact original full identities/aliases and loaded result template.
    internal static bool TryResolveAcknowledgedGoal(Wizard wizard, TutorialGoalAdmission admitted,
        out TutorialGoalAdmission selected, Func<string, IEnumerable<QuestTemplate>> templates = null) {
        selected = null;
        if (admitted?.Kind != TutorialGoalKind.QuestGoal || admitted.Vocabulary is null
            || !TryJournal(wizard, out var held)
            || !TryGoal(wizard, held, admitted.Vocabulary, admitted.GoalName, admitted.QuestName, null, templates, out var current)
            || current.Kind != TutorialGoalKind.QuestGoal || current.QuestName != admitted.QuestName
            || !ReferenceEquals(current.QuestTemplate, admitted.QuestTemplate)
            || !ReferenceEquals(current.GoalTemplate, admitted.GoalTemplate) || current.GoalType != admitted.GoalType) return false;
        if (!admitted.Prospective && (!ReferenceEquals(current.Quest, admitted.Quest)
            || !ReferenceEquals(current.Goal, admitted.Goal) || current.Quest.ID != admitted.QuestId
            || current.Goal.ID != admitted.GoalId)) return false;
        selected = current;
        return true;
    }

    internal static bool TryResolveHeldQuest(Wizard wizard, string name, out QuestInstance quest) {
        quest = null;
        return NativeVocabulary.AllowsRemove(name) && TryJournal(wizard, out var held) && TryHeld(held, name, out quest);
    }

    private static bool TryGoal(Wizard wizard, IReadOnlyList<QuestInstance> held, TutorialCommandVocabulary vocabulary,
        string name, string selectedQuest, QuestTemplate selectedTemplate,
        Func<string, IEnumerable<QuestTemplate>> templates, out TutorialGoalAdmission selected) {
        selected = null;
        if (name.Length == 0) {
            selected = new(TutorialGoalKind.None, "", "", null, null, null, null, false) { Vocabulary = vocabulary };
            return true;
        }
        if (vocabulary.IsSpecialGoal(name)) {
            if (!vocabulary.TrySpecial(selectedQuest, name, out var specialQuest, out var special)) return false;
            selected = new(special, specialQuest, name, null, null, null, null, false) { Vocabulary = vocabulary };
            return true;
        }
        if (!vocabulary.KnowsGoal(name)) return false;
        var candidates = selectedQuest is null ? held : held.Where(quest => quest.QuestName == selectedQuest).ToArray();
        var matches = candidates.SelectMany(quest => quest.GoalProgress, (quest, goal) => (Quest: quest, Goal: goal))
            .Where(row => string.Equals(row.Goal.GoalName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Goal-only wire packets cannot distinguish a forbidden quest's same-name goal from an authorized one.
        if (matches.Any(row => row.Goal.GoalName != name || !vocabulary.AllowsGoal(row.Quest.QuestName, name))) return false;
        if (selectedQuest is not null && !vocabulary.AllowsGoal(selectedQuest, name)) return false;
        GoalTemplate proposedGoal = null;
        var prospect = selectedQuest is not null && !held.Any(quest => quest.QuestName == selectedQuest);
        if (prospect && !TryTemplateGoal(selectedTemplate, name, out proposedGoal)) return false;
        if (matches.Length + (prospect ? 1 : 0) != 1) return false;
        if (prospect) {
            selected = new(TutorialGoalKind.QuestGoal, selectedQuest, name, null, null,
                selectedTemplate, proposedGoal, true) { Vocabulary = vocabulary, GoalType = proposedGoal.m_goalType };
            return true;
        }
        var found = matches[0];
        if (!TryTemplate(found.Quest.QuestName, templates, out var template)
            || !TryTemplateGoal(template, name, out var templateGoal) || templateGoal.m_goalType != found.Goal.GoalType) return false;
        selected = new(TutorialGoalKind.QuestGoal, found.Quest.QuestName, name, found.Quest, found.Goal,
            template, templateGoal, false) { Vocabulary = vocabulary, QuestId = found.Quest.ID,
                GoalId = found.Goal.ID, GoalType = found.Goal.GoalType };
        return true;
    }

    private static bool TryTemplate(string name, Func<string, IEnumerable<QuestTemplate>> templates, out QuestTemplate template) {
        template = null;
        try {
            var rows = templates is null ? QuestTemplateCollection.GetAllQuests() : templates(name);
            if (rows is null) return false;
            var matches = rows
                .Where(candidate => candidate is not null && string.Equals(candidate.m_questName, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length != 1 || matches[0].m_questName != name || !ValidTemplate(matches[0])) return false;
            template = matches[0]; return true;
        }
        catch { return false; }
    }

    private static bool ValidTemplate(QuestTemplate template) => template?.m_goals is not null && template.m_startGoals is not null
        && template.m_goals.All(goal => goal is not null && !string.IsNullOrWhiteSpace(goal.m_goalName))
        && template.m_goals.Select(goal => goal.m_goalName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == template.m_goals.Count
        && template.m_startGoals.All(name => template.m_goals.Count(goal => goal.m_goalName == name) == 1);

    private static bool TryTemplateGoal(QuestTemplate template, string name, out GoalTemplate goal) {
        goal = null;
        if (!ValidTemplate(template)) return false;
        var goals = template.m_goals.Where(candidate => string.Equals(candidate.m_goalName, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (goals.Length != 1 || goals[0].m_goalName != name) return false;
        goal = goals[0]; return true;
    }

    private static bool TryHeld(IReadOnlyList<QuestInstance> held, string name, out QuestInstance quest) {
        quest = null;
        var matches = held.Where(candidate => string.Equals(candidate.QuestName, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length > 1 || matches.Length == 1 && matches[0].QuestName != name) return false;
        quest = matches.SingleOrDefault(); return true;
    }

    private static bool TryJournal(Wizard wizard, out IReadOnlyList<QuestInstance> held) {
        held = null;
        if (wizard?.CharId is not > 0 || wizard.CharId > ulong.MaxValue - 2 || WizardCollection.IsInventorySnapshotUncertain(wizard)
            || wizard.GameObject?.m_characterId.Full != wizard.CharId || wizard.GameObject.m_globalID.Full != wizard.GameObjectID
            || wizard.GameObject.m_permID.Full != wizard.GameObjectID || wizard.QuestBehavior?.CurrentQuestIDs is not { } ids
            || wizard.QuestBehavior.CurrentQuestInstances is not { } instances) return false;
        if (ids.Any(id => id == 0) || ids.Distinct().Count() != ids.Count || ids.Count != instances.Count
            || instances.Any(quest => quest is null || quest.ID == 0 || quest.OwnerCharId != wizard.CharId
                || !ids.Contains(quest.ID) || string.IsNullOrWhiteSpace(quest.QuestName) || quest.GoalProgress is null
                || quest.GoalProgress.Any(goal => goal is null || goal.ID == 0 || goal.OwnerCharId != wizard.CharId
                    || string.IsNullOrWhiteSpace(goal.GoalName) || goal.CurrentProgress < -1)
                || quest.GoalProgress.Select(goal => goal.GoalName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != quest.GoalProgress.Length)
            || instances.Select(quest => quest.ID).Distinct().Count() != instances.Count
            || instances.Select(quest => quest.QuestName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != instances.Count) return false;
        var goals = instances.SelectMany(quest => quest.GoalProgress).ToArray();
        if (goals.Select(goal => goal.ID).Distinct().Count() != goals.Length) return false;
        held = instances.ToArray(); return true;
    }
}
