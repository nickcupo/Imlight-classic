// CLASSIC: fresh quest originals and selected journal changes share one acknowledged character write.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Imlight.Classic.Quests;
using Imlight.Common;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Imlight.CoreLib.WizardData.Collections;

internal enum QuestMutationStatus { Refused, Unchanged, Committed }
internal enum QuestMutationKind { Add, Remove, Complete, StartGoal, IncrementGoal, CompleteGoal, SetRegistry, AddRegistry, Reconcile }
internal sealed record QuestMutationReceipt(QuestMutationKind Kind, QuestInstance Quest, GoalInstance Goal,
    ServerQuestBehavior Journal, Wizard Saved = null);
internal sealed class QuestMutationDependencies {
    internal Action<Wizard> BeforePublish;
}

internal static class WizardQuestTransactions {
    internal static readonly AsyncLocal<QuestMutationDependencies> TestScope = new();

    internal static string Canonical(string name) => QuestNameAliases.Current.Canonical(name ?? string.Empty);

    internal static QuestInstance Held(Wizard live, string name) {
        if (live?.QuestBehavior?.CurrentQuestInstances is not { } quests || string.IsNullOrWhiteSpace(name)) return null;
        var matches = quests.Where(quest => quest is not null && Canonical(quest.QuestName) == Canonical(name)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static GoalInstance HeldGoal(QuestInstance quest, string name) {
        if (quest?.GoalProgress is not { } goals || string.IsNullOrWhiteSpace(name)) return null;
        var matches = goals.Where(goal => goal?.GoalName == name).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static QuestMutationStatus TryAdd(Wizard live, QuestInstance candidate, out QuestMutationReceipt receipt,
        Func<QuestMutationReceipt, bool> preparePublication = null, Action<QuestMutationReceipt> afterCommit = null) {
        if (!Usable(live) || !ValidQuest(candidate, live.CharId)) { receipt = null; return QuestMutationStatus.Refused; }
        var detached = CopyQuest(candidate);
        return Commit(live, QuestMutationKind.Add, (session, saved, rows, current) => {
            if (rows.Any(quest => quest?.ID == detached.ID)
                || rows.SelectMany(quest => quest?.GoalProgress ?? []).Any(goal => goal is not null
                    && detached.GoalProgress.Any(candidateGoal => candidateGoal.ID == goal.ID))
                || rows.Any(quest => quest?.OwnerCharId == live.CharId
                    && Canonical(quest.QuestName) == Canonical(detached.QuestName))
                || saved.QuestBehavior.HasCompletedQuest(detached.QuestName)) return null;
            saved.QuestBehavior.CurrentQuestIDs = [.. saved.QuestBehavior.CurrentQuestIDs, detached.ID];
            saved.QuestBehavior.CurrentQuestInstances = [.. current, detached];
            var result = PrepareReceipt(QuestMutationKind.Add, saved, detached);
            if (!Prepare(result, preparePublication)) return null;
            session.Store(detached);
            session.Advanced.GetMetadataFor(detached)[Raven.Client.Constants.Documents.Metadata.Collection] = QuestInstanceCollection.CollectionName;
            return new(result, true, true, []);
        }, out receipt, afterCommit, candidate);
    }

    internal static QuestMutationStatus TryRemove(Wizard live, QuestInstance expectedQuest, out QuestMutationReceipt receipt,
        Func<QuestMutationReceipt, bool> preparePublication = null, Action<QuestMutationReceipt> afterCommit = null)
        => Retire(live, expectedQuest, false, out receipt, preparePublication, afterCommit);

    internal static QuestMutationStatus TryComplete(Wizard live, QuestInstance expectedQuest, out QuestMutationReceipt receipt,
        Func<QuestMutationReceipt, bool> preparePublication = null, Action<QuestMutationReceipt> afterCommit = null)
        => Retire(live, expectedQuest, true, out receipt, preparePublication, afterCommit);

    private static QuestMutationStatus Retire(Wizard live, QuestInstance expected, bool complete, out QuestMutationReceipt receipt,
        Func<QuestMutationReceipt, bool> prepare, Action<QuestMutationReceipt> publish) {
        var kind = complete ? QuestMutationKind.Complete : QuestMutationKind.Remove;
        if (!Expected(live, expected)) { receipt = null; return QuestMutationStatus.Refused; }
        return Commit(live, kind, (session, saved, rows, current) => {
            var original = MatchExpected(current, expected);
            if (original is null || !(complete ? saved.QuestBehavior.CompleteQuest(original) : saved.QuestBehavior.RemoveQuest(original))) return null;
            var result = PrepareReceipt(kind, saved, original);
            if (!Prepare(result, prepare)) return null;
            session.Delete(original);
            return new(result, true, true, [original]);
        }, out receipt, publish);
    }

    internal static QuestMutationStatus TryStartGoal(Wizard live, QuestInstance expectedQuest, GoalInstance expectedGoal,
        out QuestMutationReceipt receipt, Func<QuestMutationReceipt, bool> preparePublication = null,
        Action<QuestMutationReceipt> afterCommit = null, Func<Wizard, bool> validateFresh = null)
        => ChangeGoal(live, expectedQuest, expectedGoal, QuestMutationKind.StartGoal, out receipt,
            preparePublication, afterCommit, validateFresh);

    internal static QuestMutationStatus TryIncrementGoal(Wizard live, QuestInstance expectedQuest, GoalInstance expectedGoal,
        out QuestMutationReceipt receipt, Func<QuestMutationReceipt, bool> preparePublication = null,
        Action<QuestMutationReceipt> afterCommit = null)
        => ChangeGoal(live, expectedQuest, expectedGoal, QuestMutationKind.IncrementGoal, out receipt, preparePublication, afterCommit);

    internal static QuestMutationStatus TryCompleteGoal(Wizard live, QuestInstance expectedQuest, GoalInstance expectedGoal,
        out QuestMutationReceipt receipt, Func<QuestMutationReceipt, bool> preparePublication = null,
        Action<QuestMutationReceipt> afterCommit = null)
        => ChangeGoal(live, expectedQuest, expectedGoal, QuestMutationKind.CompleteGoal, out receipt, preparePublication, afterCommit);

    private static QuestMutationStatus ChangeGoal(Wizard live, QuestInstance expectedQuest, GoalInstance expectedGoal,
        QuestMutationKind kind, out QuestMutationReceipt receipt, Func<QuestMutationReceipt, bool> prepare,
        Action<QuestMutationReceipt> publish, Func<Wizard, bool> validateFresh = null) {
        if (!Expected(live, expectedQuest) || !ExpectedGoal(expectedQuest, expectedGoal)) {
            receipt = null; return QuestMutationStatus.Refused;
        }
        return Commit(live, kind, (session, saved, rows, current) => {
            var original = MatchExpected(current, expectedQuest);
            var goal = original?.GoalProgress.SingleOrDefault(candidate => candidate.ID == expectedGoal.ID
                && candidate.GoalName == expectedGoal.GoalName && candidate.OwnerCharId == expectedGoal.OwnerCharId
                && candidate.GoalType == expectedGoal.GoalType);
            if (goal is null || validateFresh?.Invoke(saved) == false) return null;
            var previous = goal.CurrentProgress;
            if (kind == QuestMutationKind.StartGoal) goal.BeginGoal();
            else if (kind == QuestMutationKind.IncrementGoal) goal.IncrementGoal();
            else goal.CompleteGoal(); // Preserve dedicated tutorial completion of previously unbegun control goals.
            var result = PrepareReceipt(kind, saved, original, goal);
            if (goal.CurrentProgress == previous) return new(result, false, false, []);
            if (!Prepare(result, prepare)) return null;
            return new(result, true, false, [original]);
        }, out receipt, publish);
    }

    internal static QuestMutationStatus TrySetRegistry(Wizard live, string key, ulong value, out QuestMutationReceipt receipt,
        Func<QuestMutationReceipt, bool> preparePublication = null, Action<QuestMutationReceipt> afterCommit = null)
        => ChangeRegistry(live, QuestMutationKind.SetRegistry, journal => journal.SetRegistryValue(key, value),
            out receipt, preparePublication, afterCommit);

    internal static QuestMutationStatus TrySetQuestRegistry(Wizard live, string questName, string key, ulong value,
        out QuestMutationReceipt receipt, Func<QuestMutationReceipt, bool> preparePublication = null,
        Action<QuestMutationReceipt> afterCommit = null)
        => ChangeRegistry(live, QuestMutationKind.SetRegistry, journal => journal.SetQuestRegistryValue(questName, key, value),
            out receipt, preparePublication, afterCommit);

    internal static QuestMutationStatus TryAddRegistry(Wizard live, string key, ulong value, out QuestMutationReceipt receipt,
        Func<QuestMutationReceipt, bool> preparePublication = null, Action<QuestMutationReceipt> afterCommit = null)
        => ChangeRegistry(live, QuestMutationKind.AddRegistry, journal => journal.AddToRegistry(key, value),
            out receipt, preparePublication, afterCommit);

    internal static QuestMutationStatus TryChangeRegistry(Wizard live, Func<ServerQuestBehavior, bool> stage,
        out QuestMutationReceipt receipt, Func<QuestMutationReceipt, bool> preparePublication = null,
        Action<QuestMutationReceipt> afterCommit = null)
        => ChangeRegistry(live, QuestMutationKind.SetRegistry, stage, out receipt, preparePublication, afterCommit);

    private static QuestMutationStatus ChangeRegistry(Wizard live, QuestMutationKind kind, Func<ServerQuestBehavior, bool> stage,
        out QuestMutationReceipt receipt, Func<QuestMutationReceipt, bool> prepare, Action<QuestMutationReceipt> publish)
        => Commit(live, kind, (session, saved, rows, current) => {
            if (stage is null) return null;
            var before = saved.QuestBehavior.Registry.ToDictionary(entry => entry.Key, entry => entry.Value);
            var proposed = PrepareReceipt(kind, saved).Journal;
            if (!stage(proposed) || !saved.QuestBehavior.CurrentQuestIDs.SequenceEqual(proposed.CurrentQuestIDs ?? [])
                || !SameQuestStates(saved.QuestBehavior.CurrentQuestInstances, proposed.CurrentQuestInstances)) return null;
            var changed = before.Count != proposed.Registry.Count
                || before.Any(entry => !proposed.Registry.TryGetValue(entry.Key, out var value) || value != entry.Value);
            foreach (var key in before.Keys.Where(key => !proposed.Registry.ContainsKey(key))) saved.QuestBehavior.Registry.TryRemove(key, out _);
            foreach (var entry in proposed.Registry) saved.QuestBehavior.Registry[entry.Key] = entry.Value;
            var result = PrepareReceipt(kind, saved);
            if (!changed) return new(result, false, false, []);
            if (!Prepare(result, prepare)) return null;
            return new(result, true, true, []);
        }, out receipt, publish);

    private sealed record Staged(QuestMutationReceipt Receipt, bool Changed, bool WizardChanged,
        IReadOnlyList<QuestInstance> IntendedOriginals);

    private static QuestMutationStatus Commit(Wizard live, QuestMutationKind kind,
        Func<IDocumentSession, Wizard, List<QuestInstance>, List<QuestInstance>, Staged> stage,
        out QuestMutationReceipt receipt, Action<QuestMutationReceipt> publish, QuestInstance addedAlias = null) {
        receipt = null;
        if (!Usable(live)) return QuestMutationStatus.Refused;
        Staged staged = null;
        Dictionary<string, ulong> registryBefore = null;
        try {
            var committed = WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
                if (!Usable(live) || saved.CharId != live.CharId || saved.QuestBehavior is null
                    || !ReadJournal(session, saved, false, out var rows, out var current)) return false;
                saved.QuestBehavior.CurrentQuestInstances = current;
                registryBefore = saved.QuestBehavior.Registry.ToDictionary(entry => entry.Key, entry => entry.Value);
                try { staged = stage(session, saved, rows, current); }
                catch { return false; } // Known dependency/preparation refusal; no SaveChanges has occurred.
                if (staged is null || !staged.Changed) return false;
                if (!CanPublish(live, staged.Receipt.Journal) || addedAlias is not null
                    && !SameQuestStates([addedAlias], [staged.Receipt.Quest])) { staged = null; return false; }
                ClassicQuestClaims.ProtectReadOnlyQuestRows(session, rows, [.. staged.IntendedOriginals]);
                if (!staged.WizardChanged) session.Advanced.IgnoreChangesFor(saved);
                return true;
            }, saved => {
                TestScope.Value?.BeforePublish?.Invoke(live);
                Publish(live, staged.Receipt.Journal, registryBefore, addedAlias);
                publish?.Invoke(staged.Receipt);
            }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
            if (!committed && staged?.Changed == true) return QuestMutationStatus.Refused;
            if (staged is null) return QuestMutationStatus.Refused;
            receipt = staged.Receipt;
            return committed ? QuestMutationStatus.Committed : QuestMutationStatus.Unchanged;
        }
        catch (Exception error) {
            Logger.Error("Classic quest {0} for {1} needs reload after a failed operation: {2}",
                Logger.Args(kind, live.CharId, error.Message));
            return QuestMutationStatus.Refused;
        }
    }

    internal static QuestMutationStatus ReconcileLoadedJournal(Wizard live, out QuestMutationReceipt receipt,
        Func<QuestMutationReceipt, bool> preparePublication = null, Action<QuestMutationReceipt> afterCommit = null) {
        receipt = null;
        if (!ReloadUsable(live)) return QuestMutationStatus.Refused;
        QuestMutationReceipt staged = null;
        Dictionary<string, ulong> before = null;
        try {
            var committed = WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
                if (!ReloadUsable(live) || saved.CharId != live.CharId) return false;
                var initialized = saved.QuestBehavior is null;
                saved.QuestBehavior ??= new();
                if (saved.QuestBehavior.CurrentQuestIDs is not { } ids
                    || !ReadJournal(session, saved, true, out var rows, out var current)) return false;
                var kept = new List<QuestInstance>();
                var removed = new HashSet<QuestInstance>(ReferenceEqualityComparer.Instance);
                var seenNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var quest in current) {
                    if (saved.QuestBehavior.HasCompletedQuest(quest.QuestName) || !seenNames.Add(Canonical(quest.QuestName))) removed.Add(quest);
                    else kept.Add(quest);
                }
                var keptIds = kept.Select(quest => quest.ID).ToList();
                var changed = initialized || !ids.SequenceEqual(keptIds) || removed.Count > 0;
                before = saved.QuestBehavior.Registry.ToDictionary(entry => entry.Key, entry => entry.Value);
                saved.QuestBehavior.CurrentQuestIDs = keptIds;
                saved.QuestBehavior.CurrentQuestInstances = kept;
                staged = PrepareReceipt(QuestMutationKind.Reconcile, saved);
                if (!CanPublish(live, staged.Journal)) { staged = null; return false; }
                if (!changed) {
                    // A clean attach only hydrates the fresh durable reference subset. No save or success effects.
                    try { Publish(live, staged.Journal, before); }
                    catch { WizardCollection.MarkInventorySnapshotUncertain(live); throw; }
                    return false;
                }
                if (!Prepare(staged, preparePublication)) { staged = null; return false; }
                foreach (var quest in removed) session.Delete(quest);
                ClassicQuestClaims.ProtectReadOnlyQuestRows(session, rows, [.. removed]);
                return true;
            }, _ => {
                TestScope.Value?.BeforePublish?.Invoke(live);
                Publish(live, staged.Journal, before);
                afterCommit?.Invoke(staged);
            }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
            if (staged is null) return QuestMutationStatus.Refused;
            receipt = staged;
            return committed ? QuestMutationStatus.Committed : QuestMutationStatus.Unchanged;
        }
        catch (Exception error) {
            Logger.Error("Classic quest journal reconciliation for {0} refused: {1}", Logger.Args(live.CharId, error.Message));
            return QuestMutationStatus.Refused;
        }
    }

    private static bool Usable(Wizard live) => ClassicQuestEngine.IsActive && live?.CharId is > 0
        && live.QuestBehavior is not null && !WizardCollection.IsInventorySnapshotUncertain(live);

    private static bool ReloadUsable(Wizard live) => ClassicQuestEngine.IsActive && live?.CharId is > 0
        && !WizardCollection.IsInventorySnapshotUncertain(live);

    private static bool Expected(Wizard live, QuestInstance expected) => Usable(live) && ValidQuest(expected, live.CharId)
        && live.QuestBehavior.CurrentQuestIDs?.Contains(expected.ID) == true
        && live.QuestBehavior.CurrentQuestInstances?.Count(quest => ReferenceEquals(quest, expected)) == 1;

    private static bool ExpectedGoal(QuestInstance quest, GoalInstance expected) => expected is not null
        && quest.GoalProgress.Count(goal => ReferenceEquals(goal, expected)) == 1 && expected.OwnerCharId == quest.OwnerCharId;

    private static QuestInstance MatchExpected(IEnumerable<QuestInstance> current, QuestInstance expected)
        => current.SingleOrDefault(quest => quest.ID == expected.ID && quest.OwnerCharId == expected.OwnerCharId
            && Canonical(quest.QuestName) == Canonical(expected.QuestName));

    private static bool ValidQuest(QuestInstance quest, ulong owner) => quest is not null && quest.ID != 0
        && quest.OwnerCharId == owner && !string.IsNullOrWhiteSpace(quest.QuestName) && quest.GoalProgress is not null
        && quest.GoalProgress.All(goal => goal is not null && goal.ID != 0 && goal.OwnerCharId == owner
            && !string.IsNullOrWhiteSpace(goal.GoalName) && goal.CurrentProgress >= -1)
        && quest.GoalProgress.Select(goal => goal.ID).Distinct().Count() == quest.GoalProgress.Length
        && quest.GoalProgress.Select(goal => goal.GoalName).Distinct(StringComparer.Ordinal).Count() == quest.GoalProgress.Length;

    private static bool ReadJournal(IDocumentSession session, Wizard saved, bool reconcile,
        out List<QuestInstance> rows, out List<QuestInstance> current) {
        rows = []; current = [];
        var ids = saved.QuestBehavior?.CurrentQuestIDs;
        if (ids is null || ids.Any(id => id == 0) || !reconcile && ids.Distinct().Count() != ids.Count) return false;
        rows = session.Query<QuestInstance>(collectionName: QuestInstanceCollection.CollectionName)
            .Customize(query => query.WaitForNonStaleResults()).Take(int.MaxValue).ToList();
        foreach (var id in ids.Distinct()) {
            var matches = rows.Where(quest => quest?.ID == id).ToArray();
            if (matches.Length == 0 && reconcile) continue;
            if (matches.Length != 1 || !ValidQuest(matches[0], saved.CharId)) return false;
            current.Add(matches[0]);
        }
        var goals = current.SelectMany(quest => quest.GoalProgress).Select(goal => goal.ID).ToHashSet();
        var capturedRows = rows;
        if (goals.Any(id => capturedRows.SelectMany(quest => quest?.GoalProgress ?? []).Count(goal => goal?.ID == id) != 1)) return false;
        return reconcile || current.Select(quest => Canonical(quest.QuestName)).Distinct(StringComparer.Ordinal).Count() == current.Count;
    }

    private static QuestMutationReceipt PrepareReceipt(QuestMutationKind kind, Wizard saved,
        QuestInstance quest = null, GoalInstance goal = null) {
        var journal = new ServerQuestBehavior { CurrentQuestIDs = [.. saved.QuestBehavior.CurrentQuestIDs],
            CurrentQuestInstances = saved.QuestBehavior.CurrentQuestInstances.Select(CopyQuest).ToList() };
        foreach (var entry in saved.QuestBehavior.Registry) journal.Registry[entry.Key] = entry.Value;
        var detachedQuest = quest is null ? null : CopyQuest(quest);
        var detachedGoal = goal is null ? null : detachedQuest?.GoalProgress.Single(candidate => candidate.ID == goal.ID);
        return new(kind, detachedQuest, detachedGoal, journal, saved);
    }

    private static QuestInstance CopyQuest(QuestInstance quest) => new() { ID = quest.ID, OwnerCharId = quest.OwnerCharId,
        QuestName = Canonical(quest.QuestName), GoalProgress = quest.GoalProgress.Select(CopyGoal).ToArray() };

    private static GoalInstance CopyGoal(GoalInstance goal) {
        var copy = new GoalInstance { ID = goal.ID, OwnerCharId = goal.OwnerCharId, GoalName = goal.GoalName, GoalType = goal.GoalType };
        copy.ApplyCommittedProgress(goal);
        return copy;
    }

    private static bool Prepare(QuestMutationReceipt receipt, Func<QuestMutationReceipt, bool> prepare) {
        try { return prepare?.Invoke(receipt) ?? true; }
        catch { return false; }
    }

    private static bool CanPublish(Wizard live, ServerQuestBehavior snapshot) {
        var previous = live.QuestBehavior?.CurrentQuestInstances ?? [];
        foreach (var quest in snapshot.CurrentQuestInstances) {
            var aliases = previous.Where(alias => alias?.ID == quest.ID).ToArray();
            if (aliases.Length > 1 || aliases.Length == 1 && (aliases[0].OwnerCharId != live.CharId
                || Canonical(aliases[0].QuestName) != Canonical(quest.QuestName))) return false;
            if (aliases.Length == 0) continue;
            foreach (var goal in quest.GoalProgress) {
                var held = aliases[0].GoalProgress?.Where(alias => alias?.ID == goal.ID).ToArray() ?? [];
                if (held.Length > 1 || held.Length == 1 && (held[0].OwnerCharId != live.CharId
                    || held[0].GoalName != goal.GoalName || held[0].GoalType != goal.GoalType)) return false;
            }
        }
        return true;
    }

    private static void Publish(Wizard live, ServerQuestBehavior snapshot, IReadOnlyDictionary<string, ulong> before,
        QuestInstance addedAlias = null) {
        if (!CanPublish(live, snapshot)) throw new InvalidOperationException("The quest journal needs an authoritative reload.");
        live.QuestBehavior ??= new();
        var previous = live.QuestBehavior.CurrentQuestInstances ?? [];
        var published = new List<QuestInstance>();
        foreach (var fresh in snapshot.CurrentQuestInstances) {
            var alias = previous.SingleOrDefault(quest => quest?.ID == fresh.ID)
                ?? (addedAlias?.ID == fresh.ID ? addedAlias : fresh);
            var oldGoals = alias.GoalProgress ?? [];
            alias.GoalProgress = fresh.GoalProgress.Select(goal => {
                var held = oldGoals.SingleOrDefault(candidate => candidate?.ID == goal.ID) ?? goal;
                if (!ReferenceEquals(held, goal)) held.ApplyCommittedProgress(goal);
                return held;
            }).ToArray();
            alias.QuestName = fresh.QuestName;
            published.Add(alias);
        }
        live.QuestBehavior.CurrentQuestIDs = [.. snapshot.CurrentQuestIDs];
        live.QuestBehavior.CurrentQuestInstances = published;
        foreach (var key in before.Keys.Concat(snapshot.Registry.Keys).Distinct(StringComparer.Ordinal)) {
            var had = before.TryGetValue(key, out var old);
            var has = snapshot.Registry.TryGetValue(key, out var fresh);
            if (had == has && old == fresh) continue;
            if (has) live.QuestBehavior.Registry[key] = fresh;
            else live.QuestBehavior.Registry.TryRemove(key, out _);
        }
    }

    private static bool SameQuestStates(IReadOnlyList<QuestInstance> first, IReadOnlyList<QuestInstance> second) {
        if (first is null || second is null || first.Count != second.Count) return false;
        for (var index = 0; index < first.Count; index++) {
            var left = first[index]; var right = second[index];
            if (left is null || right is null || left.ID != right.ID || left.OwnerCharId != right.OwnerCharId
                || Canonical(left.QuestName) != Canonical(right.QuestName) || left.GoalProgress is null || right.GoalProgress is null
                || left.GoalProgress.Length != right.GoalProgress.Length) return false;
            for (var goal = 0; goal < left.GoalProgress.Length; goal++) {
                var a = left.GoalProgress[goal]; var b = right.GoalProgress[goal];
                if (a is null || b is null || a.ID != b.ID || a.OwnerCharId != b.OwnerCharId || a.GoalName != b.GoalName
                    || a.GoalType != b.GoalType || a.CurrentProgress != b.CurrentProgress) return false;
            }
        }
        return true;
    }
}
