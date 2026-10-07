// CLASSIC: dungeon successor creation is a separate, idempotent acknowledged journal transaction.
using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;

namespace Imlight.CoreLib.Classic;

internal sealed record DungeonQuestGrant(QuestTemplate Template, QuestInstance Quest,
    ResultList TransientStartResults, IReadOnlyList<IMessage> Messages);

internal static partial class ClassicQuestClaims {
    internal static bool TryReconcileDungeonQuests(Wizard live, IReadOnlyList<QuestTemplate> templates,
        IActorRef playerRef, CoreObject playerObj, IActorRef zoneRef, out IReadOnlyList<DungeonQuestGrant> grants,
        Func<Wizard, QuestTemplate, QuestInstance, IReadOnlyList<IMessage>> prepare = null,
        Action<IReadOnlyList<DungeonQuestGrant>> afterCommit = null) {
        grants = [];
        if (!ClassicQuestEngine.IsActive || live?.CharId is not > 0 || live.QuestBehavior is null
            || WizardCollection.IsInventorySnapshotUncertain(live) || templates is null
            || templates.Any(template => template is null || string.IsNullOrWhiteSpace(template.m_questName))
            || templates.Select(template => Canonical(template.m_questName)).Distinct(StringComparer.Ordinal).Count() != templates.Count)
            return false;
        if (templates.Count == 0) return false;
        var staged = new List<DungeonQuestGrant>();
        Dictionary<string, ulong> before = null;
        var dependencies = TestScope.Value ?? new();
        var committed = WizardCollection.CommitCharacterMutation(live.CharId, (session, saved) => {
            if (WizardCollection.IsInventorySnapshotUncertain(live) || saved.CharId != live.CharId
                || saved.QuestBehavior is null || !string.Equals(saved.Zone, live.Zone, StringComparison.OrdinalIgnoreCase)
                || !TryReadJournal(session, saved, out var current)) return false;
            var owned = ReadOwnedQuestRows(session, saved.CharId);
            saved.QuestBehavior.CurrentQuestInstances = current;
            before = saved.QuestBehavior.Registry.ToDictionary(entry => entry.Key, entry => entry.Value);
            var createdIds = new HashSet<ulong>();
            try {
                foreach (var template in templates) {
                    var name = Canonical(template.m_questName);
                    if (saved.QuestBehavior.HasQuest(name) || saved.QuestBehavior.HasCompletedQuest(name)) continue;
                    // An unreferenced owned row is not authority to adopt or create another instance.
                    if (owned.Any(quest => Canonical(quest.QuestName) == name)) return false;
                    if (template.m_requirements is not null && !RequirementDispatcher.EvaluateRequirements(
                        template.m_requirements, new GenericRequirementContext(template.m_requirements,
                            playerRef, playerObj, saved, zoneRef, template.m_questName))) continue;
                    if (template.m_goals is null || template.m_startGoals is null
                        || template.m_goals.Any(goal => goal is null || string.IsNullOrWhiteSpace(goal.m_goalName))
                        || template.m_goals.Select(goal => goal.m_goalName).Distinct(StringComparer.Ordinal).Count() != template.m_goals.Count
                        || template.m_startGoals.Distinct(StringComparer.Ordinal).Count() != template.m_startGoals.Count
                        || template.m_startGoals.Any(name => template.m_goals.Count(goal => goal.m_goalName == name) != 1)) return false;
                    var starting = template.m_goals.Where(goal => template.m_startGoals.Contains(goal.m_goalName)).ToArray();
                    // All currently indexed starting goals have neither gates nor activation results.
                    // Keep unknown shapes refused rather than invoking a saving StartGoal after the ACK.
                    if (starting.Any(goal => goal.m_goalRequirements?.m_requirements?.Count > 0
                        || goal.m_activateResults?.m_results?.Count > 0)) return false;
                    var quest = new QuestInstance(template, saved.CharId);
                    if (quest.ID == 0 || !createdIds.Add(quest.ID)
                        || quest.GoalProgress.Any(goal => goal.ID == 0 || goal.OwnerCharId != saved.CharId)
                        || quest.GoalProgress.Select(goal => goal.ID).Distinct().Count() != quest.GoalProgress.Length
                        || session.Query<QuestInstance>(collectionName: QuestInstanceCollection.CollectionName)
                            .Customize(query => query.WaitForNonStaleResults()).Where(row => row.ID == quest.ID).Take(1).Any()
                        || !saved.QuestBehavior.AddQuest(quest)) return false;
                    var start = ResultDispatcher.FilterResultsForWizard(template.m_startResults, saved,
                        playerRef, playerObj, zoneRef, name);
                    if (start.m_results.Any(result => result is not (ResModifyEntry or ResPostEvent))) return false;
                    foreach (var result in start.m_results.OfType<ResModifyEntry>()) {
                        var value = (ulong)result.m_value; // retain the existing handler interpretation.
                        if (!(result.m_isQuestRegistry
                            ? saved.QuestBehavior.SetQuestRegistryValue(result.m_questName, result.m_entryName, value)
                            : saved.QuestBehavior.SetRegistryValue(result.m_entryName, value))) return false;
                    }
                    var messages = prepare?.Invoke(saved, template, quest) ?? (prepare is null ? [] : null);
                    if (messages is null || (prepare is not null && messages.Count < 1 + starting.Length)
                        || messages.Any(message => message is null
                        || !(dependencies.Prepare?.Invoke(message) ?? WizardProgressionTransactions.Prepare(message)))) return false;
                    staged.Add(new(template, quest, new ResultList {
                        m_results = start.m_results.Where(result => result is ResPostEvent).ToList(),
                    }, messages.ToArray()));
                }
            }
            catch { return false; } // known preparation failure; this session has not saved or published.
            if (staged.Count == 0) return false;
            // No queries after staging new rows. The journal and all admitted successor originals save together.
            foreach (var grant in staged) {
                session.Store(grant.Quest);
                session.Advanced.GetMetadataFor(grant.Quest)[Raven.Client.Constants.Documents.Metadata.Collection] = QuestInstanceCollection.CollectionName;
            }
            ProtectReadOnlyQuestRows(session, owned); // existing tracked originals are validation reads, not start writes.
            return true;
        }, saved => {
            dependencies.BeforePublish?.Invoke(live);
            live.QuestBehavior.CurrentQuestIDs = [.. saved.QuestBehavior.CurrentQuestIDs];
            live.QuestBehavior.CurrentQuestInstances = [.. saved.QuestBehavior.CurrentQuestInstances];
            foreach (var key in before.Keys.Concat(saved.QuestBehavior.Registry.Keys).Distinct(StringComparer.Ordinal)) {
                var had = before.TryGetValue(key, out var oldValue);
                var has = saved.QuestBehavior.Registry.TryGetValue(key, out var value);
                if (had == has && oldValue == value) continue;
                if (has) live.QuestBehavior.Registry[key] = value;
                else live.QuestBehavior.Registry.TryRemove(key, out _);
            }
            afterCommit?.Invoke(staged.ToArray()); // native starts share the original acknowledged character lane.
        }, onSaveFailure: _ => WizardCollection.MarkInventorySnapshotUncertain(live));
        if (!committed) return false;
        grants = staged.ToArray();
        return true;
    }

    // CLASSIC: reuse the terminal claim's camera/serializer path for captured successor start dialogues.
    internal static bool TryPrepareDungeonDialog(ActorDialogListBase list, string tag, ulong questId, ulong goalId,
        out IMessage message)
        => TryPrepareDialog(list, tag, "QuestStart", questId, goalId, TestScope.Value ?? new(), out message);
}
