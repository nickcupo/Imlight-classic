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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Imlight.CoreLib.WizardData.Models.Player;
using Newtonsoft.Json;

namespace Imlight.CoreLib.Shared.Behaviors;

[Serializable]
public class ServerQuestBehavior : IClientBehaviorProvider<ServerQuestBehavior> {

    [JsonIgnore] public bool NoTransfer { get; set; } = true;

    // CLASSIC: quest, goal and zone-trigger result lists run on their own executor and handler actors
    // (ResultExecutorActor), so ResModifyEntry, quest completion on the player's actor and the RavenDB save
    // of this behavior (UpdateCharacterQuestBehavior serializes the live object) used to meet on one
    // Dictionary and two Lists. The registry is now a ConcurrentDictionary, and both quest lists are
    // replaced under s_writeLock instead of being changed in place, so a reader or a save always sees
    // a whole list.
    private static readonly Lock s_writeLock = new(); // CLASSIC: static, so it is never serialized.

    public readonly ConcurrentDictionary<string, ulong> Registry = new(); // CLASSIC: was Dictionary.

    // CLASSIC: an old (made-up) quest name, alone or in a registry key ("QT-old", "old_Complete"), means the quest's
    // KingsIsle name (Imlight.Classic.Quests.QuestNameAliases); saved characters are migrated at start-up.
    private static string Q(string questName) => Imlight.Classic.Quests.QuestNameAliases.Current.Canonical(questName);
    private static string E(string entryName) => Imlight.Classic.Quests.QuestNameAliases.Current.CanonicalEntry(entryName);

    // In database, only store the quest IDs to reduce storage size.
    // The quest instances are loaded from the quest instance database on demand.
    public List<ulong> CurrentQuestIDs { get; set; } = []; // CLASSIC: replaced, never changed in place.
    [JsonIgnore] public List<QuestInstance> CurrentQuestInstances { get; set; } = []; // CLASSIC: as CurrentQuestIDs.

    public bool AddQuest(QuestInstance quest) {
        if (quest == null) {
            return false;
        }

        using var writeScope = s_writeLock.EnterScope(); // CLASSIC
        if (CurrentQuestIDs.Contains(quest.ID)
            || CurrentQuestInstances.Any(q => q is not null && q.QuestName == quest.QuestName)) {
            return false;
        }

        CurrentQuestIDs = [.. CurrentQuestIDs, quest.ID]; // CLASSIC
        CurrentQuestInstances = [.. CurrentQuestInstances, quest]; // CLASSIC

        return true;
    }

    /// <summary>
    /// Replaces both quest lists at once (after a database load).
    /// </summary>
    public void ReplaceQuests(IEnumerable<QuestInstance> quests) { // CLASSIC
        var list = quests?.Where(q => q is not null).ToList() ?? [];
        using var writeScope = s_writeLock.EnterScope();
        CurrentQuestInstances = list;
        CurrentQuestIDs = [.. list.Select(q => q.ID)];
    }

    /// <summary>
    /// Removes one quest instance object (a duplicate found on load).
    /// </summary>
    public bool RemoveQuestInstanceObject(QuestInstance quest) { // CLASSIC
        using var writeScope = s_writeLock.EnterScope();
        if (!CurrentQuestInstances.Contains(quest)) {
            return false;
        }
        CurrentQuestInstances = [.. CurrentQuestInstances.Where(q => !ReferenceEquals(q, quest))];
        return true;
    }

    public bool CompleteQuest(string questName) {
        if (string.IsNullOrWhiteSpace(questName)) {
            return false;
        }

        questName = Q(questName); // CLASSIC
        var quest = CurrentQuestInstances.Find(q => q?.QuestName == questName);
        if (quest == null) {
            return false;
        }

        return CompleteQuest(quest);
    }

    public bool CompleteQuest(QuestInstance quest) {
        if (quest == null) {
            return false;
        }

        using (s_writeLock.EnterScope()) { // CLASSIC
            if (!CurrentQuestIDs.Contains(quest.ID)) {
                return false;
            }

            CurrentQuestIDs = [.. CurrentQuestIDs.Where(id => id != quest.ID)]; // CLASSIC
            CurrentQuestInstances = [.. CurrentQuestInstances.Where(q => q?.QuestName != quest.QuestName)]; // CLASSIC
        }

        // Mark the quest as completed in the registry:
        AddToQuestRegistry(quest.QuestName, "Complete", 1);

        return true;
    }

    public bool RemoveQuest(string questName) {
        if (string.IsNullOrWhiteSpace(questName)) {
            return false;
        }

        questName = Q(questName); // CLASSIC
        var quest = CurrentQuestInstances.Find(q => q.QuestName == questName);
        if (quest == null) {
            return false;
        }

        return RemoveQuest(quest);
    }

    public bool RemoveQuest(QuestInstance quest) {
        if (quest == null) {
            return false;
        }

        using var writeScope = s_writeLock.EnterScope(); // CLASSIC
        var idsToDrop = new HashSet<ulong>(
            CurrentQuestInstances.Where(q => q?.QuestName == quest.QuestName).Select(q => q.ID)) {
            quest.ID
        };

        var keptInstances = CurrentQuestInstances.Where(q => q?.QuestName != quest.QuestName).ToList();
        var keptIds = CurrentQuestIDs.Where(id => !idsToDrop.Contains(id)).ToList();
        var removedInstances = CurrentQuestInstances.Count - keptInstances.Count;
        var removedIds = CurrentQuestIDs.Count - keptIds.Count;
        CurrentQuestInstances = keptInstances; // CLASSIC
        CurrentQuestIDs = keptIds; // CLASSIC

        return removedInstances > 0 || removedIds > 0;
    }

    public void PruneStaleQuestIds() {
        if (CurrentQuestInstances.Count <= 0) {
            return;
        }

        using var writeScope = s_writeLock.EnterScope(); // CLASSIC
        var liveIds = new HashSet<ulong>(CurrentQuestInstances.Select(q => q.ID));
        CurrentQuestIDs = [.. CurrentQuestIDs.Where(liveIds.Contains)]; // CLASSIC
    }

    public bool HasQuest(string questName) {
        if (string.IsNullOrWhiteSpace(questName)) {
            return false;
        }

        questName = Q(questName); // CLASSIC

        return CurrentQuestInstances.Any(q => q.QuestName == questName);
    }

    public bool HasCompletedQuest(string questName) {
        if (string.IsNullOrWhiteSpace(questName)) {
            return false;
        }

        // Check the registry to find the completed quest key:
        // <quest_name>_Complete
        var completedKey = $"{Q(questName)}_Complete"; // CLASSIC: Q

        return Registry.TryGetValue(completedKey, out var completed) && completed > 0; // CLASSIC: one read.
    }

    public bool StartQuestGoal(string questName, string goalName) {
        if (string.IsNullOrWhiteSpace(questName) || string.IsNullOrWhiteSpace(goalName)) {
            return false;
        }

        questName = Q(questName); // CLASSIC
        var quest = CurrentQuestInstances.Find(q => q.QuestName == questName);
        if (quest == null) {
            return false;
        }

        quest.StartGoal(goalName);

        return true;
    }

    public bool IncrementQuestGoal(string questName, string goalName, int amount = 1) {
        if (string.IsNullOrWhiteSpace(questName) || string.IsNullOrWhiteSpace(goalName) || amount <= 0) {
            return false;
        }

        questName = Q(questName); // CLASSIC
        var quest = CurrentQuestInstances.Find(q => q.QuestName == questName);
        if (quest == null) {
            return false;
        }

        quest.IncrementGoal(goalName);

        return true;
    }

    public bool CompleteQuestGoal(string questName, string goalName) {
        if (string.IsNullOrWhiteSpace(questName) || string.IsNullOrWhiteSpace(goalName)) {
            return false;
        }

        questName = Q(questName); // CLASSIC
        var quest = CurrentQuestInstances.Find(q => q.QuestName == questName);
        if (quest == null) {
            return false;
        }

        quest.CompleteGoal(goalName);

        return true;
    }

    public bool AddToRegistry(string entryName, ulong value) {
        if (string.IsNullOrWhiteSpace(entryName) || value == 0) {
            return false;
        }

        Registry.TryAdd(E(entryName), value); // CLASSIC: keeps an existing value, atomically; E

        return true;
    }

    public bool AddToQuestRegistry(string questName, string entryName, ulong value) {
        if (string.IsNullOrWhiteSpace(questName) || string.IsNullOrWhiteSpace(entryName) || value == 0) {
            return false;
        }

        // Same as the normal registry, except prefixed with the quest name.
        var fullEntryName = $"{questName}_{entryName}";

        return AddToRegistry(fullEntryName, value);
    }

    public bool RemoveFromRegistry(string entryName) {
        if (string.IsNullOrWhiteSpace(entryName)) {
            return false;
        }

        return Registry.TryRemove(E(entryName), out _); // CLASSIC: E
    }

    public bool RemoveFromQuestRegistry(string questName, string entryName) {
        if (string.IsNullOrWhiteSpace(questName) || string.IsNullOrWhiteSpace(entryName)) {
            return false;
        }

        var fullEntryName = $"{questName}_{entryName}";

        return RemoveFromRegistry(fullEntryName);
    }

    public bool SetRegistryValue(string entryName, ulong value) {
        if (string.IsNullOrWhiteSpace(entryName)) {
            return false;
        }

        Registry[E(entryName)] = value; // CLASSIC: E

        return true;
    }

    public bool SetQuestRegistryValue(string questName, string entryName, ulong value) {
        if (string.IsNullOrWhiteSpace(questName) || string.IsNullOrWhiteSpace(entryName)) {
            return false;
        }

        var fullEntryName = E($"{questName}_{entryName}"); // CLASSIC: E
        Registry[fullEntryName] = value;

        return true;
    }

    public bool HasRegistryValue(string key) {
        if (string.IsNullOrWhiteSpace(key)) {
            return false;
        }

        return Registry.ContainsKey(E(key)); // CLASSIC: E
    }

    public bool HasQuestRegistryValue(string questName, string entryName) {
        if (string.IsNullOrWhiteSpace(questName) || string.IsNullOrWhiteSpace(entryName)) {
            return false;
        }

        var fullEntryName = $"{questName}_{entryName}";

        return HasRegistryValue(fullEntryName);
    }

    public ulong GetRegistryValue(string key) {
        if (string.IsNullOrWhiteSpace(key)) {
            return 0;
        }

        return Registry.TryGetValue(E(key), out var value) ? value : 0; // CLASSIC: E
    }

    public ulong GetQuestRegistryValue(string questName, string entryName) {
        if (string.IsNullOrWhiteSpace(questName) || string.IsNullOrWhiteSpace(entryName)) {
            return 0;
        }

        var fullEntryName = $"{questName}_{entryName}";
        
        return GetRegistryValue(fullEntryName);
    }

    public ServerQuestBehavior GetClientBehaviorInstance()
        => throw new NotImplementedException();

}