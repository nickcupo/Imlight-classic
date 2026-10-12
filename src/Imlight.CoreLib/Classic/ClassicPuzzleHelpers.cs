// CLASSIC: apply reviewed October-only helpers before zone/quest data is published.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Classic;
using Imlight.Classic.Rules;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Classic;

/// <summary>CLASSIC: a configured helper cannot be applied atomically to the actual data.</summary>
internal sealed class PuzzleHelperApplicationException(string message) : Exception(message) { }

internal static class ClassicPuzzleHelpers {
    private static string? ActiveProfile => ClassicRuntime.IsInitialized ? ClassicRuntime.Rules.Profile.Id : null;

    internal static WizZoneTriggers? FilterTriggers(string zonePath, WizZoneTriggers? data)
        => FilterTriggers(ActiveProfile, ClassicProgression.PuzzleHelpers, zonePath, data);

    /// <summary>Return a copy before all supervisors share the message. Cached archive data remains unchanged.</summary>
    internal static WizZoneTriggers? FilterTriggers(string? profileId, PuzzleHelpers policy, string zonePath, WizZoneTriggers? data) {
        if (!policy.AppliesTo(profileId)) return data;
        var row = policy.Zones.FirstOrDefault(z => z.Zone == zonePath);
        if (row is null) return data;
        if (data?.m_triggers is null) throw Error(policy, "missing triggers for " + zonePath);
        var targets = row.RemoveTriggers.ToHashSet(StringComparer.Ordinal);
        var found = data.m_triggers.Where(t => t is not null && targets.Contains((string)t.m_triggerName))
            .GroupBy(t => (string)t!.m_triggerName, StringComparer.Ordinal).ToArray();
        if (found.Any(group => group.Count() != 1)) throw Error(policy, "duplicate target trigger in " + zonePath);
        if (found.Length == 0) return data; // Already transformed data is valid and idempotent.
        if (found.Length != targets.Count) throw Error(policy, "partial target trigger set in " + zonePath);
        return data with { m_triggers = [.. data.m_triggers.Where(t => t is null || !targets.Contains((string)t.m_triggerName))] };
    }

    internal static void ApplyQuestHelpers(IReadOnlyDictionary<string, QuestTemplate> quests)
        => ApplyQuestHelpers(ActiveProfile, ClassicProgression.PuzzleHelpers, quests);

    /// <summary>Validate every configured target after all overlays, then change only those goals in the temporary store.</summary>
    internal static void ApplyQuestHelpers(string? profileId, PuzzleHelpers policy, IReadOnlyDictionary<string, QuestTemplate> quests) {
        if (!policy.AppliesTo(profileId)) return;
        List<System.Action> pending = [];
        foreach (var row in policy.QuestHelpers) {
            if (!quests.TryGetValue(row.Quest, out var quest) || quest is null || (string)quest.m_questName != row.Quest) {
                throw Error(policy, "missing or mismatched quest " + row.Quest);
            }
            if (quest.m_goals is null) throw Error(policy, "missing goals for " + row.Quest);
            var groups = quest.m_goals.Where(g => g is not null).GroupBy(g => (string)g.m_goalName, StringComparer.Ordinal).ToArray();
            if (groups.Any(g => g.Count() != 1)) throw Error(policy, "duplicate goal identity in " + row.Quest);
            foreach (var name in row.Goals) {
                var matches = groups.FirstOrDefault(g => g.Key == name);
                if (matches is null) throw Error(policy, "missing goal " + row.Quest + "/" + name);
                var goal = matches.Single();
                if (goal.m_goalType != GOAL_TYPE.GOAL_TYPE_USAGE) {
                    throw Error(policy, "helper target is not a usage goal: " + row.Quest + "/" + name);
                }
                pending.Add(() => goal.m_noQuestHelper = row.NoQuestHelper);
            }
        }
        foreach (var apply in pending) apply();
    }

    private static PuzzleHelperApplicationException Error(PuzzleHelpers policy, string message)
        => new(policy.SourceFile + ": " + message);
}
